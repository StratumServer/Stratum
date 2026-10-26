using Atlas.Api;
using Atlas.XUnit;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Performance.ItemClump, which stacks may take part in one merge.
///
/// Full stacks: they can neither receive nor usefully donate, so a heap of them must not crowd the
/// mergeable stacks out of the group (MaxGroupSize) nor out of the search.
///
/// Reach: every member of a group lies within the radius of the root, but two members can lie up to twice
/// that apart. Only pairs within the radius of each other mix.
///
/// Land claims: stacks inside a claim and stacks outside it do not mix, so a pile dropped outside a wall
/// cannot pull in the stacks lying behind it.
///
/// Lava and Fire: burning or lava-submerged stacks must not merge with dry ground stacks.
///
/// Hoppers: stacks resting on a BlockEntityItemFlow must neither donate to nor receive from nearby stacks.
///
/// Each scenario uses its own spot (8 blocks apart, beyond the 5 block search radius), its own item and a
/// player name of at most 16 characters.
/// </summary>
public class ItemClumpRangeScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task FullStacks_Should_NotCrowdOutMergeableStacks()
	{
		ICoreServerAPI api = World.Api;
		ITestPlayer player = await World.JoinPlayer("clump-full");
		try
		{
			await World.Ticks(2);
			Vec3d pos = DropPos(player, 56);
			Item item = RequireItem(api, "game:stone-scoria", "game:stone-kimberlite");
			int max = item.MaxStackSize;
			Assert.True(max > 6, $"setup failed: {item.Code} stacks to only {max}");

			// More full stacks than MaxGroupSize (32) on one spot, then two small stacks beside them.
			List<EntityItem> full = new List<EntityItem>();
			for (int i = 0; i < 40; i++)
			{
				full.Add(Spawn(api, new ItemStack(item, max), pos, null));
			}
			EntityItem small = Spawn(api, new ItemStack(item, 5), pos, null);
			EntityItem tiny = Spawn(api, new ItemStack(item, 1), pos, null);

			await World.Until(() => !tiny.Alive || !small.Alive, timeoutTicks: 600);

			int alive = (small.Alive ? 1 : 0) + (tiny.Alive ? 1 : 0);
			Assert.True(alive == 1, $"the two small stacks did not merge: small alive={small.Alive}, tiny alive={tiny.Alive}");
			EntityItem pile = small.Alive ? small : tiny;
			Assert.True(pile.Slot.Itemstack?.StackSize == 6, $"expected a pile of 6, got {pile.Slot.Itemstack?.StackSize}");
			foreach (EntityItem f in full)
			{
				Assert.True(f.Alive && f.Slot.Itemstack?.StackSize == max, "a full stack was changed or removed");
			}
		}
		finally
		{
			player.Player.Disconnect();
		}
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task Merge_Should_NotJoinTwoStacks_ThatAreFartherApartThanTheRadius()
	{
		ICoreServerAPI api = World.Api;
		ITestPlayer player = await World.JoinPlayer("clump-reach");
		try
		{
			await World.Ticks(2);
			Vec3d center = DropPos(player, 64);
			Item item = RequireItem(api, "game:stone-conglomerate", "game:stone-bauxite");

			// 9 blocks apart (radius 5), the small stack in the middle reaches both.
			EntityItem left = Spawn(api, new ItemStack(item, 30), center.AddCopy(-4.5, 0, 0), null);
			EntityItem right = Spawn(api, new ItemStack(item, 20), center.AddCopy(4.5, 0, 0), null);
			await World.Ticks(90);
			Assert.True(left.Alive && right.Alive && left.Slot.Itemstack?.StackSize == 30 && right.Slot.Itemstack?.StackSize == 20,
				"setup failed: the two far stacks changed before the test started");

			EntityItem middle = Spawn(api, new ItemStack(item, 1), center, null);
			await World.Until(() => !middle.Alive, timeoutTicks: 300);
			await World.Ticks(60);

			Assert.True(left.Alive && right.Alive, "a far stack disappeared");
			int leftSize = left.Slot.Itemstack?.StackSize ?? 0;
			int rightSize = right.Slot.Itemstack?.StackSize ?? 0;
			Assert.True(
				leftSize >= 30 && leftSize <= 31 && rightSize >= 20 && rightSize <= 21 && leftSize + rightSize == 51,
				$"stacks 9 blocks apart were merged: left={leftSize}, right={rightSize}");
		}
		finally
		{
			player.Player.Disconnect();
		}
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task Merge_Should_NotCrossALandClaimBorder()
	{
		ICoreServerAPI api = World.Api;
		ITestPlayer player = await World.JoinPlayer("clump-claim");
		try
		{
			await World.Ticks(2);
			Vec3d pos = DropPos(player, 72);
			Item item = RequireItem(api, "game:stone-halite", "game:stone-phyllite");

			Vec3d insidePos = pos.AddCopy(3, 0, 0);
			int ix = (int)Math.Floor(insidePos.X);
			int iz = (int)Math.Floor(pos.Z);
			LandClaim claim = LandClaim.CreateClaim("clump-claim-owner");
			claim.Areas.Add(new Cuboidi(ix - 1, 0, iz - 2, ix + 1, 255, iz + 2));
			api.World.Claims.Add(claim);

			try
			{
				EntityItem inside = Spawn(api, new ItemStack(item, 3), insidePos, null);
				EntityItem outside = Spawn(api, new ItemStack(item, 30), pos, null);
				Assert.True(api.World.Claims.Get(inside.Pos.AsBlockPos)?.Length > 0, "setup failed: the inside stack is not inside the claim");
				Assert.True((api.World.Claims.Get(outside.Pos.AsBlockPos)?.Length ?? 0) == 0, "setup failed: the outside stack is inside the claim");

				await World.Ticks(240);

				Assert.True(
					inside.Alive && outside.Alive && inside.Slot.Itemstack?.StackSize == 3 && outside.Slot.Itemstack?.StackSize == 30,
					"stacks on both sides of a land claim border were merged: "
					+ $"inside alive={inside.Alive} size={inside.Slot.Itemstack?.StackSize}, outside alive={outside.Alive} size={outside.Slot.Itemstack?.StackSize}");
			}
			finally
			{
				api.World.Claims.Remove(claim);
			}
		}
		finally
		{
			player.Player.Disconnect();
		}
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task Merge_Should_NotPullDryStacksIntoLavaOrFire()
	{
		ICoreServerAPI api = World.Api;
		ITestPlayer player = await World.JoinPlayer("clump-lava");
		try
		{
			await World.Ticks(2);
			Vec3d pos = DropPos(player, 80);
			Item item = RequireItem(api, "game:stone-kimberlite", "game:stone-scoria");

			// Larger stack in lava / on fire (20 items)
			EntityItem burning = Spawn(api, new ItemStack(item, 20), pos, null);
			burning.InLava = true;
			burning.IsOnFire = true;

			// Smaller stack on dry ground nearby (10 items)
			EntityItem dry = Spawn(api, new ItemStack(item, 10), pos.AddCopy(2.5, 0, 0), null);

			await World.Ticks(240);

			Assert.True(dry.Alive && dry.Slot.Itemstack?.StackSize == 10,
				"dry stack merged into burning/lava stack: "
				+ $"dry alive={dry.Alive} size={dry.Slot.Itemstack?.StackSize}");
		}
		finally
		{
			player.Player.Disconnect();
		}
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task Merge_Should_NotPullStackRestingOnHopper()
	{
		ICoreServerAPI api = World.Api;
		ITestPlayer player = await World.JoinPlayer("clump-hopper");
		try
		{
			await World.Ticks(2);
			Vec3d pos = DropPos(player, 88);
			Item item = RequireItem(api, "game:bone");

			BlockPos hopperPos = new BlockPos((int)Math.Floor(pos.X), (int)Math.Floor(pos.Y), (int)Math.Floor(pos.Z), 0);
			World.SetBlock("game:hopper", hopperPos);
			await World.Ticks(5);

			// Stack on hopper (20 items)
			EntityItem hopperItem = Spawn(api, new ItemStack(item, 20), new Vec3d(hopperPos.X + 0.5, hopperPos.Y + 1.0, hopperPos.Z + 0.5), null);

			// Larger floor stack 2 blocks away (30 items)
			EntityItem floorItem = Spawn(api, new ItemStack(item, 30), new Vec3d(hopperPos.X + 2.5, hopperPos.Y, hopperPos.Z + 0.5), null);

			await World.Ticks(240);

			Assert.True(floorItem.Alive && floorItem.Slot.Itemstack?.StackSize == 30,
				"floor stack absorbed hopper items: "
				+ $"floor alive={floorItem.Alive} size={floorItem.Slot.Itemstack?.StackSize}");
		}
		finally
		{
			player.Player.Disconnect();
		}
	}

	private static EntityItem Spawn(ICoreServerAPI api, ItemStack stack, Vec3d pos, string? dropper)
	{
		EntityItem? entity = (EntityItem?)api.World.SpawnItemEntity(stack, pos, new Vec3d());
		Assert.NotNull(entity);
		if (dropper != null) entity!.ByPlayerUid = dropper;
		return entity!;
	}

	private static Vec3d DropPos(ITestPlayer player, int zOffset)
	{
		return player.Player.Entity.Pos.XYZ.AddCopy(3.5, 0.2, 0.2 + zOffset);
	}

	private static Item RequireItem(ICoreServerAPI api, params string[] codes)
	{
		foreach (string code in codes)
		{
			Item? item = api.World.GetItem(new AssetLocation(code));
			if (item != null) return item;
		}
		throw new InvalidOperationException("none of the item codes resolved: " + string.Join(", ", codes));
	}
}
