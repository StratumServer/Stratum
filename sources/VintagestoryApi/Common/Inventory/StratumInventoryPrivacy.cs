using System;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace Vintagestory.API.Common;

public static class StratumInventoryPrivacy
{
	private const string HiddenContentsAttribute = "stratumInventoryContentsHidden";

	public static bool InventoryGuardsEnabled { get; set; }

	/// <summary>Checks inventory access, including range and claims.</summary>
	public static bool CanAccess(InventoryBase inventory, IPlayer player)
	{
		if (!InventoryGuardsEnabled) return true;
		if (inventory == null || player?.Entity == null) return false;
		if (!inventory.CanPlayerAccess(player, player.Entity.Pos)) return false;
		// An inventory carried by an entity takes its range from the live entity, the
		// way vanilla checks entity interaction, with no land claim test.
		// Stratum: registration is checked by CanView. CanAccess runs before
		// OpenInventory on the open path (CollectibleBehaviorHeldBag.OnInteract), so it
		// cannot also require the inventory to already be registered, or an unopened bag
		// can never be opened.
		if (inventory.StratumRangeEntity is Entity rangeEntity)
		{
			return rangeEntity.Pos.Dimension == player.Entity.Pos.Dimension
				&& player.IsInInteractionRangeOf(rangeEntity);
		}
		if (inventory.Pos == null)
		{
			return ReferenceEquals(player.InventoryManager.GetInventory(inventory.InventoryID), inventory);
		}

		return inventory.Pos.dimension == player.Entity.Pos.Dimension
			&& player.IsInInteractionRangeOf(inventory.Pos)
			&& inventory.Api.World.Claims.TestAccess(player, inventory.Pos, EnumBlockAccessFlags.Use) == EnumWorldAccessResponse.Granted;
	}

	/// <summary>Requires an open inventory and current access.</summary>
	public static bool CanView(InventoryBase inventory, IPlayer player)
	{
		if (!InventoryGuardsEnabled) return true;
		return inventory != null && player != null
			&& inventory.HasOpened(player)
			&& ReferenceEquals(player.InventoryManager.GetInventory(inventory.InventoryID), inventory)
			&& CanAccess(inventory, player);
	}

	/// <summary>Keeps the visible item without its bag contents.</summary>
	public static ItemStack GetPublicStack(ItemStack stack)
	{
		if (stack == null) return null;
		ITreeAttribute attributes = GetPublicAttributes(stack.Attributes);
		if (ReferenceEquals(attributes, stack.Attributes)) return stack;
		ItemStack result = stack.Clone();
		result.Attributes = attributes;
		return result;
	}

	/// <summary>Strips bag contents without changing the original attributes.</summary>
	public static ITreeAttribute GetPublicAttributes(ITreeAttribute attributes)
	{
		if (!InventoryGuardsEnabled || attributes == null) return attributes;
		ITreeAttribute result = null;
		foreach (var entry in attributes)
		{
			if (entry.Key == "backpack" && entry.Value is ITreeAttribute)
			{
				result ??= attributes.Clone();
				result.RemoveAttribute(entry.Key);
				result.SetBool(HiddenContentsAttribute, true);
				continue;
			}
			IAttribute replacement = GetPublicAttribute(entry.Value);
			if (!ReferenceEquals(replacement, entry.Value))
			{
				result ??= attributes.Clone();
				result[entry.Key] = replacement;
			}
		}
		return result ?? attributes;
	}

	/// <summary>Strips bag contents from trees, stacks, and tree arrays.</summary>
	public static IAttribute GetPublicAttribute(IAttribute attribute)
	{
		if (attribute is ITreeAttribute tree) return GetPublicAttributes(tree);
		if (attribute is ItemstackAttribute stack)
		{
			ItemStack publicStack = GetPublicStack(stack.value);
			if (!ReferenceEquals(publicStack, stack.value)) return new ItemstackAttribute(publicStack);
		}
		if (attribute is TreeArrayAttribute array)
		{
			TreeAttribute[] filtered = null;
			for (int i = 0; i < array.value.Length; i++)
			{
				ITreeAttribute child = GetPublicAttributes(array.value[i]);
				if (ReferenceEquals(child, array.value[i])) continue;
				filtered ??= (TreeAttribute[])array.value.Clone();
				filtered[i] = (TreeAttribute)child;
			}
			if (filtered != null) return new TreeArrayAttribute(filtered);
		}
		return attribute;
	}

	/// <summary>Keeps the slot count and the slots needed for rendering.</summary>
	public static void KeepDisplaySlots(ITreeAttribute tree, ReadOnlySpan<int> slotIds)
	{
		if (!InventoryGuardsEnabled || tree == null) return;
		ITreeAttribute inventory = tree.GetTreeAttribute("inventory");
		if (inventory == null) return;

		var visible = new TreeAttribute();
		ITreeAttribute slots = inventory.GetTreeAttribute("slots");
		foreach (int slotId in slotIds)
		{
			string key = slotId.ToString();
			if (slots?[key] != null) visible[key] = slots[key];
		}

		var filtered = new TreeAttribute();
		foreach (var entry in inventory)
		{
			// Quantities and PlayerQuantities are both written only by InventoryPerPlayer
			// (story loot chests): per-slot loot counts and the per-player looter UID map
			// for slots this viewer has not opened. Drop both.
			if (entry.Key is not ("slots" or "Quantities" or "PlayerQuantities")) filtered[entry.Key] = entry.Value;
		}
		filtered["slots"] = visible;
		tree["inventory"] = filtered;
	}

	public static bool IsPublicStackContentsHidden(ItemStack stack)
	{
		return stack?.Attributes?.GetBool(HiddenContentsAttribute) == true;
	}

	/// <summary>Removes the hidden-contents marker so a client cannot pin a bag stack non-empty.</summary>
	public static void StripHiddenContentsMarker(ItemStack stack)
	{
		stack?.Attributes?.RemoveAttribute(HiddenContentsAttribute);
	}
}
