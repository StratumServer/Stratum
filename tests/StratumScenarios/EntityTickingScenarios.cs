using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Pins down distance-banded entity ticking (Performance.EntityTicking, enabled by
/// default) with EXACT counts against Atlas's EntitySimulationTicks, the engine's own
/// entity-simulation tick counter. Per the Atlas tick contract, an entity that stays
/// unthrottled for the whole window ticks exactly once per entity-simulation tick, so:
/// - the near dummy (8 blocks from the anchor, well inside the 32-block near band with
///   drift margin) must tick EXACTLY simDelta times;
/// - the far dummy (200 blocks, beyond every band) must tick simDelta /
///   VeryFarTickInterval, that is 1 in 10, within stride phase.
///
/// Anchoring rules, all of them earned: the player is teleported to a fixed position
/// first (the join scatters players around 15 blocks, which once put this very probe's
/// near dummy in the mid band on some runs), geometry derives from the anchor's actual
/// position read after the teleport settles, and the end of the window re-checks the
/// anchor-dummy distance as setup-failure semantics so a drift can never read as a wrong
/// count.
/// </summary>
public class EntityTickingScenarios : AtlasScenarioBase
{
	private const int MeasurementTicks = 150;
	private const int VeryFarInterval = 10;

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task FarEntity_Should_BeThrottled_When_DefaultsActive()
	{
		ProbePair pair = await SpawnProbePair(World);

		int nearBefore = pair.Near.Ticks;
		int farBefore = pair.Far.Ticks;
		long simBefore = World.EntitySimulationTicks;
		await World.Ticks(MeasurementTicks);
		long simDelta = World.EntitySimulationTicks - simBefore;
		int nearDelta = pair.Near.Ticks - nearBefore;
		int farDelta = pair.Far.Ticks - farBefore;

		AssertAnchorStillNear(pair);
		Assert.True(simDelta > 0, "no entity-simulation ticks elapsed");

		// An unthrottled entity ticks once per entity-simulation tick.
		Assert.True(
			nearDelta == simDelta,
			$"near probe not exact: {nearDelta} ticks vs {simDelta} sim ticks");

		// Throttled at VeryFarTickInterval: exact up to stride phase at the window edges.
		long expected = simDelta / VeryFarInterval;
		Assert.True(
			Math.Abs(farDelta - expected) <= 2,
			$"far probe off the 1-in-{VeryFarInterval} stride: "
			+ $"{farDelta} ticks vs {expected} expected over {simDelta} sim ticks");
	}

	internal sealed record ProbePair(
		ITestPlayer Anchor, BlockPos NearPos, TickCounterBehavior Near, TickCounterBehavior Far);

	/// <summary>
	/// Shared setup: a Playing anchor player teleported to a fixed position, one counted
	/// stationary dummy 8 blocks from it (near band with drift margin), one 200 blocks out
	/// in a kept-loaded column, both settled to rest before measuring (moving entities are
	/// exempt from throttling).
	/// </summary>
	internal static async Task<ProbePair> SpawnProbePair(IWorldSession world)
	{
		ITestPlayer anchor = await world.JoinPlayer("tick-anchor");
		await world.Ticks(2);

		// Pin the anchor: the join scatters players around spawn, and every distance band
		// is measured from the nearest Playing client. Read the position only after the
		// teleport settles.
		await anchor.TeleportTo(world.Spawn);
		await world.Ticks(2);
		BlockPos anchorPos = anchor.Position;

		BlockPos nearPos = anchorPos.AddCopy(8, 1, 0);
		BlockPos farPos = anchorPos.AddCopy(200, 1, 0);

		// KeepLoaded: no player is near the far column to keep it alive, and the entity
		// throttle has no force-loaded exemption (unlike the block listener limit), so
		// this cannot bias the measurement.
		world.Api.WorldManager.LoadChunkColumnPriority(
			farPos.X / 32,
			farPos.Z / 32,
			new ChunkLoadOptions { KeepLoaded = true });
		await world.Until(
			() => world.Api.World.BlockAccessor.GetChunkAtBlockPos(farPos) != null,
			timeoutTicks: 600);

		TickCounterBehavior near = SpawnCountedDummy(world, nearPos);
		TickCounterBehavior far = SpawnCountedDummy(world, farPos);

		await world.Ticks(60);
		return new ProbePair(anchor, nearPos, near, far);
	}

	/// <summary>
	/// End-of-window guard, setup-failure semantics: if the anchor drifted toward the band
	/// boundary (entity spawns can nudge players), the run is invalid rather than silently
	/// miscounted.
	/// </summary>
	internal static void AssertAnchorStillNear(ProbePair pair)
	{
		BlockPos p = pair.Anchor.Position;
		double dx = p.X - pair.NearPos.X;
		double dz = p.Z - pair.NearPos.Z;
		double distance = Math.Sqrt((dx * dx) + (dz * dz));
		Assert.True(
			distance < 32,
			$"anchor drifted to {distance:F1} blocks from the near dummy (band boundary is 32); setup is invalid");
	}

	private static TickCounterBehavior SpawnCountedDummy(IWorldSession world, BlockPos pos)
	{
		// A straw dummy stands perfectly still. That matters: moving entities are exempt
		// from throttling (SkipMovingEntities, threshold 0.01 blocks/tick), and even a
		// dropped item keeps enough residual motion to stay exempt forever.
		Entity dummy = world.SpawnEntity("game:strawdummy", pos);

		var counter = new TickCounterBehavior(dummy);
		dummy.SidedProperties.Behaviors.Add(counter);
		return counter;
	}
}
