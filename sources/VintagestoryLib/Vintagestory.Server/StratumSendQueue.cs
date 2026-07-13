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

	private int pendingCount;
	private long pendingBytes;

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
		channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
		{
			SingleReader = true,
			SingleWriter = false,
			AllowSynchronousContinuations = false
		});
	}

	public void Start()
	{
		TyronThreadPool.QueueTask((Func<Task>)DrainAsync, "StratumSendQueueDrain");
	}

	// dataWithLength must already carry the 4-byte length prefix. Callers hand off a buffer
	// they will not mutate again (a fresh array per send), so no copy is needed here.
	public void Enqueue(byte[] dataWithLength)
	{
		Interlocked.Increment(ref pendingCount);
		Interlocked.Add(ref pendingBytes, dataWithLength.Length);
		if (!channel.Writer.TryWrite(dataWithLength))
		{
			// Writer already completed (connection closing). Drop, matches vanilla behavior
			// of a send attempted after Close()/Dispose().
			Interlocked.Decrement(ref pendingCount);
			Interlocked.Add(ref pendingBytes, -dataWithLength.Length);
		}
	}

	public void Complete()
	{
		channel.Writer.TryComplete();
	}

	private async Task DrainAsync()
	{
		ChannelReader<byte[]> reader = channel.Reader;
		byte[] coalesceBuffer = new byte[coalesceLimit];
		try
		{
			while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
			{
				int coalescedLength = 0;
				while (coalescedLength < coalesceBuffer.Length && reader.TryRead(out byte[] packet))
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

					if (coalescedLength + packet.Length > coalesceBuffer.Length)
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
			await socket.SendAsync(new ReadOnlyMemory<byte>(buffer, 0, length), SocketFlags.None, cancellationToken).ConfigureAwait(false);
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
