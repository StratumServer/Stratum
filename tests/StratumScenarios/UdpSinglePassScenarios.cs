using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// The single-pass UDP length prefix writes the same bytes as GetSize plus SerializeWithSize.
/// </summary>
public class UdpSinglePassScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task SinglePass_Should_MatchTheTwoPassBytes()
	{
		await World.Ticks(1);
		Type udpType = Type.GetType("Packet_UdpPacket, VintagestoryLib")!;
		Type positionType = Type.GetType("Packet_EntityPosition, VintagestoryLib")!;
		Type bulkType = Type.GetType("Packet_BulkEntityPosition, VintagestoryLib")!;
		Type serializer = Type.GetType("Packet_UdpPacketSerializer, VintagestoryLib")!;

		object positionPacket = Activator.CreateInstance(udpType)!;
		udpType.GetField("Id")!.SetValue(positionPacket, 5);
		udpType.GetField("EntityPosition")!.SetValue(positionPacket, Position(positionType, 7, 1000));
		Assert.Equal(TwoPass(serializer, positionPacket), SinglePass(serializer, positionPacket));

		object bulkPacket = Activator.CreateInstance(udpType)!;
		udpType.GetField("Id")!.SetValue(bulkPacket, 4);
		Array positions = Array.CreateInstance(positionType, 400);
		for (int i = 0; i < positions.Length; i++)
		{
			positions.SetValue(Position(positionType, i + 1, 1_000_000 + i), i);
		}
		object bulk = Activator.CreateInstance(bulkType)!;
		bulkType.GetMethod("SetEntityPositions", new[] { positionType.MakeArrayType(), typeof(int), typeof(int) })!
			.Invoke(bulk, new object[] { positions, positions.Length, positions.Length });
		udpType.GetField("BulkPositions")!.SetValue(bulkPacket, bulk);
		Assert.Equal(TwoPass(serializer, bulkPacket), SinglePass(serializer, bulkPacket));

		FieldInfo verify = serializer.GetField("StratumVerifyUdpLength", BindingFlags.Static | BindingFlags.NonPublic)!;
		verify.SetValue(null, true);
		try
		{
			SinglePass(serializer, positionPacket);
		}
		finally
		{
			verify.SetValue(null, false);
		}
	}

	private static object Position(Type positionType, long entityId, long x)
	{
		object position = Activator.CreateInstance(positionType)!;
		positionType.GetField("EntityId")!.SetValue(position, entityId);
		positionType.GetField("X")!.SetValue(position, x);
		positionType.GetField("Y")!.SetValue(position, x + 3);
		positionType.GetField("Z")!.SetValue(position, x + 9);
		positionType.GetField("MotionX")!.SetValue(position, x);
		return position;
	}

	private static byte[] TwoPass(Type serializer, object packet)
	{
		object stream = Stream();
		serializer.GetMethod("GetSize")!.Invoke(null, new[] { packet });
		serializer.GetMethod("SerializeWithSize")!.Invoke(null, new[] { stream, packet });
		return Bytes(stream);
	}

	private static byte[] SinglePass(Type serializer, object packet)
	{
		object stream = Stream();
		serializer.GetMethod("StratumSerializeWithSizeSinglePass")!.Invoke(null, new[] { stream, packet });
		return Bytes(stream);
	}

	private static object Stream()
	{
		return Activator.CreateInstance(Type.GetType("CitoMemoryStream, VintagestoryLib")!)!;
	}

	private static byte[] Bytes(object stream)
	{
		int length = (int)stream.GetType().GetMethod("Position")!.Invoke(stream, null)!;
		byte[] buffer = (byte[])stream.GetType().GetMethod("GetBuffer")!.Invoke(stream, null)!;
		byte[] copy = new byte[length];
		Buffer.BlockCopy(buffer, 0, copy, 0, length);
		return copy;
	}
}
