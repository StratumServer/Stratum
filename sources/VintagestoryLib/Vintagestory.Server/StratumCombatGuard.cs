using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace Vintagestory.Server;

// Kill-aura / aimbot heuristics layered on top of the vanilla attack range check. Runs only for
// attack (left-click) interactions. Two signals, both chosen so a real vanilla client can never
// trip them:
//   - multi-target: a human hits one entity per swing and can only aim at one at a time, so
//     landing attacks on several distinct entities inside a few hundred ms is not humanly possible.
//   - aim cone: a real client raytraces from the crosshair, so the target always sits roughly in
//     front of the player. Hitting something well to the side or behind is an aura giveaway.
// Defaults are monitor-only: violations are recorded and staff are alerted, but no hit is dropped
// and no one is kicked unless a server explicitly opts in.
internal static class StratumCombatGuard
{
	private static readonly object gate = new object();
	private static readonly Dictionary<string, AttackState> states = new Dictionary<string, AttackState>(StringComparer.Ordinal);

	public static void ForgetPlayer(string key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return;
		}

		lock (gate)
		{
			states.Remove(key);
		}
	}

	// Returns false only when the guard is configured to drop the hit (or kick). Default config
	// always returns true, so vanilla behaviour is untouched.
	public static bool TryAcceptAttack(ServerMain server, ConnectedClient client, Entity target, Cuboidd targetBox, double eyeX, double eyeY, double eyeZ, out string disconnectReason)
	{
		disconnectReason = null;
		ServerPlayer player = client?.Player;
		if (server == null || player?.Entity == null || target == null)
		{
			return true;
		}

		StratumRuntime.Config.EnsurePopulated();
		StratumAnticheatConfig anticheat = StratumRuntime.Config.Anticheat;
		StratumCombatAnticheatConfig config = anticheat.Combat;
		if (!anticheat.Enabled || !config.Enabled)
		{
			return true;
		}

		// Skip creative/spectator so staff testing or one-shotting mobs never shows up as noise.
		EnumGameMode mode = player.WorldData.CurrentGameMode;
		if (mode == EnumGameMode.Creative || mode == EnumGameMode.Spectator)
		{
			return true;
		}

		string key = !string.IsNullOrWhiteSpace(player.PlayerUID) ? player.PlayerUID : player.PlayerName;
		if (string.IsNullOrEmpty(key))
		{
			return true;
		}

		long now = server.ElapsedMilliseconds;
		string reason = null;

		if (config.DetectMultiTarget)
		{
			int distinct = RegisterMultiTarget(key, target.EntityId, now, config);
			if (distinct >= config.MultiTargetThreshold)
			{
				reason = "aura: " + distinct + " targets in " + config.MultiTargetWindowMs + "ms";
			}
		}

		if (reason == null && config.DetectAimCone)
		{
			string aimReason = CheckAimCone(player.Entity, targetBox, eyeX, eyeY, eyeZ, config);
			if (aimReason != null)
			{
				reason = aimReason;
			}
		}

		if (reason == null)
		{
			return true;
		}

		BlockPos pos = target.Pos?.AsBlockPos;
		bool shouldKick = StratumAnticheatReporter.RecordCombatViolation(server, player, pos, reason, out disconnectReason);
		if (shouldKick)
		{
			return false;
		}

		disconnectReason = null;
		return !config.CancelFlaggedHits;
	}

	private static int RegisterMultiTarget(string key, long entityId, long now, StratumCombatAnticheatConfig config)
	{
		long window = config.MultiTargetWindowMs;
		lock (gate)
		{
			if (!states.TryGetValue(key, out AttackState state))
			{
				state = new AttackState();
				states[key] = state;
			}

			LinkedList<AttackHit> hits = state.RecentHits;
			hits.AddLast(new AttackHit(entityId, now));

			while (hits.Count > 0 && now - hits.First.Value.WhenMs > window)
			{
				hits.RemoveFirst();
			}

			// Bound memory if something floods attacks inside the window.
			while (hits.Count > 64)
			{
				hits.RemoveFirst();
			}

			HashSet<long> distinct = new HashSet<long>();
			foreach (AttackHit hit in hits)
			{
				distinct.Add(hit.EntityId);
			}

			return distinct.Count;
		}
	}

	private static string CheckAimCone(EntityPlayer attacker, Cuboidd targetBox, double eyeX, double eyeY, double eyeZ, StratumCombatAnticheatConfig config)
	{
		if (targetBox == null || attacker?.Pos == null)
		{
			return null;
		}

		double centerX = (targetBox.X1 + targetBox.X2) * 0.5;
		double centerY = (targetBox.Y1 + targetBox.Y2) * 0.5;
		double centerZ = (targetBox.Z1 + targetBox.Z2) * 0.5;

		double dx = centerX - eyeX;
		double dy = centerY - eyeY;
		double dz = centerZ - eyeZ;
		double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
		if (length < config.MinAngleCheckDistance || length <= 1e-6)
		{
			return null;
		}

		Vec3f view = attacker.Pos.GetViewVector();
		double invLen = 1.0 / length;
		double cosAngle = view.X * (dx * invLen) + view.Y * (dy * invLen) + view.Z * (dz * invLen);
		cosAngle = Math.Clamp(cosAngle, -1.0, 1.0);

		double cosThreshold = Math.Cos(config.MaxAttackAngleDegrees * Math.PI / 180.0);
		if (cosAngle >= cosThreshold)
		{
			return null;
		}

		double angleDeg = Math.Acos(cosAngle) * 180.0 / Math.PI;
		return "aim: " + angleDeg.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " deg off";
	}

	private readonly struct AttackHit
	{
		public AttackHit(long entityId, long whenMs)
		{
			EntityId = entityId;
			WhenMs = whenMs;
		}

		public long EntityId { get; }

		public long WhenMs { get; }
	}

	private sealed class AttackState
	{
		public LinkedList<AttackHit> RecentHits { get; } = new LinkedList<AttackHit>();
	}
}
