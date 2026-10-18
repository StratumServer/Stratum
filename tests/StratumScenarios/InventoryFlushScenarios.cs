using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Slot packets go out even when the player-data broadcast waits. The broadcast
/// rotates, so clients at the front of the list cannot starve the rest.
/// </summary>
public class InventoryFlushScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task CharacterSlots_Should_Clear_While_TheBroadcastRotates()
	{
		ITestPlayer first = await World.JoinPlayer("inv-flush-a");
		ITestPlayer second = await World.JoinPlayer("inv-flush-b");
		ITestPlayer third = await World.JoinPlayer("inv-flush-c");
		object flush = FlushConfig();
		PropertyInfo cap = CapProperty(flush);
		int previous = (int)cap.GetValue(flush)!;
		cap.SetValue(flush, 1);
		try
		{
			InventoryBase[] characters = new[]
			{
				CharacterInventory(first),
				CharacterInventory(second),
				CharacterInventory(third)
			};
			for (int i = 0; i < 20 && (characters.Any(inventory => inventory.IsDirty) || Broadcasts(World.Api.World).Length > 0); i++)
			{
				FlushDirtySlots(World.Api.World);
			}
			Assert.All(characters, inventory => Assert.False(inventory.IsDirty));

			foreach (InventoryBase inventory in characters)
			{
				inventory.MarkSlotDirty(0);
			}
			FlushDirtySlots(World.Api.World);
			Assert.All(characters, inventory => Assert.False(inventory.IsDirty));
			string[] served = Broadcasts(World.Api.World);
			Assert.Single(served);

			var seen = new HashSet<string> { served[0] };
			for (int i = 0; i < 2; i++)
			{
				FlushDirtySlots(World.Api.World);
				string[] next = Broadcasts(World.Api.World);
				Assert.Single(next);
				Assert.DoesNotContain(next[0], seen);
				seen.Add(next[0]);
			}
			Assert.Equal(3, seen.Count);
		}
		finally
		{
			cap.SetValue(flush, previous);
		}
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task LaterPlayers_Should_BeBroadcast_When_TheHeadStaysDirty()
	{
		ITestPlayer[] players = new ITestPlayer[7];
		for (int i = 0; i < players.Length; i++)
		{
			players[i] = await World.JoinPlayer("inv-fair-" + i);
		}
		object flush = FlushConfig();
		PropertyInfo cap = CapProperty(flush);
		int previous = (int)cap.GetValue(flush)!;
		cap.SetValue(flush, 4);
		try
		{
			InventoryBase[] characters = players.Select(CharacterInventory).ToArray();
			for (int i = 0; i < 20 && characters.Any(inventory => inventory.IsDirty); i++)
			{
				FlushDirtySlots(World.Api.World);
			}
			foreach (InventoryBase inventory in characters)
			{
				inventory.MarkSlotDirty(0);
			}
			FlushDirtySlots(World.Api.World);
			string[] first = Broadcasts(World.Api.World);
			Assert.Equal(4, first.Length);
			foreach (string uid in first)
			{
				CharacterInventory(PlayerByUid(players, uid)).MarkSlotDirty(0);
			}
			FlushDirtySlots(World.Api.World);
			string[] second = Broadcasts(World.Api.World);
			string[] waiting = players.Select(player => player.Player.PlayerUID).Where(uid => !first.Contains(uid)).ToArray();
			Assert.Equal(3, waiting.Length);
			Assert.All(waiting, uid => Assert.Contains(uid, second));
		}
		finally
		{
			cap.SetValue(flush, previous);
		}
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task ViewerSlotPackets_Should_Send_When_TheBroadcastIsDeferred()
	{
		ITestPlayer owner = await World.JoinPlayer("inv-view-a");
		ITestPlayer viewer = await World.JoinPlayer("inv-view-b");
		object flush = FlushConfig();
		PropertyInfo cap = CapProperty(flush);
		int previous = (int)cap.GetValue(flush)!;
		bool previousGuards = InventoryGuards();
		cap.SetValue(flush, 1);
		try
		{
			InventoryBase character = CharacterInventory(owner);
			for (int i = 0; i < 20 && (character.IsDirty || Broadcasts(World.Api.World).Length > 0); i++)
			{
				FlushDirtySlots(World.Api.World);
			}
			SetInventoryGuards(false);
			object manager = viewer.Player.InventoryManager;
			FieldInfo inventories = null;
			for (Type type = manager.GetType(); type != null && inventories == null; type = type.BaseType)
			{
				inventories = type.GetField("Inventories", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			}
			dynamic registered = inventories!.GetValue(manager)!;
			registered[character.InventoryID] = character;
			character.MarkSlotDirty(0);
			FlushDirtySlots(World.Api.World);
			Assert.False(character.IsDirty);
			string[] first = Broadcasts(World.Api.World);
			Assert.Single(first);
			FlushDirtySlots(World.Api.World);
			string[] second = Broadcasts(World.Api.World);
			Assert.Single(second);
			Assert.NotEqual(first[0], second[0]);
			Assert.Contains(owner.Player.PlayerUID, new[] { first[0], second[0] });
			Assert.Contains(viewer.Player.PlayerUID, new[] { first[0], second[0] });
		}
		finally
		{
			SetInventoryGuards(previousGuards);
			cap.SetValue(flush, previous);
		}
	}

	private static bool InventoryGuards()
	{
		Type privacy = Type.GetType("Vintagestory.API.Common.StratumInventoryPrivacy, VintagestoryAPI")!;
		return (bool)privacy.GetProperty("InventoryGuardsEnabled")!.GetValue(null)!;
	}

	private static void SetInventoryGuards(bool enabled)
	{
		Type runtime = Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib")!;
		object config = runtime.GetProperty("Config")!.GetValue(null)!;
		object hardening = config.GetType().GetProperty("Hardening")!.GetValue(config)!;
		hardening.GetType().GetProperty("InventoryGuards")!.SetValue(hardening, enabled);
		Type privacy = Type.GetType("Vintagestory.API.Common.StratumInventoryPrivacy, VintagestoryAPI")!;
		privacy.GetProperty("InventoryGuardsEnabled")!.SetValue(null, enabled);
	}

	private static ITestPlayer PlayerByUid(ITestPlayer[] players, string uid)
	{
		foreach (ITestPlayer player in players)
		{
			if (player.Player.PlayerUID == uid)
			{
				return player;
			}
		}
		throw new InvalidOperationException("no joined player has uid " + uid);
	}

	private static InventoryBase CharacterInventory(ITestPlayer player)
	{
		foreach (object inventory in player.Player.InventoryManager.Inventories.Values)
		{
			if (inventory is InventoryBase inventoryBase && inventoryBase.GetType().Name == "InventoryCharacter")
			{
				return inventoryBase;
			}
		}
		throw new InvalidOperationException("the joined player has no character inventory");
	}

	private static PropertyInfo CapProperty(object flush)
	{
		return flush.GetType().GetProperty("MaxPlayerDataBroadcastsPerPass")
			?? throw new InvalidOperationException("MaxPlayerDataBroadcastsPerPass was not on the flush config");
	}

	private static object FlushConfig()
	{
		Type runtime = Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib")
			?? throw new InvalidOperationException("StratumRuntime was not in the loaded lib");
		object config = runtime.GetProperty("Config")!.GetValue(null)!;
		object performance = config.GetType().GetProperty("Performance")!.GetValue(config)!;
		return performance.GetType().GetProperty("InventoryFlush")!.GetValue(performance)!;
	}

	private static object InventorySystem(object server)
	{
		FieldInfo systemsField = server.GetType().GetField("Systems", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("Systems was not on the server");
		Array systems = (Array)systemsField.GetValue(server)!;
		return systems.Cast<object>().First(system => system.GetType().Name == "ServerSystemInventory");
	}

	private static void FlushDirtySlots(object server)
	{
		object inventory = InventorySystem(server);
		MethodInfo flush = inventory.GetType().GetMethod("SendDirtySlots", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("SendDirtySlots was not on the inventory system");
		flush.Invoke(inventory, new object[] { 0.03f });
	}

	private static string[] Broadcasts(object server)
	{
		object inventory = InventorySystem(server);
		var list = (System.Collections.IList)inventory.GetType()
			.GetField("stratumPlayerDataBroadcastsThisPass", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(inventory)!;
		string[] copy = new string[list.Count];
		for (int i = 0; i < list.Count; i++)
		{
			copy[i] = (string)list[i]!;
		}
		return copy;
	}
}
