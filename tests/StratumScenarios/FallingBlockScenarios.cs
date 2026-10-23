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
/// Pins down ModSystemFallingBlocks (Performance.FallingBlocks, enabled by default).
///
/// Every sand case runs inside a 3x3 rock shaft with a closed wall, so sideways slumping
/// (sand and gravel have fallSideways) can never add noise to the column under test. A shaft
/// stands on a rock floor; the part above the floor is 1x1 and open at the bottom.
///
/// Policy under test (see the header of ModSystemFallingBlocks):
/// - a player nearby: the entity path, a whole column comes down like in vanilla, with at most
///   MaxFallingLimit entities alive at once;
/// - no player in range: the instant path. The fall is resolved without an entity and notifies the
///   neighbours like the entity path does, so a whole column still comes down, at most
///   MaxInstantPerTick blocks per tick.
///
/// ModSystemFallingBlocks lives outside the API assembly, so it is reached by reflection
/// (the public static Instance, the public RequestSpawn, and the private fields maxFallingLimit,
/// maxInstantPerTick and activeFallingPositions). A missing member fails with a descriptive message:
/// it means the scenario runs against vanilla or the member was renamed.
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
	public async Task RockRequestedAsCrackedRock_Should_LandAsCrackedRock_OnBothPaths()
	{
		ITestPlayer player = await World.JoinPlayer("fb-variant");
		await World.Ticks(2);
		object system = RequireSystem();

		// Instant path: nobody near the slab.
		Shaft far = await PrepareFar(offsetZ: 6);
		await DropRockSlab(system, far);
		AssertSlabLanded(far, "instant path");

		// Entity path: the slab is next to the player.
		Shaft near = PrepareNear(player, offsetX: 10, offsetZ: 6);
		await DropRockSlab(system, near);
		AssertSlabLanded(near, "entity path");
	}

	// ---------------------------------------------------------------------------------------
	// A request that waits in the queue while the block in its cell is swapped.
	// ---------------------------------------------------------------------------------------
	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task BlockPlacedOverAWaitingRequest_Should_StillFall()
	{
		ITestPlayer player = await World.JoinPlayer("fb-anchor-stale");
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
			// The request is still waiting, but a player arrives before the replacement is rechecked.
			// Its vanilla hook must choose EntityBlockFalling, not the instant queue.
			await player.TeleportTo(s.At(OpenTop).AddCopy(2, 0, 0));
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

	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task BlockPlacedOverAWaitingEntityRequest_Should_StillFall()
	{
		ITestPlayer player = await World.JoinPlayer("fb-near-stale");
		await World.Ticks(2);
		object system = RequireSystem();

		FieldInfo limitField = PrivateField(system, "maxFallingLimit");
		int originalLimit = (int)limitField.GetValue(system)!;

		Shaft s = PrepareNear(player, offsetX: 10, offsetZ: 18);
		BuildShaft(s, wallTop: OpenTop);
		await World.Ticks(5);

		BlockPos top = s.At(OpenTop);
		try
		{
			// No free slot: the request for the sand waits in the entity queue.
			limitField.SetValue(system, 0);
			World.SetBlock(Sand, top);
			await World.Ticks(3);

			World.SetBlock(Gravel, top);
			await World.Ticks(3);
		}
		finally
		{
			limitField.SetValue(system, originalLimit);
		}

		await World.Until(() => CodeAt(s.At(1)) == "gravel-granite", timeoutTicks: 900);
		await World.Ticks(30);

		string actual = Column(s, 1, OpenTop);
		string expected = "gravel," + string.Join(",", Enumerable.Repeat("air", OpenTop - 1));
		Assert.True(
			actual == expected,
			$"gravel placed over a waiting sand request hangs in the air.{Environment.NewLine}"
			+ $"  expected (floor+1..+{OpenTop}): {expected}{Environment.NewLine}"
			+ $"  actual:                       {actual}");
	}

	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task FallingBlockInitialize_Should_RegisterTheSameEntityOnlyOnce()
	{
		ITestPlayer player = await World.JoinPlayer("fb-init-once");
		await World.Ticks(2);
		object system = RequireSystem();

		Shaft s = PrepareNear(player, offsetX: 10, offsetZ: 22);
		BuildShaft(s, wallTop: 8);
		BlockPos top = s.At(8);
		World.SetBlock(Sand, top);

		await World.Until(() => FallingEntityAt(top) != null, timeoutTicks: 600);
		Entity falling = FallingEntityAt(top)!;
		FieldInfo trackedField = PrivateField(falling, "StratumTracked");
		Assert.True((bool)trackedField.GetValue(falling)!, "the spawned entity was not registered with the falling block system.");

		FieldInfo countField = PrivateField(system, "totalFallingBlocks");
		int before = (int)countField.GetValue(system)!;
		Reinitialize(falling);
		int after = (int)countField.GetValue(system)!;

		Assert.Equal(before, after);
		Assert.True((bool)trackedField.GetValue(falling)!, "reinitialization cleared the entity's tracked marker.");

		await World.Until(() => CodeAt(s.At(1)) == "sand-granite", timeoutTicks: 900);
		await World.Ticks(30);
	}

	// ---------------------------------------------------------------------------------------
	// MaxFallingLimit: the number of live falling entities stays at the limit, the rest waits, and
	// everything lands.
	// ---------------------------------------------------------------------------------------
	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task FallingEntities_Should_StayWithinTheLimit_AndAllLand()
	{
		ITestPlayer player = await World.JoinPlayer("fb-anchor-limit");
		await World.Ticks(2);
		object system = RequireSystem();

		FieldInfo limitField = PrivateField(system, "maxFallingLimit");
		int originalLimit = (int)limitField.GetValue(system)!;
		const int limit = 6;
		const int cols = 5, rows = 4, count = cols * rows;

		Shaft s = PrepareNear(player, offsetX: 10, offsetZ: 40);
		try
		{
			// Placing sand over air asks it to fall, so the limit is set before the pad is built.
			limitField.SetValue(system, limit);
			BuildPad(s, cols, rows);

			int peak = 0;
			await World.Until(() =>
			{
				peak = Math.Max(peak, CountFallingEntities());
				return PadLanded(s, cols, rows) == count;
			}, timeoutTicks: 900);

			Assert.True(peak > 0, "no falling entity was ever seen, so the limit was not exercised");
			Assert.True(peak <= limit, $"{peak} falling entities were alive at once, the limit is {limit}.");
		}
		finally
		{
			limitField.SetValue(system, originalLimit);
		}
	}

	// ---------------------------------------------------------------------------------------
	// MaxFallingLimit: requests that wait are served in the order they were asked. With a limit of 1 the entities
	// appear one at a time, so the cells empty in request order (the pad places its sand cell by cell, and a
	// random pick of 6 requests would match that order once in 720 runs).
	// ---------------------------------------------------------------------------------------
	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task WaitingRequests_Should_BeServed_InTheOrderTheyWereAsked()
	{
		ITestPlayer player = await World.JoinPlayer("fb-anchor-order");
		await World.Ticks(2);
		object system = RequireSystem();

		FieldInfo limitField = PrivateField(system, "maxFallingLimit");
		int originalLimit = (int)limitField.GetValue(system)!;
		const int cols = 3, rows = 2;

		Shaft s = PrepareNear(player, offsetX: 10, offsetZ: 56);
		try
		{
			limitField.SetValue(system, 1);
			BuildPad(s, cols, rows);

			var expected = new List<(int, int)>();
			for (int cx = 0; cx < cols; cx++)
				for (int cz = 0; cz < rows; cz++)
					expected.Add((cx, cz));

			var order = new List<(int, int)>();
			var seen = new HashSet<(int, int)>();
			await World.Until(() =>
			{
				foreach ((int cx, int cz) in expected)
				{
					if (!seen.Contains((cx, cz)) && CodeAt(PadPos(s, cx, cz, 4)) == "air")
					{
						seen.Add((cx, cz));
						order.Add((cx, cz));
					}
				}
				return seen.Count == expected.Count;
			}, timeoutTicks: 900);

			Assert.True(
				order.SequenceEqual(expected),
				"the waiting requests were not served in the order they were asked."
				+ $"{Environment.NewLine}  expected: {string.Join(" ", expected)}{Environment.NewLine}  actual:   {string.Join(" ", order)}");
		}
		finally
		{
			limitField.SetValue(system, originalLimit);
		}
	}

	// ---------------------------------------------------------------------------------------
	// Dedup window: a block placed again at the source cell while the first one is still falling.
	// ---------------------------------------------------------------------------------------
	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task SecondBlockPlacedAtTheSource_Should_Fall_WhileTheFirstIsStillFalling()
	{
		ITestPlayer player = await World.JoinPlayer("fb-anchor-dedup");
		await World.Ticks(2);
		RequireSystem();

		Shaft s = PrepareNear(player, offsetX: 10, offsetZ: 12);
		BuildShaft(s, wallTop: OpenTop);
		await World.Ticks(5);

		BlockPos top = s.At(OpenTop);
		World.SetBlock(Sand, top);

		// Wait until the first block is clearly below its start (vanilla's duplicate window
		// is about one block), then place a second one in the same cell.
		await World.Until(
			() => FallingEntityAt(top) is { } e && e.Pos.Y < top.Y - 3,
			timeoutTicks: 300);
		World.SetBlock(Sand, top);

		await World.Ticks(300);

		string actual = Column(s, 1, OpenTop);
		string expected = "sand,sand," + string.Join(",", Enumerable.Repeat("air", OpenTop - 2));
		Assert.True(
			actual == expected,
			$"second block placed while the first was falling did not fall (dedup slot held too long).{Environment.NewLine}"
			+ $"  expected (floor+1..+{OpenTop}): {expected}{Environment.NewLine}"
			+ $"  actual:                       {actual}");
	}

	// ---------------------------------------------------------------------------------------
	// Give-up after the retries: the block is removed and its items dropped exactly once.
	// ---------------------------------------------------------------------------------------
	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task GivingUpOnABlockedRequest_Should_RemoveTheBlock_AndDropItOnce()
	{
		ITestPlayer player = await World.JoinPlayer("fb-anchor-giveup");
		await World.Ticks(2);
		object system = RequireSystem();

		object occupied = PrivateField(system, "activeFallingPositions").GetValue(system)!;
		MethodInfo add = occupied.GetType().GetMethod("Add")!;
		MethodInfo remove = occupied.GetType().GetMethod("Remove")!;

		Shaft s = PrepareNear(player, offsetX: 10, offsetZ: 24);
		BuildShaft(s, wallTop: 2);
		BlockPos pos = s.At(1);
		World.SetBlock(Sand, pos);
		await World.Ticks(3);

		// Mark the cell as occupied by a live falling entity, then ask for the same cell:
		// every retry finds it occupied until the request gives up.
		add.Invoke(occupied, new object[] { pos.Copy() });
		try
		{
			RequestSpawn(system, Sand, pos);
			await World.Ticks(420);
		}
		finally
		{
			remove.Invoke(occupied, new object[] { pos.Copy() });
		}

		Assert.True(
			CodeAt(pos) == "air",
			$"gave up but the block is still there ('{CodeAt(pos)}'): its items would be duplicated.");

		int dropped = 0;
		foreach (Entity entity in World.Api.World.LoadedEntities.Values)
		{
			if (entity is not EntityItem item || !item.Alive) continue;
			if (item.Itemstack?.Collectible?.Code?.Path.Contains("sand") != true) continue;
			if (Math.Abs(item.Pos.X - (pos.X + 0.5)) > 3 || Math.Abs(item.Pos.Z - (pos.Z + 0.5)) > 3) continue;
			dropped += item.Itemstack.StackSize;
		}
		Assert.True(dropped == 1, $"expected exactly one dropped sand block, found {dropped}.");
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
		const int cols = 40, rows = 40, count = cols * rows;

		Shaft s = await PrepareFar(offsetZ: 18);

		try
		{
			// Placing sand over air asks it to fall, so the budget is set before the pad is built.
			budgetField.SetValue(system, budget);
			BuildPad(s, cols, rows);

			// A large pad keeps the first observation inside the queue drain even when Atlas scheduling
			// is delayed. The bounded queue must not resolve most of the pad in that first window.
			await World.Until(() => PadLanded(s, cols, rows) > 0, timeoutTicks: 120);
			int first = PadLanded(s, cols, rows);
			Assert.True(
				first < count / 2,
				$"expected fewer than half the pad to land in the first wait window, {first} of {count} landed.");

			await World.Until(() => PadLanded(s, cols, rows) == count, timeoutTicks: 600);
		}
		finally
		{
			budgetField.SetValue(system, originalBudget);
		}
	}

	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task WaitingContainerFall_Should_KeepItemsAddedBeforeProcessing()
	{
		await World.JoinPlayer("fb-cont-refresh");
		await World.Ticks(2);
		object system = RequireSystem();
		FieldInfo budgetField = PrivateField(system, "maxInstantPerTick");
		int originalBudget = (int)budgetField.GetValue(system)!;

		Shaft s = await PrepareFar(offsetZ: 30, offsetX: 1000);
		BuildShaft(s, wallTop: 10);
		BlockPos source = s.At(10);
		World.SetBlock("game:chest-east", source);
		await World.Ticks(5);
		BlockEntity be = World.Api.World.BlockAccessor.GetBlockEntity(source);
		Assert.True(be is IBlockEntityContainer, "chest block entity does not expose a container inventory.");

		try
		{
			budgetField.SetValue(system, 0);
			RequestSpawn(system, "game:chest-east", source, be);
			await World.Ticks(3);

			IBlockEntityContainer container = (IBlockEntityContainer)be;
			container.Inventory[0]!.Itemstack = new ItemStack(World.Api.World.GetItem(new AssetLocation("game:flint")), 1);
			container.Inventory.MarkSlotDirty(0);
		}
		finally
		{
			budgetField.SetValue(system, originalBudget);
		}

		BlockPos landing = s.At(1);
		await World.Until(() => CodeAt(landing) == "chest-east", timeoutTicks: 600);
		BlockEntity landedBe = World.Api.World.BlockAccessor.GetBlockEntity(landing);
		Assert.True(landedBe is IBlockEntityContainer, "landed chest has no container block entity.");
		ItemStack? landedStack = ((IBlockEntityContainer)landedBe).Inventory[0]?.Itemstack;
		Assert.True(
			landedStack?.Collectible.Code.Path == "flint" && landedStack.StackSize == 1,
			"the chest kept the request-time inventory snapshot and lost the item added while queued.");
	}

	// ---------------------------------------------------------------------------------------
	// Setup helpers.
	// ---------------------------------------------------------------------------------------

	// A rock-granite slab at floor+4 above a 3 block air gap, requested as crackedrock-granite.
	private async Task DropRockSlab(object system, Shaft s)
	{
		BuildShaft(s, wallTop: 6);
		World.SetBlock(Rock, s.At(4));
		await World.Ticks(2);
		RequestSpawn(system, Cracked, s.At(4));
		await World.Until(() => CodeAt(s.At(1)) == "crackedrock-granite", timeoutTicks: 600);
		await World.Ticks(60);
	}

	private void AssertSlabLanded(Shaft s, string path)
	{
		Entity? falling = FallingEntityAt(s.At(4));
		string entity = falling == null
			? "no falling entity left"
			: $"falling entity still alive at y={falling.Pos.Y:F2} (floor top is y={s.FloorY + 1})";

		string actual = Column(s, 1, 4);
		string expected = "crackedrock-granite,air,air,air";
		Assert.True(
			actual == expected,
			$"{path}: rock-granite requested as crackedrock-granite did not land as crackedrock-granite.{Environment.NewLine}"
			+ $"  expected (floor+1..+4): {expected}{Environment.NewLine}"
			+ $"  actual:                 {actual}{Environment.NewLine}"
			+ $"  {entity}");
	}

	private async Task<Shaft> PrepareFar(int offsetZ, int offsetX = 200)
	{
		// A chunk-aligned centre 200 blocks out (beyond every active range), kept loaded
		// because no player is near it.
		int x = ((World.Spawn.X + offsetX) / 32) * 32 + 16;
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

	private Entity? FallingEntityAt(BlockPos pos)
	{
		foreach (Entity e in World.Api.World.LoadedEntities.Values)
		{
			if (e.Code?.Path != "blockfalling") continue;
			if (Math.Abs(e.Pos.X - (pos.X + 0.5)) < 1 && Math.Abs(e.Pos.Z - (pos.Z + 0.5)) < 1) return e;
		}
		return null;
	}

	private int CountFallingEntities()
	{
		int count = 0;
		foreach (Entity e in World.Api.World.LoadedEntities.Values)
		{
			if (e.Code?.Path == "blockfalling" && e.Alive) count++;
		}
		return count;
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

	private void Reinitialize(Entity entity)
	{
		PropertyInfo? propertiesProperty = typeof(Entity).GetProperty("Properties", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
		Assert.True(propertiesProperty != null, "Entity.Properties is missing: the engine API changed.");
		object? properties = propertiesProperty!.GetValue(entity);
		Assert.True(properties != null, "the falling entity has no properties to reinitialize with.");

		MethodInfo? initialize = entity.GetType().GetMethod("Initialize", BindingFlags.Public | BindingFlags.Instance);
		Assert.True(initialize != null, "EntityBlockFalling.Initialize is missing: the engine API changed.");
		initialize!.Invoke(entity, new object?[] { properties, World.Api, entity.InChunkIndex3d });
	}

	// RequestSpawn(Block block, BlockEntity be, BlockPos initialPos, AssetLocation fallSound,
	//              float impactDamageMul, bool canFallSideways, float dustIntensity,
	//              bool doRemoveBlock = true, Vec3d positionOffset = null)
	private void RequestSpawn(object system, string blockCode, BlockPos pos, BlockEntity? be = null)
	{
		Block block = World.Api.World.GetBlock(new AssetLocation(blockCode));
		Assert.True(block != null && block.BlockId != 0, $"block '{blockCode}' not found.");

		MethodInfo? method = system.GetType().GetMethod("RequestSpawn", BindingFlags.Public | BindingFlags.Instance);
		Assert.True(method != null, "ModSystemFallingBlocks.RequestSpawn is missing: the signature changed.");

		method!.Invoke(system, new object?[] { block, be, pos.Copy(), null, 1f, true, 0f, true, null });
	}
}
