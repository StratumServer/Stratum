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
// Resolves falls instantly, without an EntityBlockFalling, for blocks that no player is near.
// Instance is null while the system is disabled in the config, and every caller falls back to
// vanilla behavior in that case. With a player nearby everything goes through the entity path
// and behaves like vanilla.
//
// The instant path (SimulateInstantFall) notifies neighbours after it removes the block and after it
// places it, like the entity path does, so a column or a heap keeps coming down. The notifications are
// queued by the server (at most 500 per tick) and do not load chunks, and the requests they create go
// back into the same queue, which resolves at most MaxInstantPerTick per tick. A large heap far from
// players therefore takes longer to land, at a bounded cost per tick.
//
// Differences from the entity path, by design:
//  - a block that was only asked to fall sideways and has solid support below stays where it is;
//  - snow that lands on a snow block adds a layer above it instead of being absorbed into it;
//  - requests still waiting when the server stops are resolved in Dispose;
//  - a request whose chunk unloads while it waits is dropped: the rock or block stays where it is and nothing asks
//    it to fall again after the reload (on the entity path the falling entity is saved with its chunk).
public class ModSystemFallingBlocks : ModSystem
{
	public static ModSystemFallingBlocks Instance { get; private set; }

	private ICoreServerAPI sapi;
	private long tickListenerId;

	// First in, first out: requests created by a neighbour update arrive bottom up, and resolving them in
	// that order keeps a block from being asked again because the block under it was still in place.
	private readonly Queue<SpawnRequest> instantQueue = new Queue<SpawnRequest>();

	// Positions that have a request in the queue. Guards against duplicate requests.
	// Always holds private copies, never a caller's BlockPos (callers may mutate theirs).
	private readonly HashSet<BlockPos> pendingPositions = new HashSet<BlockPos>();

	// Upper bound for the flush in Dispose, so a request chain cannot keep the shutdown busy for ever.
	private const int MaxFlushOnDispose = 100000;

	private int activeRange = 128;
	private int maxInstantPerTick = 100;

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

		maxInstantPerTick = cfg.MaxInstantPerTick;

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
		// blocks are not saved at their start cells.
		// Instance goes first: a block hook that runs during the flush (SetBlock calls OnBlockRemoved and
		// OnBlockPlaced) then takes the vanilla path instead of adding to the queue that is being drained.
		if (Instance == this) Instance = null;

		IWorldAccessor world = sapi?.World;
		if (world != null)
		{
			int flushed = 0;
			while (instantQueue.Count > 0 && flushed++ < MaxFlushOnDispose)
			{
				SpawnRequest request = instantQueue.Dequeue();
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

		instantQueue.Clear();
		pendingPositions.Clear();
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
		IWorldAccessor world = sapi.World;
		int processed = 0;

		while (processed < maxInstantPerTick && instantQueue.Count > 0)
		{
			SpawnRequest request = instantQueue.Dequeue();
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

	// While a request waited, the cell got a different block (for example the player mined the sand
	// and placed gravel there). That block's own createFallingBlock was skipped by the pending guard
	// and nothing asks it again, so let it re-check itself.
	private static void RecheckReplacedBlock(IWorldAccessor world, SpawnRequest request, Block current)
	{
		if (current == null || current.Id == 0) return;
		BlockPos pos = request.InitialPos;
		current.OnNeighbourBlockChange(world, pos, pos.DownCopy());
	}

	// True when a fall request for this position is already waiting in the queue.
	public bool IsPending(BlockPos pos)
	{
		return pendingPositions.Contains(pos);
	}

	// Called where vanilla would spawn an EntityBlockFalling. Returns true when the fall was taken
	// over (queued for an instant resolve, or already queued), false when a player is near and the
	// caller must spawn the entity as vanilla does.
	public bool TryRequestInstantFall(Block block, BlockEntity be, BlockPos initialPos, bool doRemoveBlock = true)
	{
		// Private copy: used as the pending key and kept by the request. Callers may reuse and
		// mutate their BlockPos after the call, which would corrupt the hash.
		BlockPos pos = initialPos.Copy();

		if (pendingPositions.Contains(pos)) return true;

		if (HasPlayerNearby(pos.ToVec3d())) return false;

		// Snapshot the block entity now, while the block is still in place. The instant path has no
		// EntityBlockFalling.Initialize to do it, and the live block entity may change before the queue runs.
		TreeAttribute beTree = null;
		if (be != null)
		{
			beTree = new TreeAttribute();
			be.ToTreeAttributes(beTree);
		}

		pendingPositions.Add(pos);
		instantQueue.Enqueue(new SpawnRequest
		{
			Block = block,                                          // falling/landing variant
			SourceBlock = sapi.World.BlockAccessor.GetBlock(pos),   // original source at the position
			BlockEntity = be,
			BlockEntityTree = beTree,
			InitialPos = pos,
			DoRemoveBlock = doRemoveBlock
		});
		return true;
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

		bool canPlace = supported && (targetBlock.IsLiquid() || targetBlock.Replaceable >= 6000);

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
