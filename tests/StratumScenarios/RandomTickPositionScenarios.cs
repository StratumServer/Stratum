using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Pins down who owns the position object a random tick hands to a block. Vanilla queues
/// a private copy per sampled tick, so a handler may keep the reference past its return
/// (a delayed callback, a list, a dictionary key) and read it later. Stratum once queued
/// positions from a pool that the off-thread pass recycled as soon as the main thread
/// had drained the queue: a kept reference then pointed at whatever block was sampled
/// next. The staged probe block keeps every position it receives and audits them on
/// later ticks, so the outcome is countable world state: andesite for a handler that ran,
/// granite for a handler whose kept position was rewritten afterwards.
///
/// The check needs no timing at all: the engine recycling an object is deterministic once
/// two passes have run, which is why this runs a fixed number of handlers and then asserts
/// an exact zero instead of waiting for a race.
/// </summary>
[AtlasWorld(Mods = new[] { "mods/randomtickprobe" })]
public class RandomTickPositionScenarios : AtlasScenarioBase
{
	// A whole chunk footprint, 12 layers: about a third of the chunk is probe blocks, so a
	// sampled tick lands on one often enough to reach MinHandlerRuns in well under a minute.
	private const int SlabEdge = 32;
	private const int SlabLayers = 12;
	// Kept low on purpose: the assertion is an exact zero, so this only has to prove that
	// handlers ran, and rate-based floors proved flaky on slow CI runners (see RandomTickScenarios).
	private const int MinHandlerRuns = 20;
	private const int ConvergenceTimeoutTicks = 2400;

	// Extra ticks once the handlers ran, so several more passes get the chance to recycle
	// whatever they can and a later handler to notice.
	private const int SettleTicks = 150;

	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task PositionKeptByHandler_Should_NeverBeRewritten_ByTheEngine()
	{
		ITestPlayer anchor = await World.JoinPlayer("rp-anchor");
		anchor.Player.WorldData.DesiredViewDistance = 256;
		await anchor.TeleportTo(World.Spawn);
		await World.Ticks(5);

		BlockPos anchorPos = anchor.Position;
		BlockPos corner = new BlockPos(
			(anchorPos.X / 32) * 32, anchorPos.Y + 4, (anchorPos.Z / 32) * 32, 0);
		Assert.True(
			World.Api.World.BlockAccessor.GetChunkAtBlockPos(corner.AddCopy(0, SlabLayers - 1, 0)) != null,
			"slab chunk is not loaded; setup is broken");

		var slab = new List<BlockPos>(SlabEdge * SlabEdge * SlabLayers);
		for (int x = 0; x < SlabEdge; x++)
		{
			for (int z = 0; z < SlabEdge; z++)
			{
				for (int y = 0; y < SlabLayers; y++)
				{
					BlockPos pos = corner.AddCopy(x, y, z);
					World.SetBlock("stratumprobe:positionprobe", pos);
					slab.Add(pos);
				}
			}
		}

		int andesite = World.Api.World.GetBlock(new AssetLocation("game:rock-andesite"))!.BlockId;
		int granite = World.Api.World.GetBlock(new AssetLocation("game:rock-granite"))!.BlockId;

		try
		{
			await World.Until(
				() => Count(slab, andesite) + Count(slab, granite) >= MinHandlerRuns,
				timeoutTicks: ConvergenceTimeoutTicks);
		}
		catch (Exception)
		{
			Assert.Fail(
				$"only {Count(slab, andesite) + Count(slab, granite)} of {MinHandlerRuns} handler runs "
				+ $"within {ConvergenceTimeoutTicks} ticks; the probe never got going");
		}

		await World.Ticks(SettleTicks);

		int rewritten = Count(slab, granite);
		int ran = Count(slab, andesite) + rewritten;
		Assert.True(
			rewritten == 0,
			$"the engine rewrote the position object a handler kept after returning, "
			+ $"{rewritten} of {ran} handler runs (the queue must hold a private copy per tick)");
	}

	private int Count(List<BlockPos> slab, int blockId)
	{
		int count = 0;
		foreach (BlockPos pos in slab)
		{
			if (World.BlockAt(pos).BlockId == blockId)
			{
				count++;
			}
		}

		return count;
	}
}
