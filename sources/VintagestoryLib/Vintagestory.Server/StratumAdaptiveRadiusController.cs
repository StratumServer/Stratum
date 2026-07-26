using System;

namespace Vintagestory.Server;

/// <summary>
/// MSPT-driven controller that adjusts the effective chunk send radius.
/// Shrinks instantly when a tick exceeds the overload threshold (reactive),
/// shrinks gradually when smoothed MSPT exceeds the decrease threshold (proactive),
/// and recovers one chunk at a time when smoothed MSPT stays below the increase threshold.
/// Only affects chunk SEND radius. Simulation distance, entity ticking, random ticks,
/// and block tick listeners are untouched (avoids Paper #6334 spawn-rate regression).
/// </summary>
internal sealed class StratumAdaptiveRadiusController
{
	private readonly ServerMain server;
	private double smoothedMspt;
	private float accumulatedSeconds;
	private int effectiveRadius;
	private bool initialized;

	public int EffectiveRadius => effectiveRadius;

	public double SmoothedMspt => smoothedMspt;

	public StratumAdaptiveRadiusController(ServerMain server)
	{
		this.server = server;
	}

	public void Tick(float dt)
	{
		StratumAdaptiveRadiusConfig config = StratumRuntime.Config.Performance.AdaptiveRadius;
		if (!config.Enabled)
		{
			effectiveRadius = server.Config.MaxChunkRadius;
			return;
		}

		if (!initialized)
		{
			effectiveRadius = server.Config.MaxChunkRadius;
			smoothedMspt = dt * 1000.0;
			initialized = true;
			ServerMain.Logger.Notification("[Stratum] Adaptive send radius: ceiling={0} floor={1} drop={2}ms decrease={3}ms increase={4}ms alpha={5} interval={6}s",
				effectiveRadius, config.FloorChunks, config.OverloadDropThresholdMs, config.DecreaseMsptThreshold, config.IncreaseMsptThreshold, config.SmoothingAlpha, config.AdjustmentIntervalSeconds);
			return;
		}

		double currentMspt = dt * 1000.0;
		double alpha = Math.Max(0.01, Math.Min(0.5, config.SmoothingAlpha));
		smoothedMspt = smoothedMspt * (1.0 - alpha) + currentMspt * alpha;

		int ceiling = server.Config.MaxChunkRadius;
		int floor = Math.Max(1, config.FloorChunks);
		int previous = effectiveRadius;

		// Instant drop on overload spike. No interval gating.
		if (currentMspt >= config.OverloadDropThresholdMs && effectiveRadius > floor)
		{
			effectiveRadius--;
			accumulatedSeconds = 0f;
		}
		// Proactive shrink on sustained pressure.
		else if (smoothedMspt >= config.DecreaseMsptThreshold && effectiveRadius > floor)
		{
			accumulatedSeconds += dt;
			if (accumulatedSeconds >= config.AdjustmentIntervalSeconds)
			{
				effectiveRadius--;
				accumulatedSeconds = 0f;
			}
		}
		// Recovery when healthy.
		else if (smoothedMspt <= config.IncreaseMsptThreshold && effectiveRadius < ceiling)
		{
			accumulatedSeconds += dt;
			if (accumulatedSeconds >= config.AdjustmentIntervalSeconds)
			{
				effectiveRadius++;
				accumulatedSeconds = 0f;
			}
		}
		else
		{
			accumulatedSeconds = 0f;
		}

		if (effectiveRadius != previous)
		{
			ServerMain.Logger.Notification("[Stratum] Adaptive send radius: {0} -> {1} (tick={2:F0}ms avg={3:F1}ms)",
				previous, effectiveRadius, currentMspt, smoothedMspt);
		}
	}
}
