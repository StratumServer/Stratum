using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Config;

namespace Vintagestory.Server;

/// <summary>
/// One item inside a kit. StackJson stores a JsonItemStack with a stable asset code and the stack
/// attributes. Code and Quantity are human-readable mirrors so a server owner can tell what a kit
/// contains and hand-edit Quantity without needing to know item codes. Base64 remains as a legacy
/// fallback for kits saved by an earlier build.
/// </summary>
internal sealed class StratumKitItem
{
	public string StackJson { get; set; }

	/// <summary>
	/// Legacy ItemStack serialization. New kits use StackJson instead.
	/// </summary>
	public string Base64 { get; set; }

	public string Code { get; set; }

	public int Quantity { get; set; }
}

internal sealed class StratumKitDefinition
{
	public string Name { get; set; }

	public List<StratumKitItem> Items { get; set; } = new List<StratumKitItem>();

	// Scope strings, e.g. "role:admin". Empty means every player holding the stratum.kits
	// privilege. Kept as a general scope rather than a role-only field on purpose: #203 wants team
	// kits later, and AssignedTo can grow a "team:" prefix without a rewrite.
	public List<string> AssignedTo { get; set; } = new List<string>();

	public int CooldownSeconds { get; set; }

	public bool OnePerLife { get; set; }

	public bool GiveOnRespawn { get; set; }

	// "all" (default) snapshots hotbar and character inventory, worn armor and backpack included.
	// "hotbar" snapshots only the hotbar (which already includes the offhand slot). Set with
	// /kitedit setscope; applied on the next create, not retroactively to Items already stored.
	public string Scope { get; set; } = "all";

	public string CreatedByUid { get; set; }

	public string CreatedByName { get; set; }

	public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// Global, not per-player, so it does not fit CustomPlayerData the way StratumModerationStore and
/// StratumAnticheatHistory do. Its own sidecar file instead, lazily loaded on first use rather than
/// wired into StratumRuntime's config load/save cycle: kits are staff-authored operational data
/// created and edited live via /kitedit, not tunable settings meant to be replaced wholesale on
/// every config save the way Commands/Performance are.
/// </summary>
internal static class StratumKitStore
{
	private static List<StratumKitDefinition> kits;
	private static string path;

	// Called once from CmdStratumKits's constructor, unconditionally, the same way
	// StratumRuntime.LoadOrCreateConfig runs fresh on every server boot rather than caching
	// across a process's lifetime: GamePaths.Config is only correct for the CURRENT boot's data
	// directory, so a lazy load-once-ever would keep serving (and saving to) a stale path after a
	// restart that reuses the process.
	public static void Load()
	{
		path = Path.Combine(GamePaths.Config, "stratum-kits.json");
		try
		{
			kits = File.Exists(path)
				? JsonConvert.DeserializeObject<List<StratumKitDefinition>>(File.ReadAllText(path)) ?? new List<StratumKitDefinition>()
				: new List<StratumKitDefinition>();
		}
		catch (Exception exception)
		{
			StratumRuntime.LogWarning("failed to load " + path + ": " + exception.Message);
			kits = new List<StratumKitDefinition>();
		}
	}

	// Defensive fallback only, for the unlikely case something reaches the store before
	// CmdStratumKits's constructor has run Load().
	private static void EnsureLoaded()
	{
		if (kits == null)
		{
			Load();
		}
	}

	public static IReadOnlyList<StratumKitDefinition> All()
	{
		EnsureLoaded();
		return kits;
	}

	public static StratumKitDefinition Find(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return null;
		}

		EnsureLoaded();
		return kits.FirstOrDefault(kit => string.Equals(kit.Name, name, StringComparison.OrdinalIgnoreCase));
	}

	public static void Upsert(StratumKitDefinition kit)
	{
		EnsureLoaded();
		kits.RemoveAll(existing => string.Equals(existing.Name, kit.Name, StringComparison.OrdinalIgnoreCase));
		kits.Add(kit);
		Save();
	}

	public static bool Delete(string name)
	{
		EnsureLoaded();
		int removed = kits.RemoveAll(existing => string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase));
		if (removed > 0)
		{
			Save();
		}

		return removed > 0;
	}

	public static bool Rename(string name, string newName)
	{
		StratumKitDefinition kit = Find(name);
		if (kit == null || Find(newName) != null)
		{
			return false;
		}

		kit.Name = newName;
		Save();
		return true;
	}

	public static void Save()
	{
		EnsureLoaded();
		File.WriteAllText(path, JsonConvert.SerializeObject(kits, Formatting.Indented));
	}
}
