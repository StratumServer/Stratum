using System.Reflection;
using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// The selection skip grows with the playing-client count and clamps.
/// </summary>
public class EntitySelectionDisabledScenarios : AtlasScenarioBase
{
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
