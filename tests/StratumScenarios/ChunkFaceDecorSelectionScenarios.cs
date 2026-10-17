using System;
using System.Reflection;
using System.Threading.Tasks;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Holds the chunk-face condition of the decor guard in GameMain.GetBlockIntersectionBoxes.
///
/// A decor is stored in the chunk of the block it sits on, but its selection box physically lies
/// in the neighbouring cell on the decor's face. For a cell on a chunk face that neighbouring
/// cell can belong to another chunk, and WorldChunk.GetDecorSelectionBoxes reads decors from it.
/// A guard that only looks at the decors of the cell's own chunk skips the call there and drops
/// the box.
///
/// Every case below puts the decor on a block in one chunk and queries the air cell across the
/// boundary, whose own chunk carries no decors at all. All six faces are covered, so each of the
/// six terms of the guard ((x &amp; 31), (y &amp; 31), (z &amp; 31) being 0 or 31) is needed to pass.
/// (World coordinates in Vintage Story are never negative, so the `&amp; 31` vs `% 32` difference
/// for negative values has no observable case here.)
///
/// The method is invoked via reflection on GameMain, because the test project references only
/// VintagestoryAPI. The expected box count is calibrated from a control cell whose decor lives in
/// its own chunk, so the scenario does not hard-code how many boxes a rug produces.
/// </summary>
public class ChunkFaceDecorSelectionScenarios : AtlasScenarioBase
{
	private const string RugCode = "game:rug-blue-diamond-center";
	private const string SolidCode = "game:rock-granite";

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task GetBlockIntersectionBoxes_Should_KeepNeighbourChunkDecor_OnEveryChunkFace()
	{
		Type? gameMainType = Type.GetType("Vintagestory.Common.GameMain, VintagestoryLib");
		Assert.True(gameMainType != null, "GameMain not found in VintagestoryLib.");

		MethodInfo? getBoxes = gameMainType!.GetMethod(
			"GetBlockIntersectionBoxes",
			BindingFlags.Public | BindingFlags.Instance,
			null,
			new[] { typeof(BlockPos), typeof(bool) },
			null);
		Assert.True(getBoxes != null,
			"GameMain.GetBlockIntersectionBoxes(BlockPos, bool) is missing: the method was renamed or its signature changed.");

		IWorldAccessor world = World.Api.World;
		Assert.True(gameMainType.IsInstanceOfType(world),
			$"Server world is {world.GetType().FullName}, not a GameMain: the reflection target is wrong.");

		Block rug = world.GetBlock(new AssetLocation(RugCode));
		Assert.True(rug != null && rug.BlockId != 0, $"Rug block '{RugCode}' not found.");

		Cuboidf[]? Boxes(BlockPos pos) => (Cuboidf[]?)getBoxes!.Invoke(world, new object[] { pos, false });

		int cx = World.Spawn.X / 32;
		int cz = World.Spawn.Z / 32;

		// Control: decor in the cell's own chunk. This path never depended on the chunk-face
		// condition; it calibrates how many boxes one rug produces and proves the rug is placed.
		var controlSolid = new BlockPos(cx * 32 + 16, 80, cz * 32 + 16, 0);
		var controlQuery = new BlockPos(controlSolid.X - 1, controlSolid.Y, controlSolid.Z, 0);
		PlaceRug(world, rug, controlSolid, controlQuery, BlockFacing.WEST);
		await World.Ticks(1);

		int expected = Boxes(controlQuery)?.Length ?? 0;
		Assert.True(expected > 0,
			$"control failed: a rug on the west face of {controlSolid} produced no selection box in {controlQuery}.");

		// Per-face cases. Solid blocks sit on the edge of chunk (cx, 2, cz) (y 64..95), and the
		// queried cell is the one across that edge. The other two coordinates are interior and
		// distinct per case, so cases never touch each other.
		for (int i = 0; i < BlockFacing.ALLFACES.Length; i++)
		{
			BlockFacing face = BlockFacing.ALLFACES[i];
			Vec3i n = face.Normali;

			int x = cx * 32 + 8 + i;
			int y = 80 + i;
			int z = cz * 32 + 8 + i;

			if (n.X != 0) x = cx * 32 + (n.X > 0 ? 31 : 0);
			if (n.Y != 0) y = n.Y > 0 ? 95 : 64;
			if (n.Z != 0) z = cz * 32 + (n.Z > 0 ? 31 : 0);

			var solid = new BlockPos(x, y, z, 0);
			var query = new BlockPos(x + n.X, y + n.Y, z + n.Z, 0);

			PlaceRug(world, rug, solid, query, face);
			await World.Ticks(1);

			int actual = Boxes(query)?.Length ?? 0;
			Assert.True(
				actual == expected,
				$"chunk-face decor case '{face.Code}' failed: rug on the {face.Code} face of {solid} " +
				$"(chunk {solid.X / 32},{solid.Y / 32},{solid.Z / 32}) -> queried {query} " +
				$"(chunk {query.X / 32},{query.Y / 32},{query.Z / 32}), got {actual} box(es), expected {expected}. " +
				"The decor guard skipped GetDecorSelectionBoxes for a chunk-face cell.");
		}
	}

	private void PlaceRug(IWorldAccessor world, Block rug, BlockPos solid, BlockPos query, BlockFacing face)
	{
		Assert.True(world.BlockAccessor.GetChunkAtBlockPos(solid) != null,
			$"chunk of {solid} is not loaded.");
		Assert.True(world.BlockAccessor.GetChunkAtBlockPos(query) != null,
			$"chunk of {query} is not loaded.");

		// Air first: changing a block breaks the decors on it, so this runs before the rug is placed.
		World.SetBlock("game:air", query);
		World.SetBlock(SolidCode, solid);

		bool placed = world.BlockAccessor.SetDecor(rug, solid, face);
		Assert.True(placed, $"SetDecor refused the rug on the {face.Code} face of {solid}.");
	}
}
