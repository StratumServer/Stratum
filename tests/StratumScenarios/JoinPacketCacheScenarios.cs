using System.Reflection;
using System.Text;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Datastructures;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// The server identification and world metadata packets both embed the world config.
/// Stratum caches both for joins, and `/wc`, `/worldconfigcreate` and mods edit that
/// config in place while the server runs. A new joiner must get the edited config, not
/// the one the cache was built from (#211).
/// </summary>
public class JoinPacketCacheScenarios : AtlasScenarioBase
{
	private const string ProbeKey = "stratumJoinCacheProbe";

	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task JoinPackets_Should_CarryEditedWorldConfig_When_ConfigChangesAfterCaching()
	{
		ITestPlayer player = await World.JoinPlayer("joincache-a");
		object server = World.Api.World;
		ITreeAttribute worldConfig = World.Api.WorldManager.SaveGame.WorldConfiguration;
		byte[] probe = Encoding.UTF8.GetBytes(ProbeKey);
		try
		{
			SendBoth(server, player);
			Assert.False(Contains(IdentificationPacket(server, player), probe), "the probe key was in the identification packet before it was set");
			Assert.False(Contains(WorldMetaDataPacket(server), probe), "the probe key was in the world metadata packet before it was set");

			worldConfig.SetBool(ProbeKey, true);
			SendBoth(server, player);

			Assert.True(Contains(IdentificationPacket(server, player), probe), "the cached identification packet still carried the old world config");
			Assert.True(Contains(WorldMetaDataPacket(server), probe), "the cached world metadata packet still carried the old world config");
		}
		finally
		{
			worldConfig.RemoveAttribute(ProbeKey);
			player.Player.Disconnect();
		}
	}

	private static void SendBoth(object server, ITestPlayer player)
	{
		Invoke(server, "SendStratumCachedServerIdentification", player.Player);
		Invoke(server, "SendStratumCachedWorldMetaData", player.Player);
	}

	private static void Invoke(object server, string name, object player)
	{
		MethodInfo method = server.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException(name + " was not on the server");
		method.Invoke(server, new[] { player });
	}

	private static object IdentificationPacket(object server, ITestPlayer player)
	{
		Array packets = (Array)Field(server, "stratumIdentificationPackets");
		return packets.GetValue(player.Player.HasPrivilege("controlserver") ? 1 : 0)!;
	}

	private static object WorldMetaDataPacket(object server)
	{
		return Field(server, "stratumWorldMetaDataPacket");
	}

	private static object Field(object server, string name)
	{
		FieldInfo field = server.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException(name + " was not on the server");
		return field.GetValue(server)!;
	}

	private static bool Contains(object boxedPacket, byte[] needle)
	{
		Type type = boxedPacket.GetType();
		byte[] buffer = (byte[])type.GetField("buffer")!.GetValue(boxedPacket)!;
		int length = (int)type.GetField("Length")!.GetValue(boxedPacket)!;
		Assert.True(length > 0, "the join packet cache was empty, so the cached path did not run");
		return buffer.AsSpan(0, length).IndexOf(needle) >= 0;
	}
}
