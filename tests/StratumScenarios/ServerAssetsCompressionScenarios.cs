using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// The server assets packet is immutable. Dedicated-server joins must compress it
/// once. The scenario client is a dummy connection, which the server treats as
/// singleplayer and never compresses, so the test sends the same packet twice
/// down the dedicated path and the counter has to stay at one.
/// </summary>
public class ServerAssetsCompressionScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task AssetsPacket_Should_CompressOnce_When_SentTwice()
	{
		ITestPlayer player = await World.JoinPlayer("assets-a");
		object server = World.Api.World;
		object client = Client(server, player.Player.ClientId);
		FieldInfo singlePlayer = client.GetType().GetField("IsSinglePlayerClient")
			?? throw new InvalidOperationException("IsSinglePlayerClient was not on the client");
		bool previous = (bool)singlePlayer.GetValue(client)!;
		singlePlayer.SetValue(client, false);
		try
		{
			MethodInfo send = server.GetType().GetMethod("SendServerAssets")
				?? throw new InvalidOperationException("SendServerAssets was not on the server");
			send.Invoke(server, new object[] { player.Player });
			send.Invoke(server, new object[] { player.Player });
		}
		finally
		{
			singlePlayer.SetValue(client, previous);
		}

		FieldInfo compressionsField = server.GetType().GetField("stratumServerAssetsCompressions", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("stratumServerAssetsCompressions was not on the server");
		Assert.Equal(1, (int)compressionsField.GetValue(server)!);
	}

	private static object Client(object server, int clientId)
	{
		object clients = server.GetType().GetField("Clients")!.GetValue(server)!;
		return ((dynamic)clients)[clientId];
	}
}
