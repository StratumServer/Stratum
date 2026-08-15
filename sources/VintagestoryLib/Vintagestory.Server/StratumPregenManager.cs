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

	// Block-major cursor: the area is traversed as NxN chunk blocks,
	// row of blocks by row of blocks; inside a block the N*N chunks go together.
	private int nextBlockX;
	private int nextBlockZ;
	private int blockInner; // 0..(N*N-1) position inside the current block

	// fields to support the circle shape
	private int centerX;
	private int centerZ;
	private long radiusSq;

	// Storing original TTL values for restoring them after pregen
	private int originalUncompressedChunkTTL;
	private long originalCompressedChunkTTL;
	private bool ttlBoosted = false;

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

	private long lastTickMilliseconds = -1;
	private float columnBudgetAccumulator;
	private float scanBudgetAccumulator;

	/// <summary>
	/// Size of a block-major processing unit: N x N chunks processed together.
	/// </summary>
	private const int BlockSize = 3; // N x N chunks per block

	/// <summary>
	/// Returns the current short status string (one of: "idle", "running", "paused", "complete").
	/// </summary>
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

	/// <summary>
	/// Handles a /stratum pregen command, dispatching to status/start/pause/resume/stop.
	/// </summary>
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

			RestoreTtl(); // restore TTL

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

			ApplyTtlBoost();    // reduce TTL

			return TextCommandResult.Success(BuildStatus(server));
		}

		if (string.Equals(action, "stop", StringComparison.OrdinalIgnoreCase))
		{
			Stop(server, "stopped by command");
			return TextCommandResult.Success(BuildStatus(server));
		}

		return TextCommandResult.Error(Usage);
	}

	/// <summary>
	/// Temporarily reduces chunk TTL to speed up unloading during pregen.
	/// Original values are saved for later restoration.
	/// Divides by 5, but never below 1000 ms (1 second) to prevent the server from going crazy,
	// trying to dump chunks to disk every tick.
	/// </summary>
	private void ApplyTtlBoost()
	{
		if (ttlBoosted) return;

		originalUncompressedChunkTTL = MagicNum.UncompressedChunkTTL;
		originalCompressedChunkTTL = MagicNum.CompressedChunkTTL;

		int newUncompressed = Math.Max(1000, originalUncompressedChunkTTL / 5);
		long newCompressed = Math.Max(1000, originalCompressedChunkTTL / 5);

		MagicNum.UncompressedChunkTTL = newUncompressed;
		MagicNum.CompressedChunkTTL = newCompressed;

		ttlBoosted = true;
		StratumRuntime.LogInfo($"Chunk TTL boosted for faster unload: Uncompressed {originalUncompressedChunkTTL} -> {newUncompressed}ms, Compressed {originalCompressedChunkTTL} -> {newCompressed}ms");
	}

	/// <summary>
	/// Restores the original chunk TTL values that were saved before boosting.
	/// No-op if no boost was ever applied.
	/// </summary>
	private void RestoreTtl()
	{
		if (!ttlBoosted) return;

		MagicNum.UncompressedChunkTTL = originalUncompressedChunkTTL;
		MagicNum.CompressedChunkTTL = originalCompressedChunkTTL;

		ttlBoosted = false;
		StratumRuntime.LogInfo($"Chunk TTL restored: Uncompressed {originalUncompressedChunkTTL}ms, Compressed {originalCompressedChunkTTL}ms");
	}

	/// <summary>
	/// Main tick handler for the pregen manager. Accumulates per-tick budgets for column
	/// inspections and scans, respects pressure-based pauses (players online, low TPS,
	/// queue saturation), applies TTL boost while running, and drives chunk generation.
	/// </summary>
	public void Tick(ServerMain server)
	{
		if (!running)
		{
			if (ttlBoosted)
				RestoreTtl();   // restore TTL
			lastTickMilliseconds = -1; // reset so next start gets a fresh dt
			return;
		}

		if (paused)
		{
			if (ttlBoosted)
				RestoreTtl();   // restore TTL
			lastTickMilliseconds = -1;
			return;
		}

		// Calculate the real elapsed time since last tick.
		long now = server.ElapsedMilliseconds;
		float dtSeconds;
		if (lastTickMilliseconds < 0)
		{
			// First tick after start/resume: use a nominal 50 ms to avoid huge jumps.
			dtSeconds = 0.05f;
		}
		else
		{
			dtSeconds = (now - lastTickMilliseconds) / 1000f;
		}
		lastTickMilliseconds = now;

		// Anomaly protection: clamp dt to prevent huge budgets after sleep, debugger attach, or GC pause > 2 s.
		dtSeconds = Math.Clamp(dtSeconds, 0.001f, 2.0f);

		StratumPregenConfig config = StratumRuntime.Config.Performance.Pregen;
		config.EnsureSane();
		UpdateActiveRequests(server);
		if (!config.Enabled)
		{
			pressurePauses++;
			lastPauseReason = "disabled in config";
			if (ttlBoosted)
				RestoreTtl();
			return;
		}

		if (TryGetPressureReason(server, config, out string reason))
		{
			pressurePauses++;
			lastPauseReason = reason;
			MaybeLogProgress(server);
			return;
		}

		if (!ttlBoosted)
		{
			ApplyTtlBoost();
		}

		lastPauseReason = "none";

		// Time-scaled budget for this tick.
		columnBudgetAccumulator += config.MaxColumnsPerSecond * dtSeconds;
		scanBudgetAccumulator += config.MaxScansPerSecond * dtSeconds;

		int maxColumnsThisTick = (int)columnBudgetAccumulator;
		int maxScansThisTick = (int)scanBudgetAccumulator;

		// Consume the accumulated budget and cap remaining float to prevent unbounded growth.
		columnBudgetAccumulator -= maxColumnsThisTick;
		scanBudgetAccumulator -= maxScansThisTick;
		columnBudgetAccumulator = Math.Min(columnBudgetAccumulator, config.MaxColumnsPerSecond);
		scanBudgetAccumulator = Math.Min(scanBudgetAccumulator, config.MaxScansPerSecond);

		// If no full unit has accumulated yet, skip work this tick.
		if (maxColumnsThisTick <= 0 || maxScansThisTick <= 0)
		{
			MaybeLogProgress(server);
			return;
		}

		int queuedThisTick = 0;
		int scannedThisTick = 0;
		while (queuedThisTick < maxColumnsThisTick
			   && scannedThisTick < maxScansThisTick
			   && HasMoreColumns())
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

	/// <summary>
	/// Parses the /stratum pregen start command and dispatches to StartRadius or StartRect based on shape.
	/// </summary>
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

	/// <summary>
	/// Starts a circular pregen job with the given radius. The center defaults to the caller's position
	/// (if available) or the world centre.
	/// </summary>
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

		int minChunkX = centerChunkX - radiusChunks;
		int maxChunkX = centerChunkX + radiusChunks;
		int minChunkZ = centerChunkZ - radiusChunks;
		int maxChunkZ = centerChunkZ + radiusChunks;

		return Start(server, "radius", minChunkX, minChunkZ, maxChunkX, maxChunkZ, config, centerChunkX, centerChunkZ, radiusChunks);
	}

	/// <summary>
	/// Starts a rectangular pregen job defined by two corner chunk coordinates.
	/// </summary>
	private TextCommandResult StartRect(ServerMain server, TextCommandCallingArgs args, StratumPregenConfig config)
	{
		if (!TryParseInt(args[3] as string, out int x1) || !TryParseInt(args[4] as string, out int z1) || !TryParseInt(args[5] as string, out int x2) || !TryParseInt(args[6] as string, out int z2))
		{
			return TextCommandResult.Error("Usage: /stratum pregen start rect &lt;chunkX1&gt; &lt;chunkZ1&gt; &lt;chunkX2&gt; &lt;chunkZ2&gt;");
		}

		return Start(server, "rect", x1, z1, x2, z2, config);
	}

	/// <summary>
	/// Initializes and starts a pregen job with the given boundaries, shape mode, and optional circle parameters.
	/// Normalizes coordinates so that (x1,z1) may be supplied in any order.
	/// </summary>
	private TextCommandResult Start(ServerMain server, string newMode, int x1, int z1, int x2, int z2, StratumPregenConfig config, int centerX = 0, int centerZ = 0, int radius = 0)
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

		// Block-major cursor initialization.
		nextBlockX = normalizedMinX;
		nextBlockZ = normalizedMinZ;
		blockInner = 0;

		// Accurate chunk count based on the selected shape.
		if (newMode == "radius")
		{
			this.centerX = centerX;
			this.centerZ = centerZ;
			this.radiusSq = (long)radius * radius;

			long count = 0;
			for (int dx = 0; dx <= radius; dx++)
			{
				long rem = this.radiusSq - (long)dx * dx;
				int maxDz = (int)Math.Sqrt(rem);

				// Correction for potential floating-point precision issues in Math.Sqrt.
				while ((long)(maxDz + 1) * (maxDz + 1) <= rem) maxDz++;
				while ((long)maxDz * maxDz > rem) maxDz--;

				if (dx == 0) count += 2 * maxDz + 1;
				else count += 2 * (2 * maxDz + 1);
			}
			totalColumns = count;
		}
		else
		{
			totalColumns = area;
		}

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
		StratumRuntime.LogInfo($"pregen started: {mode} chunks {minChunkX},{minChunkZ} to {maxChunkX},{maxChunkZ} ({totalColumns} columns, {BlockSize}x{BlockSize} blocks)");
		ApplyTtlBoost();    // reduce TTL
		return TextCommandResult.Success(BuildStatus(server));
	}

	/// <summary>
	/// Stops the current pregen job and marks it as non-running. The provided reason is recorded in logs.
	/// </summary>
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

		RestoreTtl();   // restore TTL
		StratumRuntime.LogInfo("pregen stopped: " + reason);
	}

	/// <summary>
	/// Marks the current pregen job as completed, records timing statistics and per-stage breakdown.
	/// </summary>
	private void Complete(ServerMain server)
	{
		running = false;
		paused = false;
		completed = true;
		finishedAtMilliseconds = server.ElapsedMilliseconds;
		lastPauseReason = "none";

		RestoreTtl();   // restore TTL

		// Calculate the time spent.
		long elapsedMilliseconds = Math.Max(0, finishedAtMilliseconds - startedAtMilliseconds);
		string duration = FormatDuration(elapsedMilliseconds);

		StratumRuntime.LogInfo($"pregen complete in {duration}: inspected={inspectedColumns}/{totalColumns} queued={queuedColumns} skippedLoaded={skippedLoadedColumns} skippedInvalid={skippedInvalidColumns}");

		// Stratum: per-stage breakdown, points at the next stage worth splitting
		// instead of guessing from the generator list.
		string stageReport = server.chunkThread?.loadsavechunks?.GetStageTimingsReport();
		if (!string.IsNullOrEmpty(stageReport))
		{
			StratumRuntime.LogInfo($"pregen stage timings: {stageReport}");
		}
	}

	/// <summary>
	/// Inspects the next column according to the block-major cursor. Skips chunks outside
	/// the configured shape, those already loaded or queued, and queues new ones when possible.
	/// Returns true if a column was inspected during this call (or false if no more columns remain).
	/// </summary>
	private bool TryInspectNextColumn(ServerMain server, out bool queued)
	{
		queued = false;

		// Iterate through chunks, skipping those outside the configured shape.
		while (HasMoreColumns())
		{
			int chunkX = nextBlockX + (blockInner % BlockSize);
			int chunkZ = nextBlockZ + (blockInner / BlockSize);
			AdvanceCursor();

			// Partial block at the right/bottom edge of a rect:
			// inner cells beyond the area are silently skipped.
			if (chunkX > maxChunkX || chunkZ > maxChunkZ)
			{
				continue;
			}

			if (mode == "radius")
			{
				long dx = chunkX - centerX;
				long dz = chunkZ - centerZ;
				// If a chunk is outside the radius, skip it (do not count it in inspectedColumns).
				if (dx * dx + dz * dz > radiusSq)
				{
					continue;
				}
			}

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

		return false;
	}

	/// <summary>
	/// Attempts to enqueue a chunk column for generation. Returns true on success, false if the
	/// column is already requested or an exception occurs during enqueue (in which case it rolls back).
	/// </summary>
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

	/// <summary>
	/// Removes completed column requests from the active-requests set and increments the completed-columns counter.
	/// A request is considered complete when it no longer appears in either the server-side queue or the worker thread's pending set.
	/// </summary>
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

	/// <summary>
	/// Checks whether a given chunk column is currently queued for generation either on the server-side
	/// or inside the worker thread's requested set.
	/// </summary>
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

	/// <summary>
	/// Determines whether pregen should be paused due to server pressure conditions.
	/// Checks (in order): players online, TPS below threshold, chunk queue saturation, and loaded-chunk count.
	/// Returns true with a descriptive reason if any condition is met.
	/// </summary>
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

	/// <summary>
	/// Returns true if the chunk-generation queue is under high pressure — i.e. pending columns exceed configured limits,
	/// or worker-side columns approach physical capacity (minus a safety margin for competing player requests).
	/// </summary>
	private bool IsQueuePressureHigh(ServerMain server, StratumPregenConfig config)
	{
		int pendingColumns;
		lock (server.requestedChunkColumnsLock)
		{
			pendingColumns = server.requestedChunkColumns.Count;
		}

		var workerQueue = server.chunkThread?.requestedChunkColumns;
		int workerColumns = workerQueue?.Count ?? 0;
		int workerCapacity = workerQueue?.Capacity ?? int.MaxValue;

		// Leave a reserve for competitive requests from players (the same 200 as in moveRequestsToGeneratingQueue).
		int safetyMargin = 300;

		return pendingColumns >= config.MaxPendingColumnQueue
			   || workerColumns >= config.MaxWorkerColumnQueue
			   || workerColumns >= workerCapacity - safetyMargin;   // real physical limit
	}

	/// <summary>
	/// Reads the recent TPS (ticks per second) from the stats collector. Returns false if no valid stats are available.
	/// </summary>
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

	/// <summary>
	/// Returns true if there are still unchecked column positions remaining in the block-major cursor.
	/// </summary>
	private bool HasMoreColumns()
	{
		return nextBlockZ <= maxChunkZ;
	}

	/// <summary>
	/// Advances the block-major cursor by one chunk position. When a full NxN block is exhausted, wraps
	/// to the next row (incrementing nextBlockZ) or rolls back to the left edge and increments the Z-row.
	/// </summary>
	private void AdvanceCursor()
	{
		blockInner++;
		if (blockInner >= BlockSize * BlockSize)
		{
			blockInner = 0;
			nextBlockX += BlockSize;
			if (nextBlockX > maxChunkX)
			{
				nextBlockX = minChunkX;
				nextBlockZ += BlockSize;
			}
		}
	}

	/// <summary>
	/// Logs a one-line progress snapshot if the configured progress-log interval has elapsed since the last log.
	/// </summary>
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

	/// <summary>
	/// Builds a multi-line human-readable status report for the /stratum pregen status command.
	/// </summary>
	private string BuildStatus(ServerMain server)
	{
		StringBuilder output = new StringBuilder("Stratum Pregen\n");
		output.Append(BuildOneLineStatus(server)).Append('\n');
		output.Append("Area: ").Append(mode).Append(' ').Append(minChunkX).Append(',').Append(minChunkZ).Append(" to ").Append(maxChunkX).Append(',').Append(maxChunkZ).Append(" columns=").Append(totalColumns).Append('\n');
		output.Append("Progress: inspected=").Append(inspectedColumns).Append('/').Append(totalColumns).Append(" queued=").Append(queuedColumns).Append(" completed=").Append(completedColumns).Append(" active=").Append(activeRequests.Count).Append('\n');
		output.Append("Skipped: loaded=").Append(skippedLoadedColumns).Append(" invalid=").Append(skippedInvalidColumns).Append(" alreadyQueued=").Append(alreadyQueuedColumns).Append('\n');
		output.Append("Pressure pauses: ").Append(pressurePauses).Append(" last=").Append(lastPauseReason).Append('\n');
		output.Append("Config: rate=").Append(StratumRuntime.Config.Performance.Pregen.MaxColumnsPerSecond).Append("/s queues=").Append(StratumRuntime.Config.Performance.Pregen.MaxPendingColumnQueue).Append('/').Append(StratumRuntime.Config.Performance.Pregen.MaxWorkerColumnQueue).Append(" loadedColumns=").Append(StratumRuntime.Config.Performance.Pregen.MaxLoadedChunkColumns).Append('\n');
		output.Append("Commands: /stratum pregen start radius &lt;radiusChunks&gt; [centerChunkX] [centerChunkZ], /stratum pregen start rect &lt;chunkX1&gt; &lt;chunkZ1&gt; &lt;chunkX2&gt; &lt;chunkZ2&gt;, /stratum pregen pause|resume|stop|status");
		return output.ToString();
	}

	/// <summary>
	/// Builds a single-line status string containing short status, progress percentage, elapsed time, and ETA.
	/// </summary>
	private string BuildOneLineStatus(ServerMain server)
	{
		long elapsedMilliseconds = (running || completed) ? Math.Max(0, (finishedAtMilliseconds >= 0 ? finishedAtMilliseconds : server.ElapsedMilliseconds) - startedAtMilliseconds) : 0;
		decimal percent = totalColumns <= 0 ? 0 : decimal.Round((decimal)Math.Min(inspectedColumns, totalColumns) * 100m / totalColumns, 2);
		string eta = FormatEta(elapsedMilliseconds);
		return $"{ShortStatus} {percent}% elapsed={FormatDuration(elapsedMilliseconds)} eta={eta}";
	}

	/// <summary>
	/// Estimates the remaining time until pregen completes based on current throughput.
	/// Returns "n/a" when no progress has been made or the job is not running.
	/// </summary>
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

	/// <summary>
	/// Formats a duration in milliseconds into a human-readable string such as "2h 30m", "5m 12s", or "45s".
	/// </summary>
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

	/// <summary>
	/// Safely parses a string to an int using invariant culture (no thousands separators, no locale quirks).
	/// </summary>
	private static bool TryParseInt(string value, out int result)
	{
		return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
	}

	private const string Usage = "Usage: /stratum pregen [status|pause|resume|stop|start radius &lt;radiusChunks&gt; [centerChunkX] [centerChunkZ]|start rect &lt;chunkX1&gt; &lt;chunkZ1&gt; &lt;chunkX2&gt; &lt;chunkZ2&gt;]";
}