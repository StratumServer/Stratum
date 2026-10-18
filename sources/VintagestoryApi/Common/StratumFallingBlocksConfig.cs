using System;

namespace Vintagestory.API.Common;

// Lives in the API assembly so that both Lib (StratumPerformanceConfig owns the instance)
// and GameContent (ModSystemFallingBlocks reads it) can see it.
//
// Behavior worth knowing before changing these values (all of it only applies while Enabled is true):
//  - With a player nearby, falls go through the entity path and behave like vanilla (the cap below applies).
//  - With no player in range (the entity tracking range, 128 blocks by default) a fall is resolved instantly,
//    without an entity and WITHOUT notifying neighbours. This is deliberate: it stops collapses from
//    continuing, and loading the server, outside the range of players. A sand or gravel column there only
//    loses its bottom block, the rest stays up until something next to it changes. A block that was only
//    asked to fall sideways and has solid support below stays where it is.
//  - Requests still waiting when the server stops are resolved on shutdown. Requests whose column is
//    unloaded earlier stay at their start cell. Entities in flight when a column unloads are left to vanilla:
//    they are saved with the chunk and land after the reload.
//  - The watchdog (StuckTimeoutMs, or no player nearby) settles an entity in place; a block that already
//    stepped sideways is put back on its start cell when nobody is near.
//  - Small differences from the entity path for snow: snow landing on a snow block adds a layer above it
//    instead of being absorbed into it.
public class StratumFallingBlocksConfig
{
    // Set by StratumPerformanceConfig.EnsurePopulated after the config is loaded.
    // Null until then, readers must fall back to defaults.
    public static StratumFallingBlocksConfig Active { get; set; }

    // Master switch. Read once when the server starts, changing it needs a restart.
    // When false the system never starts and falling blocks behave like vanilla.
    public bool Enabled { get; set; } = true;

    // Maximum number of live EntityBlockFalling. Extra requests wait in a queue. Lower values keep the tick
    // flat at the price of a longer collapse (measured on a sand heap of about 13.6k blocks: 1000 gives about
    // 4.4 ms mean MSPT but takes more than 4 minutes, 5000 gives about 15 ms and about 190 s, vanilla about
    // 22 ms and about 134 s).
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
