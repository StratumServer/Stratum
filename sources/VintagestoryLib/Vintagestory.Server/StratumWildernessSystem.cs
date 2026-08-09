using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

// Backing implementation for /wilderness (see CmdStratumEssentials.HandleWilderness). Picks a
// random point in a radius band around spawn, rejects it if it falls inside any land claim or
// can't find safe solid ground nearby, and otherwise routes the actual teleport through
// StratumTeleportWarmups exactly like /home and /spawn so the warmup/cancel UX is shared, not
// reimplemented.
//
// The search is asynchronous: candidate positions far from spawn are usually in unloaded chunks,
// and ServerMain.LocateRandomPosition (vanilla, used for random player-spawn placement) already
// handles queueing those chunks to load and re-testing once they're available. We ride the same
// mechanism rather than writing a second chunk-loading path.
internal static class StratumWildernessSystem
{
	private static readonly HashSet<string> pendingSearchByUid = new HashSet<string>(StringComparer.Ordinal);

	public static TextCommandResult Request(ServerMain server, IServerPlayer player)
	{
		if (player?.Entity?.Pos == null)
		{
			return TextCommandResult.Error("Only players can use /wilderness.");
		}

		if (!pendingSearchByUid.Add(player.PlayerUID))
		{
			return TextCommandResult.Error("You already have a wilderness search in progress.");
		}

		FuzzyEntityPos spawn = server.GetSpawnPosition(player.PlayerUID, onlyGlobalDefaultSpawn: true, consumeSpawn: false);
		if (spawn == null)
		{
			pendingSearchByUid.Remove(player.PlayerUID);
			return TextCommandResult.Error("Spawn is not available yet.");
		}

		StratumRuntime.Config.EnsurePopulated();
		StratumWildernessConfig config = StratumRuntime.Config.Commands.WildernessSettings;
		double minRadiusSq = (double)config.MinRadiusBlocks * config.MinRadiusBlocks;
		Vec3d center = spawn.XYZ;
		Random rand = server.rand.Value;
		string playerUid = player.PlayerUID;

		server.LocateRandomPosition(center, config.MaxRadiusBlocks, config.MaxAttempts, candidate =>
		{
			// Resolves candidate's Y to the world-gen surface height and rejects liquid /
			// non-solid-ground spots (small random walk within the local chunk if needed).
			// forPlayer: null so a claim that only grants this player access still counts as
			// "claimed" for wilderness purposes -- the issue asks to avoid claimed land, not
			// land this player merely has permission to stand on.
			//
			// Must run before the MinRadiusBlocks check below: this mutates candidate's X/Z
			// (up to a small random walk per retry, most likely to trigger right at the edge
			// of the claimed spawn sprawl MinRadiusBlocks exists to route players past), and
			// the mutated position is what actually gets used as the teleport target. Checking
			// the radius on the pre-adjustment position let candidates walk back inside the
			// minimum radius during this step and silently defeat the guarantee.
			if (!ServerSystemSupplyChunks.AdjustForSaveSpawnSpot(server, candidate, null, rand))
			{
				return false;
			}

			double dx = candidate.X - center.X;
			double dz = candidate.Z - center.Z;
			if (dx * dx + dz * dz < minRadiusSq)
			{
				return false;
			}

			LandClaim[] claims = server.api.World.Claims.Get(candidate);
			return claims == null || claims.Length == 0;
		},
		found =>
		{
			pendingSearchByUid.Remove(playerUid);
			IServerPlayer currentPlayer = FindOnlineClient(server, playerUid);
			if (currentPlayer?.Entity?.Pos == null)
			{
				return;
			}

			if (found == null)
			{
				Send(currentPlayer, StratumCommandText.Danger("Wilderness search failed") + ": no unclaimed spot found within " + config.MinRadiusBlocks + "-" + config.MaxRadiusBlocks + " blocks of spawn after " + config.MaxAttempts + " attempts. Try again.", EnumChatType.CommandError);
				return;
			}

			EntityPos target = new EntityPos(found.X + 0.5, found.Y, found.Z + 0.5);
			StratumTeleportWarmups.StartOrTeleport(server, currentPlayer, target, "wilderness", "the wilderness");
		});

		Send(player, StratumCommandText.Warning("Searching for a wilderness location") + ", stand by.", EnumChatType.Notification);
		return TextCommandResult.Success();
	}

	private static IServerPlayer FindOnlineClient(ServerMain server, string playerUid)
	{
		return server.Clients.Values.FirstOrDefault(client => client.State.IsAdmitted() && client.Player?.PlayerUID == playerUid)?.Player;
	}

	private static void Send(IServerPlayer player, string message, EnumChatType chatType)
	{
		player.SendMessage(GlobalConstants.GeneralChatGroup, message, chatType);
	}
}
