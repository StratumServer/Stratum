using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
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
		using ConnectionHarness harness = ConnectionHarness.Open();
		harness.Accepted.SendBufferSize = 1024;
		byte[] backlog = new byte[256 * 1024];
		harness.Send(backlog);

		Stopwatch watch = Stopwatch.StartNew();
		harness.Shutdown();
		watch.Stop();
		Assert.True(watch.ElapsedMilliseconds < 50, "Shutdown blocked for " + watch.ElapsedMilliseconds + " ms");
		harness.Close();
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

		private ConnectionHarness(TcpListener listener, TcpClient client, Socket accepted, object connection)
		{
			this.listener = listener;
			Client = client;
			Accepted = accepted;
			this.connection = connection;
		}

		public static ConnectionHarness Open()
		{
			SetSendQueueEnabled(true);
			TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			int port = ((IPEndPoint)listener.LocalEndpoint).Port;
			TcpClient client = new TcpClient();
			client.Connect(IPAddress.Loopback, port);
			Socket accepted = listener.AcceptSocket();
			client.Client.ReceiveTimeout = 2000;
			object connection = Activator.CreateInstance(ConnectionType, accepted)!;
			ConnectionType.GetMethod("StartReceiving")!.Invoke(connection, null);
			object queue = ConnectionType.GetField("stratumSendQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)
				?? throw new InvalidOperationException("SendQueueEnabled did not attach a queue");
			GC.KeepAlive(queue);
			return new ConnectionHarness(listener, client, accepted, connection) { sendQueueForced = true };
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
	}
}
