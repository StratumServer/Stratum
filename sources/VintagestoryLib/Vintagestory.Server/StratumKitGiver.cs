using System;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

/// <summary>
/// Turns a StratumKitDefinition's stored bytes back into real ItemStacks and places them. Armor
/// goes to the character inventory explicitly, matched by slot compatibility (ItemSlotCharacter's
/// own CanHold already enforces EnumCharacterDressType, so this does not duplicate that check
/// itself); everything else goes through Entity.TryGiveItemStack, the same entry point Stratum's
/// own /giveitem command already uses in production (StratumTargetSelectors.cs). A worn backpack's
/// own contents travel inside its ItemStack's own attribute tree already (CollectibleBehaviorHeldBag
/// stores them under a "backpack" tree on the bag's stack), so ToBytes/FromBytes round-trips a
/// filled backpack in one piece; giving it back does not need a separate "fill it after" step.
/// </summary>
internal static class StratumKitGiver
{
	public sealed class KitGiveResult
	{
		public int GivenCount;
		public int DroppedCount;
		public int FailedCount;

		public bool AllGiven => DroppedCount == 0 && FailedCount == 0;
	}

	public static KitGiveResult Give(ServerPlayer player, StratumKitDefinition kit)
	{
		KitGiveResult result = new KitGiveResult();
		if (player?.Entity == null || kit?.Items == null)
		{
			return result;
		}

		foreach (StratumKitItem entry in kit.Items)
		{
			ItemStack stack = DecodeStack(player.Entity.World, entry);
			if (stack == null)
			{
				result.FailedCount++;
				continue;
			}

			if (TryGiveOne(player, stack))
			{
				result.GivenCount++;
			}
			else
			{
				result.DroppedCount++;
			}
		}

		return result;
	}

	public static ItemStack DecodeStack(IWorldAccessor world, StratumKitItem entry)
	{
		if (string.IsNullOrWhiteSpace(entry?.Base64))
		{
			return null;
		}

		try
		{
			byte[] data = Convert.FromBase64String(entry.Base64);
			ItemStack stack = new ItemStack(data);
			if (!stack.ResolveBlockOrItem(world))
			{
				return null;
			}

			if (entry.Quantity > 0)
			{
				stack.StackSize = entry.Quantity;
			}

			return stack;
		}
		catch (Exception exception)
		{
			StratumRuntime.LogWarning("failed to decode kit item '" + entry.Code + "': " + exception.Message);
			return null;
		}
	}

	private static bool TryGiveOne(ServerPlayer player, ItemStack stack)
	{
		if (TryPlaceArmor(player, stack))
		{
			return true;
		}

		// A clone, not the original: PlayerInventoryManager.TryGiveItemstack zeroes out whatever
		// stack it was handed once its internal transfer slot ends up empty, even when the
		// overall give reports failure (a partial absorption into an inventory that does not
		// count as a completed transfer). Passing the original here would risk dropping an
		// already-emptied stack below, silently discarding the item exactly like the loss this
		// fallback exists to prevent.
		ItemStack remainder = stack.Clone();
		if (player.Entity.TryGiveItemStack(remainder))
		{
			return true;
		}

		if (remainder.StackSize <= 0)
		{
			return true; // Absorbed somewhere despite the false return; nothing left to drop.
		}

		// Every slot the auto-placement logic tried was full or incompatible: drop what's left
		// at the player's feet rather than silently discarding it.
		Vec3d pos = player.Entity.Pos?.XYZ;
		if (pos != null)
		{
			player.Entity.World.SpawnItemEntity(remainder, pos);
		}

		return false;
	}

	private static bool TryPlaceArmor(ServerPlayer player, ItemStack stack)
	{
		IInventory character = player.InventoryManager?.GetOwnInventory(GlobalConstants.characterInvClassName);
		if (character == null)
		{
			return false;
		}

		ItemSlot source = new DummySlot(stack);
		foreach (ItemSlot slot in character)
		{
			if (slot.Itemstack != null || !slot.CanHold(source))
			{
				continue;
			}

			slot.Itemstack = stack;
			slot.MarkDirty();
			return true;
		}

		return false;
	}
}
