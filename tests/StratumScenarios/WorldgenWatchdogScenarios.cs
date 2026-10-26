using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// The blocking-load watchdog counts finished stages for the requested
/// rectangle plus the neighbour ring (PreDone - TerrainLate, 4 chunks).
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

		object inside = Column(requestType, 1L, 10, 12);
		object ring = Column(requestType, 2L, 24, 12);
		object beyond = Column(requestType, 3L, 25, 12);
		Array columns = Array.CreateInstance(requestType, 3);
		columns.SetValue(inside, 0);
		columns.SetValue(ring, 1);
		columns.SetValue(beyond, 2);
		Assert.Equal(0, (int)progress.Invoke(null, new object[] { columns, 0, 0, 20, 20 })!);

		finished.SetValue(inside, 2);
		finished.SetValue(ring, 5);
		finished.SetValue(beyond, 9);
		Assert.Equal(7, (int)progress.Invoke(null, new object[] { columns, 0, 0, 20, 20 })!);

		finished.SetValue(inside, 3);
		Assert.Equal(8, (int)progress.Invoke(null, new object[] { columns, 0, 0, 20, 20 })!);

		object lowRing = Column(requestType, 4L, 6, 15);
		object lowBeyond = Column(requestType, 5L, 5, 15);
		object zLowRing = Column(requestType, 6L, 15, 6);
		object zLowBeyond = Column(requestType, 7L, 15, 5);
		object zHighRing = Column(requestType, 8L, 15, 24);
		object zHighBeyond = Column(requestType, 9L, 15, 25);
		Array offset = Array.CreateInstance(requestType, 6);
		offset.SetValue(lowRing, 0);
		offset.SetValue(lowBeyond, 1);
		offset.SetValue(zLowRing, 2);
		offset.SetValue(zLowBeyond, 3);
		offset.SetValue(zHighRing, 4);
		offset.SetValue(zHighBeyond, 5);
		finished.SetValue(lowRing, 4);
		finished.SetValue(lowBeyond, 8);
		finished.SetValue(zLowRing, 3);
		finished.SetValue(zLowBeyond, 6);
		finished.SetValue(zHighRing, 5);
		finished.SetValue(zHighBeyond, 7);
		Assert.Equal(12, (int)progress.Invoke(null, new object[] { offset, 10, 10, 20, 20 })!);

		object nsX = Column(requestType, 10L, 6, 18);
		object nsXBeyond = Column(requestType, 11L, 5, 18);
		object nsZ = Column(requestType, 12L, 15, 29);
		object nsZBeyond = Column(requestType, 13L, 15, 30);
		Array nonSquare = Array.CreateInstance(requestType, 4);
		nonSquare.SetValue(nsX, 0);
		nonSquare.SetValue(nsXBeyond, 1);
		nonSquare.SetValue(nsZ, 2);
		nonSquare.SetValue(nsZBeyond, 3);
		finished.SetValue(nsX, 2);
		finished.SetValue(nsXBeyond, 5);
		finished.SetValue(nsZ, 3);
		finished.SetValue(nsZBeyond, 7);
		Assert.Equal(5, (int)progress.Invoke(null, new object[] { nonSquare, 10, 12, 20, 25 })!);
	}

	private object Column(Type requestType, long index, int x, int z)
	{
		return Activator.CreateInstance(requestType, index, x, z, 0, 7, World.Api.World)!;
	}
}
