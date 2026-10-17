using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Nested byType is resolved after the outer byType merge, so the surgeon hood
/// keeps the nested warmth.
/// </summary>
public class ByTypeWarmthScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task SurgeonHood_Should_KeepTheNestedWarmth()
	{
		await World.Ticks(1);
		Item hood = World.Api.World.GetItem(new AssetLocation("game:clothes-face-surgeonhood"));
		Assert.NotNull(hood);
		Assert.Equal(0.25f, hood.Attributes["warmth"].AsFloat(-1f));
	}
}
