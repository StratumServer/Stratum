using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Vintagestory.GameContent;

// Server side manager for falling blocks (ported from the FallingSpawnManager mod).
// Caps the number of live EntityBlockFalling, queues requests over the cap, and resolves falls
// instantly for blocks that no player is near. Instance is null while the system is disabled
// in the config, and every caller falls back to vanilla behavior in that case.
//
// With a player nearby a request goes to the entity queue, which spawns an EntityBlockFalling as soon as
// fewer than MaxFallingLimit are alive. With no player in range it goes to the instant queue, which
// resolves the fall without an entity (SimulateInstantFall), at most MaxInstantPerTick per tick.
//
// The instant path notifies neighbours after it removes the block and after it places it, like the entity
// path does, so a column or a heap keeps coming down. The notifications are queued by the server (at most
// 500 per tick) and do not load chunks, and the requests they create go back into the same queue.
//
// Differences from the entity path, by design:
//  - a block that was only asked to fall sideways and has solid support below stays where it is;
//  - an instant fall only walks straight down and does not step sideways into adjacent free cells before landing;
//  - a snow block falling on a partial snow layer merges into it, leaving a snow block and the remaining layer on top;
//  - requests still waiting when the server stops are resolved in Dispose (only requests currently in the queue are
//    flushed; higher blocks in a column waiting for neighbour updates remain at their start cells);
//  - a request whose chunk unloads while it waits is dropped: the rock or block stays where it is and nothing asks
//    it to fall again after the reload (on the entity path the falling entity is saved with its chunk). Entities in
//    flight when a column unloads are left to vanilla: they are saved with the chunk and land after the reload;
//  - with a player nearby, requestQueue is a single server-wide FIFO capped at MaxFallingLimit, so falls anywhere
//    wait behind an ongoing collapse, and rock collapse neighbour checks are spaced by 300 to 500 ms.
//
// Threading: with RegionTicking on, falling entities tick on worker threads, so the live-entity
// bookkeeping (activeFallingPositions, totalFallingBlocks) is guarded by a lock. Queues and block
// changes stay on the main thread.
public class ModSystemFallingBlocks : ModSystem
{
	// One retry per tick, so this is roughly 10 seconds at the 32 ms queue tick.
	private const int MaxRetries = 300;
	private const int MaxFlushOnDispose = 100000;

	public static ModSystemFallingBlocks Instance { get; private set; }

	private ICoreServerAPI sapi;
	private long tickListenerId;

	// Live EntityBlockFalling count. Maintained by EntityBlockFalling through the hooks below.
	private int totalFallingBlocks;

	// Guards totalFallingBlocks and activeFallingPositions (see Threading in the header).
	private readonly object activeLock = new object();

	// initialPos of every live falling entity. O(1) replacement for the GetNearestEntity duplicate check.
	private readonly HashSet<BlockPos> activeFallingPositions = new HashSet<BlockPos>();

	// Entity requests are served first in, first out: a request that has waited longest goes first, and the requests
	// of a collapse arrive from the bottom up. On a 6400 block slab with a limit of 1000 this drained about 28 percent
	// faster than a random pick (645 to 1245 ticks against 780 to 1920), with far less spread, and left the blocks
	// where vanilla leaves them (a random pick scatters more of them sideways).
	private readonly Queue<SpawnRequest> requestQueue = new Queue<SpawnRequest>();

	// First in, first out: requests created by a neighbour update arrive bottom up, and resolving them in
	// that order keeps a block from being asked again because the block under it was still in place.
	private readonly Queue<SpawnRequest> instantQueue = new Queue<SpawnRequest>();

	// Requests that found their position occupied this tick. Put back after the loop so each
	// request is retried once per tick instead of spinning inside one tick.
	private readonly List<SpawnRequest> blockedThisTick = new List<SpawnRequest>();

	// Positions that have a request in either queue. Guards against duplicate requests.
	// Always holds private copies, never a caller's BlockPos (callers may mutate theirs).
	private readonly HashSet<BlockPos> pendingPositions = new HashSet<BlockPos>();

	private int activeRange = 128;
	private int maxFallingLimit = 1000;
	private int maxInstantPerTick = 100;

	// Read by EntityBlockFalling. 0 disables the stuck timeout only.
	public int StuckTimeoutMs { get; private set; } = 15000;

	public override bool ShouldLoad(EnumAppSide forSide)
	{
		return forSide == EnumAppSide.Server;
	}

	public override void StartServerSide(ICoreServerAPI api)
	{
		sapi = api;

		StratumFallingBlocksConfig cfg = StratumFallingBlocksConfig.Active;
		if (cfg == null)
		{
			api.Logger.Warning("[Stratum] Falling block config was not published before mod start, using defaults.");
			cfg = new StratumFallingBlocksConfig();
		}
		cfg.EnsureSane();

		if (!cfg.Enabled) return;

		maxFallingLimit = cfg.MaxFallingLimit;
		maxInstantPerTick = cfg.MaxInstantPerTick;
		StuckTimeoutMs = cfg.StuckTimeoutMs;

		activeRange = api.World.DefaultEntityTrackingRange * GlobalConstants.ChunkSize;

		tickListenerId = api.Event.RegisterGameTickListener(OnQueueTick, 32);

		Instance = this;
	}

	public override void Dispose()
	{
		if (sapi != null && tickListenerId != 0)
		{
			sapi.Event.UnregisterGameTickListener(tickListenerId);
			tickListenerId = 0;
		}

		// Best effort: the world is still live here (players are disconnected, the save happens
		// after the mod handlers are disposed). Resolve everything that is still queued so scattered
		// blocks are not saved at their start cells. Entity queue requests resolve the same way as
		// instant ones.
		// Clear first so hooks triggered by SetBlock during the flush take the vanilla path instead
		// of adding to a queue that is being drained.
		if (Instance == this) Instance = null;

		IWorldAccessor world = sapi?.World;
		if (world != null)
		{
			FlushQueue(world, instantQueue);
			FlushQueue(world, requestQueue);
			FlushQueue(world, blockedThisTick);
		}

		requestQueue.Clear();
		instantQueue.Clear();
		blockedThisTick.Clear();
		pendingPositions.Clear();
		lock (activeLock)
		{
			activeFallingPositions.Clear();
			totalFallingBlocks = 0;
		}
	}

	private void FlushQueue(IWorldAccessor world, IEnumerable<SpawnRequest> queue)
	{
		int flushed = 0;
		foreach (SpawnRequest queuedRequest in new List<SpawnRequest>(queue))
		{
			if (flushed++ >= MaxFlushOnDispose) break;
			SpawnRequest request = queuedRequest;
			// try/catch per request: one throwing block (GetDrops, a mod's CanAcceptFallOnto)
			// must not skip every request after it.
			try
			{
				Block current = world.BlockAccessor.GetBlock(request.InitialPos);
				if (current == null || current.Id == 0 || current != request.SourceBlock) continue;
				RefreshContainerSnapshot(world, ref request);

				ItemStack[] drops = request.Block.GetDrops(world, request.InitialPos, null);
				SimulateInstantFall(
					world, request.Block, request.SourceBlock,
					request.BlockEntity, request.BlockEntityTree,
					request.InitialPos, drops, request.DoRemoveBlock);
			}
			catch (Exception e)
			{
				try { sapi?.Logger.Warning("[Stratum] Falling block flush failed at {0}: {1}", request.InitialPos, e.Message); }
				catch (Exception) { /* logger may already be tearing down */ }
			}
		}
	}

	// Called at the end of EntityBlockFalling.Initialize (runs for both new and chunk loaded entities).
	public void OnFallingBlockInitialized(EntityBlockFalling entity)
	{
		lock (activeLock)
		{
			totalFallingBlocks++;
			activeFallingPositions.Add(entity.initialPos);
		}
	}

	// Called from EntityBlockFalling.OnEntityDespawn, only for entities that were registered.
	public void OnFallingBlockDespawned(EntityBlockFalling entity)
	{
		lock (activeLock)
		{
			totalFallingBlocks = Math.Max(0, totalFallingBlocks - 1);

			// The position was released when the entity landed or fell away. A block placed again at initialPos
			// may have a new entity there by now, which a second removal would make fall twice.
			if (!entity.StratumPositionFreed) activeFallingPositions.Remove(entity.initialPos);
		}
	}

	// Free the dedup slot once an entity has landed or fallen away, so a block placed again at
	// initialPos is allowed to fall again (matches vanilla's short duplicate window). The live count
	// is intentionally left untouched. The entity calls this once.
	public void OnFallingBlockLanded(EntityBlockFalling entity)
	{
		lock (activeLock)
		{
			activeFallingPositions.Remove(entity.initialPos);
		}
	}

	private bool IsActivePosition(BlockPos pos)
	{
		lock (activeLock)
		{
			return activeFallingPositions.Contains(pos);
		}
	}

	private int LiveFallingCount
	{
		get
		{
			lock (activeLock)
			{
				return totalFallingBlocks;
			}
		}
	}

	public bool IsPositionActiveOrPending(BlockPos pos)
	{
		return pendingPositions.Contains(pos) || IsActivePosition(pos);
	}

	public bool HasPlayerNearby(Vec3d pos)
	{
		float rangeSq = (float)activeRange * activeRange;
		foreach (IPlayer player in sapi.World.AllOnlinePlayers)
		{
			EntityPlayer eplr = player.Entity;
			if (eplr != null && eplr.Pos.InRangeOf(pos, rangeSq, activeRange))
			{
				return true;
			}
		}
		return false;
	}

	private void OnQueueTick(float dt)
	{
		ProcessEntityQueue();
		ProcessInstantQueue();
	}

	private void ProcessEntityQueue()
	{
		IWorldAccessor world = sapi.World;

		while (LiveFallingCount < maxFallingLimit && requestQueue.Count > 0)
		{
			SpawnRequest request = requestQueue.Dequeue();

			Block current = world.BlockAccessor.GetBlock(request.InitialPos);
			if (current == null || current.Id == 0 || current != request.SourceBlock)
			{
				pendingPositions.Remove(request.InitialPos);
				RecheckReplacedBlock(world, request, current);
				continue;
			}

			if (IsActivePosition(request.InitialPos))
			{
				// Position still occupied: defer to next tick instead of spinning in this one.
				request.RetryCount++;
				if (request.RetryCount >= MaxRetries)
				{
					pendingPositions.Remove(request.InitialPos);
					GiveUp(world, request);
					continue;
				}
				blockedThisTick.Add(request);
				continue;
			}

			// The block entity as it is now: the request may have waited a long time, and the block could have been
			// broken and placed again in between.
			EntityBlockFalling entityBf = new EntityBlockFalling(
				request.Block, world.BlockAccessor.GetBlockEntity(request.InitialPos), request.InitialPos,
				request.FallSound, request.ImpactDamageMul,
				request.CanFallSideways, request.DustIntensity);
			entityBf.DoRemoveBlock = request.DoRemoveBlock;

			world.SpawnEntity(entityBf);

			if (request.PositionOffset != null && request.PositionOffset != Vec3d.Zero)
			{
				entityBf.Pos.X += request.PositionOffset.X;
				entityBf.Pos.Y += request.PositionOffset.Y;
				entityBf.Pos.Z += request.PositionOffset.Z;
			}

			// Initialize has already run and added the position to activeFallingPositions.
			pendingPositions.Remove(request.InitialPos);
		}

		// Put back requests blocked this tick so they retry next tick (one retry per tick).
		if (blockedThisTick.Count > 0)
		{
			foreach (SpawnRequest blocked in blockedThisTick) requestQueue.Enqueue(blocked);
			blockedThisTick.Clear();
		}
	}

	// A request that could not get its position for MaxRetries ticks. The block is still at the
	// position (the caller just validated it), so it must be removed before its items are dropped,
	// otherwise the items are duplicated. A request that does not own its block (DoRemoveBlock is
	// false, e.g. a coal pile split) just ends: the block stays and nothing is dropped.
	private static void GiveUp(IWorldAccessor world, SpawnRequest request)
	{
		if (!request.DoRemoveBlock) return;

		ItemStack[] drops = request.Block.GetDrops(world, request.InitialPos, null);
		BlockEntity liveBlockEntity = world.BlockAccessor.GetBlockEntity(request.InitialPos);
		world.BlockAccessor.SetBlock(0, request.InitialPos);
		world.BlockAccessor.TriggerNeighbourBlockUpdate(request.InitialPos);
		SpawnDrops(world, request.InitialPos, drops, liveBlockEntity);
	}

	// While a request waited, the cell got a different block (for example the player mined the sand
	// and placed gravel there). That block's own createFallingBlock was skipped by the pending guard
	// and nothing asks it again, so let it re-check itself.
	private static void RecheckReplacedBlock(IWorldAccessor world, SpawnRequest request, Block current)
	{
		if (current == null || current.Id == 0) return;
		BlockPos pos = request.InitialPos;
		current.OnNeighbourBlockChange(world, pos, pos.DownCopy());
	}

	// Containers can still receive items while a fall waits in a queue. Preserve their live contents
	// when they land or are accepted by a block below. Other block entities keep the request-time
	// snapshot, including when the source entity is gone by the time the queue is processed.
	private static void RefreshContainerSnapshot(IWorldAccessor world, ref SpawnRequest request)
	{
		BlockEntity liveBe = world.BlockAccessor.GetBlockEntity(request.InitialPos);
		if (liveBe is not IBlockEntityContainer) return;

		TreeAttribute freshTree = new TreeAttribute();
		liveBe.ToTreeAttributes(freshTree);
		request.BlockEntity = liveBe;
		request.BlockEntityTree = freshTree;
	}

	private void ProcessInstantQueue()
	{
		IWorldAccessor world = sapi.World;
		int processed = 0;

		while (processed < maxInstantPerTick && instantQueue.Count > 0)
		{
			SpawnRequest request = instantQueue.Dequeue();
			pendingPositions.Remove(request.InitialPos);
			processed++;

			try
			{
				Block current = world.BlockAccessor.GetBlock(request.InitialPos);
				if (current == null || current.Id == 0 || current != request.SourceBlock)
				{
					RecheckReplacedBlock(world, request, current);
					continue;
				}
				RefreshContainerSnapshot(world, ref request);

				ItemStack[] drops = request.Block.GetDrops(world, request.InitialPos, null);

				SimulateInstantFall(
					world,
					request.Block,
					request.SourceBlock,
					request.BlockEntity,
					request.BlockEntityTree,
					request.InitialPos,
					drops,
					request.DoRemoveBlock);
			}
			catch (Exception e)
			{
				sapi?.Logger.Warning("[Stratum] Falling block simulation failed at {0}: {1}", request.InitialPos, e.Message);
			}
		}
	}

	// Asks a block to fall. With a player nearby the request goes to the entity queue,
	// otherwise to the instant queue.
	public void RequestSpawn(Block block, BlockEntity be, BlockPos initialPos,
		AssetLocation fallSound, float impactDamageMul,
		bool canFallSideways, float dustIntensity,
		bool doRemoveBlock = true, Vec3d positionOffset = null)
	{
		BlockPos pos = initialPos.Copy();

		if (pendingPositions.Contains(pos)) return;

		pendingPositions.Add(pos);

		bool hasPlayerNearby = HasPlayerNearby(pos.ToVec3d());

		// Snapshot the block entity now, while the block is still in place. The instant path has no
		// EntityBlockFalling.Initialize to do it, and the live block entity may change before the queue runs.
		TreeAttribute beTree = null;
		if (be != null)
		{
			beTree = new TreeAttribute();
			be.ToTreeAttributes(beTree);
		}

		SpawnRequest request = new SpawnRequest
		{
			Block = block,                        // falling/landing variant
			SourceBlock = sapi.World.BlockAccessor.GetBlock(pos), // original source at the position
			BlockEntity = be,
			BlockEntityTree = beTree,
			InitialPos = pos,
			FallSound = fallSound,
			ImpactDamageMul = impactDamageMul,
			CanFallSideways = canFallSideways,
			DustIntensity = dustIntensity,
			DoRemoveBlock = doRemoveBlock,
			PositionOffset = positionOffset ?? Vec3d.Zero
		};

		if (hasPlayerNearby)
		{
			requestQueue.Enqueue(request);
		}
		else
		{
			instantQueue.Enqueue(request);
		}
	}

	private static void RemoveStartBlock(IBlockAccessor accessor, BlockPos startPos)
	{
		accessor.SetBlock(0, startPos);
		accessor.TriggerNeighbourBlockUpdate(startPos);
	}

	// Drops the block's items and, if the block entity is a container, its contents.
	public static void SpawnDrops(IWorldAccessor world, BlockPos pos, ItemStack[] drops, BlockEntity be)
	{
		Vec3d dpos = pos.ToVec3d().Add(0.5, 0.5, 0.5);

		if (drops != null)
		{
			foreach (ItemStack drop in drops)
			{
				world.SpawnItemEntity(drop, dpos);
			}
		}

		if (be is IBlockEntityContainer bec)
		{
			bec.DropContents(dpos);
		}
	}

	// Walks the fall path without creating an entity. Places the block on the first solid support
	// or drops its items. beTree is the snapshot taken before the block was removed, be is only
	// used for DropContents. sourceBlock is the original block at startPos and is validated before
	// removal; block is the falling/landing variant that gets placed.
	//
	// Neighbours are notified after the removal and after the placement, like EntityBlockFalling.UpdateBlock does.
	// With doRemoveBlock false the caller owns the removal (and its notification).
	public static void SimulateInstantFall(
		IWorldAccessor world,
		Block block,
		Block sourceBlock,
		BlockEntity be,
		TreeAttribute beTree,
		BlockPos startPos,
		ItemStack[] drops,
		bool doRemoveBlock)
	{
		IBlockAccessor accessor = world.BlockAccessor;

		if (doRemoveBlock && accessor.GetBlock(startPos) != sourceBlock) return;

		BlockPos finalPos = startPos.Copy();
		int worldHeight = accessor.MapSizeY;

		for (int i = 0; i < worldHeight; i++)
		{
			BlockPos belowPos = finalPos.DownCopy();
			Block belowBlock = accessor.GetMostSolidBlock(belowPos);

			// The block below may take the fall itself (funnel, loose soil, coal pile).
			// Pass the snapshot, not a fresh serialization of the live block entity.
			if (belowBlock.CanAcceptFallOnto(world, belowPos, block, beTree))
			{
				if (doRemoveBlock) RemoveStartBlock(accessor, startPos);
				belowBlock.OnFallOnto(world, belowPos, block, beTree);
				return;
			}

			if (belowBlock.Replaceable >= 6000 || belowBlock.IsLiquid())
			{
				finalPos = belowPos;
			}
			else
			{
				break;
			}
		}

		// The block would not move and is not turning into another variant: nothing to resolve.
		// This is the sideways-only fall (solid support below). Removing and re-placing it would
		// only re-trigger OnBlockPlaced and queue the same request again.
		if (doRemoveBlock && block == sourceBlock && finalPos.Equals(startPos)) return;

		if (doRemoveBlock) RemoveStartBlock(accessor, startPos);

		Block targetBlock = accessor.GetBlock(finalPos);
		Block supportBlock = accessor.GetMostSolidBlock(finalPos.DownCopy());

		bool supported = supportBlock.Replaceable < 6000 && !supportBlock.IsLiquid();

		// Snow lands on whatever is there by raising its snow level (vanilla UpdateSnowLayer),
		// it does not replace it.
		if (supported && block.BlockMaterial == EnumBlockMaterial.Snow)
		{
			Block occupied = accessor.GetMostSolidBlock(finalPos);
			if (occupied.Id != 0)
			{
				Block snowBlock = occupied.GetSnowCoveredVariant(finalPos, occupied.snowLevel + 1);
				if (snowBlock != null && snowBlock != occupied)
				{
					accessor.ExchangeBlock(snowBlock.Id, finalPos);
				}
				return;
			}
		}

		// Vanilla (EntityBlockFalling.UpdateBlock) does not place snow into a fluid cell.
		if (supported && block.BlockMaterial == EnumBlockMaterial.Snow
			&& accessor.GetBlock(finalPos, BlockLayersAccess.Fluid).Id != 0)
		{
			return;
		}

		bool canPlace = supported && targetBlock != null && targetBlock.IsReplacableBy(block);

		if (!canPlace && !doRemoveBlock)
		{
			// When settling an entity in place (doRemoveBlock == false), its current cell may be occupied
			// (e.g. by another settling entity in the same column, or obstacle blocks like stacked leaves).
			// Step up to find the first cell that can accept the block, matching vanilla's hop behavior.
			BlockPos stepPos = finalPos.Copy();
			for (int hop = 1; hop <= 4; hop++)
			{
				stepPos.Y++;
				Block candidate = accessor.GetBlock(stepPos);
				if (candidate != null && candidate.IsReplacableBy(block))
				{
					Block candidateSupport = accessor.GetMostSolidBlock(stepPos.DownCopy());
					if (candidateSupport.Replaceable < 6000 && !candidateSupport.IsLiquid())
					{
						finalPos = stepPos;
						targetBlock = candidate;
						canPlace = true;
						break;
					}
				}
			}
		}

		if (canPlace)
		{
			accessor.SetBlock(block.BlockId, finalPos);
			accessor.TriggerNeighbourBlockUpdate(finalPos);

			if (beTree != null)
			{
				BlockEntity newBe = accessor.GetBlockEntity(finalPos);
				if (newBe != null)
				{
					beTree.SetInt("posx", finalPos.X);
					beTree.SetInt("posy", finalPos.InternalY);
					beTree.SetInt("posz", finalPos.Z);
					newBe.FromTreeAttributes(beTree, world);
				}
			}
			return;
		}

		SpawnDrops(world, finalPos, drops, be);
	}
}
