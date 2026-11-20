using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

/// <summary>
/// Stratum #335: /group admin. Staff authority over player groups that vanilla leaves to the
/// players themselves. The subcommand is registered from the vanilla /group tree so admins find
/// it where they already look; every rule it applies lives in <see cref="StratumGroupPolicy"/>.
/// </summary>
internal sealed class CmdStratumGroups
{
	private readonly ServerMain server;

	public CmdStratumGroups(ServerMain server)
	{
		this.server = server;
	}

	public TextCommandResult Handle(TextCommandCallingArgs args)
	{
		// The subcommand registers under the /group tree's own privilege, so Commands.GroupAdmin
		// is read here on every call: a /stratum reload that changes its privilege, switches it
		// off, or adds a cooldown takes effect without a restart.
		if (!CheckAccess(args, out TextCommandResult denied))
		{
			return denied;
		}

		if (!StratumRuntime.Config.Groups.Enabled)
		{
			return TextCommandResult.Error("Group administration is off (Groups.Enabled=false).");
		}

		string action = Arg(args, 0) ?? "info";
		string groupName = Arg(args, 1);
		string first = Arg(args, 2);
		string second = Arg(args, 3);

		switch (action.ToLowerInvariant())
		{
			case "kinds":
				return ListKinds();
			case "add":
				return AddPlayer(groupName, first, second, args.Caller);
			case "remove":
				return RemovePlayer(groupName, first, args.Caller);
			case "lock":
				return LockPlayer(groupName, first, second, args.Caller);
			case "unlock":
				return UnlockPlayer(groupName, first, args.Caller);
			case "freeze":
				return FreezeRoster(groupName, first, args.Caller);
			case "kind":
				return SetKind(groupName, first, args.Caller);
			case "tag":
				return SetTag(groupName, first, args.Caller);
			case "relation":
				return SetRelation(groupName, first, second, args.Caller);
			case "info":
				return DescribeGroup(groupName);
			default:
				return TextCommandResult.Error("Usage: /group admin [add|remove|lock|unlock|freeze|kind|tag|relation|info|kinds] ...");
		}
	}

	// ---------------------------------------------------------------- membership

	private TextCommandResult AddPlayer(string groupName, string playerName, string levelText, Caller caller)
	{
		if (!TryResolve(groupName, playerName, out PlayerGroup group, out ServerPlayerData playerData, out TextCommandResult failure))
		{
			return failure;
		}

		EnumPlayerGroupMemberShip level = EnumPlayerGroupMemberShip.Member;
		if (!string.IsNullOrWhiteSpace(levelText))
		{
			// 0 = None is refused rather than stored: a None membership is not a membership, so
			// the player would be reported as added while holding nothing.
			if (!int.TryParse(levelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed < 1 || parsed > 3)
			{
				return TextCommandResult.Error(StratumGroupPolicy.AccessLevelError);
			}

			level = (EnumPlayerGroupMemberShip)parsed;
		}

		if (!StratumGroupPolicy.TryStaffAdd(server, playerData, group, out PlayerGroup displaced, out string error))
		{
			return TextCommandResult.Error(error);
		}

		if (displaced != null)
		{
			playerData.LeaveGroup(displaced);
			displaced.OnlinePlayers.RemoveAll(player => player.PlayerUID == playerData.PlayerUID);
		}

		PlayerGroupMembership membership = playerData.JoinGroup(group, level);
		server.PlayerDataManager.playerDataDirty = true;
		RefreshMembership(playerData, group, membership);

		StratumGroupPolicy.LogAudit("group admin add group=" + group.Name + " player=" + playerData.LastKnownPlayername
			+ " level=" + level + (displaced != null ? " displaced=" + displaced.Name : string.Empty) + " actor=" + caller.GetName());

		string detail = "group=" + group.Name + " level=" + level;
		if (displaced != null)
		{
			detail += ", moved out of " + displaced.Name;
		}

		return TextCommandResult.Success(StratumCommandText.Confirm("Added " + playerData.LastKnownPlayername, detail));
	}

	private TextCommandResult RemovePlayer(string groupName, string playerName, Caller caller)
	{
		if (!TryResolve(groupName, playerName, out PlayerGroup group, out ServerPlayerData playerData, out TextCommandResult failure))
		{
			return failure;
		}

		if (playerData.PlayerGroupMemberShips == null || !playerData.PlayerGroupMemberShips.ContainsKey(group.Uid))
		{
			return TextCommandResult.Error(playerData.LastKnownPlayername + " is not in group " + group.Name + ".");
		}

		// Staff removal is deliberate, so it clears the lock rather than tripping over it.
		bool wasLocked = StratumGroupPolicy.ClearLock(server, playerData, group.Uid);
		playerData.LeaveGroup(group);
		group.OnlinePlayers.RemoveAll(player => player.PlayerUID == playerData.PlayerUID);
		server.PlayerDataManager.playerDataDirty = true;
		RefreshMembership(playerData, group, null);

		StratumGroupPolicy.LogAudit("group admin remove group=" + group.Name + " player=" + playerData.LastKnownPlayername
			+ (wasLocked ? " clearedlock=true" : string.Empty) + " actor=" + caller.GetName());

		return TextCommandResult.Success(StratumCommandText.Confirm("Removed " + playerData.LastKnownPlayername, "group=" + group.Name));
	}

	// ---------------------------------------------------------------- locks

	private TextCommandResult LockPlayer(string groupName, string playerName, string durationText, Caller caller)
	{
		if (!TryResolve(groupName, playerName, out PlayerGroup group, out ServerPlayerData playerData, out TextCommandResult failure))
		{
			return failure;
		}

		StratumGroupKindConfig kind = StratumGroupPolicy.KindOf(group);
		if (!kind.Lockable)
		{
			return TextCommandResult.Error("Group kind '" + (kind.Code ?? "unclassified") + "' is not lockable.");
		}

		if (playerData.PlayerGroupMemberShips == null || !playerData.PlayerGroupMemberShips.TryGetValue(group.Uid, out PlayerGroupMembership membership)
			|| membership == null || membership.Level == EnumPlayerGroupMemberShip.None)
		{
			return TextCommandResult.Error(playerData.LastKnownPlayername + " is not in group " + group.Name + ". Add them first.");
		}

		if (!TryParseLockDuration(durationText, out DateTime? expiresUtc, out string durationError))
		{
			return TextCommandResult.Error(durationError);
		}

		StratumGroupPolicy.SetLock(server, playerData, group.Uid, expiresUtc);
		StratumGroupPolicy.LogAudit("group admin lock group=" + group.Name + " player=" + playerData.LastKnownPlayername
			+ " until=" + (expiresUtc.HasValue ? expiresUtc.Value.ToString("u", CultureInfo.InvariantCulture) : "permanent") + " actor=" + caller.GetName());

		return TextCommandResult.Success(StratumCommandText.Confirm("Locked " + playerData.LastKnownPlayername,
			"group=" + group.Name + (expiresUtc.HasValue ? StratumGroupPolicy.FormatUntil(expiresUtc) : ", permanent")));
	}

	private TextCommandResult UnlockPlayer(string groupName, string playerName, Caller caller)
	{
		if (!TryResolve(groupName, playerName, out PlayerGroup group, out ServerPlayerData playerData, out TextCommandResult failure))
		{
			return failure;
		}

		if (!StratumGroupPolicy.ClearLock(server, playerData, group.Uid))
		{
			return TextCommandResult.Error(playerData.LastKnownPlayername + " is not locked into group " + group.Name + ".");
		}

		StratumGroupPolicy.LogAudit("group admin unlock group=" + group.Name + " player=" + playerData.LastKnownPlayername + " actor=" + caller.GetName());
		return TextCommandResult.Success(StratumCommandText.Confirm("Unlocked " + playerData.LastKnownPlayername, "group=" + group.Name));
	}

	// ---------------------------------------------------------------- group state

	private TextCommandResult FreezeRoster(string groupName, string modeText, Caller caller)
	{
		if (!TryResolveGroup(groupName, out PlayerGroup group, out TextCommandResult failure))
		{
			return failure;
		}

		bool frozen = string.IsNullOrWhiteSpace(modeText)
			? !group.StratumRosterFrozen
			: string.Equals(modeText, "on", StringComparison.OrdinalIgnoreCase) || string.Equals(modeText, "true", StringComparison.OrdinalIgnoreCase);

		group.StratumRosterFrozen = frozen;
		server.PlayerDataManager.playerGroupsDirty = true;
		StratumGroupPolicy.LogAudit("group admin freeze group=" + group.Name + " frozen=" + frozen + " actor=" + caller.GetName());

		return TextCommandResult.Success(StratumCommandText.Confirm("Roster " + (frozen ? "frozen" : "unfrozen"), "group=" + group.Name));
	}

	private TextCommandResult SetKind(string groupName, string kindCode, Caller caller)
	{
		if (!TryResolveGroup(groupName, out PlayerGroup group, out TextCommandResult failure))
		{
			return failure;
		}

		if (string.IsNullOrWhiteSpace(kindCode))
		{
			return TextCommandResult.Success(StratumCommandText.Row("Kind of " + group.Name, group.StratumKind ?? "unclassified"));
		}

		bool clearing = string.Equals(kindCode, "none", StringComparison.OrdinalIgnoreCase) || string.Equals(kindCode, "unclassified", StringComparison.OrdinalIgnoreCase);
		if (!clearing && !StratumGroupPolicy.KindExists(kindCode))
		{
			return TextCommandResult.Error("No group kind '" + kindCode + "'. Use /group admin kinds to list them.");
		}

		if (!clearing)
		{
			StratumGroupKindConfig kind = StratumGroupPolicy.KindByCode(kindCode);
			if (kind.Exclusive)
			{
				List<string> conflicted = FindMembersWithOtherGroupOfKind(group, kind);
				if (conflicted.Count > 0)
				{
					return TextCommandResult.Error("Cannot set kind '" + kind.Code + "': already in another " + kind.Code + ": "
						+ string.Join(", ", conflicted.Take(8)) + (conflicted.Count > 8 ? ", ..." : string.Empty));
				}
			}
		}

		group.StratumKind = clearing ? null : StratumGroupPolicy.KindByCode(kindCode).Code;
		server.PlayerDataManager.playerGroupsDirty = true;
		RefreshTagsFor(group);
		StratumGroupPolicy.LogAudit("group admin kind group=" + group.Name + " kind=" + (group.StratumKind ?? "unclassified") + " actor=" + caller.GetName());

		return TextCommandResult.Success(StratumCommandText.Confirm("Kind set", "group=" + group.Name + " kind=" + (group.StratumKind ?? "unclassified")));
	}

	private TextCommandResult SetTag(string groupName, string tag, Caller caller)
	{
		if (!TryResolveGroup(groupName, out PlayerGroup group, out TextCommandResult failure))
		{
			return failure;
		}

		if (string.IsNullOrWhiteSpace(tag))
		{
			return TextCommandResult.Success(StratumCommandText.Row("Tag of " + group.Name, group.StratumTag ?? "none"));
		}

		if (string.Equals(tag, "none", StringComparison.OrdinalIgnoreCase))
		{
			group.StratumTag = null;
		}
		else
		{
			int maxLength = StratumRuntime.Config.Groups.MaxTagLength;
			if (tag.Length > maxLength)
			{
				return TextCommandResult.Error("Tag is longer than Groups.MaxTagLength (" + maxLength + ").");
			}

			group.StratumTag = tag;
		}

		server.PlayerDataManager.playerGroupsDirty = true;
		RefreshTagsFor(group);
		StratumGroupPolicy.LogAudit("group admin tag group=" + group.Name + " tag=" + (group.StratumTag ?? "none") + " actor=" + caller.GetName());

		return TextCommandResult.Success(StratumCommandText.Confirm("Tag set", "group=" + group.Name + " tag=" + (group.StratumTag ?? "none")));
	}

	private TextCommandResult SetRelation(string groupName, string otherName, string relation, Caller caller)
	{
		if (!StratumRuntime.Config.Groups.RelationsEnabled)
		{
			return TextCommandResult.Error("Group relations are off (Groups.RelationsEnabled=false).");
		}

		if (!TryResolveGroup(groupName, out PlayerGroup group, out TextCommandResult failure))
		{
			return failure;
		}

		if (!TryResolveGroup(otherName, out PlayerGroup other, out TextCommandResult otherFailure))
		{
			return otherFailure;
		}

		if (group.Uid == other.Uid)
		{
			return TextCommandResult.Error("A group cannot have a relation with itself.");
		}

		if (string.IsNullOrWhiteSpace(relation))
		{
			return TextCommandResult.Success(StratumCommandText.Row(group.Name + " to " + other.Name, StratumGroupPolicy.GetRelation(group, other.Uid)));
		}

		string normalized = relation.ToLowerInvariant();
		if (normalized != StratumGroupPolicy.RelationAlly && normalized != StratumGroupPolicy.RelationEnemy && normalized != StratumGroupPolicy.RelationNeutral)
		{
			return TextCommandResult.Error("Relation must be ally, enemy, or neutral.");
		}

		StratumGroupPolicy.SetRelation(server, group, other, normalized);
		StratumGroupPolicy.LogAudit("group admin relation group=" + group.Name + " other=" + other.Name + " relation=" + normalized + " actor=" + caller.GetName());

		return TextCommandResult.Success(StratumCommandText.Confirm("Relation set", group.Name + " and " + other.Name + " are now " + normalized));
	}

	// ---------------------------------------------------------------- reporting

	private TextCommandResult ListKinds()
	{
		StratumGroupsConfig config = StratumRuntime.Config.Groups;
		StringBuilder output = new StringBuilder(StratumCommandText.Title("Group kinds"));
		output.Append(StratumCommandText.Row("Default for /group create", config.DefaultKind ?? "unclassified"));

		if (config.Kinds.Count == 0)
		{
			output.Append(StratumCommandText.Empty("No kinds defined in Groups.Kinds."));
			return TextCommandResult.Success(output.ToString());
		}

		foreach (StratumGroupKindConfig kind in config.Kinds)
		{
			output.Append(StratumCommandText.Bullet(kind.Code,
				"exclusive=" + kind.Exclusive
				+ " max=" + (kind.MaxMembers > 0 ? kind.MaxMembers.ToString(CultureInfo.InvariantCulture) : "unlimited")
				+ " lockable=" + kind.Lockable
				+ " sameside=" + kind.CountsAsSameSide
				+ " playercreatable=" + kind.PlayerCreatable));
		}

		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult DescribeGroup(string groupName)
	{
		if (!TryResolveGroup(groupName, out PlayerGroup group, out TextCommandResult failure))
		{
			return failure;
		}

		StratumGroupKindConfig kind = StratumGroupPolicy.KindOf(group);
		StringBuilder output = new StringBuilder(StratumCommandText.Title("Group: " + group.Name));
		output.Append(StratumCommandText.Row("Uid", group.Uid.ToString(CultureInfo.InvariantCulture)));
		output.Append(StratumCommandText.Row("Kind", group.StratumKind ?? "unclassified"));
		output.Append(StratumCommandText.Row("Members", StratumGroupPolicy.CountMembers(server, group.Uid)
			+ (kind.MaxMembers > 0 ? " of " + kind.MaxMembers : string.Empty)));
		// The raw online roster, duplicates and all, since that is the list tag refreshes walk.
		output.Append(StratumCommandText.Row("Online members", group.OnlinePlayers.Count == 0
			? "none"
			: string.Join(", ", group.OnlinePlayers.Select(player => player.PlayerName))));
		output.Append(StratumCommandText.Row("Roster", group.StratumRosterFrozen ? "frozen" : "open"));
		output.Append(StratumCommandText.Row("Tag", group.StratumTag ?? kind.Tag ?? "none"));
		output.Append(StratumCommandText.Row("Counts as same side", kind.CountsAsSameSide ? "yes" : "no"));

		List<KeyValuePair<PlayerGroup, string>> relations = StratumGroupPolicy.ListRelations(server, group);
		output.Append(StratumCommandText.Row("Relations", relations.Count == 0
			? "none"
			: string.Join(", ", relations.Select(entry => entry.Key.Name + "=" + entry.Value))));

		List<string> locked = new List<string>();
		foreach (ServerPlayerData data in server.PlayerDataManager.PlayerDataByUid.Values)
		{
			if (StratumGroupPolicy.IsLocked(server, data, group.Uid, out DateTime? expires))
			{
				locked.Add(data.LastKnownPlayername + (expires.HasValue ? StratumGroupPolicy.FormatUntil(expires) : string.Empty));
			}
		}

		output.Append(StratumCommandText.Row("Locked members", locked.Count == 0 ? "none" : string.Join(", ", locked)));
		return TextCommandResult.Success(output.ToString());
	}

	// ---------------------------------------------------------------- helpers

	private static string Arg(TextCommandCallingArgs args, int index)
	{
		if (index >= args.Parsers.Count || args.Parsers[index].IsMissing)
		{
			return null;
		}

		return args[index] as string;
	}

	private bool TryResolveGroup(string groupName, out PlayerGroup group, out TextCommandResult failure)
	{
		group = null;
		failure = null;
		if (string.IsNullOrWhiteSpace(groupName))
		{
			failure = TextCommandResult.Error("Name a group.");
			return false;
		}

		group = server.PlayerDataManager.GetPlayerGroupByName(groupName);
		if (group == null && int.TryParse(groupName, NumberStyles.Integer, CultureInfo.InvariantCulture, out int uid))
		{
			server.PlayerDataManager.PlayerGroupsById.TryGetValue(uid, out group);
		}

		if (group == null)
		{
			failure = TextCommandResult.Error("No group named '" + groupName + "'.");
			return false;
		}

		return true;
	}

	private bool TryResolve(string groupName, string playerName, out PlayerGroup group, out ServerPlayerData playerData, out TextCommandResult failure)
	{
		playerData = null;
		if (!TryResolveGroup(groupName, out group, out failure))
		{
			return false;
		}

		if (string.IsNullOrWhiteSpace(playerName))
		{
			failure = TextCommandResult.Error("Name a player.");
			return false;
		}

		IServerPlayerData byName = server.PlayerDataManager.GetPlayerDataByLastKnownName(playerName);
		if (byName == null || !server.PlayerDataManager.PlayerDataByUid.TryGetValue(byName.PlayerUID, out playerData))
		{
			failure = TextCommandResult.Error("No player data for '" + playerName + "'. They must have joined at least once.");
			return false;
		}

		return true;
	}

	private List<string> FindMembersWithOtherGroupOfKind(PlayerGroup group, StratumGroupKindConfig kind)
	{
		List<string> conflicted = new List<string>();
		foreach (ServerPlayerData data in server.PlayerDataManager.PlayerDataByUid.Values)
		{
			if (data.PlayerGroupMemberShips == null
				|| !data.PlayerGroupMemberShips.TryGetValue(group.Uid, out PlayerGroupMembership membership)
				|| membership == null
				|| membership.Level == EnumPlayerGroupMemberShip.None)
			{
				continue;
			}

			foreach (KeyValuePair<int, PlayerGroupMembership> other in data.PlayerGroupMemberShips)
			{
				if (other.Key == group.Uid || other.Value == null || other.Value.Level == EnumPlayerGroupMemberShip.None)
				{
					continue;
				}

				if (server.PlayerDataManager.PlayerGroupsById.TryGetValue(other.Key, out PlayerGroup otherGroup)
					&& string.Equals(otherGroup.StratumKind, kind.Code, StringComparison.OrdinalIgnoreCase))
				{
					conflicted.Add(data.LastKnownPlayername);
					break;
				}
			}
		}

		return conflicted;
	}

	/// <summary>Pushes a membership change to the affected player if they are online.</summary>
	private void RefreshMembership(ServerPlayerData playerData, PlayerGroup group, PlayerGroupMembership membership)
	{
		if (!server.PlayersByUid.TryGetValue(playerData.PlayerUID, out ServerPlayer player))
		{
			return;
		}

		ServerySystemPlayerGroups groups = server.Systems.OfType<ServerySystemPlayerGroups>().FirstOrDefault();
		if (groups == null)
		{
			return;
		}

		// Reconciled rather than appended: re-adding someone already in the group (to change
		// their level, say) must not list them twice in the online roster.
		if (membership != null && !group.OnlinePlayers.Any(online => online.PlayerUID == player.PlayerUID))
		{
			group.OnlinePlayers.Add(player);
		}

		// The full list, not just this group, so a group the player was moved out of also
		// disappears from their client. It refreshes the group tag on their nametag as well.
		groups.SendPlayerGroups(player);
	}

	/// <summary>Re-applies nametags for every online member after a kind or tag change.</summary>
	private void RefreshTagsFor(PlayerGroup group)
	{
		foreach (IPlayer player in group.OnlinePlayers.ToList())
		{
			if (player is IServerPlayer serverPlayer)
			{
				StratumNametags.RefreshGroupTagFor(serverPlayer);
			}
		}
	}

	private bool CheckAccess(TextCommandCallingArgs args, out TextCommandResult failure)
	{
		failure = null;
		StratumCommandsConfig commands = StratumRuntime.Config.Commands;
		if (!commands.Enabled)
		{
			failure = TextCommandResult.Error("Stratum commands are disabled.");
			return false;
		}

		StratumCommandAccessConfig access = commands.GroupAdmin;
		if (access == null || !access.Enabled)
		{
			failure = TextCommandResult.Error("/group admin is disabled.");
			return false;
		}

		if (!StratumCommandAccessCatalog.CallerHasAccess(args.Caller, server, access))
		{
			failure = TextCommandResult.Error("You do not have permission to use /group admin.");
			return false;
		}

		if (!StratumCommandCooldowns.TryUse(args.Caller, server, "group admin", access, out TimeSpan remaining))
		{
			failure = TextCommandResult.Error("Wait " + Math.Ceiling(remaining.TotalSeconds) + "s before using /group admin again.");
			return false;
		}

		return true;
	}

	/// <summary>
	/// Lock durations use the same spelling as /mute, plus seconds: permanent when omitted,
	/// otherwise a positive value with a unit, like 30s, 90m, 4h, 3d, 2w.
	/// </summary>
	private static bool TryParseLockDuration(string durationText, out DateTime? expiresUtc, out string error)
	{
		expiresUtc = null;
		error = null;
		if (string.IsNullOrWhiteSpace(durationText)
			|| string.Equals(durationText, "perm", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(durationText, "permanent", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(durationText, "forever", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (durationText.Length < 2
			|| !double.TryParse(durationText.Substring(0, durationText.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double amount)
			|| amount <= 0)
		{
			error = "Duration must be permanent, or a positive value like 30s, 90m, 4h, 3d.";
			return false;
		}

		switch (char.ToLowerInvariant(durationText[durationText.Length - 1]))
		{
			case 's':
				expiresUtc = DateTime.UtcNow.AddSeconds(amount);
				return true;
			case 'm':
				expiresUtc = DateTime.UtcNow.AddMinutes(amount);
				return true;
			case 'h':
				expiresUtc = DateTime.UtcNow.AddHours(amount);
				return true;
			case 'd':
				expiresUtc = DateTime.UtcNow.AddDays(amount);
				return true;
			case 'w':
				expiresUtc = DateTime.UtcNow.AddDays(amount * 7);
				return true;
			default:
				error = "Duration unit must be s, m, h, d, or w.";
				return false;
		}
	}
}
