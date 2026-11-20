using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common.Entities;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Reading contract version 1 of StratumEntityBehaviorTimings, the accumulator behind the admin's
/// /stratum timings report. A reader (a monitoring mod) asks for recording with RequestRecording, which
/// is ORed with the admin switch, and reads cumulative totals with Snapshot. Neither may disturb the admin:
/// the engine keeps draining into the report exactly as before, and Drain, which stays engine-only, now
/// returns the delta since its previous call instead of emptying the accumulator (which also removes the
/// race where a Record landing between its read and its clear was lost).
///
/// Four points, one scenario each: a lease records with the admin switch off; a lease does not change the
/// admin report; Snapshot destroys nothing, from any thread; Drain returns deltas. Two small ones pin what a
/// reflection binder relies on (the lease is counted and its Dispose idempotent, the public shape).
///
/// Real engine keys prove the producers record under a lease. Exact assertions use sentinel keys the engine
/// never writes, recorded by the scenario with a start timestamp one second in the past, so every record is
/// worth about one second and nothing else adds to the key. Every scenario starts with the admin switch off
/// and an empty accumulator, and ends the same way, because the accumulator is static and the class shares
/// one server boot.
/// </summary>
public class BehaviorTimingsContractScenarios : AtlasScenarioBase
{
	private const string SentinelPrefix = "scenario.contract.";
	private const int ReaderThreads = 2;
	private const int ReaderTicks = 60;

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task Lease_Should_Record_When_AdminSwitchIsOff()
	{
		await World.JoinPlayer("timings-lease");
		await World.Ticks(5);
		await StopAdminTimings();

		Assert.False(StratumEntityBehaviorTimings.Enabled, "recording is on with no lease and the admin switch off");
		Assert.Empty(ReadTotals());

		using (StratumEntityBehaviorTimings.RequestRecording())
		{
			Assert.True(StratumEntityBehaviorTimings.Enabled, "a lease must turn recording on without the admin switch");

			// The engine asserts the admin switch (off) twice per tick: it must not undo the lease.
			await World.Ticks(30);
			Assert.True(StratumEntityBehaviorTimings.Enabled, "the engine's per-tick assert of the admin switch undid the lease");

			Dictionary<string, (long Ticks, long Calls)> totals = ReadTotals();
			string keys = string.Join(", ", totals.Keys.OrderBy(k => k, StringComparer.Ordinal));

			// Whole entity ticks (a joined player always ticks) and per-behavior keys come from different
			// producers (entity simulation, Entity.OnGameTick, the physics manager).
			KeyValuePair<string, (long Ticks, long Calls)>[] entityTypes = totals
				.Where(e => e.Key.StartsWith("entity.type.", StringComparison.Ordinal))
				.ToArray();
			Assert.True(entityTypes.Length > 0, $"no entity.type.* key recorded under a lease; keys: {keys}");
			Assert.All(entityTypes, e => Assert.True(e.Value.Ticks > 0 && e.Value.Calls > 0, $"{e.Key} recorded nothing"));
			Assert.True(
				totals.Keys.Any(k => k.StartsWith("entity.behavior.", StringComparison.Ordinal)),
				$"no entity.behavior.* key recorded under a lease; keys: {keys}");
		}

		// Last lease gone, admin switch off: recording stops and the memory is given back, as without a reader.
		Assert.False(StratumEntityBehaviorTimings.Enabled, "recording stayed on after the last lease was disposed");
		Assert.Empty(ReadTotals());
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task AdminReport_Should_BeUnchanged_When_LeaseIsHeld()
	{
		await World.JoinPlayer("timings-report");
		await World.Ticks(5);
		await StopAdminTimings();

		string before = SentinelPrefix + "before";
		string during = SentinelPrefix + "during";
		string after = SentinelPrefix + "after";

		IDisposable lease = StratumEntityBehaviorTimings.RequestRecording();
		try
		{
			// Admin switch off, lease recording a backlog: nobody drains, so the admin's report stays empty.
			RecordSentinel(before, 2);
			await World.Ticks(20);
			Assert.True(ReadTotals().ContainsKey(before), "the lease did not record the backlog sentinel: setup is invalid");
			Assert.Contains("no samples recorded", (await Report()).Message);

			// Admin switch on. Its report starts now: the backlog the lease collected must not leak into it.
			CommandResult start = await World.ExecuteCommand("/stratum timings start");
			Assert.True(start.Ok, start.Message);
			await World.Ticks(2); // the engine's first assert of the new switch rebases the drain cursors

			RecordSentinel(during, 3);
			using (StratumEntityBehaviorTimings.RequestRecording())
			{
				_ = ReadTotals(); // a second reader coming and going changes nothing for anyone else
			}

			await World.Ticks(3);
			CommandResult running = await Report();
			Assert.DoesNotContain(before, running.Message);
			(long runningCalls, double runningMs) = ParseRow(running.Message, during);
			Assert.True(runningCalls == 1, $"{during}: one drain window expected, the report says calls={runningCalls}");
			Assert.InRange(runningMs, 3000.0, 4000.0);

			// Admin switch off, lease still held: the report freezes while the reader keeps recording.
			CommandResult stop = await World.ExecuteCommand("/stratum timings stop");
			Assert.True(stop.Ok, stop.Message);
			await World.Ticks(2);
			Assert.True(StratumEntityBehaviorTimings.Enabled, "the admin's stop switched recording off under a lease");

			RecordSentinel(after, 1);
			await World.Ticks(3);
			Assert.True(ReadTotals().ContainsKey(after), "the reader stopped recording when the admin stopped");

			CommandResult frozen = await Report();
			Assert.DoesNotContain(after, frozen.Message);
			Assert.DoesNotContain(before, frozen.Message);
			Assert.Equal((runningCalls, runningMs), ParseRow(frozen.Message, during));
		}
		finally
		{
			lease.Dispose();
			await StopAdminTimings();
		}
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task Snapshot_Should_DestroyNothing_When_ReadRepeatedlyFromAnyThread()
	{
		await World.JoinPlayer("timings-snapshot");
		await World.Ticks(5);
		await StopAdminTimings();

		string kept = SentinelPrefix + "kept";
		using IDisposable lease = StratumEntityBehaviorTimings.RequestRecording();
		RecordSentinel(kept, 2);
		await World.Ticks(10);

		Dictionary<string, (long Ticks, long Calls)> first = ReadTotals();
		Dictionary<string, (long Ticks, long Calls)> second = ReadTotals();
		Assert.True(first.Count > 1, "only the sentinel was recorded: setup is invalid");
		AssertNeverShrinks(first, second);
		Assert.Equal(first[kept], second[kept]);
		Assert.Equal(2, second[kept].Calls);

		// Plain threads read as fast as they can while the server ticks and the engine keeps recording.
		var problems = new ConcurrentQueue<string>();
		long[] reads = new long[1];
		using var stop = new CancellationTokenSource();
		Thread[] readers = Enumerable.Range(0, ReaderThreads)
			.Select(i => new Thread(() => ReadLoop(problems, stop.Token, reads))
			{
				IsBackground = true,
				Name = $"timings-reader-{i}",
			})
			.ToArray();

		try
		{
			foreach (Thread reader in readers)
			{
				reader.Start();
			}

			await World.Ticks(ReaderTicks);
		}
		finally
		{
			stop.Cancel();
			foreach (Thread reader in readers.Where(t => t.IsAlive))
			{
				reader.Join();
			}
		}

		Assert.True(reads[0] > ReaderTicks, $"readers barely ran ({reads[0]} reads over {ReaderTicks} ticks): setup is invalid");
		Assert.True(problems.IsEmpty, string.Join(" | ", problems.GroupBy(p => p).Select(g => $"{g.Count()}x {g.Key}")));

		Dictionary<string, (long Ticks, long Calls)> third = ReadTotals();
		AssertNeverShrinks(second, third);
		Assert.Equal(second[kept], third[kept]);
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task Drain_Should_ReturnDeltas_When_CalledRepeatedly()
	{
		await World.JoinPlayer("timings-drain");
		await World.Ticks(5);
		await StopAdminTimings(); // admin switch off: the engine never drains here, this scenario is the only drainer

		string name = SentinelPrefix + "delta";
		long oneSecond = Stopwatch.Frequency;
		using IDisposable lease = StratumEntityBehaviorTimings.RequestRecording();
		await World.Ticks(5); // engine keys pile up beside the sentinel and must not disturb it

		RecordSentinel(name, 2);
		long firstDelta = DeltaOf(StratumEntityBehaviorTimings.Drain(), name);
		Assert.InRange(firstDelta, 2 * oneSecond, (2 * oneSecond) + (oneSecond / 2));
		Assert.Equal(firstDelta, ReadTotals()[name].Ticks); // the first drain returns everything recorded so far

		// A delta, not the total: nothing new since, so nothing returned.
		Assert.Equal(0L, DeltaOf(StratumEntityBehaviorTimings.Drain(), name));

		RecordSentinel(name, 1);
		long secondDelta = DeltaOf(StratumEntityBehaviorTimings.Drain(), name);
		Assert.InRange(secondDelta, oneSecond, oneSecond + (oneSecond / 2));

		// Drain cleared nothing: the cumulative total is both deltas and every call.
		(long ticks, long calls) = ReadTotals()[name];
		Assert.Equal(firstDelta + secondDelta, ticks);
		Assert.Equal(3L, calls);
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task Lease_Should_ReleaseOnce_When_DisposedTwice()
	{
		await World.JoinPlayer("timings-dispose");
		await World.Ticks(5);
		await StopAdminTimings();
		Assert.False(StratumEntityBehaviorTimings.Enabled, "recording is on with no lease and the admin switch off");

		IDisposable a = StratumEntityBehaviorTimings.RequestRecording();
		IDisposable b = StratumEntityBehaviorTimings.RequestRecording();
		a.Dispose();
		a.Dispose();
		Assert.True(StratumEntityBehaviorTimings.Enabled, "a second Dispose of one lease released another lease");

		b.Dispose();
		Assert.False(StratumEntityBehaviorTimings.Enabled, "recording stayed on after every lease was disposed");
		b.Dispose();
		Assert.False(StratumEntityBehaviorTimings.Enabled);
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task Contract_Should_ExposeBclOnlyMembers_When_BoundByReflection()
	{
		await World.Ticks(1);

		// What a reader that cannot reference Stratum's types looks for.
		Type type = typeof(StratumEntityBehaviorTimings);
		FieldInfo? version = type.GetField("ContractVersion", BindingFlags.Public | BindingFlags.Static);
		Assert.NotNull(version);
		Assert.True(version.IsLiteral, "ContractVersion must be a constant");
		Assert.Equal(1, version.GetRawConstantValue());

		MethodInfo? request = type.GetMethod("RequestRecording", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
		Assert.NotNull(request);
		Assert.Equal(typeof(IDisposable), request.ReturnType);

		MethodInfo? snapshot = type.GetMethod(
			"Snapshot", BindingFlags.Public | BindingFlags.Static, [typeof(List<(string, long, long)>)]);
		Assert.NotNull(snapshot);
		Assert.Equal(typeof(void), snapshot.ReturnType);
	}

	/// <summary>
	/// Switches the admin side off and empties its report, then waits for the engine's next asserts of the
	/// switch, which (with no lease held) empty the accumulator too.
	/// </summary>
	private async Task StopAdminTimings()
	{
		await World.ExecuteCommand("/stratum timings stop");
		await World.ExecuteCommand("/stratum timings reset");
		await World.Ticks(2);
	}

	private async Task<CommandResult> Report()
	{
		CommandResult report = await World.ExecuteCommand("/stratum timings report");
		Assert.True(report.Ok, report.Message);
		return report;
	}

	/// <summary>One record worth about one second, with the lease or the admin switch on.</summary>
	private static void RecordSentinel(string name, int times)
	{
		for (int i = 0; i < times; i++)
		{
			StratumEntityBehaviorTimings.RecordNamed(name, Stopwatch.GetTimestamp() - Stopwatch.Frequency);
		}
	}

	private static Dictionary<string, (long Ticks, long Calls)> ReadTotals()
	{
		var rows = new List<(string Key, long Ticks, long Calls)>();
		StratumEntityBehaviorTimings.Snapshot(rows);
		return rows.ToDictionary(r => r.Key, r => (r.Ticks, r.Calls), StringComparer.Ordinal);
	}

	private static long DeltaOf(List<StratumEntityBehaviorTimings.Measurement> drained, string name)
	{
		return drained.Where(m => m.Name == name).Sum(m => m.ElapsedTicks);
	}

	private static void AssertNeverShrinks(
		Dictionary<string, (long Ticks, long Calls)> earlier, Dictionary<string, (long Ticks, long Calls)> later)
	{
		foreach ((string key, (long ticks, long calls)) in earlier)
		{
			Assert.True(later.TryGetValue(key, out (long Ticks, long Calls) now), $"{key} vanished from a later snapshot");
			Assert.True(now.Ticks >= ticks && now.Calls >= calls, $"{key} shrank: ({ticks}, {calls}) then ({now.Ticks}, {now.Calls})");
		}
	}

	/// <summary>
	/// The admin report prints "name calls=N total=Xms" (X formatted with the machine's culture).
	/// </summary>
	private static (long Calls, double Ms) ParseRow(string report, string name)
	{
		Match row = Regex.Match(report, Regex.Escape(name) + @" calls=(\d+) total=(\d+(?:[.,]\d+)?)ms");
		Assert.True(row.Success, $"no row for {name} in the report:\n{report}");
		return (
			long.Parse(row.Groups[1].Value, CultureInfo.InvariantCulture),
			double.Parse(row.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture));
	}

	private static void ReadLoop(ConcurrentQueue<string> problems, CancellationToken stop, long[] reads)
	{
		var rows = new List<(string Key, long Ticks, long Calls)>();
		var previous = new Dictionary<string, (long Ticks, long Calls)>(StringComparer.Ordinal);
		while (!stop.IsCancellationRequested)
		{
			try
			{
				StratumEntityBehaviorTimings.Snapshot(rows);
				if (rows.Count < previous.Count)
				{
					problems.Enqueue($"a snapshot lost keys: {previous.Count} then {rows.Count}");
				}

				foreach ((string key, long ticks, long calls) in rows)
				{
					if (previous.TryGetValue(key, out (long Ticks, long Calls) seen) && (ticks < seen.Ticks || calls < seen.Calls))
					{
						problems.Enqueue($"{key} went backwards: ({seen.Ticks}, {seen.Calls}) then ({ticks}, {calls})");
					}

					previous[key] = (ticks, calls);
				}

				Interlocked.Increment(ref reads[0]);
			}
			catch (Exception e)
			{
				problems.Enqueue($"{e.GetType().Name}: {e.Message}");
			}

			Thread.Sleep(1);
		}
	}
}
