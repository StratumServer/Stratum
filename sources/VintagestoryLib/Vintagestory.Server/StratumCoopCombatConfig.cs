using System;

namespace Vintagestory.Server;

internal sealed class StratumCoopCombatConfig
{
	/// <summary>Enable cooperative PvE combat. Off by default (vanilla 500ms creature invulnerability).</summary>
	public bool Enabled { get; set; } = false;

	/// <summary>
	/// Creature post-hit invulnerability window in milliseconds while enabled. Vanilla is 500.
	/// Lower values let more players land hits on the same creature inside the window that used
	/// to block every attacker but the first. Never applied to players.
	/// </summary>
	public int CreatureInvulnerableMs { get; set; } = 100;

	public void EnsureSane()
	{
		CreatureInvulnerableMs = Math.Clamp(CreatureInvulnerableMs, 0, 500);
	}
}
