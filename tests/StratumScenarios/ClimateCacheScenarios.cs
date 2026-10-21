using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Public climate queries each return a new object. The worldgen cache stays
/// on the spawner's own call, so a kept result is not rewritten by the next one.
/// </summary>
[AtlasWorld(WorldType = "standard", Seed = 7351)]
public class ClimateCacheScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task PublicClimate_Should_NotShareObjects()
	{
		await World.Ticks(1);
		CommandResult time = await World.ExecuteCommand("/time set 16");
		Assert.True(time.Ok, time.Message);
		await World.Until(() => Hour() > 15.5 && Hour() < 16.5, timeoutTicks: 40);

		BlockPos wet = await WetColumn();
		int y = Math.Min(245, World.Api.WorldManager.MapSizeY - 2);
		BlockPos high = new BlockPos(wet.X, y, wet.Z);
		EnumGetClimateMode rainfall = EnumGetClimateMode.ForSuppliedDate_TemperatureRainfallOnly;
		double atSixteen = Math.Floor(World.Calendar.TotalDays) + (16.0 / 24.0);
		ClimateCondition first = Climate(high, rainfall, atSixteen);
		ClimateCondition second = Climate(high, rainfall, atSixteen);
		ClimateCondition third = Climate(high, rainfall, atSixteen);
		Assert.True(first.WorldgenRainfall > 0.3f);
		Assert.NotSame(first, second);
		Assert.NotSame(second, third);
		float rainfallValue = first.Rainfall;
		second.Rainfall = rainfallValue + 5f;
		Assert.Equal(rainfallValue, first.Rainfall);
		Assert.Equal(rainfallValue, third.Rainfall);

		BlockPos far = await DifferentTemperature(wet);
		ClimateCondition nearNow = Climate(wet, EnumGetClimateMode.NowValues, 0);
		ClimateCondition farNow = Climate(far, EnumGetClimateMode.NowValues, 0);
		Assert.NotEqual(nearNow.WorldGenTemperature, farNow.WorldGenTemperature);

		ClimateCondition baseClimate = new ClimateCondition
		{
			WorldGenTemperature = nearNow.WorldGenTemperature,
			WorldgenRainfall = nearNow.WorldgenRainfall,
			Temperature = 12345f
		};
		ClimateCondition fromBase = World.Api.World.BlockAccessor.GetClimateAt(wet, baseClimate, EnumGetClimateMode.NowValues, World.Calendar.TotalDays);
		Assert.Same(baseClimate, fromBase);
		Assert.Equal(nearNow.Temperature, fromBase.Temperature);
		Assert.NotEqual(farNow.Temperature, fromBase.Temperature);

		float kept = nearNow.Temperature;
		World.Api.World.BlockAccessor.GetClimateAt(far, (200 << 16) | (200 << 8));
		ClimateCondition after = Climate(far, EnumGetClimateMode.NowValues, 0);
		Assert.NotSame(nearNow, after);
		Assert.Equal(kept, nearNow.Temperature);
		Assert.NotEqual(kept, after.Temperature);
	}

	private async Task<BlockPos> WetColumn()
	{
		int y = World.Api.World.SeaLevel;
		for (int chunk = 0; chunk < 8; chunk++)
		{
			int x = World.Spawn.X + chunk * 512;
			int z = World.Spawn.Z;
			if (x < 0 || x >= World.Api.WorldManager.MapSizeX) break;
			BlockPos pos = new BlockPos(x, y, z);
			await Load(pos);
			ClimateCondition climate = Climate(pos, EnumGetClimateMode.WorldGenValues, 0);
			if (climate != null && climate.WorldgenRainfall > 0.3f)
			{
				return pos;
			}
		}

		throw new InvalidOperationException("no column with worldgen rainfall above 0.3");
	}

	private async Task<BlockPos> DifferentTemperature(BlockPos near)
	{
		ClimateCondition origin = Climate(near, EnumGetClimateMode.WorldGenValues, 0);
		for (int step = 1; step <= 6; step++)
		{
			int x = near.X + step * 1024 * 32;
			if (x < 0 || x >= World.Api.WorldManager.MapSizeX) break;
			BlockPos far = new BlockPos(x, near.Y, near.Z);
			await Load(far);
			ClimateCondition climate = Climate(far, EnumGetClimateMode.WorldGenValues, 0);
			if (climate != null && climate.WorldGenTemperature != origin.WorldGenTemperature)
			{
				return far;
			}
		}

		throw new InvalidOperationException("no far column with a different worldgen temperature");
	}

	private async Task Load(BlockPos pos)
	{
		World.Api.WorldManager.LoadChunkColumnPriority(pos.X / 32, pos.Z / 32, new ChunkLoadOptions { KeepLoaded = true });
		await World.Until(() => World.Api.World.BlockAccessor.GetChunkAtBlockPos(pos) != null, timeoutTicks: 1200);
	}

	private ClimateCondition Climate(BlockPos pos, EnumGetClimateMode mode, double totalDays)
	{
		return World.Api.World.BlockAccessor.GetClimateAt(pos, mode, totalDays);
	}

	private double Hour()
	{
		double hours = World.Calendar.TotalHours;
		double day = hours % 24.0;
		return day < 0 ? day + 24.0 : day;
	}
}
