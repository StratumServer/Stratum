using System;

namespace Vintagestory.Server;

internal class StratumChatConfig
{
	public bool Enabled { get; set; } = true;

	public bool LinkifyUrls { get; set; } = true;

	public StratumConnectionMessagesConfig ConnectionMessages { get; set; } = new StratumConnectionMessagesConfig();

	public StratumChatRateLimitConfig RateLimit { get; set; } = new StratumChatRateLimitConfig();

	public void EnsurePopulated()
	{
		ConnectionMessages ??= new StratumConnectionMessagesConfig();
		RateLimit ??= new StratumChatRateLimitConfig();
		RateLimit.EnsureSane();
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
