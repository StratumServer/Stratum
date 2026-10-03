using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace StratumProbe;

public class RandomTickProbeModSystem : ModSystem
{
	public override void Start(ICoreAPI api)
	{
		base.Start(api);
		api.RegisterBlockClass("RandomTickProbeBlock", typeof(RandomTickProbeBlock));
		api.RegisterBlockClass("RandomTickPositionProbeBlock", typeof(RandomTickPositionProbeBlock));
	}
}

/// <summary>
/// Opts into every server random tick and converts itself to granite when one lands.
/// The world itself is the counter: scenarios place a platform of these and count how
/// many turned to granite, no cross-assembly state needed (the ModLoader compiles
/// this source mod into its own assembly).
/// </summary>
public class RandomTickProbeBlock : Block
{
	public override bool ShouldReceiveServerGameTicks(IWorldAccessor world, BlockPos pos, Random offThreadRandom, out object extra)
	{
		extra = null;
		return true;
	}

	public override void OnServerGameTick(IWorldAccessor world, BlockPos pos, object extra = null)
	{
		Block granite = world.GetBlock(new AssetLocation("game:rock-granite"));
		world.BlockAccessor.SetBlock(granite.BlockId, pos);
	}
}

/// <summary>
/// Keeps the position object every random tick hands to it, the way a mod may when it
/// schedules a delayed callback or stores the position in a list: the engine gives each
/// handler its own copy (vanilla allocates one per queued tick), so nothing else should
/// ever touch it again. The world is the counter, as above. A tick turns its block into
/// andesite, which counts how many handlers ran. On later ticks the block audits the
/// positions it kept: one whose coordinates changed since the handler returned means the
/// engine recycled the object, and the block at the position it was originally handed out
/// for turns to granite.
/// </summary>
public class RandomTickPositionProbeBlock : Block
{
	// Bounded so the audit stays cheap when nothing ever moves.
	private const int MaxKept = 256;

	private readonly List<(BlockPos Pos, int X, int Y, int Z)> kept = new List<(BlockPos, int, int, int)>();

	public override bool ShouldReceiveServerGameTicks(IWorldAccessor world, BlockPos pos, Random offThreadRandom, out object extra)
	{
		extra = null;
		return true;
	}

	public override void OnServerGameTick(IWorldAccessor world, BlockPos pos, object extra = null)
	{
		Block granite = world.GetBlock(new AssetLocation("game:rock-granite"));
		for (int i = kept.Count - 1; i >= 0; i--)
		{
			var entry = kept[i];
			if (entry.Pos.X != entry.X || entry.Pos.Y != entry.Y || entry.Pos.Z != entry.Z)
			{
				world.BlockAccessor.SetBlock(granite.BlockId, new BlockPos(entry.X, entry.Y, entry.Z, 0));
				kept.RemoveAt(i);
			}
		}

		if (kept.Count < MaxKept)
		{
			kept.Add((pos, pos.X, pos.Y, pos.Z));
		}

		Block andesite = world.GetBlock(new AssetLocation("game:rock-andesite"));
		world.BlockAccessor.SetBlock(andesite.BlockId, pos);
	}
}
