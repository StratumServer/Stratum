using System.Reflection;
using Atlas.Api;
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

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task MovingPlayer_Should_WaitForTheSkip_When_TheTickIsOverloaded()
	{
		ITestPlayer player = await World.JoinPlayer("select-overload");
		object selection = SelectionConfig();
		PropertyInfo overload = selection.GetType().GetProperty("OverloadTickMs")
			?? throw new InvalidOperationException("OverloadTickMs was not on the selection config");
		PropertyInfo baseSkip = selection.GetType().GetProperty("BaseSkipTicks")!;
		PropertyInfo maxSkip = selection.GetType().GetProperty("MaxSkipTicks")!;
		int previousOverload = (int)overload.GetValue(selection)!;
		int previousBase = (int)baseSkip.GetValue(selection)!;
		int previousMax = (int)maxSkip.GetValue(selection)!;
		baseSkip.SetValue(selection, 1000);
		maxSkip.SetValue(selection, 1000);
		overload.SetValue(selection, 100_000);
		try
		{
			await World.Ticks(1);
			object serverPos = player.Player.Entity.GetType().GetProperty("ServerPos")!.GetValue(player.Player.Entity)!;
			PropertyInfo x = serverPos.GetType().GetProperty("X")
				?? throw new InvalidOperationException("ServerPos.X was not found");
			long keepingUp = SelectionTraces(World.Api.World);
			x.SetValue(serverPos, (double)x.GetValue(serverPos)! + 4);
			await World.Ticks(1);
			Assert.True(SelectionTraces(World.Api.World) > keepingUp, "a move did not trace while the tick was under the threshold");

			overload.SetValue(selection, 0);
			long overloaded = SelectionTraces(World.Api.World);
			x.SetValue(serverPos, (double)x.GetValue(serverPos)! + 4);
			await World.Ticks(2);
			Assert.Equal(overloaded, SelectionTraces(World.Api.World));
			baseSkip.SetValue(selection, 3);
			maxSkip.SetValue(selection, 20);
			await World.Ticks(4);
			Assert.True(SelectionTraces(World.Api.World) > overloaded, "the skip interval never traced");
		}
		finally
		{
			overload.SetValue(selection, previousOverload);
			baseSkip.SetValue(selection, previousBase);
			maxSkip.SetValue(selection, previousMax);
		}
	}

	private static object SelectionConfig()
	{
		Type runtime = Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib")
			?? throw new InvalidOperationException("StratumRuntime was not in the loaded lib");
		object config = runtime.GetProperty("Config")!.GetValue(null)!;
		object performance = config.GetType().GetProperty("Performance")!.GetValue(config)!;
		return performance.GetType().GetProperty("EntitySelection")!.GetValue(performance)!;
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
