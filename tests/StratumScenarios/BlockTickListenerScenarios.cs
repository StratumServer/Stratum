using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Pins down block game-tick listener limiting
/// (Performance.SimulationDistance.LimitBlockGameTickListeners, enabled by default,
/// radius 128 blocks). Two positioned tick listeners are registered through the public
/// API: one near the anchor player (its column is always active, it doubles as the tick
/// clock) and one 200 blocks out. The far one must freeze entirely, except when its
/// column is force-loaded (TickForceLoadedBlockListeners, also default on), which the
/// second scenario covers.
/// </summary>
public class BlockTickListenerScenarios : AtlasScenarioBase
{
	private const int MeasurementTicks = 120;

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task FarListener_Should_Freeze_When_DefaultsActive()
	{
		(int[] near, int[] far) = await RegisterProbePair(
			World, "bt-anchor", keepFarLoaded: false, farOffsetX: 200, farOffsetZ: 0);

		int nearBefore = near[0];
		int farBefore = far[0];
		await World.Ticks(MeasurementTicks);
		int nearDelta = near[0] - nearBefore;
		int farDelta = far[0] - farBefore;

		Assert.True(
			nearDelta > MeasurementTicks / 4,
			$"near listener barely fired ({nearDelta}/{MeasurementTicks}); setup is broken");

		// Outside the active columns the listener is not throttled but skipped entirely,
		// so the expected ratio is 0; the bound stays loose on purpose.
		double ratio = (double)farDelta / nearDelta;
		Assert.True(
			ratio < 0.2,
			$"far listener not frozen: far={farDelta} near={nearDelta} ratio={ratio:F2}");
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task ForceLoadedFarListener_Should_KeepFiring_When_ColumnIsKeptLoaded()
	{
		// Force-loaded columns are exempt from the limit
		// (TickForceLoadedBlockListeners, default true).
		// Distinct far column from the scenario above: both scenarios share the class
		// host and world, and a KeepLoaded column stays force-loaded for the rest of the
		// class, which would exempt the other probe and void its assertion.
		(int[] near, int[] far) = await RegisterProbePair(
			World, "bt-anchor2", keepFarLoaded: true, farOffsetX: 0, farOffsetZ: 200);

		int nearBefore = near[0];
		int farBefore = far[0];
		await World.Ticks(MeasurementTicks);
		int nearDelta = near[0] - nearBefore;
		int farDelta = far[0] - farBefore;

		Assert.True(
			nearDelta > MeasurementTicks / 4,
			$"near listener barely fired ({nearDelta}/{MeasurementTicks}); setup is broken");

		double ratio = (double)farDelta / nearDelta;
		Assert.True(
			ratio > 0.8,
			$"force-loaded far listener limited: far={farDelta} near={nearDelta} ratio={ratio:F2}");
	}

	/// <summary>
	/// Shared setup: a Playing anchor player, one counted listener next to them, one 200
	/// blocks out, the far column loaded and optionally kept loaded.
	/// Counters are single-cell arrays so the tick lambdas can mutate them.
	/// </summary>
	internal static async Task<(int[] Near, int[] Far)> RegisterProbePair(
		IWorldSession world, string anchorName, bool keepFarLoaded, int farOffsetX, int farOffsetZ)
	{
		// The active-column set is built from IsPlayingClient positions; Atlas joins
		// reach Playing on their own.
		await world.JoinPlayer(anchorName);
		await world.Ticks(5);

		BlockPos nearPos = world.Spawn.AddCopy(5, 1, 0);
		BlockPos farPos = world.Spawn.AddCopy(farOffsetX, 1, farOffsetZ);

		ChunkLoadOptions? options = keepFarLoaded ? new ChunkLoadOptions { KeepLoaded = true } : null;
		world.Api.WorldManager.LoadChunkColumnPriority(farPos.X / 32, farPos.Z / 32, options);
		await world.Until(
			() => world.Api.World.BlockAccessor.GetChunkAtBlockPos(farPos) != null,
			timeoutTicks: 600);

		int[] near = RegisterCountingListener(world, nearPos);
		int[] far = RegisterCountingListener(world, farPos);

		await world.Ticks(10);
		return (near, far);
	}

	private static int[] RegisterCountingListener(IWorldSession world, BlockPos pos)
	{
		int[] counter = new int[1];
		world.Api.Event.RegisterGameTickListener(
			_ => counter[0]++,
			pos,
			errorHandler: null,
			millisecondInterval: 1,
			initialDelayOffsetMs: 0);
		return counter;
	}
}
