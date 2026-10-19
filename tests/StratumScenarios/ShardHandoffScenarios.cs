using System.Collections;
using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// A server that owns a rectangle redirects a player who walks out of it.
/// </summary>
public class ShardHandoffScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task PlayerOutsideTheRectangle_Should_BeMarkedForTransfer()
	{
		ITestPlayer player = await World.JoinPlayer("shard-edge");
		await player.TeleportTo(World.Spawn);
		await World.Ticks(2);
		object shard = ShardConfig();
		PropertyInfo enabled = shard.GetType().GetProperty("Enabled")!;
		PropertyInfo minX = shard.GetType().GetProperty("MinX")!;
		PropertyInfo maxX = shard.GetType().GetProperty("MaxX")!;
		PropertyInfo minZ = shard.GetType().GetProperty("MinZ")!;
		PropertyInfo maxZ = shard.GetType().GetProperty("MaxZ")!;
		bool previous = (bool)enabled.GetValue(shard)!;
		int previousMinX = (int)minX.GetValue(shard)!;
		int previousMaxX = (int)maxX.GetValue(shard)!;
		int previousMinZ = (int)minZ.GetValue(shard)!;
		int previousMaxZ = (int)maxZ.GetValue(shard)!;
		ClearHandoffs();
		try
		{
			int x = player.Position.X;
			int z = player.Position.Z;
			minX.SetValue(shard, x + 50);
			maxX.SetValue(shard, x + 80);
			minZ.SetValue(shard, z + 50);
			maxZ.SetValue(shard, z + 80);
			enabled.SetValue(shard, true);
			await World.Ticks(5);
			IList handoffs = Handoffs();
			Assert.Contains(player.Player.PlayerUID, string.Join(" ", handoffs.Cast<string>()));
		}
		finally
		{
			enabled.SetValue(shard, previous);
			minX.SetValue(shard, previousMinX);
			maxX.SetValue(shard, previousMaxX);
			minZ.SetValue(shard, previousMinZ);
			maxZ.SetValue(shard, previousMaxZ);
			ClearHandoffs();
		}
	}

	private static object ShardConfig()
	{
		Type runtime = Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib")!;
		object config = runtime.GetProperty("Config")!.GetValue(null)!;
		return config.GetType().GetProperty("Shard")!.GetValue(config)!;
	}

	private static IList Handoffs()
	{
		Type shard = Type.GetType("Vintagestory.Server.StratumShard, VintagestoryLib")!;
		return (IList)shard.GetField("Handoffs", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
	}

	private static void ClearHandoffs()
	{
		Handoffs().Clear();
		Type shard = Type.GetType("Vintagestory.Server.StratumShard, VintagestoryLib")!;
		object handed = shard.GetField("HandedOff", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
		handed.GetType().GetMethod("Clear")!.Invoke(handed, null);
	}
}
