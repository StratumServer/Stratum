using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace Vintagestory.Server;

// Stratum: identical ground stacks share one entity. The index only holds piles
// that have already landed, so two stones still in the air stay apart.
internal static class StratumGroundItems
{
	const int CellShift = 2;

	static readonly object gate = new object();

	static readonly Dictionary<long, List<EntityItem>> cells = new Dictionary<long, List<EntityItem>>();

	static readonly HashSet<long> resting = new HashSet<long>();

	public static void Install()
	{
		EntityItem.StratumAfterServerTick = OnAfterServerTick;
		EntityItem.StratumOnDie = OnDie;
		cells.Clear();
		resting.Clear();
	}

	static void OnAfterServerTick(EntityItem item)
	{
		StratumGroundItemsConfig config = Config;
		if (config == null || !config.Enabled || item == null)
		{
			return;
		}

		// Item ticks run in parallel. The index is the only shared structure.
		lock (gate)
		{
			if (!item.Alive || item.Itemstack == null || item.Itemstack.StackSize <= 0)
			{
				Unregister(item);
				return;
			}

			if (!item.OnGround || item.Pos.Motion.LengthSq() > 0.01)
			{
				return;
			}

			if (resting.Contains(item.EntityId))
			{
				return;
			}

			if (TryAbsorb(item, config.RadiusBlocks))
			{
				item.Die(EnumDespawnReason.Removed);
				return;
			}

			Register(item);
		}
	}

	static void OnDie(EntityItem item)
	{
		lock (gate)
		{
			Unregister(item);
		}
	}

	static bool TryAbsorb(EntityItem donor, int radiusBlocks)
	{
		ItemStack incoming = donor.Itemstack;
		if (incoming?.Collectible == null && incoming?.ResolveBlockOrItem(donor.World) != true)
		{
			return false;
		}

		if (incoming.Collectible.MaxStackSize <= 1)
		{
			return false;
		}

		EntityItem survivor = FindSurvivor(donor, radiusBlocks);
		if (survivor == null)
		{
			return false;
		}

		survivor.Itemstack.StackSize += incoming.StackSize;
		survivor.WatchedAttributes.MarkPathDirty("itemstack");
		if (donor.itemSpawnedMilliseconds < survivor.itemSpawnedMilliseconds)
		{
			survivor.itemSpawnedMilliseconds = donor.itemSpawnedMilliseconds;
		}

		donor.Itemstack = null;
		return true;
	}

	static EntityItem FindSurvivor(EntityItem donor, int radiusBlocks)
	{
		int radiusSq = radiusBlocks * radiusBlocks;
		int cellRadius = (radiusBlocks + (1 << CellShift) - 1) >> CellShift;
		int x = donor.Pos.XInt;
		int z = donor.Pos.ZInt;
		int dim = donor.Pos.Dimension;
		EntityItem best = null;
		for (int dz = -cellRadius; dz <= cellRadius; dz++)
		{
			for (int dx = -cellRadius; dx <= cellRadius; dx++)
			{
				if (!cells.TryGetValue(CellKey(dim, x + (dx << CellShift), z + (dz << CellShift)), out List<EntityItem> list))
				{
					continue;
				}

				for (int i = list.Count - 1; i >= 0; i--)
				{
					EntityItem candidate = list[i];
					if (!UsablePile(candidate, donor, radiusSq))
					{
						if (candidate == null || !candidate.Alive)
						{
							list.RemoveAt(i);
						}
						continue;
					}

					if (best == null || candidate.itemSpawnedMilliseconds < best.itemSpawnedMilliseconds)
					{
						best = candidate;
					}
				}
			}
		}

		return best;
	}

	static bool UsablePile(EntityItem candidate, EntityItem donor, int radiusSq)
	{
		if (candidate == null || candidate == donor || !candidate.Alive || !candidate.OnGround)
		{
			return false;
		}

		ItemStack sink = candidate.Itemstack;
		ItemStack incoming = donor.Itemstack;
		if (sink?.Collectible == null || incoming?.Collectible == null)
		{
			return false;
		}

		if (!SameDropper(candidate.ByPlayerUid, donor.ByPlayerUid))
		{
			return false;
		}

		if (sink.StackSize + incoming.StackSize > sink.Collectible.MaxStackSize)
		{
			return false;
		}

		if (!sink.Equals(donor.World, incoming))
		{
			return false;
		}

		double dx = candidate.Pos.X - donor.Pos.X;
		double dy = candidate.Pos.Y - donor.Pos.Y;
		double dz = candidate.Pos.Z - donor.Pos.Z;
		return dx * dx + dy * dy + dz * dz <= radiusSq;
	}

	static bool SameDropper(string left, string right)
	{
		if (string.IsNullOrEmpty(left) && string.IsNullOrEmpty(right))
		{
			return true;
		}

		return string.Equals(left, right, StringComparison.Ordinal);
	}

	static void Register(EntityItem item)
	{
		long key = CellKey(item.Pos.Dimension, item.Pos.XInt, item.Pos.ZInt);
		if (!cells.TryGetValue(key, out List<EntityItem> list))
		{
			list = new List<EntityItem>(4);
			cells[key] = list;
		}

		list.Add(item);
		resting.Add(item.EntityId);
	}

	static void Unregister(EntityItem item)
	{
		if (item == null || !resting.Remove(item.EntityId))
		{
			return;
		}

		long key = CellKey(item.Pos.Dimension, item.Pos.XInt, item.Pos.ZInt);
		if (!cells.TryGetValue(key, out List<EntityItem> list))
		{
			return;
		}

		list.Remove(item);
		if (list.Count == 0)
		{
			cells.Remove(key);
		}
	}

	static long CellKey(int dimension, int x, int z)
	{
		int cellX = x >> CellShift;
		int cellZ = z >> CellShift;
		return ((long)dimension << 42) | ((long)(cellX & 0x1FFFFF) << 21) | (uint)(cellZ & 0x1FFFFF);
	}

	static StratumGroundItemsConfig Config => StratumRuntime.Config?.Performance?.GroundItems;
}
