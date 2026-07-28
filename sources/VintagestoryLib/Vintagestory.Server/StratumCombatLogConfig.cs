using System;

namespace Vintagestory.Server;

internal enum EnumCombatLogPenalty
{
	/// <summary>Kill the player on disconnect. Inventory drops on the spot.</summary>
	Kill,
	/// <summary>Kill the player when they rejoin. Inventory drops at their rejoin position.</summary>
	KillOnRejoin,
	/// <summary>Log only. No gameplay penalty. For monitoring.</summary>
	LogOnly
}

internal sealed class StratumCombatLogConfig
{
	/// <summary>Enable PvP combat log prevention. Off by default (opt-in).</summary>
	public bool Enabled { get; set; } = false;

	/// <summary>How long the combat tag lasts after a PvP hit, in seconds.</summary>
	public int TagDurationSeconds { get; set; } = 15;

	/// <summary>What happens when a tagged player disconnects.</summary>
	public EnumCombatLogPenalty Penalty { get; set; } = EnumCombatLogPenalty.Kill;

	/// <summary>Tag the attacker as well as the victim. Prevents hit-and-run logouts.</summary>
	public bool TagAttacker { get; set; } = true;

	/// <summary>Send the tagged player a warning message when they enter combat.</summary>
	public bool NotifyOnTag { get; set; } = true;

	/// <summary>Send the player a message when their combat tag expires.</summary>
	public bool NotifyOnExpiry { get; set; } = true;

	/// <summary>Broadcast to the server when someone combat logs.</summary>
	public bool BroadcastOnCombatLog { get; set; } = true;

	/// <summary>Alert online staff when someone combat logs.</summary>
	public bool AlertStaff { get; set; } = true;

	/// <summary>Message sent to a player when they are combat tagged. {0} = seconds.</summary>
	public string TagMessage { get; set; } = "Combat tagged for {0}s. Disconnecting now will kill you.";

	/// <summary>Message sent when the combat tag expires.</summary>
	public string ExpiryMessage { get; set; } = "Combat tag expired. You may disconnect safely.";

	/// <summary>Broadcast message when a player combat logs. {0} = player name.</summary>
	public string CombatLogBroadcast { get; set; } = "{0} combat logged and was killed.";

	/// <summary>Privilege that exempts a player from combat tagging.</summary>
	public string ExemptPrivilege { get; set; } = "stratum.combatlog.exempt";

	public void EnsureSane()
	{
		TagDurationSeconds = Math.Clamp(TagDurationSeconds, 5, 120);
		ExemptPrivilege ??= "stratum.combatlog.exempt";
		TagMessage ??= "Combat tagged for {0}s. Disconnecting now will kill you.";
		ExpiryMessage ??= "Combat tag expired. You may disconnect safely.";
		CombatLogBroadcast ??= "{0} combat logged and was killed.";
	}
}
