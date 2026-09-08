using System;
using Vintagestory.API.Datastructures;

namespace Vintagestory.API.Common
{
    public static class StratumInventoryPrivacy
    {
        /// <summary>Checks inventory access, including range and claims.</summary>
        public static bool CanAccess(InventoryBase inventory, IPlayer player)
        {
            if (inventory == null || player?.Entity == null) return false;
            if (!inventory.CanPlayerAccess(player, player.Entity.Pos)) return false;
            if (inventory.Pos == null) return true;

            return inventory.Pos.dimension == player.Entity.Pos.Dimension
                && player.IsInInteractionRangeOf(inventory.Pos)
                && inventory.Api.World.Claims.TestAccess(player, inventory.Pos, EnumBlockAccessFlags.Use) == EnumWorldAccessResponse.Granted;
        }

        /// <summary>Requires an open inventory and current access.</summary>
        public static bool CanView(InventoryBase inventory, IPlayer player)
        {
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
            ITreeAttribute result = null;
            foreach (var entry in attributes)
            {
                if (entry.Key == "backpack" && entry.Value is ITreeAttribute)
                {
                    result ??= attributes.Clone();
                    result.RemoveAttribute(entry.Key);
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
                if (entry.Key is not ("slots" or "PlayerQuantities" or "Quantities")) filtered[entry.Key] = entry.Value;
            }
            filtered["slots"] = visible;
            tree["inventory"] = filtered;
        }
    }
}
