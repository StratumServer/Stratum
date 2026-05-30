using System;

namespace Vintagestory.Server;

internal readonly struct StratumDisconnectMessage
{
	public StratumDisconnectMessage(string playerMessage, string publicMessage = null, bool playerMessageIsScreen = false)
	{
		PlayerMessage = playerMessage;
		PublicMessage = publicMessage;
		PlayerMessageIsScreen = playerMessageIsScreen;
	}

	public string PlayerMessage { get; }

	public string PublicMessage { get; }

	public bool PlayerMessageIsScreen { get; }
}

internal static class StratumServerTheme
{
	public static string PlayerJoined(string playerName)
	{
		StratumThemeConfig theme = GetTheme();
		if (!theme.Enabled || !theme.StyleJoinLeaveMessages)
		{
			return null;
		}

		return "<font color=\"" + theme.GoodColor + "\"><strong>+ " + Escape(playerName) + "</strong></font> " +
			"<font color=\"" + theme.MutedColor + "\">joined the server</font>";
	}

	public static string PlayerLeft(string playerName)
	{
		StratumThemeConfig theme = GetTheme();
		if (!theme.Enabled || !theme.StyleJoinLeaveMessages)
		{
			return null;
		}

		return "<font color=\"" + theme.MutedColor + "\"><strong>- " + Escape(playerName) + "</strong> left the server</font>";
	}

	public static string PlayerRemoved(string playerName, string reason)
	{
		StratumThemeConfig theme = GetTheme();
		if (!theme.Enabled || !theme.StyleJoinLeaveMessages)
		{
			return null;
		}

		return "<font color=\"" + theme.WarnColor + "\"><strong>- " + Escape(playerName) + "</strong></font> " +
			"<font color=\"" + theme.MutedColor + "\">was removed</font> " +
			"<font color=\"" + theme.LabelColor + "\">" + Escape(reason) + "</font>";
	}

	public static string Welcome(string playerName, string message)
	{
		StratumThemeConfig theme = GetTheme();
		if (!theme.Enabled || !theme.StyleWelcomeMessages)
		{
			return message;
		}

		return "<font color=\"" + theme.AccentColor + "\"><strong>Welcome, " + Escape(playerName) + "</strong></font>" +
			"<br><font color=\"" + theme.LabelColor + "\">" + Escape(message) + "</font>";
	}

	public static string DisconnectScreen(string reason, string serverName = null)
	{
		StratumThemeConfig theme = GetTheme();
		if (!theme.Enabled || !theme.StyleDisconnectScreens || string.IsNullOrWhiteSpace(reason))
		{
			return reason;
		}

		return BuildDisconnectScreen(theme, "Connection closed", reason.Trim(), "You can reconnect when this has been resolved.", serverName);
	}

	public static StratumDisconnectMessage InvalidPassword()
	{
		return Disconnect("Invalid password", "The password entered for this server did not match.", "Check the server password and try again.");
	}

	public static StratumDisconnectMessage WhitelistRequired()
	{
		return Disconnect("Whitelist required", "This server only allows approved players to join.", "Ask the server staff to add you to the whitelist.");
	}

	public static StratumDisconnectMessage WhitelistExpired(DateTime untilDate)
	{
		return Disconnect("Whitelist expired", "Your whitelist entry expired on " + untilDate.ToUniversalTime().ToString("u") + ".", "Ask the server staff to renew your access.");
	}

	public static StratumDisconnectMessage InvalidJoinData()
	{
		return Disconnect("Invalid join data", "The client did not send the information required to join.", "Restart the game and try again.");
	}

	public static StratumDisconnectMessage WrongVersion(string clientGameVersion, string clientNetworkVersion, string serverGameVersion, string serverNetworkVersion)
	{
		return Disconnect("Version mismatch", "Client: " + EmptyText(clientGameVersion) + " / network " + EmptyText(clientNetworkVersion) + ". Server: " + serverGameVersion + " / network " + serverNetworkVersion + ".", "Use a matching Vintage Story version for this server.");
	}

	public static StratumDisconnectMessage Banned(string issuedBy, DateTime untilDate, string reason)
	{
		string detail = "This account is banned until " + untilDate.ToUniversalTime().ToString("u") + ".";
		if (!string.IsNullOrWhiteSpace(issuedBy))
		{
			detail += " Issued by " + issuedBy.Trim() + ".";
		}

		string action = string.IsNullOrWhiteSpace(reason) ? "Contact server staff if you believe this is incorrect." : "Reason: " + reason.Trim();
		return Disconnect("Account banned", detail, action);
	}

	public static StratumDisconnectMessage InvalidPlayerName(string playerName)
	{
		string detail = string.IsNullOrWhiteSpace(playerName) ? "No player name was sent." : "The player name '" + playerName.Trim() + "' is not allowed.";
		return Disconnect("Invalid player name", detail, "Use 1-16 letters, numbers, underscores, or hyphens.");
	}

	public static StratumDisconnectMessage ServerFull(int maxClients, int maxQueue)
	{
		if (maxQueue > 0)
		{
			return Disconnect("Queue is full", "The server is at " + maxClients + " players and the waiting queue has " + maxQueue + " slots.", "Please try again in a few minutes.");
		}

		return Disconnect("Server is full", "The server is already at its " + maxClients + " player limit.", "Please try again when a slot opens.");
	}

	public static StratumDisconnectMessage AuthRejected(string detail)
	{
		return Disconnect("Account verification failed", string.IsNullOrWhiteSpace(detail) ? "The auth server rejected this session." : detail.Trim(), "Check your account session and try again.");
	}

	public static StratumDisconnectMessage AuthUnavailable()
	{
		return Disconnect("Auth server unavailable", "The server could not verify your game session right now.", "Please try again later. Server owners should check server-main.log and server-debug.log.");
	}

	private static StratumThemeConfig GetTheme()
	{
		StratumConfig config = StratumRuntime.Config;
		config.EnsurePopulated();
		return config.Theme;
	}

	private static StratumDisconnectMessage Disconnect(string title, string detail, string action = null, string publicMessage = null)
	{
		return new StratumDisconnectMessage(DisconnectScreen(title, detail, action), publicMessage, playerMessageIsScreen: true);
	}

	private static string DisconnectScreen(string title, string detail, string action)
	{
		StratumThemeConfig theme = GetTheme();
		if (!theme.Enabled || !theme.StyleDisconnectScreens)
		{
			return PlainDisconnectText(title, detail, action);
		}

		return BuildDisconnectScreen(theme, title, detail, action, null);
	}

	private static string BuildDisconnectScreen(StratumThemeConfig theme, string title, string detail, string action, string serverName)
	{
		string displayName = string.IsNullOrWhiteSpace(serverName) ? theme.BrandName : serverName.Trim();
		string message = "<font size=\"21\" color=\"" + theme.AccentColor + "\"><strong>" + Escape(displayName) + "</strong></font>" +
			"<br><font size=\"15\" color=\"" + theme.WarnColor + "\"><strong>" + Escape(EmptyText(title)) + "</strong></font>";
		if (!string.IsNullOrWhiteSpace(detail))
		{
			message += "<br><br><font size=\"13\" color=\"" + theme.MutedColor + "\">Reason</font>" +
				"<br><font size=\"15\" color=\"" + theme.LabelColor + "\">" + Escape(detail.Trim()) + "</font>";
		}
		if (!string.IsNullOrWhiteSpace(action))
		{
			message += "<br><br><font size=\"13\" color=\"" + theme.MutedColor + "\">" + Escape(action.Trim()) + "</font>";
		}

		return message;
	}

	private static string PlainDisconnectText(string title, string detail, string action)
	{
		string message = EmptyText(title);
		if (!string.IsNullOrWhiteSpace(detail))
		{
			message += "\n" + detail.Trim();
		}
		if (!string.IsNullOrWhiteSpace(action))
		{
			message += "\n\n" + action.Trim();
		}

		return message;
	}

	private static string EmptyText(string value)
	{
		return string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
	}

	private static string Escape(string value)
	{
		return StratumCommandText.Escape(value ?? string.Empty);
	}
}
