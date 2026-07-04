using System;

namespace Vintagestory.Server;

internal class StratumAnticheatConfig
{
	public bool Enabled { get; set; } = true;

	public bool StaffAlerts { get; set; } = true;

	public int MaxStoredViolationsPerPlayer { get; set; } = 128;

	public int KeepPlayerViolationsMinutes { get; set; } = 60;

	public StratumBlockEntityOutOfRangeAnticheatConfig BlockEntityOutOfRange { get; set; } = new StratumBlockEntityOutOfRangeAnticheatConfig();

	public StratumBlockInteractionOutOfRangeAnticheatConfig BlockInteractionOutOfRange { get; set; } = new StratumBlockInteractionOutOfRangeAnticheatConfig();

	public StratumBlockBreakProgressAnticheatConfig BlockBreakProgress { get; set; } = new StratumBlockBreakProgressAnticheatConfig();

	public StratumMovementAnticheatConfig Movement { get; set; } = new StratumMovementAnticheatConfig();

	public StratumCombatAnticheatConfig Combat { get; set; } = new StratumCombatAnticheatConfig();

	public void EnsureSane()
	{
		MaxStoredViolationsPerPlayer = Math.Clamp(MaxStoredViolationsPerPlayer, 16, 2048);
		KeepPlayerViolationsMinutes = Math.Clamp(KeepPlayerViolationsMinutes, 5, 1440);
		BlockEntityOutOfRange ??= new StratumBlockEntityOutOfRangeAnticheatConfig();
		BlockInteractionOutOfRange ??= new StratumBlockInteractionOutOfRangeAnticheatConfig();
		BlockBreakProgress ??= new StratumBlockBreakProgressAnticheatConfig();
		Movement ??= new StratumMovementAnticheatConfig();
		Combat ??= new StratumCombatAnticheatConfig();
		BlockEntityOutOfRange.EnsureSane();
		BlockInteractionOutOfRange.EnsureSane();
		BlockBreakProgress.EnsureSane();
		Movement.EnsureSane();
		Combat.EnsureSane();
	}
}

internal class StratumAnticheatRuleConfig
{
	public bool Enabled { get; set; } = true;

	public int AlertAfterViolations { get; set; } = 8;

	public int AlertWindowSeconds { get; set; } = 10;

	public int RepeatAlertSeconds { get; set; } = 20;

	public virtual void EnsureSane()
	{
		AlertAfterViolations = Math.Clamp(AlertAfterViolations, 2, 200);
		AlertWindowSeconds = Math.Clamp(AlertWindowSeconds, 2, 300);
		RepeatAlertSeconds = Math.Clamp(RepeatAlertSeconds, 5, 600);
	}
}

internal class StratumBlockEntityOutOfRangeAnticheatConfig : StratumAnticheatRuleConfig
{
}

internal class StratumBlockInteractionOutOfRangeAnticheatConfig : StratumAnticheatRuleConfig
{
	public double RangeSlack { get; set; } = 0.75;

	public bool KickConfirmedCheats { get; set; } = true;

	public int KickAfterViolations { get; set; } = 3;

	public string KickMessage { get; set; } = "Disconnected by Stratum block reach protection";

	public override void EnsureSane()
	{
		base.EnsureSane();
		RangeSlack = Math.Clamp(RangeSlack, 0, 4);
		KickAfterViolations = Math.Clamp(KickAfterViolations, 2, 100);
		KickMessage ??= "Disconnected by Stratum block reach protection";
	}
}

internal class StratumBlockBreakProgressAnticheatConfig : StratumAnticheatRuleConfig
{
	public bool KickConfirmedCheats { get; set; } = true;

	public int KickAfterViolations { get; set; } = 3;

	public string KickMessage { get; set; } = "Disconnected by Stratum block break protection";

	public override void EnsureSane()
	{
		base.EnsureSane();
		KickAfterViolations = Math.Clamp(KickAfterViolations, 2, 100);
		KickMessage ??= "Disconnected by Stratum block break protection";
	}
}

// Server-side movement checks. Defaults start generous and get tightened from testing.
internal class StratumMovementAnticheatConfig : StratumAnticheatRuleConfig
{
	public StratumMovementAnticheatConfig()
	{
		// Movement violations tend to come in bursts. This alerts quickly but still gives lag some room.
		AlertAfterViolations = 6;
		AlertWindowSeconds = 6;
		RepeatAlertSeconds = 15;
	}

	public bool UseStatBasedSpeedLimits { get; set; } = true;

	public double HorizontalSpeedMultiplier { get; set; } = 3.0;

	public double AirborneHorizontalMultiplier { get; set; } = 1.75;

	public double MinimumHorizontalBlocksPerSecond { get; set; } = 8.0;

	public double PositionVersionAcceptDistance { get; set; } = 4.0;

	// Fallback cap when stat-based speed limits are disabled, and hard ceiling when they are on.
	public double MaxHorizontalBlocksPerSecond { get; set; } = 36;

	public double MaxVerticalUpBlocksPerSecond { get; set; } = 18;

	public double CumulativeSpeedMultiplier { get; set; } = 2.5;

	// Hard ceiling for repeated smaller teleports that stay under the per-packet cap.
	public double MaxCumulativeBlocksPerSecond { get; set; } = 64;

	public double SlackBlocks { get; set; } = 2.5;

	public double MountMaxHorizontalBlocksPerSecond { get; set; } = 96;

	public double MountMaxVerticalUpBlocksPerSecond { get; set; } = 36;

	public double MountMaxCumulativeBlocksPerSecond { get; set; } = 160;

	public double MountSlackBlocks { get; set; } = 8;

	public bool KickConfirmedCheats { get; set; } = true;

	public int KickAfterViolations { get; set; } = 10;

	public string KickMessage { get; set; } = "Disconnected by Stratum movement protection";

	// Handles slow hover and air-walk cases that never trip the speed caps.
	public bool DetectFlight { get; set; } = true;

	public bool DetectWaterWalk { get; set; } = true;

	public int WaterWalkConsecutiveTicks { get; set; } = 3;

	public int WaterWalkEntryGraceMs { get; set; } = 900;

	public double GroundContactTolerance { get; set; } = 0.04;

	// Extra cells below the real ground contact check. Keep this low unless testing proves otherwise.
	public int FlightGroundScanDepth { get; set; } = 0;

	public double FlightMinAirborneSeconds { get; set; } = 1.75;

	public double FlightRequiredDescentBlocks { get; set; } = 0.35;

	// Chest-only by design. Feet collide with too many normal partial blocks.
	public bool DetectNoclip { get; set; } = true;

	public int NoclipConsecutiveTicks { get; set; } = 3;

	public override void EnsureSane()
	{
		base.EnsureSane();
		HorizontalSpeedMultiplier = Math.Clamp(HorizontalSpeedMultiplier, 1, 20);
		AirborneHorizontalMultiplier = Math.Clamp(AirborneHorizontalMultiplier, 1, 5);
		MinimumHorizontalBlocksPerSecond = Math.Clamp(MinimumHorizontalBlocksPerSecond, 3, 100);
		PositionVersionAcceptDistance = Math.Clamp(PositionVersionAcceptDistance, 0.5, 32);
		MaxHorizontalBlocksPerSecond = Math.Clamp(MaxHorizontalBlocksPerSecond, 8, 1000);
		MaxVerticalUpBlocksPerSecond = Math.Clamp(MaxVerticalUpBlocksPerSecond, 4, 1000);
		CumulativeSpeedMultiplier = Math.Clamp(CumulativeSpeedMultiplier, 1, 20);
		MaxCumulativeBlocksPerSecond = Math.Clamp(MaxCumulativeBlocksPerSecond, MaxHorizontalBlocksPerSecond, 4000);
		SlackBlocks = Math.Clamp(SlackBlocks, 0, 32);
		MountMaxHorizontalBlocksPerSecond = Math.Clamp(MountMaxHorizontalBlocksPerSecond, 16, 1000);
		MountMaxVerticalUpBlocksPerSecond = Math.Clamp(MountMaxVerticalUpBlocksPerSecond, 8, 1000);
		MountMaxCumulativeBlocksPerSecond = Math.Clamp(MountMaxCumulativeBlocksPerSecond, MountMaxHorizontalBlocksPerSecond, 4000);
		MountSlackBlocks = Math.Clamp(MountSlackBlocks, 0, 64);
		KickAfterViolations = Math.Clamp(KickAfterViolations, 3, 1000);
		FlightGroundScanDepth = Math.Clamp(FlightGroundScanDepth, 0, 16);
		FlightMinAirborneSeconds = Math.Clamp(FlightMinAirborneSeconds, 1.0, 10);
		FlightRequiredDescentBlocks = Math.Clamp(FlightRequiredDescentBlocks, 0.05, 3);
		WaterWalkConsecutiveTicks = Math.Clamp(WaterWalkConsecutiveTicks, 3, 40);
		WaterWalkEntryGraceMs = Math.Clamp(WaterWalkEntryGraceMs, 0, 5000);
		GroundContactTolerance = Math.Clamp(GroundContactTolerance, 0.005, 0.2);
		NoclipConsecutiveTicks = Math.Clamp(NoclipConsecutiveTicks, 2, 40);
		KickMessage ??= "Disconnected by Stratum movement protection";
	}
}

// Server-side combat checks for kill-aura and aimbot behaviour. These sit on top of the
// attack range checks the vanilla server already runs, so they only add signal, never remove
// any legitimate hit. Everything here defaults to monitor-only (staff alerts, no kick) because
// the heuristics can theoretically be nudged by heavy lag; enforcement is an opt-in per server.
internal class StratumCombatAnticheatConfig : StratumAnticheatRuleConfig
{
	public StratumCombatAnticheatConfig()
	{
		// Aura hits arrive in fast bursts. Alert quickly but keep a little room for a burst of
		// legitimate melee against a mob pile.
		AlertAfterViolations = 6;
		AlertWindowSeconds = 6;
		RepeatAlertSeconds = 15;
	}

	// Attacking several distinct entities in a tiny window is physically impossible with a real
	// client (one target per swing, one crosshair). This is the highest-confidence signal here.
	public bool DetectMultiTarget { get; set; } = true;

	public int MultiTargetWindowMs { get; set; } = 400;

	public int MultiTargetThreshold { get; set; } = 3;

	// Flags an attack whose target sits outside the player's aim cone. Kept generous so ordinary
	// aiming plus lag never trips it; the point is to catch hits landed on entities beside or
	// behind the player, which only an aura does.
	public bool DetectAimCone { get; set; } = true;

	public double MaxAttackAngleDegrees { get; set; } = 75.0;

	// Skip the cone test when the target is basically touching the player, where the geometry of a
	// large hitbox makes the angle meaningless.
	public double MinAngleCheckDistance { get; set; } = 1.5;

	// Combat heuristics start as monitor-only. Turn this on once a server has watched the
	// /stratum ac combat data and is comfortable the false-positive rate is zero.
	public bool KickConfirmedCheats { get; set; } = false;

	public int KickAfterViolations { get; set; } = 12;

	public string KickMessage { get; set; } = "Disconnected by Stratum combat protection";

	// When on, a flagged hit is dropped instead of applied. Off by default so v1 never changes the
	// outcome of any hit; a real client can't trigger these checks anyway.
	public bool CancelFlaggedHits { get; set; } = false;

	public override void EnsureSane()
	{
		base.EnsureSane();
		MultiTargetWindowMs = Math.Clamp(MultiTargetWindowMs, 50, 5000);
		MultiTargetThreshold = Math.Clamp(MultiTargetThreshold, 2, 20);
		MaxAttackAngleDegrees = Math.Clamp(MaxAttackAngleDegrees, 30.0, 180.0);
		MinAngleCheckDistance = Math.Clamp(MinAngleCheckDistance, 0.0, 8.0);
		KickAfterViolations = Math.Clamp(KickAfterViolations, 3, 1000);
		KickMessage ??= "Disconnected by Stratum combat protection";
	}
}
