using System;
using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common.Entities;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Turning the body-temperature throttle off restores vanilla's update intervals
/// and does not apply a start offset.
/// </summary>
public class BodyTemperatureScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task DisabledThrottle_Should_UseVanillaThresholds()
	{
		object config = BodyTemperatureConfig();
		PropertyInfo enabled = config.GetType().GetProperty("Enabled")
			?? throw new InvalidOperationException("BodyTemperature.Enabled was missing");
		bool previous = (bool)enabled.GetValue(config)!;
		enabled.SetValue(config, false);
		try
		{
			ITestPlayer player = await World.JoinPlayer("temp-off");
			object behavior = null;
			foreach (EntityBehavior candidate in player.Entity.SidedProperties.Behaviors)
			{
				if (candidate.GetType().Name == "EntityBehaviorBodyTemperature")
				{
					behavior = candidate;
					break;
				}
			}
			Assert.NotNull(behavior);
			Assert.Equal(1f, Field(behavior, "stratumUpdateThreshold"));
			Assert.Equal(3f, Field(behavior, "stratumHeatSourceThreshold"));
			Assert.Equal(0f, Field(behavior, "stratumFreezingAnimThreshold"));
			Assert.Equal(10f, Field(behavior, "stratumDamageCheckThreshold"));
		}
		finally
		{
			enabled.SetValue(config, previous);
		}
	}

	private static float Field(object behavior, string name)
	{
		FieldInfo field = behavior.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException(name + " was missing");
		return (float)field.GetValue(behavior)!;
	}

	private static object BodyTemperatureConfig()
	{
		Type runtime = Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib")
			?? throw new InvalidOperationException("StratumRuntime was not loaded");
		object config = runtime.GetProperty("Config")!.GetValue(null)!;
		object performance = config.GetType().GetProperty("Performance")!.GetValue(config)!;
		return performance.GetType().GetProperty("BodyTemperature")!.GetValue(performance)!;
	}
}
