using System.Reflection;
using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// OcclusionTraceEnabled off must not raytrace a playing client. The on twin records
/// traces for the same join, so a zero here is the switch and not an idle server.
/// </summary>
[AtlasDataFiles("fixtures/stratum-selection-off", TargetPath = "")]
public class EntitySelectionDisabledScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task PlayingClient_Should_NotTrace_When_OcclusionIsOff()
	{
		await World.JoinPlayer("select-off");
		long before = EntitySelectionTraceScenarios.SelectionTraces(World.Api.World);
		await World.Ticks(10);
		Assert.Equal(before, EntitySelectionTraceScenarios.SelectionTraces(World.Api.World));
	}

	[AtlasScenario(TimeoutMs = 60_000)]
	public void SkipTicks_Should_GrowWithPlayerCount_And_Clamp()
	{
		Type configType = Type.GetType("Vintagestory.Server.StratumEntitySelectionConfig, VintagestoryLib")
			?? throw new InvalidOperationException("StratumEntitySelectionConfig was not in the loaded lib");
		object config = Activator.CreateInstance(configType)!;
		MethodInfo skip = configType.GetMethod("SkipTicks")
			?? throw new InvalidOperationException("SkipTicks was not on the selection config");
		Assert.Equal(3, (int)skip.Invoke(config, new object[] { 50 })!);
		Assert.Equal(5, (int)skip.Invoke(config, new object[] { 250 })!);
		Assert.Equal(20, (int)skip.Invoke(config, new object[] { 2185 })!);
	}
}
