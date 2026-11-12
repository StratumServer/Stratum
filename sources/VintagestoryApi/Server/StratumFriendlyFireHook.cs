using System;

namespace Vintagestory.API.Server;

// Bridge for the group friendly fire toggle (issue #277). EntityPlayer.ShouldReceiveDamage
// reads BlockGroupDamage and, when set, drops any damage between two members of the same
// player group before Entity.ReceiveDamage does anything: no health change, no knockback, no
// hurt animation, no DidAttack, no death. StratumFriendlyFireSystem (in VintagestoryLib) owns
// the flag and sets it from config and from /friendlyfire. When the feature is off, the whole
// check costs one static bool read per damage event.
//
// The melee interaction handler and the projectile impact path also consult this before an
// attack lands, so a blocked swing or shot stops with no slap sound, no weapon durability
// loss, and no combat-log tag, exactly like the vanilla PvP-disabled path. Those seams call
// OnBlockedAttack, which StratumFriendlyFireSystem points at a throttled "you can't hurt your
// group" notice plus a counter shown in /friendlyfire status.
public static class StratumFriendlyFireHook
{
	public static bool BlockGroupDamage;

	// (attackerUid, victimUid). Set by StratumFriendlyFireSystem. Invoked from the melee and
	// projectile seams when a hit is dropped because the two players share a group.
	public static Action<string, string> OnBlockedAttack;
}
