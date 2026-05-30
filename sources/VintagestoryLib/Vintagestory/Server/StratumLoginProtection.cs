using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

/// <summary>
/// Login / spawn protection: when a player joins the world they are made invulnerable
/// for <see cref="StratumLoginProtectionConfig.ProtectionSeconds"/> seconds, until they
/// move past <see cref="StratumLoginProtectionConfig.MoveThresholdBlocks"/>, or until
/// they are on fire / standing in lava (configurable).
///
/// Implemented on top of the engine's existing "invulnerable" activity timer
/// (<see cref="Vintagestory.API.Common.Entities.Entity.SetActivityRunning"/>),
/// which is already honored by <c>Entity.ReceiveDamage</c>.
/// </summary>
internal sealed class StratumLoginProtection
{
	private readonly ServerMain server;
	private readonly Dictionary<string, ProtectionState> activeByUid = new Dictionary<string, ProtectionState>(System.StringComparer.Ordinal);

	private const int TickIntervalMs = 250;
	private const int RefreshLeadMs = 500; // refresh activity slightly before it expires so it never lapses early

	public StratumLoginProtection(ServerMain server)
	{
		this.server = server;
		server.EventManager.OnPlayerJoin += OnPlayerJoin;
		server.EventManager.OnPlayerDisconnect += OnPlayerDisconnect;
		server.RegisterGameTickListener(OnTick, TickIntervalMs);
	}

	private StratumLoginProtectionConfig Cfg => StratumRuntime.Config?.LoginProtection;

	private void OnPlayerJoin(IServerPlayer player)
	{
		StratumLoginProtectionConfig cfg = Cfg;
		if (cfg == null || !cfg.Enabled || cfg.ProtectionSeconds <= 0) return;
		if (player?.Entity?.Pos == null) return;

		if (cfg.CancelInFireOrLava && (player.Entity.IsOnFire || player.Entity.InLava))
		{
			return;
		}

		long durationMs = cfg.ProtectionSeconds * 1000L;
		player.Entity.SetActivityRunning("invulnerable", (int)durationMs);

		activeByUid[player.PlayerUID] = new ProtectionState
		{
			StartX = player.Entity.Pos.X,
			StartZ = player.Entity.Pos.Z,
			ExpiresAtMs = server.ElapsedMilliseconds + durationMs,
			RemainingSeconds = cfg.ProtectionSeconds
		};

		if (cfg.AnnounceOnStart)
		{
			player.SendMessage(GlobalConstants.GeneralChatGroup, string.Format(cfg.StartMessage, cfg.ProtectionSeconds), EnumChatType.Notification);
		}

		StratumRuntime.LogInfo("login protection: " + player.PlayerName + " protected for " + cfg.ProtectionSeconds + "s");
	}

	private void OnPlayerDisconnect(IServerPlayer player)
	{
		if (player == null) return;
		activeByUid.Remove(player.PlayerUID);
	}

	private void OnTick(float dt)
	{
		StratumLoginProtectionConfig cfg = Cfg;
		if (cfg == null || activeByUid.Count == 0) return;

		long nowMs = server.ElapsedMilliseconds;
		List<string> toEnd = null;

		foreach (KeyValuePair<string, ProtectionState> kv in activeByUid)
		{
			string uid = kv.Key;
			ProtectionState state = kv.Value;

			IServerPlayer player = server.PlayerByUid(uid) as IServerPlayer;
			if (player?.Entity?.Pos == null || player.ConnectionState != EnumClientState.Playing)
			{
				(toEnd ??= new List<string>()).Add(uid);
				continue;
			}

			if (nowMs >= state.ExpiresAtMs)
			{
				(toEnd ??= new List<string>()).Add(uid);
				EndProtection(player, cfg, "expired");
				continue;
			}

			if (cfg.CancelInFireOrLava && (player.Entity.IsOnFire || player.Entity.InLava))
			{
				(toEnd ??= new List<string>()).Add(uid);
				EndProtection(player, cfg, "fire/lava");
				continue;
			}

			if (cfg.CancelOnHorizontalMove)
			{
				double dx = player.Entity.Pos.X - state.StartX;
				double dz = player.Entity.Pos.Z - state.StartZ;
				double thr = cfg.MoveThresholdBlocks;
				if (dx * dx + dz * dz > thr * thr)
				{
					(toEnd ??= new List<string>()).Add(uid);
					EndProtection(player, cfg, "moved");
					continue;
				}
			}

			// Refresh engine invulnerable timer so it never lapses before our ExpiresAtMs
			long remainingMs = state.ExpiresAtMs - nowMs;
			if (remainingMs > 0)
			{
				player.Entity.SetActivityRunning("invulnerable", (int)(remainingMs + RefreshLeadMs));
			}
		}

		if (toEnd != null)
		{
			foreach (string uid in toEnd)
			{
				activeByUid.Remove(uid);
			}
		}
	}

	private static void EndProtection(IServerPlayer player, StratumLoginProtectionConfig cfg, string reason)
	{
		// Zero the activity timer so the next ReceiveDamage call is no longer gated.
		player.Entity.SetActivityRunning("invulnerable", 0);

		if (cfg.AnnounceOnEnd)
		{
			player.SendMessage(GlobalConstants.GeneralChatGroup, cfg.EndMessage, EnumChatType.Notification);
		}

		StratumRuntime.LogInfo("login protection: " + player.PlayerName + " ended (" + reason + ")");
	}

	private sealed class ProtectionState
	{
		public double StartX;
		public double StartZ;
		public long ExpiresAtMs;
		public int RemainingSeconds;
	}
}
