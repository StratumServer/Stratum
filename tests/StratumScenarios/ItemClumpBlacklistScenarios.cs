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
		try
		{
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
		finally
		{
			InstallBlacklist(system);
		}
	}

	/// <summary>
	/// An "@regex" entry that does not compile is dropped when the list is normalized. Left in, it would throw
	/// from WildcardUtil.Match on every merge attempt, and the exception would take the server tick with it, so
	/// nothing would merge. A regex entry is case sensitive: an upper case one does not match a lower case code.
	/// </summary>
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task InvalidAndUpperCaseRegexEntries_Should_NotStopOtherMerges()
	{
		ICoreServerAPI api = World.Api;
		ITestPlayer player = await World.JoinPlayer("clump-badregex");
		await World.Ticks(2);

		ModSystem system = RequireSystem(api);
		try
		{
			InstallBlacklist(system, "@[stone", "@^GAME:STONE-BASALT$", "stone-limestone");

			EntityItem[] limestone = Pair(api, player, "game:stone-limestone", 32);
			EntityItem[] basalt = Pair(api, player, "game:stone-basalt", 40);

			await World.Until(() => !basalt[1].Alive, timeoutTicks: 300);
			Assert.True(
				basalt[0].Alive && basalt[0].Slot.Itemstack.StackSize == 6,
				"the stone merged less than expected: the upper case regex matched, or the invalid entry stopped the merges");
			AssertApart(limestone, "plain code next to an invalid regex entry");
		}
		finally
		{
			InstallBlacklist(system);
		}
	}

	/// <summary>
	/// A blacklisted item never enters the queue, so it cannot use a queue slot or a merge budget unit.
	/// </summary>
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task BlacklistedItems_Should_NotEnterTheQueue()
	{
		ICoreServerAPI api = World.Api;
		ITestPlayer player = await World.JoinPlayer("clump-queue");
		await World.Ticks(2);

		ModSystem system = RequireSystem(api);
		try
		{
			InstallBlacklist(system, "stone-peridotite");

			EntityItem[] blocked = Pair(api, player, "game:stone-peridotite", 48);
			EntityItem[] control = Pair(api, player, "game:stone-scoria", 56);

			// Both pairs are well inside the delay, so the control stays tracked while the others never were.
			await World.Ticks(10);
			System.Collections.IDictionary tracked = (System.Collections.IDictionary)system.GetType()
				.GetField("byId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(system)!;
			Assert.True(tracked.Contains(control[0].EntityId), "setup failed: the control is not tracked by the system");
			Assert.False(tracked.Contains(blocked[0].EntityId) || tracked.Contains(blocked[1].EntityId),
				"a blacklisted item was queued for merging");
		}
		finally
		{
			InstallBlacklist(system);
		}
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task DebugCounters_Should_NotChange_WhenDebugIsDisabled()
	{
		ICoreServerAPI api = World.Api;
		ITestPlayer player = await World.JoinPlayer("clump-nodebug");
		await World.Ticks(2);

		ModSystem system = RequireSystem(api);
		const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
		FieldInfo debugField = system.GetType().GetField("debug", flags)!;
		string[] counterNames =
		{
			"dbgEnqueued", "dbgProcessed", "dbgInvalid", "dbgFrozen", "dbgRetried", "dbgGaveUp",
			"dbgSearchLt2", "dbgGroupLt2", "dbgMerged", "dbgKilled", "dbgQueueMax", "dbgHovering", "dbgSamples"
		};
		FieldInfo[] counters = new FieldInfo[counterNames.Length];
		int[] previousValues = new int[counterNames.Length];
		bool previousDebug = (bool)debugField.GetValue(system)!;

		for (int i = 0; i < counterNames.Length; i++)
		{
			counters[i] = system.GetType().GetField(counterNames[i], flags)!;
			previousValues[i] = (int)counters[i].GetValue(system)!;
		}

		try
		{
			debugField.SetValue(system, false);
			foreach (FieldInfo counter in counters) counter.SetValue(system, 0);

			EntityItem[] pair = Pair(api, player, "game:stone-obsidian", 64);
			await World.Until(() => !pair[1].Alive, timeoutTicks: 300);
			Assert.True(pair[0].Alive && pair[0].Slot.Itemstack.StackSize == 6,
				"setup failed: the non-debug item pair did not merge");

			foreach (FieldInfo counter in counters)
			{
				Assert.Equal(0, counter.GetValue(system));
			}
		}
		finally
		{
			debugField.SetValue(system, previousDebug);
			for (int i = 0; i < counters.Length; i++) counters[i].SetValue(system, previousValues[i]);
		}
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

		object normalized = normalize!.Invoke(null, new object?[] { new List<string>(entries), null })!;
		patterns!.SetValue(system, normalized);
		cache!.FieldType.GetMethod("Clear")!.Invoke(cache.GetValue(system), null);
	}
}
