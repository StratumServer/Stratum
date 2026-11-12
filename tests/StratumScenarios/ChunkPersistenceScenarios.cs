using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// A modified chunk column must survive a full save / unload / reload cycle. This targets
/// the riskiest surface of the fork: the save pipeline (incremental autosave, DbChunk
/// batch reuse) and the chunk read path (pooled reads). A silent divergence here would be
/// the worst possible regression, so every assertion is unconditional.
///
/// Two synchronization lessons are baked in. Writes are confirmed by reading them back
/// (a column still loading swallows writes silently), and a reload is only considered
/// complete once the probe moddata is visible again: moddata can only come from the
/// database, so its presence proves the stored column was applied rather than a fresh
/// or regenerated one.
/// </summary>
public class ChunkPersistenceScenarios : AtlasScenarioBase
{
	private const string ModdataKey = "stratum:probe";

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task ModifiedColumn_Should_SurviveUnloadReload_When_SavedBeforeUnload()
	{
		BlockPos anchor = World.Spawn.AddCopy(200, 1, 0);
		await LoadColumn(anchor);

		List<BlockPos> pattern = await WritePatternConfirmed(anchor, saltForCycle: 0);
		byte[] payload = { 0xA7, 0x01, 0x22, 0x03, 0x15 };
		SetColumnModdata(anchor, payload);

		await SaveUnloadReload(anchor);

		foreach (BlockPos pos in pattern)
		{
			Assert.Equal("game:rock-granite", World.BlockAt(pos).Code.ToString());
		}

		Assert.Equal(payload, ReadColumnModdata(anchor));
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task RepeatedCycles_Should_PersistEveryMutation_When_ColumnIsRecycled()
	{
		// Exercises the incremental dirty-flush path repeatedly: each cycle mutates the
		// column, round-trips it through disk, and re-verifies the cumulative state.
		BlockPos anchor = World.Spawn.AddCopy(0, 1, 200);
		var written = new List<BlockPos>();

		for (int cycle = 0; cycle < 3; cycle++)
		{
			if (cycle == 0)
			{
				await LoadColumn(anchor);
			}

			written.AddRange(await WritePatternConfirmed(anchor, saltForCycle: cycle));
			SetColumnModdata(anchor, new byte[] { (byte)cycle, 0x51 });

			await SaveUnloadReload(anchor);

			foreach (BlockPos pos in written)
			{
				Assert.Equal("game:rock-granite", World.BlockAt(pos).Code.ToString());
			}

			byte[]? moddata = ReadColumnModdata(anchor);
			Assert.NotNull(moddata);
			Assert.Equal((byte)cycle, moddata![0]);
		}
	}

	private async Task LoadColumn(BlockPos anchor)
	{
		World.Api.WorldManager.LoadChunkColumnPriority(anchor.X / 32, anchor.Z / 32);
		await World.Until(
			() => World.Api.World.BlockAccessor.GetChunkAtBlockPos(anchor) != null,
			timeoutTicks: 600);
	}

	private async Task SaveUnloadReload(BlockPos anchor)
	{
		CommandResult save = await World.ExecuteCommand("/autosavenow");
		Assert.True(save.Ok, $"/autosavenow failed: {save.Message}");

		// Part of the chunk flush happens off-thread after the command returns, and
		// UnloadChunkColumn never persists: unloading too early discards the in-memory
		// chunk before it reaches the database. Give the flush time to settle.
		await World.Ticks(60);

		World.Api.WorldManager.UnloadChunkColumn(anchor.X / 32, anchor.Z / 32);
		await World.Until(
			() => World.Api.World.BlockAccessor.GetChunkAtBlockPos(anchor) == null,
			timeoutTicks: 600);

		World.Api.WorldManager.LoadChunkColumnPriority(anchor.X / 32, anchor.Z / 32);

		// Moddata only exists in the stored column: once it is readable again, the
		// database copy has been applied and block assertions are meaningful.
		await World.Until(
			() => World.Api.World.BlockAccessor.GetChunkAtBlockPos(anchor) != null
				&& ReadColumnModdata(anchor) != null,
			timeoutTicks: 600);
	}

	/// <summary>
	/// Writes a deterministic pattern of blocks inside the anchor's chunk column, salted
	/// per cycle so repeated cycles add distinct positions, then waits until every write
	/// reads back: a column that is still loading swallows writes silently. The pattern
	/// starts from a corner padded 8 blocks into the column rather than from the anchor
	/// itself, so its 8 by 16 footprint stays inside the column SaveUnloadReload unloads
	/// wherever the spawn happens to sit within its chunk.
	/// </summary>
	private async Task<List<BlockPos>> WritePatternConfirmed(BlockPos anchor, int saltForCycle)
	{
		BlockPos corner = new BlockPos(
			(anchor.X / 32 * 32) + 8, anchor.Y, (anchor.Z / 32 * 32) + 8, 0);
		var positions = new List<BlockPos>();
		for (int i = 0; i < 8; i++)
		{
			positions.Add(corner.AddCopy(i, 2 + saltForCycle, (i * 3) % 16));
		}

		foreach (BlockPos pos in positions)
		{
			World.SetBlock("game:rock-granite", pos);
		}

		await World.Until(
			() => positions.TrueForAll(p => World.BlockAt(p).Code.Path == "rock-granite"),
			timeoutTicks: 300);
		return positions;
	}

	private void SetColumnModdata(BlockPos anchor, byte[] payload)
	{
		IWorldChunk chunk = World.Api.World.BlockAccessor.GetChunkAtBlockPos(anchor);
		Assert.NotNull(chunk);
		chunk.SetModdata(ModdataKey, payload);
		chunk.MarkModified();
	}

	private byte[]? ReadColumnModdata(BlockPos anchor)
	{
		IWorldChunk? chunk = World.Api.World.BlockAccessor.GetChunkAtBlockPos(anchor);
		return chunk?.GetModdata(ModdataKey);
	}
}
