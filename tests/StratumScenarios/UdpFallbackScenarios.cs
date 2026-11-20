using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// TCP-fallback position sends must reuse one packet wrapper, and returning a
/// bulk-position array the pool does not own must not kill the UDP thread.
/// </summary>
public class UdpFallbackScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task FallbackSend_Should_ReuseOneWrapper_And_ClearIt()
	{
		ITestPlayer player = await World.JoinPlayer("udp-fallback");
		object server = World.Api.World;
		object client = Client(server, player.Player.ClientId);
		client.GetType().GetField("FallBackToTcp")!.SetValue(client, true);

		Type udpType = Type.GetType("Packet_UdpPacket, VintagestoryLib")
			?? throw new InvalidOperationException("Packet_UdpPacket was not in the loaded lib");
		object packet = Activator.CreateInstance(udpType)!;
		udpType.GetField("Id")!.SetValue(packet, 1);

		MethodInfo send = server.GetType().GetMethod("SendPacketBlocking", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("SendPacketBlocking was not on the server");
		send.Invoke(server, new[] { client, packet });
		send.Invoke(server, new[] { client, packet });

		FieldInfo wrapperField = server.GetType().GetField("stratumFallbackPacket", BindingFlags.Static | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("stratumFallbackPacket was not on the server");
		object wrapper = wrapperField.GetValue(null)
			?? throw new InvalidOperationException("the fallback wrapper was not created");
		Assert.Null(wrapper.GetType().GetField("UdpPacket")!.GetValue(wrapper));
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task ForeignPositionArray_Should_NotThrow_When_Returned()
	{
		await World.JoinPlayer("udp-return");
		object physics = Physics(World.Api.World);
		Type positionType = Type.GetType("Packet_EntityPosition, VintagestoryLib")
			?? throw new InvalidOperationException("Packet_EntityPosition was not in the loaded lib");
		Array foreign = Array.CreateInstance(positionType, 4);
		Type bulkType = Type.GetType("Packet_BulkEntityPosition, VintagestoryLib")
			?? throw new InvalidOperationException("Packet_BulkEntityPosition was not in the loaded lib");
		object bulk = Activator.CreateInstance(bulkType)!;
		bulkType.GetMethod("SetEntityPositions", new[] { foreign.GetType() })!.Invoke(bulk, new object[] { foreign });

		Type udpType = Type.GetType("Packet_UdpPacket, VintagestoryLib")!;
		object packet = Activator.CreateInstance(udpType)!;
		udpType.GetField("Id")!.SetValue(packet, 4);
		udpType.GetField("BulkPositions")!.SetValue(packet, bulk);

		MethodInfo ret = physics.GetType().GetMethod("StratumReturnBulkPositionPacket", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("StratumReturnBulkPositionPacket was not on the physics manager");
		ret.Invoke(physics, new[] { packet, false });
		ret.Invoke(physics, new[] { packet, false });
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task SinglePlayerPositionPacket_Should_RemainIntact_WhileQueued()
	{
		await World.JoinPlayer("udp-singleplayer");
		object physics = Physics(World.Api.World);
		Type positionType = Type.GetType("Packet_EntityPosition, VintagestoryLib")
			?? throw new InvalidOperationException("Packet_EntityPosition was not in the loaded lib");
		Array positions = Array.CreateInstance(positionType, 4);
		Type bulkType = Type.GetType("Packet_BulkEntityPosition, VintagestoryLib")
			?? throw new InvalidOperationException("Packet_BulkEntityPosition was not in the loaded lib");
		object bulk = Activator.CreateInstance(bulkType)!;
		bulkType.GetMethod("SetEntityPositions", new[] { positions.GetType() })!.Invoke(bulk, new object[] { positions });

		Type udpType = Type.GetType("Packet_UdpPacket, VintagestoryLib")!;
		object packet = Activator.CreateInstance(udpType)!;
		udpType.GetField("Id")!.SetValue(packet, 4);
		udpType.GetField("BulkPositions")!.SetValue(packet, bulk);

		MethodInfo ret = physics.GetType().GetMethod("StratumReturnBulkPositionPacket", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("StratumReturnBulkPositionPacket was not on the physics manager");
		ret.Invoke(physics, new[] { packet, true });

		Assert.Same(positions, bulkType.GetField("EntityPositions")!.GetValue(bulk));
		Assert.Equal(4, bulkType.GetField("EntityPositionsCount")!.GetValue(bulk));
	}

	private static object Client(object server, int clientId)
	{
		object clients = server.GetType().GetField("Clients")!.GetValue(server)!;
		return ((dynamic)clients)[clientId];
	}

	private static object Physics(object server)
	{
		object systems = server.GetType().GetField("Systems", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
		foreach (object system in (System.Collections.IEnumerable)systems)
		{
			FieldInfo? field = system.GetType().GetField("physicsManager", BindingFlags.Instance | BindingFlags.NonPublic);
			if (field != null)
			{
				return field.GetValue(system)!;
			}
		}
		throw new InvalidOperationException("physicsManager was not on a server system");
	}
}
