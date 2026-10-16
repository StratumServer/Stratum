using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// A dirty character inventory also broadcasts player data to every connected client.
/// One flush may do that for a limited number of players. The rest stay dirty, and the
/// next call of the same flush publishes them.
/// </summary>
public class InventoryFlushScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task CharacterBroadcast_Should_LeaveTheRestDirty_When_ThePassIsCapped()
	{
		ITestPlayer first = await World.JoinPlayer("inv-flush-a");
		ITestPlayer second = await World.JoinPlayer("inv-flush-b");
		ITestPlayer third = await World.JoinPlayer("inv-flush-c");
		object flush = FlushConfig();
		PropertyInfo cap = flush.GetType().GetProperty("MaxPlayerDataBroadcastsPerPass")
			?? throw new InvalidOperationException("MaxPlayerDataBroadcastsPerPass was not on the flush config");
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
			for (int i = 0; i < 20 && characters.Any(inventory => inventory.IsDirty); i++)
			{
				FlushDirtySlots(World.Api.World);
			}
			Assert.All(characters, inventory => Assert.False(inventory.IsDirty));
			foreach (InventoryBase inventory in characters)
			{
				inventory.MarkSlotDirty(0);
			}
			FlushDirtySlots(World.Api.World);
			Assert.Equal(2, characters.Count(inventory => inventory.IsDirty));
			FlushDirtySlots(World.Api.World);
			Assert.Equal(1, characters.Count(inventory => inventory.IsDirty));
			FlushDirtySlots(World.Api.World);
			Assert.Equal(0, characters.Count(inventory => inventory.IsDirty));
		}
		finally
		{
			cap.SetValue(flush, previous);
		}
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

	private static object FlushConfig()
	{
		Type runtime = Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib")
			?? throw new InvalidOperationException("StratumRuntime was not in the loaded lib");
		object config = runtime.GetProperty("Config")!.GetValue(null)!;
		object performance = config.GetType().GetProperty("Performance")!.GetValue(config)!;
		return performance.GetType().GetProperty("InventoryFlush")!.GetValue(performance)!;
	}

	private static void FlushDirtySlots(object server)
	{
		FieldInfo systemsField = server.GetType().GetField("Systems", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("Systems was not on the server");
		Array systems = (Array)systemsField.GetValue(server)!;
		object inventory = systems.Cast<object>().First(system => system.GetType().Name == "ServerSystemInventory");
		MethodInfo flush = inventory.GetType().GetMethod("SendDirtySlots", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("SendDirtySlots was not on the inventory system");
		flush.Invoke(inventory, new object[] { 0.03f });
	}
}
