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

	public StratumEntityInteractionOutOfRangeAnticheatConfig EntityInteractionOutOfRange { get; set; } = new StratumEntityInteractionOutOfRangeAnticheatConfig();

	public StratumBlockBreakProgressAnticheatConfig BlockBreakProgress { get; set; } = new StratumBlockBreakProgressAnticheatConfig();

	public StratumMovementAnticheatConfig Movement { get; set; } = new StratumMovementAnticheatConfig();

	public StratumCombatAnticheatConfig Combat { get; set; } = new StratumCombatAnticheatConfig();

	public StratumNoFallAnticheatConfig NoFall { get; set; } = new StratumNoFallAnticheatConfig();

	public StratumMultiBreakAnticheatConfig MultiBreak { get; set; } = new StratumMultiBreakAnticheatConfig();

	public StratumMovementAuthorityAnticheatConfig MovementAuthority { get; set; } = new StratumMovementAuthorityAnticheatConfig();

	public void EnsureSane()
	{
		MaxStoredViolationsPerPlayer = Math.Clamp(MaxStoredViolationsPerPlayer, 16, 2048);
		KeepPlayerViolationsMinutes = Math.Clamp(KeepPlayerViolationsMinutes, 5, 1440);
		BlockEntityOutOfRange ??= new StratumBlockEntityOutOfRangeAnticheatConfig();
		BlockInteractionOutOfRange ??= new StratumBlockInteractionOutOfRangeAnticheatConfig();
		EntityInteractionOutOfRange ??= new StratumEntityInteractionOutOfRangeAnticheatConfig();
		BlockBreakProgress ??= new StratumBlockBreakProgressAnticheatConfig();
		Movement ??= new StratumMovementAnticheatConfig();
		Combat ??= new StratumCombatAnticheatConfig();
		NoFall ??= new StratumNoFallAnticheatConfig();
		MultiBreak ??= new StratumMultiBreakAnticheatConfig();
		MovementAuthority ??= new StratumMovementAuthorityAnticheatConfig();
		BlockEntityOutOfRange.EnsureSane();
		BlockInteractionOutOfRange.EnsureSane();
		EntityInteractionOutOfRange.EnsureSane();
		BlockBreakProgress.EnsureSane();
		Movement.EnsureSane();
		Combat.EnsureSane();
		NoFall.EnsureSane();
		MultiBreak.EnsureSane();
		MovementAuthority.EnsureSane();
	}
}

internal class StratumMovementAuthorityAnticheatConfig : StratumAnticheatRuleConfig
{
	public StratumMovementAuthorityAnticheatConfig()
	{
		AlertAfterViolations = 6;
		AlertWindowSeconds = 10;
		RepeatAlertSeconds = 15;
	}

	public bool DetectStepHeight { get; set; } = true;

	public double MaxStepHeightBlocks { get; set; } = 1.3;

	public double StepMaxHorizontalBlocks { get; set; } = 1.5;

	public bool DetectAcceleration { get; set; } = true;

	public double MaxSpeedJumpBlocksPerSecond { get; set; } = 16.0;

	public double MinFlaggedSpeed { get; set; } = 12.0;

	public bool KickConfirmedCheats { get; set; } = false;

	public int KickAfterViolations { get; set; } = 8;

	public string KickMessage { get; set; } = "Disconnected by Stratum movement protection";

	public override void EnsureSane()
	{
		base.EnsureSane();
		MaxStepHeightBlocks = Math.Clamp(MaxStepHeightBlocks, 1.05, 8.0);
		StepMaxHorizontalBlocks = Math.Clamp(StepMaxHorizontalBlocks, 0.3, 8.0);
		MaxSpeedJumpBlocksPerSecond = Math.Clamp(MaxSpeedJumpBlocksPerSecond, 4.0, 200.0);
		MinFlaggedSpeed = Math.Clamp(MinFlaggedSpeed, 4.0, 200.0);
		KickAfterViolations = Math.Clamp(KickAfterViolations, 3, 200);
		KickMessage ??= "Disconnected by Stratum movement protection";
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

	public bool KickConfirmedCheats { get; set; } = false;

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

// Reach on entity interactions (right-click: feed/trade/mount/melee). Same shape as block reach.
// A slightly larger default slack absorbs the extra jitter of a moving target under lag.
internal class StratumEntityInteractionOutOfRangeAnticheatConfig : StratumAnticheatRuleConfig
{
	public double RangeSlack { get; set; } = 1.0;

	public bool KickConfirmedCheats { get; set; } = false;

	public int KickAfterViolations { get; set; } = 3;

	public string KickMessage { get; set; } = "Disconnected by Stratum entity reach protection";

	public override void EnsureSane()
	{
		base.EnsureSane();
		RangeSlack = Math.Clamp(RangeSlack, 0, 4);
		KickAfterViolations = Math.Clamp(KickAfterViolations, 2, 100);
		KickMessage ??= "Disconnected by Stratum entity reach protection";
	}
}

internal class StratumBlockBreakProgressAnticheatConfig : StratumAnticheatRuleConfig
{
	public bool KickConfirmedCheats { get; set; } = false;

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

	public bool KickConfirmedCheats { get; set; } = false;

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

internal class StratumNoFallAnticheatConfig : StratumAnticheatRuleConfig
{
	public StratumNoFallAnticheatConfig()
	{
		AlertAfterViolations = 5;
		AlertWindowSeconds = 30;
		RepeatAlertSeconds = 30;
	}

	public double MinFallBlocks { get; set; } = 5.0;

	public double MinDescentSpeed { get; set; } = 4.0;

	public bool KickConfirmedCheats { get; set; } = false;

	public int KickAfterViolations { get; set; } = 10;

	public string KickMessage { get; set; } = "Disconnected by Stratum no-fall protection";

	public override void EnsureSane()
	{
		base.EnsureSane();
		MinFallBlocks = Math.Clamp(MinFallBlocks, 3.0, 64.0);
		MinDescentSpeed = Math.Clamp(MinDescentSpeed, 1.0, 30.0);
		KickAfterViolations = Math.Clamp(KickAfterViolations, 3, 200);
		KickMessage ??= "Disconnected by Stratum no-fall protection";
	}
}

// Nuker / veinmine / multi-break
internal class StratumMultiBreakAnticheatConfig : StratumAnticheatRuleConfig
{
	public StratumMultiBreakAnticheatConfig()
	{
		AlertAfterViolations = 2;
		AlertWindowSeconds = 10;
		RepeatAlertSeconds = 15;
	}

	public int WindowMs { get; set; } = 300;

	public int MaxBreaksInWindow { get; set; } = 6;

	// Cap on tracked positions per player so a huge radius nuke can't grow memory
	public int MaxTrackedBreaks { get; set; } = 128;

	// Off by default: monitor-only
	public bool KickConfirmedCheats { get; set; } = false;

	public int KickAfterViolations { get; set; } = 4;

	public string KickMessage { get; set; } = "Disconnected by Stratum multi-break protection";

	// when every hit lands dead-centre (0.5,0.5,0.5) can't come from a real raytrace, which always strikes a block surface. 
	// Combined with the superhuman burst rate this is near-zero false-positive, so such a burst may kick immediately even while KickConfirmedCheats stays off.
	public bool KickOnFingerprint { get; set; } = true;

	public override void EnsureSane()
	{
		base.EnsureSane();
		WindowMs = Math.Clamp(WindowMs, 50, 5000);
		MaxBreaksInWindow = Math.Clamp(MaxBreaksInWindow, 3, 200);
		MaxTrackedBreaks = Math.Clamp(MaxTrackedBreaks, MaxBreaksInWindow, 2048);
		KickAfterViolations = Math.Clamp(KickAfterViolations, 2, 200);
		KickMessage ??= "Disconnected by Stratum multi-break protection";
	}
}
