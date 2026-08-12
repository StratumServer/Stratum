using System.Linq;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

/// <summary>
/// Stratum player privacy: hides player map pins by default, with staff override and group exception.
/// Also hides a vanished player's map pin unconditionally (see <see cref="VanishHidesSenderFromReceiver"/>),
/// regardless of the opt-in config below. Wires into the engine via <see cref="PlayerMapDisclosureHook"/>
/// so VSEssentials' player-pin broadcaster (SystemRemotePlayerTracking) consults Stratum state without
/// taking a hard reference back into VintagestoryLib.
/// </summary>
internal static class StratumPlayerPrivacy
{
	private static ServerMain server;

	public static void Initialize(ServerMain server)
	{
		StratumPlayerPrivacy.server = server;

		PlayerMapDisclosureHook.AllowMapPinDisclosure = AllowMapPinDisclosure;
		PlayerMapDisclosureHook.CoordinateSnap = CoordinateSnap;

		ApplyWorldConfigOverrides();
	}

	private static StratumPlayerPrivacyConfig Cfg => StratumRuntime.Config?.PlayerPrivacy;

	private static void ApplyWorldConfigOverrides()
	{
		StratumPlayerPrivacyConfig cfg = Cfg;
		if (cfg == null || !cfg.Enabled) return;

		if (cfg.MaxBroadcastDistanceBlocks >= 0 && server?.api?.World?.Config != null)
		{
			server.api.World.Config.SetFloat("mapPlayerRenderDistance", cfg.MaxBroadcastDistanceBlocks);
			StratumRuntime.LogInfo("player privacy: mapPlayerRenderDistance forced to " + cfg.MaxBroadcastDistanceBlocks + " blocks");
		}

		if (cfg.AllowGroupMapVisibility && server?.api?.World?.Config != null)
		{
			server.api.World.Config.SetBool("mapShowGroupPlayers", true);
		}

		StratumRuntime.LogInfo("player privacy: enabled (hideMapPins=" + cfg.HideMapPins
			+ " groupVisibility=" + cfg.AllowGroupMapVisibility
			+ " coordSnap=" + cfg.CoordinateSnapBlocks
			+ " showStaffPins=" + cfg.ShowStaffPinsToAll + ")");
	}

	private static bool? AllowMapPinDisclosure(IServerPlayer sender, IServerPlayer receiver)
	{
		// Vanish hides the map pin too, independent of the Enabled/HideMapPins config below:
		// a vanished player is meant to disappear entirely, not just off the entity tracker.
		// Mirrors the /near and entity-visibility rule exactly (see StratumStaffCommandState),
		// so staff who can still see a vanished player (and haven't opted into hideothers)
		// keep seeing their pin too.
		if (VanishHidesSenderFromReceiver(sender, receiver)) return false;

		StratumPlayerPrivacyConfig cfg = Cfg;
		if (cfg == null || !cfg.Enabled) return null;

		bool receiverIsStaff = StratumCommandAccessCatalog.PlayerHasAccess(receiver, cfg.StaffOverride);
		if (receiverIsStaff) return true;

		bool senderIsStaff = StratumCommandAccessCatalog.PlayerHasAccess(sender, cfg.StaffOverride);
		if (senderIsStaff && cfg.ShowStaffPinsToAll) return true;

		if (cfg.AllowGroupMapVisibility && SharesGroup(sender, receiver)) return true;

		if (cfg.HideMapPins) return false;

		return null; // defer to engine default (distance check)
	}

	private static int? CoordinateSnap(IServerPlayer sender, IServerPlayer receiver)
	{
		StratumPlayerPrivacyConfig cfg = Cfg;
		if (cfg == null || !cfg.Enabled || cfg.CoordinateSnapBlocks <= 0) return null;

		// Staff and group members get exact coords.
		if (StratumCommandAccessCatalog.PlayerHasAccess(receiver, cfg.StaffOverride)) return 0;
		if (cfg.AllowGroupMapVisibility && SharesGroup(sender, receiver)) return 0;

		return cfg.CoordinateSnapBlocks;
	}

	private static bool VanishHidesSenderFromReceiver(IServerPlayer sender, IServerPlayer receiver)
	{
		if (sender == null || receiver == null || sender.PlayerUID == receiver.PlayerUID) return false;
		if (!StratumStaffCommandState.IsVanished(sender.PlayerUID)) return false;

		bool receiverSeesVanished = StratumCommandAccessCatalog.PlayerHasAccess(receiver, StratumRuntime.Config.Commands.Vanish)
			&& !StratumStaffCommandState.HidesOtherVanished(receiver.PlayerUID);
		return !receiverSeesVanished;
	}

	private static bool SharesGroup(IServerPlayer a, IServerPlayer b)
	{
		if (a?.Groups == null || b?.Groups == null || a.Groups.Length == 0 || b.Groups.Length == 0) return false;
		int[] aGroups = a.Groups.Select(g => g.GroupUid).ToArray();
		return b.Groups.Any(g => aGroups.Contains(g.GroupUid));
	}
}
