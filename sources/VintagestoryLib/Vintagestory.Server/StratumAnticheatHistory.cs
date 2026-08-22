using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Vintagestory.Server;

/// <summary>
/// Cross-session confirmed-violation count per player. StratumAnticheatReporter's own
/// PlayerViolations dictionary is in-memory only and prunes a player out after
/// KeepPlayerViolationsMinutes of inactivity, so a repeat offender who logs off and back on starts
/// over there. This store is what StratumAnticheatPunishments reads to decide whether a player has
/// accumulated enough flags for the next rung of the punishment ladder, persisted the same way
/// StratumModerationStore and StratumCustodyStore already persist their own player-scoped state.
/// </summary>
internal static class StratumAnticheatHistory
{
	private const string HistoryKey = "stratum.anticheat.history.v1";

	public static StratumAnticheatHistoryRecord RecordFlag(ServerMain server, ServerPlayerData target)
	{
		StratumAnticheatHistoryRecord record = Load(target);
		record.TotalFlags++;
		record.LastFlagUtc = DateTime.UtcNow;
		Save(server, target, record);
		return record;
	}

	public static void MarkPunishmentApplied(ServerMain server, ServerPlayerData target, StratumAnticheatHistoryRecord record, int tier)
	{
		record.HighestPunishmentApplied = tier;
		record.LastPunishmentUtc = DateTime.UtcNow;
		Save(server, target, record);
	}

	public static StratumAnticheatHistoryRecord Load(ServerPlayerData target)
	{
		if (target?.CustomPlayerData == null || !target.CustomPlayerData.TryGetValue(HistoryKey, out string json) || string.IsNullOrWhiteSpace(json))
		{
			return new StratumAnticheatHistoryRecord();
		}

		try
		{
			return JsonConvert.DeserializeObject<StratumAnticheatHistoryRecord>(json) ?? new StratumAnticheatHistoryRecord();
		}
		catch (Exception exception)
		{
			StratumRuntime.LogWarning("failed to read anticheat history for " + target.LastKnownPlayername + ": " + exception.Message);
			return new StratumAnticheatHistoryRecord();
		}
	}

	private static void Save(ServerMain server, ServerPlayerData target, StratumAnticheatHistoryRecord record)
	{
		target.CustomPlayerData ??= new Dictionary<string, string>();
		target.CustomPlayerData[HistoryKey] = JsonConvert.SerializeObject(record);
		server.PlayerDataManager.playerDataDirty = true;
	}
}

internal sealed class StratumAnticheatHistoryRecord
{
	public int TotalFlags { get; set; }

	// Ordinal of StratumAnticheatPunishments.Tier. Kept as a plain int here rather than the enum
	// itself so this record never needs to change shape if the ladder gains or reorders tiers.
	public int HighestPunishmentApplied { get; set; }

	public DateTime? LastFlagUtc { get; set; }

	public DateTime? LastPunishmentUtc { get; set; }
}
