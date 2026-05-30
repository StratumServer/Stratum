using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace Vintagestory.Server;

/// <summary>
/// Per-chunk-column entity cap enforcement. Runs every
/// <see cref="StratumEntityTickingConfig.EntityCapsEnforcementIntervalSeconds"/> seconds and,
/// for any chunk column that has more loaded creatures or ground items than the configured cap,
/// flags the oldest excess for despawn. The simulation system's existing per-tick despawn pass
/// picks them up; we don't call <c>DespawnEntity</c> from here.
///
/// Creatures honor the same exempt-code-prefix list and named-entity rule as
/// <see cref="ServerSystemEntitySimulation"/>'s hard-despawn so player livestock and tamed mobs
/// aren't culled. Item entities ignore those exemptions; uncollected loot is fair game.
/// </summary>
internal sealed class StratumEntityCaps
{
	private readonly ServerMain server;
	private const int TickIntervalMs = 1000;
	private float secsSinceEnforce;

	public StratumEntityCaps(ServerMain server)
	{
		this.server = server;
		server.RegisterGameTickListener(OnTick, TickIntervalMs);
	}

	private void OnTick(float dt)
	{
		StratumEntityTickingConfig cfg = StratumRuntime.Config?.Performance?.EntityTicking;
		if (cfg == null || !cfg.EntityCapsEnabled) return;

		secsSinceEnforce += dt;
		if (secsSinceEnforce < cfg.EntityCapsEnforcementIntervalSeconds) return;
		secsSinceEnforce = 0f;

		Enforce(cfg);
	}

	private void Enforce(StratumEntityTickingConfig cfg)
	{
		int maxC = Math.Max(0, cfg.MaxCreaturesPerChunkColumn);
		int maxI = Math.Max(0, cfg.MaxItemEntitiesPerChunkColumn);
		if (maxC == 0 && maxI == 0) return;

		string[] exempt = (cfg.HardDespawnExemptCodePrefixes != null && cfg.HardDespawnExemptCodePrefixes.Count > 0)
			? cfg.HardDespawnExemptCodePrefixes.ToArray()
			: Array.Empty<string>();
		bool exemptNamed = cfg.HardDespawnExemptIfNamed;

		Dictionary<long, List<Entity>> creatures = null;
		Dictionary<long, List<Entity>> items = null;

		foreach (Entity e in server.LoadedEntities.Values)
		{
			if (e == null || e is EntityPlayer || e.ShouldDespawn) continue;

			// Column key: chunkX (24b) | chunkZ (24b) | dimension (8b). Floor-shift so
			// negative coords bucket correctly.
			int cx = (int)Math.Floor(e.Pos.X / 32.0);
			int cz = (int)Math.Floor(e.Pos.Z / 32.0);
			int dim = e.Pos.Dimension & 0xFF;
			long key = ((long)(uint)(cx & 0xFFFFFF))
				| (((long)(uint)(cz & 0xFFFFFF)) << 24)
				| ((long)dim << 48);

			if (e is EntityItem)
			{
				if (maxI <= 0) continue;
				(items ??= new Dictionary<long, List<Entity>>())
					.TryGetValue(key, out List<Entity> li);
				if (li == null) items[key] = li = new List<Entity>();
				li.Add(e);
			}
			else if (e.IsCreature)
			{
				if (maxC <= 0) continue;
				if (IsExempt(e, exempt, exemptNamed)) continue;
				(creatures ??= new Dictionary<long, List<Entity>>())
					.TryGetValue(key, out List<Entity> lc);
				if (lc == null) creatures[key] = lc = new List<Entity>();
				lc.Add(e);
			}
		}

		int culledC = 0, culledI = 0;
		EntityDespawnData unloadReason = new EntityDespawnData { Reason = EnumDespawnReason.Unload };
		if (creatures != null && maxC > 0)
		{
			foreach (KeyValuePair<long, List<Entity>> kv in creatures)
			{
				if (kv.Value.Count <= maxC) continue;
				kv.Value.Sort(static (a, b) => a.EntityId.CompareTo(b.EntityId));
				int excess = kv.Value.Count - maxC;
				for (int i = 0; i < excess; i++)
				{
					Entity ent = kv.Value[i];
					if (ent.ShouldDespawn) continue;
					server.DespawnEntity(ent, unloadReason);
					culledC++;
				}
			}
		}
		if (items != null && maxI > 0)
		{
			foreach (KeyValuePair<long, List<Entity>> kv in items)
			{
				if (kv.Value.Count <= maxI) continue;
				kv.Value.Sort(static (a, b) => a.EntityId.CompareTo(b.EntityId));
				int excess = kv.Value.Count - maxI;
				for (int i = 0; i < excess; i++)
				{
					Entity ent = kv.Value[i];
					if (ent.ShouldDespawn) continue;
					server.DespawnEntity(ent, unloadReason);
					culledI++;
				}
			}
		}

		if (culledC > 0 || culledI > 0)
		{
			ServerMain.Logger.VerboseDebug("[Stratum] entity caps: culled {0} creatures, {1} item entities (interval={2}s, cap c/i = {3}/{4})",
				culledC, culledI, cfg.EntityCapsEnforcementIntervalSeconds, maxC, maxI);
		}
	}

	private static bool IsExempt(Entity e, string[] prefixes, bool exemptNamed)
	{
		string codePath = e.Code?.Path;
		if (codePath != null)
		{
			for (int i = 0; i < prefixes.Length; i++)
			{
				if (codePath.StartsWith(prefixes[i], StringComparison.Ordinal)) return true;
			}
		}
		if (exemptNamed)
		{
			string named = e.WatchedAttributes?.GetTreeAttribute("nametag")?.GetString("name");
			if (!string.IsNullOrEmpty(named)) return true;
		}
		return false;
	}
}
