using System.Collections;
using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// A second position for the same client and entity replaces the unsent one.
/// The UDP queue does not grow one entry per movement.
/// </summary>
public class UdpPositionCoalesceScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task SecondPosition_Should_ReplaceTheUnsentEntry()
	{
		await World.JoinPlayer("udp-coalesce");
		await World.Ticks(1);
		object server = World.Api.World;
		object network = server.GetType().GetField("ServerUdpNetwork")!.GetValue(server)!;
		FieldInfo queueField = network.GetType().GetField("ImmediateUdpQueue", BindingFlags.Instance | BindingFlags.NonPublic)!;
		object previous = queueField.GetValue(network)!;
		Type queueType = previous.GetType();
		object queue = Activator.CreateInstance(queueType, server, network)!;
		try
		{
			dynamic clients = server.GetType().GetField("Clients")!.GetValue(server)!;
			object client = null;
			foreach (object candidate in (IEnumerable)clients.Values)
			{
				client = candidate;
				break;
			}
			Assert.NotNull(client);

			object first = PositionPacket(1, 10);
			object second = PositionPacket(1, 20);
			object other = PositionPacket(2, 30);
			MethodInfo enqueue = queueType.GetMethod("QueuePacket", BindingFlags.Instance | BindingFlags.NonPublic)!;
			enqueue.Invoke(queue, new[] { client, first });
			enqueue.Invoke(queue, new[] { client, second });
			enqueue.Invoke(queue, new[] { client, other });

			object entries = queueType.GetField("queue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(queue)!;
			Assert.Equal(2, (int)entries.GetType().GetProperty("Count")!.GetValue(entries)!);
			object held = FirstPacket(entries);
			Assert.Same(second, held);
		}
		finally
		{
			queueField.SetValue(network, previous);
		}
	}

	private static object PositionPacket(long entityId, int x)
	{
		Type positionType = Type.GetType("Packet_EntityPosition, VintagestoryLib")!;
		Type udpType = Type.GetType("Packet_UdpPacket, VintagestoryLib")!;
		object position = Activator.CreateInstance(positionType)!;
		positionType.GetField("EntityId")!.SetValue(position, entityId);
		positionType.GetField("X")!.SetValue(position, (long)x);
		object packet = Activator.CreateInstance(udpType)!;
		udpType.GetField("Id")!.SetValue(packet, 5);
		udpType.GetField("EntityPosition")!.SetValue(packet, position);
		return packet;
	}

	private static object FirstPacket(object entries)
	{
		object queued = null;
		foreach (object item in (IEnumerable)entries)
		{
			queued = item;
			break;
		}
		return queued!.GetType().GetField("packet", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(queued)!;
	}
}
