namespace Vintagestory.Server;

// Group friendly fire. When AllowGroupDamage is false, a hit between two players who share a
// player group (one made with /group create) is dropped before it reaches the health behavior:
// no damage, no knockback, no hurt animation, no death. Covers melee and projectiles, since
// both resolve through DamageSource.GetCauseEntity(). Healing between group members is never
// blocked, and a player can always still damage themselves.
//
// Not covered: explosions. ServerMain.CreateExplosion builds its DamageSource without a source
// or cause entity, so there is no attacker to compare against.
internal sealed class StratumFriendlyFireConfig
{
	/// <summary>Whether players in the same player group can damage each other. True is vanilla behaviour.</summary>
	public bool AllowGroupDamage { get; set; } = true;
}
