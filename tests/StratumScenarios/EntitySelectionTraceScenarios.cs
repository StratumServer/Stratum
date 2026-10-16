using System.Reflection;
using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// The occlusion raytrace is what feeds OnBeingLookedAt. With a playing client and the
/// pass left on, a few server ticks have to record at least one trace.
/// </summary>
public class EntitySelectionTraceScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task PlayingClient_Should_Trace_When_OcclusionIsOn()
	{
		await World.JoinPlayer("select-on");
		long before = SelectionTraces(World.Api.World);
		await World.Ticks(10);
		Assert.True(SelectionTraces(World.Api.World) > before, "a playing client produced no occlusion traces");
	}

	internal static long SelectionTraces(object server)
	{
		object runtime = Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib")!
			.GetProperty("PerformanceStats")!.GetValue(null)!;
		FieldInfo field = runtime.GetType().GetField("totalEntitySelectionTraces", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("totalEntitySelectionTraces was not on the stats");
		return (long)field.GetValue(runtime)!;
	}
}
