using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.Common;

namespace Vintagestory.Server;

internal static class StratumRoleRuntime
{
	public static PlayerRole FindRole(ServerMain server, string roleCode)
	{
		if (server?.Config?.RolesByCode == null || string.IsNullOrWhiteSpace(roleCode))
		{
			return null;
		}

		foreach (KeyValuePair<string, PlayerRole> entry in server.Config.RolesByCode)
		{
			if (string.Equals(entry.Key, roleCode, StringComparison.OrdinalIgnoreCase))
			{
				return entry.Value;
			}
		}

		return null;
	}

	public static bool TryValidatePrivilegeEdit(ServerMain server, Caller caller, IPlayerRole targetRole, string privilege, bool granting, out string error)
	{
		error = null;
		if (targetRole == null || string.IsNullOrWhiteSpace(targetRole.Code))
		{
			error = "The target role does not exist.";
			return false;
		}

		if (string.IsNullOrWhiteSpace(privilege))
		{
			error = "A privilege is required.";
			return false;
		}

		if (IsRootCaller(server, caller))
		{
			return true;
		}

		PlayerRole callerRole = FindCallerRole(server, caller);
		if (callerRole == null || targetRole.PrivilegeLevel >= callerRole.PrivilegeLevel)
		{
			error = "You can only edit roles below your own level.";
			return false;
		}

		if (granting && !caller.HasPrivilege(privilege))
		{
			error = "You cannot grant a privilege you do not hold.";
			return false;
		}

		return true;
	}

	public static bool TryValidateApplyAll(ServerMain server, Caller caller, PlayerRole targetRole, out string error)
	{
		error = null;
		if (targetRole == null)
		{
			error = "The target role does not exist.";
			return false;
		}

		if (IsStaffRole(targetRole))
		{
			error = "applyall cannot assign a staff role. Use /player <player> role for individual staff changes.";
			return false;
		}

		if (IsRootCaller(server, caller))
		{
			return true;
		}

		PlayerRole callerRole = FindCallerRole(server, caller);
		if (callerRole == null || targetRole.PrivilegeLevel >= callerRole.PrivilegeLevel)
		{
			error = "You can only assign roles below your own level.";
			return false;
		}

		return true;
	}

	public static bool IsStaffRole(IPlayerRole role)
	{
		if (role == null)
		{
			return false;
		}

		return string.Equals(role.Code, "sumod", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(role.Code, "crmod", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(role.Code, "admin", StringComparison.OrdinalIgnoreCase)
			|| role.Privileges?.Contains(Privilege.controlserver) == true
			|| role.Privileges?.Contains(Privilege.grantrevoke) == true;
	}

	public static void PersistRoleChanges(ServerMain server, IPlayerRole changedRole)
	{
		server.Config.SetRoles(server.Config.Roles, server.Config.DefaultRoleCode);
		server.ConfigNeedsSaving = true;
		ServerSystemLoadConfig.SaveRolesConfig(server);

		foreach (ConnectedClient client in server.Clients.Values)
		{
			if (client?.Player == null || !client.State.IsAdmitted())
			{
				continue;
			}

			if (changedRole != null && !string.Equals(client.ServerData?.RoleCode, changedRole.Code, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			RefreshPlayer(server, client.Player);
		}
	}

	public static void RefreshPlayer(ServerMain server, IServerPlayer player)
	{
		server.SendOwnPlayerData(player, sendInventory: false, sendPrivileges: true);
		server.SendRoles(player);
		if (StratumNametags.RefreshFor(player))
		{
			server.BroadcastPlayerData(player, sendInventory: false, sendPrivileges: false);
		}
	}

	private static bool IsRootCaller(ServerMain server, Caller caller)
	{
		return caller?.Type == EnumCallerType.Console || caller?.HasPrivilege(Privilege.root) == true;
	}

	private static PlayerRole FindCallerRole(ServerMain server, Caller caller)
	{
		if (caller == null)
		{
			return null;
		}

		string roleCode = caller.CallerRole;
		if (caller.Player != null)
		{
			ServerPlayerData playerData = server.PlayerDataManager.GetServerPlayerData(caller.Player.PlayerUID);
			roleCode = playerData?.RoleCode ?? roleCode;
		}

		return FindRole(server, roleCode);
	}
}
