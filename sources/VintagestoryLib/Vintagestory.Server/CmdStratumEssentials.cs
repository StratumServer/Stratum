using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Common.CommandAbbr;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace Vintagestory.Server;

internal class CmdStratumEssentials
{
	private const string HomeDataKey = "stratum:homes";
	private readonly ServerMain server;
	private readonly Dictionary<string, TeleportRequest> pendingTeleportRequests = new Dictionary<string, TeleportRequest>(StringComparer.Ordinal);
	private readonly Dictionary<string, long> lastTeleportRequestMs = new Dictionary<string, long>(StringComparer.Ordinal);

	public CmdStratumEssentials(ServerMain server)
	{
		this.server = server;
		StratumRuntime.Config.EnsurePopulated();
		StratumTeleportWarmups.EnsureRegistered(server);
		RegisterConfiguredPrivileges(server);

		if (!StratumRuntime.Config.Commands.Enabled)
		{
			StratumRuntime.LogInfo("player QoL commands disabled in config");
			return;
		}

		CommandArgumentParsers parsers = server.api.commandapi.Parsers;
		StratumCommandsConfig commands = StratumRuntime.Config.Commands;
		if (StratumCommandRegistration.ShouldRegister(commands.Spawn, "/spawn", "Commands.Spawn"))
		{
			server.api.commandapi.Create("spawn")
				.WithDescription("Teleport to server spawn")
				.RequiresPrivilege(Privilege.chat)
				.HandleWith(HandleSpawn);
		}

		if (StratumCommandRegistration.ShouldRegister(commands.SetSpawn, "/setspawn", "Commands.SetSpawn"))
		{
			server.api.commandapi.Create("setspawn")
				.WithDescription("Set the server spawn to your position")
				.RequiresPrivilege(Privilege.chat)
				.HandleWith(HandleSetSpawn);
		}

		if (StratumCommandRegistration.ShouldRegister(commands.TeleportRequests.Request, "/tpa commands", "Commands.TeleportRequests.Request"))
		{
			server.api.commandapi.Create("tpa")
				.WithDescription("Request teleport to another player, or accept/decline a request")
				.WithArgs(parsers.OptionalWord("player|accept|decline|cancel"))
				.RequiresPrivilege(Privilege.chat)
				.HandleWith(HandleTpa);

			server.api.commandapi.Create("tpaccept")
				.WithDescription("Accept a pending teleport request")
				.RequiresPrivilege(Privilege.chat)
				.HandleWith(HandleTpAccept);

			server.api.commandapi.Create("tpdeny")
				.WithDescription("Decline a pending teleport request")
				.RequiresPrivilege(Privilege.chat)
				.HandleWith(HandleTpDecline);

			server.api.commandapi.Create("tpdecline")
				.WithDescription("Decline a pending teleport request")
				.RequiresPrivilege(Privilege.chat)
				.HandleWith(HandleTpDecline);

			server.api.commandapi.Create("tpacancel")
				.WithDescription("Cancel your outgoing teleport request")
				.RequiresPrivilege(Privilege.chat)
				.HandleWith(HandleTpCancel);
		}

		if (StratumCommandRegistration.ShouldRegister(commands.TeleportRequests.Here, "/tpahere", "Commands.TeleportRequests.Here")
			&& StratumCommandRegistration.ShouldRegister(commands.TeleportRequests.AllowTpaHere, "/tpahere", "Commands.TeleportRequests.AllowTpaHere"))
		{
			server.api.commandapi.Create("tpahere")
				.WithDescription("Request another player to teleport to you")
				.WithArgs(parsers.OptionalWord("player"))
				.RequiresPrivilege(Privilege.chat)
				.HandleWith(HandleTpaHere);
		}

		if (StratumCommandRegistration.ShouldRegister(commands.Homes.Home, "/home commands", "Commands.Homes.Home"))
		{
			server.api.commandapi.Create("home")
				.WithDescription("Teleport to one of your homes")
				.WithArgs(parsers.OptionalWord("name"))
				.RequiresPrivilege(Privilege.chat)
				.HandleWith(HandleHome);

			server.api.commandapi.Create("homes")
				.WithDescription("List your homes")
				.RequiresPrivilege(Privilege.chat)
				.HandleWith(HandleHomes);
		}

		if (StratumCommandRegistration.ShouldRegister(commands.Homes.SetHome, "/sethome", "Commands.Homes.SetHome"))
		{
			server.api.commandapi.Create("sethome")
				.WithDescription("Set a home at your current position")
				.WithArgs(parsers.OptionalWord("name"))
				.RequiresPrivilege(Privilege.chat)
				.HandleWith(HandleSetHome);
		}

		if (StratumCommandRegistration.ShouldRegister(commands.Homes.DeleteHome, "/delhome", "Commands.Homes.DeleteHome"))
		{
			server.api.commandapi.Create("delhome")
				.WithDescription("Delete one of your homes")
				.WithArgs(parsers.OptionalWord("name"))
				.RequiresPrivilege(Privilege.chat)
				.HandleWith(HandleDeleteHome);
		}
	}

	public static void RegisterConfiguredPrivileges(ServerMain server)
	{
		StratumRuntime.Config.EnsurePopulated();
		foreach (StratumCommandAccessEntry entry in StratumCommandAccessCatalog.Enumerate(StratumRuntime.Config.Commands))
		{
			RegisterAccessPrivilege(server, entry.Access, entry.Description);
		}
	}

	private static void RegisterAccessPrivilege(ServerMain server, StratumCommandAccessConfig access, string description)
	{
		string privilege = access?.Privilege;
		if (string.IsNullOrWhiteSpace(privilege) || IsBuiltInPrivilege(privilege) || server.AllPrivileges.Contains(privilege) || server.PrivilegeDescriptions.ContainsKey(privilege))
		{
			return;
		}

		server.api.Permissions.RegisterPrivilege(privilege, description);
	}

	private static bool IsBuiltInPrivilege(string privilege)
	{
		return Privilege.AllCodes().Any(code => string.Equals(code, privilege, StringComparison.Ordinal));
	}

	private TextCommandResult HandleSpawn(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Spawn, "spawn", out TextCommandResult failure))
		{
			return failure;
		}

		IServerPlayer player = GetPlayer(args);
		if (player == null)
		{
			return TextCommandResult.Error("Only players can use /spawn.");
		}

		FuzzyEntityPos spawn = server.GetSpawnPosition(player.PlayerUID, onlyGlobalDefaultSpawn: true, consumeSpawn: false);
		if (spawn == null)
		{
			return TextCommandResult.Error("Spawn is not available yet.");
		}

		return StratumTeleportWarmups.StartOrTeleport(server, player, spawn, "spawn", "spawn");
	}

	private TextCommandResult HandleSetSpawn(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.SetSpawn, "setspawn", out TextCommandResult failure))
		{
			return failure;
		}

		IServerPlayer player = GetPlayer(args);
		if (player == null)
		{
			return TextCommandResult.Error("Only players can use /setspawn.");
		}

		BlockPos pos = player.Entity.Pos.AsBlockPos;
		if (!server.WorldMap.IsValidPos(pos.X, pos.Y, pos.Z))
		{
			return TextCommandResult.Error("Invalid spawn position.");
		}

		server.SaveGameData.DefaultSpawn = new PlayerSpawnPos
		{
			x = pos.X,
			y = pos.Y,
			z = pos.Z,
			yaw = player.Entity.Pos.Yaw,
			pitch = player.Entity.Pos.Pitch,
			roll = player.Entity.Pos.Roll
		};

		StratumRuntime.LogInfo($"spawn set by {player.PlayerName} at {pos.X}, {pos.Y}, {pos.Z}");
		return TextCommandResult.Success(StratumCommandText.Confirm("Spawn set", "to " + pos.X + ", " + pos.Y + ", " + pos.Z + "."));
	}

	private TextCommandResult HandleTpa(TextCommandCallingArgs args)
	{
		string action = args[0] as string;
		if (string.IsNullOrWhiteSpace(action))
		{
			return TextCommandResult.Error("Usage: /tpa &lt;player&gt;|accept|decline|cancel");
		}

		if (string.Equals(action, "accept", StringComparison.OrdinalIgnoreCase))
		{
			return AcceptTeleportRequest(args);
		}

		if (string.Equals(action, "decline", StringComparison.OrdinalIgnoreCase) || string.Equals(action, "deny", StringComparison.OrdinalIgnoreCase))
		{
			return DeclineTeleportRequest(args);
		}

		if (string.Equals(action, "cancel", StringComparison.OrdinalIgnoreCase))
		{
			return CancelTeleportRequest(args);
		}

		return CreateTeleportRequest(args, action, requestTargetToTeleportHere: false);
	}

	private TextCommandResult HandleTpaHere(TextCommandCallingArgs args)
	{
		if (!StratumRuntime.Config.Commands.TeleportRequests.AllowTpaHere)
		{
			return TextCommandResult.Error("/tpahere is disabled.");
		}

		string targetName = args[0] as string;
		if (string.IsNullOrWhiteSpace(targetName))
		{
			return TextCommandResult.Error("Usage: /tpahere &lt;player&gt;");
		}

		return CreateTeleportRequest(args, targetName, requestTargetToTeleportHere: true);
	}

	private TextCommandResult HandleTpAccept(TextCommandCallingArgs args)
	{
		return AcceptTeleportRequest(args);
	}

	private TextCommandResult HandleTpDecline(TextCommandCallingArgs args)
	{
		return DeclineTeleportRequest(args);
	}

	private TextCommandResult HandleTpCancel(TextCommandCallingArgs args)
	{
		return CancelTeleportRequest(args);
	}

	private TextCommandResult CreateTeleportRequest(TextCommandCallingArgs args, string targetName, bool requestTargetToTeleportHere)
	{
		StratumTeleportRequestsConfig config = StratumRuntime.Config.Commands.TeleportRequests;
		StratumCommandAccessConfig access = requestTargetToTeleportHere ? config.Here : config.Request;
		string command = requestTargetToTeleportHere ? "tpahere" : "tpa";
		if (!CheckAccess(args, access, command, out TextCommandResult failure))
		{
			return failure;
		}

		IServerPlayer requester = GetPlayer(args);
		if (requester == null)
		{
			return TextCommandResult.Error("Only players can send teleport requests.");
		}

		ConnectedClient target = FindOnlineClientByName(targetName);
		if (target?.Player == null)
		{
			return TextCommandResult.Error("No such online player: " + targetName);
		}

		if (target.Player.PlayerUID == requester.PlayerUID)
		{
			return TextCommandResult.Error("You cannot send a teleport request to yourself.");
		}

		PruneExpiredTeleportRequests();
		long now = server.ElapsedMilliseconds;
		if (lastTeleportRequestMs.TryGetValue(requester.PlayerUID, out long lastRequest) && now - lastRequest < config.CooldownSeconds * 1000L)
		{
			long waitSeconds = Math.Max(1, (config.CooldownSeconds * 1000L - (now - lastRequest) + 999L) / 1000L);
			return TextCommandResult.Error("Wait " + waitSeconds + "s before sending another teleport request.");
		}

		pendingTeleportRequests[target.Player.PlayerUID] = new TeleportRequest
		{
			RequesterUid = requester.PlayerUID,
			RequesterName = requester.PlayerName,
			TargetUid = target.Player.PlayerUID,
			TargetName = target.Player.PlayerName,
			CreatedMs = now,
			RequestTargetToTeleportHere = requestTargetToTeleportHere
		};
		lastTeleportRequestMs[requester.PlayerUID] = now;

		string targetMessage = requestTargetToTeleportHere
			? StratumCommandText.Warning("Teleport request") + " from " + StratumCommandText.Escape(requester.PlayerName) + ": teleport to them. Use /tpa accept or /tpa decline within " + config.TimeoutSeconds + "s."
			: StratumCommandText.Warning("Teleport request") + " from " + StratumCommandText.Escape(requester.PlayerName) + ": they want to teleport to you. Use /tpa accept or /tpa decline within " + config.TimeoutSeconds + "s.";
		Send(target.Player, targetMessage, EnumChatType.Notification);

		return TextCommandResult.Success(StratumCommandText.Confirm("Teleport request sent", "to " + target.Player.PlayerName + "."));
	}

	private TextCommandResult AcceptTeleportRequest(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.TeleportRequests.Request, "tpa", out TextCommandResult failure))
		{
			return failure;
		}

		IServerPlayer targetPlayer = GetPlayer(args);
		if (targetPlayer == null)
		{
			return TextCommandResult.Error("Only players can accept teleport requests.");
		}

		PruneExpiredTeleportRequests();
		if (!pendingTeleportRequests.TryGetValue(targetPlayer.PlayerUID, out TeleportRequest request))
		{
			return TextCommandResult.Error("You do not have a pending teleport request.");
		}

		ConnectedClient requesterClient = FindOnlineClientByUid(request.RequesterUid);
		ConnectedClient targetClient = FindOnlineClientByUid(request.TargetUid);
		pendingTeleportRequests.Remove(targetPlayer.PlayerUID);

		if (requesterClient?.Player == null || targetClient?.Player == null)
		{
			return TextCommandResult.Error("That teleport request is no longer valid.");
		}

		if (request.RequestTargetToTeleportHere)
		{
			Send(requesterClient.Player, StratumCommandText.Confirm("Teleport-here accepted", "by " + targetClient.Player.PlayerName + "."), EnumChatType.Notification);
			return StratumTeleportWarmups.StartOrTeleport(server, targetClient.Player, requesterClient.Player.Entity.Pos.Copy(), "tpahere", requesterClient.Player.PlayerName);
		}

		Send(requesterClient.Player, StratumCommandText.Confirm("Teleport request accepted", "by " + targetClient.Player.PlayerName + "."), EnumChatType.Notification);
		return StratumTeleportWarmups.StartOrTeleport(server, requesterClient.Player, targetClient.Player.Entity.Pos.Copy(), "tpa", targetClient.Player.PlayerName);
	}

	private TextCommandResult DeclineTeleportRequest(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.TeleportRequests.Request, "tpa", out TextCommandResult failure))
		{
			return failure;
		}

		IServerPlayer player = GetPlayer(args);
		if (player == null)
		{
			return TextCommandResult.Error("Only players can decline teleport requests.");
		}

		PruneExpiredTeleportRequests();
		if (!pendingTeleportRequests.TryGetValue(player.PlayerUID, out TeleportRequest request))
		{
			return TextCommandResult.Error("You do not have a pending teleport request.");
		}

		pendingTeleportRequests.Remove(player.PlayerUID);
		ConnectedClient requesterClient = FindOnlineClientByUid(request.RequesterUid);
		if (requesterClient?.Player != null)
		{
			Send(requesterClient.Player, StratumCommandText.Warning("Teleport request declined") + " by " + StratumCommandText.Escape(player.PlayerName) + ".", EnumChatType.CommandError);
		}

		return TextCommandResult.Success(StratumCommandText.Confirm("Teleport request declined"));
	}

	private TextCommandResult CancelTeleportRequest(TextCommandCallingArgs args)
	{
		IServerPlayer requester = GetPlayer(args);
		if (requester == null)
		{
			return TextCommandResult.Error("Only players can cancel teleport requests.");
		}

		string[] keys = pendingTeleportRequests
			.Where(entry => entry.Value.RequesterUid == requester.PlayerUID)
			.Select(entry => entry.Key)
			.ToArray();

		if (keys.Length == 0)
		{
			return TextCommandResult.Error("You do not have an outgoing teleport request.");
		}

		foreach (string key in keys)
		{
			TeleportRequest request = pendingTeleportRequests[key];
			pendingTeleportRequests.Remove(key);
			ConnectedClient targetClient = FindOnlineClientByUid(request.TargetUid);
			if (targetClient?.Player != null)
			{
				Send(targetClient.Player, StratumCommandText.Warning("Teleport request cancelled") + " by " + StratumCommandText.Escape(requester.PlayerName) + ".", EnumChatType.CommandError);
			}
		}

		return TextCommandResult.Success(StratumCommandText.Confirm("Teleport request cancelled"));
	}

	private TextCommandResult HandleHome(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Homes.Home, "home", out TextCommandResult failure))
		{
			return failure;
		}

		IServerPlayer player = GetPlayer(args);
		if (player == null)
		{
			return TextCommandResult.Error("Only players can use homes.");
		}

		StratumHomeData data = LoadHomes(player);
		if (data.Homes.Count == 0)
		{
			return TextCommandResult.Error("You do not have any homes set.");
		}

		string requestedName = NormalizeHomeName(args[0] as string);
		if (requestedName == null)
		{
			requestedName = data.Homes.ContainsKey("home") ? "home" : data.Homes.Count == 1 ? data.Homes.Keys.First() : null;
		}

		if (requestedName == null)
		{
			return TextCommandResult.Error(StratumCommandText.Warning("Choose a home") + ": " + StratumCommandText.Escape(string.Join(", ", data.Homes.Keys.OrderBy(name => name))));
		}

		if (!data.Homes.TryGetValue(requestedName, out StratumHomePosition home))
		{
			return TextCommandResult.Error("No such home: " + requestedName);
		}

		if (!server.WorldMap.IsValidPos((int)home.X, (int)home.Y, (int)home.Z))
		{
			return TextCommandResult.Error("Home " + requestedName + " is outside the current world bounds.");
		}

		return StratumTeleportWarmups.StartOrTeleport(server, player, home.ToEntityPos(), "home", "home " + requestedName);
	}

	private TextCommandResult HandleSetHome(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Homes.SetHome, "sethome", out TextCommandResult failure))
		{
			return failure;
		}

		IServerPlayer player = GetPlayer(args);
		if (player == null)
		{
			return TextCommandResult.Error("Only players can set homes.");
		}

		string homeName = NormalizeHomeName(args[0] as string) ?? "home";
		if (!IsValidHomeName(homeName))
		{
			return TextCommandResult.Error("Home names may only use letters, numbers, dash, and underscore.");
		}

		StratumHomeData data = LoadHomes(player);
		int maxHomes = GetMaxHomes(player);
		bool replacing = data.Homes.ContainsKey(homeName);
		if (!replacing && data.Homes.Count >= maxHomes)
		{
			return TextCommandResult.Error("You can set up to " + maxHomes + " homes for your role.");
		}

		data.Homes[homeName] = StratumHomePosition.FromEntityPos(player.Entity.Pos);
		SaveHomes(player, data);
		return TextCommandResult.Success(StratumCommandText.Confirm(replacing ? "Updated home" : "Set home", homeName + " (" + data.Homes.Count + "/" + maxHomes + ")."));
	}

	private TextCommandResult HandleDeleteHome(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Homes.DeleteHome, "delhome", out TextCommandResult failure))
		{
			return failure;
		}

		IServerPlayer player = GetPlayer(args);
		if (player == null)
		{
			return TextCommandResult.Error("Only players can delete homes.");
		}

		StratumHomeData data = LoadHomes(player);
		if (data.Homes.Count == 0)
		{
			return TextCommandResult.Error("You do not have any homes set.");
		}

		string homeName = NormalizeHomeName(args[0] as string);
		if (homeName == null)
		{
			homeName = data.Homes.ContainsKey("home") ? "home" : data.Homes.Count == 1 ? data.Homes.Keys.First() : null;
		}

		if (homeName == null)
		{
			return TextCommandResult.Error(StratumCommandText.Warning("Choose a home to delete") + ": " + StratumCommandText.Escape(string.Join(", ", data.Homes.Keys.OrderBy(name => name))));
		}

		if (!data.Homes.Remove(homeName))
		{
			return TextCommandResult.Error("No such home: " + homeName);
		}

		SaveHomes(player, data);
		return TextCommandResult.Success(StratumCommandText.Confirm("Deleted home", homeName + "."));
	}

	private TextCommandResult HandleHomes(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Homes.Home, "homes", out TextCommandResult failure))
		{
			return failure;
		}

		IServerPlayer player = GetPlayer(args);
		if (player == null)
		{
			return TextCommandResult.Error("Only players can list homes.");
		}

		StratumHomeData data = LoadHomes(player);
		int maxHomes = GetMaxHomes(player);
		if (data.Homes.Count == 0)
		{
			return TextCommandResult.Success(StratumCommandText.Empty("Homes: none (0/" + maxHomes + ")"));
		}

		StringBuilder output = new StringBuilder(StratumCommandText.Title("Homes"));
		output.Append(StratumCommandText.Row("Used", data.Homes.Count.ToString(GlobalConstants.DefaultCultureInfo) + "/" + maxHomes.ToString(GlobalConstants.DefaultCultureInfo)));
		foreach (KeyValuePair<string, StratumHomePosition> entry in data.Homes.OrderBy(entry => entry.Key))
		{
			output.Append(StratumCommandText.Bullet(entry.Key, entry.Value.FormatBlockPos()));
		}

		return TextCommandResult.Success(output.ToString());
	}

	private bool CheckAccess(TextCommandCallingArgs args, StratumCommandAccessConfig access, string command, out TextCommandResult failure)
	{
		StratumRuntime.Config.EnsurePopulated();
		failure = null;

		if (!StratumRuntime.Config.Commands.Enabled)
		{
			failure = TextCommandResult.Error("Stratum player commands are disabled.");
			return false;
		}

		if (access == null || !access.Enabled)
		{
			failure = TextCommandResult.Error("/" + command + " is disabled.");
			return false;
		}

		if (StratumCommandAccessCatalog.CallerHasAccess(args.Caller, server, access))
		{
			if (!StratumCommandCooldowns.TryUse(args.Caller, server, command, access, out TimeSpan remaining))
			{
				failure = TextCommandResult.Error("Wait " + Math.Ceiling(remaining.TotalSeconds).ToString(GlobalConstants.DefaultCultureInfo) + "s before using /" + command + " again.");
				return false;
			}

			return true;
		}

		failure = TextCommandResult.Error("You do not have permission to use /" + command + ".");
		return false;
	}

	private int GetMaxHomes(IServerPlayer player)
	{
		StratumHomesConfig config = StratumRuntime.Config.Commands.Homes;
		string roleCode = player.Role?.Code;
		if (roleCode != null && TryGetRoleLimit(config.MaxHomesByRole, roleCode, out int limit))
		{
			return Math.Max(0, limit);
		}

		return Math.Max(0, config.DefaultMaxHomes);
	}

	private static bool TryGetRoleLimit(Dictionary<string, int> limits, string roleCode, out int limit)
	{
		limit = 0;
		if (limits == null)
		{
			return false;
		}

		if (limits.TryGetValue(roleCode, out limit))
		{
			return true;
		}

		foreach (KeyValuePair<string, int> entry in limits)
		{
			if (string.Equals(entry.Key, roleCode, StringComparison.OrdinalIgnoreCase))
			{
				limit = entry.Value;
				return true;
			}
		}

		return false;
	}

	private StratumHomeData LoadHomes(IServerPlayer player)
	{
		byte[] data = player.GetModdata(HomeDataKey);
		if (data == null)
		{
			return new StratumHomeData();
		}

		try
		{
			StratumHomeData homes = SerializerUtil.Deserialize<StratumHomeData>(data) ?? new StratumHomeData();
			homes.Homes ??= new Dictionary<string, StratumHomePosition>(StringComparer.OrdinalIgnoreCase);
			return homes;
		}
		catch (Exception exception)
		{
			StratumRuntime.LogWarning("failed to load homes for " + player.PlayerName + ": " + exception.Message);
			return new StratumHomeData();
		}
	}

	private static void SaveHomes(IServerPlayer player, StratumHomeData data)
	{
		data.Homes ??= new Dictionary<string, StratumHomePosition>(StringComparer.OrdinalIgnoreCase);
		if (data.Homes.Count == 0)
		{
			player.RemoveModdata(HomeDataKey);
			return;
		}

		player.SetModdata(HomeDataKey, SerializerUtil.Serialize(data));
	}

	private void PruneExpiredTeleportRequests()
	{
		long now = server.ElapsedMilliseconds;
		long timeoutMs = StratumRuntime.Config.Commands.TeleportRequests.TimeoutSeconds * 1000L;
		string[] expired = pendingTeleportRequests
			.Where(entry => now - entry.Value.CreatedMs > timeoutMs)
			.Select(entry => entry.Key)
			.ToArray();

		foreach (string key in expired)
		{
			pendingTeleportRequests.Remove(key);
		}
	}

	private ConnectedClient FindOnlineClientByName(string playerName)
	{
		return server.Clients.Values.FirstOrDefault(client => client.State.IsAdmitted() && string.Equals(client.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));
	}

	private ConnectedClient FindOnlineClientByUid(string playerUid)
	{
		return server.Clients.Values.FirstOrDefault(client => client.State.IsAdmitted() && client.Player?.PlayerUID == playerUid);
	}

	private static IServerPlayer GetPlayer(TextCommandCallingArgs args)
	{
		return args.Caller.Player as IServerPlayer;
	}

	private static void Send(IServerPlayer player, string message, EnumChatType chatType)
	{
		player.SendMessage(GlobalConstants.GeneralChatGroup, message, chatType);
	}

	private static string NormalizeHomeName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return null;
		}

		return name.Trim().ToLowerInvariant();
	}

	private static bool IsValidHomeName(string name)
	{
		return name.Length > 0 && name.Length <= 32 && name.All(character => char.IsLetterOrDigit(character) || character == '-' || character == '_');
	}

	private sealed class TeleportRequest
	{
		public string RequesterUid { get; set; }

		public string RequesterName { get; set; }

		public string TargetUid { get; set; }

		public string TargetName { get; set; }

		public long CreatedMs { get; set; }

		public bool RequestTargetToTeleportHere { get; set; }
	}
}

[ProtoContract]
internal sealed class StratumHomeData
{
	[ProtoMember(1)]
	public Dictionary<string, StratumHomePosition> Homes { get; set; } = new Dictionary<string, StratumHomePosition>(StringComparer.OrdinalIgnoreCase);
}

[ProtoContract]
internal sealed class StratumHomePosition
{
	[ProtoMember(1)]
	public double X { get; set; }

	[ProtoMember(2)]
	public double Y { get; set; }

	[ProtoMember(3)]
	public double Z { get; set; }

	[ProtoMember(4)]
	public int Dimension { get; set; }

	[ProtoMember(5)]
	public float Yaw { get; set; }

	[ProtoMember(6)]
	public float Pitch { get; set; }

	public static StratumHomePosition FromEntityPos(EntityPos pos)
	{
		return new StratumHomePosition
		{
			X = pos.X,
			Y = pos.Y,
			Z = pos.Z,
			Dimension = pos.Dimension,
			Yaw = pos.Yaw,
			Pitch = pos.Pitch
		};
	}

	public EntityPos ToEntityPos()
	{
		return new EntityPos(X, Y, Z)
		{
			Dimension = Dimension,
			Yaw = Yaw,
			Pitch = Pitch
		};
	}

	public string FormatBlockPos()
	{
		return ((int)X).ToString(CultureInfo.InvariantCulture) + ", " + ((int)Y).ToString(CultureInfo.InvariantCulture) + ", " + ((int)Z).ToString(CultureInfo.InvariantCulture);
	}
}