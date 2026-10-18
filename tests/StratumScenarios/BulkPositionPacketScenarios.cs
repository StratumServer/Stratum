using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// A bulk position packet this server did not rent keeps its positions when the
/// UDP thread returns it. A rented packet gives its array back.
/// </summary>
public class BulkPositionPacketScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task ForeignPacket_Should_KeepItsPositions_When_ReturnedTwice()
	{
		await World.Ticks(1);
		object physics = Physics(World.Api.World);
		Type positionType = Type.GetType("Packet_EntityPosition, VintagestoryLib")!;
		Type bulkType = Type.GetType("Packet_BulkEntityPosition, VintagestoryLib")!;
		Type udpType = Type.GetType("Packet_UdpPacket, VintagestoryLib")!;
		MethodInfo ret = physics.GetType().GetMethod("StratumReturnBulkPositionPacket", BindingFlags.Instance | BindingFlags.NonPublic)!;

		Array foreign = Array.CreateInstance(positionType, 1);
		foreign.SetValue(Activator.CreateInstance(positionType), 0);
		object bulk = Activator.CreateInstance(bulkType)!;
		bulkType.GetMethod("SetEntityPositions", new[] { positionType.MakeArrayType() })!.Invoke(bulk, new[] { foreign });
		object udp = Packet(udpType, bulk);
		ret.Invoke(physics, new[] { udp, false });
		ret.Invoke(physics, new[] { udp, false });
		Assert.Same(foreign, bulkType.GetField("EntityPositions")!.GetValue(bulk));
		Assert.Equal(1, bulkType.GetField("EntityPositionsCount")!.GetValue(bulk));

		MethodInfo rent = physics.GetType().GetMethod("StratumRentPositionArray", BindingFlags.Instance | BindingFlags.NonPublic)!;
		Array rentedArray = (Array)rent.Invoke(physics, new object[] { 1 })!;
		Type rentedType = physics.GetType().GetNestedType("StratumRentedBulkPositionPacket", BindingFlags.NonPublic)!;
		object rented = Activator.CreateInstance(rentedType)!;
		rentedType.GetField("Rented", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rented, rentedArray);
		Array swapped = Array.CreateInstance(positionType, 3);
		bulkType.GetMethod("SetEntityPositions", new[] { positionType.MakeArrayType(), typeof(int), typeof(int) })!
			.Invoke(rented, new object[] { swapped, 1, swapped.Length });
		ret.Invoke(physics, new[] { Packet(udpType, rented), false });
		Assert.Null(bulkType.GetField("EntityPositions")!.GetValue(rented));

		bulkType.GetMethod("SetEntityPositions", new[] { positionType.MakeArrayType(), typeof(int), typeof(int) })!
			.Invoke(rented, new object[] { rentedArray, 1, rentedArray.Length });
		rentedType.GetField("Rented", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rented, rentedArray);
		ret.Invoke(physics, new[] { Packet(udpType, rented), false });
		Assert.Null(bulkType.GetField("EntityPositions")!.GetValue(rented));
		Assert.Equal(0, bulkType.GetField("EntityPositionsCount")!.GetValue(rented));
		Assert.Same(rentedArray, rent.Invoke(physics, new object[] { 1 }));
	}

	private static object Packet(Type udpType, object bulk)
	{
		object udp = Activator.CreateInstance(udpType)!;
		udpType.GetField("Id")!.SetValue(udp, 4);
		udpType.GetField("BulkPositions")!.SetValue(udp, bulk);
		return udp;
	}

	private static object Physics(object server)
	{
		FieldInfo systemsField = server.GetType().GetField("Systems", BindingFlags.Instance | BindingFlags.NonPublic)!;
		Array systems = (Array)systemsField.GetValue(server)!;
		object simulation = systems.Cast<object>().First(system => system.GetType().Name == "ServerSystemEntitySimulation");
		return simulation.GetType().GetField("physicsManager", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(simulation)!;
	}
}
