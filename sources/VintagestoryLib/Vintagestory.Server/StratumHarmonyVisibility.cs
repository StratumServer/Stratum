using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

// Stratum's own changes are compiled into VintagestoryLib/VSEssentials/VSSurvivalMod at build
// time, not applied as runtime Harmony patches, so there's nothing here for Stratum to skip or
// gate the way Synergy's ConflictDetector does for its own patches. This is visibility only: a
// third-party mod's Harmony *transpiler* against a method Stratum has source-patched is pattern
// matching IL it expects to look like vanilla, and can fail with no error (or, rarer, match the
// wrong spot) if Stratum's change reshaped that method. Prefix/postfix/finalizer patches just wrap the
// call and aren't affected the same way. This logs what every loaded mod has patched so that's a
// fast lookup during triage instead of a guess; it does not attempt to cross-reference against
// Stratum's own changes (that would need a build-time method manifest -- see
// research/gap-mod-patch-conflict-visibility.md in the contributions repo for why that turned out
// to be a much bigger lift than a startup log).
internal static class StratumHarmonyVisibility
{
	public static void LogPatchedMethods(ServerMain server)
	{
		StratumDiagnosticsConfig config = StratumRuntime.Config.Diagnostics;
		if (config == null || !config.LogModHarmonyPatches)
		{
			return;
		}

		Dictionary<string, OwnerCounts> byOwner = new Dictionary<string, OwnerCounts>();
		int patchedMethodCount = 0;

		foreach (MethodBase method in Harmony.GetAllPatchedMethods())
		{
			Patches info = Harmony.GetPatchInfo(method);
			if (info == null)
			{
				continue;
			}

			patchedMethodCount++;
			CountOwners(byOwner, info.Prefixes, counts => counts.Prefixes++);
			CountOwners(byOwner, info.Postfixes, counts => counts.Postfixes++);
			CountOwners(byOwner, info.Transpilers, counts => counts.Transpilers++);
			CountOwners(byOwner, info.Finalizers, counts => counts.Finalizers++);
		}

		if (byOwner.Count == 0)
		{
			StratumRuntime.LogInfo("harmony visibility: no mod-owned Harmony patches detected");
			return;
		}

		int transpilerOwners = byOwner.Count(entry => entry.Value.Transpilers > 0);
		StratumRuntime.LogInfo($"harmony visibility: {byOwner.Count} mod(s) patch {patchedMethodCount} method(s) total ({transpilerOwners} mod(s) use a transpiler)");

		foreach (KeyValuePair<string, OwnerCounts> entry in byOwner.OrderByDescending(e => e.Value.Transpilers).ThenBy(e => e.Key, System.StringComparer.OrdinalIgnoreCase))
		{
			OwnerCounts counts = entry.Value;
			string detail = $"prefix={counts.Prefixes} postfix={counts.Postfixes} transpiler={counts.Transpilers} finalizer={counts.Finalizers}";
			if (counts.Transpilers > 0)
			{
				StratumRuntime.LogWarning($"harmony visibility: mod '{entry.Key}' uses a transpiler ({detail}) -- if it targets a method Stratum has source-patched, verify the mod's feature works instead of assuming it does");
			}
			else
			{
				StratumRuntime.LogInfo($"harmony visibility: mod '{entry.Key}' ({detail})");
			}
		}
	}

	// Methods the group friendly fire toggle (#277) relies on to drop a hit between group mates.
	// A mod that Harmony-patches one of these and skips the original can silently defeat the
	// toggle. No server platform can stop that, so when friendly fire is off we name the mod in the
	// log for triage. Keyed by the declaring type's simple name plus the method name, which is
	// enough to be unambiguous here and avoids caring about overloads or full namespaces.
	private static readonly HashSet<string> FriendlyFireCriticalMethods = new HashSet<string>(System.StringComparer.Ordinal)
	{
		"Entity.ReceiveDamage",
		"Entity.ShouldReceiveDamage",
		"EntityAgent.ReceiveDamage",
		"EntityAgent.ShouldReceiveDamage",
		"EntityHumanoid.ShouldReceiveDamage",
		"EntityPlayer.ShouldReceiveDamage",
		"EntityAgent.OnInteract",
		"EntityBehaviorHealth.OnEntityReceiveDamage",
		"ServerSystemEntitySimulation.HandleEntityInteraction",
		"EntityProjectileBase.CanDealDamage",
		"EntityProjectileBase.DealDamage",
		"EntityProjectileBase.ImpactOnEntity",
		"ServerMain.CreateExplosion",
	};

	// Runs regardless of Diagnostics.LogModHarmonyPatches: this is a targeted safety warning for
	// one feature, not the full patch dump. It is called after the friendly-fire state is applied,
	// including startup, /friendlyfire changes, and /stratum reload.
	public static void WarnFriendlyFireConflicts()
	{
		if (!StratumFriendlyFireHook.BlockGroupDamage)
		{
			return;
		}

		foreach (MethodBase method in Harmony.GetAllPatchedMethods())
		{
			string key = (method.DeclaringType?.Name ?? "?") + "." + method.Name;
			if (!FriendlyFireCriticalMethods.Contains(key))
			{
				continue;
			}

			Patches info = Harmony.GetPatchInfo(method);
			if (info == null)
			{
				continue;
			}

			HashSet<string> owners = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
			CollectOwners(owners, info.Prefixes);
			CollectOwners(owners, info.Postfixes);
			CollectOwners(owners, info.Transpilers);
			CollectOwners(owners, info.Finalizers);
			if (owners.Count == 0)
			{
				continue;
			}

			StratumRuntime.LogWarning($"harmony visibility: group friendly fire (/friendlyfire) relies on {key}, which mod(s) {string.Join(", ", owners.OrderBy(o => o, System.StringComparer.OrdinalIgnoreCase))} also patch. Confirm a hit between two group members is still blocked with that mod loaded.");
		}
	}

	private static void CollectOwners(HashSet<string> owners, IReadOnlyCollection<Patch> patches)
	{
		if (patches == null)
		{
			return;
		}

		foreach (Patch patch in patches)
		{
			owners.Add(string.IsNullOrWhiteSpace(patch.owner) ? "(unknown)" : patch.owner);
		}
	}

	private static void CountOwners(Dictionary<string, OwnerCounts> byOwner, IReadOnlyCollection<Patch> patches, System.Action<OwnerCounts> increment)
	{
		if (patches == null)
		{
			return;
		}

		foreach (Patch patch in patches)
		{
			string owner = string.IsNullOrWhiteSpace(patch.owner) ? "(unknown)" : patch.owner;
			if (!byOwner.TryGetValue(owner, out OwnerCounts counts))
			{
				counts = new OwnerCounts();
				byOwner[owner] = counts;
			}

			increment(counts);
		}
	}

	private sealed class OwnerCounts
	{
		public int Prefixes;
		public int Postfixes;
		public int Transpilers;
		public int Finalizers;
	}
}
