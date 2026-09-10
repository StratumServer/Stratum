using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

StratumInventoryPrivacy.InventoryGuardsEnabled = true;
Check(StratumInventoryPrivacy.GetPublicAttributes(null) == null, "null attributes should stay null");

var attributes = new TreeAttribute();
var backpack = new TreeAttribute();
backpack.SetString("name", "private");
attributes["backpack"] = backpack;
attributes["public"] = new StringAttribute("visible");
var filtered = StratumInventoryPrivacy.GetPublicAttributes(attributes);
Check(filtered.GetTreeAttribute("backpack") == null, "nested backpack contents should be removed");
Check(filtered.GetBool("stratumInventoryContentsHidden"), "filtered stacks need the hidden marker");
Check(filtered.GetString("public") == "visible", "unrelated attributes must survive");
Check(attributes.GetTreeAttribute("backpack") != null, "the source tree must not be mutated");

var nullAttributesStack = new ItemStack { Attributes = null };
Check(ReferenceEquals(StratumInventoryPrivacy.GetPublicStack(nullAttributesStack), nullAttributesStack), "null stack attributes should be safe");

var itemstackAttribute = new ItemstackAttribute(new ItemStack { Attributes = attributes });
var itemstackFiltered = (ItemstackAttribute)StratumInventoryPrivacy.GetPublicAttribute(itemstackAttribute);
Check(itemstackFiltered.value.Attributes.GetTreeAttribute("backpack") == null, "itemstack attributes should be filtered");
Check(itemstackAttribute.value.Attributes.GetTreeAttribute("backpack") != null, "itemstack source must not be mutated");

var array = new TreeArrayAttribute(new[] { attributes, new TreeAttribute() });
var arrayFiltered = (TreeArrayAttribute)StratumInventoryPrivacy.GetPublicAttribute(array);
Check(arrayFiltered.value.Length == 2, "tree arrays must preserve their length");
Check(arrayFiltered.value[0].GetTreeAttribute("backpack") == null, "tree array children should be filtered");
Check(array.value[0].GetTreeAttribute("backpack") != null, "tree array source must not be mutated");

var displayTree = new TreeAttribute();
var inventoryTree = new TreeAttribute();
var slots = new TreeAttribute();
slots["0"] = new StringAttribute("visible");
slots["1"] = new StringAttribute("private");
inventoryTree["slots"] = slots;
inventoryTree["PlayerQuantities"] = new IntArrayAttribute(new[] { 1, 2 });
inventoryTree["Quantities"] = new IntArrayAttribute(new[] { 1, 2 });
displayTree["inventory"] = inventoryTree;
StratumInventoryPrivacy.KeepDisplaySlots(displayTree, new[] { 0 });
var displayInventory = displayTree.GetTreeAttribute("inventory");
Check(displayInventory.GetTreeAttribute("slots").GetString("0") == "visible", "selected display slots should survive");
Check(displayInventory.GetTreeAttribute("slots").GetString("1") == null, "unselected display slots should be hidden");
Check(!displayInventory.HasAttribute("PlayerQuantities"), "per-player quantities must not leak");
Check(displayInventory.HasAttribute("Quantities"), "rendering quantities must survive");

StratumInventoryPrivacy.InventoryGuardsEnabled = false;
Check(ReferenceEquals(StratumInventoryPrivacy.GetPublicAttributes(attributes), attributes), "disabled filtering must preserve attributes");
Check(ReferenceEquals(StratumInventoryPrivacy.GetPublicAttribute(itemstackAttribute), itemstackAttribute), "disabled filtering must preserve attribute wrappers");
StratumInventoryPrivacy.StripHiddenContentsMarker(nullAttributesStack);
StratumInventoryPrivacy.InventoryGuardsEnabled = true;

Console.WriteLine("PASS: inventory privacy filtering, rollback data, display slots, and disabled behavior.");
return 0;
