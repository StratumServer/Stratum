using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Config;

namespace Vintagestory.Server;

// #278: class change requests live in one server-wide file, like /report, so staff see a single
// queue. The count of self-service changes a player has used lives on their player data.
internal static class StratumClassChangeStore
{
	public const string StatusPending = "pending";
	public const string StatusApproved = "approved";
	public const string StatusDenied = "denied";
	public const string StatusCancelled = "cancelled";

	private const string SelfServiceUsedKey = "stratum.class-changes-used.v1";

	// Save keeps the most recent MaxRetainedClosedRequests closed requests and drops the rest. It
	// never prunes a pending request, or a decided one the player has not been told about.
	private const int MaxRetainedClosedRequests = 200;

	private static readonly object StoreLock = new object();
	private static StratumClassChangeState state;

	public static int GetSelfServiceUsed(ServerPlayerData playerData)
	{
		if (playerData?.CustomPlayerData == null || !playerData.CustomPlayerData.TryGetValue(SelfServiceUsedKey, out string json) || string.IsNullOrWhiteSpace(json))
		{
			return 0;
		}

		try
		{
			return Math.Max(0, JsonConvert.DeserializeObject<int>(json));
		}
		catch (Exception exception)
		{
			StratumRuntime.LogWarning("failed to read class change count for " + playerData.LastKnownPlayername + ": " + exception.Message);
			return 0;
		}
	}

	public static void AddSelfServiceUsed(ServerMain server, ServerPlayerData playerData)
	{
		playerData.CustomPlayerData ??= new Dictionary<string, string>();
		int used = GetSelfServiceUsed(playerData);
		playerData.CustomPlayerData[SelfServiceUsedKey] = JsonConvert.SerializeObject(used == int.MaxValue ? used : used + 1);
		server.PlayerDataManager.playerDataDirty = true;
	}

	public static StratumClassChangeRequest AddRequest(string playerUid, string playerName, string currentClass, string requestedClass, string reason, out StratumClassChangeRequest replaced)
	{
		lock (StoreLock)
		{
			EnsureLoaded();
			// One open request per player: a new one replaces the last, so the queue never holds
			// two conflicting asks from the same player.
			replaced = FindPending(playerUid);
			if (replaced != null)
			{
				replaced.Status = StatusCancelled;
				replaced.DecidedUtc = DateTime.UtcNow;
				replaced.Note = "replaced by a newer request";
			}

			StratumClassChangeRequest request = new StratumClassChangeRequest
			{
				Id = state.NextId++,
				CreatedUtc = DateTime.UtcNow,
				PlayerUid = playerUid,
				PlayerName = playerName,
				CurrentClass = currentClass,
				RequestedClass = requestedClass,
				Reason = reason ?? string.Empty,
				Status = StatusPending
			};

			state.Requests.Add(request);
			Save();
			return request;
		}
	}

	public static StratumClassChangeRequest GetPending(string playerUid)
	{
		lock (StoreLock)
		{
			EnsureLoaded();
			return FindPending(playerUid);
		}
	}

	public static IReadOnlyList<StratumClassChangeRequest> ListRequests(string status)
	{
		lock (StoreLock)
		{
			EnsureLoaded();
			IEnumerable<StratumClassChangeRequest> requests = state.Requests;
			if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
			{
				requests = requests.Where(request => string.Equals(request.Status, status, StringComparison.OrdinalIgnoreCase));
			}

			return requests.OrderByDescending(request => request.CreatedUtc).ToArray();
		}
	}

	public static bool TryGetRequest(int id, out StratumClassChangeRequest request)
	{
		lock (StoreLock)
		{
			EnsureLoaded();
			request = state.Requests.FirstOrDefault(entry => entry.Id == id);
			return request != null;
		}
	}

	public static bool Decide(int id, string status, string staffName, string note, out StratumClassChangeRequest request)
	{
		lock (StoreLock)
		{
			EnsureLoaded();
			request = state.Requests.FirstOrDefault(entry => entry.Id == id);
			if (request == null || request.Status != StatusPending)
			{
				return false;
			}

			request.Status = status;
			request.DecidedBy = staffName;
			request.DecidedUtc = DateTime.UtcNow;
			request.Note = note ?? string.Empty;
			Save();
			return true;
		}
	}

	public static StratumClassChangeRequest CancelPending(string playerUid)
	{
		lock (StoreLock)
		{
			EnsureLoaded();
			StratumClassChangeRequest request = FindPending(playerUid);
			if (request == null)
			{
				return null;
			}

			request.Status = StatusCancelled;
			request.DecidedUtc = DateTime.UtcNow;
			request.Note = "cancelled by the player";
			Save();
			return request;
		}
	}

	// Approved or denied requests the player has not been told about yet, because they were
	// offline when staff decided. An approved one among them has not been applied either.
	public static IReadOnlyList<StratumClassChangeRequest> ListUndelivered(string playerUid)
	{
		lock (StoreLock)
		{
			EnsureLoaded();
			return state.Requests
				.Where(request => request.PlayerUid == playerUid && !request.PlayerNotified && (request.Status == StatusApproved || request.Status == StatusDenied))
				.OrderBy(request => request.DecidedUtc)
				.ToArray();
		}
	}

	public static void MarkDelivered(StratumClassChangeRequest request, bool applied)
	{
		lock (StoreLock)
		{
			EnsureLoaded();
			request.PlayerNotified = true;
			if (applied)
			{
				request.Applied = true;
				request.AppliedUtc = DateTime.UtcNow;
			}
			Save();
		}
	}

	private static StratumClassChangeRequest FindPending(string playerUid)
	{
		return state.Requests.FirstOrDefault(request => request.Status == StatusPending && request.PlayerUid == playerUid);
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
			state = new StratumClassChangeState();
			return;
		}

		try
		{
			state = JsonConvert.DeserializeObject<StratumClassChangeState>(File.ReadAllText(path)) ?? new StratumClassChangeState();
			state.Requests ??= new List<StratumClassChangeRequest>();
			state.NextId = Math.Max(state.NextId, state.Requests.Count == 0 ? 1 : state.Requests.Max(request => request.Id) + 1);
		}
		catch (Exception exception)
		{
			StratumRuntime.LogWarning("failed to read class change request store: " + exception.Message);
			state = new StratumClassChangeState();
		}
	}

	private static void Save()
	{
		PruneClosedRequests();
		GamePaths.EnsurePathExists(GamePaths.Config);
		File.WriteAllText(StorePath, JsonConvert.SerializeObject(state, Formatting.Indented));
	}

	private static void PruneClosedRequests()
	{
		int closedCount = state.Requests.Count(IsClosed);
		int toDrop = closedCount - MaxRetainedClosedRequests;
		int index = 0;
		while (index < state.Requests.Count && toDrop > 0)
		{
			if (IsClosed(state.Requests[index]))
			{
				state.Requests.RemoveAt(index);
				toDrop--;
			}
			else
			{
				index++;
			}
		}
	}

	private static bool IsClosed(StratumClassChangeRequest request)
	{
		return request.Status != StatusPending && (request.Status == StatusCancelled || request.PlayerNotified);
	}

	private static string StorePath => Path.Combine(GamePaths.Config, "stratum.classrequests.json");
}

internal sealed class StratumClassChangeState
{
	public int NextId { get; set; } = 1;

	public List<StratumClassChangeRequest> Requests { get; set; } = new List<StratumClassChangeRequest>();
}

internal sealed class StratumClassChangeRequest
{
	public int Id { get; set; }

	public DateTime CreatedUtc { get; set; }

	public string PlayerUid { get; set; }

	public string PlayerName { get; set; }

	public string CurrentClass { get; set; }

	public string RequestedClass { get; set; }

	public string Reason { get; set; }

	public string Status { get; set; }

	public string DecidedBy { get; set; }

	public DateTime? DecidedUtc { get; set; }

	public string Note { get; set; }

	public bool PlayerNotified { get; set; }

	public bool Applied { get; set; }

	public DateTime? AppliedUtc { get; set; }
}
