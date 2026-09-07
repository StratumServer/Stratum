using System;

namespace Vintagestory.Server;

// Group friendly fire. When AllowGroupDamage is false, a hit between two players who share a
// player group (one made with /group create) is dropped before it reaches the health behavior:
// no damage, no knockback, no hurt animation, no death. Covers melee and projectiles, since
// both resolve through DamageSource.GetCauseEntity(). Healing between group members is never
// blocked, and a player can always still damage themselves.
//
// The toggle is evaluated when a hit lands, not on a schedule. A damage-over-time effect
// (bleeding, burning) is gated only when the hit that starts it lands; ticks from an effect
// already running when the toggle flips keep going. Vanilla has no player-inflicted
// damage-over-time, so this only matters with mods that add one.
//
// Explosions are covered when the igniter is known: ServerMain.CreateExplosion receives the
// igniting player's uid (bombs record it), and Stratum skips that player's group mates when
// building the hurt list. The igniter still takes damage from their own blast.
internal sealed class StratumFriendlyFireConfig
{
	/// <summary>Whether players in the same player group can damage each other. True is vanilla behaviour.</summary>
	public bool AllowGroupDamage { get; set; } = true;

	/// <summary>Tell an attacker, in chat, when their hit was dropped for hitting a group mate.</summary>
	public bool NotifyBlockedAttacker { get; set; } = true;

	/// <summary>Minimum gap between those notices per attacker, so a held attack does not spam chat.</summary>
	public int NotifyThrottleMs { get; set; } = 3000;

	/// <summary>The notice text. {0} is the group mate's name.</summary>
	public string BlockedMessage { get; set; } = "{0} is in your group, friendly fire is off.";

	public void EnsureSane()
	{
		NotifyThrottleMs = Math.Clamp(NotifyThrottleMs, 500, 60000);
		if (string.IsNullOrWhiteSpace(BlockedMessage))
		{
			BlockedMessage = "{0} is in your group, friendly fire is off.";
		}
	}
}
