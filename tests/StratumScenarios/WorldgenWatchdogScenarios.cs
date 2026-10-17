using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// The blocking-load watchdog counts finished stages only for columns inside
/// the requested rectangle.
/// </summary>
public class WorldgenWatchdogScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task RectangleProgress_Should_IgnoreColumnsOutsideTheRequest()
	{
		await World.Ticks(1);
		Type requestType = Type.GetType("Vintagestory.Server.ChunkColumnLoadRequest, VintagestoryLib")
			?? throw new InvalidOperationException("ChunkColumnLoadRequest was not loaded");
		Type supplyType = Type.GetType("Vintagestory.Server.ServerSystemSupplyChunks, VintagestoryLib")
			?? throw new InvalidOperationException("ServerSystemSupplyChunks was not loaded");
		MethodInfo progress = supplyType.GetMethod("StratumRectangleStageProgress", BindingFlags.Static | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("StratumRectangleStageProgress was not found");
		FieldInfo finished = requestType.GetField("stratumStagesFinished", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("stratumStagesFinished was not found");

		object inside = Activator.CreateInstance(requestType, 1L, 10, 12, 0, 7, World.Api.World)!;
		object outside = Activator.CreateInstance(requestType, 2L, 80, 80, 0, 7, World.Api.World)!;
		Array columns = Array.CreateInstance(requestType, 3);
		columns.SetValue(inside, 0);
		columns.SetValue(outside, 1);
		Assert.Equal(0, (int)progress.Invoke(null, new object[] { columns, 0, 0, 20, 20 })!);

		finished.SetValue(inside, 2);
		finished.SetValue(outside, 9);
		Assert.Equal(2, (int)progress.Invoke(null, new object[] { columns, 0, 0, 20, 20 })!);

		finished.SetValue(inside, 3);
		Assert.Equal(3, (int)progress.Invoke(null, new object[] { columns, 0, 0, 20, 20 })!);
	}
}
