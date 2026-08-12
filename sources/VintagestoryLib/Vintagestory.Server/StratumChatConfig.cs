using System;
using Vintagestory.API.Config;

namespace Vintagestory.Server;

internal class StratumChatConfig
{
	public const string DefaultGlobalDisabledMessage = "Global chat is disabled on this server.";

	public const string DefaultGroupsDisabledMessage = "Group chat is disabled on this server.";

	public bool Enabled { get; set; } = true;

	public bool LinkifyUrls { get; set; } = true;

	public StratumConnectionMessagesConfig ConnectionMessages { get; set; } = new StratumConnectionMessagesConfig();

	public StratumChatRateLimitConfig RateLimit { get; set; } = new StratumChatRateLimitConfig();

	public StratumChatChannelConfig Global { get; set; } = StratumChatChannelConfig.WithMessage(DefaultGlobalDisabledMessage);

	public StratumChatChannelConfig Groups { get; set; } = StratumChatChannelConfig.WithMessage(DefaultGroupsDisabledMessage);

	public void EnsurePopulated()
	{
		ConnectionMessages ??= new StratumConnectionMessagesConfig();
		RateLimit ??= new StratumChatRateLimitConfig();
		RateLimit.EnsureSane();
		Global ??= StratumChatChannelConfig.WithMessage(DefaultGlobalDisabledMessage);
		Groups ??= StratumChatChannelConfig.WithMessage(DefaultGroupsDisabledMessage);
		Global.EnsurePopulated(DefaultGlobalDisabledMessage);
		Groups.EnsurePopulated(DefaultGroupsDisabledMessage);
	}

	// Maps a chat channel id to the toggle that governs it. Returns null for the
	// negative server channels (ServerInfo, DamageLog, InfoLog, AllChatGroups),
	// which are server-to-client only and are never player chat. Deliberately
	// independent of Enabled/LinkifyUrls above: those gate formatting, not delivery,
	// and a formatting toggle should not silently re-enable a channel an operator
	// turned off.
	public StratumChatChannelConfig ChannelFor(int channelId)
	{
		if (channelId == GlobalConstants.GeneralChatGroup)
		{
			return Global;
		}

		return channelId > 0 ? Groups : null;
	}
}

internal class StratumChatChannelConfig
{
	public bool Enabled { get; set; } = true;

	// When the channel is disabled, holders of Commands.ChatControl can still post.
	public bool AllowStaffBypass { get; set; } = true;

	public string DisabledMessage { get; set; } = string.Empty;

	public static StratumChatChannelConfig WithMessage(string message)
	{
		return new StratumChatChannelConfig { DisabledMessage = message };
	}

	public void EnsurePopulated(string defaultMessage)
	{
		if (string.IsNullOrWhiteSpace(DisabledMessage))
		{
			DisabledMessage = defaultMessage;
		}
	}
}

internal class StratumConnectionMessagesConfig
{
	public bool ShowJoins { get; set; } = true;

	public bool ShowLeaves { get; set; } = true;

	public bool ShowDisconnects { get; set; } = true;
}

internal class StratumChatRateLimitConfig
{
	public bool Enabled { get; set; } = true;

	public int MinimumIntervalMs { get; set; } = 750;

	public bool DropDuplicates { get; set; } = true;

	public int DuplicateWindowMs { get; set; } = 3000;

	public bool ExemptCommands { get; set; } = true;

	public void EnsureSane()
	{
		MinimumIntervalMs = Math.Max(0, MinimumIntervalMs);
		DuplicateWindowMs = Math.Max(0, DuplicateWindowMs);
	}
}
