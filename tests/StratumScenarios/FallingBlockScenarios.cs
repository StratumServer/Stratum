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
/// - a player nearby: the entity path, a whole column comes down like in vanilla;
/// - no player in range: the instant path, which does NOT notify neighbours, so a column only
///   loses its bottom block. This is deliberate, it keeps collapses from loading the server
///   outside the range of players.
///
/// ModSystemFallingBlocks lives outside the API assembly, so it is reached by reflection
/// (the public static Instance, the public RequestSpawn, and the private fields
/// maxFallingLimit and activeFallingPositions). A missing member fails with a descriptive
/// message: it means the scenario runs against vanilla or the member was renamed.
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
	// Policy: no player in range, instant path, no neighbour notification.
	// ---------------------------------------------------------------------------------------
	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task SandColumn_Should_OnlyLoseItsBottomBlock_When_NoPlayerInRange()
	{
		await World.JoinPlayer("fb-anchor-far");
		await World.Ticks(2);
		RequireSystem();

		Shaft s = await PrepareFar(offsetZ: 0);
		BuildColumn(s);
		await World.Ticks(5);

		RemoveSupport(s);
		await World.Ticks(150);

		// Bottom block landed on the floor, everything above it stayed up.
		string expected = "sand,air,air,air,air,sand,sand,sand,sand,sand";
		string actual = Column(s, 1, ColumnTop);
		Assert.True(
			actual == expected,
			$"far column: expected only the bottom sand to come down (instant path, no neighbour updates).{Environment.NewLine}"
			+ $"  expected (floor+1..+{ColumnTop}): {expected}{Environment.NewLine}"
			+ $"  actual:                         {actual}");
	}

	// The contrast: with a player nearby the same column comes down completely.
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
	// Source block differs from the falling variant (rock -> crackedrock), both queues.
	// A real cave-in drops the slab through air, so the slab hangs above a gap and must land on the
	// floor as crackedrock-granite. (A block that already rests on its support does not move at all
	// on the entity path, which says nothing about the source/variant check.)
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
	// A request that waits for a free slot while the player swaps the block in its cell.
	// ---------------------------------------------------------------------------------------
	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task BlockPlacedOverAWaitingRequest_Should_StillFall()
	{
		ITestPlayer player = await World.JoinPlayer("fb-anchor-stale");
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

			// The player replaces the sand with gravel over the same hole. Its own request is
			// refused by the pending guard, nothing asks it again.
			World.SetBlock(Gravel, top);
			await World.Ticks(3);
		}
		finally
		{
			limitField.SetValue(system, originalLimit);
		}

		await World.Ticks(300);

		string actual = Column(s, 1, OpenTop);
		string expected = "gravel," + string.Join(",", Enumerable.Repeat("air", OpenTop - 1));
		Assert.True(
			actual == expected,
			$"gravel placed over a waiting sand request hangs in the air.{Environment.NewLine}"
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
	// Despawn safety net with StuckTimeoutMs = 0.
	// ---------------------------------------------------------------------------------------
	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task StuckTimeoutDisabled_Should_SettleBlock_WhenEntityDespawns()
	{
		ITestPlayer player = await World.JoinPlayer("fb-stuck");
		await World.Ticks(2);
		object system = RequireSystem();

		// Set StuckTimeoutMs to 0 (watchdog disabled)
		PropertyInfo timeoutProp = system.GetType().GetProperty("StuckTimeoutMs");
		Assert.True(timeoutProp != null, "StuckTimeoutMs property not found");
		int originalTimeout = (int)timeoutProp.GetValue(system)!;
		timeoutProp.SetValue(system, 0);

		try
		{
			Shaft s = PrepareNear(player, offsetX: 10, offsetZ: 30);
			BuildShaft(s, wallTop: OpenTop);
			await World.Ticks(5);

			BlockPos top = s.At(OpenTop);
			World.SetBlock(Sand, top);

			// Wait for the entity to spawn
			await World.Until(() => FallingEntityAt(top) != null, timeoutTicks: 300);
			Entity falling = FallingEntityAt(top)!;

			// Forcefully kill the entity (simulating chunk unload or vanilla timeout)
			falling.Die(EnumDespawnReason.Removed);

			await World.Ticks(5); // Give the safety net a chance to act

			// The safety net should have instantly settled the block down
			string actual = Column(s, 1, 2);
			Assert.True(
				actual.StartsWith("sand"),
				$"Block was lost on despawn with StuckTimeoutMs=0. Expected sand at bottom, got: {actual}");
		}
		finally
		{
			timeoutProp.SetValue(system, originalTimeout);
		}
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

	// RequestSpawn(Block block, BlockEntity be, BlockPos initialPos, AssetLocation fallSound,
	//              float impactDamageMul, bool canFallSideways, float dustIntensity,
	//              bool doRemoveBlock = true, Vec3d positionOffset = null)
	private void RequestSpawn(object system, string blockCode, BlockPos pos)
	{
		Block block = World.Api.World.GetBlock(new AssetLocation(blockCode));
		Assert.True(block != null && block.BlockId != 0, $"block '{blockCode}' not found.");

		MethodInfo? method = system.GetType().GetMethod("RequestSpawn", BindingFlags.Public | BindingFlags.Instance);
		Assert.True(method != null, "ModSystemFallingBlocks.RequestSpawn is missing: the signature changed.");

		method!.Invoke(system, new object?[] { block, null, pos.Copy(), null, 1f, true, 0f, true, null });
	}
}
