using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Vintagestory.Server;

internal sealed class StratumTimings
{
	private sealed class Bucket
	{
		public long Calls;

		public long TotalTicks;

		public long MaxTicks;

		public long LastTicks;

		public long SlowCalls;
	}

	private readonly object gate = new object();
	private readonly Dictionary<string, Bucket> buckets = new Dictionary<string, Bucket>(StringComparer.Ordinal);
	private bool enabled;
	private DateTime startedUtc;
	private DateTime stoppedUtc;

	public bool Enabled => enabled;

	public string Start()
	{
		lock (gate)
		{
			buckets.Clear();
			enabled = true;
			startedUtc = DateTime.UtcNow;
			stoppedUtc = default;
			return "Stratum timings started";
		}
	}

	public string Stop()
	{
		lock (gate)
		{
			if (!enabled)
			{
				return "Stratum timings are already stopped";
			}

			enabled = false;
			stoppedUtc = DateTime.UtcNow;
			return "Stratum timings stopped";
		}
	}

	public string Reset()
	{
		lock (gate)
		{
			buckets.Clear();
			startedUtc = enabled ? DateTime.UtcNow : default;
			stoppedUtc = default;
			return enabled ? "Stratum timings reset and still running" : "Stratum timings reset";
		}
	}

	public long GetTimestamp()
	{
		return enabled ? Stopwatch.GetTimestamp() : 0L;
	}

	public void RecordElapsed(string name, long startedTimestamp)
	{
		if (!enabled || startedTimestamp == 0L)
		{
			return;
		}

		long elapsedTicks = Stopwatch.GetTimestamp() - startedTimestamp;
		RecordTicks(name, elapsedTicks);
	}

	public void RecordMeasuredTicks(string name, long elapsedTicks)
	{
		if (!enabled || elapsedTicks <= 0L)
		{
			return;
		}

		RecordTicks(name, elapsedTicks);
	}

	public void RecordPacket(int packetId, long startedTimestamp)
	{
		if (!enabled || startedTimestamp == 0L)
		{
			return;
		}

		RecordElapsed("packet." + GetPacketName(packetId), startedTimestamp);
	}

	private void RecordTicks(string name, long elapsedTicks)
	{
		lock (gate)
		{
			if (!buckets.TryGetValue(name, out Bucket bucket))
			{
				bucket = new Bucket();
				buckets[name] = bucket;
			}

			bucket.Calls++;
			bucket.TotalTicks += elapsedTicks;
			bucket.LastTicks = elapsedTicks;
			bucket.MaxTicks = Math.Max(bucket.MaxTicks, elapsedTicks);
			if (TicksToMilliseconds(elapsedTicks) >= StratumRuntime.Config.Performance.Timings.SlowSampleThresholdMs)
			{
				bucket.SlowCalls++;
			}
		}
	}

	public string BuildReport()
	{
		StratumConfig config = StratumRuntime.Config;
		config.EnsurePopulated();
		int topEntries = Math.Max(1, config.Performance.Timings.ReportTopEntries);
		List<(string Name, long Calls, long TotalTicks, long MaxTicks, long LastTicks, long SlowCalls)> snapshot;
		DateTime localStarted;
		DateTime localStopped;
		bool localEnabled;

		lock (gate)
		{
			localEnabled = enabled;
			localStarted = startedUtc;
			localStopped = stoppedUtc;
			snapshot = buckets.Select(entry => (entry.Key, entry.Value.Calls, entry.Value.TotalTicks, entry.Value.MaxTicks, entry.Value.LastTicks, entry.Value.SlowCalls)).ToList();
		}

		StringBuilder output = new StringBuilder("Stratum Timings\n");
		if (localStarted == default && snapshot.Count == 0)
		{
			output.Append("State: stopped, no samples recorded\n");
			output.Append("Use /stratum timings start to begin sampling.");
			return output.ToString();
		}

		DateTime end = localEnabled ? DateTime.UtcNow : (localStopped == default ? DateTime.UtcNow : localStopped);
		TimeSpan duration = localStarted == default ? TimeSpan.Zero : end - localStarted;
		output.Append("State: ").Append(localEnabled ? "running" : "stopped")
			.Append(", duration=").Append(FormatDuration(duration))
			.Append(", buckets=").Append(snapshot.Count)
			.Append(", slowThreshold=").Append(config.Performance.Timings.SlowSampleThresholdMs.ToString("0.##")).Append("ms\n");

		if (snapshot.Count == 0)
		{
			output.Append("No timing samples yet.");
			return output.ToString();
		}

		output.Append("\nTop Total Time\n");
		foreach (var row in snapshot.OrderByDescending(row => row.TotalTicks).ThenBy(row => row.Name).Take(topEntries))
		{
			AppendTimingRow(output, row.Name, row.Calls, row.TotalTicks, row.MaxTicks, row.LastTicks, row.SlowCalls);
		}

		output.Append("\nTop Max Sample\n");
		foreach (var row in snapshot.OrderByDescending(row => row.MaxTicks).ThenBy(row => row.Name).Take(Math.Min(8, topEntries)))
		{
			AppendTimingRow(output, row.Name, row.Calls, row.TotalTicks, row.MaxTicks, row.LastTicks, row.SlowCalls);
		}

		return output.ToString();
	}

	private static void AppendTimingRow(StringBuilder output, string name, long calls, long totalTicks, long maxTicks, long lastTicks, long slowCalls)
	{
		double totalMs = TicksToMilliseconds(totalTicks);
		double averageMs = calls <= 0 ? 0 : totalMs / calls;
		output.Append("  ").Append(name)
			.Append(" calls=").Append(calls)
			.Append(" total=").Append(totalMs.ToString("0.###")).Append("ms")
			.Append(" avg=").Append(averageMs.ToString("0.###")).Append("ms")
			.Append(" max=").Append(TicksToMilliseconds(maxTicks).ToString("0.###")).Append("ms")
			.Append(" last=").Append(TicksToMilliseconds(lastTicks).ToString("0.###")).Append("ms");

		if (slowCalls > 0)
		{
			output.Append(" slow=").Append(slowCalls);
		}

		output.Append('\n');
	}

	private static string FormatDuration(TimeSpan duration)
	{
		return $"{(int)duration.TotalHours:0}h {duration.Minutes:00}m {duration.Seconds:00}s";
	}

	private static double TicksToMilliseconds(long ticks)
	{
		return ticks * 1000d / Stopwatch.Frequency;
	}

	private static string GetPacketName(int packetId)
	{
		return packetId switch
		{
			3 => "BlockPlaceOrBreak(3)",
			7 => "ActivateInventorySlot(7)",
			8 => "MoveItemstack(8)",
			10 => "CreateItemstack(10)",
			17 => "EntityInteraction(17)",
			21 => "MoveKeyChange(21)",
			22 => "BlockEntityPacket(22)",
			23 => "CustomPacket(23)",
			25 => "HandInteraction(25)",
			28 => "BlockDamage(28)",
			30 => "InvOpenClose(30)",
			31 => "EntityPacket(31)",
			34 => "RequestPositionTCP(34)",
			_ => "Id" + packetId
		};
	}
}