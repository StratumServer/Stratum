using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

internal static class StratumTeleportWarmups
{
	private static readonly Dictionary<string, PendingTeleportWarmup> PendingByUid = new Dictionary<string, PendingTeleportWarmup>(StringComparer.Ordinal);

	private static readonly Dictionary<string, DateTime> LastTeleportUtcByUid = new Dictionary<string, DateTime>(StringComparer.Ordinal);

	private static bool registered;

	public static void EnsureRegistered(ServerMain server)
	{
		if (registered)
		{
			return;
		}

		registered = true;
		server.RegisterGameTickListener(_ => OnTick(server), 250);
	}

	public static TextCommandResult StartOrTeleport(ServerMain server, IServerPlayer player, EntityPos target, string command, string destinationLabel)
	{
		if (player?.Entity?.Pos == null || target == null)
		{
			return TextCommandResult.Error("Teleport target is not available.");
		}

		StratumRuntime.Config.EnsurePopulated();
		StratumTeleportRequestsConfig config = StratumRuntime.Config.Commands.TeleportRequests;
		if (!RequiresWarmup(player, config))
		{
			CompleteTeleport(server, player, target.Copy(), command, destinationLabel);
			return TextCommandResult.Success();
		}

		if (PendingByUid.ContainsKey(player.PlayerUID))
		{
			return TextCommandResult.Error("You already have a teleport pending. Stay still or wait for it to finish.");
		}

		if (config.TeleportCooldownSeconds > 0 && LastTeleportUtcByUid.TryGetValue(player.PlayerUID, out DateTime lastTeleportUtc))
		{
			TimeSpan cooldown = TimeSpan.FromSeconds(config.TeleportCooldownSeconds);
			TimeSpan elapsed = DateTime.UtcNow - lastTeleportUtc;
			if (elapsed < cooldown)
			{
				return TextCommandResult.Error("Wait " + Math.Ceiling((cooldown - elapsed).TotalSeconds).ToString(GlobalConstants.DefaultCultureInfo) + "s before teleporting again.");
			}
		}

		int warmupSeconds = Math.Max(0, config.WarmupSeconds);
		if (warmupSeconds == 0)
		{
			CompleteTeleport(server, player, target.Copy(), command, destinationLabel);
			return TextCommandResult.Success();
		}

		PendingByUid[player.PlayerUID] = new PendingTeleportWarmup
		{
			PlayerUid = player.PlayerUID,
			PlayerName = player.PlayerName,
			Command = command,
			DestinationLabel = destinationLabel,
			Target = target.Copy(),
			Start = player.Entity.Pos.Copy(),
			StartHealth = GetHealth(player),
			CompleteUtc = DateTime.UtcNow.AddSeconds(warmupSeconds)
		};

		Send(player, StratumCommandText.Warning("Teleporting in " + warmupSeconds + "s") + " to " + StratumCommandText.Escape(destinationLabel) + ". Do not move or take damage.", EnumChatType.Notification);
		return TextCommandResult.Success(StratumCommandText.Warning("Teleport pending") + " for " + warmupSeconds + "s. Stay still.");
	}

	public static void Cancel(IServerPlayer player, string reason)
	{
		if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID) || !PendingByUid.Remove(player.PlayerUID))
		{
			return;
		}

		Send(player, StratumCommandText.Danger("Teleport cancelled") + ": " + StratumCommandText.Escape(reason), EnumChatType.CommandError);
	}

	private static void OnTick(ServerMain server)
	{
		if (PendingByUid.Count == 0)
		{
			return;
		}

		StratumTeleportRequestsConfig config = StratumRuntime.Config.Commands.TeleportRequests;
		DateTime nowUtc = DateTime.UtcNow;
		double moveDistance = Math.Max(0, config.CancelMoveDistanceBlocks);
		double moveDistanceSq = moveDistance * moveDistance;

		foreach (PendingTeleportWarmup pending in PendingByUid.Values.ToArray())
		{
			ConnectedClient client = server.Clients.Values.FirstOrDefault(entry => entry.State.IsAdmitted() && entry.Player?.PlayerUID == pending.PlayerUid);
			IServerPlayer player = client?.Player;
			if (player?.Entity?.Pos == null)
			{
				PendingByUid.Remove(pending.PlayerUid);
				continue;
			}

			if (!player.Entity.Alive)
			{
				Cancel(player, "you died");
				continue;
			}

			if (config.CancelOnMove && (player.Entity.Pos.Dimension != pending.Start.Dimension || DistanceSquared(player.Entity.Pos, pending.Start) > moveDistanceSq))
			{
				Cancel(player, "you moved");
				continue;
			}

			float currentHealth = GetHealth(player);
			if (config.CancelOnDamage && currentHealth < pending.StartHealth - 0.001f)
			{
				Cancel(player, "you took damage");
				continue;
			}

			if (nowUtc < pending.CompleteUtc)
			{
				continue;
			}

			PendingByUid.Remove(pending.PlayerUid);
			CompleteTeleport(server, player, pending.Target.Copy(), pending.Command, pending.DestinationLabel);
		}
	}

	private static bool RequiresWarmup(IServerPlayer player, StratumTeleportRequestsConfig config)
	{
		if (player == null || player.WorldData.CurrentGameMode == EnumGameMode.Creative || player.WorldData.CurrentGameMode == EnumGameMode.Spectator)
		{
			return false;
		}

		return !(config.BypassWarmupForStaff && StratumCommandAccessCatalog.PlayerHasAccess(player, StratumRuntime.Config.Commands.StaffChat));
	}

	private static void CompleteTeleport(ServerMain server, IServerPlayer player, EntityPos target, string command, string destinationLabel)
	{
		StratumStaffCommandState.RecordBackLocation(player);
		player.Entity.TeleportTo(target);
		LastTeleportUtcByUid[player.PlayerUID] = DateTime.UtcNow;
		Send(player, StratumCommandText.Success("Teleported") + " to " + StratumCommandText.Escape(destinationLabel) + ".", EnumChatType.CommandSuccess);
		StratumRuntime.LogAudit("teleport " + StratumCommandText.AuditField("command", command) + " " + StratumCommandText.AuditField("player", player.PlayerName) + " " + StratumCommandText.AuditField("destination", destinationLabel), false);
	}

	private static float GetHealth(IServerPlayer player)
	{
		ITreeAttribute healthTree = player?.Entity?.WatchedAttributes?.GetTreeAttribute("health");
		return healthTree?.GetFloat("currenthealth") ?? float.MaxValue;
	}

	private static double DistanceSquared(EntityPos a, EntityPos b)
	{
		double x = a.X - b.X;
		double y = a.Y - b.Y;
		double z = a.Z - b.Z;
		return x * x + y * y + z * z;
	}

	private static void Send(IServerPlayer player, string message, EnumChatType chatType)
	{
		player.SendMessage(GlobalConstants.GeneralChatGroup, message, chatType);
	}

	private sealed class PendingTeleportWarmup
	{
		public string PlayerUid { get; set; }

		public string PlayerName { get; set; }

		public string Command { get; set; }

		public string DestinationLabel { get; set; }

		public EntityPos Target { get; set; }

		public EntityPos Start { get; set; }

		public float StartHealth { get; set; }

		public DateTime CompleteUtc { get; set; }
	}
}