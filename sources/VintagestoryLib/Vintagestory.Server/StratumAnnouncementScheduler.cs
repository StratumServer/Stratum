using System;
using Vintagestory.API.Common;

namespace Vintagestory.Server;

/// <summary>
/// Broadcasts configured messages to all players at a fixed interval.
/// Messages rotate sequentially. Skips broadcast when no players are online.
/// </summary>
internal sealed class StratumAnnouncementScheduler
{
	private readonly ServerMain server;
	private long lastBroadcastMs;
	private int nextIndex;

	public StratumAnnouncementScheduler(ServerMain server)
	{
		this.server = server;
		lastBroadcastMs = server.ElapsedMilliseconds;
		server.RegisterGameTickListener(OnTick, 10_000);
	}

	private void OnTick(float dt)
	{
		StratumAnnouncementsConfig cfg = StratumRuntime.Config.Announcements;
		if (cfg == null || !cfg.Enabled || cfg.Messages == null || cfg.Messages.Length == 0)
		{
			return;
		}

		if (server.AllOnlinePlayers.Length == 0)
		{
			return;
		}

		long elapsedMs = server.ElapsedMilliseconds;
		long intervalMs = (long)Math.Max(10, cfg.IntervalSeconds) * 1000L;

		if (elapsedMs - lastBroadcastMs < intervalMs)
		{
			return;
		}

		lastBroadcastMs = elapsedMs;

		if (cfg.RandomOrder)
		{
			nextIndex = server.rand.Value.Next(cfg.Messages.Length);
		}

		string message = cfg.Messages[nextIndex % cfg.Messages.Length];

		if (!cfg.RandomOrder)
		{
			nextIndex = (nextIndex + 1) % cfg.Messages.Length;
		}

		string formatted = cfg.Prefix + message + cfg.Suffix;
		server.BroadcastMessageToAllGroups(formatted, EnumChatType.Notification);
	}
}
