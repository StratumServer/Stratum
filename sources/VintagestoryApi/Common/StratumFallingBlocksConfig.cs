using System;

namespace Vintagestory.API.Common;

// Lives in the API assembly so that both Lib (StratumPerformanceConfig owns the instance)
// and GameContent (ModSystemFallingBlocks reads it) can see it.
//
// Behavior worth knowing before changing these values (all of it only applies while Enabled is true):
//  - With a player nearby (the entity tracking range, 128 blocks by default) falls go through the entity
//    path up to MaxFallingLimit. When the cap is reached, requests wait in a single server-wide FIFO queue,
//    meaning a block dropped elsewhere waits behind an ongoing collapse.
//  - Rock collapse chains space their neighbour checks by 300 to 500 ms.
//  - With no player in range a fall is resolved instantly, without an entity. The neighbours are notified
//    like on the entity path, so a column or a heap keeps coming down, at most MaxInstantPerTick blocks per tick.
//    A block that was only asked to fall sideways and has solid support below stays where it is.
//  - An instant fall walks straight down without lateral steps before landing.
//  - A snow block falling on a partial snow layer merges into it, leaving a snow block and the remaining layer on top.
//  - Requests waiting when the server stops are resolved on shutdown (only queued blocks are flushed; higher
//    blocks in a column waiting for neighbour updates remain in place).
//  - A request whose chunk unloads while waiting is dropped: the block stays at its position and will not fall
//    again after the chunk reloads. Entities in flight when a column unloads are left to vanilla: they are saved
//    with the chunk and land after the reload.
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

	public void EnsureSane()
	{
		MaxFallingLimit = Math.Clamp(MaxFallingLimit, 10, 10000);
		MaxInstantPerTick = Math.Clamp(MaxInstantPerTick, 1, 10000); // 0 would process everything in one tick
	}
}
