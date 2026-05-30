using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Vintagestory.Server;

// Publishes a JSON server snapshot once per second on a named pipe.
// Pipe name: stratum-metrics-<pid>. Direction: server -> client. One client at a time.
// Designed so an external GUI/operator dashboard can subscribe without ever poking the
// chat command pipeline. If no one is connected, Publish() is a near-zero cost no-op.
internal static class StratumMetricsPublisher
{
	private static readonly object stateLock = new();
	private static NamedPipeServerStream? pipe;
	private static StreamWriter? writer;
	private static Thread? acceptThread;
	private static volatile bool running;
	private static volatile bool clientConnected;
	private static string pipeName = "stratum-metrics";
	private static DateTime startedUtc;
	private static long publishCount;
	private static long lastPublishElapsedMs;
	private static DateTime cpuLastSampledUtc;
	private static TimeSpan cpuLastTotal;
	private static double cpuLastPercent;

	public static string PipeName => pipeName;
	public static bool ClientConnected => clientConnected;

	public static void Start()
	{
		if (running) return;
		running = true;
		startedUtc = DateTime.UtcNow;
		pipeName = "stratum-metrics-" + Environment.ProcessId;
		acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "stratum-metrics-pipe" };
		acceptThread.Start();
		StratumRuntime.LogInfo("metrics pipe ready: \\\\.\\pipe\\" + pipeName);
	}

	public static void Stop()
	{
		running = false;
		Disconnect();
	}

	public static void Publish(ServerMain server)
	{
		if (!running || !clientConnected) return;
		StreamWriter? w;
		lock (stateLock) { w = writer; }
		if (w == null) return;

		try
		{
			string json = BuildSnapshot(server);
			w.WriteLine(json);
			w.Flush();
			publishCount++;
			lastPublishElapsedMs = server.ElapsedMilliseconds;
		}
		catch (IOException) { Disconnect(); }
		catch (ObjectDisposedException) { Disconnect(); }
	}

	private static void Disconnect()
	{
		lock (stateLock)
		{
			clientConnected = false;
			try { writer?.Dispose(); } catch { }
			try { pipe?.Dispose(); } catch { }
			writer = null;
			pipe = null;
		}
	}

	private static void AcceptLoop()
	{
		while (running)
		{
			NamedPipeServerStream? srv = null;
			try
			{
				srv = new NamedPipeServerStream(
					pipeName,
					PipeDirection.Out,
					1,
					PipeTransmissionMode.Byte,
					PipeOptions.Asynchronous,
					0,
					4096);
				srv.WaitForConnection();
				StreamWriter w = new(srv, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
				lock (stateLock)
				{
					pipe = srv;
					writer = w;
					clientConnected = true;
				}

				// Hold the connection until the next-tick Publish or a manual disconnect
				// drops it. Sleep cheaply in the meantime.
				while (running && clientConnected)
				{
					Thread.Sleep(250);
				}
			}
			catch (Exception ex)
			{
				try { srv?.Dispose(); } catch { }
				if (!running) return;
				if (ex is not IOException && ex is not ObjectDisposedException)
				{
					StratumRuntime.LogWarning("metrics pipe accept failed: " + ex.Message);
				}
				Thread.Sleep(500);
			}
		}
	}

	private static string BuildSnapshot(ServerMain server)
	{
		StatsCollection stats = server.StatsCollector[
			((server.StatsCollectorIndex - 1) % server.StatsCollector.Length + server.StatsCollector.Length) % server.StatsCollector.Length];
		double mspt = stats.ticksTotal <= 0 ? 0.0 : (double)stats.tickTimeTotal / stats.ticksTotal;
		double tps = stats.ticksTotal <= 0 ? 0.0 : stats.ticksTotal / 2.0;
		double targetTps = server.Config.TickTime <= 0 ? 0.0 : 1000.0 / server.Config.TickTime;
		double tickBudgetMs = server.Config.TickTime;

		int playersOnline = 0;
		int activeEntities = 0;
		foreach (ConnectedClient c in server.Clients.Values)
		{
			if (c.State.IsAdmitted()) playersOnline++;
		}
		foreach (Entity e in server.LoadedEntities.Values)
		{
			if (e.State != EnumEntityState.Inactive) activeEntities++;
		}

		long managedBytes = GC.GetTotalMemory(forceFullCollection: false);
		long workingSet = 0, privateBytes = 0;
		int threadCount = 0, handleCount = 0;
		double cpuPercent = 0.0;
		try
		{
			Process proc = Process.GetCurrentProcess();
			proc.Refresh();
			workingSet = proc.WorkingSet64;
			privateBytes = proc.PrivateMemorySize64;
			threadCount = proc.Threads.Count;
			handleCount = proc.HandleCount;
			cpuPercent = SampleCpuPercent(proc);
		}
		catch { }

		GCMemoryInfo gcInfo;
		try { gcInfo = GC.GetGCMemoryInfo(); } catch { gcInfo = default; }
		double gcPauseMs = 0.0;
		try { gcPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds; } catch { }

		ThreadPool.GetAvailableThreads(out int poolWorkerAvail, out int poolIoAvail);
		ThreadPool.GetMaxThreads(out int poolWorkerMax, out int poolIoMax);
		int poolThreadCount = ThreadPool.ThreadCount;
		long poolCompleted = ThreadPool.CompletedWorkItemCount;
		long poolPending = ThreadPool.PendingWorkItemCount;

		var packets = StratumRuntime.PacketLimiter.Snapshot();
		var breaks = StratumRuntime.BlockBreakGuard.Snapshot();

		// Per-entity-type rollup (top N by count). One pass, cheap dict.
		Dictionary<string, (int Total, int Active)> entityTypes = new(StringComparer.Ordinal);
		foreach (Entity e in server.LoadedEntities.Values)
		{
			string code = e.Code?.ToString() ?? "<unknown>";
			entityTypes.TryGetValue(code, out var t);
			bool active = e.State != EnumEntityState.Inactive;
			entityTypes[code] = (t.Total + 1, t.Active + (active ? 1 : 0));
		}
		var entityTypeTop = entityTypes.OrderByDescending(kv => kv.Value.Total).Take(30).ToArray();

		// EventManager listener / delayed callback counts. Public on internal EventManager.
		int listenersEntity = 0, listenersBlock = 0, callbacksEntity = 0, callbacksBlock = 0, singleCallbacksBlock = 0;
		try
		{
			var em = server.EventManager;
			if (em != null)
			{
				listenersEntity = em.GameTickListenersEntity.Count;
				listenersBlock = em.GameTickListenersBlock.Count;
				callbacksEntity = em.DelayedCallbacksEntity.Count;
				callbacksBlock = em.DelayedCallbacksBlock.Count;
				singleCallbacksBlock = em.SingleDelayedCallbacksBlock.Count;
			}
		}
		catch { }

		int generatingChunks = 0;
		try { generatingChunks = server.chunkThread?.requestedChunkColumns?.Count ?? 0; } catch { }

		double windowSeconds = 2.0;
		double tcpPps = stats.statTotalPackets / windowSeconds;
		double tcpKbps = stats.statTotalPacketsLength / windowSeconds / 1024.0;
		double udpPps = stats.statTotalUdpPackets / windowSeconds;
		double udpKbps = stats.statTotalUdpPacketsLength / windowSeconds / 1024.0;

		StringBuilder sb = new(2048);
		sb.Append('{');
		AppendString(sb, "schema", "stratum.metrics.v2"); sb.Append(',');
		AppendNumber(sb, "ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); sb.Append(',');
		AppendNumber(sb, "uptimeMs", (long)(DateTime.UtcNow - startedUtc).TotalMilliseconds); sb.Append(',');
		AppendString(sb, "runPhase", server.RunPhase.ToString()); sb.Append(',');
		AppendNumber(sb, "pid", Environment.ProcessId); sb.Append(',');

		// tick
		sb.Append("\"tick\":{");
		AppendNumber(sb, "tps", tps, "0.00"); sb.Append(',');
		AppendNumber(sb, "mspt", mspt, "0.00"); sb.Append(',');
		AppendNumber(sb, "targetTps", targetTps, "0.00"); sb.Append(',');
		AppendNumber(sb, "budgetMs", tickBudgetMs, "0.00"); sb.Append(',');
		AppendNumber(sb, "ticksInWindow", stats.ticksTotal);
		sb.Append("},");

		// players
		sb.Append("\"players\":{");
		AppendNumber(sb, "online", playersOnline); sb.Append(',');
		AppendNumber(sb, "max", server.Config.MaxClients); sb.Append(',');
		AppendNumber(sb, "queued", server.ConnectionQueue.Count); sb.Append(',');
		sb.Append("\"list\":[");
		bool first = true;
		ConnectedClient[] clientsSorted = server.Clients.Values
			.OrderBy(c => c.PlayerName ?? "")
			.ToArray();
		foreach (ConnectedClient c in clientsSorted)
		{
			if (!c.State.IsAdmitted()) continue;
			if (!first) sb.Append(',');
			first = false;
			long idleSec = Math.Max(0, (server.ElapsedMilliseconds - c.LastActivityTotalMs) / 1000);
			int pingMs = (int)((c.LastPing > 0 ? c.LastPing : 0f) * 1000f);
			string role = c.ServerData?.RoleCode ?? "unknown";
			string uid = c.Player?.PlayerUID ?? "";
			double x = 0, y = 0, z = 0;
			if (c.Player?.Entity?.ServerPos != null)
			{
				x = c.Player.Entity.ServerPos.X;
				y = c.Player.Entity.ServerPos.Y;
				z = c.Player.Entity.ServerPos.Z;
			}
			sb.Append('{');
			AppendString(sb, "name", c.PlayerName ?? ""); sb.Append(',');
			AppendString(sb, "uid", uid); sb.Append(',');
			AppendString(sb, "state", c.State.ToString()); sb.Append(',');
			AppendString(sb, "role", role); sb.Append(',');
			AppendNumber(sb, "pingMs", pingMs); sb.Append(',');
			AppendNumber(sb, "idleSec", idleSec); sb.Append(',');
			AppendNumber(sb, "x", x, "0.0"); sb.Append(',');
			AppendNumber(sb, "y", y, "0.0"); sb.Append(',');
			AppendNumber(sb, "z", z, "0.0");
			sb.Append('}');
		}
		sb.Append("]},");

		// world
		sb.Append("\"world\":{");
		AppendNumber(sb, "chunks", server.loadedChunks.Count); sb.Append(',');
		AppendNumber(sb, "mapChunks", server.loadedMapChunks.Count); sb.Append(',');
		AppendNumber(sb, "entities", server.LoadedEntities.Count); sb.Append(',');
		AppendNumber(sb, "activeEntities", activeEntities); sb.Append(',');
		AppendNumber(sb, "fastChunkQueue", server.fastChunkQueue.Count); sb.Append(',');
		AppendNumber(sb, "requestedColumns", server.ChunkColumnRequested.Count); sb.Append(',');
		AppendNumber(sb, "simpleLoads", server.simpleLoadRequests.Count); sb.Append(',');
		AppendNumber(sb, "peekColumns", server.peekChunkColumnQueue.Count); sb.Append(',');
		AppendNumber(sb, "generatingChunks", generatingChunks);
		sb.Append("},");

		// network throughput (sampled over last full 2s window)
		sb.Append("\"net\":{");
		AppendNumber(sb, "tcpPps", tcpPps, "0.00"); sb.Append(',');
		AppendNumber(sb, "tcpKbps", tcpKbps, "0.00"); sb.Append(',');
		AppendNumber(sb, "udpPps", udpPps, "0.00"); sb.Append(',');
		AppendNumber(sb, "udpKbps", udpKbps, "0.00"); sb.Append(',');
		AppendNumber(sb, "totalRxBytes", server.TotalReceivedBytes); sb.Append(',');
		AppendNumber(sb, "totalTxBytes", server.TotalSentBytes); sb.Append(',');
		AppendNumber(sb, "totalRxBytesUdp", server.TotalReceivedBytesUdp); sb.Append(',');
		AppendNumber(sb, "totalTxBytesUdp", server.TotalSentBytesUdp);
		sb.Append("},");

		// memory
		sb.Append("\"mem\":{");
		AppendNumber(sb, "managedMB", managedBytes / 1024.0 / 1024.0, "0.0"); sb.Append(',');
		AppendNumber(sb, "processMB", workingSet / 1024.0 / 1024.0, "0.0"); sb.Append(',');
		AppendNumber(sb, "privateMB", privateBytes / 1024.0 / 1024.0, "0.0"); sb.Append(',');
		AppendNumber(sb, "heapMB", gcInfo.HeapSizeBytes / 1024.0 / 1024.0, "0.0"); sb.Append(',');
		AppendNumber(sb, "fragmentedMB", gcInfo.FragmentedBytes / 1024.0 / 1024.0, "0.0"); sb.Append(',');
		AppendNumber(sb, "committedMB", gcInfo.TotalCommittedBytes / 1024.0 / 1024.0, "0.0"); sb.Append(',');
		AppendNumber(sb, "pauseTimePct", gcInfo.PauseTimePercentage, "0.00"); sb.Append(',');
		AppendNumber(sb, "gcPauseMs", gcPauseMs, "0.0"); sb.Append(',');
		AppendNumber(sb, "gen0", GC.CollectionCount(0)); sb.Append(',');
		AppendNumber(sb, "gen1", GC.CollectionCount(1)); sb.Append(',');
		AppendNumber(sb, "gen2", GC.CollectionCount(2));
		sb.Append("},");

		// process / threads
		sb.Append("\"proc\":{");
		AppendNumber(sb, "threads", threadCount); sb.Append(',');
		AppendNumber(sb, "handles", handleCount); sb.Append(',');
		AppendNumber(sb, "cpuPercent", cpuPercent, "0.00"); sb.Append(',');
		AppendNumber(sb, "poolThreads", poolThreadCount); sb.Append(',');
		AppendNumber(sb, "poolWorkerAvail", poolWorkerAvail); sb.Append(',');
		AppendNumber(sb, "poolWorkerMax", poolWorkerMax); sb.Append(',');
		AppendNumber(sb, "poolIoAvail", poolIoAvail); sb.Append(',');
		AppendNumber(sb, "poolIoMax", poolIoMax); sb.Append(',');
		AppendNumber(sb, "poolPending", poolPending); sb.Append(',');
		AppendNumber(sb, "poolCompleted", poolCompleted);
		sb.Append("},");

		// event listeners + delayed callbacks
		sb.Append("\"events\":{");
		AppendNumber(sb, "listenersEntity", listenersEntity); sb.Append(',');
		AppendNumber(sb, "listenersBlock", listenersBlock); sb.Append(',');
		AppendNumber(sb, "callbacksEntity", callbacksEntity); sb.Append(',');
		AppendNumber(sb, "callbacksBlock", callbacksBlock); sb.Append(',');
		AppendNumber(sb, "singleCallbacksBlock", singleCallbacksBlock);
		sb.Append("},");

		// per-entity-type rollup (top 30)
		sb.Append("\"entityTypes\":[");
		bool firstType = true;
		foreach (var kv in entityTypeTop)
		{
			if (!firstType) sb.Append(',');
			firstType = false;
			sb.Append('{');
			AppendString(sb, "code", kv.Key); sb.Append(',');
			AppendNumber(sb, "total", kv.Value.Total); sb.Append(',');
			AppendNumber(sb, "active", kv.Value.Active);
			sb.Append('}');
		}
		sb.Append("],");

		// protection
		sb.Append("\"protection\":{");
		AppendBool(sb, "packetMonitoring", StratumRuntime.Config.Hardening.PacketMonitoring); sb.Append(',');
		AppendBool(sb, "blockBreakGuards", StratumRuntime.Config.Hardening.BlockBreakGuards); sb.Append(',');
		AppendBool(sb, "inventoryGuards", StratumRuntime.Config.Hardening.InventoryGuards); sb.Append(',');
		AppendBool(sb, "entityGuards", StratumRuntime.Config.Hardening.EntityGuards); sb.Append(',');
		AppendBool(sb, "timingsRunning", StratumRuntime.Timings.Enabled); sb.Append(',');
		sb.Append("\"packets\":{");
		AppendNumber(sb, "accepted", packets.Accepted); sb.Append(',');
		AppendNumber(sb, "dropped", packets.Dropped); sb.Append(',');
		AppendNumber(sb, "violations", packets.Violations); sb.Append(',');
		AppendNumber(sb, "bytes", packets.Bytes);
		sb.Append("},\"blockBreaks\":{");
		AppendNumber(sb, "observed", breaks.Observed); sb.Append(',');
		AppendNumber(sb, "rejected", breaks.Rejected); sb.Append(',');
		AppendNumber(sb, "kicked", breaks.Kicked);
		sb.Append("}},");

		// preflight + config
		sb.Append("\"preflight\":{");
		AppendBool(sb, "ok", StratumRuntime.LastPreflight.Passed); sb.Append(',');
		AppendString(sb, "summary", StratumRuntime.LastPreflight.Summary ?? "");
		sb.Append("},");
		AppendString(sb, "configPath", StratumRuntime.ConfigPath ?? ""); sb.Append(',');
		AppendString(sb, "configStatus", StratumRuntime.LastLoadStatus ?? "");

		sb.Append('}');
		return sb.ToString();
	}

	private static void AppendString(StringBuilder sb, string key, string value)
	{
		sb.Append('"').Append(key).Append("\":");
		sb.Append('"');
		foreach (char ch in value)
		{
			switch (ch)
			{
				case '\\': sb.Append("\\\\"); break;
				case '"': sb.Append("\\\""); break;
				case '\n': sb.Append("\\n"); break;
				case '\r': sb.Append("\\r"); break;
				case '\t': sb.Append("\\t"); break;
				default:
					if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("X4"));
					else sb.Append(ch);
					break;
			}
		}
		sb.Append('"');
	}

	private static void AppendNumber(StringBuilder sb, string key, long value)
	{
		sb.Append('"').Append(key).Append("\":").Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
	}

	private static void AppendNumber(StringBuilder sb, string key, int value)
	{
		sb.Append('"').Append(key).Append("\":").Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
	}

	private static void AppendNumber(StringBuilder sb, string key, double value, string format)
	{
		sb.Append('"').Append(key).Append("\":").Append(value.ToString(format, System.Globalization.CultureInfo.InvariantCulture));
	}

	private static void AppendBool(StringBuilder sb, string key, bool value)
	{
		sb.Append('"').Append(key).Append("\":").Append(value ? "true" : "false");
	}

	private static double SampleCpuPercent(Process proc)
	{
		DateTime now = DateTime.UtcNow;
		TimeSpan total = proc.TotalProcessorTime;
		lock (stateLock)
		{
			if (cpuLastSampledUtc == default)
			{
				cpuLastSampledUtc = now;
				cpuLastTotal = total;
				return 0.0;
			}
			double elapsedMs = (now - cpuLastSampledUtc).TotalMilliseconds;
			if (elapsedMs < 250) return cpuLastPercent;
			double cpuMs = (total - cpuLastTotal).TotalMilliseconds;
			int cores = Math.Max(1, Environment.ProcessorCount);
			double pct = cpuMs / elapsedMs / cores * 100.0;
			if (pct < 0) pct = 0; else if (pct > 100.0 * cores) pct = 100.0 * cores;
			cpuLastSampledUtc = now;
			cpuLastTotal = total;
			cpuLastPercent = pct;
			return pct;
		}
	}
}
