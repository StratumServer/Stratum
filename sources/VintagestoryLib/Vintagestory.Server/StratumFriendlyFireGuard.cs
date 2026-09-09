using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

// Shared decision for the friendly fire seams that run before a hit lands: the melee
// interaction handler (HandleEntityInteraction) and, through the same rule inlined against
// API-only types, the projectile impact path in VSEssentials.
//
// Keeping the rule here means the vanilla patch stays a one-line call, and the "does this hit
// count" logic has one home next to StratumPlayerGroups.
internal static class StratumFriendlyFireGuard
{
	// True when a melee attack from attacker on target must be dropped. Fires the blocked
	// attack notice as a side effect so the caller only has to return.
	public static bool BlocksMeleeAttack(IServerPlayer attacker, Entity target)
	{
		if (!StratumFriendlyFireHook.BlockGroupDamage) return false;
		if (attacker == null || target is not EntityPlayer targetPlayer) return false;
		if (targetPlayer.Player is not IServerPlayer targetServerPlayer) return false;
		if (attacker.PlayerUID == targetServerPlayer.PlayerUID) return false;
		if (!StratumPlayerGroups.SharesGroup(attacker, targetServerPlayer)) return false;

		StratumFriendlyFireHook.OnBlockedAttack?.Invoke(attacker.PlayerUID, targetServerPlayer.PlayerUID);
		return true;
	}
}
