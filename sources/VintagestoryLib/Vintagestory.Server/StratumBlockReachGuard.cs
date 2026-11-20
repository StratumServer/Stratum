using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Vintagestory.Server;

internal static class StratumBlockReachGuard
{
	public static bool TryAcceptPlaceOrBreak(ServerMain server, ConnectedClient client, BlockSelection selection, int mode, out string disconnectReason)
	{
		return TryAccept(server, client, selection, ActionName(mode), out disconnectReason);
	}

	public static bool TryAcceptBlockInteract(ServerMain server, ConnectedClient client, BlockSelection selection, out string disconnectReason)
	{
		return TryAccept(server, client, selection, "interact", out disconnectReason);
	}

	public static bool IsWithinBlockInteractionRange(IPlayer player, BlockPos blockPos, float slack)
	{
#pragma warning disable CS0618 // Vintage Story 1.22 exposes no replacement for the BlockPos overload.
		return player.IsInInteractionRangeOf(blockPos, slack);
#pragma warning restore CS0618
	}

	private static bool TryAccept(ServerMain server, ConnectedClient client, BlockSelection selection, string action, out string disconnectReason)
	{
		disconnectReason = null;
		ServerPlayer player = client?.Player;
		if (server == null || player?.Entity == null || selection?.Position == null)
		{
			return true;
		}

		StratumRuntime.Config.EnsurePopulated();
		StratumAnticheatConfig anticheat = StratumRuntime.Config.Anticheat;
		StratumBlockInteractionOutOfRangeAnticheatConfig config = anticheat.BlockInteractionOutOfRange;
		if (!anticheat.Enabled || !config.Enabled || player.WorldData.CurrentGameMode != EnumGameMode.Survival)
		{
			return true;
		}

		double maxRange = StratumReach.MaxReach(player, config.RangeSlack);
		double distance = StratumReach.BlockDistance(player, selection.Position);
		if (distance <= maxRange)
		{
			return true;
		}

		ServerMain.Logger.Audit(
			"{0} tried to {1} a block out of survival reach {2:0.##}/{3:0.##} at {4}",
			player.PlayerName,
			action,
			distance,
			maxRange,
			selection.Position);

		bool shouldKick = StratumAnticheatReporter.RecordBlockInteractionOutOfRange(server, player, action, selection.Position, distance, maxRange, out disconnectReason);
		if (shouldKick)
		{
			return false;
		}

		disconnectReason = null;
		return false;
	}

	private static string ActionName(int mode)
	{
		return mode switch
		{
			0 => "break",
			1 => "place",
			2 => "break decor",
			_ => "modify"
		};
	}
}
