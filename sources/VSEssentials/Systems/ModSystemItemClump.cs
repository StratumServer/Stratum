using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

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
//  - Small default radius (5 horizontal, 2 vertical): items do not vanish far from where they lay
//    and do not merge across floors.
//  - Largest stacks receive, smallest stacks donate, which minimizes the resulting entity count.
//  - Blacklist uses vanilla WildcardUtil.Match ('*', case insensitive, "@regex"). The result is cached
//    per collectible, so Code.ToString() runs once.
// Merging goes through ItemSlot.TryPutInto and GetMergableQuantity, so all vanilla rules
// (attributes, freshness and other transition states) still apply.
public class ModSystemItemClump : ModSystem
{
	// Tick period. Precision does not matter, a few times per second is enough.
	private const int TickIntervalMs = 250;

	// An item counts as settled when its squared speed is at most this value.
	private const double SettledMotionSq = 0.005d;

	private ICoreServerAPI sapi;
	private long tickListenerId;
	private bool running;

	// Queue ordered by due time, plus an index by EntityId for O(1) dedup and cancel.
	private readonly Queue<ItemClumpCandidate> queue = new Queue<ItemClumpCandidate>();
	private readonly Dictionary<long, ItemClumpCandidate> byId = new Dictionary<long, ItemClumpCandidate>();

	// Reused buffer (the server tick is single threaded).
	private readonly List<EntityItem> group = new List<EntityItem>();

	// Blacklist result per collectible.
	private readonly Dictionary<CollectibleObject, bool> blacklistCache = new Dictionary<CollectibleObject, bool>();
	private string[] blacklistPatterns = Array.Empty<string>();

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

	// Debug statistics (Debug in the config).
	private bool debug;
	private long debugNextLogMs;
	private int dbgEnqueued, dbgProcessed, dbgInvalid, dbgFrozen, dbgRetried,
		dbgGaveUp, dbgSearchLt2, dbgGroupLt2, dbgMerged, dbgKilled, dbgQueueMax, dbgHovering;
	private int dbgSamples;

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
		blacklistPatterns = NormalizeBlacklist(cfg.Blacklist);
		blacklistCache.Clear();

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

		debug = cfg.Debug;
		debugNextLogMs = 0;
		api.Logger.Notification("[Stratum] ItemClump: enabled, radius={0}/{1}, frozenRange={2}, debug={3}",
			radius, verticalRadius, frozenRange, debug);

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
		group.Clear();
		blacklistCache.Clear();
		matchCollectible = null;
		sapi = null;
		running = false;
	}

	private void OnEntitySpawn(Entity entity)
	{
		if (entity is not EntityItem item) return;
		Enqueue(item);
	}

	private void OnEntityDespawn(Entity entity, EntityDespawnData despawn)
	{
		if (entity is not EntityItem item) return;
		if (byId.Remove(item.EntityId, out ItemClumpCandidate c))
		{
			c.Cancelled = true; // the queue entry is skipped when it reaches the head
		}
	}

	private void Enqueue(EntityItem item)
	{
		if (byId.ContainsKey(item.EntityId)) return;

		ItemClumpCandidate c = new ItemClumpCandidate
		{
			Entity = item,
			EntityId = item.EntityId,
			DueMs = sapi.World.ElapsedMilliseconds + delayMs
		};
		byId[c.EntityId] = c;
		queue.Enqueue(c);
		dbgEnqueued++;
	}

	private void OnTick(float dt)
	{
		long now = sapi.World.ElapsedMilliseconds;

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
			dbgProcessed++;

			if (!IsValid(e))
			{
				queue.Dequeue();
				byId.Remove(c.EntityId);
				dbgInvalid++;
				continue;
			}

			// An item far from players is not simulated, it is frozen and never moves, so waiting
			// for it to settle is pointless and it merges right away. Freeze detection, most reliable first:
			//  1) the server marked the entity inactive (outside the simulation range);
			//  2) no player is near the item (by simulation range);
			//  3) below: the item hangs without support and did not move between two checks.
			bool frozen = e.State == EnumEntityState.Inactive || NoPlayerNear(e.ServerPos);

			if (!frozen && !IsSettled(e))
			{
				EntityPos p = e.ServerPos;

				// Live physics never lets an unsupported item stand still, it has to move within
				// 1.5 s. An unchanged position means it hangs in the air outside the simulation
				// range. Nothing to wait for, treat it as frozen.
				if (c.HasLastPos && SqDist(p.X, p.Y, p.Z, c.LastX, c.LastY, c.LastZ) < 1e-4)
				{
					frozen = true;
					dbgHovering++;
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
						dbgGaveUp++;
					}
					else
					{
						c.DueMs = now + delayMs;
						queue.Enqueue(c);
						dbgRetried++;
					}
					continue;
				}
			}

			if (frozen) dbgFrozen++;

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

		if (debug) DebugLog(now);
	}

	private void DebugLog(long now)
	{
		if (queue.Count > dbgQueueMax) dbgQueueMax = queue.Count;
		if (now < debugNextLogMs) return;
		debugNextLogMs = now + 10000;

		sapi.Logger.Notification(
			"[Stratum] ItemClump 10s: enqueued={0} processed={1} invalid={2} frozen={3} (hovering={12}) retried={4} gaveUp={5} " +
			"search<2={6} group<2={7} mergedOps={8} donorsKilled={9} queue={10} (max {11})",
			dbgEnqueued, dbgProcessed, dbgInvalid, dbgFrozen, dbgRetried, dbgGaveUp,
			dbgSearchLt2, dbgGroupLt2, dbgMerged, dbgKilled, queue.Count, dbgQueueMax, dbgHovering);

		dbgEnqueued = dbgProcessed = dbgInvalid = dbgFrozen = dbgRetried = dbgGaveUp =
			dbgSearchLt2 = dbgGroupLt2 = dbgMerged = dbgKilled = dbgQueueMax = dbgHovering = 0;
		dbgSamples = 0;
	}

	private void TryClump(EntityItem root, bool frozen)
	{
		ItemStack rootStack = root.Slot.Itemstack;
		CollectibleObject coll = rootStack.Collectible;

		// A non stackable or already full stack can neither receive nor meaningfully donate.
		if (coll.MaxStackSize <= 1 || rootStack.StackSize >= coll.MaxStackSize) return;
		if (IsBlacklisted(coll)) return;

		matchCollectible = coll;
		Entity[] found;
		try
		{
			found = sapi.World.GetEntitiesAround(root.ServerPos.XYZ, radius, verticalRadius, matcher);
		}
		finally
		{
			matchCollectible = null;
		}

		if (debug && dbgSamples < 5)
		{
			dbgSamples++;
			sapi.Logger.Notification("[Stratum] ItemClump sample: {0} x{1} at {2} frozen={3} found={4}",
				coll.Code, rootStack.StackSize, root.ServerPos.XYZ, frozen, found?.Length ?? -1);
		}

		if (found == null || found.Length < 2)
		{
			dbgSearchLt2++;
			return;
		}

		group.Clear();
		group.Add(root);
		for (int i = 0; i < found.Length && group.Count < maxGroupSize; i++)
		{
			if (found[i] is not EntityItem other || other == root) continue;
			if (!IsValid(other) || (!frozen && !IsSettled(other))) continue;
			group.Add(other);
		}

		if (group.Count < 2)
		{
			dbgGroupLt2++;
			group.Clear();
			return;
		}

		// Largest stacks first (receivers), ties broken by EntityId for determinism.
		group.Sort(bySizeDesc);

		// Donors go from the end (smallest), receivers from the start (largest).
		for (int d = group.Count - 1; d > 0; d--)
		{
			EntityItem donor = group[d];

			for (int r = 0; r < d; r++)
			{
				ItemStack ds = donor.Slot.Itemstack;
				if (ds == null || ds.StackSize <= 0) break;

				EntityItem recv = group[r];
				ItemStack rs = recv.Slot.Itemstack;
				if (rs == null) continue;

				int space = rs.Collectible.MaxStackSize - rs.StackSize;
				if (space <= 0) continue;

				// Vanilla rules: matching attributes, freshness and so on.
				if (rs.Collectible.GetMergableQuantity(rs, ds, EnumMergePriority.AutoMerge) <= 0) continue;

				int moved = donor.Slot.TryPutInto(sapi.World, recv.Slot, Math.Min(space, ds.StackSize));
				if (moved <= 0) continue;

				// Reassigning through the Itemstack property marks the watched attributes dirty,
				// otherwise clients keep seeing the old stack size.
				recv.Itemstack = recv.Slot.Itemstack;
				dbgMerged++;
			}

			ItemStack left = donor.Slot.Itemstack;
			if (left == null || left.StackSize <= 0)
			{
				donor.Die(EnumDespawnReason.Removed);
				dbgKilled++;
			}
			else
			{
				donor.Itemstack = left;
			}
		}

		group.Clear();
	}

	private bool MatchEntity(Entity e)
	{
		return e is EntityItem ei
			&& ei.Alive
			&& ei.Slot?.Itemstack?.Collectible == matchCollectible;
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
		Vec3d m = e.ServerPos.Motion;
		return m.X * m.X + m.Y * m.Y + m.Z * m.Z <= SettledMotionSq;
	}

	private static string[] NormalizeBlacklist(List<string> raw)
	{
		if (raw == null) return Array.Empty<string>();

		return raw
			.Where(entry => !string.IsNullOrWhiteSpace(entry))
			.Select(entry => AddDefaultDomain(entry.Trim()))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	// A code without a domain is vanilla: "gear-rusty" becomes "game:gear-rusty".
	// Regex patterns ("@...") are left alone.
	private static string AddDefaultDomain(string code)
	{
		return code[0] == '@' || code.Contains(':') ? code : "game:" + code;
	}

	private bool IsBlacklisted(CollectibleObject coll)
	{
		if (blacklistPatterns.Length == 0) return false;
		if (blacklistCache.TryGetValue(coll, out bool cached)) return cached;

		bool hit = false;
		if (coll.Code != null)
		{
			// Once per collectible (cached afterwards), so the string allocation is fine here.
			string code = coll.Code.ToString();
			hit = WildcardUtil.Match(blacklistPatterns, code);
		}

		blacklistCache[coll] = hit;
		return hit;
	}
}
