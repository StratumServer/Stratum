using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Vintagestory.Server;

internal static class StratumModerationStore
{
	private const string PlayerRecordsKey = "stratum.moderation.records.v1";
	private const string WarningType = "warning";
	private const string MuteType = "mute";
	private const string NoteType = "note";

	public static StratumModerationRecord AddWarning(ServerMain server, ServerPlayerData target, Caller actor, string reason)
	{
		return AddPlayerRecord(server, target, actor, WarningType, reason, null);
	}

	public static StratumModerationRecord AddMute(ServerMain server, ServerPlayerData target, Caller actor, string reason, DateTime? expiresUtc)
	{
		List<StratumModerationRecord> records = LoadPlayerRecords(target);
		foreach (StratumModerationRecord record in records)
		{
			if (record.Type == MuteType && record.Active)
			{
				record.Active = false;
				record.ClosedUtc = DateTime.UtcNow;
				record.ClosedBy = actor.GetName();
				record.CloseReason = "replaced by a newer mute";
			}
		}

		StratumModerationRecord mute = CreateRecord(target, actor, MuteType, reason, expiresUtc, NextRecordId(records));
		records.Add(mute);
		SavePlayerRecords(server, target, records);
		return mute;
	}

	public static StratumModerationRecord AddNote(ServerMain server, ServerPlayerData target, Caller actor, string text)
	{
		return AddPlayerRecord(server, target, actor, NoteType, text, null);
	}

	public static IReadOnlyList<StratumModerationRecord> GetWarnings(ServerPlayerData target)
	{
		return LoadPlayerRecords(target).Where(record => record.Type == WarningType && record.Active).ToArray();
	}

	public static IReadOnlyList<StratumModerationRecord> GetNotes(ServerPlayerData target)
	{
		return LoadPlayerRecords(target).Where(record => record.Type == NoteType && record.Active).ToArray();
	}

	public static IReadOnlyList<StratumModerationRecord> GetHistory(ServerPlayerData target)
	{
		return LoadPlayerRecords(target).OrderByDescending(record => record.CreatedUtc).ToArray();
	}

	public static bool DeleteWarning(ServerMain server, ServerPlayerData target, int recordId, Caller actor, string reason)
	{
		return CloseRecord(server, target, WarningType, recordId, actor, string.IsNullOrWhiteSpace(reason) ? "deleted" : reason);
	}

	public static bool DeleteNote(ServerMain server, ServerPlayerData target, int recordId, Caller actor, string reason)
	{
		return CloseRecord(server, target, NoteType, recordId, actor, string.IsNullOrWhiteSpace(reason) ? "deleted" : reason);
	}

	public static bool Unmute(ServerMain server, ServerPlayerData target, Caller actor, string reason)
	{
		List<StratumModerationRecord> records = LoadPlayerRecords(target);
		bool changed = false;
		foreach (StratumModerationRecord record in records)
		{
			if (record.Type == MuteType && record.Active)
			{
				record.Active = false;
				record.ClosedUtc = DateTime.UtcNow;
				record.ClosedBy = actor.GetName();
				record.CloseReason = string.IsNullOrWhiteSpace(reason) ? "unmuted" : reason;
				changed = true;
			}
		}

		if (changed)
		{
			SavePlayerRecords(server, target, records);
		}

		return changed;
	}

	public static bool TryGetActiveMute(ServerMain server, ServerPlayerData target, out StratumModerationRecord activeMute)
	{
		activeMute = null;
		List<StratumModerationRecord> records = LoadPlayerRecords(target);
		bool changed = false;
		DateTime nowUtc = DateTime.UtcNow;

		foreach (StratumModerationRecord record in records.Where(record => record.Type == MuteType && record.Active))
		{
			if (record.ExpiresUtc.HasValue && record.ExpiresUtc.Value <= nowUtc)
			{
				record.Active = false;
				record.ClosedUtc = nowUtc;
				record.ClosedBy = "system";
				record.CloseReason = "expired";
				changed = true;
				continue;
			}

			activeMute = record;
		}

		if (changed)
		{
			SavePlayerRecords(server, target, records);
		}

		return activeMute != null;
	}

	public static string FormatExpiry(StratumModerationRecord record)
	{
		return record?.ExpiresUtc == null ? "permanent" : record.ExpiresUtc.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
	}

	private static StratumModerationRecord AddPlayerRecord(ServerMain server, ServerPlayerData target, Caller actor, string type, string text, DateTime? expiresUtc)
	{
		List<StratumModerationRecord> records = LoadPlayerRecords(target);
		StratumModerationRecord record = CreateRecord(target, actor, type, text, expiresUtc, NextRecordId(records));
		records.Add(record);
		SavePlayerRecords(server, target, records);
		return record;
	}

	private static bool CloseRecord(ServerMain server, ServerPlayerData target, string type, int recordId, Caller actor, string reason)
	{
		List<StratumModerationRecord> records = LoadPlayerRecords(target);
		StratumModerationRecord record = records.FirstOrDefault(entry => entry.Type == type && entry.Id == recordId && entry.Active);
		if (record == null)
		{
			return false;
		}

		record.Active = false;
		record.ClosedUtc = DateTime.UtcNow;
		record.ClosedBy = actor.GetName();
		record.CloseReason = reason;
		SavePlayerRecords(server, target, records);
		return true;
	}

	private static StratumModerationRecord CreateRecord(ServerPlayerData target, Caller actor, string type, string text, DateTime? expiresUtc, int id)
	{
		return new StratumModerationRecord
		{
			Id = id,
			Type = type,
			TargetUid = target.PlayerUID,
			TargetName = target.LastKnownPlayername,
			ActorUid = actor.Player?.PlayerUID,
			ActorName = actor.GetName(),
			Text = text ?? string.Empty,
			CreatedUtc = DateTime.UtcNow,
			ExpiresUtc = expiresUtc,
			Active = true
		};
	}

	private static int NextRecordId(List<StratumModerationRecord> records)
	{
		return records.Count == 0 ? 1 : records.Max(record => record.Id) + 1;
	}

	private static List<StratumModerationRecord> LoadPlayerRecords(ServerPlayerData target)
	{
		if (target?.CustomPlayerData == null || !target.CustomPlayerData.TryGetValue(PlayerRecordsKey, out string json) || string.IsNullOrWhiteSpace(json))
		{
			return new List<StratumModerationRecord>();
		}

		try
		{
			return JsonConvert.DeserializeObject<List<StratumModerationRecord>>(json) ?? new List<StratumModerationRecord>();
		}
		catch (Exception exception)
		{
			StratumRuntime.LogWarning("failed to read moderation records for " + target.LastKnownPlayername + ": " + exception.Message);
			return new List<StratumModerationRecord>();
		}
	}

	private static void SavePlayerRecords(ServerMain server, ServerPlayerData target, List<StratumModerationRecord> records)
	{
		target.CustomPlayerData ??= new Dictionary<string, string>();
		target.CustomPlayerData[PlayerRecordsKey] = JsonConvert.SerializeObject(records);
		server.PlayerDataManager.playerDataDirty = true;
	}
}

internal sealed class StratumModerationRecord
{
	public int Id { get; set; }

	public string Type { get; set; }

	public string TargetUid { get; set; }

	public string TargetName { get; set; }

	public string ActorUid { get; set; }

	public string ActorName { get; set; }

	public string Text { get; set; }

	public DateTime CreatedUtc { get; set; }

	public DateTime? ExpiresUtc { get; set; }

	public bool Active { get; set; }

	public DateTime? ClosedUtc { get; set; }

	public string ClosedBy { get; set; }

	public string CloseReason { get; set; }
}

internal static class StratumReportStore
{
	private static readonly object StoreLock = new object();
	private static StratumReportState state;

	public static StratumReportEntry AddReport(string reporterUid, string reporterName, string targetUid, string targetName, string reason)
	{
		lock (StoreLock)
		{
			EnsureLoaded();
			StratumReportEntry report = new StratumReportEntry
			{
				Id = state.NextId++,
				CreatedUtc = DateTime.UtcNow,
				ReporterUid = reporterUid,
				ReporterName = reporterName,
				TargetUid = targetUid,
				TargetName = targetName,
				Reason = reason,
				Status = "open"
			};

			state.Reports.Add(report);
			Save();
			return report;
		}
	}

	public static IReadOnlyList<StratumReportEntry> ListReports(string status)
	{
		lock (StoreLock)
		{
			EnsureLoaded();
			IEnumerable<StratumReportEntry> reports = state.Reports;
			if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
			{
				reports = reports.Where(report => string.Equals(report.Status, status, StringComparison.OrdinalIgnoreCase));
			}

			return reports.OrderByDescending(report => report.CreatedUtc).ToArray();
		}
	}

	public static bool TryGetReport(int id, out StratumReportEntry report)
	{
		lock (StoreLock)
		{
			EnsureLoaded();
			report = state.Reports.FirstOrDefault(entry => entry.Id == id);
			return report != null;
		}
	}

	public static bool ClaimReport(int id, string staffName, out StratumReportEntry report)
	{
		lock (StoreLock)
		{
			EnsureLoaded();
			report = state.Reports.FirstOrDefault(entry => entry.Id == id);
			if (report == null || string.Equals(report.Status, "closed", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			report.Status = "claimed";
			report.ClaimedBy = staffName;
			report.ClaimedUtc = DateTime.UtcNow;
			Save();
			return true;
		}
	}

	public static bool CloseReport(int id, string staffName, string resolution, out StratumReportEntry report)
	{
		lock (StoreLock)
		{
			EnsureLoaded();
			report = state.Reports.FirstOrDefault(entry => entry.Id == id);
			if (report == null || string.Equals(report.Status, "closed", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			report.Status = "closed";
			report.ClosedBy = staffName;
			report.ClosedUtc = DateTime.UtcNow;
			report.Resolution = resolution ?? string.Empty;
			Save();
			return true;
		}
	}

	private static void EnsureLoaded()
	{
		if (state != null)
		{
			return;
		}

		string path = StorePath;
		if (!File.Exists(path))
		{
			state = new StratumReportState();
			return;
		}

		try
		{
			state = JsonConvert.DeserializeObject<StratumReportState>(File.ReadAllText(path)) ?? new StratumReportState();
			state.Reports ??= new List<StratumReportEntry>();
			state.NextId = Math.Max(state.NextId, state.Reports.Count == 0 ? 1 : state.Reports.Max(report => report.Id) + 1);
		}
		catch (Exception exception)
		{
			StratumRuntime.LogWarning("failed to read report store: " + exception.Message);
			state = new StratumReportState();
		}
	}

	private static void Save()
	{
		GamePaths.EnsurePathExists(GamePaths.Config);
		File.WriteAllText(StorePath, JsonConvert.SerializeObject(state, Formatting.Indented));
	}

	private static string StorePath => Path.Combine(GamePaths.Config, "stratum.reports.json");
}

internal sealed class StratumReportState
{
	public int NextId { get; set; } = 1;

	public List<StratumReportEntry> Reports { get; set; } = new List<StratumReportEntry>();
}

internal sealed class StratumReportEntry
{
	public int Id { get; set; }

	public DateTime CreatedUtc { get; set; }

	public string ReporterUid { get; set; }

	public string ReporterName { get; set; }

	public string TargetUid { get; set; }

	public string TargetName { get; set; }

	public string Reason { get; set; }

	public string Status { get; set; }

	public string ClaimedBy { get; set; }

	public DateTime? ClaimedUtc { get; set; }

	public string ClosedBy { get; set; }

	public DateTime? ClosedUtc { get; set; }

	public string Resolution { get; set; }
}