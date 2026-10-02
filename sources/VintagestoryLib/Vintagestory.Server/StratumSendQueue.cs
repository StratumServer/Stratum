using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.Server.Network;

namespace Vintagestory.Server;

// Stratum: single-writer send queue per connection. Replaces StratumNetworkFlush (disabled
// in the client-crash fix, see TcpNetConnection.SendPreparedBytes).
//
// The old flush buffered small packets and flushed at tick end, but TcpNetConnection.Send
// never went through the buffer, so it raced the flush and reordered packets on the wire.
// Physics offthreads sending concurrently with the tick thread made this worse: multiple
// threads posting SendAsync on the same socket can interleave partial sends even though
// each individual SendAsync call preserves its own buffer's byte order.
//
// This design closes that hole by construction. Every send path (Send, SendPreparedBytes)
// enqueues a fully framed packet. One drain task per connection is the only caller of
// Socket.SendAsync for that connection, so FIFO order holds regardless of which thread
// produced the packet. Small packets queued back-to-back get coalesced into one send;
// large packets (>= LargeThresholdBytes) go out alone without a copy.
internal sealed class StratumSendQueue
{
	private readonly Channel<byte[]> channel;
	private readonly TcpNetConnection connection;
	private readonly Socket socket;
	private readonly CancellationToken cancellationToken;
	private readonly int largeThreshold;
	private readonly int coalesceLimit;
	private readonly long maxPendingBytes;

	private readonly object startGate = new object();
	private Task drainTask;
	private int closed;

	private int pendingCount;
	private long pendingBytes;
	private int overflowDisconnectStarted;

	public int PendingCount => Volatile.Read(ref pendingCount);

	public long PendingBytes => Interlocked.Read(ref pendingBytes);

	public StratumSendQueue(TcpNetConnection connection, Socket socket, CancellationToken cancellationToken)
	{
		this.connection = connection;
		this.socket = socket;
		this.cancellationToken = cancellationToken;
		StratumNetworkConfig config = StratumRuntime.Config.Performance.Network;
		largeThreshold = config.LargeThresholdBytes;
		coalesceLimit = config.CoalesceLimitBytes;
		maxPendingBytes = config.MaxPendingBytes;
		channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
		{
			SingleReader = true,
			SingleWriter = false,
			AllowSynchronousContinuations = false
		});
	}

	// The drain task and its coalesce buffer stay unallocated until the first packet.
	// A connection that never sends (a scanner holding the socket open) then costs the
	// socket alone, not a 64 KiB buffer and a thread-pool task.
	private void EnsureDrainStarted()
	{
		if (drainTask != null)
		{
			return;
		}

		var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		drainTask = done.Task;
		TyronThreadPool.QueueTask(async () =>
		{
			try
			{
				await DrainAsync().ConfigureAwait(false);
				done.TrySetResult();
			}
			catch (Exception ex)
			{
				done.TrySetException(ex);
			}
		}, "StratumSendQueueDrain");
	}

	// Finish bytes already queued, then return. Close and Shutdown call this before they
	// cancel the socket, so a disconnect reason enqueued just above them still reaches
	// the client. The wait is bounded: a wedged socket does not hold the caller forever.
	public void FlushPending(int timeoutMs)
	{
		Task task;
		lock (startGate)
		{
			closed = 1;
			channel.Writer.TryComplete();
			task = drainTask;
		}

		if (task == null)
		{
			return;
		}

		try
		{
			task.Wait(Math.Max(0, timeoutMs));
		}
		catch (AggregateException)
		{
		}
	}

	// dataWithLength must already carry the 4-byte length prefix. Callers hand off a buffer
	// they will not mutate again (a fresh array per send), so no copy is needed here.
	public void Enqueue(byte[] dataWithLength)
	{
		if (Volatile.Read(ref overflowDisconnectStarted) != 0)
		{
			return;
		}

		int length = dataWithLength.Length;
		long queued = Interlocked.Add(ref pendingBytes, length);
		// Queue was already holding data and this packet would pass the cap. Roll it
		// back and disconnect. Dropping the packet without closing would split the
		// length-prefixed stream. Blocking here would put the gameplay thread back
		// on the slow client. An empty queue still accepts one packet so a single
		// large send is not refused.
		if (queued > maxPendingBytes && queued - length > 0)
		{
			Interlocked.Add(ref pendingBytes, -length);
			DisconnectOverflow();
			return;
		}

		Interlocked.Increment(ref pendingCount);
		lock (startGate)
		{
			if (closed != 0 || !channel.Writer.TryWrite(dataWithLength))
			{
				// Writer already completed (connection closing). Drop, matches vanilla behavior
				// of a send attempted after Close()/Dispose().
				Interlocked.Decrement(ref pendingCount);
				Interlocked.Add(ref pendingBytes, -length);
				return;
			}

			EnsureDrainStarted();
		}
	}

	private void DisconnectOverflow()
	{
		if (Interlocked.Exchange(ref overflowDisconnectStarted, 1) != 0)
		{
			return;
		}

		string player = connection.client?.PlayerName;
		if (string.IsNullOrEmpty(player))
		{
			player = "unidentified";
		}

		string address = connection.Address;
		if (string.IsNullOrEmpty(address))
		{
			address = connection.TcpSocket?.RemoteEndPoint?.ToString() ?? "unknown";
		}

		StratumRuntime.LogWarning("StratumSendQueue disconnected " + player + " at " + address + ": pending bytes exceeded Performance.Network.MaxPendingBytes (" + maxPendingBytes + ").");
		connection.InvokeDisconnected();
	}

	public void Complete()
	{
		channel.Writer.TryComplete();
	}

	private async Task DrainAsync()
	{
		ChannelReader<byte[]> reader = channel.Reader;
		byte[] coalesceBuffer = null;
		try
		{
			while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
			{
				int coalescedLength = 0;
				while (coalescedLength < coalesceLimit && reader.TryRead(out byte[] packet))
				{
					if (packet.Length >= largeThreshold)
					{
						if (coalescedLength > 0)
						{
							// Flush what is already queued first so this large packet does not
							// jump ahead of packets enqueued earlier.
							if (!await SendAsync(coalesceBuffer, coalescedLength).ConfigureAwait(false)) return;
							coalescedLength = 0;
						}

						Decrement(packet.Length);
						if (!await SendAsync(packet, packet.Length).ConfigureAwait(false)) return;
						continue;
					}

					coalesceBuffer ??= new byte[coalesceLimit];
					if (coalescedLength + packet.Length > coalesceLimit)
					{
						if (!await SendAsync(coalesceBuffer, coalescedLength).ConfigureAwait(false)) return;
						coalescedLength = 0;
					}

					Decrement(packet.Length);
					Buffer.BlockCopy(packet, 0, coalesceBuffer, coalescedLength, packet.Length);
					coalescedLength += packet.Length;
				}

				if (coalescedLength > 0)
				{
					if (!await SendAsync(coalesceBuffer, coalescedLength).ConfigureAwait(false)) return;
				}
			}
		}
		catch (OperationCanceledException)
		{
			// Connection closing, drain loop exits normally.
		}
	}

	private void Decrement(int bytes)
	{
		Interlocked.Decrement(ref pendingCount);
		Interlocked.Add(ref pendingBytes, -bytes);
	}

	private async Task<bool> SendAsync(byte[] buffer, int length)
	{
		try
		{
			// This overload returns the number of bytes accepted. A short write leaves a
			// truncated length-prefixed frame, and the next packet would be parsed as the
			// rest of it. Retry the tail. A non-positive count means the socket took
			// nothing; disconnect instead of spinning.
			int offset = 0;
			while (offset < length)
			{
				int sent = await socket.SendAsync(new ReadOnlyMemory<byte>(buffer, offset, length - offset), SocketFlags.None, cancellationToken).ConfigureAwait(false);
				if (sent <= 0)
				{
					connection.InvokeDisconnected();
					return false;
				}

				offset += sent;
			}

			return true;
		}
		catch (OperationCanceledException)
		{
			return false;
		}
		catch
		{
			connection.InvokeDisconnected();
			return false;
		}
	}
}
