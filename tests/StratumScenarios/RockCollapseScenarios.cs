using Atlas.Api;
using Atlas.XUnit;
using System;
using System.Collections;
using System.Reflection;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Cave-ins (BlockBehaviorUnstableRock) with no player in range of the rock.
///
/// collapseLayer ends by asking three random neighbours within 8 blocks whether they collapse as well, and that
/// call chain (CheckCollapsible, collapse, collapseLayer, checkCollapsibleNeighbours) runs on one stack. On the
/// entity path a collapsed rock is removed at once (EntityBlockFalling.Initialize), so a region cannot be found
/// twice and the chain ends. A rock that waits in the instant queue stays in place, so every nested call found
/// the same region again. On a single layer slab of 13 by 13 rocks that took up to 108 nested calls and 2592
/// collapseLayer calls where vanilla needs 6 and 10. The patch ends the chain at a layer whose rocks all wait in
/// the queue already.
///
/// The scenario hangs a thin unconnected rock slab in the air far from the only player, starts the collapse the
/// way an explosion does (CheckCollapsible on rocks), and expects the slab to come down and the instant queue to
/// drain. A stack overflow would end the process, which the test run reports as a crashed host.
/// </summary>
public class RockCollapseScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 240_000)]
	public async Task ThinRockSlab_Should_Collapse_AndDrainTheQueue_When_NoPlayerInRange()
	{
		ICoreServerAPI api = World.Api;
		await World.JoinPlayer("rock-anchor");
		await World.Ticks(2);

		Assert.True(EnableCaveIns(api) > 0, "setup failed: no rock block has the UnstableRock behavior, so cave-ins cannot run here");
		object system = RequireSystem(api);

		int cx = ((World.Spawn.X + 200) / 32) * 32 + 16;
		int cz = (World.Spawn.Z / 32) * 32 + 16;
		for (int a = cx / 32 - 1; a <= cx / 32 + 1; a++)
		{
			for (int b = cz / 32 - 1; b <= cz / 32 + 1; b++)
			{
				api.WorldManager.LoadChunkColumnPriority(a, b, new ChunkLoadOptions { KeepLoaded = true });
			}
		}
		await World.Until(() => api.World.BlockAccessor.GetChunkAtBlockPos(new BlockPos(cx + 14, 1, cz + 14, 0)) != null
			&& api.World.BlockAccessor.GetChunkAtBlockPos(new BlockPos(cx - 14, 1, cz - 14, 0)) != null, timeoutTicks: 900);
		await World.Ticks(30);

		IBlockAccessor ba = api.World.BlockAccessor;
		int floorY = ba.GetTerrainMapheightAt(new BlockPos(cx, 1, cz, 0)) + 30;
		Block rock = api.World.GetBlock(new AssetLocation("game:rock-limestone"))!;
		Assert.True(rock != null, "game:rock-limestone not found");

		// A floor to land on, and a one layer slab hanging 7 blocks above it with nothing under or beside it.
		const int half = 6;
		for (int x = cx - half - 4; x <= cx + half + 4; x++)
		{
			for (int z = cz - half - 4; z <= cz + half + 4; z++)
			{
				ba.SetBlock(rock.Id, new BlockPos(x, floorY, z, 0));
				for (int dy = 1; dy <= 12; dy++) ba.SetBlock(0, new BlockPos(x, floorY + dy, z, 0));
			}
		}
		int placed = 0;
		for (int x = cx - half; x <= cx + half; x++)
		{
			for (int z = cz - half; z <= cz + half; z++)
			{
				ba.SetBlock(rock.Id, new BlockPos(x, floorY + 7, z, 0));
				placed++;
			}
		}
		await World.Ticks(30);

		object? behavior = FindBehavior(ba.GetBlock(new BlockPos(cx, floorY + 7, cz, 0)));
		Assert.True(behavior != null, "the rock slab has no UnstableRock behavior");
		MethodInfo check = behavior!.GetType().GetMethod("CheckCollapsible", new[] { typeof(IWorldAccessor), typeof(BlockPos) })!;

		int started = 0;
		for (int i = -half; i <= half; i += 2)
		{
			for (int j = -half; j <= half; j += 2)
			{
				if ((bool)check.Invoke(behavior, new object[] { api.World, new BlockPos(cx + i, floorY + 7, cz + j, 0) })!) started++;
			}
		}
		Assert.True(started > 0, "setup failed: no collapse started, so nothing was tested");

		await World.Until(() => QueueCount(system) == 0, timeoutTicks: 1800);
		await World.Ticks(60);

		int hanging = 0;
		for (int x = cx - half; x <= cx + half; x++)
			for (int z = cz - half; z <= cz + half; z++)
				if (ba.GetBlock(new BlockPos(x, floorY + 7, z, 0)).Id != 0) hanging++;

		Assert.True(QueueCount(system) == 0, $"the instant queue still holds {QueueCount(system)} requests");
		Assert.True(hanging < placed, $"no rock came down: {hanging} of {placed} still hang ({started} collapses started)");
	}

	private static object RequireSystem(ICoreServerAPI api)
	{
		foreach (ModSystem system in api.ModLoader.Systems)
		{
			if (system.GetType().FullName != "Vintagestory.GameContent.ModSystemFallingBlocks") continue;
			Assert.True(system.GetType().GetProperty("Instance")!.GetValue(null) != null,
				"ModSystemFallingBlocks is disabled, so the instant path does not run");
			return system;
		}
		throw new InvalidOperationException("ModSystemFallingBlocks is not loaded: the server is not the patched one.");
	}

	private static int QueueCount(object system)
	{
		FieldInfo field = system.GetType().GetField("instantQueue", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("ModSystemFallingBlocks.instantQueue is missing: it was renamed or removed.");
		return ((ICollection)field.GetValue(system)!).Count;
	}

	private static object? FindBehavior(Block block)
	{
		foreach (BlockBehavior bh in block.BlockBehaviors)
		{
			if (bh.GetType().Name == "BlockBehaviorUnstableRock") return bh;
		}
		return null;
	}

	// The behaviors read the world config once when they load, and cave-ins are off by default.
	private static int EnableCaveIns(ICoreServerAPI api)
	{
		int count = 0;
		foreach (Block block in api.World.Blocks)
		{
			if (block == null || block.Code == null) continue;
			object? bh = FindBehavior(block);
			if (bh == null) continue;
			Type t = bh.GetType();
			t.GetField("CaveIns")!.SetValue(bh, true);
			t.GetField("AllowFallingBlocks")!.SetValue(bh, true);
			count++;
		}
		return count;
	}
}
