using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.Common;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

internal sealed class StratumCommandAccessEntry
{
	public StratumCommandAccessEntry(string commandKey, string displayCommand, StratumCommandAccessConfig access, string description)
	{
		CommandKey = commandKey;
		DisplayCommand = displayCommand;
		Access = access;
		Description = description;
	}

	public string CommandKey { get; }

	public string DisplayCommand { get; }

	public StratumCommandAccessConfig Access { get; }

	public string Description { get; }
}

internal static class StratumCommandAccessCatalog
{
	public static IEnumerable<StratumCommandAccessEntry> Enumerate(StratumCommandsConfig commands)
	{
		yield return new StratumCommandAccessEntry("spawn", "/spawn", commands.Spawn, "Use /spawn");
		yield return new StratumCommandAccessEntry("setspawn", "/setspawn", commands.SetSpawn, "Use /setspawn");
		yield return new StratumCommandAccessEntry("tpa", "/tpa", commands.TeleportRequests.Request, "Use /tpa, /tpaccept, /tpdeny, and /tpacancel");
		yield return new StratumCommandAccessEntry("tpahere", "/tpahere", commands.TeleportRequests.Here, "Use /tpahere");
		yield return new StratumCommandAccessEntry("home", "/home", commands.Homes.Home, "Use /home and /homes");
		yield return new StratumCommandAccessEntry("sethome", "/sethome", commands.Homes.SetHome, "Use /sethome");
		yield return new StratumCommandAccessEntry("delhome", "/delhome", commands.Homes.DeleteHome, "Use /delhome");
		yield return new StratumCommandAccessEntry("seen", "/seen", commands.Seen, "Use /seen");
		yield return new StratumCommandAccessEntry("whois", "/whois", commands.Whois, "Use /whois");
		yield return new StratumCommandAccessEntry("near", "/near", commands.Near, "Use /near");
		yield return new StratumCommandAccessEntry("back", "/back", commands.Back, "Use /back");
		yield return new StratumCommandAccessEntry("msg", "/msg", commands.Message, "Use /msg and /reply");
		yield return new StratumCommandAccessEntry("reply", "/reply", commands.Message, "Use /reply");
		yield return new StratumCommandAccessEntry("staffchat", "/staffchat", commands.StaffChat, "Use /staffchat");
		yield return new StratumCommandAccessEntry("chatcontrol", "/slowmode", commands.ChatControl, "Use /slowmode, /lockchat, and /chatclear");
		yield return new StratumCommandAccessEntry("staffbroadcast", "/staffbroadcast", commands.StaffBroadcast, "Use /staffbroadcast");
		yield return new StratumCommandAccessEntry("info", "/rules", commands.InfoCommands, "Use /rules, /discord, /website, and /motd");
		yield return new StratumCommandAccessEntry("vanish", "/vanish", commands.Vanish, "Use /vanish");
		yield return new StratumCommandAccessEntry("freeze", "/freeze", commands.Freeze, "Use /freeze");
		yield return new StratumCommandAccessEntry("jail", "/jail", commands.Jail, "Use /setjail, /jail, /unjail, and /jailstatus");
		yield return new StratumCommandAccessEntry("warn", "/warn", commands.Warn, "Use /warn, /warnings, and /delwarn");
		yield return new StratumCommandAccessEntry("mute", "/mute", commands.Mute, "Use /mute, /unmute, and /mutestatus");
		yield return new StratumCommandAccessEntry("notes", "/note", commands.Notes, "Use /note and /notes");
		yield return new StratumCommandAccessEntry("report", "/report", commands.Report, "Use /report");
		yield return new StratumCommandAccessEntry("reports", "/reports", commands.ReportManage, "Use /reports staff queue commands");
	}

	public static StratumCommandAccessEntry Find(StratumCommandsConfig commands, string command)
	{
		if (string.IsNullOrWhiteSpace(command))
		{
			return null;
		}

		string normalized = command.Trim().TrimStart('/');
		return Enumerate(commands).FirstOrDefault(entry =>
			string.Equals(entry.CommandKey, normalized, StringComparison.OrdinalIgnoreCase) ||
			string.Equals(entry.DisplayCommand.TrimStart('/'), normalized, StringComparison.OrdinalIgnoreCase));
	}

	public static bool CallerHasAccess(Caller caller, ServerMain server, StratumCommandAccessConfig access)
	{
		if (caller == null || access == null || !access.Enabled)
		{
			return false;
		}

		if (caller.Type == EnumCallerType.Console)
		{
			return true;
		}

		if (string.IsNullOrWhiteSpace(access.Privilege))
		{
			return true;
		}

		return caller.HasPrivilege(access.Privilege);
	}

	public static bool PlayerHasAccess(IServerPlayer player, StratumCommandAccessConfig access)
	{
		if (player == null || access == null || !access.Enabled)
		{
			return false;
		}

		if (string.IsNullOrWhiteSpace(access.Privilege))
		{
			return true;
		}

		return player.HasPrivilege(access.Privilege);
	}

	public static bool RoleCanUse(ServerMain server, string roleCode, StratumCommandAccessConfig access)
	{
		if (string.IsNullOrWhiteSpace(roleCode) || access == null || !access.Enabled)
		{
			return false;
		}

		if (string.IsNullOrWhiteSpace(access.Privilege))
		{
			return true;
		}

		if (server.Config.RolesByCode.TryGetValue(roleCode, out PlayerRole role))
		{
			return role.Privileges?.Any(privilege => string.Equals(privilege, access.Privilege, StringComparison.OrdinalIgnoreCase)) == true;
		}

		return false;
	}
}