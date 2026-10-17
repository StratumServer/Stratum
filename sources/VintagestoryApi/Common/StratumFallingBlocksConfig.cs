using System;

namespace Vintagestory.API.Common;

// Lives in the API assembly so that both Lib (StratumPerformanceConfig owns the instance)
// and GameContent (ModSystemFallingBlocks reads it) can see it.
public class StratumFallingBlocksConfig
{
    // Set by StratumPerformanceConfig.EnsurePopulated after the config is loaded.
    // Null until then, readers must fall back to defaults.
    public static StratumFallingBlocksConfig Active { get; set; }

    // Master switch. When false the system never starts and falling blocks behave like vanilla.
    public bool Enabled { get; set; } = true;

    // Maximum number of live EntityBlockFalling. Extra requests wait in a queue.
    public int MaxFallingLimit { get; set; } = 1000;

    // Instant fall simulations (blocks with no player nearby) allowed per system tick.
    public int MaxInstantPerTick { get; set; } = 100;

    // Watchdog in ms for falling entities that never settle (vanilla bug). An entity alive longer
    // than this is placed where it currently hangs. 0 disables the watchdog.
    public int StuckTimeoutMs { get; set; } = 15000;

    public void EnsureSane()
    {
        MaxFallingLimit = Math.Clamp(MaxFallingLimit, 10, 10000);
        MaxInstantPerTick = Math.Clamp(MaxInstantPerTick, 1, 10000); // 0 would process everything in one tick
        StuckTimeoutMs = Math.Clamp(StuckTimeoutMs, 0, 120000);
    }
}
