using System;
using System.Collections.Generic;
using System.Globalization;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

internal static class StratumStaffCommandState
{
	public const string LastSeenUtcKey = "stratum.lastSeenUtc";

	public const string LastSeenStateKey = "stratum.lastSeenState";

	private static readonly Dictionary<string, EntityPos> BackPositions = new Dictionary<string, EntityPos>(StringComparer.Ordinal);

	private static readonly HashSet<string> VanishedPlayerUids = new HashSet<string>(StringComparer.Ordinal);

	private static readonly Dictionary<string, FrozenPlayerState> FrozenPlayers = new Dictionary<string, FrozenPlayerState>(StringComparer.Ordinal);

	private static readonly Dictionary<string, DateTime> LastSlowmodeChatUtcByUid = new Dictionary<string, DateTime>(StringComparer.Ordinal);

	private static bool chatLocked;

	private static string chatLockReason;

	private static string chatLockActor;

	private static DateTime chatLockUtc;

	private static int slowmodeSeconds;

	private static string slowmodeActor;

	private static DateTime slowmodeUtc;

	public static IEnumerable<FrozenPlayerState> FrozenSnapshots => FrozenPlayers.Values;

	public static bool IsChatLocked => chatLocked;

	public static string ChatLockReason => chatLockReason;

	public static string ChatLockActor => chatLockActor;

	public static DateTime ChatLockUtc => chatLockUtc;

	public static int SlowmodeSeconds => slowmodeSeconds;

	public static string SlowmodeActor => slowmodeActor;

	public static DateTime SlowmodeUtc => slowmodeUtc;

	public static void RecordBackLocation(IServerPlayer player)
	{
		if (player?.Entity?.Pos == null || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return;
		}

		BackPositions[player.PlayerUID] = player.Entity.Pos.Copy();
	}

	public static bool TryGetBackLocation(string playerUid, out EntityPos position)
	{
		if (BackPositions.TryGetValue(playerUid, out EntityPos stored))
		{
			position = stored.Copy();
			return true;
		}

		position = null;
		return false;
	}

	public static bool IsVanished(string playerUid)
	{
		return !string.IsNullOrWhiteSpace(playerUid) && VanishedPlayerUids.Contains(playerUid);
	}

	public static bool SetVanished(IServerPlayer player, bool vanished)
	{
		if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return false;
		}

		return vanished ? VanishedPlayerUids.Add(player.PlayerUID) : VanishedPlayerUids.Remove(player.PlayerUID);
	}

	public static bool ShouldHideEntityFromClient(Entity entity, ConnectedClient client)
	{
		if (entity is not EntityPlayer entityPlayer || client?.Player == null || !IsVanished(entityPlayer.PlayerUID))
		{
			return false;
		}

		IServerPlayer viewer = client.Player;
		if (viewer.PlayerUID == entityPlayer.PlayerUID)
		{
			return false;
		}

		StratumRuntime.Config.EnsurePopulated();
		return !StratumCommandAccessCatalog.PlayerHasAccess(viewer, StratumRuntime.Config.Commands.Vanish);
	}

	public static void HideVanishedPlayerFromOthers(ServerMain server, IServerPlayer player)
	{
		if (player?.Entity == null)
		{
			return;
		}

		Packet_Server packet = ServerPackets.GetEntityDespawnPacket(new List<EntityDespawn>
		{
			new EntityDespawn
			{
				EntityId = player.Entity.EntityId,
				DespawnData = new EntityDespawnData { Reason = EnumDespawnReason.Unload }
			}
		});

		foreach (ConnectedClient client in server.Clients.Values)
		{
			if (client.State.IsAdmitted() && ShouldHideEntityFromClient(player.Entity, client))
			{
				client.TrackedEntities.Remove(player.Entity.EntityId);
				server.SendPacket(client.Id, packet);
			}
		}
	}

	public static void RevealPlayerToOthers(ServerMain server, IServerPlayer player)
	{
		if (player?.Entity == null)
		{
			return;
		}

		Packet_Server packet = ServerPackets.GetEntitySpawnPacket(new List<Entity> { player.Entity });
		int rangeSq = MagicNum.DefaultEntityTrackingRange * MagicNum.ServerChunkSize * MagicNum.DefaultEntityTrackingRange * MagicNum.ServerChunkSize;
		foreach (ConnectedClient client in server.Clients.Values)
		{
			if (!client.State.IsAdmitted() || client.Player == null || client.Player.PlayerUID == player.PlayerUID || ShouldHideEntityFromClient(player.Entity, client))
			{
				continue;
			}

			if (player.Entity.Pos.InRangeOf(client.Player.Entity.Pos, rangeSq))
			{
				client.TrackedEntities.Add(player.Entity.EntityId);
				server.SendPacket(client.Id, packet);
			}
		}
	}

	public static bool IsFrozen(string playerUid)
	{
		return !string.IsNullOrWhiteSpace(playerUid) && FrozenPlayers.ContainsKey(playerUid);
	}

	public static bool Freeze(IServerPlayer player)
	{
		if (player?.Entity?.Pos == null || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return false;
		}

		FrozenPlayers[player.PlayerUID] = new FrozenPlayerState(player.PlayerUID, player.PlayerName, player.Entity.Pos.Copy());
		return true;
	}

	public static bool Unfreeze(string playerUid)
	{
		return !string.IsNullOrWhiteSpace(playerUid) && FrozenPlayers.Remove(playerUid);
	}

	public static void SetChatLocked(bool locked, string reason, string actor)
	{
		chatLocked = locked;
		chatLockReason = locked ? reason : null;
		chatLockActor = actor;
		chatLockUtc = DateTime.UtcNow;
	}

	public static void SetSlowmode(int seconds, string actor)
	{
		slowmodeSeconds = Math.Max(0, seconds);
		slowmodeActor = actor;
		slowmodeUtc = DateTime.UtcNow;
		if (slowmodeSeconds == 0)
		{
			LastSlowmodeChatUtcByUid.Clear();
		}
	}

	public static bool TryRejectBySlowmode(IServerPlayer player, DateTime nowUtc, out TimeSpan remaining)
	{
		remaining = TimeSpan.Zero;
		if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID) || slowmodeSeconds <= 0)
		{
			return false;
		}

		if (LastSlowmodeChatUtcByUid.TryGetValue(player.PlayerUID, out DateTime lastChatUtc))
		{
			TimeSpan elapsed = nowUtc - lastChatUtc;
			TimeSpan required = TimeSpan.FromSeconds(slowmodeSeconds);
			if (elapsed < required)
			{
				remaining = required - elapsed;
				return true;
			}
		}

		LastSlowmodeChatUtcByUid[player.PlayerUID] = nowUtc;
		return false;
	}

	public static void ClearSessionState(string playerUid)
	{
		if (string.IsNullOrWhiteSpace(playerUid))
		{
			return;
		}

		VanishedPlayerUids.Remove(playerUid);
		FrozenPlayers.Remove(playerUid);
		LastSlowmodeChatUtcByUid.Remove(playerUid);
	}

	public static void MarkSeen(ServerMain server, IServerPlayer player, string state)
	{
		if (player == null)
		{
			return;
		}

		ServerPlayerData data = server.PlayerDataManager.GetOrCreateServerPlayerData(player.PlayerUID, player.PlayerName);
		data.CustomPlayerData ??= new Dictionary<string, string>();
		data.CustomPlayerData[LastSeenUtcKey] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
		data.CustomPlayerData[LastSeenStateKey] = state;
		server.PlayerDataManager.playerDataDirty = true;
	}

	public static bool TryGetLastSeen(ServerPlayerData data, out DateTime utc, out string state)
	{
		utc = default;
		state = null;
		if (data?.CustomPlayerData == null || !data.CustomPlayerData.TryGetValue(LastSeenUtcKey, out string raw))
		{
			return false;
		}

		data.CustomPlayerData.TryGetValue(LastSeenStateKey, out state);
		return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out utc);
	}
}

internal sealed class FrozenPlayerState
{
	public FrozenPlayerState(string playerUid, string playerName, EntityPos position)
	{
		PlayerUid = playerUid;
		PlayerName = playerName;
		Position = position;
	}

	public string PlayerUid { get; }

	public string PlayerName { get; }

	public EntityPos Position { get; }
}