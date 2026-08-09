using System;

namespace Vintagestory.API.Server;

// Bridge for the combat log system. EntityProjectileBase (in VSEssentials) calls the static
// delegate after confirming a PvP projectile hit. StratumCombatLogSystem sets the delegate
// at construction. When the system is disabled or not loaded, the delegate is null and the
// call site is a single null check per projectile impact (free).
public static class StratumCombatLogHook
{
	// Args: attackerPlayerUid, victimPlayerUid. Set by StratumCombatLogSystem.
	public static Action<string, string> OnProjectilePvPHit;
}
