using System;

namespace Vintagestory.API.Common;

// Lives in the API assembly so that both Lib (StratumPerformanceConfig owns the instance)
// and GameContent (ModSystemFallingBlocks reads it) can see it.
//
// Behavior worth knowing before changing these values (all of it only applies while Enabled is true):
//  - With a player nearby (the entity tracking range, 128 blocks by default) falls go through the entity
//    path and behave like vanilla.
//  - With no player in range a fall is resolved instantly, without an entity. The neighbours are notified
//    like on the entity path, so a column or a heap keeps coming down, at most MaxInstantPerTick blocks per tick.
//    A block that was only asked to fall sideways and has solid support below stays where it is.
//  - An instant fall walks straight down without lateral steps before landing.
//  - A snow block falling on a partial snow layer merges into it, leaving a snow block and the remaining layer on top.
//  - Requests waiting when the server stops are resolved on shutdown (only queued blocks are flushed; higher
//    blocks in a column waiting for neighbour updates remain in place).
//  - A request whose chunk unloads while waiting is dropped: the block stays at its position and will not fall
//    again after the chunk reloads.
public class StratumFallingBlocksConfig
{
	// Set by StratumPerformanceConfig.EnsurePopulated after the config is loaded.
	// Null until then, readers must fall back to defaults.
	public static StratumFallingBlocksConfig Active { get; set; }

	// Master switch. Read once when the server starts, changing it needs a restart.
	// When false the system never starts and falling blocks behave like vanilla.
	public bool Enabled { get; set; } = true;

	// Instant fall simulations (blocks with no player nearby) allowed per system tick. Each one notifies its
	// neighbours, which can ask more blocks to fall, so a bigger heap lasts longer instead of costing more per tick.
	public int MaxInstantPerTick { get; set; } = 100;

	public void EnsureSane()
	{
		MaxInstantPerTick = Math.Clamp(MaxInstantPerTick, 1, 10000); // 0 would process everything in one tick
	}
}
