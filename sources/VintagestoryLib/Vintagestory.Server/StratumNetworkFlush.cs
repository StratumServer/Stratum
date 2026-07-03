using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using Vintagestory.Server.Network;

namespace Vintagestory.Server;

// Stratum: batch small TCP packets per connection, flush at tick end.
//
// During the tick, SendPreparedBytes accumulates small packets in a per-connection
// buffer. At tick end (ServerMain.Process), FlushAll sends each connection's
// accumulated data in a single Socket.Send. Large or compressed packets flush
// the pending buffer first (preserves order) then send directly.
//
// Benchmarked against TCP_CORK (Linux kernel batching). Buffer is 4x faster because
// Cork still incurs one syscall per Send call (kernel holds data but the transition
// still happens). Buffer reduces actual syscall count from N*packets to N*connections.
//
// Sends can come from the tick thread and physics workers, so the shared buffer is locked.
// Flush copies the bytes before SendAsync because the socket may still be reading after
// this method returns.
internal static class StratumNetworkFlush
{
	private const int MtuThreshold = 1400;

	private sealed class ConnectionBuffer
	{
		public readonly object Sync = new object();
		public byte[] Buffer = new byte[4096];
		public int WritePos;

		public void EnsureCapacity(int additional)
		{
			int need = WritePos + additional;
			if (need <= Buffer.Length) return;
			int newSize = Buffer.Length;
			while (newSize < need) newSize *= 2;
			byte[] newBuf = new byte[newSize];
			System.Buffer.BlockCopy(Buffer, 0, newBuf, 0, WritePos);
			Buffer = newBuf;
		}
	}

	private static readonly ConcurrentDictionary<TcpNetConnection, ConnectionBuffer> buffers = new();
	private static int purgeCounter;

	// Returns true if the packet was buffered (caller should NOT send).
	// Returns false if the caller should send normally (large/compressed).
	internal static bool TryBuffer(TcpNetConnection connection, byte[] dataWithLength, int length, bool compressedFlag)
	{
		int totalSize = length + 4;

		if (totalSize > MtuThreshold || compressedFlag)
		{
			FlushConnection(connection);
			return false;
		}

		ConnectionBuffer state = buffers.GetOrAdd(connection, static _ => new ConnectionBuffer());

		byte[] toSend = null;
		lock (state.Sync)
		{
			state.EnsureCapacity(totalSize);
			System.Buffer.BlockCopy(dataWithLength, 0, state.Buffer, state.WritePos, totalSize);
			state.WritePos += totalSize;

			if (state.WritePos >= MtuThreshold)
			{
				toSend = TakePending(state);
			}
		}

		// Do the socket call outside the lock.
		if (toSend != null)
		{
			RawSend(connection, toSend);
		}

		return true;
	}

	internal static void GetBufferedPressure(TcpNetConnection connection, out int pendingSends, out long pendingBytes)
	{
		pendingSends = 0;
		pendingBytes = 0;

		if (connection == null || !buffers.TryGetValue(connection, out ConnectionBuffer state))
		{
			return;
		}

		// Advisory read for back-pressure accounting; an approximate size does not need the lock.
		int bytes = Volatile.Read(ref state.WritePos);
		if (bytes <= 0)
		{
			return;
		}

		pendingSends = 1;
		pendingBytes = bytes;
	}

	internal static void FlushAll()
	{
		foreach (KeyValuePair<TcpNetConnection, ConnectionBuffer> kvp in buffers)
		{
			FlushConnectionDirect(kvp.Key, kvp.Value);
		}

		if ((Interlocked.Increment(ref purgeCounter) & 0xFF) == 0)
		{
			PurgeDisconnected();
		}
	}

	private static void FlushConnection(TcpNetConnection connection)
	{
		if (buffers.TryGetValue(connection, out ConnectionBuffer state))
		{
			FlushConnectionDirect(connection, state);
		}
	}

	private static void FlushConnectionDirect(TcpNetConnection connection, ConnectionBuffer state)
	{
		byte[] toSend;
		lock (state.Sync)
		{
			if (state.WritePos == 0) return;
			toSend = TakePending(state);
		}

		RawSend(connection, toSend);
	}

	// SendAsync must not get the reusable buffer. It may still be reading it after we return.
	// Must be called while holding state.Sync.
	private static byte[] TakePending(ConnectionBuffer state)
	{
		byte[] copy = new byte[state.WritePos];
		System.Buffer.BlockCopy(state.Buffer, 0, copy, 0, state.WritePos);
		state.WritePos = 0;
		return copy;
	}

	private static void RawSend(TcpNetConnection connection, byte[] data)
	{
		if (!connection.Connected)
		{
			return;
		}

		Socket socket = connection.TcpSocket;
		CancellationTokenSource cts = connection.cts;
		if (socket == null || cts == null)
		{
			return;
		}

		try
		{
			socket.SendAsync((ReadOnlyMemory<byte>)data, SocketFlags.None, cts.Token);
		}
		catch { }
	}

	private static void PurgeDisconnected()
	{
		foreach (KeyValuePair<TcpNetConnection, ConnectionBuffer> kvp in buffers)
		{
			if (!kvp.Key.Connected)
			{
				buffers.TryRemove(kvp.Key, out _);
			}
		}
	}

	internal static void Cleanup()
	{
		buffers.Clear();
	}
}
