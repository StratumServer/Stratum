using Atlas.Api;
using Atlas.XUnit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Pins down the instant path of ModSystemFallingBlocks (Performance.FallingBlocks, enabled by default).
///
/// Every sand case runs inside a 3x3 rock shaft with a closed wall, so sideways slumping
/// (sand and gravel have fallSideways) can never add noise to the column under test. A shaft
/// stands on a rock floor; the part above the floor is 1x1 and open at the bottom.
///
/// Policy under test (see the header of ModSystemFallingBlocks):
/// - a player nearby: the entity path, a whole column comes down like in vanilla;
/// - no player in range: the instant path. The fall is resolved without an entity and notifies the
///   neighbours like the entity path does, so a whole column still comes down, at most
///   MaxInstantPerTick blocks per tick.
///
/// ModSystemFallingBlocks lives outside the API assembly, so it is reached by reflection
/// (the public static Instance, the public TryRequestInstantFall, and the private field
/// maxInstantPerTick). A missing member fails with a descriptive message: it means the scenario
/// runs against vanilla or the member was renamed.
/// </summary>
public class FallingBlockScenarios : AtlasScenarioBase
{
	private const string Rock = "game:rock-granite";
	private const string Cracked = "game:crackedrock-granite";
	private const string Sand = "game:sand-granite";
	private const string Gravel = "game:gravel-granite";
	private const string Air = "game:air";

	// Layout of the column cases, measured from the floor: floor+4 is the support, floor+5..+10 six sand.
	private const int ColumnSupport = 4;
	private const int ColumnTop = 10;

	// Layout of the open-shaft cases: the shaft is open from floor+1 to floor+15.
	private const int OpenTop = 15;

	private readonly record struct Shaft(int X, int Z, int FloorY)
	{
		public BlockPos At(int dy) => new BlockPos(X, FloorY + dy, Z, 0);
	}

	// ---------------------------------------------------------------------------------------
	// No player in range: the instant path notifies the neighbours, so the whole column lands.
	// ---------------------------------------------------------------------------------------
	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task SandColumn_Should_FallCompletely_When_NoPlayerInRange()
	{
		await World.JoinPlayer("fb-anchor-far");
		await World.Ticks(2);
		RequireSystem();

		Shaft s = await PrepareFar(offsetZ: 0);
		BuildColumn(s);
		await World.Ticks(5);

		RemoveSupport(s);
		await World.Until(() => CodeAt(s.At(ColumnTop)) == "air", timeoutTicks: 600);
		await World.Ticks(30);

		string expected = "sand,sand,sand,sand,sand,sand,air,air,air,air";
		string actual = Column(s, 1, ColumnTop);
		Assert.True(
			actual == expected,
			$"far column: expected the whole column to land (instant path with neighbour updates).{Environment.NewLine}"
			+ $"  expected (floor+1..+{ColumnTop}): {expected}{Environment.NewLine}"
			+ $"  actual:                         {actual}");
	}

	// The contrast: with a player nearby the same column comes down through the entity path.
	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task SandColumn_Should_FallCompletely_When_PlayerNearby()
	{
		ITestPlayer player = await World.JoinPlayer("fb-anchor-near");
		await World.Ticks(2);
		RequireSystem();

		Shaft s = PrepareNear(player, offsetX: 10, offsetZ: 0);
		BuildColumn(s);
		await World.Ticks(5);

		RemoveSupport(s);
		await World.Until(() => CodeAt(s.At(ColumnTop)) == "air", timeoutTicks: 900);
		await World.Ticks(90);

		string expected = "sand,sand,sand,sand,sand,sand,air,air,air,air";
		string actual = Column(s, 1, ColumnTop);
		Assert.True(
			actual == expected,
			$"near column: expected the whole column to land (entity path).{Environment.NewLine}"
			+ $"  expected (floor+1..+{ColumnTop}): {expected}{Environment.NewLine}"
			+ $"  actual:                         {actual}");
	}

	// ---------------------------------------------------------------------------------------
	// Source block differs from the falling variant (rock -> crackedrock).
	// A real cave-in drops the slab through air, so the slab hangs above a gap and must land on the
	// floor as crackedrock-granite.
	// ---------------------------------------------------------------------------------------
	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task RockRequestedAsCrackedRock_Should_LandAsCrackedRock_When_NoPlayerInRange()
	{
		await World.JoinPlayer("fb-variant");
		await World.Ticks(2);
		object system = RequireSystem();

		Shaft far = await PrepareFar(offsetZ: 6);
		BuildShaft(far, wallTop: 6);
		World.SetBlock(Rock, far.At(4));
		await World.Ticks(2);
		RequestInstantFall(system, Cracked, far.At(4));
		await World.Until(() => CodeAt(far.At(1)) == "crackedrock-granite", timeoutTicks: 600);
		await World.Ticks(60);

		string actual = Column(far, 1, 4);
		string expected = "crackedrock-granite,air,air,air";
		Assert.True(
			actual == expected,
			$"rock-granite requested as crackedrock-granite did not land as crackedrock-granite.{Environment.NewLine}"
			+ $"  expected (floor+1..+4): {expected}{Environment.NewLine}"
			+ $"  actual:                 {actual}");
	}

	// ---------------------------------------------------------------------------------------
	// A request that waits in the queue while the block in its cell is swapped.
	// ---------------------------------------------------------------------------------------
	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task BlockPlacedOverAWaitingRequest_Should_StillFall()
	{
		await World.JoinPlayer("fb-anchor-stale");
		await World.Ticks(2);
		object system = RequireSystem();

		FieldInfo budgetField = PrivateField(system, "maxInstantPerTick");
		int originalBudget = (int)budgetField.GetValue(system)!;

		Shaft s = await PrepareFar(offsetZ: 12);
		BuildShaft(s, wallTop: OpenTop);
		await World.Ticks(5);

		BlockPos top = s.At(OpenTop);
		try
		{
			// No budget: the request for the sand waits in the queue.
			budgetField.SetValue(system, 0);
			World.SetBlock(Sand, top);
			await World.Ticks(3);

			// The player replaces the sand with gravel over the same hole. Its own request is
			// refused by the pending guard, nothing asks it again.
			World.SetBlock(Gravel, top);
			await World.Ticks(3);
		}
		finally
		{
			budgetField.SetValue(system, originalBudget);
		}

		await World.Until(() => CodeAt(s.At(1)) == "gravel-granite", timeoutTicks: 600);
		await World.Ticks(30);

		string actual = Column(s, 1, OpenTop);
		string expected = "gravel," + string.Join(",", Enumerable.Repeat("air", OpenTop - 1));
		Assert.True(
			actual == expected,
			$"gravel placed over a waiting sand request hangs in the air.{Environment.NewLine}"
			+ $"  expected (floor+1..+{OpenTop}): {expected}{Environment.NewLine}"
			+ $"  actual:                       {actual}");
	}

	// ---------------------------------------------------------------------------------------
	// MaxInstantPerTick bounds the work per tick, and everything still lands.
	// ---------------------------------------------------------------------------------------
	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task InstantFalls_Should_StayWithinThePerTickBudget_AndAllLand()
	{
		await World.JoinPlayer("fb-anchor-budget");
		await World.Ticks(2);
		object system = RequireSystem();

		FieldInfo budgetField = PrivateField(system, "maxInstantPerTick");
		int originalBudget = (int)budgetField.GetValue(system)!;
		const int budget = 5;
		const int cols = 5, rows = 4, count = cols * rows;

		Shaft s = await PrepareFar(offsetZ: 18);

		try
		{
			// Placing sand over air asks it to fall, so the budget is set before the pad is built.
			budgetField.SetValue(system, budget);
			BuildPad(s, cols, rows);

			// The first tick that lands anything. The system tick (32 ms) can run twice between two
			// Atlas ticks (~33 ms), hence the factor 2.
			await World.Until(() => PadLanded(s, cols, rows) > 0, timeoutTicks: 120);
			int first = PadLanded(s, cols, rows);
			Assert.True(
				first <= budget * 2 && first < count,
				$"expected at most {budget * 2} blocks to land in the first tick, {first} of {count} landed.");

			await World.Until(() => PadLanded(s, cols, rows) == count, timeoutTicks: 600);
		}
		finally
		{
			budgetField.SetValue(system, originalBudget);
		}
	}

	// ---------------------------------------------------------------------------------------
	// Setup helpers.
	// ---------------------------------------------------------------------------------------

	private async Task<Shaft> PrepareFar(int offsetZ)
	{
		// A chunk-aligned centre 200 blocks out (beyond every active range), kept loaded
		// because no player is near it.
		int x = ((World.Spawn.X + 200) / 32) * 32 + 16;
		int z = ((World.Spawn.Z) / 32) * 32 + 16 + offsetZ;

		World.Api.WorldManager.LoadChunkColumnPriority(
			x / 32, z / 32, new ChunkLoadOptions { KeepLoaded = true });
		await World.Until(
			() => World.Api.World.BlockAccessor.GetChunkAtBlockPos(new BlockPos(x, 1, z, 0)) != null,
			timeoutTicks: 600);

		return Locate(x, z);
	}

	private Shaft PrepareNear(ITestPlayer player, int offsetX, int offsetZ)
	{
		BlockPos p = player.Position;
		return Locate(p.X + offsetX, p.Z + offsetZ);
	}

	private Shaft Locate(int x, int z)
	{
		IBlockAccessor ba = World.Api.World.BlockAccessor;
		int floorY = ba.GetTerrainMapheightAt(new BlockPos(x, 1, z, 0)) + 12;
		Assert.True(
			floorY + OpenTop + 3 < ba.MapSizeY,
			$"no room above the terrain at {x},{z} (floor y {floorY}, world height {ba.MapSizeY}).");
		return new Shaft(x, z, floorY);
	}

	// 3x3 rock floor, a closed wall around the centre up to wallTop, an empty centre.
	private void BuildShaft(Shaft s, int wallTop)
	{
		for (int dx = -1; dx <= 1; dx++)
		{
			for (int dz = -1; dz <= 1; dz++)
			{
				World.SetBlock(Rock, new BlockPos(s.X + dx, s.FloorY, s.Z + dz, 0));

				for (int dy = 1; dy <= wallTop; dy++)
				{
					string code = (dx == 0 && dz == 0) ? Air : Rock;
					World.SetBlock(code, new BlockPos(s.X + dx, s.FloorY + dy, s.Z + dz, 0));
				}
			}
		}
	}

	// A shaft whose centre holds: air gap, a rock support, then six sand on top of it.
	private void BuildColumn(Shaft s)
	{
		BuildShaft(s, wallTop: ColumnTop);
		World.SetBlock(Rock, s.At(ColumnSupport));
		for (int dy = ColumnSupport + 1; dy <= ColumnTop; dy++)
		{
			World.SetBlock(Sand, s.At(dy));
		}
	}

	// A rock floor of cols x rows blocks with air above it, and one sand block per cell at floor+4. The sand
	// is placed over air, so each block asks to fall.
	private void BuildPad(Shaft s, int cols, int rows)
	{
		for (int cx = -1; cx <= cols; cx++)
		{
			for (int cz = -1; cz <= rows; cz++)
			{
				bool inside = cx >= 0 && cx < cols && cz >= 0 && cz < rows;
				World.SetBlock(Rock, new BlockPos(s.X + cx, s.FloorY, s.Z + cz, 0));
				for (int dy = 1; dy <= 6; dy++)
				{
					World.SetBlock(inside ? Air : Rock, new BlockPos(s.X + cx, s.FloorY + dy, s.Z + cz, 0));
				}
			}
		}

		for (int cx = 0; cx < cols; cx++)
		{
			for (int cz = 0; cz < rows; cz++)
			{
				World.SetBlock(Sand, PadPos(s, cx, cz, 4));
			}
		}
	}

	private static BlockPos PadPos(Shaft s, int cx, int cz, int dy) => new BlockPos(s.X + cx, s.FloorY + dy, s.Z + cz, 0);

	private int PadLanded(Shaft s, int cols, int rows)
	{
		int landed = 0;
		for (int cx = 0; cx < cols; cx++)
		{
			for (int cz = 0; cz < rows; cz++)
			{
				if (CodeAt(PadPos(s, cx, cz, 1)).StartsWith("sand")) landed++;
			}
		}
		return landed;
	}

	// Plain SetBlock does not notify neighbours on the server, so notify the cell like a break would.
	private void RemoveSupport(Shaft s)
	{
		BlockPos support = s.At(ColumnSupport);
		World.SetBlock(Air, support);
		World.Api.World.BlockAccessor.TriggerNeighbourBlockUpdate(support);
	}

	private string CodeAt(BlockPos pos)
	{
		return World.Api.World.BlockAccessor.GetBlock(pos).Code.Path;
	}

	private string Column(Shaft s, int fromDy, int toDy)
	{
		var parts = new List<string>();
		for (int dy = fromDy; dy <= toDy; dy++)
		{
			string path = CodeAt(s.At(dy));
			parts.Add(path.StartsWith("sand") ? "sand" : path.StartsWith("gravel") ? "gravel" : path);
		}
		return string.Join(",", parts);
	}

	// ---------------------------------------------------------------------------------------
	// Reflection into ModSystemFallingBlocks.
	// ---------------------------------------------------------------------------------------

	private static Type SystemType()
	{
		foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			Type? type = assembly.GetType("Vintagestory.GameContent.ModSystemFallingBlocks");
			if (type != null) return type;
		}
		throw new InvalidOperationException(
			"ModSystemFallingBlocks not found: the patched server is not loaded.");
	}

	private static object RequireSystem()
	{
		object? instance = SystemType()
			.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?
			.GetValue(null);
		Assert.True(
			instance != null,
			"ModSystemFallingBlocks.Instance is null: Performance.FallingBlocks is disabled or the server is not the patched one.");
		return instance!;
	}

	private static FieldInfo PrivateField(object system, string name)
	{
		FieldInfo? field = system.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
		Assert.True(field != null, $"ModSystemFallingBlocks.{name} is missing: it was renamed or removed.");
		return field!;
	}

	// TryRequestInstantFall(Block block, BlockEntity be, BlockPos initialPos, bool doRemoveBlock = true)
	private void RequestInstantFall(object system, string blockCode, BlockPos pos)
	{
		Block block = World.Api.World.GetBlock(new AssetLocation(blockCode));
		Assert.True(block != null && block.BlockId != 0, $"block '{blockCode}' not found.");

		MethodInfo? method = system.GetType().GetMethod("TryRequestInstantFall", BindingFlags.Public | BindingFlags.Instance);
		Assert.True(method != null, "ModSystemFallingBlocks.TryRequestInstantFall is missing: the signature changed.");

		bool taken = (bool)method!.Invoke(system, new object?[] { block, null, pos.Copy(), true })!;
		Assert.True(taken, $"the request at {pos} was not taken: a player is in range of the position.");
	}
}
