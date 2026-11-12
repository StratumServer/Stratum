using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Regression coverage for the pregen job queue (PR #381). All jobs are tiny rects, so the
/// whole class is meant to run in well under a minute including world boot.
///
/// Covers:
///  - FIFO handoff after completion, asserted through the status of the LAST job
///    (a bare "complete" is also reported when the second job was silently dropped);
///  - the 32-job cap behind a paused job;
///  - `stop` clearing pending work and restoring the original chunk TTL;
///  - `skip` draining one job at a time and the "queue is empty" reply;
///  - usage/error replies containing no raw angle brackets (chat renders VTML and swallows
///    them, see #76) and `queue` errors showing the `queue` form instead of `start`.
///
/// Atlas contract notes:
///  - scenarios run ON the game thread: never block (no Thread.Sleep), wait with World.Ticks,
///    and never use ConfigureAwait(false);
///  - the world is shared by all scenarios of the class, and StratumRuntime.Pregen is a
///    static singleton, so every scenario ends with `stop` in a finally block.
/// </summary>
public class PregenQueueScenarios : AtlasScenarioBase
{
	private const int ChunkSize = 32;
	private const int PollTicks = 5;

	// ------------------------------------------------------------------
	// Helpers
	// ------------------------------------------------------------------

	private Task<CommandResult> Pregen(string args) => World.ExecuteCommand("/stratum pregen " + args);

	/// <summary>
	/// Rect of <paramref name="size"/> x <paramref name="size"/> chunks, placed near world spawn
	/// (always a valid chunk position) and spaced 10 chunks apart per index.
	/// </summary>
	private string Rect(int index, int size = 2)
	{
		(int x, int z) = Origin(index);
		return $"{x} {z} {x + size - 1} {z + size - 1}";
	}

	private (int X, int Z) Origin(int index) =>
		(World.Spawn.X / ChunkSize + 20 + index * 10, World.Spawn.Z / ChunkSize + 20);

	/// <summary>Polls `status` once per few ticks until the predicate holds; returns the last status text.</summary>
	private async Task<(bool Reached, string Status)> WaitForStatus(Func<string, bool> predicate, int maxPolls = 400)
	{
		string status = "";
		for (int i = 0; i < maxPolls; i++)
		{
			status = (await Pregen("status")).Message;
			if (predicate(status)) return (true, status);
			await World.Ticks(PollTicks);
		}
		return (false, status);
	}

	/// <summary>Second line of the status block starts with idle|running|paused|complete.</summary>
	private async Task<string> ShortStatus()
	{
		string text = (await Pregen("status")).Message;
		string line = text.Split('\n').Skip(1).FirstOrDefault() ?? "";
		return line.Split(' ').FirstOrDefault() ?? "";
	}

	/// <summary>MagicNum lives in the server lib, which the test project does not reference: read it by reflection.</summary>
	private static int ReadUncompressedTtl()
	{
		static IEnumerable<Type> SafeGetTypes(Assembly a)
		{
			try { return a.GetTypes(); }
			catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
			catch { return Array.Empty<Type>(); }
		}

		Type? type = AppDomain.CurrentDomain.GetAssemblies()
			.SelectMany(SafeGetTypes)
			.FirstOrDefault(t => t.Name == "MagicNum");

		Assert.True(type != null, "MagicNum type not found in the loaded assemblies.");

		const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
		FieldInfo? field = type!.GetField("UncompressedChunkTTL", flags);
		if (field != null) return Convert.ToInt32(field.GetValue(null));

		PropertyInfo? prop = type.GetProperty("UncompressedChunkTTL", flags);
		Assert.True(prop != null, "UncompressedChunkTTL not found on MagicNum.");
		return Convert.ToInt32(prop!.GetValue(null));
	}

	private async Task StopQuietly()
	{
		try { await Pregen("stop"); } catch { /* best-effort cleanup, must not mask the real failure */ }
	}

	// ------------------------------------------------------------------
	// Scenarios
	// ------------------------------------------------------------------

	[AtlasScenario]
	public async Task PregenQueue_Should_RunJobsInFifoOrder_AndFinishLastJob()
	{
		try
		{
			// A starts at once (nothing running), B waits behind it.
			CommandResult a = await Pregen("queue rect " + Rect(0));
			Assert.True(a.Ok, "queue rect with nothing running must start immediately: " + a.Message);

			CommandResult b = await Pregen("queue rect " + Rect(1));
			Assert.True(b.Ok && b.Message.Contains("Queued #1"), "second job must be queued as #1: " + b.Message);

			(int bx, int bz) = Origin(1);
			var (reached, status) = await WaitForStatus(s =>
				// The status block describes the LAST job that ran, so these lines prove B actually ran.
				s.Contains("\ncomplete ")
				&& s.Contains($"Area: rect {bx},{bz} to {bx + 1},{bz + 1}")
				&& s.Contains("inspected=4/4")
				&& s.Contains("Queue: 0 job(s)"));

			Assert.True(reached, "chain did not finish with job B as the last completed job; last status:\n" + status);
		}
		finally { await StopQuietly(); }
	}

	[AtlasScenario]
	public async Task PregenQueue_Should_RefuseThe33rdJob_WhenQueueIsFull()
	{
		try
		{
			// A paused job keeps `running` true, so everything after it is queued.
			// 20x20 columns at the configured rate cannot finish in the gap before `pause`.
			Assert.True((await Pregen("start rect " + Rect(0, 20))).Ok, "start failed");
			Assert.True((await Pregen("pause")).Ok, "pause failed");

			for (int i = 1; i <= 32; i++)
			{
				CommandResult r = await Pregen("queue rect " + Rect(i));
				Assert.True(r.Ok && r.Message.Contains($"Queued #{i}"), $"job {i} should be queued as #{i}: {r.Message}");
			}

			CommandResult overflow = await Pregen("queue rect " + Rect(33));
			Assert.False(overflow.Ok, "33rd job must be refused");
			Assert.Contains("Queue is full (32).", overflow.Message);

			Assert.Contains("Queued jobs: 32", (await Pregen("queue list")).Message);
		}
		finally { await StopQuietly(); }
	}

	[AtlasScenario]
	public async Task PregenStop_Should_ClearQueue_AndRestoreTtl()
	{
		int originalTtl = ReadUncompressedTtl();
		try
		{
			Assert.True((await Pregen("start rect " + Rect(0, 20))).Ok, "start failed");
			Assert.True((await Pregen("queue rect " + Rect(1))).Ok, "queue #1 failed");
			Assert.True((await Pregen("queue rect " + Rect(2))).Ok, "queue #2 failed");
			Assert.Contains("Queued jobs: 2", (await Pregen("queue list")).Message);

			Assert.True((await Pregen("stop")).Ok, "stop failed");

			Assert.Contains("Queued jobs: 0", (await Pregen("queue list")).Message);
			Assert.Equal("idle", await ShortStatus());

			// RestoreTtl runs synchronously in Stop, but Tick also re-checks; give it a couple of ticks.
			await World.Ticks(2);
			Assert.Equal(originalTtl, ReadUncompressedTtl());
		}
		finally { await StopQuietly(); }
	}

	[AtlasScenario]
	public async Task PregenSkip_Should_DrainOneJobAtATime()
	{
		try
		{
			Assert.True((await Pregen("start rect " + Rect(0, 20))).Ok, "start failed");
			Assert.True((await Pregen("pause")).Ok, "pause failed");
			Assert.True((await Pregen("queue rect " + Rect(1))).Ok, "queue failed");

			// First skip: A is dropped, B (the only queued job) starts.
			(int bx, int bz) = Origin(1);
			CommandResult first = await Pregen("skip");
			Assert.True(first.Ok, "first skip failed: " + first.Message);
			Assert.Contains($"Area: rect {bx},{bz} to {bx + 1},{bz + 1}", first.Message);
			Assert.Contains("Queue: 0 job(s)", first.Message);

			// Second skip: queue is empty, pregen goes idle.
			CommandResult second = await Pregen("skip");
			Assert.True(second.Ok, "second skip failed: " + second.Message);
			Assert.Contains("Skipped; queue is empty.", second.Message);
			Assert.Equal("idle", await ShortStatus());

			// Nothing running any more: skip is an error, not a silent no-op.
			Assert.False((await Pregen("skip")).Ok, "skip with nothing running must be an error");
		}
		finally { await StopQuietly(); }
	}

	[AtlasTheory]
	[InlineData("foo")]
	[InlineData("queue")]
	[InlineData("queue bogus")]
	[InlineData("start bogus")]
	[InlineData("start radius x")]
	[InlineData("start rect 1 2 3")]
	[InlineData("queue radius x")]
	[InlineData("queue rect 1 2 3")]
	public async Task PregenUsage_Should_NotContainRawAngleBrackets(string args)
	{
		CommandResult r = await Pregen(args);

		Assert.False(r.Ok, $"'{args}' should be an error");
		Assert.False(r.Message.Contains('<') || r.Message.Contains('>'),
			$"'{args}' reply contains a raw angle bracket (VTML would swallow it): {r.Message}");
	}

	[AtlasScenario]
	public async Task PregenUsage_Should_ShowTheVerbTheUserTyped()
	{
		// A typo in a queue command must show the queue form, not the start form.
		Assert.Contains("pregen queue radius", (await Pregen("queue radius x")).Message);
		Assert.Contains("pregen queue rect", (await Pregen("queue rect 1 2 3")).Message);
		Assert.Contains("pregen start radius", (await Pregen("start radius x")).Message);
		Assert.Contains("pregen start rect", (await Pregen("start rect 1 2 3")).Message);

		// The generic usage must actually mention the queue syntax.
		Assert.Contains("queue", (await Pregen("foo")).Message);
	}
}
