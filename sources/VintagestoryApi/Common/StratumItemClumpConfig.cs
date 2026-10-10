using System;
using System.Collections.Generic;

namespace Vintagestory.API.Common;

// Lives in the API assembly so that both Lib (StratumPerformanceConfig owns the instance)
// and GameContent (ModSystemItemClump reads it) can see it.
public class StratumItemClumpConfig
{
    // Set by StratumPerformanceConfig.EnsurePopulated after the config is loaded.
    // Null until then, readers must fall back to defaults.
    public static StratumItemClumpConfig Active { get; set; }

    // Merges resting item stacks of the same type.
    public bool Enabled { get; set; } = true;

    // Search radius for neighbouring items in blocks, horizontal and vertical.
    public float Radius { get; set; } = 5.0f;
    public float VerticalRadius { get; set; } = 2.0f;

    // Delay in ms after an item spawns or loads, and between retries while it is still moving.
    public int DelayMs { get; set; } = 1500;

    // How many times to retry while an item has not settled on the ground.
    public int MaxSettleAttempts { get; set; } = 20;

    // Budget of merge attempts per system tick (250 ms).
    public int MaxAttemptsPerTick { get; set; } = 25;

    // Maximum number of items in one merge group.
    public int MaxGroupSize { get; set; } = 32;

    // Full collectible codes that never merge. Supports '*' ("game:gear-*") and "@regex".
    // Case insensitive, a code without a domain is treated as vanilla.
    public List<string> Blacklist { get; set; } = new List<string>();

    // Logs merge statistics every 10 seconds.
    public bool Debug { get; set; } = false;

    public void EnsureSane()
    {
        Radius = Math.Clamp(Radius, 0.25f, 8f);
        VerticalRadius = Math.Clamp(VerticalRadius, 0.25f, 8f);
        DelayMs = Math.Clamp(DelayMs, 250, 60000);
        MaxSettleAttempts = Math.Clamp(MaxSettleAttempts, 1, 200);
        MaxAttemptsPerTick = Math.Clamp(MaxAttemptsPerTick, 1, 1000);
        MaxGroupSize = Math.Clamp(MaxGroupSize, 2, 256);
        Blacklist ??= new List<string>();
    }
}
