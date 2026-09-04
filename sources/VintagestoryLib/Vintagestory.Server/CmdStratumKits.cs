using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

/// <summary>
/// #210: kits editable live, in-game, via commands, with role assignment and configurable
/// activation. /kit redeems a kit the caller is entitled to; /kitedit is the staff-facing CRUD
/// surface. No UI is possible (the issue's own scope checkbox: server-side only, no custom
/// client), so every /kitedit sub-verb that would otherwise need an item code instead snapshots a
/// live ItemStack off the caller: create snapshots the caller's whole inventory, additem
/// snapshots their active hotbar slot.
/// </summary>
internal class CmdStratumKits
{
	private const string RedeemedThisLifeKey = "stratum.kit.redeemed-this-life.v1";

	// Single source of truth for the /kitedit action words: drives the WordRange parser, the
	// unknown-action message, and ActionShape below. Ordered the way an operator uses them.
	private static readonly string[] KitEditActions =
	{
		"list", "create", "additem", "removeitem", "preview", "delete", "rename", "setrole", "setcooldown", "setscope", "onrespawn", "oneperlife", "give"
	};

	private readonly ServerMain server;

	public CmdStratumKits(ServerMain server)
	{
		this.server = server;
		StratumRuntime.Config.EnsurePopulated();
		StratumKitStore.Load();
		server.EventManager.OnPlayerRespawn += OnPlayerRespawn;

		if (!StratumRuntime.Config.Commands.Enabled) return;

		CommandArgumentParsers parsers = server.api.commandapi.Parsers;

		StratumCommandAccessConfig kitAccess = StratumRuntime.Config.Commands.Kits;
		if (StratumCommandRegistration.ShouldRegister(kitAccess, "/kit", "Commands.Kits"))
		{
			server.api.commandapi.Create("kit")
				.WithDescription("Redeem a kit you have been assigned, or list the kits you can redeem")
				.WithAdditionalInformation("/kit lists the kits assigned to you. /kit &lt;name&gt; redeems one. A kit with no role assignment is available to every player with kit access.")
				.WithArgs(parsers.OptionalWord("name"))
				.RequiresPrivilege(Privilege.chat)
				.HandleWith(HandleKit);
		}

		StratumCommandAccessConfig kitEditAccess = StratumRuntime.Config.Commands.KitEdit;
		if (StratumCommandRegistration.ShouldRegister(kitEditAccess, "/kitedit", "Commands.KitEdit"))
		{
			server.api.commandapi.Create("kitedit")
				.WithDescription("Create and manage kits")
				.WithAdditionalInformation("Actions: list, create &lt;name&gt;, additem &lt;name&gt;, removeitem &lt;name&gt; &lt;index&gt;, preview &lt;name&gt;, delete &lt;name&gt;, rename &lt;name&gt; &lt;newName&gt;, setrole &lt;name&gt; &lt;role&gt;, setcooldown &lt;name&gt; &lt;seconds&gt;, setscope &lt;name&gt; all|hotbar, onrespawn &lt;name&gt; on|off, oneperlife &lt;name&gt; on|off, give &lt;name&gt; &lt;player&gt;. create snapshots your whole inventory (or only your hotbar and offhand, if scope is hotbar), additem snapshots your active hotbar slot.")
				.WithArgs(
					parsers.WordRange("action", KitEditActions),
					parsers.OptionalWord("name"),
					parsers.OptionalWord("value"))
				.RequiresPrivilege(Privilege.chat)
				.HandleWith(HandleKitEdit);
		}
	}

	// #210: "activate the kit ... through respawn, which is configurable". Also clears the
	// one-per-life redeemed set for every kit, the same life boundary the give-on-respawn pass
	// itself is keyed to.
	private void OnPlayerRespawn(IServerPlayer player)
	{
		if (player?.ServerData == null) return;

		if (player.ServerData.CustomPlayerData.Remove(RedeemedThisLifeKey))
		{
			server.PlayerDataManager.playerDataDirty = true;
		}

		if (!StratumRuntime.Config.Commands.Kits.Enabled) return;

		// This event fires before the player is actually revived: vanilla's own handler for it
		// (ServerSystemEntitySimulation.OnPlayerRespawn) only starts an async teleport, calling
		// Entity.Revive() later once the target chunk has finished loading. Giving kit items
		// synchronously here would race that revive and could be silently lost, so this polls
		// for Alive first instead, giving up after ~10s if it never arrives (e.g. the player
		// disconnected mid-respawn).
		string playerUid = player.PlayerUID;
		int attemptsRemaining = 40;
		long listenerId = 0;
		listenerId = server.RegisterGameTickListener(_ =>
		{
			ConnectedClient client = server.GetClientByUID(playerUid);
			bool alive = client?.Player?.Entity?.Alive == true;
			if (!alive && client != null && --attemptsRemaining > 0) return;

			server.UnregisterGameTickListener(listenerId);
			if (client?.Player == null || !alive) return;

			GiveAssignedRespawnKits(client.Player, client.ServerData);
		}, 250);
	}

	private void GiveAssignedRespawnKits(ServerPlayer target, ServerPlayerData data)
	{
		foreach (StratumKitDefinition kit in StratumKitStore.All())
		{
			if (kit.GiveOnRespawn
				&& IsAssignedTo(kit, data)
				&& StratumCommandAccessCatalog.PlayerHasAccess(target, StratumRuntime.Config.Commands.Kits))
			{
				StratumKitGiver.Give(target, kit);
				MarkRedeemedThisLife(data, kit.Name);
			}
		}
	}

	private TextCommandResult HandleKit(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, "kit", StratumRuntime.Config.Commands.Kits, out TextCommandResult failure))
		{
			return failure;
		}

		IServerPlayer caller = args.Caller.Player as IServerPlayer;
		if (caller?.ServerData == null)
		{
			return TextCommandResult.Error("Only a connected player can use /kit.");
		}

		if (args.Parsers[0].IsMissing)
		{
			return ListAvailableKits(caller);
		}

		string name = args[0] as string;
		StratumKitDefinition kit = StratumKitStore.Find(name);
		if (kit == null)
		{
			return TextCommandResult.Error("No kit named '" + name + "'. Run /kit to see the kits you can redeem.");
		}

		if (!IsAssignedTo(kit, caller.ServerData))
		{
			return TextCommandResult.Error("You are not assigned the kit '" + kit.Name + "'.");
		}

		if (kit.OnePerLife && HasRedeemedThisLife(caller.ServerData, kit.Name))
		{
			return TextCommandResult.Error("You have already redeemed '" + kit.Name + "' this life.");
		}

		StratumCommandAccessConfig perKitCooldown = new StratumCommandAccessConfig
		{
			CooldownSeconds = kit.CooldownSeconds,
			CooldownBypassForStaff = true
		};
		if (!StratumCommandCooldowns.TryUse(args.Caller, server, "kit:" + kit.Name, perKitCooldown, out TimeSpan remaining))
		{
			return TextCommandResult.Error("Wait " + Math.Ceiling(remaining.TotalSeconds) + "s before redeeming '" + kit.Name + "' again.");
		}

		ServerPlayer target = server.GetClientByUID(caller.PlayerUID)?.Player;
		if (target == null)
		{
			return TextCommandResult.Error("Could not resolve your player entity.");
		}

		StratumKitGiver.KitGiveResult result = StratumKitGiver.Give(target, kit);
		MarkRedeemedThisLife(caller.ServerData, kit.Name);
		StratumRuntime.LogAudit("kit redeemed name=" + kit.Name + " actor=" + args.Caller.GetName() + " given=" + result.GivenCount + " dropped=" + result.DroppedCount + " failed=" + result.FailedCount, true);
		return DescribeGiveResult(kit, result);
	}

	private TextCommandResult ListAvailableKits(IServerPlayer caller)
	{
		List<StratumKitDefinition> available = StratumKitStore.All().Where(kit => IsAssignedTo(kit, caller.ServerData)).ToList();
		if (available.Count == 0)
		{
			return TextCommandResult.Success(StratumCommandText.Info("You are not assigned any kits."));
		}

		StringBuilder output = new StringBuilder(StratumCommandText.Title("Your kits"));
		foreach (StratumKitDefinition kit in available.OrderBy(kit => kit.Name, StringComparer.OrdinalIgnoreCase))
		{
			output.Append(StratumCommandText.Bullet(kit.Name, kit.Items.Count + " item(s)" + (kit.CooldownSeconds > 0 ? ", cooldown " + kit.CooldownSeconds + "s" : "") + (kit.OnePerLife ? ", once per life" : "")));
		}

		output.Append(StratumCommandText.Row("Redeem", "/kit <name>"));
		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult HandleKitEdit(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, "kitedit", StratumRuntime.Config.Commands.KitEdit, out TextCommandResult failure))
		{
			return failure;
		}

		string action = (args[0] as string)?.ToLowerInvariant();
		string name = args.Parsers[1].IsMissing ? null : args[1] as string;
		string value = args.Parsers[2].IsMissing ? null : args[2] as string;

		if (!CheckActionArgs(action, name, value, out TextCommandResult usage))
		{
			return usage;
		}

		return action switch
		{
			"create" => CreateKit(name, args.Caller),
			"additem" => AddItem(name, args.Caller),
			"removeitem" => RemoveItem(name, value),
			"delete" => DeleteKit(name),
			"rename" => RenameKit(name, value),
			"setrole" => SetRole(name, value),
			"setcooldown" => SetCooldown(name, value),
			"setscope" => SetScope(name, value),
			"onrespawn" => SetFlag(name, value, "onrespawn"),
			"oneperlife" => SetFlag(name, value, "oneperlife"),
			"preview" => PreviewKit(name),
			"give" => GiveKit(name, value),
			"list" => ListAllKits(),
			_ => UnknownAction(action)
		};
	}

	// Argument shape per /kitedit action: how many words follow the action, and the usage line to
	// show when the caller gets it wrong. Angle brackets are written escaped because command
	// results render as VTML in chat, where a raw <name> is parsed as an unknown tag and dropped.
	private static (int ArgCount, string Usage) ActionShape(string action)
	{
		return action switch
		{
			"list" => (0, "/kitedit list"),
			"create" => (1, "/kitedit create &lt;name&gt;"),
			"additem" => (1, "/kitedit additem &lt;name&gt;"),
			"removeitem" => (2, "/kitedit removeitem &lt;name&gt; &lt;index&gt;"),
			"preview" => (1, "/kitedit preview &lt;name&gt;"),
			"delete" => (1, "/kitedit delete &lt;name&gt;"),
			"rename" => (2, "/kitedit rename &lt;name&gt; &lt;newName&gt;"),
			"setrole" => (2, "/kitedit setrole &lt;name&gt; &lt;role&gt;"),
			"setcooldown" => (2, "/kitedit setcooldown &lt;name&gt; &lt;seconds&gt;"),
			"setscope" => (2, "/kitedit setscope &lt;name&gt; all|hotbar"),
			"onrespawn" => (2, "/kitedit onrespawn &lt;name&gt; on|off"),
			"oneperlife" => (2, "/kitedit oneperlife &lt;name&gt; on|off"),
			"give" => (2, "/kitedit give &lt;name&gt; &lt;player&gt;"),
			_ => (-1, null)
		};
	}

	// The command is registered with two optional word parsers, so vanilla only rejects a fourth
	// word. An argument the action itself does not take has to be caught here or it is silently
	// ignored, which is how /kitedit create starter_kit 1 in #293 looked like it had worked.
	private static bool CheckActionArgs(string action, string name, string value, out TextCommandResult failure)
	{
		failure = null;
		(int expected, string usage) = ActionShape(action);
		if (usage == null)
		{
			failure = UnknownAction(action);
			return false;
		}

		int provided = (string.IsNullOrWhiteSpace(name) ? 0 : 1) + (string.IsNullOrWhiteSpace(value) ? 0 : 1);
		if (provided < expected)
		{
			failure = TextCommandResult.Error("Usage: " + usage);
			return false;
		}

		if (provided > expected)
		{
			failure = TextCommandResult.Error("/kitedit " + action + " takes " + DescribeArgCount(expected) + ". Usage: " + usage);
			return false;
		}

		return true;
	}

	private static string DescribeArgCount(int count)
	{
		return count switch
		{
			0 => "no arguments",
			1 => "1 argument",
			_ => count + " arguments"
		};
	}

	private static TextCommandResult UnknownAction(string action)
	{
		return TextCommandResult.Error("Unknown /kitedit action '" + action + "'. Actions: " + string.Join(", ", KitEditActions) + ". Run /kitedit list to see existing kits.");
	}

	private TextCommandResult CreateKit(string name, Caller caller)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return TextCommandResult.Error("Usage: /kitedit create &lt;name&gt;");
		}

		IPlayer player = caller.Player;
		if (player?.InventoryManager == null)
		{
			return TextCommandResult.Error("Only a connected player can snapshot a kit from their own inventory.");
		}

		StratumKitDefinition existing = StratumKitStore.Find(name);
		// A re-create keeps whatever scope /kitedit setscope already put on the kit; a brand new
		// kit defaults to "all", matching the snapshot behavior this command always had.
		string scope = existing?.Scope ?? "all";
		List<StratumKitItem> items = SnapshotInventory(player, scope);
		if (items.Count == 0)
		{
			return TextCommandResult.Error("Your inventory is empty; nothing to snapshot.");
		}

		StratumKitDefinition kit = existing ?? new StratumKitDefinition
		{
			Name = name,
			CreatedByUid = player.PlayerUID,
			CreatedByName = player.PlayerName,
			CreatedUtc = DateTime.UtcNow
		};
		kit.Items = items;
		StratumKitStore.Upsert(kit);
		StratumRuntime.LogAudit("kitedit create name=" + kit.Name + " items=" + items.Count + " actor=" + caller.GetName(), true);
		return TextCommandResult.Success(StratumCommandText.Confirm((existing != null ? "Kit updated" : "Kit created"), kit.Name + ": " + items.Count + " item(s) snapshotted from your inventory."));
	}

	private TextCommandResult AddItem(string name, Caller caller)
	{
		StratumKitDefinition kit = StratumKitStore.Find(name);
		if (kit == null)
		{
			return TextCommandResult.Error("No kit named '" + name + "'.");
		}

		ItemSlot activeSlot = caller.Player?.InventoryManager?.ActiveHotbarSlot;
		if (activeSlot?.Itemstack == null)
		{
			return TextCommandResult.Error("Hold the item you want to add in your active hotbar slot.");
		}

		StratumKitItem item = EncodeStack(activeSlot.Itemstack);
		kit.Items.Add(item);
		StratumKitStore.Save();
		StratumRuntime.LogAudit("kitedit additem name=" + kit.Name + " item=" + item.Code + " actor=" + caller.GetName(), true);
		return TextCommandResult.Success(StratumCommandText.Confirm("Item added", item.Code + " x" + item.Quantity + " added to '" + kit.Name + "' (now " + kit.Items.Count + " item(s))."));
	}

	private TextCommandResult RemoveItem(string name, string indexText)
	{
		StratumKitDefinition kit = StratumKitStore.Find(name);
		if (kit == null)
		{
			return TextCommandResult.Error("No kit named '" + name + "'.");
		}

		if (!int.TryParse(indexText, out int index) || index < 1 || index > kit.Items.Count)
		{
			return TextCommandResult.Error("Usage: /kitedit removeitem &lt;name&gt; &lt;index 1-" + kit.Items.Count + "&gt;. See /kitedit preview " + kit.Name + ".");
		}

		StratumKitItem removed = kit.Items[index - 1];
		kit.Items.RemoveAt(index - 1);
		StratumKitStore.Save();
		return TextCommandResult.Success(StratumCommandText.Confirm("Item removed", removed.Code + " removed from '" + kit.Name + "' (now " + kit.Items.Count + " item(s))."));
	}

	private TextCommandResult DeleteKit(string name)
	{
		if (!StratumKitStore.Delete(name))
		{
			return TextCommandResult.Error("No kit named '" + name + "'.");
		}

		return TextCommandResult.Success(StratumCommandText.Confirm("Kit deleted", name));
	}

	private TextCommandResult RenameKit(string name, string newName)
	{
		if (string.IsNullOrWhiteSpace(newName))
		{
			return TextCommandResult.Error("Usage: /kitedit rename &lt;name&gt; &lt;newName&gt;");
		}

		if (!StratumKitStore.Rename(name, newName))
		{
			return TextCommandResult.Error("Rename failed: either '" + name + "' does not exist, or '" + newName + "' is already taken.");
		}

		return TextCommandResult.Success(StratumCommandText.Confirm("Kit renamed", name + " -> " + newName));
	}

	// Toggle: setrole adds the role scope if the kit does not already carry it, removes it if it
	// does. Repeated calls build up or tear down a multi-role AssignedTo list without a separate
	// "clear" verb.
	private TextCommandResult SetRole(string name, string roleCode)
	{
		StratumKitDefinition kit = StratumKitStore.Find(name);
		if (kit == null)
		{
			return TextCommandResult.Error("No kit named '" + name + "'.");
		}

		if (string.IsNullOrWhiteSpace(roleCode))
		{
			return TextCommandResult.Error("Usage: /kitedit setrole &lt;name&gt; &lt;role&gt;");
		}

		if (!server.Config.RolesByCode.ContainsKey(roleCode))
		{
			return TextCommandResult.Error("No role found for '" + roleCode + "'.");
		}

		string scope = "role:" + roleCode;
		bool removed = kit.AssignedTo.RemoveAll(entry => string.Equals(entry, scope, StringComparison.OrdinalIgnoreCase)) > 0;
		if (!removed)
		{
			kit.AssignedTo.Add(scope);
		}

		StratumKitStore.Save();
		return TextCommandResult.Success(StratumCommandText.Confirm(removed ? "Role unassigned" : "Role assigned", roleCode + (removed ? " no longer gets " : " now gets ") + kit.Name));
	}

	private TextCommandResult SetCooldown(string name, string secondsText)
	{
		StratumKitDefinition kit = StratumKitStore.Find(name);
		if (kit == null)
		{
			return TextCommandResult.Error("No kit named '" + name + "'.");
		}

		if (!int.TryParse(secondsText, out int seconds) || seconds < 0)
		{
			return TextCommandResult.Error("Usage: /kitedit setcooldown &lt;name&gt; &lt;seconds&gt;, 0 disables the cooldown");
		}

		kit.CooldownSeconds = seconds;
		StratumKitStore.Save();
		return TextCommandResult.Success(StratumCommandText.Confirm("Cooldown set", kit.Name + " = " + seconds + "s"));
	}

	// Changes what a future create/re-create captures; does not retroactively filter Items already
	// stored on the kit. Re-run create afterward to apply the new scope to the item list itself.
	private TextCommandResult SetScope(string name, string scope)
	{
		StratumKitDefinition kit = StratumKitStore.Find(name);
		if (kit == null)
		{
			return TextCommandResult.Error("No kit named '" + name + "'.");
		}

		string normalized = scope?.ToLowerInvariant();
		if (normalized != "all" && normalized != "hotbar")
		{
			return TextCommandResult.Error("Usage: /kitedit setscope &lt;name&gt; all|hotbar");
		}

		kit.Scope = normalized;
		StratumKitStore.Save();
		return TextCommandResult.Success(StratumCommandText.Confirm("Scope set", kit.Name + " = " + normalized + ". Run /kitedit create " + kit.Name + " again to apply it to the item list."));
	}

	private TextCommandResult SetFlag(string name, string onOff, string flag)
	{
		StratumKitDefinition kit = StratumKitStore.Find(name);
		if (kit == null)
		{
			return TextCommandResult.Error("No kit named '" + name + "'.");
		}

		bool? on = string.Equals(onOff, "on", StringComparison.OrdinalIgnoreCase) ? true
			: string.Equals(onOff, "off", StringComparison.OrdinalIgnoreCase) ? false
			: (bool?)null;
		if (on == null)
		{
			return TextCommandResult.Error("Usage: /kitedit " + flag + " &lt;name&gt; on|off");
		}

		if (flag == "onrespawn")
		{
			kit.GiveOnRespawn = on.Value;
		}
		else
		{
			kit.OnePerLife = on.Value;
		}

		StratumKitStore.Save();
		return TextCommandResult.Success(StratumCommandText.Confirm(flag + " set", kit.Name + " = " + (on.Value ? "on" : "off")));
	}

	private TextCommandResult PreviewKit(string name)
	{
		StratumKitDefinition kit = StratumKitStore.Find(name);
		if (kit == null)
		{
			return TextCommandResult.Error("No kit named '" + name + "'.");
		}

		StringBuilder output = new StringBuilder(StratumCommandText.Title("Kit: " + kit.Name));
		output.Append(StratumCommandText.Row("Assigned to", kit.AssignedTo.Count == 0 ? "everyone with stratum.kits" : string.Join(", ", kit.AssignedTo)));
		output.Append(StratumCommandText.Row("Scope", string.IsNullOrEmpty(kit.Scope) ? "all" : kit.Scope));
		output.Append(StratumCommandText.Row("Cooldown", kit.CooldownSeconds + "s"));
		output.Append(StratumCommandText.Row("One per life", kit.OnePerLife ? "yes" : "no"));
		output.Append(StratumCommandText.Row("Give on respawn", kit.GiveOnRespawn ? "yes" : "no"));
		output.Append("\n").Append(StratumCommandText.Title("Items"));
		for (int i = 0; i < kit.Items.Count; i++)
		{
			StratumKitItem item = kit.Items[i];
			output.Append(StratumCommandText.Bullet((i + 1).ToString(), item.Code + " x" + item.Quantity));
		}

		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult GiveKit(string name, string playerName)
	{
		StratumKitDefinition kit = StratumKitStore.Find(name);
		if (kit == null)
		{
			return TextCommandResult.Error("No kit named '" + name + "'.");
		}

		ConnectedClient client = server.Clients.Values.FirstOrDefault(candidate => string.Equals(candidate.Player?.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));
		if (client?.Player == null)
		{
			return TextCommandResult.Error("No online player named '" + playerName + "'.");
		}

		StratumKitGiver.KitGiveResult result = StratumKitGiver.Give(client.Player, kit);
		return DescribeGiveResult(kit, result);
	}

	private TextCommandResult ListAllKits()
	{
		IReadOnlyList<StratumKitDefinition> kits = StratumKitStore.All();
		if (kits.Count == 0)
		{
			return TextCommandResult.Success(StratumCommandText.Info("No kits exist yet. Create one with /kitedit create <name>."));
		}

		StringBuilder output = new StringBuilder(StratumCommandText.Title("All kits"));
		foreach (StratumKitDefinition kit in kits.OrderBy(kit => kit.Name, StringComparer.OrdinalIgnoreCase))
		{
			output.Append(StratumCommandText.Bullet(kit.Name, kit.Items.Count + " item(s), assigned to " + (kit.AssignedTo.Count == 0 ? "everyone" : string.Join(", ", kit.AssignedTo))));
		}

		return TextCommandResult.Success(output.ToString());
	}

	private static TextCommandResult DescribeGiveResult(StratumKitDefinition kit, StratumKitGiver.KitGiveResult result)
	{
		string summary = result.GivenCount + " given";
		if (result.DroppedCount > 0)
		{
			summary += ", " + result.DroppedCount + " dropped at feet (inventory full)";
		}

		if (result.FailedCount > 0)
		{
			summary += ", " + result.FailedCount + " failed to decode";
		}

		return result.AllGiven
			? TextCommandResult.Success(StratumCommandText.Confirm("Kit '" + kit.Name + "' given", summary))
			: TextCommandResult.Success(StratumCommandText.Warning("Kit '" + kit.Name + "' partially given: " + summary));
	}

	// "all" walks hotbar and character (worn armor and a worn backpack's own stack included);
	// "hotbar" walks only the hotbar, which already includes the offhand slot, so nothing worn or
	// stored in a character equipment slot is captured. A worn backpack's contents are stored in
	// the backpack stack itself, inside the character inventory; walking the backpack inventory
	// too would add every content item a second time, so it is never walked directly here either
	// way.
	private static List<StratumKitItem> SnapshotInventory(IPlayer player, string scope)
	{
		bool hotbarOnly = string.Equals(scope, "hotbar", StringComparison.OrdinalIgnoreCase);
		List<StratumKitItem> items = new List<StratumKitItem>();
		foreach (InventoryBase inventory in player.InventoryManager.InventoriesOrdered)
		{
			bool included = inventory.ClassName == GlobalConstants.hotBarInvClassName
				|| (!hotbarOnly && inventory.ClassName == GlobalConstants.characterInvClassName);
			if (!included)
			{
				continue;
			}

			foreach (ItemSlot slot in inventory)
			{
				if (slot.Itemstack != null)
				{
					items.Add(EncodeStack(slot.Itemstack));
				}
			}
		}

		return items;
	}

	private static StratumKitItem EncodeStack(ItemStack stack)
	{
		JsonItemStack jsonStack = new JsonItemStack
		{
			Type = stack.Class,
			Code = stack.Collectible?.Code,
			StackSize = stack.StackSize,
			Attributes = stack.Attributes == null ? null : JsonObject.FromJson(stack.Attributes.ToJsonToken())
		};
		JObject json = new JObject
		{
			["type"] = jsonStack.Type == EnumItemClass.Block ? "block" : "item",
			["code"] = jsonStack.Code?.ToShortString(),
			["stacksize"] = jsonStack.StackSize
		};
		if (jsonStack.Attributes?.Token != null)
		{
			json["attributes"] = jsonStack.Attributes.Token.DeepClone();
		}

		return new StratumKitItem
		{
			StackJson = json.ToString(Formatting.None),
			Code = stack.Collectible?.Code?.ToString() ?? "unknown",
			Quantity = stack.StackSize
		};
	}

	private static bool IsAssignedTo(StratumKitDefinition kit, IServerPlayerData playerData)
	{
		if (kit.AssignedTo == null || kit.AssignedTo.Count == 0)
		{
			return true;
		}

		return kit.AssignedTo.Any(scope => string.Equals(scope, "role:" + playerData.RoleCode, StringComparison.OrdinalIgnoreCase));
	}

	private static bool HasRedeemedThisLife(IServerPlayerData playerData, string kitName)
	{
		if (!playerData.CustomPlayerData.TryGetValue(RedeemedThisLifeKey, out string raw) || string.IsNullOrWhiteSpace(raw))
		{
			return false;
		}

		return raw.Split(';').Contains(kitName, StringComparer.OrdinalIgnoreCase);
	}

	private void MarkRedeemedThisLife(IServerPlayerData playerData, string kitName)
	{
		playerData.CustomPlayerData.TryGetValue(RedeemedThisLifeKey, out string raw);
		HashSet<string> redeemed = string.IsNullOrWhiteSpace(raw)
			? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			: new HashSet<string>(raw.Split(';'), StringComparer.OrdinalIgnoreCase);
		redeemed.Add(kitName);
		playerData.CustomPlayerData[RedeemedThisLifeKey] = string.Join(";", redeemed);
		server.PlayerDataManager.playerDataDirty = true;
	}

	private bool CheckAccess(TextCommandCallingArgs args, string commandLabel, StratumCommandAccessConfig access, out TextCommandResult failure)
	{
		failure = null;
		if (!StratumRuntime.Config.Commands.Enabled)
		{
			failure = TextCommandResult.Error("Stratum commands are disabled.");
			return false;
		}

		if (access == null || !access.Enabled)
		{
			failure = TextCommandResult.Error("/" + commandLabel + " is disabled.");
			return false;
		}

		if (!StratumCommandAccessCatalog.CallerHasAccess(args.Caller, server, access))
		{
			failure = TextCommandResult.Error("You do not have permission to use /" + commandLabel + ".");
			return false;
		}

		if (!StratumCommandCooldowns.TryUse(args.Caller, server, commandLabel, access, out TimeSpan remaining))
		{
			failure = TextCommandResult.Error("Wait " + Math.Ceiling(remaining.TotalSeconds) + "s before using /" + commandLabel + " again.");
			return false;
		}

		return true;
	}
}
