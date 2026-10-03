using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// BlockTicks.Enabled off must not turn a dirty backlog into a single-tick drain.
/// The 512 cap still applies. Modified queues still drain in that same pass.
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

		(int dirtyAfter, int modifiedAfter, string report) =
			await DirtyBlockBudgetScenarios.Measure(World, player.Position);

		Assert.Equal(88, dirtyAfter);
		Assert.True(modifiedAfter < 88, $"modified queue still held {modifiedAfter} after a full drain");
		Assert.Contains("Budgets: off", report, StringComparison.Ordinal);
	}
}
