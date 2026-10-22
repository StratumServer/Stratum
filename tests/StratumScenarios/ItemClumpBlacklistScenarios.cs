using Atlas.Api;
using Atlas.XUnit;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Performance.ItemClump.Blacklist: a blacklisted collectible never merges, whether the entry is a plain
/// code (without a domain, so it is read as game:), a '*' pattern or an "@regex". A stone that matches no
/// entry still merges, so the scenario also proves the system was running.
///
/// The server reads the blacklist once at start, and the scenarios share one server, so the patterns are
/// installed through the same NormalizeBlacklist the config path uses, set on the running system by
/// reflection. A missing member fails with a descriptive message.
/// </summary>
public class ItemClumpBlacklistScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task BlacklistedStones_Should_StayApart_WhileOtherStonesMerge()
	{
		ICoreServerAPI api = World.Api;
		ITestPlayer player = await World.JoinPlayer("clump-blacklist");
		await World.Ticks(2);

		ModSystem system = RequireSystem(api);
		InstallBlacklist(system, "stone-granite", "STONE-AND*", "@^game:stone-chert$");

		EntityItem[] granite = Pair(api, player, "game:stone-granite", 0);
		EntityItem[] andesite = Pair(api, player, "game:stone-andesite", 8);
		EntityItem[] chert = Pair(api, player, "game:stone-chert", 16);
		EntityItem[] basalt = Pair(api, player, "game:stone-basalt", 24);

		// The control merges, which also means the delay and the settling time are over.
		await World.Until(() => !basalt[1].Alive, timeoutTicks: 300);
		Assert.True(
			basalt[0].Alive && basalt[0].Slot.Itemstack.StackSize == 6,
			"setup failed: the stone that is not blacklisted did not merge, so the system was not running");

		AssertApart(granite, "plain code without a domain");
		AssertApart(andesite, "'*' pattern, upper case");
		AssertApart(chert, "@regex");
	}

	private static void AssertApart(EntityItem[] pair, string entry)
	{
		Assert.True(
			pair[0].Alive && pair[1].Alive
				&& pair[0].Slot.Itemstack.StackSize == 5 && pair[1].Slot.Itemstack.StackSize == 1,
			$"a blacklisted stone merged ({entry}): "
			+ $"pile alive={pair[0].Alive} size={pair[0].Slot.Itemstack?.StackSize}, "
			+ $"single alive={pair[1].Alive} size={pair[1].Slot.Itemstack?.StackSize}");
	}

	// A resting pile of 5 and a single stone next to it, both without a dropper.
	private EntityItem[] Pair(ICoreServerAPI api, ITestPlayer player, string code, int zOffset)
	{
		Item item = api.World.GetItem(new AssetLocation(code));
		Assert.True(item != null, $"item '{code}' not found.");
		Vec3d pos = player.Player.Entity.Pos.XYZ.AddCopy(1.2, 0.2, 0.2 + zOffset);
		EntityItem pile = (EntityItem)api.World.SpawnItemEntity(new ItemStack(item, 5), pos, new Vec3d());
		EntityItem single = (EntityItem)api.World.SpawnItemEntity(new ItemStack(item, 1), pos, new Vec3d());
		return new[] { pile, single };
	}

	private static ModSystem RequireSystem(ICoreServerAPI api)
	{
		foreach (ModSystem system in api.ModLoader.Systems)
		{
			if (system.GetType().FullName == "Vintagestory.GameContent.ModSystemItemClump") return system;
		}
		throw new InvalidOperationException("ModSystemItemClump is not loaded: Performance.ItemClump is disabled or the server is not the patched one.");
	}

	private static void InstallBlacklist(ModSystem system, params string[] entries)
	{
		const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
		Type type = system.GetType();

		MethodInfo? normalize = type.GetMethod("NormalizeBlacklist", all);
		FieldInfo? patterns = type.GetField("blacklistPatterns", all);
		FieldInfo? cache = type.GetField("blacklistCache", all);
		Assert.True(normalize != null && patterns != null && cache != null,
			"ModSystemItemClump.NormalizeBlacklist, blacklistPatterns or blacklistCache is missing: it was renamed or removed.");

		object normalized = normalize!.Invoke(null, new object[] { new List<string>(entries) })!;
		patterns!.SetValue(system, normalized);
		cache!.FieldType.GetMethod("Clear")!.Invoke(cache.GetValue(system), null);
	}
}
