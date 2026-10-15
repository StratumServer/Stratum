using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// GetPlayerData runs on the join thread while the chunk thread holds a SQLite
/// transaction. Both have to take transactionLock. Without it, disposing the
/// join command calls SqliteConnection.RemoveCommand and the chunk thread dies.
/// </summary>
public class SqliteJoinRaceScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 120_000)]
	public void GetPlayerData_Should_NotThrow_While_ChunkWritesHoldTheLock()
	{
		object server = World.Api.World;
		object chunkThread = server.GetType().GetField("chunkThread", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
		object gameDatabase = chunkThread.GetType().GetField("gameDatabase", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chunkThread)!;
		object connection = gameDatabase.GetType().GetField("conn", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gameDatabase)!;

		MethodInfo getPlayerData = connection.GetType().GetMethod("GetPlayerData", new[] { typeof(string) })!;
		MethodInfo setChunks = connection.GetType().GetMethod("SetChunks")!;
		MethodInfo getChunk = connection.GetType().GetMethod("GetChunk", new[] { typeof(ulong) })!;
		object batch = CreateChunkBatch();

		ConcurrentQueue<Exception> errors = new ConcurrentQueue<Exception>();
		int stop = 0;
		Task[] readers = new Task[2];
		for (int i = 0; i < readers.Length; i++)
		{
			readers[i] = Task.Run(() =>
			{
				while (Volatile.Read(ref stop) == 0)
				{
					try
					{
						getPlayerData.Invoke(connection, new object[] { "stratum-race" });
					}
					catch (TargetInvocationException ex)
					{
						errors.Enqueue(ex.InnerException ?? ex);
						return;
					}
				}
			});
		}

		Task writer = Task.Run(() =>
		{
			Stopwatch watch = Stopwatch.StartNew();
			while (watch.ElapsedMilliseconds < 2000 && errors.IsEmpty)
			{
				try
				{
					setChunks.Invoke(connection, new[] { batch });
					getChunk.Invoke(connection, new object[] { ChunkIndex(400000, 0, 0) });
				}
				catch (TargetInvocationException ex)
				{
					errors.Enqueue(ex.InnerException ?? ex);
					return;
				}
			}
		});

		Assert.True(writer.Wait(TimeSpan.FromSeconds(30)), "chunk writes did not finish");
		Volatile.Write(ref stop, 1);
		Assert.True(Task.WaitAll(readers, TimeSpan.FromSeconds(10)), "player-data reads did not finish");
		Assert.True(errors.IsEmpty, errors.TryPeek(out Exception? first) ? first.ToString() : "race threw");
	}

	private static object CreateChunkBatch()
	{
		Type posType = Type.GetType("Vintagestory.Common.Database.ChunkPos, VintagestoryLib")
			?? throw new InvalidOperationException("ChunkPos was not in the loaded lib");
		Type chunkType = Type.GetType("Vintagestory.Common.Database.DbChunk, VintagestoryLib")
			?? throw new InvalidOperationException("DbChunk was not in the loaded lib");
		Type listType = typeof(List<>).MakeGenericType(chunkType);
		object list = Activator.CreateInstance(listType)!;
		MethodInfo add = listType.GetMethod("Add")!;
		byte[] data = new byte[256];
		for (int z = 0; z < 32; z++)
		{
			object pos = Activator.CreateInstance(posType, 400000, 0, z, 0)!;
			object chunk = Activator.CreateInstance(chunkType, pos, data)!;
			add.Invoke(list, new[] { chunk });
		}
		return list;
	}

	private static ulong ChunkIndex(int x, int y, int z)
	{
		return (ulong)((uint)x | ((long)z << 27) | ((long)y << 54));
	}
}
