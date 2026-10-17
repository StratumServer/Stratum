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
/// Performance.ItemClump (enabled by default) must not skip the pickup window of
/// EntityItem.CanCollect: a stack a player just dropped (ByPlayerUid set, under 1000 ms old)
/// stays its own entity until the window has passed, and only then may be poured into an older
/// pile. Items without a dropper have no window and merge as usual.
///
/// Timing is expressed in ticks (Atlas paces at ~33 ms), with wide margins: 8 ticks is far below
/// the 1000 ms window, and the merge itself is awaited with Until instead of a fixed count, since
/// the clump delay is a config value this scenario does not hard-code.
/// </summary>
public class ItemClumpPickupWindowScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task FreshPlayerDrop_Should_StayApart_UntilPickupWindowPasses_ThenMerge()
	{
		ICoreServerAPI api = World.Api;
		ITestPlayer player = await World.JoinPlayer("clump-window");
		await World.Ticks(2);
		Vec3d pos = DropPos(player);
		Item item = RequireItem(api, "game:flint", "game:stone-granite");

		// Older pile from player A. Wait until its own window is long over and it has settled.
		EntityItem older = Spawn(api, new ItemStack(item, 5), pos, "clump-window-a");
		await World.Ticks(90);
		Assert.True(older.Alive && older.Slot.Itemstack.StackSize == 5,
			"setup failed: the older pile is gone or changed before the test started");

		// Fresh drop from player B right next to it.
		EntityItem fresh = Spawn(api, new ItemStack(item, 1), pos, "clump-window-b");
		await World.Ticks(8);

		Assert.True(
			older.Alive && fresh.Alive
				&& older.Slot.Itemstack.StackSize == 5 && fresh.Slot.Itemstack.StackSize == 1,
			"fresh drop was merged inside its pickup window: "
			+ $"older alive={older.Alive} size={older.Slot.Itemstack?.StackSize}, "
			+ $"fresh alive={fresh.Alive} size={fresh.Slot.Itemstack?.StackSize}");

		// After the window the merge is allowed again and must happen.
		await World.Until(() => !fresh.Alive, timeoutTicks: 300);
		Assert.True(!fresh.Alive && older.Alive && older.Slot.Itemstack.StackSize == 6,
			"fresh drop did not merge into the older pile after the pickup window: "
			+ $"older alive={older.Alive} size={older.Slot.Itemstack?.StackSize}, fresh alive={fresh.Alive}");
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task ItemsWithoutDropper_Should_Merge_WithoutWaitingForAWindow()
	{
		ICoreServerAPI api = World.Api;
		ITestPlayer player = await World.JoinPlayer("clump-nodropper");
		await World.Ticks(2);
		Vec3d pos = DropPos(player).AddCopy(0, 0, 6);
		Item item = RequireItem(api, "game:flint", "game:stone-granite");

		EntityItem older = Spawn(api, new ItemStack(item, 5), pos, null);
		await World.Ticks(90);
		EntityItem fresh = Spawn(api, new ItemStack(item, 1), pos, null);

		await World.Until(() => !fresh.Alive, timeoutTicks: 300);
		Assert.True(!fresh.Alive && older.Alive && older.Slot.Itemstack.StackSize == 6,
			"drops without a dropper did not merge: "
			+ $"older alive={older.Alive} size={older.Slot.Itemstack?.StackSize}, fresh alive={fresh.Alive}");
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task Merge_Should_Extend_DeathTime_OfTheReceiver_IfDonorHasLongerLife()
	{
		ICoreServerAPI api = World.Api;
		ITestPlayer player = await World.JoinPlayer("clump-deathtime");
		await World.Ticks(2);
		Vec3d pos = DropPos(player).AddCopy(0, 0, 12);
		Item item = RequireItem(api, "game:flint", "game:stone-granite");

		// Old stack with a small remaining lifetime.
		EntityItem older = Spawn(api, new ItemStack(item, 5), pos, null);
		older.Attributes.SetFloat("deathTime", 100f);
		await World.Ticks(90); // Wait for it to stop moving

		// Fresh drop with a long lifetime.
		EntityItem fresh = Spawn(api, new ItemStack(item, 1), pos, null);
		fresh.Attributes.SetFloat("deathTime", 500f);

		await World.Until(() => !fresh.Alive, timeoutTicks: 300);

		Assert.True(older.Alive, "older item despawned unexpectedly");
		
		float actualDeathTime = older.Attributes.GetFloat("deathTime", -1);
		Assert.True(
			actualDeathTime >= 500f,
			$"deathTime was not extended after merge. Expected >= 500, but got {actualDeathTime}");
	}

	private static EntityItem Spawn(ICoreServerAPI api, ItemStack stack, Vec3d pos, string? dropper)
	{
		EntityItem entity = (EntityItem)api.World.SpawnItemEntity(stack, pos, new Vec3d());
		if (dropper != null) entity.ByPlayerUid = dropper;
		return entity;
	}

	private static Vec3d DropPos(ITestPlayer player)
	{
		return player.Player.Entity.Pos.XYZ.AddCopy(1.2, 0.2, 0.2);
	}

	private static Item RequireItem(ICoreServerAPI api, params string[] codes)
	{
		foreach (string code in codes)
		{
			Item item = api.World.GetItem(new AssetLocation(code));
			if (item != null) return item;
		}
		throw new InvalidOperationException("none of the item codes resolved: " + string.Join(", ", codes));
	}
}
