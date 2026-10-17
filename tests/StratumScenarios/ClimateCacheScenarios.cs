using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// GetClimateAt returns a caller-owned object. A later query, including one
/// 1024 chunks away, does not reuse or rewrite that object.
/// </summary>
public class ClimateCacheScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task Climate_Should_StayDistinct_When_TheCallerKeepsTheResult()
	{
		await World.Ticks(1);
		int y = World.Api.World.SeaLevel;
		BlockPos near = new BlockPos(World.Spawn.X, y, World.Spawn.Z);
		ClimateCondition first = World.Api.World.BlockAccessor.GetClimateAt(near, EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, 0);
		Assert.NotNull(first);
		float temperature = first.Temperature;
		first.Temperature = temperature + 40f;

		ClimateCondition second = World.Api.World.BlockAccessor.GetClimateAt(near, EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, 0);
		Assert.NotSame(first, second);
		Assert.Equal(temperature, second.Temperature);

		int farX = near.X + (1024 * 32);
		if (farX > 0 && farX < World.Api.WorldManager.MapSizeX)
		{
			BlockPos far = new BlockPos(farX, y, near.Z);
			ClimateCondition farClimate = World.Api.World.BlockAccessor.GetClimateAt(far, EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, 0);
			Assert.NotNull(farClimate);
			Assert.NotSame(first, farClimate);
			farClimate.Temperature = temperature + 80f;
			ClimateCondition nearAgain = World.Api.World.BlockAccessor.GetClimateAt(near, EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, 0);
			Assert.Equal(temperature, nearAgain.Temperature);
		}
	}
}
