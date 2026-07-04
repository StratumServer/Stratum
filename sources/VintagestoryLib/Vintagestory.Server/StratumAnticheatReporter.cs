using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

internal static class StratumAnticheatReporter
{
	private const string BlockEntityOutOfRangeType = "block-entity-range";
	private const string BlockInteractionOutOfRangeType = "block-interaction-range";
	private const string BlockBreakProgressType = "block-break-progress";
	private const string MovementType = "movement";
	private const string CombatType = "combat";

	private static readonly object Lock = new object();
	private static readonly Dictionary<string, PlayerViolationState> PlayerViolations = new Dictionary<string, PlayerViolationState>(StringComparer.OrdinalIgnoreCase);

	public static void RecordBlockEntityOutOfRange(ServerMain server, ServerPlayer player, BlockPos pos)
	{
		if (server == null || player == null || pos == null)
		{
			return;
		}

		StratumRuntime.Config.EnsurePopulated();
		StratumAnticheatConfig config = StratumRuntime.Config.Anticheat;
		if (!config.Enabled || !config.BlockEntityOutOfRange.Enabled)
		{
			return;
		}

		DateTime now = DateTime.UtcNow;
		ViolationRecordResult result = RecordViolation(player, now, BlockEntityOutOfRangeType, FormatBlockPos(pos), config, config.BlockEntityOutOfRange);

		if (result.ShouldAlert)
		{
			SendStaffAlert(server, player, "block entity range", pos, result.RollingCount, result.TotalCount, config.BlockEntityOutOfRange.AlertWindowSeconds);
		}
	}

	public static bool RecordBlockInteractionOutOfRange(ServerMain server, ServerPlayer player, string action, BlockPos pos, double distance, double maxRange, out string disconnectReason)
	{
		disconnectReason = null;
		if (server == null || player == null || pos == null)
		{
			return false;
		}

		StratumRuntime.Config.EnsurePopulated();
		StratumAnticheatConfig config = StratumRuntime.Config.Anticheat;
		if (!config.Enabled || !config.BlockInteractionOutOfRange.Enabled)
		{
			return false;
		}

		string detail = action + " at " + FormatBlockPos(pos) + " distance=" + distance.ToString("0.##", CultureInfo.InvariantCulture) + "/" + maxRange.ToString("0.##", CultureInfo.InvariantCulture);
		DateTime now = DateTime.UtcNow;
		ViolationRecordResult result = RecordViolation(player, now, BlockInteractionOutOfRangeType, detail, config, config.BlockInteractionOutOfRange);
		if (result.ShouldAlert)
		{
			SendStaffAlert(server, player, "survival block reach", pos, result.RollingCount, result.TotalCount, config.BlockInteractionOutOfRange.AlertWindowSeconds);
		}

		if (config.BlockInteractionOutOfRange.KickConfirmedCheats && result.RollingCount >= config.BlockInteractionOutOfRange.KickAfterViolations)
		{
			disconnectReason = string.IsNullOrWhiteSpace(config.BlockInteractionOutOfRange.KickMessage)
				? "Disconnected by Stratum block reach protection"
				: config.BlockInteractionOutOfRange.KickMessage;
			return true;
		}

		return false;
	}

	public static bool RecordBlockBreakViolation(ServerMain server, ServerPlayer player, BlockPos pos, string reason, out string disconnectReason)
	{
		disconnectReason = null;
		if (server == null || player == null || pos == null)
		{
			return false;
		}

		StratumRuntime.Config.EnsurePopulated();
		StratumAnticheatConfig config = StratumRuntime.Config.Anticheat;
		if (!config.Enabled || !config.BlockBreakProgress.Enabled)
		{
			return false;
		}

		string detail = string.IsNullOrWhiteSpace(reason) ? FormatBlockPos(pos) : reason;
		DateTime now = DateTime.UtcNow;
		ViolationRecordResult result = RecordViolation(player, now, BlockBreakProgressType, detail, config, config.BlockBreakProgress);
		if (result.ShouldAlert)
		{
			SendStaffAlert(server, player, "block break progress", pos, result.RollingCount, result.TotalCount, config.BlockBreakProgress.AlertWindowSeconds);
		}

		if (config.BlockBreakProgress.KickConfirmedCheats && result.RollingCount >= config.BlockBreakProgress.KickAfterViolations)
		{
			disconnectReason = string.IsNullOrWhiteSpace(config.BlockBreakProgress.KickMessage)
				? "Disconnected by Stratum block break protection"
				: config.BlockBreakProgress.KickMessage;
			return true;
		}

		return false;
	}

	public static bool RecordMovementViolation(ServerMain server, ServerPlayer player, BlockPos pos, string reason, out string disconnectReason)
	{
		disconnectReason = null;
		if (server == null || player == null)
		{
			return false;
		}

		StratumRuntime.Config.EnsurePopulated();
		StratumAnticheatConfig config = StratumRuntime.Config.Anticheat;
		if (!config.Enabled || !config.Movement.Enabled)
		{
			return false;
		}

		string detail = string.IsNullOrWhiteSpace(reason) ? (pos != null ? FormatBlockPos(pos) : "movement") : reason;
		DateTime now = DateTime.UtcNow;
		ViolationRecordResult result = RecordViolation(player, now, MovementType, detail, config, config.Movement);
		if (result.ShouldAlert)
		{
			SendStaffAlert(server, player, "suspicious movement", pos, result.RollingCount, result.TotalCount, config.Movement.AlertWindowSeconds);
		}

		if (config.Movement.KickConfirmedCheats && result.RollingCount >= config.Movement.KickAfterViolations)
		{
			disconnectReason = string.IsNullOrWhiteSpace(config.Movement.KickMessage)
				? "Disconnected by Stratum movement protection"
				: config.Movement.KickMessage;
			return true;
		}

		return false;
	}

	public static bool RecordCombatViolation(ServerMain server, ServerPlayer player, BlockPos pos, string reason, out string disconnectReason)
	{
		disconnectReason = null;
		if (server == null || player == null)
		{
			return false;
		}

		StratumRuntime.Config.EnsurePopulated();
		StratumAnticheatConfig config = StratumRuntime.Config.Anticheat;
		if (!config.Enabled || !config.Combat.Enabled)
		{
			return false;
		}

		string detail = string.IsNullOrWhiteSpace(reason) ? "combat" : reason;
		DateTime now = DateTime.UtcNow;
		ViolationRecordResult result = RecordViolation(player, now, CombatType, detail, config, config.Combat);
		if (result.ShouldAlert)
		{
			SendStaffAlert(server, player, "combat", pos, result.RollingCount, result.TotalCount, config.Combat.AlertWindowSeconds);
		}

		if (config.Combat.KickConfirmedCheats && result.RollingCount >= config.Combat.KickAfterViolations)
		{
			disconnectReason = string.IsNullOrWhiteSpace(config.Combat.KickMessage)
				? "Disconnected by Stratum combat protection"
				: config.Combat.KickMessage;
			return true;
		}

		return false;
	}

	public static string BuildReport(string playerFilter, int maxEvents = 12)
	{
		StratumRuntime.Config.EnsurePopulated();
		DateTime now = DateTime.UtcNow;
		StratumAnticheatConfig config = StratumRuntime.Config.Anticheat;
		bool showTop = string.IsNullOrWhiteSpace(playerFilter) || string.Equals(playerFilter, "top", StringComparison.OrdinalIgnoreCase);
		List<PlayerViolationSnapshot> players;

		lock (Lock)
		{
			PruneStalePlayers(now, config);
			players = PlayerViolations.Values
				.Select(state => state.ToSnapshot(maxEvents))
				.OrderByDescending(snapshot => snapshot.TotalCount)
				.ThenBy(snapshot => snapshot.PlayerName, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		if (!showTop)
		{
			players = players
				.Where(snapshot => string.Equals(snapshot.PlayerName, playerFilter, StringComparison.OrdinalIgnoreCase) || string.Equals(snapshot.PlayerKey, playerFilter, StringComparison.OrdinalIgnoreCase))
				.ToList();
		}

		StringBuilder output = new StringBuilder(StratumCommandText.Title("Stratum Anticheat"));
		output.Append(StratumCommandText.Row("Enabled", config.Enabled ? "true" : "false"));
		output.Append(StratumCommandText.Row("Staff alerts", config.StaffAlerts ? "true" : "false"));
		output.Append(StratumCommandText.Row("Tracked players", players.Count.ToString(CultureInfo.InvariantCulture)));

		if (players.Count == 0)
		{
			output.Append(StratumCommandText.Empty(showTop ? "\nNo recorded violations." : "\nNo recorded violations for " + playerFilter + "."));
			return output.ToString();
		}

		if (showTop)
		{
			AppendTopReport(output, players, now);
			output.Append(StratumCommandText.Row("Player details", "/stratum ac &lt;player&gt;"));
			return output.ToString();
		}

		AppendPlayerReport(output, players[0], now);
		output.Append(StratumCommandText.Row("Overview", "/stratum ac"));
		return output.ToString();
	}

	private static void AppendTopReport(StringBuilder output, List<PlayerViolationSnapshot> players, DateTime now)
	{
		output.Append("\n").Append(StratumCommandText.Title("Top Violation Types"));
		foreach (var entry in players
			.SelectMany(player => player.TypeCounts)
			.GroupBy(entry => entry.Type)
			.Select(group => new { Type = group.Key, Count = group.Sum(entry => entry.Count) })
			.OrderByDescending(entry => entry.Count)
			.ThenBy(entry => FriendlyTypeName(entry.Type))
			.Take(5))
		{
			output.Append(StratumCommandText.Bullet(FriendlyTypeName(entry.Type), entry.Count.ToString(CultureInfo.InvariantCulture) + " hits"));
		}

		output.Append("\n").Append(StratumCommandText.Title("Top Players"));
		foreach (PlayerViolationSnapshot player in players.Take(8))
		{
			string topType = player.TypeCounts.Length == 0 ? "unknown" : FriendlyTypeName(player.TypeCounts.OrderByDescending(entry => entry.Count).First().Type);
			output.Append(StratumCommandText.Bullet(
				player.PlayerName,
				player.TotalCount.ToString(CultureInfo.InvariantCulture) + " hits, mostly " + topType + ", last seen " + FormatAge(now - player.LastSeenUtc) + " ago"));
		}
	}

	private static void AppendPlayerReport(StringBuilder output, PlayerViolationSnapshot player, DateTime now)
	{
		output.Append("\n").Append(StratumCommandText.Title("Player: " + player.PlayerName));
		output.Append(StratumCommandText.Row("Total", player.TotalCount.ToString(CultureInfo.InvariantCulture) + " recorded violations"));
		output.Append(StratumCommandText.Row("Last seen", FormatAge(now - player.LastSeenUtc) + " ago"));

		output.Append("\n").Append(StratumCommandText.Title("Breakdown"));
		foreach (ViolationTypeCount entry in player.TypeCounts.OrderByDescending(entry => entry.Count))
		{
			output.Append(StratumCommandText.Bullet(FriendlyTypeName(entry.Type), entry.Count.ToString(CultureInfo.InvariantCulture) + " hits"));
		}

		output.Append("\n").Append(StratumCommandText.Title("Recent"));
		foreach (ViolationEventSnapshot entry in player.RecentEvents.Take(8))
		{
			output.Append(StratumCommandText.RawRow(
				FriendlyTypeName(entry.Type),
				StratumCommandText.Escape(FormatAge(now - entry.Utc) + " ago, " + FriendlyDetail(entry))));
		}
	}

	private static string FriendlyTypeName(string type)
	{
		switch (type)
		{
			case BlockEntityOutOfRangeType:
				return "Block entity out of range";
			case BlockInteractionOutOfRangeType:
				return "Block reach";
			case BlockBreakProgressType:
				return "Block break timing";
			case MovementType:
				return "Movement";
			case CombatType:
				return "Combat";
			default:
				return string.IsNullOrWhiteSpace(type) ? "Unknown" : type;
		}
	}

	private static string FriendlyDetail(ViolationEventSnapshot entry)
	{
		string detail = entry.Detail ?? "";
		if (entry.Type == MovementType)
		{
			if (detail.StartsWith("water walk", StringComparison.OrdinalIgnoreCase))
			{
				return "walking on liquid";
			}

			if (detail.StartsWith("flight:", StringComparison.OrdinalIgnoreCase))
			{
				return "hovering or flying without falling";
			}

			if (detail.StartsWith("noclip:", StringComparison.OrdinalIgnoreCase))
			{
				return "inside solid collision";
			}

			if (detail.StartsWith("correction replay", StringComparison.OrdinalIgnoreCase))
			{
				return "kept moving away after server correction";
			}

			if (detail.StartsWith("delta ", StringComparison.OrdinalIgnoreCase))
			{
				return "moved faster or farther than allowed (" + detail + ")";
			}
		}

		if (entry.Type == BlockEntityOutOfRangeType)
		{
			return "sent a block entity packet too far away at " + detail;
		}

		if (entry.Type == CombatType)
		{
			if (detail.StartsWith("aura:", StringComparison.OrdinalIgnoreCase))
			{
				return "attacked several entities at once (" + detail + ")";
			}

			if (detail.StartsWith("aim:", StringComparison.OrdinalIgnoreCase))
			{
				return "attacked an entity outside their aim (" + detail + ")";
			}
		}

		return detail;
	}

	private static ViolationRecordResult RecordViolation(ServerPlayer player, DateTime now, string type, string detail, StratumAnticheatConfig config, StratumAnticheatRuleConfig rule)
	{
		int rollingCount;
		int totalCount;
		bool shouldAlert;

		lock (Lock)
		{
			PruneStalePlayers(now, config);

			string playerKey = GetPlayerKey(player);
			if (!PlayerViolations.TryGetValue(playerKey, out PlayerViolationState state))
			{
				state = new PlayerViolationState(playerKey, player.PlayerName);
				PlayerViolations[playerKey] = state;
			}

			state.PlayerName = player.PlayerName;
			state.LastSeenUtc = now;
			state.TotalCount++;
			state.Events.AddLast(new PlayerViolationEvent(type, now, detail));
			ServerMain.Logger?.Audit("Stratum anticheat {0} player={1} detail={2}", type, player.PlayerName, detail);

			while (state.Events.Count > config.MaxStoredViolationsPerPlayer)
			{
				state.Events.RemoveFirst();
			}

			TimeSpan window = TimeSpan.FromSeconds(rule.AlertWindowSeconds);
			DateTime cutoff = now - window;
			rollingCount = state.Events.Count(entry => entry.Type == type && entry.Utc >= cutoff);
			totalCount = state.TotalCount;

			DateTime lastAlertUtc = state.GetLastStaffAlertUtc(type);
			shouldAlert = config.StaffAlerts
				&& rollingCount >= rule.AlertAfterViolations
				&& now - lastAlertUtc >= TimeSpan.FromSeconds(rule.RepeatAlertSeconds);

			if (shouldAlert)
			{
				state.SetLastStaffAlertUtc(type, now);
			}
		}

		return new ViolationRecordResult(rollingCount, totalCount, shouldAlert);
	}

	private static string FormatAge(TimeSpan age)
	{
		if (age.TotalSeconds < 60) return ((int)Math.Max(0, age.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + "s";
		if (age.TotalMinutes < 60) return ((int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m";
		if (age.TotalHours < 24) return ((int)age.TotalHours).ToString(CultureInfo.InvariantCulture) + "h";
		return ((int)age.TotalDays).ToString(CultureInfo.InvariantCulture) + "d";
	}

	private static void SendStaffAlert(ServerMain server, ServerPlayer player, string label, BlockPos pos, int rollingCount, int totalCount, int windowSeconds)
	{
		string message = StratumCommandText.Pill("Stratum AC", StratumCommandText.Bad)
			+ " "
			+ StratumCommandText.Warning(player.PlayerName)
			+ " has "
			+ rollingCount.ToString(CultureInfo.InvariantCulture)
			+ " "
			+ StratumCommandText.Escape(label)
			+ " violations in "
			+ windowSeconds.ToString(CultureInfo.InvariantCulture)
			+ "s. Total recorded: "
			+ totalCount.ToString(CultureInfo.InvariantCulture)
			+ ". Last: "
			+ StratumCommandText.Escape(FormatBlockPos(pos));

		foreach (ConnectedClient client in server.Clients.Values)
		{
			if (ShouldReceiveStaffAlert(client))
			{
				client.Player.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);
			}
		}
	}

	private static bool ShouldReceiveStaffAlert(ConnectedClient client)
	{
		if (client == null || !client.State.IsAdmitted() || client.Player == null)
		{
			return false;
		}

		return client.Player.HasPrivilege(Privilege.controlserver) || StratumCommandAccessCatalog.PlayerHasAccess(client.Player, StratumRuntime.Config.Commands.StaffChat);
	}

	private static void PruneStalePlayers(DateTime now, StratumAnticheatConfig config)
	{
		TimeSpan keepFor = TimeSpan.FromMinutes(config.KeepPlayerViolationsMinutes);
		List<string> staleKeys = null;
		foreach (KeyValuePair<string, PlayerViolationState> entry in PlayerViolations)
		{
			if (now - entry.Value.LastSeenUtc <= keepFor)
			{
				continue;
			}

			staleKeys ??= new List<string>();
			staleKeys.Add(entry.Key);
		}

		if (staleKeys == null)
		{
			return;
		}

		foreach (string key in staleKeys)
		{
			PlayerViolations.Remove(key);
		}
	}

	private static string GetPlayerKey(ServerPlayer player)
	{
		return !string.IsNullOrWhiteSpace(player.PlayerUID) ? player.PlayerUID : player.PlayerName;
	}

	private static string FormatBlockPos(BlockPos pos)
	{
		if (pos == null)
		{
			return "unknown";
		}

		return pos.X.ToString(CultureInfo.InvariantCulture) + ", " + pos.InternalY.ToString(CultureInfo.InvariantCulture) + ", " + pos.Z.ToString(CultureInfo.InvariantCulture);
	}

	private sealed class PlayerViolationState
	{
		public PlayerViolationState(string playerKey, string playerName)
		{
			PlayerKey = playerKey;
			PlayerName = playerName;
		}

		public string PlayerKey { get; }

		public string PlayerName { get; set; }

		public int TotalCount { get; set; }

		public DateTime LastSeenUtc { get; set; }

		public DateTime LastStaffAlertUtc { get; set; } = DateTime.MinValue;

		private Dictionary<string, DateTime> LastStaffAlertsByType { get; } = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

		public LinkedList<PlayerViolationEvent> Events { get; } = new LinkedList<PlayerViolationEvent>();

		public DateTime GetLastStaffAlertUtc(string type)
		{
			return LastStaffAlertsByType.TryGetValue(type, out DateTime utc) ? utc : DateTime.MinValue;
		}

		public void SetLastStaffAlertUtc(string type, DateTime utc)
		{
			LastStaffAlertUtc = utc;
			LastStaffAlertsByType[type] = utc;
		}

		public PlayerViolationSnapshot ToSnapshot(int maxEvents)
		{
			return new PlayerViolationSnapshot(
				PlayerKey,
				PlayerName,
				TotalCount,
				LastSeenUtc,
				Events.GroupBy(entry => entry.Type)
					.Select(group => new ViolationTypeCount(group.Key, group.Count()))
					.OrderByDescending(entry => entry.Count)
					.ToArray(),
				Events.Reverse().Take(Math.Max(1, maxEvents)).Select(entry => new ViolationEventSnapshot(entry.Type, entry.Utc, entry.Detail)).ToArray());
		}
	}

	private readonly struct PlayerViolationEvent
	{
		public PlayerViolationEvent(string type, DateTime utc, string detail)
		{
			Type = type;
			Utc = utc;
			Detail = detail;
		}

		public string Type { get; }

		public DateTime Utc { get; }

		public string Detail { get; }
	}

	private readonly struct ViolationRecordResult
	{
		public ViolationRecordResult(int rollingCount, int totalCount, bool shouldAlert)
		{
			RollingCount = rollingCount;
			TotalCount = totalCount;
			ShouldAlert = shouldAlert;
		}

		public int RollingCount { get; }

		public int TotalCount { get; }

		public bool ShouldAlert { get; }
	}

	private readonly struct PlayerViolationSnapshot
	{
		public PlayerViolationSnapshot(string playerKey, string playerName, int totalCount, DateTime lastSeenUtc, ViolationTypeCount[] typeCounts, ViolationEventSnapshot[] recentEvents)
		{
			PlayerKey = playerKey;
			PlayerName = playerName;
			TotalCount = totalCount;
			LastSeenUtc = lastSeenUtc;
			TypeCounts = typeCounts;
			RecentEvents = recentEvents;
		}

		public string PlayerKey { get; }

		public string PlayerName { get; }

		public int TotalCount { get; }

		public DateTime LastSeenUtc { get; }

		public ViolationTypeCount[] TypeCounts { get; }

		public ViolationEventSnapshot[] RecentEvents { get; }
	}

	private readonly struct ViolationTypeCount
	{
		public ViolationTypeCount(string type, int count)
		{
			Type = type;
			Count = count;
		}

		public string Type { get; }

		public int Count { get; }
	}

	private readonly struct ViolationEventSnapshot
	{
		public ViolationEventSnapshot(string type, DateTime utc, string detail)
		{
			Type = type;
			Utc = utc;
			Detail = detail;
		}

		public string Type { get; }

		public DateTime Utc { get; }

		public string Detail { get; }
	}
}
