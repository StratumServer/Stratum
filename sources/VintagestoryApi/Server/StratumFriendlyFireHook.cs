namespace Vintagestory.API.Server;

// Bridge for the group friendly fire toggle (issue #277). EntityPlayer.ShouldReceiveDamage
// reads this flag and, when set, drops any damage between two members of the same player
// group before Entity.ReceiveDamage does anything: no health change, no knockback, no hurt
// animation, no DidAttack, no death. StratumFriendlyFireSystem (in VintagestoryLib) owns the
// flag and sets it from config and from /friendlyfire. When the feature is off, the whole
// check costs one static bool read per damage event.
public static class StratumFriendlyFireHook
{
	public static bool BlockGroupDamage;
}
