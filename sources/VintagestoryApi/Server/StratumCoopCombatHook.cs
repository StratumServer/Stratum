namespace Vintagestory.API.Server;

// Bridge for the cooperative PvE combat toggle. EntityBehaviorHealth (in VSEssentials) reads
// CreatureInvulnerableMs instead of its hardcoded 500ms post-hit invulnerability window when
// the entity being hit isn't a player. StratumCoopCombatSystem owns the value and updates it
// from config/command. When the feature is off, this stays 500 and behavior is untouched.
public static class StratumCoopCombatHook
{
	public static int CreatureInvulnerableMs = 500;
}
