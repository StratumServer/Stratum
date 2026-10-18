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
// Deliberate policy for the instant path (SimulateInstantFall, also used by the entity watchdog):
// a fall resolved without an entity does NOT notify neighbours (plain SetBlock, no
// TriggerNeighbourBlockUpdate). The goal is to stop collapses from loading the server outside the
// range of players, so a chain of collapses must not continue there. Consequences, by design:
//  - a sand/gravel column with no player in range loses its bottom block and the rest stays up until
//    something next to it changes. On the queued path the block above falls too: a settling entity
//    notifies its start cell, so that column loses two blocks;
//  - a block that was only asked to fall sideways and has solid support below stays where it is;
//  - the watchdog puts a block that has already stepped sideways back on its start cell (it settles
//    from initialPos when nobody is near);
//  - requests still waiting in the entity queue when the server stops are resolved in Dispose, but
//    requests whose column is unloaded earlier stay at their start cell. Entities in flight when a
//    column unloads are left to vanilla: they are saved with the chunk and land after the reload.
// Known differences from the entity path for snow (small): snow that lands on a snow block adds a layer
// above it instead of being absorbed into it.
// With a player nearby everything goes through the entity path and behaves like vanilla.
//
// Threading: with RegionTicking on, falling entities tick on worker threads, so the live-entity
// bookkeeping (activeFallingPositions, totalFallingBlocks) is guarded by a lock. Placing blocks and
// spawning items stays on the main thread: the watchdog defers its settle with EnqueueMainThreadTask.
public class ModSystemFallingBlocks : ModSystem
{
	// One retry per tick, so this is roughly 10 seconds at the 32 ms queue tick.
	private const int MaxRetries = 300;

	public static ModSystemFallingBlocks Instance { get; private set; }

	private ICoreServerAPI sapi;
	private long tickListenerId;

	// Live EntityBlockFalling count. Maintained by EntityBlockFalling through the hooks below.
	private int totalFallingBlocks;

	// Guards totalFallingBlocks and activeFallingPositions (see Threading in the header).
	private readonly object activeLock = new object();

	// initialPos of every live falling entity. O(1) replacement for the GetNearestEntity duplicate check.
	private readonly HashSet<BlockPos> activeFallingPositions = new HashSet<BlockPos>();

	// Requests are popped in random order, which looks more natural than FIFO for a collapse.
	private readonly List<SpawnRequest> requestQueue = new List<SpawnRequest>();
	private readonly List<SpawnRequest> instantQueue = new List<SpawnRequest>();

	// Requests that found their position occupied this tick. Put back after the loop so each
	// request is retried once per tick instead of spinning inside one tick.
	private readonly List<SpawnRequest> blockedThisTick = new List<SpawnRequest>();

	// Positions that have a request in either queue. Guards against duplicate requests.
	// Always holds private copies, never a caller's BlockPos (callers may mutate theirs).
	private readonly HashSet<BlockPos> pendingPositions = new HashSet<BlockPos>();

	private readonly Random rng = new Random();

	private int activeRange = 128;
	private int maxFallingLimit = 500;
	private int maxInstantPerTick = 100;

	// Read by EntityBlockFalling. 0 means the watchdog is off.
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
		// instant ones; there is no neighbour cascade (see the policy at the top of the file).
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

		if (Instance == this) Instance = null;
	}

	private void FlushQueue(IWorldAccessor world, List<SpawnRequest> queue)
	{
		foreach (SpawnRequest request in queue)
		{
			// try/catch per request: one throwing block (GetDrops, a mod's CanAcceptFallOnto)
			// must not skip every request after it.
			try
			{
				Block current = world.BlockAccessor.GetBlock(request.InitialPos);
				if (current == null || current.Id == 0 || current != request.SourceBlock) continue;

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
			activeFallingPositions.Remove(entity.initialPos);
		}
	}

	// Free the dedup slot once an entity has landed or fallen away, so a block placed again at
	// initialPos is allowed to fall again (matches vanilla's short duplicate window). The live count
	// is intentionally left untouched. Idempotent: safe to call after despawn already removed it.
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

	// Swap with the last element and remove it, so a random pick stays O(1).
	private SpawnRequest PopRandom(List<SpawnRequest> list)
	{
		int index = rng.Next(list.Count);
		SpawnRequest picked = list[index];
		int last = list.Count - 1;
		list[index] = list[last];
		list.RemoveAt(last);
		return picked;
	}

	private void ProcessEntityQueue()
	{
		IWorldAccessor world = sapi.World;

		while (LiveFallingCount < maxFallingLimit && requestQueue.Count > 0)
		{
			SpawnRequest request = PopRandom(requestQueue);

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

			EntityBlockFalling entityBf = new EntityBlockFalling(
				request.Block, request.BlockEntity, request.InitialPos,
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
			requestQueue.AddRange(blockedThisTick);
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
		world.BlockAccessor.SetBlock(0, request.InitialPos);
		SpawnDrops(world, request.InitialPos, drops, request.BlockEntity);
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

	private void ProcessInstantQueue()
	{
		IWorldAccessor world = sapi.World;
		int processed = 0;

		while (processed < maxInstantPerTick && instantQueue.Count > 0)
		{
			SpawnRequest request = PopRandom(instantQueue);
			pendingPositions.Remove(request.InitialPos);
			processed++;

			Block current = world.BlockAccessor.GetBlock(request.InitialPos);
			if (current == null || current.Id == 0 || current != request.SourceBlock)
			{
				RecheckReplacedBlock(world, request, current);
				continue;
			}

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
	}

	// Asks a block to fall. With a player nearby the request goes to the entity queue,
	// otherwise to the instant queue.
	public void RequestSpawn(Block block, BlockEntity be, BlockPos initialPos,
		AssetLocation fallSound, float impactDamageMul,
		bool canFallSideways, float dustIntensity,
		bool doRemoveBlock = true, Vec3d positionOffset = null)
	{
		// Private copy: used as the pending key and kept by the request. Callers such as
		// ItemSnowshovel reuse and mutate their BlockPos after the call, which would corrupt the hash.
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
			requestQueue.Add(request);
		}
		else
		{
			instantQueue.Add(request);
		}
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
	// No neighbour updates here, on purpose (see the policy at the top of the file).
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
				if (doRemoveBlock) accessor.SetBlock(0, startPos);
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
		// only re-trigger OnBlockPlaced and queue the same request again, which is exactly the load
		// this system exists to avoid outside the range of players.
		if (doRemoveBlock && block == sourceBlock && finalPos.Equals(startPos)) return;

		if (doRemoveBlock) accessor.SetBlock(0, startPos);

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

		bool canPlace = supported && (targetBlock.IsLiquid() || targetBlock.Replaceable >= 6000);

		if (canPlace)
		{
			accessor.SetBlock(block.BlockId, finalPos);

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
