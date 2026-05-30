using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Vintagestory.API.Common;

namespace Vintagestory.Server;

internal static class StratumCustodyStore
{
	private const string JailKey = "stratum.jail.v1";

	public static StratumJailStatus JailPlayer(ServerMain server, ServerPlayerData target, Caller actor, string reason, StratumPositionConfig returnPosition)
	{
		StratumJailStatus status = new StratumJailStatus
		{
			Active = true,
			TargetUid = target.PlayerUID,
			TargetName = target.LastKnownPlayername,
			ActorUid = actor.Player?.PlayerUID,
			ActorName = actor.GetName(),
			Reason = reason ?? string.Empty,
			JailedUtc = DateTime.UtcNow,
			ReturnPosition = returnPosition
		};

		SaveJailStatus(server, target, status);
		return status;
	}

	public static bool UnjailPlayer(ServerMain server, ServerPlayerData target, Caller actor, string reason, out StratumJailStatus status)
	{
		if (!TryGetActiveJail(target, out status))
		{
			return false;
		}

		status.Active = false;
		status.ReleasedUtc = DateTime.UtcNow;
		status.ReleasedBy = actor.GetName();
		status.ReleaseReason = string.IsNullOrWhiteSpace(reason) ? "released" : reason;
		SaveJailStatus(server, target, status);
		return true;
	}

	public static bool TryGetActiveJail(ServerPlayerData target, out StratumJailStatus status)
	{
		status = LoadJailStatus(target);
		return status?.Active == true;
	}

	public static StratumJailStatus LoadJailStatus(ServerPlayerData target)
	{
		if (target?.CustomPlayerData == null || !target.CustomPlayerData.TryGetValue(JailKey, out string json) || string.IsNullOrWhiteSpace(json))
		{
			return null;
		}

		try
		{
			return JsonConvert.DeserializeObject<StratumJailStatus>(json);
		}
		catch (Exception exception)
		{
			StratumRuntime.LogWarning("failed to read jail status for " + target.LastKnownPlayername + ": " + exception.Message);
			return null;
		}
	}

	private static void SaveJailStatus(ServerMain server, ServerPlayerData target, StratumJailStatus status)
	{
		target.CustomPlayerData ??= new Dictionary<string, string>();
		target.CustomPlayerData[JailKey] = JsonConvert.SerializeObject(status);
		server.PlayerDataManager.playerDataDirty = true;
	}
}

internal sealed class StratumJailStatus
{
	public bool Active { get; set; }

	public string TargetUid { get; set; }

	public string TargetName { get; set; }

	public string ActorUid { get; set; }

	public string ActorName { get; set; }

	public string Reason { get; set; }

	public DateTime JailedUtc { get; set; }

	public StratumPositionConfig ReturnPosition { get; set; }

	public DateTime? ReleasedUtc { get; set; }

	public string ReleasedBy { get; set; }

	public string ReleaseReason { get; set; }
}