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
public class ModSystemFallingBlocks : ModSystem
{
	public static ModSystemFallingBlocks Instance { get; private set; }

	private ICoreServerAPI sapi;
	private long tickListenerId;

	// Live EntityBlockFalling count. Maintained by EntityBlockFalling through the two hooks below.
	private int totalFallingBlocks;

	// initialPos of every live falling entity. O(1) replacement for the GetNearestEntity duplicate check.
	private readonly HashSet<BlockPos> activeFallingPositions = new HashSet<BlockPos>();

	// Requests are popped in random order, which looks more natural than FIFO for a collapse.
	private readonly List<SpawnRequest> requestQueue = new List<SpawnRequest>();
	private readonly List<SpawnRequest> instantQueue = new List<SpawnRequest>();

	// Positions that have a request in either queue. Guards against duplicate requests.
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

		requestQueue.Clear();
		instantQueue.Clear();
		pendingPositions.Clear();
		activeFallingPositions.Clear();
		totalFallingBlocks = 0;

		if (Instance == this) Instance = null;
	}

	// Called at the end of EntityBlockFalling.Initialize (runs for both new and chunk loaded entities).
	public void OnFallingBlockInitialized(EntityBlockFalling entity)
	{
		totalFallingBlocks++;
		activeFallingPositions.Add(entity.initialPos);
	}

	// Called from EntityBlockFalling.OnEntityDespawn, only for entities that were registered.
	public void OnFallingBlockDespawned(EntityBlockFalling entity)
	{
		totalFallingBlocks = Math.Max(0, totalFallingBlocks - 1);
		activeFallingPositions.Remove(entity.initialPos);
	}

	public bool IsPositionActiveOrPending(BlockPos pos)
	{
		return pendingPositions.Contains(pos) || activeFallingPositions.Contains(pos);
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

		while (totalFallingBlocks < maxFallingLimit && requestQueue.Count > 0)
		{
			SpawnRequest request = PopRandom(requestQueue);

			Block block = world.BlockAccessor.GetBlock(request.InitialPos);
			if (block == null || block.Id == 0 || block != request.Block)
			{
				pendingPositions.Remove(request.InitialPos);
				continue;
			}

			if (activeFallingPositions.Contains(request.InitialPos))
			{
				request.RetryCount++;
				if (request.RetryCount >= 300)
				{
					pendingPositions.Remove(request.InitialPos);
					ItemStack[] drops = request.Block.GetDrops(world, request.InitialPos, null);
					SpawnDrops(world, request.InitialPos, drops, request.BlockEntity);
					continue;
				}

				// The guard stays set, the request is still in progress.
				requestQueue.Add(request);
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
			if (current == null || current.Id == 0 || current != request.Block)
			{
				continue;
			}

			ItemStack[] drops = request.Block.GetDrops(world, request.InitialPos, null);

			SimulateInstantFall(
				world,
				request.Block,
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
		if (pendingPositions.Contains(initialPos)) return;

		pendingPositions.Add(initialPos);

		bool hasPlayerNearby = HasPlayerNearby(initialPos.ToVec3d());

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
			Block = block,
			BlockEntity = be,
			BlockEntityTree = beTree,
			InitialPos = initialPos.Copy(),
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
	// used for DropContents.
	public static void SimulateInstantFall(
		IWorldAccessor world,
		Block block,
		BlockEntity be,
		TreeAttribute beTree,
		BlockPos startPos,
		ItemStack[] drops,
		bool doRemoveBlock)
	{
		if (doRemoveBlock)
		{
			if (world.BlockAccessor.GetBlock(startPos) != block) return;
			world.BlockAccessor.SetBlock(0, startPos);
		}

		BlockPos finalPos = startPos.Copy();
		int worldHeight = world.BlockAccessor.MapSizeY;

		for (int i = 0; i < worldHeight; i++)
		{
			BlockPos belowPos = finalPos.DownCopy();
			Block belowBlock = world.BlockAccessor.GetMostSolidBlock(belowPos);

			// The block below may take the fall itself (funnel, loose soil, coal pile).
			// Pass the snapshot, not a fresh serialization of the live block entity.
			if (belowBlock.CanAcceptFallOnto(world, belowPos, block, beTree))
			{
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

		Block targetBlock = world.BlockAccessor.GetBlock(finalPos);
		Block supportBlock = world.BlockAccessor.GetMostSolidBlock(finalPos.DownCopy());

		bool canPlace = supportBlock.Replaceable < 6000
			&& !supportBlock.IsLiquid()
			&& (targetBlock.IsLiquid() || targetBlock.Replaceable >= 6000);

		if (canPlace)
		{
			world.BlockAccessor.SetBlock(block.BlockId, finalPos);

			if (beTree != null)
			{
				BlockEntity newBe = world.BlockAccessor.GetBlockEntity(finalPos);
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
