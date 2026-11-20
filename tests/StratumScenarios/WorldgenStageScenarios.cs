using System.Reflection;
using System.Text;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Fresh columns have to run every worldgen stage. The timings buckets name which
/// stage owned the time. This does not change the generators.
/// </summary>
public class WorldgenStageScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task FreshColumns_Should_RecordEachWorldgenStage()
	{
		StartTimings();
		BlockPos anchor = new BlockPos(80 * 32, 120, 80 * 32);
		World.Api.WorldManager.LoadChunkColumnPriority(80, 80);
		await World.Until(
			() => World.Api.World.BlockAccessor.GetChunkAtBlockPos(anchor) != null,
			timeoutTicks: 800);

		string report = TimingsReport();
		string stages = WorldgenLines(report);
		Assert.False(string.IsNullOrEmpty(stages), "no worldgen stage was recorded\n" + report);
		Assert.Contains("worldgen.terrain", stages);
	}

	private static string WorldgenLines(string report)
	{
		StringBuilder lines = new StringBuilder();
		foreach (string line in report.Split('\n'))
		{
			if (line.Contains("worldgen."))
			{
				lines.AppendLine(line.Trim());
			}
		}

		return lines.ToString();
	}

	private static void StartTimings()
	{
		object timings = Timings();
		timings.GetType().GetMethod("Start")!.Invoke(timings, null);
	}

	private static string TimingsReport()
	{
		object timings = Timings();
		return (string)timings.GetType().GetMethod("BuildReport")!.Invoke(timings, null)!;
	}

	private static object Timings()
	{
		Type runtime = Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib")
			?? throw new InvalidOperationException("StratumRuntime was not loaded");
		return runtime.GetProperty("Timings")!.GetValue(null)!;
	}
}
