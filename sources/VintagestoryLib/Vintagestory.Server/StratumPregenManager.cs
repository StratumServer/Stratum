using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.CommandAbbr;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

internal sealed class StratumPregenManager
{
	private readonly HashSet<long> activeRequests = new HashSet<long>();
	private bool running;
	private bool paused;
	private bool completed;
	private string mode = "idle";
	private int minChunkX;
	private int maxChunkX;
	private int minChunkZ;
	private int maxChunkZ;
	private int nextChunkX;
	private int nextChunkZ;
	private long totalColumns;
	private long inspectedColumns;
	private long queuedColumns;
	private long completedColumns;
	private long skippedLoadedColumns;
	private long skippedInvalidColumns;
	private long alreadyQueuedColumns;
	private long pressurePauses;
	private long startedAtMilliseconds;
	private long finishedAtMilliseconds;
	private long lastProgressLogMilliseconds;
	private string lastPauseReason = "none";

	public string ShortStatus
	{
		get
		{
			if (running)
			{
				return paused ? "paused" : "running";
			}

			return completed ? "complete" : "idle";
		}
	}

	public TextCommandResult HandleCommand(ServerMain server, TextCommandCallingArgs args)
	{
		string action = args[1] as string;
		if (string.IsNullOrWhiteSpace(action) || string.Equals(action, "status", StringComparison.OrdinalIgnoreCase))
		{
			return TextCommandResult.Success(BuildStatus(server));
		}

		if (string.Equals(action, "start", StringComparison.OrdinalIgnoreCase))
		{
			return HandleStart(server, args);
		}

		if (string.Equals(action, "pause", StringComparison.OrdinalIgnoreCase))
		{
			if (!running)
			{
				return TextCommandResult.Error("No pregen job is running.");
			}

			paused = true;
			lastPauseReason = "manual";
			return TextCommandResult.Success(BuildStatus(server));
		}

		if (string.Equals(action, "resume", StringComparison.OrdinalIgnoreCase))
		{
			if (!running)
			{
				return TextCommandResult.Error("No pregen job is running.");
			}

			paused = false;
			lastPauseReason = "none";
			return TextCommandResult.Success(BuildStatus(server));
		}

		if (string.Equals(action, "stop", StringComparison.OrdinalIgnoreCase))
		{
			Stop(server, "stopped by command");
			return TextCommandResult.Success(BuildStatus(server));
		}

		return TextCommandResult.Error(Usage);
	}

	public void Tick(ServerMain server)
	{
		if (!running || paused)
		{
			return;
		}

		StratumPregenConfig config = StratumRuntime.Config.Performance.Pregen;
		config.EnsureSane();
		UpdateActiveRequests(server);
		if (!config.Enabled)
		{
			pressurePauses++;
			lastPauseReason = "disabled in config";
			return;
		}

		if (TryGetPressureReason(server, config, out string reason))
		{
			pressurePauses++;
			lastPauseReason = reason;
			MaybeLogProgress(server);
			return;
		}

		lastPauseReason = "none";
		int queuedThisTick = 0;
		int scannedThisTick = 0;
		while (queuedThisTick < config.MaxColumnsPerSecond && scannedThisTick < config.MaxScansPerSecond && HasMoreColumns())
		{
			if (IsQueuePressureHigh(server, config))
			{
				pressurePauses++;
				lastPauseReason = "chunk queue pressure";
				break;
			}

			if (TryInspectNextColumn(server, out bool queued))
			{
				scannedThisTick++;
				if (queued)
				{
					queuedThisTick++;
				}
			}
		}

		UpdateActiveRequests(server);
		if (!HasMoreColumns() && activeRequests.Count == 0)
		{
			Complete(server);
			return;
		}

		MaybeLogProgress(server);
	}

	private TextCommandResult HandleStart(ServerMain server, TextCommandCallingArgs args)
	{
		StratumPregenConfig config = StratumRuntime.Config.Performance.Pregen;
		config.EnsureSane();
		if (!config.Enabled)
		{
			return TextCommandResult.Error("Pregen is disabled in stratum.json.");
		}

		if (!server.AutoGenerateChunks)
		{
			return TextCommandResult.Error("Cannot pregen while AutoGenerateChunks is disabled.");
		}

		if (running)
		{
			return TextCommandResult.Error("A pregen job is already running. Use /stratum pregen stop first.");
		}

		string shape = args[2] as string;
		if (string.Equals(shape, "radius", StringComparison.OrdinalIgnoreCase))
		{
			return StartRadius(server, args, config);
		}

		if (string.Equals(shape, "rect", StringComparison.OrdinalIgnoreCase))
		{
			return StartRect(server, args, config);
		}

		return TextCommandResult.Error(Usage);
	}

	private TextCommandResult StartRadius(ServerMain server, TextCommandCallingArgs args, StratumPregenConfig config)
	{
		if (!TryParseInt(args[3] as string, out int radiusChunks) || radiusChunks < 1)
		{
			return TextCommandResult.Error("Usage: /stratum pregen start radius &lt;radiusChunks&gt; [centerChunkX] [centerChunkZ]");
		}

		if (radiusChunks > config.MaxRadiusChunks)
		{
			return TextCommandResult.Error($"Radius {radiusChunks} exceeds configured maximum {config.MaxRadiusChunks} chunks.");
		}

		int centerChunkX;
		int centerChunkZ;
		if (TryParseInt(args[4] as string, out int parsedCenterX) && TryParseInt(args[5] as string, out int parsedCenterZ))
		{
			centerChunkX = parsedCenterX;
			centerChunkZ = parsedCenterZ;
		}
		else if (args.Caller?.Pos != null)
		{
			centerChunkX = args.Caller.Pos.XInt / MagicNum.ServerChunkSize;
			centerChunkZ = args.Caller.Pos.ZInt / MagicNum.ServerChunkSize;
		}
		else
		{
			centerChunkX = server.WorldMap.MapSizeX / MagicNum.ServerChunkSize / 2;
			centerChunkZ = server.WorldMap.MapSizeZ / MagicNum.ServerChunkSize / 2;
		}

		return Start(server, "radius", centerChunkX - radiusChunks, centerChunkZ - radiusChunks, centerChunkX + radiusChunks, centerChunkZ + radiusChunks, config);
	}

	private TextCommandResult StartRect(ServerMain server, TextCommandCallingArgs args, StratumPregenConfig config)
	{
		if (!TryParseInt(args[3] as string, out int x1) || !TryParseInt(args[4] as string, out int z1) || !TryParseInt(args[5] as string, out int x2) || !TryParseInt(args[6] as string, out int z2))
		{
			return TextCommandResult.Error("Usage: /stratum pregen start rect &lt;chunkX1&gt; &lt;chunkZ1&gt; &lt;chunkX2&gt; &lt;chunkZ2&gt;");
		}

		return Start(server, "rect", x1, z1, x2, z2, config);
	}

	private TextCommandResult Start(ServerMain server, string newMode, int x1, int z1, int x2, int z2, StratumPregenConfig config)
	{
		int normalizedMinX = Math.Min(x1, x2);
		int normalizedMaxX = Math.Max(x1, x2);
		int normalizedMinZ = Math.Min(z1, z2);
		int normalizedMaxZ = Math.Max(z1, z2);
		long width = (long)normalizedMaxX - normalizedMinX + 1;
		long height = (long)normalizedMaxZ - normalizedMinZ + 1;
		long area = width * height;
		if (area <= 0 || area > config.MaxAreaColumns)
		{
			return TextCommandResult.Error($"Pregen area is {area} columns; configured maximum is {config.MaxAreaColumns}.");
		}

		mode = newMode;
		minChunkX = normalizedMinX;
		maxChunkX = normalizedMaxX;
		minChunkZ = normalizedMinZ;
		maxChunkZ = normalizedMaxZ;
		nextChunkX = minChunkX;
		nextChunkZ = minChunkZ;
		totalColumns = area;
		inspectedColumns = 0;
		queuedColumns = 0;
		completedColumns = 0;
		skippedLoadedColumns = 0;
		skippedInvalidColumns = 0;
		alreadyQueuedColumns = 0;
		pressurePauses = 0;
		activeRequests.Clear();
		startedAtMilliseconds = server.ElapsedMilliseconds;
		finishedAtMilliseconds = -1;
		lastProgressLogMilliseconds = server.ElapsedMilliseconds;
		lastPauseReason = "none";
		completed = false;
		paused = false;
		running = true;
		StratumRuntime.LogInfo($"pregen started: {mode} chunks {minChunkX},{minChunkZ} to {maxChunkX},{maxChunkZ} ({totalColumns} columns)");
		return TextCommandResult.Success(BuildStatus(server));
	}

	private void Stop(ServerMain server, string reason)
	{
		if (!running)
		{
			return;
		}

		running = false;
		paused = false;
		completed = false;
		finishedAtMilliseconds = server.ElapsedMilliseconds;
		activeRequests.Clear();
		lastPauseReason = reason;
		StratumRuntime.LogInfo("pregen stopped: " + reason);
	}

	private void Complete(ServerMain server)
	{
		running = false;
		paused = false;
		completed = true;
		finishedAtMilliseconds = server.ElapsedMilliseconds;
		lastPauseReason = "none";
		StratumRuntime.LogInfo($"pregen complete: inspected={inspectedColumns}/{totalColumns} queued={queuedColumns} skippedLoaded={skippedLoadedColumns} skippedInvalid={skippedInvalidColumns}");
	}

	private bool TryInspectNextColumn(ServerMain server, out bool queued)
	{
		queued = false;
		if (!HasMoreColumns())
		{
			return false;
		}

		int chunkX = nextChunkX;
		int chunkZ = nextChunkZ;
		AdvanceCursor();
		inspectedColumns++;
		if (!server.WorldMap.IsValidChunkPos(chunkX, 0, chunkZ))
		{
			skippedInvalidColumns++;
			return true;
		}

		long mapChunkIndex = server.WorldMap.MapChunkIndex2D(chunkX, chunkZ);
		if (activeRequests.Contains(mapChunkIndex) || IsColumnQueuedOrGenerating(server, mapChunkIndex))
		{
			activeRequests.Add(mapChunkIndex);
			alreadyQueuedColumns++;
			return true;
		}

		if (server.IsChunkColumnFullyLoaded(chunkX, chunkZ))
		{
			skippedLoadedColumns++;
			return true;
		}

		if (TryQueueColumn(server, mapChunkIndex))
		{
			activeRequests.Add(mapChunkIndex);
			queuedColumns++;
			queued = true;
		}
		else
		{
			alreadyQueuedColumns++;
		}

		return true;
	}

	private bool TryQueueColumn(ServerMain server, long mapChunkIndex)
	{
		if (!server.ChunkColumnRequested.TryAdd(mapChunkIndex, 1))
		{
			return false;
		}

		try
		{
			lock (server.requestedChunkColumnsLock)
			{
				server.requestedChunkColumns.Enqueue(mapChunkIndex);
			}
			return true;
		}
		catch (Exception exception)
		{
			server.ChunkColumnRequested.TryRemove(mapChunkIndex, out _);
			StratumRuntime.LogWarning("pregen queue request failed: " + exception.Message);
			return false;
		}
	}

	private void UpdateActiveRequests(ServerMain server)
	{
		if (activeRequests.Count == 0)
		{
			return;
		}

		List<long> completedRequests = null;
		foreach (long mapChunkIndex in activeRequests)
		{
			if (!IsColumnQueuedOrGenerating(server, mapChunkIndex))
			{
				(completedRequests ??= new List<long>()).Add(mapChunkIndex);
			}
		}

		if (completedRequests == null)
		{
			return;
		}

		foreach (long mapChunkIndex in completedRequests)
		{
			activeRequests.Remove(mapChunkIndex);
			completedColumns++;
		}
	}

	private bool IsColumnQueuedOrGenerating(ServerMain server, long mapChunkIndex)
	{
		lock (server.requestedChunkColumnsLock)
		{
			if (server.requestedChunkColumns.Contains(mapChunkIndex))
			{
				return true;
			}
		}

		return server.chunkThread?.requestedChunkColumns.GetByIndex(mapChunkIndex) != null;
	}

	private bool TryGetPressureReason(ServerMain server, StratumPregenConfig config, out string reason)
	{
		if (config.PauseWhenPlayersOnline && server.Clients.Values.Count(client => client.State == EnumClientState.Playing || client.State == EnumClientState.Queued) > 0)
		{
			reason = "players online";
			return true;
		}

		if (config.PauseBelowTps > 0 && TryGetRecentTps(server, out decimal tps) && tps < (decimal)config.PauseBelowTps)
		{
			reason = "tps below " + config.PauseBelowTps.ToString("0.##", CultureInfo.InvariantCulture);
			return true;
		}

		if (IsQueuePressureHigh(server, config))
		{
			reason = "chunk queue pressure";
			return true;
		}

		int loadedChunkColumns = server.WorldMap.ChunkMapSizeY <= 0 ? 0 : server.loadedChunks.Count / server.WorldMap.ChunkMapSizeY;
		if (loadedChunkColumns >= config.MaxLoadedChunkColumns)
		{
			reason = "loaded chunk pressure";
			return true;
		}

		reason = null;
		return false;
	}

	private bool IsQueuePressureHigh(ServerMain server, StratumPregenConfig config)
	{
		int pendingColumns;
		lock (server.requestedChunkColumnsLock)
		{
			pendingColumns = server.requestedChunkColumns.Count;
		}

		int workerColumns = server.chunkThread?.requestedChunkColumns.Count ?? 0;
		return pendingColumns >= config.MaxPendingColumnQueue || workerColumns >= config.MaxWorkerColumnQueue;
	}

	private static bool TryGetRecentTps(ServerMain server, out decimal tps)
	{
		StatsCollection stats = server.StatsCollector[GameMath.Mod(server.StatsCollectorIndex - 1, server.StatsCollector.Length)];
		if (stats.ticksTotal <= 0)
		{
			tps = 0;
			return false;
		}

		tps = decimal.Round((decimal)stats.ticksTotal / 2m, 2);
		return true;
	}

	private bool HasMoreColumns()
	{
		return nextChunkZ <= maxChunkZ;
	}

	private void AdvanceCursor()
	{
		nextChunkX++;
		if (nextChunkX > maxChunkX)
		{
			nextChunkX = minChunkX;
			nextChunkZ++;
		}
	}

	private void MaybeLogProgress(ServerMain server)
	{
		StratumPregenConfig config = StratumRuntime.Config.Performance.Pregen;
		if (config.ProgressLogIntervalSeconds <= 0 || server.ElapsedMilliseconds - lastProgressLogMilliseconds < config.ProgressLogIntervalSeconds * 1000L)
		{
			return;
		}

		lastProgressLogMilliseconds = server.ElapsedMilliseconds;
		StratumRuntime.LogInfo("pregen progress: " + BuildOneLineStatus(server));
	}

	private string BuildStatus(ServerMain server)
	{
		StringBuilder output = new StringBuilder("Stratum Pregen\n");
		output.Append(BuildOneLineStatus(server)).Append('\n');
		output.Append("Area: ").Append(mode).Append(' ').Append(minChunkX).Append(',').Append(minChunkZ).Append(" to ").Append(maxChunkX).Append(',').Append(maxChunkZ).Append(" columns=").Append(totalColumns).Append('\n');
		output.Append("Progress: inspected=").Append(inspectedColumns).Append('/').Append(totalColumns).Append(" queued=").Append(queuedColumns).Append(" completed=").Append(completedColumns).Append(" active=").Append(activeRequests.Count).Append('\n');
		output.Append("Skipped: loaded=").Append(skippedLoadedColumns).Append(" invalid=").Append(skippedInvalidColumns).Append(" alreadyQueued=").Append(alreadyQueuedColumns).Append('\n');
		output.Append("Pressure pauses: ").Append(pressurePauses).Append(" last=").Append(lastPauseReason).Append('\n');
		output.Append("Config: rate=").Append(StratumRuntime.Config.Performance.Pregen.MaxColumnsPerSecond).Append("/s queues=").Append(StratumRuntime.Config.Performance.Pregen.MaxPendingColumnQueue).Append('/').Append(StratumRuntime.Config.Performance.Pregen.MaxWorkerColumnQueue).Append(" loadedColumns=").Append(StratumRuntime.Config.Performance.Pregen.MaxLoadedChunkColumns).Append('\n');
		return output.ToString();
	}

	private string BuildOneLineStatus(ServerMain server)
	{
		long elapsedMilliseconds = (running || completed) ? Math.Max(0, (finishedAtMilliseconds >= 0 ? finishedAtMilliseconds : server.ElapsedMilliseconds) - startedAtMilliseconds) : 0;
		decimal percent = totalColumns <= 0 ? 0 : decimal.Round((decimal)Math.Min(inspectedColumns, totalColumns) * 100m / totalColumns, 2);
		string eta = FormatEta(elapsedMilliseconds);
		return $"{ShortStatus} {percent}% elapsed={FormatDuration(elapsedMilliseconds)} eta={eta}";
	}

	private string FormatEta(long elapsedMilliseconds)
	{
		if (!running || inspectedColumns <= 0 || totalColumns <= 0)
		{
			return "n/a";
		}

		double columnsPerMillisecond = inspectedColumns / (double)Math.Max(1, elapsedMilliseconds);
		long remainingColumns = Math.Max(0, totalColumns - inspectedColumns);
		long etaMilliseconds = (long)(remainingColumns / Math.Max(columnsPerMillisecond, 0.000001));
		return FormatDuration(etaMilliseconds);
	}

	private static string FormatDuration(long milliseconds)
	{
		TimeSpan time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
		if (time.TotalHours >= 1)
		{
			return ((int)time.TotalHours).ToString(CultureInfo.InvariantCulture) + "h " + time.Minutes.ToString(CultureInfo.InvariantCulture) + "m";
		}

		if (time.TotalMinutes >= 1)
		{
			return time.Minutes.ToString(CultureInfo.InvariantCulture) + "m " + time.Seconds.ToString(CultureInfo.InvariantCulture) + "s";
		}

		return time.Seconds.ToString(CultureInfo.InvariantCulture) + "s";
	}

	private static bool TryParseInt(string value, out int result)
	{
		return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
	}

	private const string Usage = "Usage: /stratum pregen [status|pause|resume|stop|start radius &lt;radiusChunks&gt; [centerChunkX] [centerChunkZ]|start rect &lt;chunkX1&gt; &lt;chunkZ1&gt; &lt;chunkX2&gt; &lt;chunkZ2&gt;]";
}