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

	public void EnsureSane()
	{
		MaxStoredViolationsPerPlayer = Math.Clamp(MaxStoredViolationsPerPlayer, 16, 2048);
		KeepPlayerViolationsMinutes = Math.Clamp(KeepPlayerViolationsMinutes, 5, 1440);
		BlockEntityOutOfRange ??= new StratumBlockEntityOutOfRangeAnticheatConfig();
		BlockInteractionOutOfRange ??= new StratumBlockInteractionOutOfRangeAnticheatConfig();
		BlockBreakProgress ??= new StratumBlockBreakProgressAnticheatConfig();
		Movement ??= new StratumMovementAnticheatConfig();
		BlockEntityOutOfRange.EnsureSane();
		BlockInteractionOutOfRange.EnsureSane();
		BlockBreakProgress.EnsureSane();
		Movement.EnsureSane();
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

	public bool KickConfirmedCheats { get; set; } = true;

	public int KickAfterViolations { get; set; } = 10;

	public string KickMessage { get; set; } = "Disconnected by Stratum movement protection";

	// Handles slow hover and air-walk cases that never trip the speed caps.
	public bool DetectFlight { get; set; } = true;

	public bool DetectWaterWalk { get; set; } = true;

	public int WaterWalkConsecutiveTicks { get; set; } = 3;

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
		KickAfterViolations = Math.Clamp(KickAfterViolations, 3, 1000);
		FlightGroundScanDepth = Math.Clamp(FlightGroundScanDepth, 0, 16);
		FlightMinAirborneSeconds = Math.Clamp(FlightMinAirborneSeconds, 1.0, 10);
		FlightRequiredDescentBlocks = Math.Clamp(FlightRequiredDescentBlocks, 0.05, 3);
		WaterWalkConsecutiveTicks = Math.Clamp(WaterWalkConsecutiveTicks, 3, 40);
		GroundContactTolerance = Math.Clamp(GroundContactTolerance, 0.005, 0.2);
		NoclipConsecutiveTicks = Math.Clamp(NoclipConsecutiveTicks, 2, 40);
		KickMessage ??= "Disconnected by Stratum movement protection";
	}
}
