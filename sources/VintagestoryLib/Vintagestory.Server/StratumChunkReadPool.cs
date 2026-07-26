using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Threading;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;

namespace Vintagestory.Server;

/// <summary>
/// Stratum parallel chunk-read pool. Holds N additional read-only <see cref="SqliteConnection"/>s
/// against the savegame file so the chunk-load thread can fan chunk-column reads out to multiple
/// connections in parallel. SQLite's WAL mode allows many concurrent readers + one writer with
/// snapshot isolation, so reads here never block (and aren't blocked by) the writer connection
/// owned by <c>SQLiteDbConnectionv2</c>.
///
/// Each pool slot has its own connection plus its own prepared
/// <c>SELECT data FROM chunk WHERE position=@position</c> command; slots are leased to callers
/// via a <see cref="SemaphoreSlim"/> + lock-free free-slot bag so concurrent callers never share
/// a connection or its command.
/// </summary>
internal sealed class StratumChunkReadPool : IDisposable
{
	private readonly SqliteConnection[] connections;
	private readonly SqliteCommand[] getChunkCmds;
	private readonly SemaphoreSlim available;
	private readonly ConcurrentBag<int> freeSlots;
	private readonly object disposeLock = new object();
	private bool disposed;

	public int WorkerCount => connections.Length;
	public bool IsOpen => !disposed && connections.Length > 0;

	public StratumChunkReadPool(string filename, int workers, bool corruptionProtection)
	{
		workers = Math.Max(1, Math.Min(16, workers));
		connections = new SqliteConnection[workers];
		getChunkCmds = new SqliteCommand[workers];
		freeSlots = new ConcurrentBag<int>();
		available = new SemaphoreSlim(workers, workers);

		for (int i = 0; i < workers; i++)
		{
			DbConnectionStringBuilder conf = new DbConnectionStringBuilder
			{
				{ "Data Source", filename },
				{ "Pooling", "false" },
				{ "Mode", "ReadOnly" },
			};
			SqliteConnection conn = new SqliteConnection(conf.ToString());
			conn.Open();
			// Match the writer's pragmas so our snapshot is consistent with whatever
			// journal_mode the writer chose. WAL is the expected path; for journal_mode=Off
			// (corruption protection disabled) reads still work but offer no isolation
			// guarantees during a concurrent write.
			using (SqliteCommand pragma = conn.CreateCommand())
			{
				pragma.CommandTimeout = 1;
				pragma.CommandText = corruptionProtection
					? "PRAGMA journal_mode=WAL;PRAGMA synchronous=Normal;PRAGMA query_only=ON;"
					: "PRAGMA query_only=ON;";
				pragma.ExecuteNonQuery();
			}

			SqliteCommand cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT data FROM chunk WHERE position=@position";
			SqliteParameter p = (SqliteParameter)cmd.CreateParameter();
			p.ParameterName = "position";
			p.DbType = DbType.UInt64;
			p.Value = 0UL;
			cmd.Parameters.Add(p);
			cmd.Prepare();

			connections[i] = conn;
			getChunkCmds[i] = cmd;
			freeSlots.Add(i);
		}
	}

	// Returns null only when the row does not exist. Every failure path throws:
	// callers treat null as "chunk absent from the database" and regenerate the
	// column, so a swallowed read error would silently overwrite saved terrain
	// with fresh worldgen. The sequential GameDatabase path propagates read
	// errors the same way.
	public byte[] GetChunkBytes(ulong position)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		if (!available.Wait(15000))
		{
			throw new TimeoutException("[Stratum] chunk read pool: no free connection within 15s");
		}
		int slot = -1;
		try
		{
			if (!freeSlots.TryTake(out slot))
			{
				// Should never happen — semaphore should mirror bag count exactly.
				throw new InvalidOperationException("[Stratum] chunk read pool: semaphore and free-slot bag disagree");
			}
			ObjectDisposedException.ThrowIf(disposed, this);

			SqliteCommand cmd = getChunkCmds[slot];
			cmd.Parameters["position"].Value = position;
			byte[] result = null;
			using (DbDataReader reader = cmd.ExecuteReader())
			{
				if (reader.Read())
				{
					result = reader["data"] as byte[];
				}
			}
			return result;
		}
		catch (SqliteException ex)
		{
			ServerMain.Logger?.Error("[Stratum] chunk read pool slot {0} read failed: {1}", slot, ex.Message);
			throw;
		}
		finally
		{
			if (slot >= 0) freeSlots.Add(slot);
			available.Release();
		}
	}

	public void Dispose()
	{
		lock (disposeLock)
		{
			if (disposed) return;
			disposed = true;
		}
		// Drain outstanding leases so we don't dispose a connection mid-use.
		for (int i = 0; i < connections.Length; i++)
		{
			available.Wait();
		}
		for (int i = 0; i < connections.Length; i++)
		{
			try { getChunkCmds[i]?.Dispose(); } catch { }
			try { connections[i]?.Close(); } catch { }
			try { connections[i]?.Dispose(); } catch { }
			getChunkCmds[i] = null;
			connections[i] = null;
		}
		available.Dispose();
	}
}
