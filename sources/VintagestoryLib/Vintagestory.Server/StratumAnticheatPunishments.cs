using System;
using System.Globalization;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

/// <summary>
/// The punishment half of #221. RecordViolation in StratumAnticheatReporter already funnels every
/// confirmed violation type through one place; this is the single call slotted in right after it,
/// per player, once the recording lock has been released. Every action here delegates to a
/// primitive that already exists and is already tested elsewhere: Freeze, jail via
/// StratumCustodyStore, InventoryBase.DropAll/Clear, PlayerDataManager.BanPlayer. Nothing new is
/// invented here, only chained together against a threshold ladder.
///
/// Runs synchronously in whatever thread called RecordViolation, on purpose. The reporter already
/// does exactly that for staff alerts and for KickConfirmedCheats: every Record*Violation call site
/// calls server.DisconnectPlayer directly, immediately, with no queue, right after the lock
/// releases. That calling context is already proven safe for real game-state actions, not only
/// logging, so a deferred tick-queue here would add real complexity for a correctness problem this
/// codebase does not already have.
/// </summary>
internal static class StratumAnticheatPunishments
{
	// Highest tier first: a single burst that crosses several thresholds at once (a confirmed
	// nuker burst, for instance) applies only the tier it actually deserves, not every rung on the
	// way up. HighestPunishmentApplied on the history record is this ordinal, so a later, smaller
	// violation cannot re-trigger a lower tier the player already passed.
	private enum Tier
	{
		None = 0,
		DropInventory = 1,
		Freeze = 2,
		Jail = 3,
		Ban = 4
	}

	public static void Evaluate(ServerMain server, ServerPlayer player)
	{
		StratumAnticheatPunishmentConfig config = StratumRuntime.Config.Anticheat.Punishments;
		if (!config.Enabled || server == null || player == null)
		{
			return;
		}

		ServerPlayerData target = player.client?.ServerData;
		if (target == null)
		{
			return;
		}

		StratumAnticheatHistoryRecord history = StratumAnticheatHistory.RecordFlag(server, target);
		Tier applied = (Tier)history.HighestPunishmentApplied;
		Tier due = HighestDueTier(config, history.TotalFlags, applied);
		if (due == Tier.None)
		{
			return;
		}

		Apply(server, player, target, config, due, history.TotalFlags);
		StratumAnticheatHistory.MarkPunishmentApplied(server, target, history, (int)due);
	}

	private static Tier HighestDueTier(StratumAnticheatPunishmentConfig config, int totalFlags, Tier applied)
	{
		if (totalFlags >= config.BanAfterFlags && applied < Tier.Ban)
		{
			return Tier.Ban;
		}

		if (totalFlags >= config.JailAfterFlags && applied < Tier.Jail)
		{
			return Tier.Jail;
		}

		if (totalFlags >= config.FreezeAfterFlags && applied < Tier.Freeze)
		{
			return Tier.Freeze;
		}

		if (totalFlags >= config.DropInventoryAfterFlags && applied < Tier.DropInventory)
		{
			return Tier.DropInventory;
		}

		return Tier.None;
	}

	private static void Apply(ServerMain server, ServerPlayer player, ServerPlayerData target, StratumAnticheatPunishmentConfig config, Tier tier, int totalFlags)
	{
		string label;
		switch (tier)
		{
			case Tier.DropInventory:
				ApplyInventoryPunishment(player, config);
				label = config.WipeInsteadOfDrop ? "wiped their inventory" : "dropped their inventory";
				break;
			case Tier.Freeze:
				StratumStaffCommandState.Freeze(player);
				label = "frozen them";
				break;
			case Tier.Jail:
				ApplyJail(player, target, config);
				label = "jailed them";
				break;
			case Tier.Ban:
				ApplyBan(server, player, config);
				label = "banned them";
				break;
			default:
				return;
		}

		StratumRuntime.LogAudit("anticheat-punishment target=" + player.PlayerName + " tier=" + tier + " totalFlags=" + totalFlags.ToString(CultureInfo.InvariantCulture), true);
		AnnouncePunishment(server, player, label, totalFlags);
	}

	private static void ApplyInventoryPunishment(ServerPlayer player, StratumAnticheatPunishmentConfig config)
	{
		Vec3d pos = player.Entity?.Pos?.XYZ;
		foreach (InventoryBase inventory in player.InventoryManager.InventoriesOrdered)
		{
			// Matches the skip EntityPlayer.WalkInventory already uses: a creative inventory is
			// infinite, dropping or clearing it punishes nothing and can misbehave.
			if (inventory.ClassName == "creative")
			{
				continue;
			}

			if (config.WipeInsteadOfDrop || pos == null)
			{
				inventory.Clear();
			}
			else
			{
				inventory.DropAll(pos);
			}
		}
	}

	private static void ApplyJail(ServerPlayer player, ServerPlayerData target, StratumAnticheatPunishmentConfig config)
	{
		Caller system = new Caller { Type = EnumCallerType.Console };
		CmdStratumStaffCommands.Instance?.JailAutomatically(target, player.client, system, config.JailReason);
	}

	private static void ApplyBan(ServerMain server, ServerPlayer player, StratumAnticheatPunishmentConfig config)
	{
		DateTime? untilDate = config.BanDurationHours <= 0 ? (DateTime?)null : DateTime.UtcNow.AddHours(config.BanDurationHours);
		server.PlayerDataManager.BanPlayer(player.PlayerName, player.PlayerUID, "Stratum Anticheat", config.BanReason, untilDate);
		server.DisconnectPlayer(player.client, config.BanReason, config.BanReason);
	}

	private static void AnnouncePunishment(ServerMain server, ServerPlayer player, string label, int totalFlags)
	{
		string message = StratumCommandText.Pill("Stratum AC", StratumCommandText.Bad)
			+ " automatically "
			+ label
			+ " "
			+ StratumCommandText.Warning(player.PlayerName)
			+ " after "
			+ totalFlags.ToString(CultureInfo.InvariantCulture)
			+ " accumulated flags.";

		foreach (ConnectedClient client in server.Clients.Values)
		{
			if (StratumAnticheatReporter.ShouldReceiveStaffAlert(client))
			{
				client.Player.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);
			}
		}
	}
}
