using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Statistical probes for random tick limiting
/// (Performance.SimulationDistance.LimitRandomTicks, enabled by default, radius 96
/// blocks). The staged probe mod supplies a block that turns to granite on every random
/// tick it receives, so coverage becomes countable world state: a platform of probe
/// blocks near the player converts steadily, and a platform four chunks out (128
/// blocks, past the clamped radius) must stay untouched.
///
/// The effective conversion rate stacks many engine factors (random sampling, per-chunk
/// caps, the fork's per-pass chunk cap with rotation and slice striding, and a
/// Calendar.SpeedOfTime scale), so the positive assertion claims CANDIDACY, not a rate:
/// one single conversion proves the column is inside the sampled radius (floor of 1,
/// long converging ceiling). The far-platform assertion is exact zero, which is
/// rate-insensitive: outside the clamped radius the chunk is never a candidate at all
/// (decompiled: candidacy is a pure grid scan of server-loaded chunks around each
/// Playing client's chunk, plus or minus range in all three axes).
/// </summary>
[AtlasWorld(Mods = new[] { "mods/randomtickprobe" })]
public class RandomTickScenarios : AtlasScenarioBase
{
	// Platforms are 16x16x4 slabs (1024 blocks in a single column) so that even heavily
	// throttled sampling produces a first conversion well within the ceiling. The floor
	// is deliberately "more than zero": rate-based floors proved flaky on slow CI
	// runners because the engine's stacked rate factors can slow sampling several-fold.
	private const int ConversionFloor = 0;
	private const int ConvergenceTimeoutTicks = 2400;
	private const int PlatformEdge = 16;
	private const int PlatformLayers = 4;

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task FarPlatform_Should_StayUntouched_When_DefaultsActive()
	{
		(List<BlockPos> near, List<BlockPos> far) = await PlacePlatforms(World, "rt-anchor");

		await WaitForConversions(World, near, "near");

		// Random ticking is proven live by the near clock; the far column must now stay
		// untouched over a fixed observation window.
		await World.Ticks(450);
		AssertColumnStillLoaded(World, far[0]);
		int farConverted = CountConverted(World, far);
		Assert.True(
			farConverted == 0,
			$"far platform received random ticks: {farConverted}/{far.Count} converted");
	}

	internal static async Task WaitForConversions(
		IWorldSession world, List<BlockPos> platform, string label)
	{
		try
		{
			await world.Until(
				() => CountConverted(world, platform) > ConversionFloor,
				timeoutTicks: ConvergenceTimeoutTicks);
		}
		catch (Exception)
		{
			AssertColumnStillLoaded(world, platform[0]);
			int converted = CountConverted(world, platform);
			Assert.Fail(
				$"{label} platform never reached {ConversionFloor + 1} conversions "
				+ $"within {ConvergenceTimeoutTicks} ticks ({converted}/{platform.Count} converted)");
		}
	}

	internal static async Task<(List<BlockPos> Near, List<BlockPos> Far)> PlacePlatforms(
		IWorldSession world, string anchorName)
	{
		// The anchor player centers the 96-block random tick radius on spawn. JoinPlayer
		// itself waits for the server assets packet build to complete, so the mass
		// SetBlock below does not race the build's off-thread item enumeration.
		ITestPlayer anchor = await world.JoinPlayer(anchorName);
		anchor.Player.WorldData.DesiredViewDistance = 256;

		// The join scatters players around world spawn (SpawnPlayerRandomlyAround,
		// roughly 15 blocks), which can shift the anchor's CHUNK by one in any
		// direction: geometry derived from World.Spawn therefore varied by a full chunk
		// per run, and every random tick flake of this pack traced back to it (a
		// "distance 4" column that was really at 3 became a candidate, one at 5 starved
		// the wait). Pin the anchor to a fixed position, then derive everything from its
		// ACTUAL chunk.
		await anchor.TeleportTo(world.Spawn);
		await world.Ticks(5);

		BlockPos anchorPos = anchor.Position;
		int anchorChunkX = anchorPos.X / 32;
		int anchorChunkZ = anchorPos.Z / 32;

		// Vanilla gates random ticks itself: only chunks within BlockTickChunkRange
		// (5 chunks by default) of a Playing client are sampled at all (decompiled:
		// pure grid scan of server-loaded chunks, plus or minus range on all three
		// axes). LimitRandomTicks merely clamps that range down to
		// RandomTickDistanceBlocks (96 blocks = 3 chunks by default). The far platform
		// therefore sits in the column at chunk distance EXACTLY 4 from the anchor's own
		// chunk: inside the vanilla range, outside the clamp. Both platforms are padded
		// 8 blocks into their column so all 16x16 positions stay in a single chunk
		// column.
		BlockPos nearCorner = new BlockPos(
			(anchorChunkX * 32) + 8, anchorPos.Y + 1, (anchorChunkZ * 32) + 8, 0);
		int farChunkX = anchorChunkX + 4;
		BlockPos farCorner = new BlockPos(
			(farChunkX * 32) + 8, anchorPos.Y + 1, (anchorChunkZ * 32) + 8, 0);

		// KeepLoaded: the observation windows outlive the unload timer for a column
		// with no player nearby. Safe for the measurement: the random tick limit has no
		// force-loaded exemption (unlike the block listener limit), it gates purely on
		// grid distance to Playing clients.
		world.Api.WorldManager.LoadChunkColumnPriority(
			farChunkX,
			anchorChunkZ,
			new ChunkLoadOptions { KeepLoaded = true });
		await world.Until(
			() => world.Api.World.BlockAccessor.GetChunkAtBlockPos(farCorner) != null,
			timeoutTicks: 600);

		return (PlacePlatform(world, nearCorner), PlacePlatform(world, farCorner));
	}

	private static int CountConverted(IWorldSession world, List<BlockPos> platform)
	{
		int converted = 0;
		foreach (BlockPos pos in platform)
		{
			if (world.BlockAt(pos).Code.ToString() == "game:rock-granite")
			{
				converted++;
			}
		}

		return converted;
	}

	private static void AssertColumnStillLoaded(IWorldSession world, BlockPos pos)
	{
		Assert.True(
			world.Api.World.BlockAccessor.GetChunkAtBlockPos(pos) != null,
			"far column unloaded mid-measurement; the probe window is too long for the unload timer");
	}

	private static List<BlockPos> PlacePlatform(IWorldSession world, BlockPos corner)
	{
		var positions = new List<BlockPos>(PlatformEdge * PlatformEdge * PlatformLayers);
		for (int x = 0; x < PlatformEdge; x++)
		{
			for (int z = 0; z < PlatformEdge; z++)
			{
				for (int y = 0; y < PlatformLayers; y++)
				{
					BlockPos pos = corner.AddCopy(x, y, z);
					world.SetBlock("stratumprobe:probe", pos);
					positions.Add(pos);
				}
			}
		}

		return positions;
	}
}
