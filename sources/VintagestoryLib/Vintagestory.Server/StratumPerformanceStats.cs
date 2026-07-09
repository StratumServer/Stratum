using System;

namespace Vintagestory.Server;

internal sealed class StratumPerformanceStats
{
	private readonly object gate = new object();
	private long totalChunkSendTicks;
	private long totalChunksSent;
	private long totalDeferredClients;
	private long totalColumnRequests;
	private long totalCancelledColumnRequests;
	private long totalPrioritizedChunkRings;
	private long totalGenerationDeferredClients;
	private long totalSkippedClientChunkCap;
	private long totalSkippedServerChunkCap;
	private long totalSkippedOutboundPressure;
	private long totalNearRingChunkSends;
	private long totalFarRingChunkSends;
	private long totalEntityTicks;
	private long totalEntitiesSeen;
	private long totalEntitiesTicked;
	private long totalEntitiesThrottled;
	private long totalPlayerEntitiesTicked;
	private long totalCreatureEntitiesTicked;
	private long totalInanimateEntitiesTicked;
	private long totalDimensionSkippedEntities;
	private long totalEntitySelectionTicks;
	private long totalEntitySelectionTraces;
	private long totalEntityLookedAtBlocks;
	private long totalAutoSaveDelays;
	private long totalAutoSavesCompleted;
	private long totalIncrementalSaveFlushes;
	private long totalIncrementalLoadedChunksSaved;
	private long totalIncrementalMapChunksSaved;
	private long totalBlockTickPasses;
	private long totalBlockTickChunksTicked;
	private long totalBlockTickChunksDeferred;
	private long totalRandomTickAttempts;
	private long totalMainThreadBlockTicks;
	private long totalBlockGameTickListenersReady;
	private long totalBlockGameTickListenersTriggered;
	private long totalBlockGameTickListenersSkipped;
	private int lastChunkBudget;
	private int lastChunksSent;
	private int lastDeferredClients;
	private int lastColumnRequestBudget;
	private int lastColumnRequests;
	private int lastCancelledColumnRequests;
	private int lastPrioritizedChunkRings;
	private int lastGenerationDeferredClients;
	private int lastPendingColumnRequests;
	private int lastWorkerColumnRequests;
	private int lastSkippedClientChunkCap;
	private int lastSkippedServerChunkCap;
	private int lastSkippedOutboundPressure;
	private int lastNearRingChunkSends;
	private int lastFarRingChunkSends;
	private int lastTrackedColumnRequests;
	private int lastWantedByColumnLinks;
	private int lastSharedColumnRequests;
	private int lastWorkerTrackedColumnRequests;
	private int peakChunksSent;
	private int peakDeferredClients;
	private int peakColumnRequests;
	private int peakCancelledColumnRequests;
	private int peakGenerationDeferredClients;
	private int peakPendingColumnRequests;
	private int peakWorkerColumnRequests;
	private int peakTrackedColumnRequests;
	private int peakSharedColumnRequests;
	private int lastEntitiesSeen;
	private int lastEntitiesTicked;
	private int lastEntitiesThrottled;
	private int lastPlayerEntitiesTicked;
	private int lastCreatureEntitiesTicked;
	private int lastInanimateEntitiesTicked;
	private int lastDimensionSkippedEntities;
	private int peakEntitiesSeen;
	private int peakEntitiesThrottled;
	private int lastEntitySelectionTraces;
	private int lastEntityLookedAtBlocks;
	private int peakEntitySelectionTraces;
	private int lastAutoSavePendingColumnRequests;
	private int lastAutoSaveWorkerColumnRequests;
	private int lastAutoSaveDelaySeconds;
	private int peakAutoSaveDelaySeconds;
	private bool lastAutoSaveWasDelayed;
	private int lastIncrementalLoadedChunksSaved;
	private int lastIncrementalMapChunksSaved;
	private int lastIncrementalLoadedChunksScanned;
	private int lastIncrementalMapChunksScanned;
	private int lastIncrementalLoadedChunkScanSize;
	private int lastIncrementalMapChunkScanSize;
	private int peakIncrementalLoadedChunksSaved;
	private int peakIncrementalMapChunksSaved;
	private int lastBlockTickChunksSeen;
	private int lastBlockTickChunksTicked;
	private int lastBlockTickChunksDeferred;
	private int lastRandomTickAttempts;
	private int lastRandomTickRangeChunks;
	private int lastQueuedBlockTicksBefore;
	private int lastQueuedBlockTicksExecuted;
	private int lastQueuedBlockTicksAfter;
	private int lastBlockGameTickListenersReady;
	private int lastBlockGameTickListenersTriggered;
	private int lastBlockGameTickListenersSkipped;
	private int peakBlockTickChunksSeen;
	private int peakBlockTickChunksDeferred;
	private int peakQueuedBlockTicksAfter;
	private int peakBlockGameTickListenersSkipped;
	private int lastTcpFallbackClients;
	private int peakTcpFallbackClients;

	public void RecordChunkSendTick(int chunkBudget, int chunksSent, int deferredClients, int columnRequestBudget, int columnRequests, int generationDeferredClients, int pendingColumnRequests, int workerColumnRequests, int cancelledColumnRequests, int prioritizedChunkRings, int skippedClientChunkCap, int skippedServerChunkCap, int skippedOutboundPressure, int nearRingChunkSends, int farRingChunkSends, int trackedColumnRequests, int wantedByColumnLinks, int sharedColumnRequests, int workerTrackedColumnRequests)
	{
		lock (gate)
		{
			totalChunkSendTicks++;
			totalChunksSent += chunksSent;
			totalDeferredClients += deferredClients;
			totalColumnRequests += columnRequests;
			totalCancelledColumnRequests += cancelledColumnRequests;
			totalPrioritizedChunkRings += prioritizedChunkRings;
			totalGenerationDeferredClients += generationDeferredClients;
			totalSkippedClientChunkCap += skippedClientChunkCap;
			totalSkippedServerChunkCap += skippedServerChunkCap;
			totalSkippedOutboundPressure += skippedOutboundPressure;
			totalNearRingChunkSends += nearRingChunkSends;
			totalFarRingChunkSends += farRingChunkSends;
			lastChunkBudget = chunkBudget;
			lastChunksSent = chunksSent;
			lastDeferredClients = deferredClients;
			lastColumnRequestBudget = columnRequestBudget;
			lastColumnRequests = columnRequests;
			lastCancelledColumnRequests = cancelledColumnRequests;
			lastPrioritizedChunkRings = prioritizedChunkRings;
			lastGenerationDeferredClients = generationDeferredClients;
			lastPendingColumnRequests = pendingColumnRequests;
			lastWorkerColumnRequests = workerColumnRequests;
			lastSkippedClientChunkCap = skippedClientChunkCap;
			lastSkippedServerChunkCap = skippedServerChunkCap;
			lastSkippedOutboundPressure = skippedOutboundPressure;
			lastNearRingChunkSends = nearRingChunkSends;
			lastFarRingChunkSends = farRingChunkSends;
			lastTrackedColumnRequests = trackedColumnRequests;
			lastWantedByColumnLinks = wantedByColumnLinks;
			lastSharedColumnRequests = sharedColumnRequests;
			lastWorkerTrackedColumnRequests = workerTrackedColumnRequests;
			peakChunksSent = Math.Max(peakChunksSent, chunksSent);
			peakDeferredClients = Math.Max(peakDeferredClients, deferredClients);
			peakColumnRequests = Math.Max(peakColumnRequests, columnRequests);
			peakCancelledColumnRequests = Math.Max(peakCancelledColumnRequests, cancelledColumnRequests);
			peakGenerationDeferredClients = Math.Max(peakGenerationDeferredClients, generationDeferredClients);
			peakPendingColumnRequests = Math.Max(peakPendingColumnRequests, pendingColumnRequests);
			peakWorkerColumnRequests = Math.Max(peakWorkerColumnRequests, workerColumnRequests);
			peakTrackedColumnRequests = Math.Max(peakTrackedColumnRequests, trackedColumnRequests);
			peakSharedColumnRequests = Math.Max(peakSharedColumnRequests, sharedColumnRequests);
		}
	}

	public void RecordEntityTick(int entitiesSeen, int entitiesTicked, int entitiesThrottled, int playerEntitiesTicked = 0, int creatureEntitiesTicked = 0, int inanimateEntitiesTicked = 0, int dimensionSkippedEntities = 0)
	{
		lock (gate)
		{
			totalEntityTicks++;
			totalEntitiesSeen += entitiesSeen;
			totalEntitiesTicked += entitiesTicked;
			totalEntitiesThrottled += entitiesThrottled;
			totalPlayerEntitiesTicked += playerEntitiesTicked;
			totalCreatureEntitiesTicked += creatureEntitiesTicked;
			totalInanimateEntitiesTicked += inanimateEntitiesTicked;
			totalDimensionSkippedEntities += dimensionSkippedEntities;
			lastEntitiesSeen = entitiesSeen;
			lastEntitiesTicked = entitiesTicked;
			lastEntitiesThrottled = entitiesThrottled;
			lastPlayerEntitiesTicked = playerEntitiesTicked;
			lastCreatureEntitiesTicked = creatureEntitiesTicked;
			lastInanimateEntitiesTicked = inanimateEntitiesTicked;
			lastDimensionSkippedEntities = dimensionSkippedEntities;
			peakEntitiesSeen = Math.Max(peakEntitiesSeen, entitiesSeen);
			peakEntitiesThrottled = Math.Max(peakEntitiesThrottled, entitiesThrottled);
		}
	}

	public void RecordEntitySelectionTick(int selectionTraces, int lookedAtBlocks)
	{
		lock (gate)
		{
			totalEntitySelectionTicks++;
			totalEntitySelectionTraces += selectionTraces;
			totalEntityLookedAtBlocks += lookedAtBlocks;
			lastEntitySelectionTraces = selectionTraces;
			lastEntityLookedAtBlocks = lookedAtBlocks;
			peakEntitySelectionTraces = Math.Max(peakEntitySelectionTraces, selectionTraces);
		}
	}

	public void RecordNetworkStats(int tcpFallbackClients)
	{
		lock (gate)
		{
			lastTcpFallbackClients = tcpFallbackClients;
			peakTcpFallbackClients = Math.Max(peakTcpFallbackClients, tcpFallbackClients);
		}
	}

	public void RecordAutoSaveDelayed(int pendingColumnRequests, int workerColumnRequests, int delaySeconds)
	{
		lock (gate)
		{
			totalAutoSaveDelays++;
			lastAutoSaveWasDelayed = true;
			lastAutoSavePendingColumnRequests = pendingColumnRequests;
			lastAutoSaveWorkerColumnRequests = workerColumnRequests;
			lastAutoSaveDelaySeconds = delaySeconds;
			peakAutoSaveDelaySeconds = Math.Max(peakAutoSaveDelaySeconds, delaySeconds);
		}
	}

	public void RecordAutoSaveCompleted(int delaySeconds)
	{
		lock (gate)
		{
			totalAutoSavesCompleted++;
			lastAutoSaveWasDelayed = delaySeconds > 0;
			lastAutoSaveDelaySeconds = delaySeconds;
			peakAutoSaveDelaySeconds = Math.Max(peakAutoSaveDelaySeconds, delaySeconds);
		}
	}

	public void RecordIncrementalSaveFlush(int loadedChunksSaved, int mapChunksSaved, int loadedChunksScanned, int mapChunksScanned, int loadedChunkScanSize, int mapChunkScanSize)
	{
		lock (gate)
		{
			totalIncrementalSaveFlushes++;
			totalIncrementalLoadedChunksSaved += loadedChunksSaved;
			totalIncrementalMapChunksSaved += mapChunksSaved;
			lastIncrementalLoadedChunksSaved = loadedChunksSaved;
			lastIncrementalMapChunksSaved = mapChunksSaved;
			lastIncrementalLoadedChunksScanned = loadedChunksScanned;
			lastIncrementalMapChunksScanned = mapChunksScanned;
			lastIncrementalLoadedChunkScanSize = loadedChunkScanSize;
			lastIncrementalMapChunkScanSize = mapChunkScanSize;
			peakIncrementalLoadedChunksSaved = Math.Max(peakIncrementalLoadedChunksSaved, loadedChunksSaved);
			peakIncrementalMapChunksSaved = Math.Max(peakIncrementalMapChunksSaved, mapChunksSaved);
		}
	}

	public void RecordBlockTickPass(int chunksSeen, int chunksTicked, int chunksDeferred, int randomTickAttempts, int randomTickRangeChunks)
	{
		lock (gate)
		{
			totalBlockTickPasses++;
			totalBlockTickChunksTicked += chunksTicked;
			totalBlockTickChunksDeferred += chunksDeferred;
			totalRandomTickAttempts += randomTickAttempts;
			lastBlockTickChunksSeen = chunksSeen;
			lastBlockTickChunksTicked = chunksTicked;
			lastBlockTickChunksDeferred = chunksDeferred;
			lastRandomTickAttempts = randomTickAttempts;
			lastRandomTickRangeChunks = randomTickRangeChunks;
			peakBlockTickChunksSeen = Math.Max(peakBlockTickChunksSeen, chunksSeen);
			peakBlockTickChunksDeferred = Math.Max(peakBlockTickChunksDeferred, chunksDeferred);
		}
	}

	public void RecordBlockGameTickListeners(int ready, int triggered, int skipped)
	{
		if (ready == 0 && triggered == 0 && skipped == 0)
		{
			return;
		}

		lock (gate)
		{
			totalBlockGameTickListenersReady += ready;
			totalBlockGameTickListenersTriggered += triggered;
			totalBlockGameTickListenersSkipped += skipped;
			lastBlockGameTickListenersReady = ready;
			lastBlockGameTickListenersTriggered = triggered;
			lastBlockGameTickListenersSkipped = skipped;
			peakBlockGameTickListenersSkipped = Math.Max(peakBlockGameTickListenersSkipped, skipped);
		}
	}

	public void RecordMainThreadBlockTicks(int queuedBefore, int executed, int queuedAfter)
	{
		lock (gate)
		{
			totalMainThreadBlockTicks += executed;
			lastQueuedBlockTicksBefore = queuedBefore;
			lastQueuedBlockTicksExecuted = executed;
			lastQueuedBlockTicksAfter = queuedAfter;
			peakQueuedBlockTicksAfter = Math.Max(peakQueuedBlockTicksAfter, queuedAfter);
		}
	}

	public string BuildReport()
	{
		StratumConfig config = StratumRuntime.Config;
		config.EnsurePopulated();
		StratumChunkSendingConfig chunkSending = config.Performance.ChunkSending;
		StratumChunkGenerationConfig chunkGeneration = config.Performance.ChunkGeneration;
		StratumChunkRequestManagementConfig chunkRequestManagement = config.Performance.ChunkRequestManagement;
		StratumPregenConfig pregen = config.Performance.Pregen;
		StratumEntityTickingConfig entityTicking = config.Performance.EntityTicking;
		StratumSimulationDistanceConfig simulationDistance = config.Performance.SimulationDistance;
		StratumAutoSaveConfig autoSave = config.Performance.AutoSave;
		StratumBlockTickConfig blockTicks = config.Performance.BlockTicks;

		lock (gate)
		{
			decimal averageChunks = totalChunkSendTicks <= 0 ? 0m : decimal.Round((decimal)totalChunksSent / totalChunkSendTicks, 2);
			decimal averageColumnRequests = totalChunkSendTicks <= 0 ? 0m : decimal.Round((decimal)totalColumnRequests / totalChunkSendTicks, 2);
			decimal averageEntitiesSeen = totalEntityTicks <= 0 ? 0m : decimal.Round((decimal)totalEntitiesSeen / totalEntityTicks, 2);
			decimal averageEntitiesThrottled = totalEntityTicks <= 0 ? 0m : decimal.Round((decimal)totalEntitiesThrottled / totalEntityTicks, 2);
			decimal averagePlayerEntitiesTicked = totalEntityTicks <= 0 ? 0m : decimal.Round((decimal)totalPlayerEntitiesTicked / totalEntityTicks, 2);
			decimal averageCreatureEntitiesTicked = totalEntityTicks <= 0 ? 0m : decimal.Round((decimal)totalCreatureEntitiesTicked / totalEntityTicks, 2);
			decimal averageInanimateEntitiesTicked = totalEntityTicks <= 0 ? 0m : decimal.Round((decimal)totalInanimateEntitiesTicked / totalEntityTicks, 2);
			decimal averageDimensionSkippedEntities = totalEntityTicks <= 0 ? 0m : decimal.Round((decimal)totalDimensionSkippedEntities / totalEntityTicks, 2);
			decimal averageEntitySelectionTraces = totalEntitySelectionTicks <= 0 ? 0m : decimal.Round((decimal)totalEntitySelectionTraces / totalEntitySelectionTicks, 2);
			decimal averageEntityLookedAtBlocks = totalEntitySelectionTicks <= 0 ? 0m : decimal.Round((decimal)totalEntityLookedAtBlocks / totalEntitySelectionTicks, 2);
			decimal averageBlockTickChunks = totalBlockTickPasses <= 0 ? 0m : decimal.Round((decimal)totalBlockTickChunksTicked / totalBlockTickPasses, 2);
			return "Stratum Performance\n" +
				"\nChunk Pipeline\n" +
				$"  Send: {(chunkSending.Enabled ? "on" : "off")} server={chunkSending.MaxChunksPerServerTick} client={chunkSending.MaxChunksPerClientTick} local={(chunkSending.IncludeLocalClients ? "yes" : "no")} adaptive={(chunkSending.AdaptiveUnderOverload ? "yes" : "no")} overload={chunkSending.OverloadTickMs}ms scale={chunkSending.OverloadScale:0.##}\n" +
				$"  Last: budget={lastChunkBudget} sent={lastChunksSent} deferredClients={lastDeferredClients}\n" +
				$"  Fairness: lastNearFar={lastNearRingChunkSends}/{lastFarRingChunkSends} totalNearFar={totalNearRingChunkSends}/{totalFarRingChunkSends} skippedClient={lastSkippedClientChunkCap} skippedServer={lastSkippedServerChunkCap} skippedPressure={lastSkippedOutboundPressure}\n" +
				$"  Totals: ticks={totalChunkSendTicks} sent={totalChunksSent} avg={averageChunks} peak={peakChunksSent} skippedClient={totalSkippedClientChunkCap} skippedServer={totalSkippedServerChunkCap} skippedPressure={totalSkippedOutboundPressure}\n" +
				$"  Generation: {(chunkGeneration.Enabled ? "on" : "off")} server={chunkGeneration.MaxColumnRequestsPerServerTick} client={chunkGeneration.MaxColumnRequestsPerClientTick} local={(chunkGeneration.IncludeLocalClients ? "yes" : "no")} adaptive={(chunkGeneration.AdaptiveUnderOverload ? "yes" : "no")} overload={chunkGeneration.OverloadTickMs}ms scale={chunkGeneration.OverloadScale:0.##}\n" +
				$"  Last gen: budget={lastColumnRequestBudget} requested={lastColumnRequests} queues={lastPendingColumnRequests}/{lastWorkerColumnRequests}\n" +
				$"  Gen totals: requested={totalColumnRequests} avg={averageColumnRequests} deferredClients={totalGenerationDeferredClients} peak={peakColumnRequests}\n" +
				$"  Requests: {(chunkRequestManagement.Enabled ? "on" : "off")} staleCancel={(chunkRequestManagement.CancelStalePendingRequests ? "yes" : "no")} forward={(chunkRequestManagement.PrioritizeMovingDirection ? "yes" : "no")} lastCancel={lastCancelledColumnRequests} peakCancel={peakCancelledColumnRequests} totalCancel={totalCancelledColumnRequests} prioritizedRings={totalPrioritizedChunkRings} lastPrioritized={lastPrioritizedChunkRings}\n" +
				$"  Wanted: tracked={lastTrackedColumnRequests} wantedBy={lastWantedByColumnLinks} shared={lastSharedColumnRequests} workerTracked={lastWorkerTrackedColumnRequests} peaks={peakTrackedColumnRequests}/{peakSharedColumnRequests}\n" +
				$"  Pregen: {(pregen.Enabled ? "on" : "off")} rate={pregen.MaxColumnsPerSecond}/s queues={pregen.MaxPendingColumnQueue}/{pregen.MaxWorkerColumnQueue} status={StratumRuntime.Pregen.ShortStatus}\n" +
				"\nEntity Ticking\n" +
				$"  Mode: {(entityTicking.Enabled ? "on" : "off")} far={entityTicking.FarEntityDistanceBlocks} interval={entityTicking.FarEntityTickInterval} movingExempt={(entityTicking.SkipMovingEntities ? "yes" : "no")}\n" +
				$"  Selection: last={lastEntitySelectionTraces} lookedAt={lastEntityLookedAtBlocks} avg={averageEntitySelectionTraces} avgLookedAt={averageEntityLookedAtBlocks} peak={peakEntitySelectionTraces}\n" +
				$"  Last: seen={lastEntitiesSeen} ticked={lastEntitiesTicked} players={lastPlayerEntitiesTicked} creatures={lastCreatureEntitiesTicked} inanimate={lastInanimateEntitiesTicked} dimSkipped={lastDimensionSkippedEntities} throttled={lastEntitiesThrottled}\n" +
				$"  Totals: ticks={totalEntityTicks} avgSeen={averageEntitiesSeen} avgPlayers={averagePlayerEntitiesTicked} avgCreatures={averageCreatureEntitiesTicked} avgInanimate={averageInanimateEntitiesTicked} avgDimSkipped={averageDimensionSkippedEntities} avgThrottled={averageEntitiesThrottled} peakThrottled={peakEntitiesThrottled}\n" +
				$"  Network: tcpFallback={lastTcpFallbackClients} peakTcpFallback={peakTcpFallbackClients}\n" +
				"\nAutosave\n" +
				$"  Smoothing: {(autoSave.Enabled ? "on" : "off")} maxDelay={autoSave.MaxDelaySeconds}s limits={autoSave.MaxPendingChunkColumns}/{autoSave.MaxWorkerChunkColumns}\n" +
				$"  Incremental: {(autoSave.IncrementalDirtyFlush ? "on" : "off")} interval={autoSave.IncrementalFlushIntervalSeconds}s loaded={autoSave.MaxLoadedChunksPerFlush}/{autoSave.MaxLoadedChunkScansPerFlush} map={autoSave.MaxMapChunksPerFlush}/{autoSave.MaxMapChunkScansPerFlush}\n" +
				$"  Last: delayed={(lastAutoSaveWasDelayed ? "yes" : "no")} delay={lastAutoSaveDelaySeconds}s queues={lastAutoSavePendingColumnRequests}/{lastAutoSaveWorkerColumnRequests}\n" +
				$"  Flush: lastLoaded={lastIncrementalLoadedChunksSaved}/{lastIncrementalLoadedChunksScanned}/{lastIncrementalLoadedChunkScanSize} lastMap={lastIncrementalMapChunksSaved}/{lastIncrementalMapChunksScanned}/{lastIncrementalMapChunkScanSize}\n" +
				$"  Totals: completed={totalAutoSavesCompleted} delayedChecks={totalAutoSaveDelays} peakDelay={peakAutoSaveDelaySeconds}s flushes={totalIncrementalSaveFlushes} saved={totalIncrementalLoadedChunksSaved}/{totalIncrementalMapChunksSaved} peaks={peakIncrementalLoadedChunksSaved}/{peakIncrementalMapChunksSaved}\n" +
				"\nBlock Ticks\n" +
				$"  Budgets: {(blockTicks.Enabled ? "on" : "off")} chunks={blockTicks.MaxChunksPerPass} randomPerChunk={blockTicks.MaxRandomTicksPerChunk} mainThread={blockTicks.MaxMainThreadBlockTicksPerPass}\n" +
				$"  Simulation: {(simulationDistance.Enabled ? "on" : "off")} randomRange={lastRandomTickRangeChunks} chunks listenerRange={simulationDistance.BlockGameTickListenerDistanceBlocks} forceLoaded={(simulationDistance.TickForceLoadedBlockListeners ? "yes" : "no")}\n" +
				$"  Last: chunks={lastBlockTickChunksTicked}/{lastBlockTickChunksSeen} deferred={lastBlockTickChunksDeferred} random={lastRandomTickAttempts} queue={lastQueuedBlockTicksBefore}->{lastQueuedBlockTicksAfter}\n" +
				$"  Listeners: last={lastBlockGameTickListenersTriggered}/{lastBlockGameTickListenersReady} skipped={lastBlockGameTickListenersSkipped} totalSkipped={totalBlockGameTickListenersSkipped} peakSkipped={peakBlockGameTickListenersSkipped}\n" +
				$"  Totals: passes={totalBlockTickPasses} avgChunks={averageBlockTickChunks} deferred={totalBlockTickChunksDeferred} mainThreadTicks={totalMainThreadBlockTicks}\n" +
				$"  Peaks: chunksSeen={peakBlockTickChunksSeen} deferred={peakBlockTickChunksDeferred} queueAfter={peakQueuedBlockTicksAfter}";
		}
	}

	public (bool AutoSaveDelayed, int AutoSaveDelaySeconds, int EntitiesThrottled, int SkippedOutboundPressure, int BlockListenersSkipped, int GenerationDeferredClients) DoctorSnapshot()
	{
		lock (gate)
		{
			return (lastAutoSaveWasDelayed, lastAutoSaveDelaySeconds, lastEntitiesThrottled, lastSkippedOutboundPressure, lastBlockGameTickListenersSkipped, lastGenerationDeferredClients);
		}
	}
}
