using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.CommandAbbr;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.Common;

namespace Vintagestory.Server;

internal class CmdStratumRoles
{
	private readonly ServerMain server;

	public CmdStratumRoles(ServerMain server)
	{
		this.server = server;
		StratumRuntime.Config.EnsurePopulated();
		if (!StratumRuntime.Config.Commands.Enabled)
		{
			return;
		}

		StratumCommandAccessConfig access = StratumRuntime.Config.Commands.RoleEditing;
		if (!StratumCommandRegistration.ShouldRegister(access, "/roles", "Commands.RoleEditing"))
		{
			return;
		}

		CommandArgumentParsers parsers = server.api.commandapi.Parsers;
		server.api.commandapi.Create("roles")
			.WithDescription("Edit server roles while the server is running")
			.WithArgs(
				parsers.OptionalWordRange("action", "list", "info", "grant", "revoke", "applyall"),
				parsers.OptionalWord("role"),
				parsers.OptionalWord("privilege or confirm"))
			.RequiresPrivilege(Privilege.controlserver)
			.HandleWith(HandleRoles);
	}

	private TextCommandResult HandleRoles(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, out TextCommandResult failure))
		{
			return failure;
		}

		string action = args.Parsers[0].IsMissing ? "list" : args[0] as string;
		if (string.Equals(action, "list", StringComparison.OrdinalIgnoreCase))
		{
			return ListRoles();
		}

		string roleCode = args.Parsers[1].IsMissing ? null : args[1] as string;
		if (string.Equals(action, "info", StringComparison.OrdinalIgnoreCase))
		{
			return DescribeRole(roleCode);
		}

		if (string.Equals(action, "grant", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(action, "revoke", StringComparison.OrdinalIgnoreCase))
		{
			string privilege = args.Parsers[2].IsMissing ? null : args[2] as string;
			return ChangePrivilege(roleCode, privilege, string.Equals(action, "grant", StringComparison.OrdinalIgnoreCase), args.Caller);
		}

		if (string.Equals(action, "applyall", StringComparison.OrdinalIgnoreCase))
		{
			bool confirmed = !args.Parsers[2].IsMissing
				&& string.Equals(args[2] as string, "confirm", StringComparison.OrdinalIgnoreCase);
			return ApplyRoleToPlayers(roleCode, confirmed, args.Caller);
		}

		return TextCommandResult.Error("Usage: /roles [list|info <role>|grant <role> <privilege>|revoke <role> <privilege>|applyall <role> confirm]");
	}

	private TextCommandResult ListRoles()
	{
		StringBuilder output = new StringBuilder(StratumCommandText.Title("Server roles"));
		output.Append(StratumCommandText.Row("Default", server.Config.DefaultRoleCode));

		foreach (PlayerRole role in server.Config.Roles.OrderByDescending(value => value.PrivilegeLevel).ThenBy(value => value.Code, StringComparer.OrdinalIgnoreCase))
		{
			string privileges = role.Privileges == null || role.Privileges.Count == 0
				? "none"
				: string.Join(", ", role.Privileges.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
			output.Append(StratumCommandText.Bullet(role.Code, role.Name + " level=" + role.PrivilegeLevel + " privileges=" + privileges));
		}

		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult DescribeRole(string roleCode)
	{
		PlayerRole role = StratumRoleRuntime.FindRole(server, roleCode);
		if (role == null)
		{
			return TextCommandResult.Error("No role found for '" + roleCode + "'.");
		}

		string privileges = role.Privileges == null || role.Privileges.Count == 0
			? "none"
			: string.Join(", ", role.Privileges.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
		StringBuilder output = new StringBuilder(StratumCommandText.Title("Role: " + role.Code));
		output.Append(StratumCommandText.Row("Name", role.Name));
		output.Append(StratumCommandText.Row("Privilege level", role.PrivilegeLevel.ToString()));
		output.Append(StratumCommandText.Row("Privileges", privileges));
		output.Append(StratumCommandText.Row("Staff role", StratumRoleRuntime.IsStaffRole(role) ? "yes" : "no"));
		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult ChangePrivilege(string roleCode, string privilege, bool granting, Caller caller)
	{
		PlayerRole role = StratumRoleRuntime.FindRole(server, roleCode);
		if (role == null)
		{
			return TextCommandResult.Error("No role found for '" + roleCode + "'.");
		}

		string existingPrivilege = FindPrivilege(role, privilege);
		if (granting && existingPrivilege != null)
		{
			return TextCommandResult.Error("Role already has privilege '" + existingPrivilege + "'.");
		}

		if (!granting && existingPrivilege == null)
		{
			return TextCommandResult.Error("Role does not have privilege '" + privilege + "'.");
		}

		string requestedPrivilege = granting ? privilege : existingPrivilege;
		if (!StratumRoleRuntime.TryValidatePrivilegeEdit(server, caller, role, requestedPrivilege, granting, out string error))
		{
			return TextCommandResult.Error(error);
		}

		if (granting)
		{
			role.GrantPrivilege(requestedPrivilege);
		}
		else
		{
			role.RevokePrivilege(requestedPrivilege);
		}

		StratumRoleRuntime.PersistRoleChanges(server, role);
		string action = granting ? "granted" : "revoked";
		StratumRuntime.LogAudit("roles " + action + " privilege=" + requestedPrivilege + " role=" + role.Code + " actor=" + caller.GetName(), true);
		return TextCommandResult.Success(StratumCommandText.Confirm("Privilege " + requestedPrivilege + " " + action, "role=" + role.Code));
	}

	private TextCommandResult ApplyRoleToPlayers(string roleCode, bool confirmed, Caller caller)
	{
		PlayerRole role = StratumRoleRuntime.FindRole(server, roleCode);
		if (!StratumRoleRuntime.TryValidateApplyAll(server, caller, role, out string error))
		{
			return TextCommandResult.Error(error);
		}

		List<ServerPlayerData> targets = new List<ServerPlayerData>();
		int staffCount = 0;
		int unchangedCount = 0;
		foreach (ServerPlayerData playerData in server.PlayerDataManager.PlayerDataByUid.Values)
		{
			if (playerData == null)
			{
				continue;
			}

			PlayerRole currentRole = StratumRoleRuntime.FindRole(server, playerData.RoleCode);
			if (StratumRoleRuntime.IsStaffRole(currentRole))
			{
				staffCount++;
				continue;
			}

			if (string.Equals(playerData.RoleCode, role.Code, StringComparison.OrdinalIgnoreCase))
			{
				unchangedCount++;
				continue;
			}

			targets.Add(playerData);
		}

		if (!confirmed)
		{
			return TextCommandResult.Success(StratumCommandText.Warning("No changes made. This would assign " + role.Code + " to " + targets.Count + " players and skip " + staffCount + " staff players. Run /roles applyall " + role.Code + " confirm to continue."));
		}

		int changedCount = 0;
		foreach (ServerPlayerData playerData in targets)
		{
			playerData.SetRole(role);
			changedCount++;
			ConnectedClient client = server.GetClientByUID(playerData.PlayerUID);
			if (client?.Player != null && client.State.IsAdmitted())
			{
				StratumRoleRuntime.RefreshPlayer(server, client.Player);
			}
		}

		if (changedCount > 0)
		{
			server.PlayerDataManager.playerDataDirty = true;
		}

		StratumRuntime.LogAudit("roles applyall role=" + role.Code + " changed=" + changedCount + " skippedStaff=" + staffCount + " unchanged=" + unchangedCount + " actor=" + caller.GetName(), true);
		return TextCommandResult.Success(StratumCommandText.Confirm("Role applied", changedCount + " players changed; " + staffCount + " staff skipped."));
	}

	private bool CheckAccess(TextCommandCallingArgs args, out TextCommandResult failure)
	{
		failure = null;
		StratumCommandAccessConfig access = StratumRuntime.Config.Commands.RoleEditing;
		if (access == null || !access.Enabled)
		{
			failure = TextCommandResult.Error("/roles is disabled.");
			return false;
		}

		if (!StratumCommandAccessCatalog.CallerHasAccess(args.Caller, server, access))
		{
			failure = TextCommandResult.Error("You do not have permission to use /roles.");
			return false;
		}

		if (!StratumCommandCooldowns.TryUse(args.Caller, server, "roles", access, out TimeSpan remaining))
		{
			failure = TextCommandResult.Error("Wait " + Math.Ceiling(remaining.TotalSeconds) + "s before using /roles again.");
			return false;
		}

		return true;
	}

	private static string FindPrivilege(PlayerRole role, string privilege)
	{
		if (role?.Privileges == null || string.IsNullOrWhiteSpace(privilege))
		{
			return null;
		}

		return role.Privileges.FirstOrDefault(value => string.Equals(value, privilege, StringComparison.OrdinalIgnoreCase));
	}
}
