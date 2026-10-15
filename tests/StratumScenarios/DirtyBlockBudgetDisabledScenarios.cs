using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// BlockTicks.Enabled off does not remove the dirty-position cap.
/// One pass still publishes only that many distinct positions. Repeats do not
/// spend a slot, so a queue of repeats can finish in the same pass. Modified
/// queues still drain fully.
/// </summary>
[AtlasDataFiles("fixtures/stratum-blocktick-budget-off", TargetPath = "")]
public class DirtyBlockBudgetDisabledScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task DisabledBlockTicks_Should_StillCapDirtyAndDrainModified()
	{
		var player = await World.JoinPlayer("dirty-cap-off");
		await player.TeleportTo(World.Spawn);
		await World.Ticks(5);

		(int dirtyAfter, int dirtySecond, int modifiedAfter, int noRelightAfter, string report) =
			await DirtyBlockBudgetScenarios.Measure(World, player.Position);

		Assert.Equal(88, dirtyAfter);
		Assert.Equal(0, dirtySecond);
		Assert.Equal(0, modifiedAfter);
		Assert.Equal(0, noRelightAfter);
		Assert.Contains("Budgets: off", report, StringComparison.Ordinal);
		Assert.Contains("modified=600->0", report, StringComparison.Ordinal);
		Assert.Contains("noRelight=600->0", report, StringComparison.Ordinal);
	}
}
