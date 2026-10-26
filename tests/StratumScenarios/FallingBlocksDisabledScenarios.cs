using Atlas.XUnit;
using System.Reflection;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Proves that when FallingBlocks.Enabled is false in stratum-performance.json,
/// falling blocks take the vanilla EntityBlockFalling path even far from players,
/// restoring vanilla behavior.
/// </summary>
[AtlasDataFiles("fixtures/stratum-fallingblocks-off", TargetPath = "")]
public class FallingBlocksDisabledScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task WhenDisabled_Falls_Should_UseVanillaEntityPath()
	{
		await World.JoinPlayer("fb-disabled");
		await World.Ticks(2);

		int x = ((World.Spawn.X + 300) / 32) * 32 + 16;
		int z = ((World.Spawn.Z) / 32) * 32 + 16 + 50;

		World.Api.WorldManager.LoadChunkColumnPriority(x / 32, z / 32, new ChunkLoadOptions { KeepLoaded = true });
		await World.Until(() => World.Api.World.BlockAccessor.GetChunk(x / 32, 0, z / 32) != null, timeoutTicks: 600);

		int floorY = World.Api.World.BlockAccessor.GetTerrainMapheightAt(new BlockPos(x, 1, z, 0));
		BlockPos landing = new BlockPos(x, floorY + 1, z, 0);
		BlockPos support = landing.UpCopy(2);
		BlockPos sandPos = support.UpCopy(1);

		World.SetBlock("game:rock-granite", landing.DownCopy(1));
		World.SetBlock("game:air", landing);
		World.SetBlock("game:air", landing.UpCopy(1));
		World.SetBlock("game:rock-granite", support);
		World.SetBlock("game:sand-granite", sandPos);
		await World.Ticks(5);

		bool sawEntity = false;
		EntityDelegate spawnListener = (Entity e) =>
		{
			if (e.GetType().Name == "EntityBlockFalling")
			{
				BlockPos? p = e.GetType().GetField("initialPos")?.GetValue(e) as BlockPos;
				if (p != null && p.Equals(sandPos)) sawEntity = true;
			}
		};
		World.Api.Event.OnEntitySpawn += spawnListener;

		try
		{
			World.SetBlock("game:air", support);
			World.Api.World.BlockAccessor.TriggerNeighbourBlockUpdate(support);

			await World.Until(() => sawEntity, timeoutTicks: 300);
			Assert.True(sawEntity, "when disabled, falling block should spawn an EntityBlockFalling even far from players");
		}
		finally
		{
			World.Api.Event.OnEntitySpawn -= spawnListener;
		}
	}
}
