using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

// PvP combat log prevention. Tags players who engage in PvP for a configurable window.
// Disconnecting while tagged applies the configured penalty (kill on disconnect, kill on
// rejoin, or log-only). Opt-in, disabled by default.
//
// Hook points:
//   - OnPlayerInteractEntity (mode == 0): covers melee attacks.
//   - EntityProjectileBase.ImpactOnEntity via a patched callback: covers arrows, thrown
//     stones, spears, and any projectile with a player CauseEntity.
//
// The melee hook fires on the attack attempt (before damage lands). This matches industry
// convention: swing intent = combat. The projectile hook fires after impact confirms.
// Both renew the tag on every hit.
internal sealed class StratumCombatLogSystem
{
	private readonly ServerMain server;
	private readonly Dictionary<string, CombatTagEntry> activeTags = new Dictionary<string, CombatTagEntry>(StringComparer.Ordinal);
	private readonly HashSet<string> pendingKillOnRejoin = new HashSet<string>(StringComparer.Ordinal);

	private struct CombatTagEntry
	{
		public long ExpiresAtMs;
		public string OpponentName;
	}

	public StratumCombatLogSystem(ServerMain server)
	{
		this.server = server;
		server.EventManager.OnPlayerInteractEntity += OnPlayerInteractEntity;
		server.EventManager.OnPlayerDisconnect += OnPlayerDisconnect;
		server.EventManager.OnPlayerJoin += OnPlayerJoin;
		server.RegisterGameTickListener(OnTick, 1000);

		// Expose a static hook for the projectile patch (lives in VSEssentials, bridges through VintagestoryAPI).
		StratumCombatLogHook.OnProjectilePvPHit = OnProjectileHit;

		IChatCommand root = server.api.ChatCommands.Get("stratum");
		if (root != null)
		{
			root.BeginSubCommand("combatlog")
				.WithDescription("Combat log prevention status and testing")
				.RequiresPrivilege("controlserver")
				.BeginSubCommand("status")
					.WithDescription("Show active combat tags")
					.HandleWith(CmdStatus)
				.EndSubCommand()
				.BeginSubCommand("tag")
					.WithDescription("Tag yourself for testing (simulates a PvP hit)")
					.HandleWith(CmdTagSelf)
				.EndSubCommand()
			.EndSubCommand();
		}
	}

	private StratumCombatLogConfig Cfg => StratumRuntime.Config?.CombatLog;

	// --- Melee hook (fires on attack attempt against another player) ---

	private void OnPlayerInteractEntity(Entity target, IPlayer byPlayer, ItemSlot slot, Vec3d hitPosition, int mode, ref EnumHandling handling)
	{
		if (mode != 0) return;

		StratumCombatLogConfig cfg = Cfg;
		if (cfg == null || !cfg.Enabled) return;

		if (target is not EntityPlayer targetPlayer) return;
		if (byPlayer?.Entity == null) return;

		IServerPlayer attacker = byPlayer as IServerPlayer;
		IServerPlayer victim = server.PlayerByUid(targetPlayer.PlayerUID) as IServerPlayer;
		if (attacker == null || victim == null) return;
		if (attacker.PlayerUID == victim.PlayerUID) return;

		if (IsExempt(attacker) || IsExempt(victim)) return;

		long expiresAt = server.ElapsedMilliseconds + cfg.TagDurationSeconds * 1000L;
		TagPlayer(victim, attacker.PlayerName, expiresAt, cfg);
		if (cfg.TagAttacker) TagPlayer(attacker, victim.PlayerName, expiresAt, cfg);
	}

	// --- Projectile hook (called from patched EntityProjectileBase after a PvP hit confirms) ---

	private void OnProjectileHit(string attackerUid, string victimUid)
	{
		StratumCombatLogConfig cfg = Cfg;
		if (cfg == null || !cfg.Enabled) return;

		IServerPlayer attacker = server.PlayerByUid(attackerUid) as IServerPlayer;
		IServerPlayer victim = server.PlayerByUid(victimUid) as IServerPlayer;
		if (attacker == null || victim == null) return;
		if (attackerUid == victimUid) return;

		if (IsExempt(attacker) || IsExempt(victim)) return;

		long expiresAt = server.ElapsedMilliseconds + cfg.TagDurationSeconds * 1000L;
		TagPlayer(victim, attacker.PlayerName, expiresAt, cfg);
		if (cfg.TagAttacker) TagPlayer(attacker, victim.PlayerName, expiresAt, cfg);
	}

	// --- Disconnect penalty ---

	private void OnPlayerDisconnect(IServerPlayer player)
	{
		if (player == null) return;

		StratumCombatLogConfig cfg = Cfg;
		if (cfg == null || !cfg.Enabled) return;

		string uid = player.PlayerUID;
		if (!activeTags.TryGetValue(uid, out CombatTagEntry entry)) return;

		long now = server.ElapsedMilliseconds;
		if (now >= entry.ExpiresAtMs)
		{
			activeTags.Remove(uid);
			return;
		}

		activeTags.Remove(uid);
		long secondsRemaining = (entry.ExpiresAtMs - now) / 1000;
		string logLine = player.PlayerName + " combat logged (opponent: " + entry.OpponentName + ", " + secondsRemaining + "s remaining)";

		switch (cfg.Penalty)
		{
			case EnumCombatLogPenalty.Kill:
				KillPlayer(player);
				break;
			case EnumCombatLogPenalty.KillOnRejoin:
				pendingKillOnRejoin.Add(uid);
				break;
			case EnumCombatLogPenalty.LogOnly:
				break;
		}

		StratumRuntime.LogInfo(logLine);

		if (cfg.BroadcastOnCombatLog && cfg.Penalty != EnumCombatLogPenalty.LogOnly)
		{
			server.BroadcastMessageToAllGroups(string.Format(cfg.CombatLogBroadcast, player.PlayerName), EnumChatType.Notification);
		}

		if (cfg.AlertStaff)
		{
			string staffMsg = StratumCommandText.Pill("Combat Log", StratumCommandText.Bad)
				+ " " + StratumCommandText.Warning(player.PlayerName)
				+ " disconnected while tagged (opponent: "
				+ StratumCommandText.Escape(entry.OpponentName)
				+ ", " + secondsRemaining + "s remaining, penalty: " + cfg.Penalty + ")";
			foreach (ConnectedClient client in server.Clients.Values)
			{
				if (client?.Player != null && client.Player.HasPrivilege("controlserver"))
				{
					client.Player.SendMessage(GlobalConstants.GeneralChatGroup, staffMsg, EnumChatType.Notification);
				}
			}
		}
	}

	// --- Rejoin penalty ---

	private void OnPlayerJoin(IServerPlayer player)
	{
		if (player == null) return;

		StratumCombatLogConfig cfg = Cfg;
		if (cfg == null || !cfg.Enabled) return;
		if (cfg.Penalty != EnumCombatLogPenalty.KillOnRejoin) return;

		if (pendingKillOnRejoin.Remove(player.PlayerUID))
		{
			server.RegisterCallback(dt => KillPlayer(player), 2000);
			StratumRuntime.LogInfo(player.PlayerName + " killed on rejoin (combat log penalty)");
		}
	}

	// --- Tick: expire old tags ---

	private void OnTick(float dt)
	{
		StratumCombatLogConfig cfg = Cfg;
		if (cfg == null || activeTags.Count == 0) return;

		long now = server.ElapsedMilliseconds;
		List<string> expired = null;

		foreach (KeyValuePair<string, CombatTagEntry> kv in activeTags)
		{
			if (now >= kv.Value.ExpiresAtMs)
			{
				expired ??= new List<string>();
				expired.Add(kv.Key);
			}
		}

		if (expired == null) return;

		for (int i = 0; i < expired.Count; i++)
		{
			activeTags.Remove(expired[i]);

			if (cfg.NotifyOnExpiry)
			{
				IServerPlayer player = server.PlayerByUid(expired[i]) as IServerPlayer;
				if (player != null && player.ConnectionState == EnumClientState.Playing)
				{
					player.SendMessage(GlobalConstants.GeneralChatGroup, cfg.ExpiryMessage, EnumChatType.Notification);
				}
			}
		}
	}

	// --- Helpers ---

	private void TagPlayer(IServerPlayer player, string opponentName, long expiresAt, StratumCombatLogConfig cfg)
	{
		string uid = player.PlayerUID;
		bool isNew = !activeTags.TryGetValue(uid, out CombatTagEntry existing) || existing.ExpiresAtMs < server.ElapsedMilliseconds;

		activeTags[uid] = new CombatTagEntry
		{
			ExpiresAtMs = expiresAt,
			OpponentName = opponentName
		};

		if (isNew && cfg.NotifyOnTag)
		{
			player.SendMessage(GlobalConstants.GeneralChatGroup, string.Format(cfg.TagMessage, cfg.TagDurationSeconds), EnumChatType.Notification);
		}
	}

	private void KillPlayer(IServerPlayer player)
	{
		if (player?.Entity == null || !player.Entity.Alive) return;
		player.Entity.Die(EnumDespawnReason.Death, new DamageSource
		{
			Source = EnumDamageSource.Internal,
			Type = EnumDamageType.PiercingAttack
		});
	}

	private bool IsExempt(IServerPlayer player)
	{
		EnumGameMode mode = player.WorldData.CurrentGameMode;
		if (mode == EnumGameMode.Creative || mode == EnumGameMode.Spectator) return true;

		StratumCombatLogConfig cfg = Cfg;
		if (cfg != null && !string.IsNullOrEmpty(cfg.ExemptPrivilege) && player.HasPrivilege(cfg.ExemptPrivilege))
		{
			return true;
		}
		return false;
	}

	// --- Admin commands ---

	private TextCommandResult CmdStatus(TextCommandCallingArgs args)
	{
		StratumCombatLogConfig cfg = Cfg;
		if (cfg == null || !cfg.Enabled)
		{
			return TextCommandResult.Success("Combat log prevention is disabled.");
		}

		if (activeTags.Count == 0)
		{
			return TextCommandResult.Success("Enabled, penalty: " + cfg.Penalty + ", no active tags.");
		}

		long now = server.ElapsedMilliseconds;
		string result = "Active tags (" + activeTags.Count + "), penalty: " + cfg.Penalty + "\n";
		foreach (KeyValuePair<string, CombatTagEntry> kv in activeTags)
		{
			IServerPlayer p = server.PlayerByUid(kv.Key) as IServerPlayer;
			string name = p?.PlayerName ?? kv.Key;
			long remaining = Math.Max(0, (kv.Value.ExpiresAtMs - now) / 1000);
			result += "  " + name + " vs " + kv.Value.OpponentName + " (" + remaining + "s)\n";
		}
		return TextCommandResult.Success(result);
	}

	private TextCommandResult CmdTagSelf(TextCommandCallingArgs args)
	{
		StratumCombatLogConfig cfg = Cfg;
		if (cfg == null || !cfg.Enabled)
		{
			return TextCommandResult.Error("Combat log prevention is disabled.");
		}

		IServerPlayer caller = args.Caller.Player as IServerPlayer;
		if (caller == null)
		{
			return TextCommandResult.Error("Must be called by a player.");
		}

		long expiresAt = server.ElapsedMilliseconds + cfg.TagDurationSeconds * 1000L;
		activeTags[caller.PlayerUID] = new CombatTagEntry
		{
			ExpiresAtMs = expiresAt,
			OpponentName = "[test]"
		};

		caller.SendMessage(GlobalConstants.GeneralChatGroup, string.Format(cfg.TagMessage, cfg.TagDurationSeconds), EnumChatType.Notification);
		return TextCommandResult.Success("Tagged for " + cfg.TagDurationSeconds + "s. Disconnect to test.");
	}
}
