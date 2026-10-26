using Atlas.Api;
using Atlas.XUnit;
using System;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Proves that setting Performance.ItemClump.Enabled to false disables item clumping completely
/// and leaves separate resting item stacks untouched.
/// </summary>
[AtlasDataFiles("fixtures/stratum-itemclump-off", TargetPath = "")]
public class ItemClumpDisabledScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task RestingItems_Should_StayApart_WhenItemClumpIsDisabled()
	{
		ICoreServerAPI api = World.Api;
		ITestPlayer player = await World.JoinPlayer("clump-off");
		try
		{
			await World.Ticks(2);
			Vec3d pos = player.Player.Entity.Pos.XYZ.AddCopy(3.5, 0.2, 0.2);
			Item item = RequireItem(api, "game:stone-granite");

			EntityItem first = Spawn(api, new ItemStack(item, 10), pos);
			EntityItem second = Spawn(api, new ItemStack(item, 5), pos);

			// Well past the default clump delay (1500 ms) and settling time
			await World.Ticks(180);

			Assert.True(
				first.Alive && second.Alive
					&& first.Slot.Itemstack?.StackSize == 10 && second.Slot.Itemstack?.StackSize == 5,
				"resting items merged even though Performance.ItemClump.Enabled is false: "
				+ $"first alive={first.Alive} size={first.Slot.Itemstack?.StackSize}, "
				+ $"second alive={second.Alive} size={second.Slot.Itemstack?.StackSize}");
		}
		finally
		{
			player.Player.Disconnect();
		}
	}

	private static EntityItem Spawn(ICoreServerAPI api, ItemStack stack, Vec3d pos)
	{
		EntityItem? entity = (EntityItem?)api.World.SpawnItemEntity(stack, pos, new Vec3d());
		Assert.NotNull(entity);
		return entity!;
	}

	private static Item RequireItem(ICoreServerAPI api, string code)
	{
		Item? item = api.World.GetItem(new AssetLocation(code));
		if (item != null) return item;
		throw new InvalidOperationException($"item '{code}' not found.");
	}
}
