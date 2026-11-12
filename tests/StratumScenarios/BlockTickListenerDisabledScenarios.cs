using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Same probe as <see cref="BlockTickListenerScenarios"/>, but the seeded
/// stratum-performance.json turns SimulationDistance.LimitBlockGameTickListeners off
/// before boot. Far listeners must then fire at full rate again, which is what proves
/// the toggle actually restores vanilla behavior. As with every config fixture here,
/// stratum.json is seeded at the current config version so the boot loads the sidecar
/// without running a migration (see <see cref="EntityTickingDisabledScenarios"/>).
/// </summary>
[AtlasDataFiles("fixtures/stratum-blockticks-off", TargetPath = "")]
public class BlockTickListenerDisabledScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task FarListener_Should_FireFullRate_When_LimitDisabledByConfig()
	{
		(int[] near, int[] far) = await BlockTickListenerScenarios.RegisterProbePair(
			World, "bt-anchor3", keepFarLoaded: false, farOffsetX: 200, farOffsetZ: 0);

		int nearBefore = near[0];
		int farBefore = far[0];
		await World.Ticks(120);
		int nearDelta = near[0] - nearBefore;
		int farDelta = far[0] - farBefore;

		Assert.True(
			nearDelta > 30,
			$"near listener barely fired ({nearDelta}/120); setup is broken");

		double ratio = (double)farDelta / nearDelta;
		Assert.True(
			ratio > 0.8,
			"far listener limited despite LimitBlockGameTickListeners=false: "
			+ $"far={farDelta} near={nearDelta} ratio={ratio:F2}");
	}
}
