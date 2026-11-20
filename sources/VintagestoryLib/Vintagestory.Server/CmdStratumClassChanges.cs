using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

/// <summary>
/// #278: in-game class change requests. /class change switches the caller's class straight away
/// while they have self-service changes left (ClassChangeSettings.SelfServiceChanges), and files a
/// request once they run out. Holders of Commands.ClassChangeManage see new requests as they
/// arrive and settle them with /classrequests. An approval takes effect at once for an online
/// player, and on their next join otherwise. The class swap itself is vanilla's
/// CharacterSystem.setCharacterClass, reached through StratumCharacterClassHook, and like a
/// vanilla reselection outside creative mode it swaps traits but hands out no starting gear.
/// </summary>
internal class CmdStratumClassChanges
{
	private static readonly string[] ClassActions = { "info", "list", "change", "cancel" };

	private static readonly string[] RequestActions = { "list", "info", "approve", "deny" };

	private const int ListLimit = 20;

	private readonly ServerMain server;

	public CmdStratumClassChanges(ServerMain server)
	{
		this.server = server;
		StratumRuntime.Config.EnsurePopulated();
		server.EventManager.OnPlayerJoin += OnPlayerJoin;

		if (!StratumRuntime.Config.Commands.Enabled) return;

		CommandArgumentParsers parsers = server.api.commandapi.Parsers;
		StratumCommandsConfig commands = StratumRuntime.Config.Commands;

		if (StratumCommandRegistration.ShouldRegister(commands.ClassChange, "/class", "Commands.ClassChange"))
		{
			server.api.commandapi.Create("class")
				.WithDescription("Show your class, change it, or request a change from staff")
				.WithAdditionalInformation("/class shows your class, your remaining self-service changes and any open request. /class list shows the classes. /class change &lt;class&gt; [reason] switches class while you have self-service changes left, and otherwise sends staff a request. /class cancel withdraws your open request.")
				.WithArgs(parsers.OptionalWordRange("action", ClassActions), parsers.OptionalWord("class"), parsers.OptionalAll("reason"))
				.RequiresPrivilege(Privilege.chat)
				.HandleWith(HandleClass);
		}

		if (StratumCommandRegistration.ShouldRegister(commands.ClassChangeManage, "/classrequests", "Commands.ClassChangeManage"))
		{
			server.api.commandapi.Create("classrequests")
				.WithDescription("Approve or deny class change requests")
				.WithAdditionalInformation("Actions: list [pending|approved|denied|cancelled|all], info &lt;id&gt;, approve &lt;id&gt; [note], deny &lt;id&gt; [reason].")
				.WithArgs(parsers.OptionalWordRange("action", RequestActions), parsers.OptionalWord("id or status"), parsers.OptionalAll("note"))
				.RequiresPrivilege(Privilege.chat)
				.HandleWith(HandleRequests);
		}
	}

	private TextCommandResult HandleClass(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, "class", StratumRuntime.Config.Commands.ClassChange, out TextCommandResult failure))
		{
			return failure;
		}

		if (args.Caller.Player is not IServerPlayer player || player.Entity == null || player.ServerData is not ServerPlayerData playerData)
		{
			return TextCommandResult.Error("Only a connected player can use /class.");
		}

		if (!TryGetClasses(out IReadOnlyList<string> classes, out failure))
		{
			return failure;
		}

		string action = args[0] as string ?? "info";
		switch (action)
		{
			case "list":
				return ListClasses(classes, GetCurrentClass(player));
			case "change":
				return ChangeClass(player, playerData, classes, args[1] as string, args[2] as string);
			case "cancel":
				return CancelRequest(player);
			default:
				return ShowStatus(player, playerData);
		}
	}

	private TextCommandResult ShowStatus(IServerPlayer player, ServerPlayerData playerData)
	{
		StringBuilder output = new StringBuilder(StratumCommandText.Title("Your class"));
		output.Append(StratumCommandText.Row("Class", ClassLabel(GetCurrentClass(player))));
		output.Append(StratumCommandText.Row("Self-service changes left", FormatRemaining(playerData)));

		StratumClassChangeRequest pending = StratumClassChangeStore.GetPending(player.PlayerUID);
		output.Append(StratumCommandText.Row("Open request", pending == null ? "none" : "#" + pending.Id + " to " + ClassLabel(pending.RequestedClass)));
		return TextCommandResult.Success(output.ToString());
	}

	private static TextCommandResult ListClasses(IReadOnlyList<string> classes, string currentClass)
	{
		StringBuilder output = new StringBuilder(StratumCommandText.Title("Classes"));
		foreach (string code in classes)
		{
			output.Append(StratumCommandText.Bullet(code, ClassName(code) + (code == currentClass ? " (current)" : string.Empty)));
		}

		return TextCommandResult.Success(output.ToString());
	}

	private TextCommandResult ChangeClass(IServerPlayer player, ServerPlayerData playerData, IReadOnlyList<string> classes, string requested, string reason)
	{
		if (string.IsNullOrWhiteSpace(requested))
		{
			return TextCommandResult.Error("Usage: /class change &lt;class&gt; [reason]. /class list shows the classes.");
		}

		string classCode = classes.FirstOrDefault(code => string.Equals(code, requested, StringComparison.OrdinalIgnoreCase));
		if (classCode == null)
		{
			return TextCommandResult.Error("No class " + StratumCommandText.Escape(requested) + ". /class list shows the classes.");
		}

		// Before the first character creation the player has no class to change, and vanilla
		// resets them to the default one on join anyway. The creation dialog is the way in.
		if (!player.GetModData("createCharacter", false))
		{
			return TextCommandResult.Error("Finish creating your character first.");
		}

		string currentClass = GetCurrentClass(player);
		if (classCode == currentClass)
		{
			return TextCommandResult.Error("You are already a " + StratumCommandText.Escape(ClassName(classCode)) + ".");
		}

		reason = reason?.Trim() ?? string.Empty;
		int maxReasonLength = StratumRuntime.Config.Commands.ClassChangeSettings.MaxReasonLength;
		if (reason.Length > maxReasonLength)
		{
			return TextCommandResult.Error("Keep the reason under " + maxReasonLength.ToString(CultureInfo.InvariantCulture) + " characters.");
		}

		if (HasSelfServiceChangeLeft(playerData))
		{
			if (!TryApplyClass(player, classCode, out string error))
			{
				return TextCommandResult.Error(error);
			}

			StratumClassChangeStore.AddSelfServiceUsed(server, playerData);
			StratumClassChangeRequest superseded = StratumClassChangeStore.CancelPending(player.PlayerUID);
			StratumRuntime.LogAudit("class change self-service player=" + player.PlayerName + " from=" + currentClass + " to=" + classCode + (superseded == null ? string.Empty : " cancelledRequest=" + superseded.Id), true);
			return TextCommandResult.Success(StratumCommandText.Confirm("Class changed", "You are now a " + ClassName(classCode) + ". Self-service changes left: " + FormatRemaining(playerData) + "."));
		}

		StratumClassChangeRequest request = StratumClassChangeStore.AddRequest(player.PlayerUID, player.PlayerName, currentClass, classCode, reason, out StratumClassChangeRequest replaced);
		NotifyApprovers(StratumCommandText.Pill("Class request #" + request.Id, StratumCommandText.Warn) + " "
			+ StratumCommandText.Escape(player.PlayerName) + " asks to change from " + StratumCommandText.Escape(ClassLabel(currentClass))
			+ " to " + StratumCommandText.Escape(ClassLabel(classCode))
			+ (reason.Length == 0 ? string.Empty : ": " + StratumCommandText.Escape(reason))
			+ ". /classrequests approve|deny " + request.Id.ToString(CultureInfo.InvariantCulture));
		StratumRuntime.LogAudit("class request id=" + request.Id + " player=" + player.PlayerName + " from=" + currentClass + " to=" + classCode + " reason=" + StratumCommandText.AuditValue(reason) + (replaced == null ? string.Empty : " replaced=" + replaced.Id), true);

		string detail = "Request #" + request.Id + " to become a " + ClassName(classCode) + " was sent to staff.";
		if (replaced != null)
		{
			detail += " It replaces request #" + replaced.Id + ".";
		}
		return TextCommandResult.Success(StratumCommandText.Confirm("Request sent", detail));
	}

	private static TextCommandResult CancelRequest(IServerPlayer player)
	{
		StratumClassChangeRequest request = StratumClassChangeStore.CancelPending(player.PlayerUID);
		if (request == null)
		{
			return TextCommandResult.Error("You have no open class change request.");
		}

		StratumRuntime.LogAudit("class request cancel id=" + request.Id + " player=" + player.PlayerName, true);
		return TextCommandResult.Success(StratumCommandText.Confirm("Request withdrawn", "#" + request.Id + "."));
	}

	private TextCommandResult HandleRequests(TextCommandCallingArgs args)
	{
		if (!CheckAccess(args, "classrequests", StratumRuntime.Config.Commands.ClassChangeManage, out TextCommandResult failure))
		{
			return failure;
		}

		string action = args[0] as string ?? "list";
		string selector = args[1] as string;
		string note = (args[2] as string)?.Trim() ?? string.Empty;

		if (action == "list")
		{
			return FormatRequests(string.IsNullOrWhiteSpace(selector) ? StratumClassChangeStore.StatusPending : selector);
		}

		if (!int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out int requestId))
		{
			return TextCommandResult.Error("Usage: /classrequests " + action + " &lt;id&gt;" + (action == "info" ? string.Empty : " [note]"));
		}

		if (!StratumClassChangeStore.TryGetRequest(requestId, out StratumClassChangeRequest request))
		{
			return TextCommandResult.Error("No class change request #" + requestId + ".");
		}

		if (action == "info")
		{
			return TextCommandResult.Success(FormatRequest(request));
		}

		if (request.Status != StratumClassChangeStore.StatusPending)
		{
			return TextCommandResult.Error("Request #" + requestId + " is already " + request.Status + ".");
		}

		string actorName = args.Caller.GetName();
		if (action == "deny")
		{
			StratumClassChangeStore.Decide(requestId, StratumClassChangeStore.StatusDenied, actorName, note, out request);
			StratumRuntime.LogAudit("class request deny id=" + request.Id + " player=" + request.PlayerName + " actor=" + actorName + " note=" + StratumCommandText.AuditValue(note), true);
			IServerPlayer deniedPlayer = GetOnlinePlayer(request.PlayerUid);
			if (deniedPlayer != null)
			{
				DeliverDecision(deniedPlayer, request);
			}
			return TextCommandResult.Success(StratumCommandText.Confirm("Denied request", "#" + request.Id + " from " + request.PlayerName + "."));
		}

		if (!TryGetClasses(out IReadOnlyList<string> classes, out failure))
		{
			return failure;
		}

		if (!classes.Contains(request.RequestedClass))
		{
			return TextCommandResult.Error("Class " + StratumCommandText.Escape(request.RequestedClass) + " no longer exists on this server. Deny the request instead.");
		}

		StratumClassChangeStore.Decide(requestId, StratumClassChangeStore.StatusApproved, actorName, note, out request);
		StratumRuntime.LogAudit("class request approve id=" + request.Id + " player=" + request.PlayerName + " to=" + request.RequestedClass + " actor=" + actorName + " note=" + StratumCommandText.AuditValue(note), true);

		IServerPlayer approvedPlayer = GetOnlinePlayer(request.PlayerUid);
		if (approvedPlayer == null)
		{
			return TextCommandResult.Success(StratumCommandText.Confirm("Approved request", "#" + request.Id + ". " + request.PlayerName + " is offline and becomes a " + ClassName(request.RequestedClass) + " on their next join."));
		}

		return DeliverDecision(approvedPlayer, request)
			? TextCommandResult.Success(StratumCommandText.Confirm("Approved request", "#" + request.Id + ". " + request.PlayerName + " is now a " + ClassName(request.RequestedClass) + "."))
			: TextCommandResult.Error("Approved request #" + request.Id + ", but the class change failed. See the server log.");
	}

	private void OnPlayerJoin(IServerPlayer player)
	{
		if (player?.Entity == null)
		{
			return;
		}

		foreach (StratumClassChangeRequest request in StratumClassChangeStore.ListUndelivered(player.PlayerUID))
		{
			DeliverDecision(player, request);
		}

		if (StratumCommandAccessCatalog.PlayerHasAccess(player, StratumRuntime.Config.Commands.ClassChangeManage))
		{
			int pending = StratumClassChangeStore.ListRequests(StratumClassChangeStore.StatusPending).Count;
			if (pending > 0)
			{
				Send(player, StratumCommandText.Pill("Class requests", StratumCommandText.Warn) + " " + pending.ToString(CultureInfo.InvariantCulture) + " pending. /classrequests lists them.");
			}
		}
	}

	// Tells the player how staff settled their request and, for an approval, applies the class.
	// Returns false only when an approved class could not be applied.
	private bool DeliverDecision(IServerPlayer player, StratumClassChangeRequest request)
	{
		string note = string.IsNullOrWhiteSpace(request.Note) ? string.Empty : " Note: " + StratumCommandText.Escape(request.Note);
		if (request.Status == StratumClassChangeStore.StatusDenied)
		{
			Send(player, StratumCommandText.Danger("Class request #" + request.Id + " denied.") + note);
			StratumClassChangeStore.MarkDelivered(request, false);
			return true;
		}

		bool classExists = TryGetClasses(out IReadOnlyList<string> classes, out _) && classes.Contains(request.RequestedClass);
		if (!classExists || !TryApplyClass(player, request.RequestedClass, out _))
		{
			// Not retried on every join: a class removed from the server will not come back.
			Send(player, StratumCommandText.Warning("Class request #" + request.Id + " was approved, but the class could not be applied. Ask staff for help."));
			StratumClassChangeStore.MarkDelivered(request, false);
			return false;
		}

		Send(player, StratumCommandText.Success("Class request #" + request.Id + " approved.") + " You are now a " + StratumCommandText.Escape(ClassName(request.RequestedClass)) + "." + note);
		StratumClassChangeStore.MarkDelivered(request, true);
		return true;
	}

	private bool TryApplyClass(IServerPlayer player, string classCode, out string error)
	{
		error = null;
		try
		{
			StratumCharacterClassHook.ApplyClass(player, classCode);
		}
		catch (Exception exception)
		{
			StratumRuntime.LogWarning("class change to " + classCode + " failed for " + player.PlayerName + ": " + exception);
			error = "The class change failed. See the server log.";
			return false;
		}

		// Same stamp and format as vanilla's character selection, so allowClassChangeAfterMonths
		// counts from this change.
		DateTime now = DateTime.UtcNow;
		player.ServerData.LastCharacterSelectionDate = now.ToShortDateString() + " " + now.ToShortTimeString();
		server.PlayerDataManager.playerDataDirty = true;
		return true;
	}

	private TextCommandResult FormatRequests(string status)
	{
		IReadOnlyList<StratumClassChangeRequest> requests = StratumClassChangeStore.ListRequests(status);
		if (requests.Count == 0)
		{
			return TextCommandResult.Success(StratumCommandText.Empty("No " + status + " class change requests."));
		}

		StringBuilder output = new StringBuilder(StratumCommandText.Title("Class requests: " + status));
		output.Append(StratumCommandText.Row("Showing", Math.Min(ListLimit, requests.Count).ToString(CultureInfo.InvariantCulture) + " of " + requests.Count.ToString(CultureInfo.InvariantCulture)));
		foreach (StratumClassChangeRequest request in requests.Take(ListLimit))
		{
			output.Append("\n").Append(StratumCommandText.Pill("#" + request.Id, StratumCommandText.Accent)).Append(" ");
			output.Append(StratumCommandText.Pill(request.Status, StatusColor(request.Status))).Append(" ");
			output.Append(StratumCommandText.Escape(request.PlayerName + ": " + ClassLabel(request.CurrentClass) + " -> " + ClassLabel(request.RequestedClass)));
			if (!string.IsNullOrWhiteSpace(request.Reason))
			{
				output.Append(StratumCommandText.Row("Reason", TrimForList(request.Reason)));
			}
		}

		return TextCommandResult.Success(output.ToString());
	}

	private static string FormatRequest(StratumClassChangeRequest request)
	{
		StringBuilder output = new StringBuilder(StratumCommandText.Title("Class request #" + request.Id));
		output.Append(" ").Append(StratumCommandText.Pill(request.Status, StatusColor(request.Status)));
		output.Append(StratumCommandText.Row("Created", FormatUtc(request.CreatedUtc)));
		output.Append(StratumCommandText.Row("Player", request.PlayerName));
		output.Append(StratumCommandText.Row("Current class", ClassLabel(request.CurrentClass)));
		output.Append(StratumCommandText.Row("Requested class", ClassLabel(request.RequestedClass)));
		output.Append(StratumCommandText.Row("Reason", string.IsNullOrWhiteSpace(request.Reason) ? "none" : request.Reason));
		if (request.DecidedUtc != null)
		{
			output.Append(StratumCommandText.Row("Closed", (request.DecidedBy ?? request.PlayerName) + " at " + FormatUtc(request.DecidedUtc.Value)));
			output.Append(StratumCommandText.Row("Note", string.IsNullOrWhiteSpace(request.Note) ? "none" : request.Note));
		}
		if (request.Status == StratumClassChangeStore.StatusApproved)
		{
			output.Append(StratumCommandText.Row("Applied", request.Applied ? FormatUtc(request.AppliedUtc ?? request.DecidedUtc.Value) : request.PlayerNotified ? "failed" : "on next join"));
		}

		return output.ToString();
	}

	private void NotifyApprovers(string message)
	{
		foreach (ConnectedClient client in server.Clients.Values)
		{
			if (client.State.IsAdmitted() && StratumCommandAccessCatalog.PlayerHasAccess(client.Player, StratumRuntime.Config.Commands.ClassChangeManage))
			{
				Send(client.Player, message);
			}
		}
	}

	private IServerPlayer GetOnlinePlayer(string playerUid)
	{
		ConnectedClient client = server.GetClientByUID(playerUid);
		return client != null && client.State.IsAdmitted() && client.Player?.Entity != null ? client.Player : null;
	}

	private static bool TryGetClasses(out IReadOnlyList<string> classes, out TextCommandResult failure)
	{
		classes = StratumCharacterClassHook.ListClasses?.Invoke();
		failure = null;
		if (classes == null || classes.Count == 0 || StratumCharacterClassHook.ApplyClass == null)
		{
			failure = TextCommandResult.Error("Character classes are not available on this server.");
			return false;
		}

		return true;
	}

	private static bool HasSelfServiceChangeLeft(ServerPlayerData playerData)
	{
		int limit = StratumRuntime.Config.Commands.ClassChangeSettings.SelfServiceChanges;
		return limit < 0 || StratumClassChangeStore.GetSelfServiceUsed(playerData) < limit;
	}

	private static string FormatRemaining(ServerPlayerData playerData)
	{
		int limit = StratumRuntime.Config.Commands.ClassChangeSettings.SelfServiceChanges;
		if (limit < 0)
		{
			return "unlimited";
		}

		return Math.Max(0, limit - StratumClassChangeStore.GetSelfServiceUsed(playerData)).ToString(CultureInfo.InvariantCulture);
	}

	private static string GetCurrentClass(IServerPlayer player)
	{
		return player.Entity.WatchedAttributes.GetString("characterClass");
	}

	private static string ClassName(string classCode)
	{
		if (string.IsNullOrEmpty(classCode))
		{
			return "none";
		}

		string key = "characterclass-" + classCode;
		return Lang.HasTranslation(key, true, false) ? Lang.Get(key) : classCode;
	}

	private static string ClassLabel(string classCode)
	{
		string name = ClassName(classCode);
		return name == classCode || string.IsNullOrEmpty(classCode) ? name : name + " (" + classCode + ")";
	}

	private static string StatusColor(string status)
	{
		return status switch
		{
			StratumClassChangeStore.StatusPending => StratumCommandText.Warn,
			StratumClassChangeStore.StatusApproved => StratumCommandText.Good,
			StratumClassChangeStore.StatusDenied => StratumCommandText.Bad,
			_ => StratumCommandText.Muted
		};
	}

	private static string TrimForList(string value)
	{
		return value.Length <= 90 ? value : value.Substring(0, 87) + "...";
	}

	private static string FormatUtc(DateTime utc)
	{
		return utc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
	}

	private static void Send(IServerPlayer player, string message)
	{
		player.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);
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
