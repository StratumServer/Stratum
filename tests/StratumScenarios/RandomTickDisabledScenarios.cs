using Atlas.XUnit;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Same probe as <see cref="RandomTickScenarios"/>, but the seeded
/// stratum-performance.json turns SimulationDistance.LimitRandomTicks off before boot.
/// Far chunks must then random-tick again, which is what proves the toggle restores
/// vanilla behavior. As with every config fixture here, stratum.json is seeded at the
/// current config version so the boot loads the sidecar without running a migration
/// (see <see cref="EntityTickingDisabledScenarios"/>).
/// </summary>
[AtlasWorld(Mods = new[] { "mods/randomtickprobe" })]
[AtlasDataFiles("fixtures/stratum-randomticks-off", TargetPath = "")]
public class RandomTickDisabledScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task FarPlatform_Should_Convert_When_LimitDisabledByConfig()
	{
		(List<BlockPos> near, List<BlockPos> far) =
			await RandomTickScenarios.PlacePlatforms(World, "rt-anchor2");

		// Both platforms must convert; converging waits absorb the engine's asynchronous
		// chunk bookkeeping (see RandomTickScenarios).
		await RandomTickScenarios.WaitForConversions(World, near, "near");
		await RandomTickScenarios.WaitForConversions(World, far, "far");
	}
}
