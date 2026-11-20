using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Same probe as <see cref="EntityTickingScenarios"/>, but the seeded
/// stratum-performance.json turns Performance.EntityTicking off before boot. With the
/// throttle off, BOTH dummies are unthrottled everywhere, so the Atlas tick contract
/// guarantees exact counts: near and far must each tick exactly once per
/// entity-simulation tick. That is what proves the config toggle genuinely restores
/// vanilla behavior instead of merely widening the bands.
///
/// The fixture also seeds a stratum.json at the current config version.
/// StratumRuntime.LoadOrCreateConfig reads stratum-performance.json on its own, but a data
/// dir holding only that sidecar counts as a version 2 install and runs the config
/// migration on every boot. Keep the seeded ConfigVersion equal to
/// StratumConfigMigration.CurrentVersion so the boot is a plain load.
/// </summary>
[AtlasDataFiles("fixtures/stratum-entityticking-off", TargetPath = "")]
public class EntityTickingDisabledScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task FarEntity_Should_TickFullRate_When_EntityTickingDisabledByConfig()
	{
		EntityTickingScenarios.ProbePair pair = await EntityTickingScenarios.SpawnProbePair(World);

		int nearBefore = pair.Near.Ticks;
		int farBefore = pair.Far.Ticks;
		long simBefore = World.EntitySimulationTicks;
		await World.Ticks(150);
		long simDelta = World.EntitySimulationTicks - simBefore;
		int nearDelta = pair.Near.Ticks - nearBefore;
		int farDelta = pair.Far.Ticks - farBefore;

		EntityTickingScenarios.AssertAnchorStillNear(pair);
		Assert.True(simDelta > 0, "no entity-simulation ticks elapsed");

		Assert.True(
			nearDelta == simDelta,
			$"near probe not exact: {nearDelta} ticks vs {simDelta} sim ticks");
		Assert.True(
			farDelta == simDelta,
			"far probe throttled despite EntityTicking.Enabled=false: "
			+ $"{farDelta} ticks vs {simDelta} sim ticks");
	}
}
