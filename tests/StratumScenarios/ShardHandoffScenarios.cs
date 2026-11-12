using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// A server that owns a rectangle gates entity ticking and column loads at the boundary.
/// </summary>
public class ShardHandoffScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task EntityOutsideTheRectangle_Should_NotTick_And_ShouldDespawnWhenDue()
	{
		ITestPlayer player = await World.JoinPlayer("shard-edge");
		try
		{
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

			try
			{
				int px = player.Position.X;
				int pz = player.Position.Z;
				// Put the shard rectangle 50 blocks away from player.
				minX.SetValue(shard, px + 50);
				maxX.SetValue(shard, px + 100);
				minZ.SetValue(shard, pz + 50);
				maxZ.SetValue(shard, pz + 100);
				enabled.SetValue(shard, true);

				// Spawn a dummy right next to player (outside the shard rectangle).
				Entity dummy = World.SpawnEntity("game:strawdummy", player.Position.AddCopy(1, 0, 1));
				var ai = new TickCounterBehavior(dummy);
				dummy.SidedProperties.Behaviors.Add(ai);

				await World.Ticks(20);
				int ticksWhileOutside = ai.Ticks;
				Assert.True(ticksWhileOutside == 0, $"entity outside the shard was ticked: {ticksWhileOutside} ticks");

				// Entity outside must still be despawned when ShouldDespawn is true.
				dummy.Die(EnumDespawnReason.Death);
				await World.Ticks(10);
				Assert.True(dummy.State == EnumEntityState.Despawned || World.Api.World.GetEntityById(dummy.EntityId) == null,
					"entity outside the shard failed to despawn when ShouldDespawn was true");
			}
			finally
			{
				enabled.SetValue(shard, previous);
				minX.SetValue(shard, previousMinX);
				maxX.SetValue(shard, previousMaxX);
				minZ.SetValue(shard, previousMinZ);
				maxZ.SetValue(shard, previousMaxZ);
			}
		}
		finally
		{
			player.Player.Disconnect();
		}
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public void AllowsColumn_Should_AllowOverlappingAndBorderChunks_And_RejectDistantChunks()
	{
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

		try
		{
			// 1. Open bounds (default int.MaxValue) must not overflow and should allow all chunks.
			minX.SetValue(shard, 0);
			maxX.SetValue(shard, int.MaxValue);
			minZ.SetValue(shard, 0);
			maxZ.SetValue(shard, int.MaxValue);
			enabled.SetValue(shard, true);

			Assert.True(AllowsColumn(0, 0), "chunk (0,0) rejected with open bounds");
			Assert.True(AllowsColumn(1000, 1000), "chunk (1000,1000) rejected with open bounds");

			// 2. Closed bounds: rectangle from [64..128) -> chunks 2 and 3.
			minX.SetValue(shard, 64);
			maxX.SetValue(shard, 127);
			minZ.SetValue(shard, 64);
			maxZ.SetValue(shard, 127);

			// Chunk 2 (64..95) and chunk 3 (96..127) are inside:
			Assert.True(AllowsColumn(2, 2), "chunk (2,2) inside shard was rejected");
			Assert.True(AllowsColumn(3, 3), "chunk (3,3) inside shard was rejected");

			// 1-chunk border buffer: chunks 1 (32..63) and 4 (128..159) are adjacent and must be allowed:
			Assert.True(AllowsColumn(1, 2), "adjacent chunk (1,2) was rejected");
			Assert.True(AllowsColumn(4, 3), "adjacent chunk (4,3) was rejected");

			// 2+ chunks outside must be rejected:
			Assert.False(AllowsColumn(0, 2), "distant chunk (0,2) was not rejected");
			Assert.False(AllowsColumn(5, 3), "distant chunk (5,3) was not rejected");
		}
		finally
		{
			enabled.SetValue(shard, previous);
			minX.SetValue(shard, previousMinX);
			maxX.SetValue(shard, previousMaxX);
			minZ.SetValue(shard, previousMinZ);
			maxZ.SetValue(shard, previousMaxZ);
		}
	}

	private static object ShardConfig()
	{
		Type runtime = Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib")!;
		object config = runtime.GetProperty("Config")!.GetValue(null)!;
		return config.GetType().GetProperty("Shard")!.GetValue(config)!;
	}

	private static bool AllowsColumn(int chunkX, int chunkZ)
	{
		Type shardType = Type.GetType("Vintagestory.Server.StratumShard, VintagestoryLib")!;
		MethodInfo allowsColumn = shardType.GetMethod("AllowsColumn", BindingFlags.Public | BindingFlags.Static)!;
		return (bool)allowsColumn.Invoke(null, new object[] { chunkX, chunkZ })!;
	}
}
