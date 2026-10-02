using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// With the send queue on, DisconnectPlayer only enqueues the reason and CloseConnection
/// shuts the socket down immediately. The reason has to be on the wire before that FIN.
/// </summary>
public class SendQueueDisconnectScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 60_000)]
	public void Shutdown_Should_DeliverQueuedBytes_When_SendQueueEnabled()
	{
		Type connectionType = Type.GetType("Vintagestory.Server.Network.TcpNetConnection, VintagestoryLib")
			?? throw new InvalidOperationException("TcpNetConnection was not in the loaded lib");

		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		int port = ((IPEndPoint)listener.LocalEndpoint).Port;

		using var client = new TcpClient();
		client.Connect(IPAddress.Loopback, port);
		using Socket accepted = listener.AcceptSocket();
		client.Client.ReceiveTimeout = 2000;

		object connection = Activator.CreateInstance(connectionType, accepted)!;
		connectionType.GetMethod("StartReceiving")!.Invoke(connection, null);
		object queue = connectionType.GetField("stratumSendQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)
			?? throw new InvalidOperationException("SendQueueEnabled did not attach a queue");

		byte[] payload = Encoding.ASCII.GetBytes("kick-reason");
		MethodInfo send = connectionType.GetMethod("Send", new[] { typeof(byte[]), typeof(bool) })!;
		send.Invoke(connection, new object[] { payload, false });
		connectionType.GetMethod("Shutdown")!.Invoke(connection, null);

		byte[] buffer = new byte[64];
		int read = client.Client.Receive(buffer);
		string text = Encoding.ASCII.GetString(buffer, 0, read);
		Assert.Contains("kick-reason", text, StringComparison.Ordinal);

		connectionType.GetMethod("Close")!.Invoke(connection, null);
		GC.KeepAlive(queue);
	}
}
