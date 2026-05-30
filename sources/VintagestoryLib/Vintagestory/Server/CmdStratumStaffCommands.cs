using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.CommandAbbr;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

internal class CmdStratumStaffCommands
{
	private readonly ServerMain server;
	private readonly Dictionary<string, string> lastMessagePartnerByUid = new Dictionary<string, string>(StringComparer.Ordinal);

	public CmdStratumStaffCommands(ServerMain server)
	{
		this.server = server;
		server.EventManager.OnPlayerJoin += OnPlayerJoin;
		server.EventManager.OnPlayerDisconnect += OnPlayerDisconnect;
		server.EventManager.OnPlayerDeath += OnPlayerDeath;
		server.EventManager.OnPlayerChat += OnPlayerChat;
		server.RegisterGameTickListener(OnFreezeTick, 250);

		StratumRuntime.Config.EnsurePopulated();
		if (!StratumRuntime.Config.Commands.Enabled)
		{
			return;
		}

		CommandArgumentParsers parsers = server.api.commandapi.Parsers;
		server.api.commandapi.Create("seen")
			.WithDescription("Show when a player was last seen")
			.WithArgs(parsers.Word("player"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleSeen);

		server.api.commandapi.Create("whois")
			.WithDescription("Show staff investigation details for a player")
			.WithArgs(parsers.Word("player"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleWhois);

		server.api.commandapi.Create("near")
			.WithDescription("List nearby players")
			.WithArgs(parsers.OptionalInt("radius", StratumRuntime.Config.Commands.NearDefaultRadiusBlocks))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleNear);

		server.api.commandapi.Create("back")
			.WithDescription("Teleport back to your previous Stratum teleport or death location")
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleBack);

		RegisterMessageCommand("msg", parsers);
		RegisterMessageCommand("tell", parsers);
		RegisterMessageCommand("w", parsers);

		server.api.commandapi.Create("reply")
			.WithAlias("r")
			.WithDescription("Reply to your last private message")
			.WithArgs(parsers.All("message"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleReply);

		server.api.commandapi.Create("staffchat")
			.WithDescription("Send a message to online staff")
			.WithArgs(parsers.All("message"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleStaffChat);

		server.api.commandapi.Create("slowmode")
			.WithDescription("Set, clear, or inspect global chat slowmode")
			.WithArgs(parsers.OptionalWord("seconds|off|status"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleSlowmode);

		server.api.commandapi.Create("lockchat")
			.WithDescription("Lock or unlock global player chat")
			.WithArgs(parsers.OptionalWordRange("mode", "on", "off", "toggle", "status"), parsers.OptionalAll("reason"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleLockChat);

		server.api.commandapi.Create("chatclear")
			.WithAlias("clearchat")
			.WithDescription("Clear visible chat history for online players")
			.WithArgs(parsers.OptionalInt("lines", StratumRuntime.Config.Commands.ClearChatLines))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleClearChat);

		server.api.commandapi.Create("staffbroadcast")
			.WithAlias("sbc")
			.WithDescription("Broadcast a highlighted staff message to the server")
			.WithArgs(parsers.All("message"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleStaffBroadcast);

		server.api.commandapi.Create("rules")
			.WithDescription("Show the server rules")
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleRules);

		server.api.commandapi.Create("discord")
			.WithDescription("Show the server Discord link")
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleDiscord);

		server.api.commandapi.Create("website")
			.WithDescription("Show the server website link")
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleWebsite);

		server.api.commandapi.Create("motd")
			.WithDescription("Show the server message of the day")
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleMotd);

		server.api.commandapi.Create("vanish")
			.WithDescription("Toggle staff vanish")
			.WithArgs(parsers.OptionalWordRange("mode", "on", "off", "toggle"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleVanish);

		server.api.commandapi.Create("freeze")
			.WithDescription("Freeze or unfreeze an online player")
			.WithArgs(parsers.Word("player"), parsers.OptionalWordRange("mode", "on", "off", "toggle"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleFreeze);

		server.api.commandapi.Create("revive")
			.WithDescription("Revive a dead online player in place (one-life event recovery)")
			.WithArgs(parsers.Word("player"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleRevive);

		server.api.commandapi.Create("setjail")
			.WithDescription("Set the Stratum jail location to your current position")
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleSetJail);

		server.api.commandapi.Create("jail")
			.WithDescription("Jail a known player")
			.WithArgs(parsers.Word("player"), parsers.OptionalAll("reason"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleJail);

		server.api.commandapi.Create("unjail")
			.WithDescription("Release a jailed player")
			.WithArgs(parsers.Word("player"), parsers.OptionalAll("reason"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleUnjail);

		server.api.commandapi.Create("jailstatus")
			.WithDescription("Show whether a player is jailed")
			.WithArgs(parsers.Word("player"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleJailStatus);

		server.api.commandapi.Create("warn")
			.WithDescription("Warn a player and store the reason in moderation history")
			.WithArgs(parsers.Word("player"), parsers.All("reason"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleWarn);

		server.api.commandapi.Create("warnings")
			.WithDescription("List active warnings for a player")
			.WithArgs(parsers.Word("player"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleWarnings);

		server.api.commandapi.Create("delwarn")
			.WithDescription("Remove an active warning from a player")
			.WithArgs(parsers.Word("player"), parsers.Int("warning id"), parsers.OptionalAll("reason"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleDeleteWarning);

		server.api.commandapi.Create("mute")
			.WithDescription("Mute a player for a duration or permanently")
			.WithArgs(parsers.Word("player"), parsers.Word("duration"), parsers.All("reason"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleMute);

		server.api.commandapi.Create("unmute")
			.WithDescription("Remove an active mute from a player")
			.WithArgs(parsers.Word("player"), parsers.OptionalAll("reason"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleUnmute);

		server.api.commandapi.Create("mutestatus")
			.WithDescription("Show whether a player is muted")
			.WithArgs(parsers.Word("player"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleMuteStatus);

		server.api.commandapi.Create("note")
			.WithDescription("Add, list, or delete staff notes for a player")
			.WithArgs(parsers.Word("player"), parsers.WordRange("action", "add", "list", "delete"), parsers.OptionalAll("text or id"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleNote);

		server.api.commandapi.Create("notes")
			.WithDescription("List staff notes for a player")
			.WithArgs(parsers.Word("player"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleNotes);

		server.api.commandapi.Create("report")
			.WithDescription("Report a player to online staff")
			.WithArgs(parsers.Word("player"), parsers.All("reason"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleReport);

		server.api.commandapi.Create("reports")
			.WithDescription("Manage player reports")
			.WithArgs(parsers.OptionalWordRange("action", "list", "info", "claim", "close"), parsers.OptionalWord("id or status"), parsers.OptionalAll("resolution"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleReports);

		StratumTargetCommandOverrides.Register(server);
	}

	private void RegisterMessageCommand(string command, CommandArgumentParsers parsers)
	{
		server.api.commandapi.Create(command)
			.WithDescription("Send a private message to an online player")
			.WithArgs(parsers.Word("player"), parsers.All("message"))
			.RequiresPrivilege(Privilege.chat)
			.HandleWith(HandleMessage);
	}

	private TextCommandResult HandleSeen(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Seen, "seen", out TextCommandResult failure))
		{
			return failure;
		}

		string playerName = args[0] as string;
		ConnectedClient client = FindOnlineClientByName(playerName);
		ServerPlayerData data = client?.ServerData ?? server.PlayerDataManager.GetServerPlayerDataByLastKnownPlayername(playerName);
		if (data == null)
		{
			return TextCommandResult.Error("No known player named " + playerName + ".");
		}

		if (client != null && client.State.IsAdmitted())
		{
			long connectedSeconds = Math.Max(0, (server.ElapsedMilliseconds - client.MillisecsAtConnect) / 1000);
			return TextCommandResult.Success(data.LastKnownPlayername + " is online, connected for " + FormatDuration(TimeSpan.FromSeconds(connectedSeconds)) + ".");
		}

		if (StratumStaffCommandState.TryGetLastSeen(data, out DateTime seenUtc, out string seenState))
		{
			return TextCommandResult.Success(data.LastKnownPlayername + " was last seen " + FormatUtc(seenUtc) + " (" + (seenState ?? "offline") + ").");
		}

		return TextCommandResult.Success(data.LastKnownPlayername + " is offline. Last join: " + (data.LastJoinDate ?? "unknown") + ".");
	}

	private TextCommandResult HandleWhois(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Whois, "whois", out TextCommandResult failure))
		{
			return failure;
		}

		string playerName = args[0] as string;
		ConnectedClient client = FindOnlineClientByName(playerName);
		ServerPlayerData data = client?.ServerData ?? server.PlayerDataManager.GetServerPlayerDataByLastKnownPlayername(playerName);
		if (data == null)
		{
			return TextCommandResult.Error("No known player named " + playerName + ".");
		}

		string online = client != null && client.State.IsAdmitted() ? "online" : "offline";
		string position = client?.Player?.Entity?.Pos == null ? "unknown" : FormatPosition(client.Player.Entity.Pos);
		string ping = client?.LastPing > 0 ? ((int)(client.LastPing * 1000f)).ToString(GlobalConstants.DefaultCultureInfo) + "ms" : "n/a";
		int privilegeCount = client?.Player?.Privileges?.Length ?? data.GetAllPrivilegeCodes(server.Config).Count;
		string lastSeen = StratumStaffCommandState.TryGetLastSeen(data, out DateTime seenUtc, out string seenState)
			? FormatUtc(seenUtc) + " (" + (seenState ?? online) + ")"
			: data.LastJoinDate ?? "unknown";

		StringBuilder output = new StringBuilder(StratumCommandText.Title("Whois: " + data.LastKnownPlayername));
		output.Append(StratumCommandText.Row("UID", data.PlayerUID));
		output.Append(StratumCommandText.RawRow("State", StratumCommandText.Pill(online, online == "online" ? StratumCommandText.Good : StratumCommandText.Muted) + " role=" + StratumCommandText.Escape(data.RoleCode) + " privileges=" + privilegeCount));
		output.Append(StratumCommandText.Row("First join", data.FirstJoinDate ?? "unknown"));
		output.Append(StratumCommandText.Row("Last join", data.LastJoinDate ?? "unknown"));
		output.Append(StratumCommandText.Row("Last seen", lastSeen));
		output.Append(StratumCommandText.Row("Position", position));
		output.Append(StratumCommandText.Row("Ping", ping));
		output.Append(StratumCommandText.Row("Session", "vanished=" + (StratumStaffCommandState.IsVanished(data.PlayerUID) ? "yes" : "no") + ", frozen=" + (StratumStaffCommandState.IsFrozen(data.PlayerUID) ? "yes" : "no")));
		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult HandleNear(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Near, "near", out TextCommandResult failure))
		{
			return failure;
		}

		IServerPlayer player = GetPlayer(args);
		if (player == null)
		{
			return TextCommandResult.Error("Only players can use /near.");
		}

		int radius = GameMath.Clamp((int)args[0], 1, Math.Max(1, StratumRuntime.Config.Commands.NearMaxRadiusBlocks));
		EntityPos pos = player.Entity.Pos;
		var nearby = server.Clients.Values
			.Where(client => client.State.IsAdmitted() && client.Player?.Entity?.Pos != null && client.Player.PlayerUID != player.PlayerUID)
			.Where(client => client.Player.Entity.Pos.Dimension == pos.Dimension)
			.Where(client => !StratumStaffCommandState.IsVanished(client.Player.PlayerUID) || StratumCommandAccessCatalog.PlayerHasAccess(player, StratumRuntime.Config.Commands.Vanish))
			.Select(client => new
			{
				client.Player.PlayerName,
				Distance = Math.Sqrt(DistanceSquared(pos, client.Player.Entity.Pos))
			})
			.Where(entry => entry.Distance <= radius)
			.OrderBy(entry => entry.Distance)
			.ToArray();

		if (nearby.Length == 0)
		{
			return TextCommandResult.Success(StratumCommandText.Empty("No players within " + radius + " blocks."));
		}

		StringBuilder output = new StringBuilder(StratumCommandText.Title("Nearby Players"));
		output.Append(StratumCommandText.Row("Radius", radius + " blocks"));
		foreach (var entry in nearby)
		{
			output.Append(StratumCommandText.Bullet(entry.PlayerName, Math.Round(entry.Distance).ToString(GlobalConstants.DefaultCultureInfo) + "m"));
		}

		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult HandleBack(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Back, "back", out TextCommandResult failure))
		{
			return failure;
		}

		IServerPlayer player = GetPlayer(args);
		if (player == null)
		{
			return TextCommandResult.Error("Only players can use /back.");
		}

		if (!StratumStaffCommandState.TryGetBackLocation(player.PlayerUID, out EntityPos target))
		{
			return TextCommandResult.Error("No previous Stratum teleport or death location recorded.");
		}

		return StratumTeleportWarmups.StartOrTeleport(server, player, target, "back", "previous location");
	}

	private TextCommandResult HandleMessage(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Message, "msg", out TextCommandResult failure))
		{
			return failure;
		}

		if (TryRejectMuted(args, out failure))
		{
			return failure;
		}

		string targetName = args[0] as string;
		string message = args[1] as string;
		if (string.IsNullOrWhiteSpace(message))
		{
			return TextCommandResult.Error("Usage: /msg <player> <message>");
		}

		ConnectedClient target = FindOnlineClientByName(targetName);
		if (target == null || !target.State.IsAdmitted())
		{
			return TextCommandResult.Error("No such online player: " + targetName);
		}

		IServerPlayer sender = GetPlayer(args);
		if (sender != null && target.Player.PlayerUID == sender.PlayerUID)
		{
			return TextCommandResult.Error("You cannot message yourself.");
		}

		SendPrivateMessage(sender, target.Player, args.Caller.GetName().Replace("Player ", string.Empty), message);
		return TextCommandResult.Success(StratumCommandText.Confirm("Message sent", "to " + target.PlayerName + "."));
	}

	private TextCommandResult HandleReply(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Message, "reply", out TextCommandResult failure))
		{
			return failure;
		}

		if (TryRejectMuted(args, out failure))
		{
			return failure;
		}

		IServerPlayer sender = GetPlayer(args);
		if (sender == null)
		{
			return TextCommandResult.Error("Only players can use /reply.");
		}

		string message = args[0] as string;
		if (string.IsNullOrWhiteSpace(message))
		{
			return TextCommandResult.Error("Usage: /reply <message>");
		}

		if (!lastMessagePartnerByUid.TryGetValue(sender.PlayerUID, out string targetUid))
		{
			return TextCommandResult.Error("No one to reply to yet.");
		}

		ConnectedClient target = FindOnlineClientByUid(targetUid);
		if (target == null || !target.State.IsAdmitted())
		{
			return TextCommandResult.Error("Your last message partner is no longer online.");
		}

		SendPrivateMessage(sender, target.Player, sender.PlayerName, message);
		return TextCommandResult.Success(StratumCommandText.Confirm("Reply sent", "to " + target.PlayerName + "."));
	}

	private TextCommandResult HandleStaffChat(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.StaffChat, "staffchat", out TextCommandResult failure))
		{
			return failure;
		}

		if (TryRejectMuted(args, out failure))
		{
			return failure;
		}

		string message = args[0] as string;
		if (string.IsNullOrWhiteSpace(message))
		{
			return TextCommandResult.Error("Usage: /staffchat <message>");
		}

		string senderName = GetPlayer(args)?.PlayerName ?? "Console";
		string formatted = "<font color=\"#f5c542\"><strong>[Staff]</strong></font> <strong>" + EscapeVtml(senderName) + ":</strong> " + EscapeVtml(message);
		foreach (ConnectedClient client in server.Clients.Values)
		{
			if (client.State.IsAdmitted() && StratumCommandAccessCatalog.PlayerHasAccess(client.Player, StratumRuntime.Config.Commands.StaffChat))
			{
				Send(client.Player, formatted, EnumChatType.Notification);
			}
		}

		StratumRuntime.LogInfo("staffchat " + senderName + ": " + message.Replace("{", "{{").Replace("}", "}}"));
		return TextCommandResult.Success();
	}

	private TextCommandResult HandleSlowmode(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.ChatControl, "slowmode", out TextCommandResult failure))
		{
			return failure;
		}

		string value = args[0] as string;
		if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "status", StringComparison.OrdinalIgnoreCase))
		{
			return TextCommandResult.Success(StratumCommandText.Title("Chat Slowmode") + StratumCommandText.Row("Status", StratumStaffCommandState.SlowmodeSeconds <= 0 ? "off" : StratumStaffCommandState.SlowmodeSeconds + " seconds"));
		}

		if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "disable", StringComparison.OrdinalIgnoreCase) || value == "0")
		{
			StratumStaffCommandState.SetSlowmode(0, args.Caller.GetName());
			server.SendMessageToGeneral("<font color=\"#9bd77e\"><strong>Chat slowmode disabled.</strong></font>", EnumChatType.Notification);
			StratumRuntime.LogAudit("slowmode off actor=" + args.Caller.GetName(), true);
			return TextCommandResult.Success(StratumCommandText.Confirm("Chat slowmode disabled"));
		}

		if (!TryParseSeconds(value, out int seconds, out string error))
		{
			return TextCommandResult.Error(error);
		}

		int maxSeconds = StratumRuntime.Config.Commands.SlowmodeMaxSeconds;
		if (maxSeconds > 0 && seconds > maxSeconds)
		{
			return TextCommandResult.Error("Slowmode cannot exceed " + maxSeconds + " seconds.");
		}

		StratumStaffCommandState.SetSlowmode(seconds, args.Caller.GetName());
		server.SendMessageToGeneral("<font color=\"#e6c15f\"><strong>Chat slowmode set to " + seconds + " seconds.</strong></font>", EnumChatType.Notification);
		StratumRuntime.LogAudit("slowmode seconds=" + seconds + " actor=" + args.Caller.GetName(), true);
		return TextCommandResult.Success(StratumCommandText.Confirm("Chat slowmode set", "to " + seconds + " seconds."));
	}

	private TextCommandResult HandleLockChat(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.ChatControl, "lockchat", out TextCommandResult failure))
		{
			return failure;
		}

		string mode = args[0] as string;
		string reason = args[1] as string;
		if (string.Equals(mode, "status", StringComparison.OrdinalIgnoreCase))
		{
			StringBuilder output = new StringBuilder(StratumCommandText.Title("Chat Lock"));
			output.Append(StratumCommandText.Row("Status", StratumStaffCommandState.IsChatLocked ? "locked" : "unlocked"));
			if (StratumStaffCommandState.IsChatLocked)
			{
				output.Append(StratumCommandText.Row("Reason", StratumStaffCommandState.ChatLockReason ?? "none"));
			}

			return TextCommandResult.Success(output.ToString());
		}

		bool enable = string.Equals(mode, "on", StringComparison.OrdinalIgnoreCase) || ((!string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase)) && !StratumStaffCommandState.IsChatLocked);
		if (enable)
		{
			StratumStaffCommandState.SetChatLocked(true, reason, args.Caller.GetName());
			string suffix = string.IsNullOrWhiteSpace(reason) ? string.Empty : " Reason: " + EscapeVtml(reason);
			server.SendMessageToGeneral("<font color=\"#e47d68\"><strong>Chat has been locked by staff.</strong></font>" + suffix, EnumChatType.Notification);
			StratumRuntime.LogAudit("lockchat on actor=" + args.Caller.GetName() + " reason=" + EscapeLog(reason), true);
			return TextCommandResult.Success(StratumCommandText.Warning("Chat locked"));
		}

		StratumStaffCommandState.SetChatLocked(false, null, args.Caller.GetName());
		server.SendMessageToGeneral("<font color=\"#9bd77e\"><strong>Chat has been unlocked.</strong></font>", EnumChatType.Notification);
		StratumRuntime.LogAudit("lockchat off actor=" + args.Caller.GetName(), true);
		return TextCommandResult.Success(StratumCommandText.Confirm("Chat unlocked"));
	}

	private TextCommandResult HandleClearChat(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.ChatControl, "chatclear", out TextCommandResult failure))
		{
			return failure;
		}

		int lines = GameMath.Clamp((int)args[0], 1, StratumRuntime.Config.Commands.ClearChatLines);
		int players = 0;
		foreach (ConnectedClient client in server.Clients.Values)
		{
			if (!client.State.IsAdmitted() || client.Player == null)
			{
				continue;
			}

			players++;
			for (int i = 0; i < lines; i++)
			{
				Send(client.Player, " ", EnumChatType.Notification);
			}
		}

		server.SendMessageToGeneral("<font color=\"#e6c15f\"><strong>Chat was cleared by staff.</strong></font>", EnumChatType.Notification);
		StratumRuntime.LogAudit("chatclear actor=" + args.Caller.GetName() + " lines=" + lines + " players=" + players, true);
		return TextCommandResult.Success(StratumCommandText.Confirm("Cleared chat", "for " + players + " online players."));
	}

	private TextCommandResult HandleStaffBroadcast(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.StaffBroadcast, "staffbroadcast", out TextCommandResult failure))
		{
			return failure;
		}

		string message = args[0] as string;
		if (string.IsNullOrWhiteSpace(message))
		{
			return TextCommandResult.Error("Usage: /staffbroadcast <message>");
		}

		server.BroadcastMessageToAllGroups("<font color=\"#f5c542\"><strong>[Staff]</strong></font> " + EscapeVtml(message), EnumChatType.AllGroups);
		StratumRuntime.LogAudit("staffbroadcast actor=" + args.Caller.GetName() + " message=" + EscapeLog(message), true);
		return TextCommandResult.Success(StratumCommandText.Confirm("Staff broadcast sent"));
	}

	private TextCommandResult HandleRules(TextCommandCallingArgs args)
	{
		return HandleInfoCommand(args, "rules", StratumRuntime.Config.Chat.RulesText, "No rules have been configured.");
	}

	private TextCommandResult HandleDiscord(TextCommandCallingArgs args)
	{
		return HandleInfoCommand(args, "discord", StratumRuntime.Config.Chat.DiscordUrl, "No Discord link has been configured.");
	}

	private TextCommandResult HandleWebsite(TextCommandCallingArgs args)
	{
		return HandleInfoCommand(args, "website", StratumRuntime.Config.Chat.WebsiteUrl, "No website link has been configured.");
	}

	private TextCommandResult HandleMotd(TextCommandCallingArgs args)
	{
		return HandleInfoCommand(args, "motd", StratumRuntime.Config.Chat.MotdText, "No message of the day has been configured.");
	}

	private TextCommandResult HandleInfoCommand(TextCommandCallingArgs args, string command, string value, string fallback)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.InfoCommands, command, out TextCommandResult failure))
		{
			return failure;
		}

		return TextCommandResult.Success(string.IsNullOrWhiteSpace(value) ? fallback : value);
	}

	private TextCommandResult HandleVanish(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Vanish, "vanish", out TextCommandResult failure))
		{
			return failure;
		}

		IServerPlayer player = GetPlayer(args);
		if (player == null)
		{
			return TextCommandResult.Error("Only players can use /vanish.");
		}

		string mode = args[0] as string;
		bool enable = string.Equals(mode, "on", StringComparison.OrdinalIgnoreCase) || (!string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase) && !StratumStaffCommandState.IsVanished(player.PlayerUID));
		StratumStaffCommandState.SetVanished(player, enable);
		if (enable)
		{
			StratumStaffCommandState.HideVanishedPlayerFromOthers(server, player);
			return TextCommandResult.Success(StratumCommandText.Confirm("Vanish enabled"));
		}

		StratumStaffCommandState.RevealPlayerToOthers(server, player);
		return TextCommandResult.Success(StratumCommandText.Confirm("Vanish disabled"));
	}

	private TextCommandResult HandleFreeze(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Freeze, "freeze", out TextCommandResult failure))
		{
			return failure;
		}

		string token = args[0] as string;
		string mode = args[1] as string;
		return RunForOnlineTargets(token, target => FreezeSingleTarget(target, mode));
	}

	private TextCommandResult FreezeSingleTarget(ConnectedClient target, string mode)
	{
		bool enable = string.Equals(mode, "on", StringComparison.OrdinalIgnoreCase) || (!string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase) && !StratumStaffCommandState.IsFrozen(target.Player.PlayerUID));
		if (enable)
		{
			StratumStaffCommandState.Freeze(target.Player);
			Send(target.Player, StratumCommandText.Danger("You have been frozen by staff."), EnumChatType.Notification);
			return TextCommandResult.Success(StratumCommandText.Warning("Frozen") + " " + StratumCommandText.Escape(target.PlayerName) + ".");
		}

		StratumStaffCommandState.Unfreeze(target.Player.PlayerUID);
		Send(target.Player, StratumCommandText.Confirm("You have been unfrozen by staff."), EnumChatType.Notification);
		return TextCommandResult.Success(StratumCommandText.Confirm("Unfroze", target.PlayerName + "."));
	}

	private TextCommandResult HandleRevive(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Revive, "revive", out TextCommandResult failure))
		{
			return failure;
		}

		string token = args[0] as string;
		string actorName = args.Caller.GetName();
		return RunForOnlineTargets(token, target => ReviveSingleTarget(target, actorName));
	}

	private TextCommandResult ReviveSingleTarget(ConnectedClient target, string actorName)
	{
		EntityPlayer targetEntity = target.Player?.Entity;
		if (targetEntity == null)
		{
			return TextCommandResult.Error("Player " + target.PlayerName + " has no entity loaded.");
		}

		if (targetEntity.Alive)
		{
			return TextCommandResult.Success(StratumCommandText.Confirm("Already alive", StratumCommandText.Escape(target.PlayerName) + " is not dead."));
		}

		targetEntity.Revive();

		Send(target.Player, StratumCommandText.Confirm("You have been revived by staff."), EnumChatType.Notification);
		StratumRuntime.LogAudit("revive target=" + target.PlayerName + " actor=" + actorName, true);
		return TextCommandResult.Success(StratumCommandText.Confirm("Revived", StratumCommandText.Escape(target.PlayerName) + " in place."));
	}

	private TextCommandResult HandleSetJail(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Jail, "setjail", out TextCommandResult failure))
		{
			return failure;
		}

		IServerPlayer player = GetPlayer(args);
		if (player?.Entity?.Pos == null)
		{
			return TextCommandResult.Error("Only players can use /setjail.");
		}

		StratumRuntime.Config.Commands.JailSettings.Location = StratumPositionConfig.FromEntityPos(player.Entity.Pos);
		StratumRuntime.SaveConfig();
		StratumRuntime.LogAudit("setjail actor=" + args.Caller.GetName() + " pos=" + StratumRuntime.Config.Commands.JailSettings.Location.FormatBlockPos(), true);
		return TextCommandResult.Success(StratumCommandText.Confirm("Jail set", "to " + StratumRuntime.Config.Commands.JailSettings.Location.FormatBlockPos() + "."));
	}

	private TextCommandResult HandleJail(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Jail, "jail", out TextCommandResult failure))
		{
			return failure;
		}

		if (!TryGetJailLocation(out EntityPos jailPosition, out string locationError))
		{
			return TextCommandResult.Error(locationError);
		}

		string token = args[0] as string;
		string reason = args[1] as string;
		Caller caller = args.Caller;
		return RunForKnownTargets(token, (targetData, onlineTarget) => JailSingleTarget(targetData, onlineTarget, caller, reason, jailPosition));
	}

	private TextCommandResult JailSingleTarget(ServerPlayerData targetData, ConnectedClient onlineTarget, Caller caller, string reason, EntityPos jailPosition)
	{
		StratumPositionConfig returnPosition = onlineTarget?.Player?.Entity?.Pos == null ? null : StratumPositionConfig.FromEntityPos(onlineTarget.Player.Entity.Pos);
		StratumJailStatus status = StratumCustodyStore.JailPlayer(server, targetData, caller, reason, returnPosition);
		if (onlineTarget?.Player?.Entity != null)
		{
			StratumStaffCommandState.RecordBackLocation(onlineTarget.Player);
			onlineTarget.Player.Entity.TeleportTo(jailPosition);
			Send(onlineTarget.Player, "<font color=\"#e47d68\"><strong>You have been jailed by staff.</strong></font>" + FormatOptionalReason(reason), EnumChatType.CommandError);
		}

		StratumRuntime.LogAudit("jail target=" + targetData.LastKnownPlayername + " actor=" + caller.GetName() + " reason=" + EscapeLog(reason), true);
		return TextCommandResult.Success(StratumCommandText.Warning("Jailed") + " " + StratumCommandText.Escape(targetData.LastKnownPlayername) + " at " + StratumCommandText.Escape(FormatUtc(status.JailedUtc)) + ".");
	}

	private TextCommandResult HandleUnjail(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Jail, "unjail", out TextCommandResult failure))
		{
			return failure;
		}

		string token = args[0] as string;
		string reason = args[1] as string;
		Caller caller = args.Caller;
		return RunForKnownTargets(token, (targetData, onlineTarget) => UnjailSingleTarget(targetData, onlineTarget, caller, reason));
	}

	private TextCommandResult UnjailSingleTarget(ServerPlayerData targetData, ConnectedClient onlineTarget, Caller caller, string reason)
	{
		if (!StratumCustodyStore.UnjailPlayer(server, targetData, caller, reason, out StratumJailStatus status))
		{
			return TextCommandResult.Error(targetData.LastKnownPlayername + " is not jailed.");
		}

		if (onlineTarget?.Player?.Entity != null)
		{
			Send(onlineTarget.Player, StratumCommandText.Confirm("You have been released from jail by staff."), EnumChatType.Notification);
			if (StratumRuntime.Config.Commands.JailSettings.ReturnOnUnjail && status.ReturnPosition?.Set == true)
			{
				StratumStaffCommandState.RecordBackLocation(onlineTarget.Player);
				onlineTarget.Player.Entity.TeleportTo(status.ReturnPosition.ToEntityPos());
			}
		}

		StratumRuntime.LogAudit("unjail target=" + targetData.LastKnownPlayername + " actor=" + caller.GetName() + " reason=" + EscapeLog(reason), true);
		return TextCommandResult.Success(StratumCommandText.Confirm("Released", targetData.LastKnownPlayername + " from jail."));
	}

	private TextCommandResult HandleJailStatus(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Jail, "jailstatus", out TextCommandResult failure))
		{
			return failure;
		}

		string targetName = args[0] as string;
		if (!TryGetKnownPlayerData(targetName, out ServerPlayerData targetData, out _))
		{
			return TextCommandResult.Error("No known player named " + targetName + ".");
		}

		if (!StratumCustodyStore.TryGetActiveJail(targetData, out StratumJailStatus status))
		{
			return TextCommandResult.Success(StratumCommandText.Empty(targetData.LastKnownPlayername + " is not jailed."));
		}

		StringBuilder output = new StringBuilder(StratumCommandText.Title("Jail Status: " + targetData.LastKnownPlayername));
		output.Append(StratumCommandText.RawRow("State", StratumCommandText.Pill("jailed", StratumCommandText.Bad)));
		output.Append(StratumCommandText.Row("Since", FormatUtc(status.JailedUtc)));
		output.Append(StratumCommandText.Row("By", status.ActorName));
		output.Append(StratumCommandText.Row("Reason", string.IsNullOrWhiteSpace(status.Reason) ? "none" : status.Reason));
		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult HandleWarn(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Warn, "warn", out TextCommandResult failure))
		{
			return failure;
		}

		string token = args[0] as string;
		string reason = args[1] as string;
		if (string.IsNullOrWhiteSpace(reason))
		{
			return TextCommandResult.Error("Usage: /warn <player> <reason>");
		}

		Caller caller = args.Caller;
		return RunForKnownTargets(token, (targetData, onlineTarget) => WarnSingleTarget(targetData, onlineTarget, caller, reason));
	}

	private TextCommandResult WarnSingleTarget(ServerPlayerData targetData, ConnectedClient onlineTarget, Caller caller, string reason)
	{
		StratumModerationRecord warning = StratumModerationStore.AddWarning(server, targetData, caller, reason);
		if (onlineTarget?.Player != null)
		{
			Send(onlineTarget.Player, "<font color=\"#e6c15f\"><strong>You have been warned:</strong></font> " + EscapeVtml(reason), EnumChatType.Notification);
		}

		StratumRuntime.LogAudit("warn id=" + warning.Id + " target=" + targetData.LastKnownPlayername + " actor=" + caller.GetName() + " reason=" + EscapeLog(reason), true);
		return TextCommandResult.Success(StratumCommandText.Warning("Warned") + " " + StratumCommandText.Escape(targetData.LastKnownPlayername) + " " + StratumCommandText.Pill("#" + warning.Id, StratumCommandText.Warn) + ".");
	}

	private TextCommandResult HandleWarnings(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Warn, "warnings", out TextCommandResult failure))
		{
			return failure;
		}

		string targetName = args[0] as string;
		if (!TryGetKnownPlayerData(targetName, out ServerPlayerData targetData, out _))
		{
			return TextCommandResult.Error("No known player named " + targetName + ".");
		}

		IReadOnlyList<StratumModerationRecord> warnings = StratumModerationStore.GetWarnings(targetData);
		if (warnings.Count == 0)
		{
			return TextCommandResult.Success(StratumCommandText.Empty(targetData.LastKnownPlayername + " has no active warnings."));
		}

		StringBuilder output = new StringBuilder(StratumCommandText.Title("Warnings: " + targetData.LastKnownPlayername));
		output.Append(StratumCommandText.Row("Active", warnings.Count.ToString(GlobalConstants.DefaultCultureInfo)));
		foreach (StratumModerationRecord warning in warnings.OrderByDescending(record => record.CreatedUtc))
		{
			output.Append("\n").Append(StratumCommandText.Pill("#" + warning.Id, StratumCommandText.Warn)).Append(" ");
			output.Append(StratumCommandText.Escape(FormatUtc(warning.CreatedUtc))).Append(" by ").Append(StratumCommandText.Escape(warning.ActorName));
			output.Append(StratumCommandText.Row("Reason", warning.Text));
		}

		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult HandleDeleteWarning(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Warn, "delwarn", out TextCommandResult failure))
		{
			return failure;
		}

		string targetName = args[0] as string;
		int warningId = (int)args[1];
		string reason = args[2] as string;
		if (!TryGetKnownPlayerData(targetName, out ServerPlayerData targetData, out _))
		{
			return TextCommandResult.Error("No known player named " + targetName + ".");
		}

		if (!StratumModerationStore.DeleteWarning(server, targetData, warningId, args.Caller, reason))
		{
			return TextCommandResult.Error("No active warning #" + warningId + " for " + targetData.LastKnownPlayername + ".");
		}

		StratumRuntime.LogAudit("delwarn id=" + warningId + " target=" + targetData.LastKnownPlayername + " actor=" + args.Caller.GetName(), true);
		return TextCommandResult.Success(StratumCommandText.Confirm("Deleted warning", "#" + warningId + " for " + targetData.LastKnownPlayername + "."));
	}

	private TextCommandResult HandleMute(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Mute, "mute", out TextCommandResult failure))
		{
			return failure;
		}

		string token = args[0] as string;
		string durationText = args[1] as string;
		string reason = args[2] as string;
		if (string.IsNullOrWhiteSpace(reason))
		{
			return TextCommandResult.Error("Usage: /mute <player> <duration> <reason>");
		}

		if (!TryParseMuteDuration(durationText, out DateTime? expiresUtc, out string durationError))
		{
			return TextCommandResult.Error(durationError);
		}

		Caller caller = args.Caller;
		return RunForKnownTargets(token, (targetData, onlineTarget) => MuteSingleTarget(targetData, onlineTarget, caller, reason, expiresUtc));
	}

	private TextCommandResult MuteSingleTarget(ServerPlayerData targetData, ConnectedClient onlineTarget, Caller caller, string reason, DateTime? expiresUtc)
	{
		StratumModerationRecord mute = StratumModerationStore.AddMute(server, targetData, caller, reason, expiresUtc);
		string expiry = StratumModerationStore.FormatExpiry(mute);
		if (onlineTarget?.Player != null)
		{
			Send(onlineTarget.Player, "<font color=\"#e47d68\"><strong>You have been muted until " + EscapeVtml(expiry) + ":</strong></font> " + EscapeVtml(reason), EnumChatType.CommandError);
		}

		StratumRuntime.LogAudit("mute id=" + mute.Id + " target=" + targetData.LastKnownPlayername + " actor=" + caller.GetName() + " expires=" + expiry + " reason=" + EscapeLog(reason), true);
		return TextCommandResult.Success(StratumCommandText.Danger("Muted") + " " + StratumCommandText.Escape(targetData.LastKnownPlayername) + " until " + StratumCommandText.Escape(expiry) + " " + StratumCommandText.Pill("#" + mute.Id, StratumCommandText.Bad) + ".");
	}

	private TextCommandResult HandleUnmute(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Mute, "unmute", out TextCommandResult failure))
		{
			return failure;
		}

		string token = args[0] as string;
		string reason = args[1] as string;
		Caller caller = args.Caller;
		return RunForKnownTargets(token, (targetData, onlineTarget) => UnmuteSingleTarget(targetData, onlineTarget, caller, reason));
	}

	private TextCommandResult UnmuteSingleTarget(ServerPlayerData targetData, ConnectedClient onlineTarget, Caller caller, string reason)
	{
		if (!StratumModerationStore.Unmute(server, targetData, caller, reason))
		{
			return TextCommandResult.Error(targetData.LastKnownPlayername + " has no active mute.");
		}

		if (onlineTarget?.Player != null)
		{
			Send(onlineTarget.Player, "You have been unmuted by staff.", EnumChatType.Notification);
		}

		StratumRuntime.LogAudit("unmute target=" + targetData.LastKnownPlayername + " actor=" + caller.GetName(), true);
		return TextCommandResult.Success(StratumCommandText.Confirm("Unmuted", targetData.LastKnownPlayername + "."));
	}

	private TextCommandResult HandleMuteStatus(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Mute, "mutestatus", out TextCommandResult failure))
		{
			return failure;
		}

		string targetName = args[0] as string;
		if (!TryGetKnownPlayerData(targetName, out ServerPlayerData targetData, out _))
		{
			return TextCommandResult.Error("No known player named " + targetName + ".");
		}

		if (!StratumModerationStore.TryGetActiveMute(server, targetData, out StratumModerationRecord mute))
		{
			return TextCommandResult.Success(StratumCommandText.Empty(targetData.LastKnownPlayername + " is not muted."));
		}

		StringBuilder output = new StringBuilder(StratumCommandText.Title("Mute Status: " + targetData.LastKnownPlayername));
		output.Append(StratumCommandText.RawRow("State", StratumCommandText.Pill("muted", StratumCommandText.Bad)));
		output.Append(StratumCommandText.Row("Until", StratumModerationStore.FormatExpiry(mute)));
		output.Append(StratumCommandText.Row("Reason", mute.Text));
		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult HandleNote(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Notes, "note", out TextCommandResult failure))
		{
			return failure;
		}

		string targetName = args[0] as string;
		string action = args[1] as string;
		string value = args[2] as string;
		if (!TryGetKnownPlayerData(targetName, out ServerPlayerData targetData, out _))
		{
			return TextCommandResult.Error("No known player named " + targetName + ".");
		}

		if (string.Equals(action, "add", StringComparison.OrdinalIgnoreCase))
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return TextCommandResult.Error("Usage: /note <player> add <text>");
			}

			StratumModerationRecord note = StratumModerationStore.AddNote(server, targetData, args.Caller, value);
			StratumRuntime.LogAudit("note add id=" + note.Id + " target=" + targetData.LastKnownPlayername + " actor=" + args.Caller.GetName(), true);
			return TextCommandResult.Success(StratumCommandText.Confirm("Added note", "#" + note.Id + " for " + targetData.LastKnownPlayername + "."));
		}

		if (string.Equals(action, "delete", StringComparison.OrdinalIgnoreCase))
		{
			if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int noteId))
			{
				return TextCommandResult.Error("Usage: /note <player> delete <id>");
			}

			if (!StratumModerationStore.DeleteNote(server, targetData, noteId, args.Caller, "deleted"))
			{
				return TextCommandResult.Error("No active note #" + noteId + " for " + targetData.LastKnownPlayername + ".");
			}

			StratumRuntime.LogAudit("note delete id=" + noteId + " target=" + targetData.LastKnownPlayername + " actor=" + args.Caller.GetName(), true);
			return TextCommandResult.Success(StratumCommandText.Confirm("Deleted note", "#" + noteId + " for " + targetData.LastKnownPlayername + "."));
		}

		return FormatNotes(targetData);
	}

	private TextCommandResult HandleNotes(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Notes, "notes", out TextCommandResult failure))
		{
			return failure;
		}

		string targetName = args[0] as string;
		if (!TryGetKnownPlayerData(targetName, out ServerPlayerData targetData, out _))
		{
			return TextCommandResult.Error("No known player named " + targetName + ".");
		}

		return FormatNotes(targetData);
	}

	private TextCommandResult HandleReport(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.Report, "report", out TextCommandResult failure))
		{
			return failure;
		}

		IServerPlayer reporter = GetPlayer(args);
		if (reporter == null)
		{
			return TextCommandResult.Error("Only players can create reports.");
		}

		string targetName = args[0] as string;
		string reason = args[1] as string;
		if (string.IsNullOrWhiteSpace(reason))
		{
			return TextCommandResult.Error("Usage: /report <player> <reason>");
		}

		TryGetKnownPlayerData(targetName, out ServerPlayerData targetData, out _);
		StratumReportEntry report = StratumReportStore.AddReport(reporter.PlayerUID, reporter.PlayerName, targetData?.PlayerUID, targetData?.LastKnownPlayername ?? targetName, reason);
		NotifyStaff("<font color=\"#e6c15f\"><strong>[Report #" + report.Id + "]</strong></font> " + EscapeVtml(reporter.PlayerName) + " reported " + EscapeVtml(report.TargetName) + ": " + EscapeVtml(reason));
		StratumRuntime.LogAudit("report id=" + report.Id + " reporter=" + reporter.PlayerName + " target=" + report.TargetName + " reason=" + EscapeLog(reason), true);
		return TextCommandResult.Success(StratumCommandText.Confirm("Report #" + report.Id + " submitted", "Staff have been notified."));
	}

	private TextCommandResult HandleReports(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, StratumRuntime.Config.Commands.ReportManage, "reports", out TextCommandResult failure))
		{
			return failure;
		}

		string action = args[0] as string ?? "list";
		string selector = args[1] as string;
		string detail = args[2] as string;

		if (string.Equals(action, "list", StringComparison.OrdinalIgnoreCase))
		{
			return FormatReports(string.IsNullOrWhiteSpace(selector) ? "open" : selector);
		}

		if (!int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out int reportId))
		{
			return TextCommandResult.Error("Usage: /reports " + action + " <id>");
		}

		if (string.Equals(action, "info", StringComparison.OrdinalIgnoreCase))
		{
			return StratumReportStore.TryGetReport(reportId, out StratumReportEntry report) ? TextCommandResult.Success(FormatReport(report)) : TextCommandResult.Error("No report #" + reportId + ".");
		}

		if (string.Equals(action, "claim", StringComparison.OrdinalIgnoreCase))
		{
			if (!StratumReportStore.ClaimReport(reportId, args.Caller.GetName(), out StratumReportEntry report))
			{
				return TextCommandResult.Error("No open or claimed report #" + reportId + ".");
			}

			StratumRuntime.LogAudit("report claim id=" + report.Id + " actor=" + args.Caller.GetName(), true);
			return TextCommandResult.Success(StratumCommandText.Confirm("Claimed report", "#" + report.Id + "."));
		}

		if (string.Equals(action, "close", StringComparison.OrdinalIgnoreCase))
		{
			if (!StratumReportStore.CloseReport(reportId, args.Caller.GetName(), detail, out StratumReportEntry report))
			{
				return TextCommandResult.Error("No open or claimed report #" + reportId + ".");
			}

			StratumRuntime.LogAudit("report close id=" + report.Id + " actor=" + args.Caller.GetName() + " resolution=" + EscapeLog(detail), true);
			return TextCommandResult.Success(StratumCommandText.Confirm("Closed report", "#" + report.Id + "."));
		}

		return TextCommandResult.Error("Usage: /reports [list <open|claimed|closed|all>|info <id>|claim <id>|close <id> <resolution>]");
	}

	private void OnFreezeTick(float dt)
	{
		foreach (FrozenPlayerState frozen in StratumStaffCommandState.FrozenSnapshots.ToArray())
		{
			ConnectedClient client = FindOnlineClientByUid(frozen.PlayerUid);
			if (client?.Player?.Entity?.Pos == null || !client.State.IsAdmitted())
			{
				StratumStaffCommandState.Unfreeze(frozen.PlayerUid);
				continue;
			}

			StopMovement(client.Player.Entity.Controls);
			client.Player.Entity.Pos.Motion.Set(0, 0, 0);
			if (client.Player.Entity.Pos.Dimension != frozen.Position.Dimension || DistanceSquared(client.Player.Entity.Pos, frozen.Position) > 0.04)
			{
				client.Player.Entity.TeleportTo(frozen.Position.Copy());
			}
		}

		EnforceJailedPlayers();
	}

	private void OnPlayerJoin(IServerPlayer player)
	{
		StratumStaffCommandState.MarkSeen(server, player, "online");
		ApplyJailOnJoin(player);
	}

	private void OnPlayerDisconnect(IServerPlayer player)
	{
		StratumStaffCommandState.MarkSeen(server, player, "offline");
		StratumStaffCommandState.ClearSessionState(player.PlayerUID);
	}

	private void OnPlayerDeath(IServerPlayer player, DamageSource damageSource)
	{
		StratumStaffCommandState.RecordBackLocation(player);
	}

	private void OnPlayerChat(IServerPlayer player, int channelId, ref string message, ref string data, BoolRef consumed)
	{
		if (consumed.value || player == null)
		{
			return;
		}

		ServerPlayerData playerData = server.PlayerDataManager.GetOrCreateServerPlayerData(player.PlayerUID, player.PlayerName);
		if (!StratumModerationStore.TryGetActiveMute(server, playerData, out StratumModerationRecord mute))
		{
			if (StratumCommandAccessCatalog.PlayerHasAccess(player, StratumRuntime.Config.Commands.ChatControl))
			{
				return;
			}

			if (StratumStaffCommandState.IsChatLocked)
			{
				consumed.value = true;
				string suffix = string.IsNullOrWhiteSpace(StratumStaffCommandState.ChatLockReason) ? string.Empty : " Reason: " + StratumStaffCommandState.ChatLockReason;
				player.SendMessage(channelId, "Chat is currently locked by staff." + suffix, EnumChatType.CommandError);
				return;
			}

			if (StratumStaffCommandState.TryRejectBySlowmode(player, DateTime.UtcNow, out TimeSpan remaining))
			{
				consumed.value = true;
				player.SendMessage(channelId, "Chat slowmode is enabled. Wait " + Math.Ceiling(remaining.TotalSeconds).ToString(GlobalConstants.DefaultCultureInfo) + " more seconds.", EnumChatType.CommandError);
			}

			return;
		}

		consumed.value = true;
		player.SendMessage(channelId, "You are muted until " + StratumModerationStore.FormatExpiry(mute) + ". Reason: " + mute.Text, EnumChatType.CommandError);
	}

	private bool CheckAccess(TextCommandCallingArgs args, StratumCommandAccessConfig access, string command, out TextCommandResult failure)
	{
		StratumRuntime.Config.EnsurePopulated();
		failure = null;

		if (!StratumRuntime.Config.Commands.Enabled)
		{
			failure = TextCommandResult.Error("Stratum commands are disabled.");
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

	private void SendPrivateMessage(IServerPlayer sender, IServerPlayer target, string senderName, string message)
	{
		string safeMessage = EscapeVtml(message);
		string safeSender = EscapeVtml(senderName);
		Send(target, "<font color=\"#8bd5ff\"><strong>[From " + safeSender + "]</strong></font> " + safeMessage, EnumChatType.Notification);
		if (sender != null)
		{
			Send(sender, "<font color=\"#8bd5ff\"><strong>[To " + EscapeVtml(target.PlayerName) + "]</strong></font> " + safeMessage, EnumChatType.CommandSuccess);
			lastMessagePartnerByUid[sender.PlayerUID] = target.PlayerUID;
			lastMessagePartnerByUid[target.PlayerUID] = sender.PlayerUID;
		}
	}

	private bool TryRejectMuted(TextCommandCallingArgs args, out TextCommandResult failure)
	{
		failure = null;
		IServerPlayer player = GetPlayer(args);
		if (player == null)
		{
			return false;
		}

		ServerPlayerData playerData = server.PlayerDataManager.GetOrCreateServerPlayerData(player.PlayerUID, player.PlayerName);
		if (!StratumModerationStore.TryGetActiveMute(server, playerData, out StratumModerationRecord mute))
		{
			return false;
		}

		failure = TextCommandResult.Error("You are muted until " + StratumModerationStore.FormatExpiry(mute) + ". Reason: " + mute.Text);
		return true;
	}

	private bool TryGetKnownPlayerData(string playerName, out ServerPlayerData targetData, out ConnectedClient onlineTarget)
	{
		onlineTarget = FindOnlineClientByName(playerName);
		targetData = onlineTarget?.ServerData ?? server.PlayerDataManager.GetServerPlayerDataByLastKnownPlayername(playerName);
		return targetData != null;
	}

	private void ApplyJailOnJoin(IServerPlayer player)
	{
		if (player?.Entity == null || !TryGetJailLocation(out EntityPos jailPosition, out _))
		{
			return;
		}

		ServerPlayerData playerData = server.PlayerDataManager.GetOrCreateServerPlayerData(player.PlayerUID, player.PlayerName);
		if (!StratumCustodyStore.TryGetActiveJail(playerData, out StratumJailStatus status))
		{
			return;
		}

		player.Entity.TeleportTo(jailPosition);
		Send(player, "You are jailed." + FormatOptionalReason(status.Reason), EnumChatType.CommandError);
	}

	private void EnforceJailedPlayers()
	{
		if (!TryGetJailLocation(out EntityPos jailPosition, out _))
		{
			return;
		}

		double maxDistance = StratumRuntime.Config.Commands.JailSettings.MaxDistanceBlocks;
		double maxDistanceSq = maxDistance * maxDistance;
		foreach (ConnectedClient client in server.Clients.Values)
		{
			if (!client.State.IsAdmitted() || client.Player?.Entity?.Pos == null || !StratumCustodyStore.TryGetActiveJail(client.ServerData, out _))
			{
				continue;
			}

			EntityPos current = client.Player.Entity.Pos;
			if (current.Dimension != jailPosition.Dimension || DistanceSquared(current, jailPosition) > maxDistanceSq)
			{
				client.Player.Entity.TeleportTo(jailPosition.Copy());
			}
		}
	}

	private static bool TryGetJailLocation(out EntityPos position, out string error)
	{
		StratumJailConfig jail = StratumRuntime.Config.Commands.JailSettings;
		if (jail?.Location?.Set != true)
		{
			position = null;
			error = "No jail location is set. Use /setjail first.";
			return false;
		}

		position = jail.Location.ToEntityPos();
		error = null;
		return true;
	}

	private TextCommandResult FormatNotes(ServerPlayerData targetData)
	{
		IReadOnlyList<StratumModerationRecord> notes = StratumModerationStore.GetNotes(targetData);
		if (notes.Count == 0)
		{
			return TextCommandResult.Success(StratumCommandText.Empty(targetData.LastKnownPlayername + " has no active staff notes."));
		}

		StringBuilder output = new StringBuilder(StratumCommandText.Title("Staff Notes: " + targetData.LastKnownPlayername));
		output.Append(StratumCommandText.Row("Active", notes.Count.ToString(GlobalConstants.DefaultCultureInfo)));
		foreach (StratumModerationRecord note in notes.OrderByDescending(record => record.CreatedUtc))
		{
			output.Append("\n").Append(StratumCommandText.Pill("#" + note.Id, StratumCommandText.Accent)).Append(" ");
			output.Append(StratumCommandText.Escape(FormatUtc(note.CreatedUtc))).Append(" by ").Append(StratumCommandText.Escape(note.ActorName));
			output.Append(StratumCommandText.Row("Note", note.Text));
		}

		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult FormatReports(string status)
	{
		IReadOnlyList<StratumReportEntry> reports = StratumReportStore.ListReports(status);
		if (reports.Count == 0)
		{
			return TextCommandResult.Success(StratumCommandText.Empty("No " + status + " reports."));
		}

		StringBuilder output = new StringBuilder(StratumCommandText.Title("Reports: " + status));
		output.Append(StratumCommandText.Row("Showing", Math.Min(20, reports.Count).ToString(GlobalConstants.DefaultCultureInfo) + " of " + reports.Count.ToString(GlobalConstants.DefaultCultureInfo)));
		foreach (StratumReportEntry report in reports.Take(20))
		{
			output.Append("\n").Append(StratumCommandText.Pill("#" + report.Id, StratumCommandText.Accent)).Append(" ");
			output.Append(StratumCommandText.Pill(report.Status, ReportStatusColor(report.Status))).Append(" ");
			output.Append(StratumCommandText.Escape(report.ReporterName)).Append(" -> ").Append(StratumCommandText.Escape(report.TargetName));
			output.Append(StratumCommandText.Row("Reason", TrimForList(report.Reason)));
		}

		if (reports.Count > 20)
		{
			output.Append(StratumCommandText.Row("More", (reports.Count - 20).ToString(GlobalConstants.DefaultCultureInfo) + " additional reports"));
		}

		return TextCommandResult.Success(output.ToString());
	}

	private static string FormatReport(StratumReportEntry report)
	{
		StringBuilder output = new StringBuilder(StratumCommandText.Title("Report #" + report.Id));
		output.Append(" ").Append(StratumCommandText.Pill(report.Status, ReportStatusColor(report.Status)));
		output.Append(StratumCommandText.Row("Created", FormatUtc(report.CreatedUtc)));
		output.Append(StratumCommandText.Row("Reporter", report.ReporterName));
		output.Append(StratumCommandText.Row("Target", report.TargetName));
		output.Append(StratumCommandText.Row("Reason", report.Reason));
		output.Append(StratumCommandText.Row("Claimed", report.ClaimedBy ?? "none"));
		output.Append(StratumCommandText.Row("Closed", report.ClosedBy == null ? "no" : report.ClosedBy + " at " + FormatUtc(report.ClosedUtc.Value)));
		output.Append(StratumCommandText.Row("Resolution", string.IsNullOrWhiteSpace(report.Resolution) ? "none" : report.Resolution));
		return output.ToString();
	}

	private static string ReportStatusColor(string status)
	{
		if (string.Equals(status, "closed", StringComparison.OrdinalIgnoreCase))
		{
			return StratumCommandText.Good;
		}

		if (string.Equals(status, "claimed", StringComparison.OrdinalIgnoreCase))
		{
			return StratumCommandText.Warn;
		}

		return StratumCommandText.Bad;
	}

	private void NotifyStaff(string message)
	{
		foreach (ConnectedClient client in server.Clients.Values)
		{
			if (client.State.IsAdmitted() && StratumCommandAccessCatalog.PlayerHasAccess(client.Player, StratumRuntime.Config.Commands.ReportManage))
			{
				Send(client.Player, message, EnumChatType.Notification);
			}
		}
	}

	private static bool TryParseMuteDuration(string durationText, out DateTime? expiresUtc, out string error)
	{
		expiresUtc = null;
		error = null;
		if (string.IsNullOrWhiteSpace(durationText) || string.Equals(durationText, "perm", StringComparison.OrdinalIgnoreCase) || string.Equals(durationText, "permanent", StringComparison.OrdinalIgnoreCase) || string.Equals(durationText, "forever", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (durationText.Length < 2 || !double.TryParse(durationText.Substring(0, durationText.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double amount) || amount <= 0)
		{
			error = "Duration must be permanent, or a positive value like 10m, 2h, 7d.";
			return false;
		}

		char unit = char.ToLowerInvariant(durationText[durationText.Length - 1]);
		TimeSpan duration;
		switch (unit)
		{
			case 'm':
				duration = TimeSpan.FromMinutes(amount);
				break;
			case 'h':
				duration = TimeSpan.FromHours(amount);
				break;
			case 'd':
				duration = TimeSpan.FromDays(amount);
				break;
			case 'w':
				duration = TimeSpan.FromDays(amount * 7);
				break;
			default:
				error = "Duration unit must be m, h, d, or w.";
				return false;
		}

		expiresUtc = DateTime.UtcNow.Add(duration);
		return true;
	}

	private static string TrimForList(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return string.Empty;
		}

		return value.Length <= 90 ? value : value.Substring(0, 87) + "...";
	}

	private static string EscapeLog(string value)
	{
		return (value ?? string.Empty).Replace("{", "{{").Replace("}", "}}");
	}

	private static string FormatOptionalReason(string reason)
	{
		return string.IsNullOrWhiteSpace(reason) ? string.Empty : " Reason: " + EscapeVtml(reason);
	}

	private static bool TryParseSeconds(string value, out int seconds, out string error)
	{
		seconds = 0;
		error = null;
		if (string.IsNullOrWhiteSpace(value))
		{
			error = "Duration must be a positive value like 10, 10s, 2m, or 1h.";
			return false;
		}

		string trimmed = value.Trim();
		char last = trimmed[trimmed.Length - 1];
		string numberText = char.IsLetter(last) ? trimmed.Substring(0, trimmed.Length - 1) : trimmed;
		if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out double amount) || amount <= 0)
		{
			error = "Duration must be a positive value like 10, 10s, 2m, or 1h.";
			return false;
		}

		double totalSeconds;
		switch (char.ToLowerInvariant(last))
		{
			case 's':
				totalSeconds = amount;
				break;
			case 'm':
				totalSeconds = amount * 60;
				break;
			case 'h':
				totalSeconds = amount * 3600;
				break;
			default:
				if (char.IsLetter(last))
				{
					error = "Duration unit must be s, m, or h.";
					return false;
				}

				totalSeconds = amount;
				break;
		}

		if (totalSeconds > int.MaxValue)
		{
			error = "Duration is too large.";
			return false;
		}

		seconds = Math.Max(1, (int)Math.Ceiling(totalSeconds));
		return true;
	}

	private ConnectedClient FindOnlineClientByName(string playerName)
	{
		return server.Clients.Values.FirstOrDefault(client => client.State.IsAdmitted() && string.Equals(client.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));
	}

	private ConnectedClient FindOnlineClientByUid(string playerUid)
	{
		return server.Clients.Values.FirstOrDefault(client => client.State.IsAdmitted() && client.Player?.PlayerUID == playerUid);
	}

	private List<ConnectedClient> ResolveOnlineClientList(string token)
	{
		var result = new List<ConnectedClient>();
		if (string.IsNullOrWhiteSpace(token)) return result;

		if (StratumTargetSelector.LooksLikeFriendlyToken(token))
		{
			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (var p in StratumTargetSelector.ResolveOnlinePlayers(token, (ICoreServerAPI)server.api))
			{
				if (!seen.Add(p.PlayerUID)) continue;
				var client = FindOnlineClientByUid(p.PlayerUID);
				if (client != null) result.Add(client);
			}
			return result;
		}

		var single = FindOnlineClientByName(token);
		if (single != null && single.State.IsAdmitted()) result.Add(single);
		return result;
	}

	private TextCommandResult RunForOnlineTargets(string token, System.Func<ConnectedClient, TextCommandResult> perTarget)
	{
		var targets = ResolveOnlineClientList(token);
		if (targets.Count == 0)
		{
			return TextCommandResult.Error("No matching online players for selector '" + token + "'.");
		}
		if (targets.Count == 1)
		{
			return perTarget(targets[0]);
		}

		int success = 0;
		var failures = new List<string>();
		foreach (var t in targets)
		{
			TextCommandResult r;
			try { r = perTarget(t); }
			catch (Exception ex)
			{
				failures.Add(t.PlayerName + " (" + ex.GetType().Name + ")");
				continue;
			}
			if (r != null && r.Status == EnumCommandStatus.Success) success++;
			else failures.Add(t.PlayerName);
		}

		string msg = StratumCommandText.Confirm("Applied", success + "/" + targets.Count + " players");
		if (failures.Count > 0) msg += " " + StratumCommandText.Warning("skipped: " + string.Join(", ", failures));
		return TextCommandResult.Success(msg);
	}

	private TextCommandResult RunForKnownTargets(string token, System.Func<ServerPlayerData, ConnectedClient, TextCommandResult> perTarget)
	{
		if (string.IsNullOrWhiteSpace(token))
		{
			return TextCommandResult.Error("No target specified.");
		}

		var sapi = (ICoreServerAPI)server.api;
		var resolved = new List<(ServerPlayerData data, ConnectedClient client)>();
		var seenUids = new HashSet<string>(StringComparer.Ordinal);
		var notFound = new List<string>();

		foreach (string raw in token.Split(','))
		{
			string part = raw.Trim();
			if (part.Length == 0) continue;

			if (part[0] == '@')
			{
				var players = StratumTargetSelector.ResolveOnlinePlayers(part, sapi);
				if (players.Count == 0) notFound.Add(part);
				foreach (var p in players)
				{
					if (!seenUids.Add(p.PlayerUID)) continue;
					var client = FindOnlineClientByUid(p.PlayerUID);
					var data = client?.ServerData ?? server.PlayerDataManager.GetOrCreateServerPlayerData(p.PlayerUID, p.PlayerName);
					if (data != null) resolved.Add((data, client));
				}
			}
			else
			{
				if (!TryGetKnownPlayerData(part, out var data, out var client))
				{
					notFound.Add(part);
					continue;
				}
				if (seenUids.Add(data.PlayerUID))
				{
					resolved.Add((data, client));
				}
			}
		}

		if (resolved.Count == 0)
		{
			string err = "No known players found.";
			if (notFound.Count > 0) err += " Unknown: " + string.Join(", ", notFound);
			return TextCommandResult.Error(err);
		}

		if (resolved.Count == 1 && notFound.Count == 0)
		{
			return perTarget(resolved[0].data, resolved[0].client);
		}

		int success = 0;
		var failures = new List<string>(notFound);
		foreach (var (data, client) in resolved)
		{
			TextCommandResult r;
			try { r = perTarget(data, client); }
			catch (Exception ex)
			{
				failures.Add(data.LastKnownPlayername + " (" + ex.GetType().Name + ")");
				continue;
			}
			if (r != null && r.Status == EnumCommandStatus.Success) success++;
			else failures.Add(data.LastKnownPlayername);
		}

		string msg = StratumCommandText.Confirm("Applied", success + "/" + resolved.Count + " players");
		if (failures.Count > 0) msg += " " + StratumCommandText.Warning("skipped: " + string.Join(", ", failures));
		return TextCommandResult.Success(msg);
	}

	private static IServerPlayer GetPlayer(TextCommandCallingArgs args)
	{
		return args.Caller.Player as IServerPlayer;
	}

	private static void Send(IServerPlayer player, string message, EnumChatType chatType)
	{
		player.SendMessage(GlobalConstants.GeneralChatGroup, message, chatType);
	}

	private static void StopMovement(EntityControls controls)
	{
		controls.Forward = false;
		controls.Backward = false;
		controls.Left = false;
		controls.Right = false;
		controls.Jump = false;
		controls.Up = false;
		controls.Down = false;
		controls.Sprint = false;
		controls.Gliding = false;
		controls.WalkVector.Set(0, 0, 0);
		controls.FlyVector.Set(0, 0, 0);
		controls.HandUse = EnumHandInteract.None;
	}

	private static double DistanceSquared(EntityPos a, EntityPos b)
	{
		double x = a.X - b.X;
		double y = a.Y - b.Y;
		double z = a.Z - b.Z;
		return x * x + y * y + z * z;
	}

	private static string FormatPosition(EntityPos pos)
	{
		return pos.X.ToString("0.#", GlobalConstants.DefaultCultureInfo) + ", " + pos.Y.ToString("0.#", GlobalConstants.DefaultCultureInfo) + ", " + pos.Z.ToString("0.#", GlobalConstants.DefaultCultureInfo) + " dim=" + pos.Dimension;
	}

	private static string FormatUtc(DateTime utc)
	{
		return utc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
	}

	private static string FormatDuration(TimeSpan duration)
	{
		if (duration.TotalDays >= 1)
		{
			return (int)duration.TotalDays + "d " + duration.Hours + "h " + duration.Minutes + "m";
		}

		if (duration.TotalHours >= 1)
		{
			return duration.Hours + "h " + duration.Minutes + "m";
		}

		return duration.Minutes + "m " + duration.Seconds + "s";
	}

	private static string EscapeVtml(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return string.Empty;
		}

		StringBuilder builder = new StringBuilder(value.Length);
		foreach (char character in value)
		{
			switch (character)
			{
				case '&':
					builder.Append("&amp;");
					break;
				case '<':
					builder.Append("&lt;");
					break;
				case '>':
					builder.Append("&gt;");
					break;
				default:
					builder.Append(character);
					break;
			}
		}

		return builder.ToString();
	}
}