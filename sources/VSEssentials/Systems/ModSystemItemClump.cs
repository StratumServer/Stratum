using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Vintagestory.GameContent;

// Server side merging of resting item stacks (EntityItem). Works on any EntityItem in the world,
// whoever created it (player, drops, other mods, vanilla).
//
// Design:
//  - Item lifecycle is tracked with OnEntitySpawn / OnEntityLoaded / OnEntityDespawn. EntityItem lives in
//    the API assembly and cannot call into this assembly, so direct hooks are not possible here.
//  - One shared queue and one tick listener instead of a RegisterCallback per item. The queue is
//    monotonic in time (same delay for everyone), so only the head has to be checked.
//  - Attempts per tick are budgeted, so thousands of items loaded with the world do not hit a single tick.
//  - Only a newcomer (spawned or loaded) becomes a candidate. It searches for neighbours and pours
//    itself into them. Items that have been lying around are not rescanned.
//  - Small default radius (5 horizontal, 2 vertical): items do not vanish far from where they lay.
//    The vertical radius reaches through a one block floor or wall, so stacks on both sides of a thin
//    floor can merge.
//  - Largest stacks receive, smallest stacks donate, which minimizes the resulting entity count.
//  - A stack a player just dropped is still inside the pickup window of EntityItem.CanCollect
//    (other players cannot take it yet). It takes no part in merging, neither as donor nor as
//    receiver, until that window has passed, so merging never skips it.
//  - The merged stack gets the average despawn clock of the items it holds, weighted by quantity.
//    deathTime is elapsed time (EntityBehaviorDespawn adds to it and kills the item once it passes
//    minSeconds). Taking the smaller value of the pair would let a trickle of fresh drops keep an old pile
//    alive for ever, and the larger one would cut a fresh pile short. Stacks with different minsecondsToDespawn (for
//    example a death drop kept for an hour) never merge, otherwise one side would get the wrong lifetime.
//  - A merge moves items between entities that may sit in different chunks. Only the chunk of a donor that
//    dies is dirtied by the removal, so the receiver and a surviving donor chunk are marked modified
//    explicitly, otherwise the next save writes the old counts.
//  - Spawn and despawn events can come from worker threads (RegionTicking), so they only append to a
//    concurrent queue; the queue and the index are touched from the tick listener alone.
// Merging goes through ItemSlot.TryPutInto and GetMergableQuantity, so all vanilla rules
// (attributes, freshness and other transition states) still apply.
public class ModSystemItemClump : ModSystem
{
	// Tick period. Merge attempts are budgeted per tick, so the period sets the merge rate: 25 attempts every
	// 50 ms drain a burst of drops in a few seconds, where the same budget every 250 ms left most of them as
	// separate entities (simulated and sent to clients) for ten. A tick with nothing due costs one queue peek.
	private const int TickIntervalMs = 50;

	// An item counts as settled when its squared speed is at most this value.
	private const double SettledMotionSq = 0.005d;

	// Mirrors EntityItem.CanCollect: a stack dropped by a player cannot be picked up by others for this long.
	private const int PickupWindowMs = 1000;

	private ICoreServerAPI sapi;
	private long tickListenerId;
	private bool running;

	// Queue ordered by due time, plus an index by EntityId for O(1) dedup and cancel.
	private readonly Queue<ItemClumpCandidate> queue = new Queue<ItemClumpCandidate>();
	private readonly Dictionary<long, ItemClumpCandidate> byId = new Dictionary<long, ItemClumpCandidate>();

	// Spawn/load/despawn events, drained on the main thread by OnTick (events may arrive from workers).
	private readonly ConcurrentQueue<(EntityItem Item, bool Spawned)> intake = new ConcurrentQueue<(EntityItem Item, bool Spawned)>();

	// Reused buffer (the server tick is single threaded).
	private readonly List<EntityItem> group = new List<EntityItem>();
	private readonly Dictionary<EntityItem, LandClaim> claimOf = new Dictionary<EntityItem, LandClaim>();

	private float radius;
	private float verticalRadius;
	private int delayMs;
	private int maxSettleAttempts;
	private int maxAttemptsPerTick;
	private int maxGroupSize;

	// Distance from a player beyond which items count as frozen (physics does not tick them,
	// so speed and OnGround keep their values from spawn). The smaller of the server tracking
	// range and the entity simulation range.
	private int frozenRange;

	// Parameter for the search predicate, avoids a closure per call.
	private CollectibleObject matchCollectible;
	private readonly ActionConsumable<Entity> matcher;

	private static readonly Comparison<EntityItem> bySizeDesc = CompareBySizeDesc;

	public ModSystemItemClump()
	{
		matcher = MatchEntity;
	}

	public override bool ShouldLoad(EnumAppSide forSide)
	{
		return forSide == EnumAppSide.Server;
	}

	public override void StartServerSide(ICoreServerAPI api)
	{
		StratumItemClumpConfig cfg = StratumItemClumpConfig.Active;
		if (cfg == null)
		{
			api.Logger.Warning("[Stratum] Item clump config was not published before mod start, using defaults.");
			cfg = new StratumItemClumpConfig();
		}
		cfg.EnsureSane();

		if (!cfg.Enabled) return;

		sapi = api;

		radius = cfg.Radius;
		verticalRadius = cfg.VerticalRadius;
		delayMs = cfg.DelayMs;
		maxSettleAttempts = cfg.MaxSettleAttempts;
		maxAttemptsPerTick = cfg.MaxAttemptsPerTick;
		maxGroupSize = cfg.MaxGroupSize;

		int trackingRange = GlobalConstants.DefaultSimulationRange;
		try
		{
			trackingRange = Math.Min(trackingRange, api.World.DefaultEntityTrackingRange * GlobalConstants.ChunkSize);
		}
		catch (Exception)
		{
			// keep the simulation range
		}
		frozenRange = Math.Max(trackingRange, GlobalConstants.ChunkSize);

		api.Logger.Notification("[Stratum] ItemClump: enabled, radius={0}/{1}, frozenRange={2}",
			radius, verticalRadius, frozenRange);

		api.Event.OnEntitySpawn += OnEntitySpawn;
		api.Event.OnEntityLoaded += OnEntitySpawn; // same logic for spawn and load
		api.Event.OnEntityDespawn += OnEntityDespawn;
		tickListenerId = api.Event.RegisterGameTickListener(OnTick, TickIntervalMs);

		running = true;
	}

	public override void Dispose()
	{
		if (sapi != null && running)
		{
			sapi.Event.OnEntitySpawn -= OnEntitySpawn;
			sapi.Event.OnEntityLoaded -= OnEntitySpawn;
			sapi.Event.OnEntityDespawn -= OnEntityDespawn;
			sapi.Event.UnregisterGameTickListener(tickListenerId);
		}

		queue.Clear();
		byId.Clear();
		while (intake.TryDequeue(out _)) { }
		group.Clear();
		claimOf.Clear();
		matchCollectible = null;
		sapi = null;
		running = false;
	}

	private void OnEntitySpawn(Entity entity)
	{
		if (entity is not EntityItem item) return;
		intake.Enqueue((item, true));
	}

	private void OnEntityDespawn(Entity entity, EntityDespawnData despawn)
	{
		if (entity is not EntityItem item) return;
		intake.Enqueue((item, false));
	}

	// Applies the events in the order they arrived (a despawn followed by a reload of the same
	// entity must end up tracked).
	private void DrainIntake(long now)
	{
		while (intake.TryDequeue(out (EntityItem Item, bool Spawned) ev))
		{
			if (ev.Spawned)
			{
				Enqueue(ev.Item, now);
			}
			else if (byId.Remove(ev.Item.EntityId, out ItemClumpCandidate c))
			{
				c.Cancelled = true; // the queue entry is skipped when it reaches the head
			}
		}
	}

	private void Enqueue(EntityItem item, long now)
	{
		if (byId.ContainsKey(item.EntityId)) return;

		// A non stackable item can neither receive nor donate, so it never needs a turn in the queue.
		ItemStack stack = item.Slot?.Itemstack;
		if (stack != null && stack.Collectible.MaxStackSize <= 1) return;

		ItemClumpCandidate c = new ItemClumpCandidate
		{
			Entity = item,
			EntityId = item.EntityId,
			DueMs = now + delayMs
		};
		byId[c.EntityId] = c;
		queue.Enqueue(c);
	}

	private void OnTick(float dt)
	{
		long now = sapi.World.ElapsedMilliseconds;
		DrainIntake(now);

		// Two independent budgets:
		//  clumpBudget: expensive merge attempts (GetEntitiesAround + TryPutInto);
		//  scanBudget: cheap queue checks (alive, resting, player nearby).
		// With one shared budget the queue was filled with repeated checks of items that were
		// still falling, and real merges never got a turn.
		int clumpBudget = maxAttemptsPerTick;
		int scanBudget = Math.Max(500, maxAttemptsPerTick * 20);

		while (scanBudget-- > 0 && queue.Count > 0)
		{
			ItemClumpCandidate c = queue.Peek();

			if (c.Cancelled)
			{
				queue.Dequeue();
				continue;
			}

			if (c.DueMs > now) break; // the queue is monotonic, the rest is too early as well

			EntityItem e = c.Entity;

			if (!IsValid(e))
			{
				queue.Dequeue();
				byId.Remove(c.EntityId);
				continue;
			}

			// A full stack cannot receive and only donates next to other full stacks, which is nothing
			// to merge. Drop it without spending the expensive budget on it.
			ItemStack ready = e.Slot.Itemstack;
			if (ready.StackSize >= ready.Collectible.MaxStackSize)
			{
				queue.Dequeue();
				byId.Remove(c.EntityId);
				continue;
			}

			// A stack a player just dropped is still in its pickup window: merging it now would
			// make it collectible by others earlier than vanilla allows. Not a settle failure, so
			// the attempt is not spent. now + delayMs keeps the queue monotonic.
			if (InPickupWindow(e, now))
			{
				queue.Dequeue();
				c.DueMs = now + delayMs;
				queue.Enqueue(c);
				continue;
			}

			// An item far from players is not simulated, it is frozen and never moves, so waiting
			// for it to settle is pointless and it merges right away. Freeze detection, most reliable first:
			//  1) the server marked the entity inactive (outside the simulation range);
			//  2) no player is near the item (by simulation range);
			//  3) below: the item hangs without support and did not move between two checks.
			bool frozen = e.State == EnumEntityState.Inactive || NoPlayerNear(e.Pos);

			if (!frozen && !IsSettled(e))
			{
				EntityPos p = e.Pos;

				// Live physics never lets an unsupported item stand still, it has to move within
				// 1.5 s. An unchanged position means it hangs in the air outside the simulation
				// range. Nothing to wait for, treat it as frozen.
				if (c.HasLastPos && SqDist(p.X, p.Y, p.Z, c.LastX, c.LastY, c.LastZ) < 1e-4)
				{
					frozen = true;
				}
				else
				{
					c.HasLastPos = true;
					c.LastX = p.X;
					c.LastY = p.Y;
					c.LastZ = p.Z;

					// Still flying or moving: check again later, but not forever.
					queue.Dequeue();
					if (++c.Attempts >= maxSettleAttempts)
					{
						byId.Remove(c.EntityId);
					}
					else
					{
						c.DueMs = now + delayMs;
						queue.Enqueue(c);
					}
					continue;
				}
			}

			// Ready to merge, but the budget of expensive attempts is spent for this tick.
			// Leave it at the head of the queue until the next tick.
			if (clumpBudget <= 0) break;
			clumpBudget--;

			queue.Dequeue();
			// Stop tracking BEFORE merging: if the item survives it can become a candidate
			// again (for example after a chunk reload).
			byId.Remove(c.EntityId);
			TryClump(e, frozen);
		}
	}

	private void TryClump(EntityItem root, bool frozen)
	{
		ItemStack rootStack = root.Slot.Itemstack;
		CollectibleObject coll = rootStack.Collectible;

		// A non stackable or already full stack can neither receive nor meaningfully donate.
		if (coll.MaxStackSize <= 1 || rootStack.StackSize >= coll.MaxStackSize) return;

		long now = sapi.World.ElapsedMilliseconds;

		matchCollectible = coll;
		Entity[] found;
		try
		{
			found = sapi.World.GetEntitiesAround(root.Pos.XYZ, radius, verticalRadius, matcher);
		}
		finally
		{
			matchCollectible = null;
		}

		if (found == null || found.Length < 2) return;

		group.Clear();
		claimOf.Clear();
		try
		{
			group.Add(root);
			for (int i = 0; i < found.Length && group.Count < maxGroupSize; i++)
			{
				if (found[i] is not EntityItem other || other == root) continue;
				if (!IsValid(other) || (!frozen && !IsSettled(other)) || InPickupWindow(other, now)) continue;
				group.Add(other);
			}

			if (group.Count < 2) return;

			// Largest stacks first (receivers), ties broken by EntityId for determinism.
			group.Sort(bySizeDesc);

			// Donors go from the end (smallest), receivers from the start (largest).
			for (int d = group.Count - 1; d > 0; d--)
			{
				EntityItem donor = group[d];
				if (!IsValid(donor)) continue;

				for (int r = 0; r < d; r++)
				{
					EntityItem recv = group[r];
					if (!IsValid(recv)) continue;

					ItemStack ds = donor.Slot.Itemstack;
					if (ds == null || ds.StackSize <= 0) break;

					ItemStack rs = recv.Slot.Itemstack;
					int space = rs.Collectible.MaxStackSize - rs.StackSize;
					if (space <= 0) continue;

					// Stacks that expire on different clocks (a death drop kept for an hour next to ordinary
					// stacks) must not mix: the merged stack would get the wrong lifetime.
					if (DespawnOverride(donor) != DespawnOverride(recv)) continue;

					// Every member is within the radius of the root, but two members can sit up to twice that apart.
					// Only stacks that are close to each other mix, and never across a land claim border, so
					// a pile dropped outside a claimed wall cannot pull in the owner's stacks lying behind it.
					if (!InMergeRange(donor, recv) || !SameClaim(donor, recv)) continue;

					// Vanilla rules: matching attributes, freshness and so on.
					if (rs.Collectible.GetMergableQuantity(rs, ds, EnumMergePriority.AutoMerge) <= 0) continue;

					int receiverBefore = rs.StackSize;
					int moved = donor.Slot.TryPutInto(sapi.World, recv.Slot, Math.Min(space, ds.StackSize));
					if (moved <= 0)
					{
						// The merge check can run the transition update, which spoils or converts a stack in place
						// without moving anything. Keep the entities and clients in step with the slots.
						ResyncStack(donor);
						ResyncStack(recv);
						continue;
					}

					MergeDespawnClock(donor, recv, receiverBefore, moved);

					// The Itemstack setter does not mark the path dirty, so do it explicitly (as vanilla BlockMeal does),
					// otherwise clients that already track the item keep the old stack size.
					recv.Itemstack = recv.Slot.Itemstack;
					recv.WatchedAttributes.MarkPathDirty("itemstack");
					MarkChunkModified(recv);
				}

				ItemStack left = donor.Slot.Itemstack;
				if (left == null || left.StackSize <= 0)
				{
					// Die only flags the entity, the chunk is dirtied when it is removed on a later entity tick.
					// Marking it now keeps a crash in between from saving the receiver's new count next to the
					// donor's old one.
					if (donor.Alive)
					{
						MarkChunkModified(donor);
						donor.Die(EnumDespawnReason.Removed);
					}
				}
				else
				{
					donor.Itemstack = left;
					donor.WatchedAttributes.MarkPathDirty("itemstack");
					MarkChunkModified(donor);
				}
			}
		}
		finally
		{
			group.Clear();
			claimOf.Clear();
		}
	}

	private void ResyncStack(EntityItem e)
	{
		if (!e.Alive) return;

		ItemStack stack = e.Slot.Itemstack;
		if (stack == null || stack.StackSize <= 0)
		{
			MarkChunkModified(e);
			e.Die(EnumDespawnReason.Removed);
			return;
		}

		e.Itemstack = stack;
		e.WatchedAttributes.MarkPathDirty("itemstack");
		MarkChunkModified(e);
	}

	private bool InMergeRange(EntityItem a, EntityItem b)
	{
		EntityPos pa = a.Pos;
		EntityPos pb = b.Pos;
		double dx = pa.X - pb.X, dz = pa.Z - pb.Z;
		return dx * dx + dz * dz <= (double)radius * radius && Math.Abs(pa.Y - pb.Y) <= verticalRadius;
	}

	// True when both stacks lie outside every claim, or share their first claim. The claim of a stack is
	// looked up once per group.
	private bool SameClaim(EntityItem a, EntityItem b)
	{
		return ClaimOf(a) == ClaimOf(b);
	}

	private LandClaim ClaimOf(EntityItem e)
	{
		if (claimOf.TryGetValue(e, out LandClaim claim)) return claim;

		LandClaim[] claims = sapi.World.Claims.Get(e.Pos.AsBlockPos);
		claim = claims != null && claims.Length > 0 ? claims[0] : null;
		claimOf[e] = claim;
		return claim;
	}

	private bool MatchEntity(Entity e)
	{
		return e is EntityItem ei
			&& ei.Alive
			&& ei.Slot?.Itemstack is ItemStack stack
			&& stack.Collectible == matchCollectible
			&& stack.StackSize < matchCollectible.MaxStackSize;
	}

	private static int CompareBySizeDesc(EntityItem a, EntityItem b)
	{
		int cmp = b.Slot.Itemstack.StackSize.CompareTo(a.Slot.Itemstack.StackSize);
		return cmp != 0 ? cmp : a.EntityId.CompareTo(b.EntityId);
	}

	private static bool IsValid(EntityItem e)
	{
		return e != null
			&& e.Alive
			&& e.World != null
			&& e.Slot?.Itemstack != null
			&& e.Slot.Itemstack.StackSize > 0;
	}

	// Per entity override of the despawn time (a death drop is kept for an hour); 0 means the default.
	private static int DespawnOverride(EntityItem e)
	{
		return e.Attributes.GetInt("minsecondsToDespawn");
	}

	// deathTime is elapsed time, not remaining time: EntityBehaviorDespawn adds to it and kills the item
	// once it passes minSeconds. The receiver keeps the average of both clocks weighted by the quantity each
	// side contributes, so the pile ages like the items it holds and a trickle of fresh drops cannot keep
	// it alive for ever.
	private static void MergeDespawnClock(EntityItem donor, EntityItem recv, int receiverBefore, int moved)
	{
		EntityBehaviorDespawn donorClock = donor.GetBehavior<EntityBehaviorDespawn>();
		EntityBehaviorDespawn recvClock = recv.GetBehavior<EntityBehaviorDespawn>();
		if (donorClock == null || recvClock == null) return;

		double total = (double)receiverBefore + moved;
		if (total <= 0) return;

		recvClock.DeathTime = (float)((recvClock.DeathTime * (double)receiverBefore + donorClock.DeathTime * (double)moved) / total);
	}

	// Entity merges change an entity of a chunk that nothing else has dirtied. Autosave, unload and
	// shutdown write only chunks marked for saving. InChunkIndex3d is the chunk the entity is listed
	// in (it follows the entity once a second), which is the chunk that gets written.
	private void MarkChunkModified(EntityItem e)
	{
		sapi.WorldManager.GetChunk(e.InChunkIndex3d)?.MarkModified();
	}

	// The inverse of EntityItem.CanCollect: a player dropped it (ByPlayerUid is set) and less than
	// the pickup delay has passed since it spawned.
	private static bool InPickupWindow(EntityItem e, long now)
	{
		return e.ByPlayerUid != null && now - e.itemSpawnedMilliseconds <= PickupWindowMs;
	}

	private bool NoPlayerNear(EntityPos ep)
	{
		Vec3d pos = ep.XYZ;
		int r = frozenRange;
		foreach (IPlayer player in sapi.World.AllOnlinePlayers)
		{
			EntityPlayer eplr = player.Entity;
			if (eplr != null && eplr.Pos.InRangeOf(pos, r * r, r))
			{
				return false;
			}
		}
		return true;
	}

	private static double SqDist(double x1, double y1, double z1, double x2, double y2, double z2)
	{
		double dx = x1 - x2, dy = y1 - y2, dz = z1 - z2;
		return dx * dx + dy * dy + dz * dz;
	}

	private static bool IsSettled(EntityItem e)
	{
		// Support is ground or liquid (an item in water or lava floats instead of lying).
		// An item moving through the air has not settled yet.
		if (!e.OnGround && !e.FeetInLiquid && !e.Swimming) return false;
		Vec3d m = e.Pos.Motion;
		return m.X * m.X + m.Y * m.Y + m.Z * m.Z <= SettledMotionSq;
	}
}
