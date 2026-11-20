using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// With the send queue on, DisconnectPlayer only enqueues the reason. Shutdown must
/// put that reason on the wire before the FIN, and it must return without waiting
/// on a peer that never reads.
/// The fixture forces the queue on. The shipped default is off.
/// </summary>
[AtlasDataFiles("fixtures/stratum-sendqueue-on", TargetPath = "")]
public class SendQueueDisconnectScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 60_000)]
	public void Shutdown_Should_DeliverQueuedBytes_When_SendQueueEnabled()
	{
		using ConnectionHarness harness = ConnectionHarness.Open();
		byte[] payload = Encoding.ASCII.GetBytes("kick-reason");
		harness.Send(payload);
		harness.Shutdown();

		byte[] buffer = new byte[64];
		int read = harness.Client.Client.Receive(buffer);
		string text = Encoding.ASCII.GetString(buffer, 0, read);
		Assert.Contains("kick-reason", text, StringComparison.Ordinal);
		harness.Close();
	}

	[AtlasScenario(TimeoutMs = 60_000)]
	public void Shutdown_Should_ReturnImmediately_When_PeerNeverReads()
	{
		// A 4 KiB receive buffer absorbs about 8 KiB. A few MiB stays queued, so the
		// drain is still inside SendAsync when Shutdown returns, and the FIN follows
		// the drain or the 250 ms bound.
		using ConnectionHarness harness = ConnectionHarness.Open(receiveBufferSize: 4096);
		harness.Accepted.SendBufferSize = 1024;
		byte[] backlog = new byte[4 * 1024 * 1024];
		harness.Send(backlog);

		Stopwatch watch = Stopwatch.StartNew();
		harness.Shutdown();
		watch.Stop();
		Assert.True(watch.ElapsedMilliseconds < 50, "Shutdown blocked for " + watch.ElapsedMilliseconds + " ms");
		Task drain = harness.DrainTask;
		Assert.False(drain.IsCompleted, "drain finished before the peer read");

		Socket socket = harness.Client.Client;
		socket.ReceiveTimeout = 50;
		byte[] buffer = new byte[64 * 1024];
		int got = 0;
		bool closed = false;
		Stopwatch eof = Stopwatch.StartNew();
		while (eof.ElapsedMilliseconds < 500)
		{
			try
			{
				int read = socket.Receive(buffer);
				if (read == 0)
				{
					closed = true;
					break;
				}
				got += read;
			}
			catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
			{
			}
		}
		Assert.True(closed, "no EOF after " + eof.ElapsedMilliseconds + " ms, read " + got + " bytes");
		Assert.True(got > 8192, "peer absorbed the backlog, read only " + got + " bytes");
		harness.Close();
	}

	[AtlasScenario(TimeoutMs = 60_000)]
	public void ServerQuery_Should_AnswerAndClose_When_SendQueueEnabled()
	{
		using ConnectionHarness harness = ConnectionHarness.Open();
		byte[] query = ConnectionHarness.SerializeServerQuery();
		object server = World.Api.World;
		object message = ConnectionHarness.CreateDataMessage(harness.Connection, query);
		MethodInfo process = server.GetType().GetMethod("ProcessNetMessage", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("ProcessNetMessage was not on the server");
		process.Invoke(server, new[] { message });

		byte[] expected = ConnectionHarness.PreparedQueryAnswer();
		byte[] received = new byte[expected.Length];
		int filled = 0;
		Socket socket = harness.Client.Client;
		socket.ReceiveTimeout = 2000;
		while (filled < received.Length)
		{
			int read = socket.Receive(received, filled, received.Length - filled, SocketFlags.None);
			Assert.NotEqual(0, read);
			filled += read;
		}
		Assert.Equal(expected, received);

		Stopwatch eof = Stopwatch.StartNew();
		int end = socket.Receive(new byte[16]);
		eof.Stop();
		Assert.Equal(0, end);
		Assert.True(eof.ElapsedMilliseconds < 500, "FIN arrived after " + eof.ElapsedMilliseconds + " ms");
		harness.Close();
	}

	[AtlasScenario(TimeoutMs = 60_000)]
	public void OversizedPacket_Should_StayQueued_When_SmallPacketIsAhead()
	{
		const int cap = 128 * 1024;
		ConnectionHarness.SetMaxPendingBytes(cap);
		try
		{
			using ConnectionHarness harness = ConnectionHarness.Open();
			harness.Send(new byte[64]);
			harness.Send(new byte[cap - 4]);
			Assert.False(harness.OverflowStarted);
		}
		finally
		{
			ConnectionHarness.SetMaxPendingBytes(8 * 1024 * 1024);
		}
	}

	private sealed class ConnectionHarness : IDisposable
	{
		private static readonly Type ConnectionType = Type.GetType("Vintagestory.Server.Network.TcpNetConnection, VintagestoryLib")
			?? throw new InvalidOperationException("TcpNetConnection was not in the loaded lib");

		private readonly TcpListener listener;
		private readonly object connection;
		private bool sendQueueForced;

		public TcpClient Client { get; }

		public Socket Accepted { get; }

		public object Connection => connection;

		public Task DrainTask
		{
			get
			{
				return (Task)queue.GetType().GetField("drainTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(queue)!;
			}
		}

		private readonly object queue;

		private ConnectionHarness(TcpListener listener, TcpClient client, Socket accepted, object connection, object queue)
		{
			this.listener = listener;
			Client = client;
			Accepted = accepted;
			this.connection = connection;
			this.queue = queue;
		}

		public static ConnectionHarness Open(int? receiveBufferSize = null)
		{
			SetSendQueueEnabled(true);
			TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			int port = ((IPEndPoint)listener.LocalEndpoint).Port;
			TcpClient client = new TcpClient();
			if (receiveBufferSize.HasValue)
			{
				client.ReceiveBufferSize = receiveBufferSize.Value;
			}
			client.Connect(IPAddress.Loopback, port);
			Socket accepted = listener.AcceptSocket();
			client.Client.ReceiveTimeout = 2000;
			object connection = Activator.CreateInstance(ConnectionType, accepted)!;
			ConnectionType.GetMethod("StartReceiving")!.Invoke(connection, null);
			object queue = ConnectionType.GetField("stratumSendQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)
				?? throw new InvalidOperationException("SendQueueEnabled did not attach a queue");
			GC.KeepAlive(queue);
			return new ConnectionHarness(listener, client, accepted, connection, queue) { sendQueueForced = true };
		}

		public bool OverflowStarted
		{
			get
			{
				return (int)queue.GetType().GetField("overflowDisconnectStarted", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(queue)! != 0;
			}
		}

		public void Send(byte[] payload)
		{
			MethodInfo send = ConnectionType.GetMethod("Send", new[] { typeof(byte[]), typeof(bool) })!;
			send.Invoke(connection, new object[] { payload, false });
		}

		public void Shutdown()
		{
			ConnectionType.GetMethod("Shutdown")!.Invoke(connection, null);
		}

		public void Close()
		{
			ConnectionType.GetMethod("Close")!.Invoke(connection, null);
		}

		public void Dispose()
		{
			try
			{
				Close();
			}
			catch (TargetInvocationException)
			{
			}
			Client.Dispose();
			listener.Stop();
			if (sendQueueForced)
			{
				SetSendQueueEnabled(false);
			}
		}

		private static void SetSendQueueEnabled(bool enabled)
		{
			Type runtime = Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib")
				?? throw new InvalidOperationException("StratumRuntime was not in the loaded lib");
			object config = runtime.GetProperty("Config")!.GetValue(null)!;
			object performance = config.GetType().GetProperty("Performance")!.GetValue(config)!;
			object network = performance.GetType().GetProperty("Network")!.GetValue(performance)!;
			network.GetType().GetProperty("SendQueueEnabled")!.SetValue(network, enabled);
		}

		public static byte[] SerializeServerQuery()
		{
			Type packetType = Type.GetType("Packet_Client, VintagestoryLib")
				?? throw new InvalidOperationException("Packet_Client was not in the loaded lib");
			object packet = Activator.CreateInstance(packetType)!;
			packetType.GetField("Id")!.SetValue(packet, 15);
			Type serializer = Type.GetType("Packet_ClientSerializer, VintagestoryLib")
				?? throw new InvalidOperationException("Packet_ClientSerializer was not in the loaded lib");
			return (byte[])serializer.GetMethod("SerializeToBytes")!.Invoke(null, new[] { packet })!;
		}

		public static object CreateDataMessage(object connection, byte[] payload)
		{
			Type messageType = Type.GetType("Vintagestory.Common.NetIncomingMessage, VintagestoryLib")
				?? throw new InvalidOperationException("NetIncomingMessage was not in the loaded lib");
			object message = Activator.CreateInstance(messageType)!;
			messageType.GetField("SenderConnection")!.SetValue(message, connection);
			Type typeEnum = Type.GetType("Vintagestory.Common.NetworkMessageType, VintagestoryLib")
				?? throw new InvalidOperationException("NetworkMessageType was not in the loaded lib");
			messageType.GetField("Type")!.SetValue(message, Enum.Parse(typeEnum, "Data"));
			messageType.GetField("message")!.SetValue(message, payload);
			messageType.GetField("messageLength")!.SetValue(message, payload.Length);
			return message;
		}

		public static byte[] PreparedQueryAnswer()
		{
			Type queryType = Type.GetType("Vintagestory.Server.StratumServerListQuery, VintagestoryLib")
				?? throw new InvalidOperationException("StratumServerListQuery was not in the loaded lib");
			object instance = queryType.GetField("Instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
			return (byte[])queryType.GetProperty("PreparedPacket", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
		}

		public static void SetMaxPendingBytes(int bytes)
		{
			Type runtime = Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib")
				?? throw new InvalidOperationException("StratumRuntime was not in the loaded lib");
			object config = runtime.GetProperty("Config")!.GetValue(null)!;
			object performance = config.GetType().GetProperty("Performance")!.GetValue(config)!;
			object network = performance.GetType().GetProperty("Network")!.GetValue(performance)!;
			network.GetType().GetProperty("MaxPendingBytes")!.SetValue(network, bytes);
		}
	}
}
