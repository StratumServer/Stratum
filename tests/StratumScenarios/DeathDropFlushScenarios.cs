using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// A quiet death has to send the emptied hotbar and bag slots without waiting
/// for some other inventory change.
/// </summary>
public class DeathDropFlushScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task DeathDrop_Should_ClearTheDirtySlots()
	{
		ITestPlayer player = await World.JoinPlayer("death-flush");
		await player.GiveItem("game:stone-granite", 1);
		ItemSlot hotbar = player.Player.InventoryManager.ActiveHotbarSlot;
		Assert.NotNull(hotbar.Itemstack);
		InventoryBase inventory = (InventoryBase)hotbar.Inventory;
		for (int i = 0; i < 20 && inventory.IsDirty; i++)
		{
			await World.Ticks(1);
		}
		Assert.False(inventory.IsDirty);

		player.Entity.Die(EnumDespawnReason.Death, new DamageSource { Source = EnumDamageSource.Suicide, Type = EnumDamageType.Gravity });
		await World.Ticks(10);

		Assert.False(inventory.IsDirty, "emptied slots were still dirty: " + string.Join(",", inventory.DirtySlots));
	}
}
