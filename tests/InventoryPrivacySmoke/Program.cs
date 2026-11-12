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
Check(!displayInventory.HasAttribute("Quantities"), "loot quantities must not leak");

// CanAccess/CanView regression coverage for the mount-bag bug: CanAccess ran before
// OpenInventory ever registered the wrapper inventory in the player's InventoryManager, so
// requiring that registration in the entity-range branch meant a mount bag could never be
// opened in the first place. See StratumInventoryPrivacy.CanAccess and
// docs/commands/inventory-privacy.md for the fixed contract this pins.
{
	var mount = new EntityPlayer();
	var rider = new EntityPlayer();
	var otherDimension = new EntityPlayer();
	otherDimension.Pos.Dimension = 1;

	var bagInventory = new InventoryGeneric(2, "mountedbaginv", "test", null) { StratumRangeEntity = mount };
	var testInventoryManager = new StratumTestInventoryManager();
	var testPlayer = new StratumTestPlayer("rider-uid", rider, testInventoryManager);
	testPlayer.StratumRangeEntity = mount;

	// Row 1: in range, not yet registered. This is the open path, and the regression: the
	// server must be able to grant access before OpenInventory puts the wrapper in
	// InventoryManager.Inventories, or the bag can never be opened at all.
	testInventoryManager.StratumRegisteredInventory = null!;
	testPlayer.StratumInRange = true;
	Check(StratumInventoryPrivacy.CanAccess(bagInventory, testPlayer), "an unregistered mount bag in range must be accessible so it can be opened");
	Check(!StratumInventoryPrivacy.CanView(bagInventory, testPlayer), "CanView still requires HasOpened even when CanAccess is true");
	bagInventory.openedByPlayerGUIds.Add(testPlayer.PlayerUID);
	Check(!StratumInventoryPrivacy.CanView(bagInventory, testPlayer), "an opened mount bag must still be registered before it can be viewed");
	bagInventory.openedByPlayerGUIds.Remove(testPlayer.PlayerUID);

	// Row 2: in range, registered, and opened. The ordinary post-open state.
	testInventoryManager.StratumRegisteredInventory = bagInventory;
	bagInventory.openedByPlayerGUIds.Add(testPlayer.PlayerUID);
	Check(StratumInventoryPrivacy.CanAccess(bagInventory, testPlayer), "a registered, opened mount bag in range must stay accessible");
	Check(StratumInventoryPrivacy.CanView(bagInventory, testPlayer), "a registered, opened mount bag in range must be viewable");

	// Row 3: registered and opened, but the mount walked out of range.
	testPlayer.StratumInRange = false;
	Check(!StratumInventoryPrivacy.CanAccess(bagInventory, testPlayer), "an out-of-range mount bag must not be accessible even if still registered");
	Check(!StratumInventoryPrivacy.CanView(bagInventory, testPlayer), "an out-of-range mount bag must not be viewable");
	testPlayer.StratumInRange = true;

	// A different entity in the same dimension must not pass because the test double ignores
	// the entity argument. This keeps the range check tied to the live inventory owner.
	var unrelatedMount = new EntityPlayer();
	var wrongEntityInventory = new InventoryGeneric(2, "mountedbaginv", "wrong-entity", null) { StratumRangeEntity = unrelatedMount };
	Check(!StratumInventoryPrivacy.CanAccess(wrongEntityInventory, testPlayer), "range checks must use the inventory's live entity");

	// Row 4: registered and opened, but the mount is in a different dimension.
	var crossDimensionInventory = new InventoryGeneric(2, "mountedbaginv", "otherdim", null) { StratumRangeEntity = otherDimension };
	testInventoryManager.StratumRegisteredInventory = crossDimensionInventory;
	crossDimensionInventory.openedByPlayerGUIds.Add(testPlayer.PlayerUID);
	Check(!StratumInventoryPrivacy.CanAccess(crossDimensionInventory, testPlayer), "a mount bag in a different dimension must not be accessible");
	Check(!StratumInventoryPrivacy.CanView(crossDimensionInventory, testPlayer), "a mount bag in a different dimension must not be viewable");

	// Row 5: the unaffected Pos == null branch (a player's own inventory, checked by
	// ownership) still requires registration. This pins that the entity-branch fix did not
	// loosen the ownership branch it sits next to.
	var ownInventory = new InventoryGeneric(2, "characterinv", "test", null);
	testInventoryManager.StratumRegisteredInventory = null!;
	Check(!StratumInventoryPrivacy.CanAccess(ownInventory, testPlayer), "an unregistered own inventory must still require registration");
}

StratumInventoryPrivacy.InventoryGuardsEnabled = false;
Check(ReferenceEquals(StratumInventoryPrivacy.GetPublicAttributes(attributes), attributes), "disabled filtering must preserve attributes");
Check(ReferenceEquals(StratumInventoryPrivacy.GetPublicAttribute(itemstackAttribute), itemstackAttribute), "disabled filtering must preserve attribute wrappers");
StratumInventoryPrivacy.StripHiddenContentsMarker(nullAttributesStack);
StratumInventoryPrivacy.InventoryGuardsEnabled = true;

Console.WriteLine("PASS: inventory privacy filtering, rollback data, display slots, CanAccess/CanView, and disabled behavior.");
return 0;
