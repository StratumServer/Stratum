using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

StratumInventoryPrivacy.InventoryGuardsEnabled = true;
if (StratumInventoryPrivacy.GetPublicAttributes(null) != null)
{
    return 1;
}

var attributes = new TreeAttribute();
attributes["backpack"] = new TreeAttribute();
var filtered = StratumInventoryPrivacy.GetPublicAttributes(attributes);
if (filtered.GetTreeAttribute("backpack") != null || !filtered.GetBool("stratumInventoryContentsHidden"))
{
    return 2;
}

Console.WriteLine("PASS: null attributes and nested backpack contents are filtered safely.");
return 0;
