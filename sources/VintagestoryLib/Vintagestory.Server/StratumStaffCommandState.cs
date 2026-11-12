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

	public const string VanishStateKey = "stratum.vanishEnabled";

	private static readonly Dictionary<string, EntityPos> BackPositions = new Dictionary<string, EntityPos>(StringComparer.Ordinal);

	private static readonly HashSet<string> VanishedPlayerUids = new HashSet<string>(StringComparer.Ordinal);

	// Stratum #213: per-viewer override of "do I see other vanished players".
	// Absent = follow Commands.VanishHideOtherVanishedDefault. Pure session state: unlike vanish
	// itself (#312, persisted in ServerPlayerData.CustomPlayerData and re-read at identification)
	// this preference is not stored, so it resets to the configured default on every reconnect.
	// Cleared in ClearSessionState.
	private static readonly Dictionary<string, bool> HideOtherVanishedByUid = new Dictionary<string, bool>(StringComparer.Ordinal);

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

	public static bool SetVanished(ServerMain server, IServerPlayer player, bool vanished)
	{
		if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return false;
		}

		bool changed = vanished ? VanishedPlayerUids.Add(player.PlayerUID) : VanishedPlayerUids.Remove(player.PlayerUID);
		PersistVanishedState(server, player, vanished);
		return changed;
	}

	public static void RestoreVanishedState(ServerMain server, IServerPlayer player)
	{
		RestoreVanishedState(server, player, persistRevocation: true);
	}

	// Stratum #312: called twice per session. First from ServerMain.FinalizePlayerIdentification,
	// before the first SpawnEntity/SendServerReady, so a reconnecting vanished player is never
	// broadcast to nearby clients during the seconds between identification and RequestJoin. That
	// call passes persistRevocation: false, because the role is not final there yet (a
	// single-player client is upgraded in HandleRequestJoin) and a stored flag must not be erased
	// on a role that is still settling. It is called again from CmdStratumStaffCommands' OnPlayerJoin
	// handler, where the role is final and a revoked privilege can be persisted away.
	public static void RestoreVanishedState(ServerMain server, IServerPlayer player, bool persistRevocation)
	{
		if (server == null || player == null || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return;
		}

		ServerPlayerData data = server.PlayerDataManager.GetOrCreateServerPlayerData(player.PlayerUID, player.PlayerName);
		if (data.CustomPlayerData == null || !data.CustomPlayerData.TryGetValue(VanishStateKey, out string raw)
			|| !bool.TryParse(raw, out bool vanished) || !vanished)
		{
			VanishedPlayerUids.Remove(player.PlayerUID);
			return;
		}

		StratumRuntime.Config.EnsurePopulated();
		if (StratumCommandAccessCatalog.PlayerHasAccess(player, StratumRuntime.Config.Commands.Vanish))
		{
			VanishedPlayerUids.Add(player.PlayerUID);
			return;
		}

		VanishedPlayerUids.Remove(player.PlayerUID);
		if (persistRevocation)
		{
			data.CustomPlayerData[VanishStateKey] = bool.FalseString;
			server.PlayerDataManager.playerDataDirty = true;
		}
	}

	private static void PersistVanishedState(ServerMain server, IServerPlayer player, bool vanished)
	{
		if (server == null || player == null || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return;
		}

		ServerPlayerData data = server.PlayerDataManager.GetOrCreateServerPlayerData(player.PlayerUID, player.PlayerName);
		data.CustomPlayerData ??= new Dictionary<string, string>();
		data.CustomPlayerData[VanishStateKey] = vanished ? bool.TrueString : bool.FalseString;
		server.PlayerDataManager.playerDataDirty = true;
	}

	public static bool HidesOtherVanished(string viewerUid)
	{
		if (string.IsNullOrWhiteSpace(viewerUid))
		{
			return false;
		}

		if (HideOtherVanishedByUid.TryGetValue(viewerUid, out bool preference))
		{
			return preference;
		}

		return StratumRuntime.Config.Commands.VanishHideOtherVanishedDefault;
	}

	public static void SetHidesOtherVanished(string viewerUid, bool hide)
	{
		if (!string.IsNullOrWhiteSpace(viewerUid))
		{
			HideOtherVanishedByUid[viewerUid] = hide;
		}
	}

	public static bool ShouldHideEntityFromClient(Entity entity, ConnectedClient client)
	{
		// Stratum #312: nobody vanished is the overwhelmingly common case, and this runs once per
		// client per entity in UpdateTrackedEntityLists, SendAttributesViaTCP, the state-tick loop
		// and the per-packet position/animation loops. Bail before any type test or behaviour walk.
		if (VanishedPlayerUids.Count == 0)
		{
			return false;
		}

		if (entity == null || client?.Player == null)
		{
			return false;
		}

		if (entity is EntityPlayer entityPlayer && IsVanished(entityPlayer.PlayerUID))
		{
			return ShouldHideVanishedPlayerFromClient(entityPlayer, client);
		}

		// Stratum #312: a projectile is spawned at the shooter's eye height and carries firedBy
		// (EntityProjectileBase.Initialize writes it server side before the spawn packet is built),
		// so a vanished shooter is located by their own arrow. Filtering the predicate rather than
		// only SendPrioritySpawn hides it for its whole flight, including the position updates.
		if (entity is IProjectile projectile && projectile.FiredBy is EntityPlayer shooter && IsVanished(shooter.PlayerUID))
		{
			return ShouldHideVanishedPlayerFromClient(shooter, client);
		}

		IMountable mountable = entity.GetInterface<IMountable>();
		if (mountable?.Seats == null)
		{
			return false;
		}

		// Stratum #312: every seat is inspected before deciding. A shared mount stays visible to
		// bystanders when it carries an ordinary player, because hiding it leaves that player
		// floating and still exposes the vanished passenger's position. The documented limitation
		// is that the mount's seat data can still reveal the vanished passenger.
		EntityPlayer vanishedPassenger = null;
		foreach (IMountableSeat seat in mountable.Seats)
		{
			if (seat?.Passenger is not EntityPlayer passenger)
			{
				continue;
			}

			if (passenger == client.Entityplayer)
			{
				return false;
			}

			if (!IsVanished(passenger.PlayerUID))
			{
				// A bystander must still see a mount carrying an ordinary player.
				return false;
			}

			if (vanishedPassenger == null)
			{
				vanishedPassenger = passenger;
			}
		}

		return vanishedPassenger != null && ShouldHideVanishedPlayerFromClient(vanishedPassenger, client);
	}

	private static bool ShouldHideVanishedPlayerFromClient(EntityPlayer entityPlayer, ConnectedClient client)
	{
		IServerPlayer viewer = client.Player;
		if (viewer.PlayerUID == entityPlayer.PlayerUID)
		{
			return false;
		}

		StratumRuntime.Config.EnsurePopulated();
		if (!StratumCommandAccessCatalog.PlayerHasAccess(viewer, StratumRuntime.Config.Commands.Vanish))
		{
			// Non-staff never see a vanished player.
			return true;
		}

		// Stratum #213: staff who opted in stop seeing other vanished staff.
		// One-directional on purpose: this is the viewer's own preference.
		return HidesOtherVanished(viewer.PlayerUID);
	}

	private static bool IsVanishVisibilitySubject(Entity entity)
	{
		// Stratum #312: same early-out as ShouldHideEntityFromClient. This walks all of
		// LoadedEntities, so with nobody vanished it must not touch the behaviour list either.
		if (VanishedPlayerUids.Count == 0 || entity == null)
		{
			return false;
		}

		if (entity is EntityPlayer player && IsVanished(player.PlayerUID))
		{
			return true;
		}

		if (entity is IProjectile projectile && projectile.FiredBy is EntityPlayer shooter && IsVanished(shooter.PlayerUID))
		{
			return true;
		}

		IMountable mountable = entity.GetInterface<IMountable>();
		if (mountable?.Seats == null)
		{
			return false;
		}

		foreach (IMountableSeat seat in mountable.Seats)
		{
			if (seat?.Passenger is EntityPlayer passenger && IsVanished(passenger.PlayerUID))
			{
				return true;
			}
		}

		return false;
	}

	public static void HideVanishedPlayerFromOthers(ServerMain server, IServerPlayer player)
	{
		RefreshVanishedVisibilityForAllViewers(server);
	}

	public static void RevealPlayerToOthers(ServerMain server, IServerPlayer player)
	{
		if (server == null || player?.Entity == null)
		{
			return;
		}

		Packet_Server spawnPacket = ServerPackets.GetEntitySpawnPacket(new List<Entity> { player.Entity });
		// Stratum #312: packet 41 has to go first. The vanilla client only fills PlayersByUid from
		// player data, and UpdateTrackedEntityLists will never send it later because the id below is
		// already in TrackedEntities. Without it the observer gets an EntityPlayer with no
		// ClientPlayer: empty hands, no armor, missing from the player list until it leaves range.
		Packet_Server dataPacket = ((ServerWorldPlayerData)player.WorldData).ToPacketForOtherPlayers(player);
		int rangeSq = MagicNum.DefaultEntityTrackingRange * MagicNum.ServerChunkSize * MagicNum.DefaultEntityTrackingRange * MagicNum.ServerChunkSize;
		foreach (ConnectedClient client in server.Clients.Values)
		{
			if (!client.State.IsAdmitted() || client.Player?.Entity == null || client.Player.PlayerUID == player.PlayerUID || ShouldHideEntityFromClient(player.Entity, client))
			{
				continue;
			}

			if (player.Entity.Pos.InRangeOf(client.Player.Entity.Pos, rangeSq))
			{
				client.TrackedEntities.Add(player.Entity.EntityId);
				server.SendPacket(client.Id, dataPacket);
				server.SendPacket(client.Id, spawnPacket);
			}
		}

		// Stratum #312: the active hotbar slot number only travels in packet 53, which the filter
		// added to BroadcastHotbarSlot withheld for the whole vanish. Re-broadcasting it here is
		// what makes the revealed player show the right held item instead of an empty hand.
		server.BroadcastHotbarSlot(player);
	}

	private static void RefreshVanishedVisibilityForAllViewers(ServerMain server)
	{
		if (server == null)
		{
			return;
		}

		foreach (ConnectedClient client in server.Clients.Values)
		{
			if (client?.Player?.Entity != null && client.State.IsAdmitted())
			{
				RefreshVanishedVisibilityForViewer(server, client.Player);
			}
		}
	}

	// Stratum #213/#312: the inverse of Hide/RevealPlayerToOthers. Those iterate every client for
	// one subject; this iterates every loaded entity for one viewer, keeping the ones the vanish
	// rules apply to (IsVanishVisibilitySubject: a vanished player, a projectile they fired, or a
	// mount carrying them). It is what makes the /vanish hideothers toggle take effect mid-session.
	//
	// Both halves are fast paths, not correctness requirements: UpdateTrackedEntityLists
	// re-evaluates ShouldHideEntityFromClient on every state tick, despawning a newly hidden entity
	// (PhysicsManager's tracking hysteresis loop checks the predicate before the outer-radius
	// keep-alive) and re-spawning a newly visible one within a tick or two either way. Doing it
	// here makes a /vanish toggle instant rather than tick-latent.
	public static void RefreshVanishedVisibilityForViewer(ServerMain server, IServerPlayer viewer)
	{
		if (server == null || viewer?.Entity == null || string.IsNullOrWhiteSpace(viewer.PlayerUID))
		{
			return;
		}

		if (!server.Clients.TryGetValue(viewer.ClientId, out ConnectedClient viewerClient)
			|| viewerClient.Player == null || !viewerClient.State.IsAdmitted())
		{
			return;
		}

		List<EntityDespawn> despawns = null;
		List<Entity> spawns = null;
		int rangeSq = MagicNum.DefaultEntityTrackingRange * MagicNum.ServerChunkSize * MagicNum.DefaultEntityTrackingRange * MagicNum.ServerChunkSize;

		foreach (Entity subject in server.LoadedEntities.Values)
		{
			if (!IsVanishVisibilitySubject(subject) || subject == viewer.Entity)
			{
				continue;
			}

			long entityId = subject.EntityId;
			bool tracked = viewerClient.TrackedEntities.Contains(entityId);
			bool hide = ShouldHideEntityFromClient(subject, viewerClient);

			if (hide && tracked)
			{
				viewerClient.TrackedEntities.Remove(entityId);
				foreach (List<Entity> threadedEntities in viewerClient.threadedTrackedEntities ?? Array.Empty<List<Entity>>())
				{
					threadedEntities?.RemoveAll(entity => entity?.EntityId == entityId);
				}
				(despawns ??= new List<EntityDespawn>()).Add(new EntityDespawn
				{
					EntityId = entityId,
					DespawnData = new EntityDespawnData { Reason = EnumDespawnReason.Unload }
				});
			}
			else if (!hide && !tracked && subject.Pos.InRangeOf(viewer.Entity.Pos, rangeSq))
			{
				viewerClient.TrackedEntities.Add(entityId);
				(spawns ??= new List<Entity>()).Add(subject);
			}
		}

		if (despawns != null)
		{
			server.SendPacket(viewerClient.Id, ServerPackets.GetEntityDespawnPacket(despawns));
		}

		if (spawns != null)
		{
			// Stratum #312: same ordering rule as RevealPlayerToOthers -- player data before the
			// spawn, because adding the id to TrackedEntities above means UpdateTrackedEntityLists
			// will not take the entitiesNowInRange path that would otherwise send it.
			foreach (Entity spawn in spawns)
			{
				SendPlayerDataForSpawn(server, spawn, viewerClient);
			}

			server.SendPacket(viewerClient.Id, ServerPackets.GetEntitySpawnPacket(spawns));
		}
	}

	// Stratum #312: packet 41 for one revealed subject, if that subject is a player at all -- a
	// revealed mount or projectile carries no separate player data of its own.
	private static void SendPlayerDataForSpawn(ServerMain server, Entity subject, ConnectedClient viewerClient)
	{
		if (subject is not EntityPlayer entityPlayer
			|| !server.PlayersByUid.TryGetValue(entityPlayer.PlayerUID, out ServerPlayer owner)
			|| owner?.WorldData is not ServerWorldPlayerData worldData)
		{
			return;
		}

		server.SendPacket(viewerClient.Id, worldData.ToPacketForOtherPlayers(owner));
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

		// Stratum #312: dropping the UID here is correct and intentional. Vanish persists in
		// ServerPlayerData.CustomPlayerData and is re-read in FinalizePlayerIdentification, which
		// also covers a server restart; keeping the set populated across a disconnect would not.
		VanishedPlayerUids.Remove(playerUid);
		HideOtherVanishedByUid.Remove(playerUid);
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
