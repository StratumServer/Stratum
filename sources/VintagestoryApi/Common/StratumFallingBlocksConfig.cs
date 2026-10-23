using System;

namespace Vintagestory.API.Common;

// Lives in the API assembly so that both Lib (StratumPerformanceConfig owns the instance)
// and GameContent (ModSystemFallingBlocks reads it) can see it.
//
// Behavior worth knowing before changing these values (all of it only applies while Enabled is true):
//  - With a player nearby (the entity tracking range, 128 blocks by default) falls go through the entity
//    path and behave like vanilla, except that at most MaxFallingLimit entities are alive at once.
//  - With no player in range a fall is resolved instantly, without an entity. The neighbours are notified
//    like on the entity path, so a column or a heap keeps coming down, at most MaxInstantPerTick blocks per tick.
//    A block that was only asked to fall sideways and has solid support below stays where it is.
//  - The watchdog (StuckTimeoutMs, or no player nearby) settles an entity in place; a block that already
//    stepped sideways is put back on its start cell when nobody is near.
//  - Requests still waiting when the server stops are resolved on shutdown. Requests whose column is
//    unloaded earlier stay at their start cell. Entities in flight when a column unloads are left to vanilla:
//    they are saved with the chunk and land after the reload.
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

	// Instant fall simulations (blocks with no player nearby) allowed per system tick. Each one notifies its
	// neighbours, which can ask more blocks to fall, so a bigger heap lasts longer instead of costing more per tick.
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
