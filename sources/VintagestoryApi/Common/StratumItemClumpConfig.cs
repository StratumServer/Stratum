using System;
using System.Collections.Generic;

namespace Vintagestory.API.Common;

// Lives in the API assembly so that both Lib (StratumPerformanceConfig owns the instance)
// and GameContent (ModSystemItemClump reads it) can see it.
//
// Merging rules worth knowing (all of it only applies while Enabled is true):
//  - A stack a player just dropped takes no part in merging until the vanilla pickup window (1 s) has passed.
//  - The merged stack gets the average despawn clock of the items it holds, weighted by quantity. Stacks with
//    different minsecondsToDespawn (for example a death drop kept for an hour) never merge.
//  - The search is a sphere-like box (Radius horizontally, VerticalRadius vertically). Two stacks only mix when
//    they are within that range of each other and in the same land claim (or both outside every claim). There is
//    no line of sight check, so a vertical radius of 2 reaches through a one block floor or wall that is not a
//    claim border.
public class StratumItemClumpConfig
{
	// Set by StratumPerformanceConfig.EnsurePopulated after the config is loaded.
	// Null until then, readers must fall back to defaults.
	public static StratumItemClumpConfig Active { get; set; }

	// Merges resting item stacks of the same type. Read once when the server starts, changing it needs a restart.
	public bool Enabled { get; set; } = true;

	// Search radius for neighbouring items in blocks, horizontal and vertical.
	public float Radius { get; set; } = 5.0f;
	public float VerticalRadius { get; set; } = 2.0f;

	// Delay in ms after an item spawns or loads, and between retries while it is still moving.
	public int DelayMs { get; set; } = 1500;

	// How many times to retry while an item has not settled on the ground.
	public int MaxSettleAttempts { get; set; } = 20;

	// Budget of merge attempts per system tick (50 ms).
	public int MaxAttemptsPerTick { get; set; } = 25;

	// Maximum number of items in one merge group.
	public int MaxGroupSize { get; set; } = 32;

	// Full collectible codes that never merge. Supports '*' ("game:gear-*") and "@regex". Plain codes and '*'
	// patterns without a domain are treated as vanilla ("gear-*" becomes "game:gear-*") and match case
	// insensitively. A regex must match the whole lowercase code including the domain ("@game:gear-.*") and
	// is case sensitive; a regex without a domain will not match any collectible. An entry that is not a
	// valid regex is ignored and logged at start.
	public List<string> Blacklist { get; set; } = new List<string>();

	// Logs merge statistics every 10 seconds.
	public bool Debug { get; set; } = false;

	public void EnsureSane()
	{
		Radius = float.IsFinite(Radius) ? Math.Clamp(Radius, 0.25f, 8f) : 5.0f;
		VerticalRadius = float.IsFinite(VerticalRadius) ? Math.Clamp(VerticalRadius, 0.25f, 8f) : 2.0f;
		DelayMs = Math.Clamp(DelayMs, 250, 60000);
		MaxSettleAttempts = Math.Clamp(MaxSettleAttempts, 1, 200);
		MaxAttemptsPerTick = Math.Clamp(MaxAttemptsPerTick, 1, 1000);
		MaxGroupSize = Math.Clamp(MaxGroupSize, 2, 256);
		Blacklist ??= new List<string>();
	}
}
