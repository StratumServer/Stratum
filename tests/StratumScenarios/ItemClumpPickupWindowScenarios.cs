using Atlas.Api;
using Atlas.XUnit;
using System;
using System.Reflection;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Performance.ItemClump (enabled by default).
///
/// Pickup window: a stack a player just dropped (ByPlayerUid set, under 1000 ms old, see
/// EntityItem.CanCollect) stays its own entity until the window has passed, and only then may be
/// poured into an older pile. Items without a dropper have no window and merge as usual.
///
/// Despawn clock: deathTime is ELAPSED time (EntityBehaviorDespawn adds to it and kills the item once
/// it passes minSeconds), so the merged stack takes the smaller value, the clock with more life left.
/// Stacks with different minsecondsToDespawn never merge.
///
/// Chunk save: a merge marks the chunk of the receiver (and of a surviving donor) as modified,
/// otherwise a merge across a chunk border is lost on the next save.
///
/// Timing is expressed in ticks (Atlas paces at ~33 ms), with wide margins, and merges are awaited with
/// Until. Where the clump delay itself matters (1500 ms by default), the scenario waits on the server clock
/// instead of a tick count, because a slow run stretches ticks but not the clump clock.
/// Each scenario uses its own spot (8 blocks apart, beyond the 5 block search radius), its own item (stacks of
/// different items never merge, so a pile left by another scenario cannot join in) and a player name of at
/// most 16 characters (the engine rejects longer ones).
/// </summary>
public class ItemClumpPickupWindowScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task FreshPlayerDrop_Should_StayApart_UntilPickupWindowPasses_ThenMerge()
	{
		ICoreServerAPI api = World.Api;
		ITestPlayer player = await World.JoinPlayer("clump-window");
		await World.Ticks(2);
		Vec3d pos = DropPos(player, 0);
		Item item = RequireItem(api, "game:flint");

		// Older pile from player A. Wait until its own window is long over and it has settled.
		EntityItem older = Spawn(api, new ItemStack(item, 5), pos, "clump-window-a");
		await World.Ticks(90);
		Assert.True(older.Alive && older.Slot.Itemstack.StackSize == 5,
			"setup failed: the older pile is gone or changed before the test started");

		// Fresh drop from player B right next to it.
		EntityItem fresh = Spawn(api, new ItemStack(item, 1), pos, "clump-window-b");
		await World.Ticks(8);

		Assert.True(
			older.Alive && fresh.Alive
				&& older.Slot.Itemstack.StackSize == 5 && fresh.Slot.Itemstack.StackSize == 1,
			"fresh drop was merged inside its pickup window: "
			+ $"older alive={older.Alive} size={older.Slot.Itemstack?.StackSize}, "
			+ $"fresh alive={fresh.Alive} size={fresh.Slot.Itemstack?.StackSize}");

		// After the window the merge is allowed again and must happen.
		await World.Until(() => !fresh.Alive, timeoutTicks: 300);
		Assert.True(!fresh.Alive && older.Alive && older.Slot.Itemstack.StackSize == 6,
			"fresh drop did not merge into the older pile after the pickup window: "
			+ $"older alive={older.Alive} size={older.Slot.Itemstack?.StackSize}, fresh alive={fresh.Alive}");
	}

	/// <summary>
	/// With the default delay of 1500 ms nothing is processed inside a fresh drop's 1000 ms window, so the
	/// scenario above only holds the check on the stack that is being processed. This one holds the check
	/// on the OTHER stacks: another candidate (no dropper) falls due while the player drop is still inside
	/// its window and must not pull it in.
	/// </summary>
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task FreshPlayerDrop_Should_NotBePulledIn_ByAnotherCandidateDueInsideItsWindow()
	{
		ICoreServerAPI api = World.Api;
		ITestPlayer player = await World.JoinPlayer("clump-other");
		await World.Ticks(2);
		Vec3d pos = DropPos(player, 8);
		Item item = RequireItem(api, "game:stone-granite");

		// Settled pile, then a no-dropper stack that will come due about 300 ms after the player drop.
		EntityItem older = Spawn(api, new ItemStack(item, 5), pos, null);
		await World.Ticks(90);
		EntityItem trigger = Spawn(api, new ItemStack(item, 1), pos, null);
		long triggerSpawnedMs = api.World.ElapsedMilliseconds;
		await World.Until(() => api.World.ElapsedMilliseconds - triggerSpawnedMs >= 1200, timeoutTicks: 300); // 1.2 s of the 1.5 s clump delay

		EntityItem fresh = Spawn(api, new ItemStack(item, 1), pos, "clump-other-p");

		// The trigger merges into the pile while the fresh drop is still in its window ...
		await World.Until(() => !trigger.Alive, timeoutTicks: 120);
		Assert.True(
			fresh.Alive && fresh.Slot.Itemstack.StackSize == 1,
			"a no-dropper stack that came due pulled the player's fresh drop in during its pickup window "
			+ $"(fresh alive={fresh.Alive} size={fresh.Slot.Itemstack?.StackSize}, "
			+ $"pile size={older.Slot.Itemstack?.StackSize})");

		// ... and the fresh drop joins the pile after its own window.
		await World.Until(() => !fresh.Alive, timeoutTicks: 300);
		Assert.True(older.Alive && older.Slot.Itemstack.StackSize == 7,
			$"expected one pile of 7 in the end, got {older.Slot.Itemstack?.StackSize}");
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task ItemsWithoutDropper_Should_Merge_WithoutWaitingForAWindow()
	{
		ICoreServerAPI api = World.Api;
		ITestPlayer player = await World.JoinPlayer("clump-nodropper");
		await World.Ticks(2);
		Vec3d pos = DropPos(player, 16);
		Item item = RequireItem(api, "game:stone-andesite");

		EntityItem older = Spawn(api, new ItemStack(item, 5), pos, null);
		await World.Ticks(90);
		EntityItem fresh = Spawn(api, new ItemStack(item, 1), pos, null);

		await World.Until(() => !fresh.Alive, timeoutTicks: 300);
		Assert.True(!fresh.Alive && older.Alive && older.Slot.Itemstack.StackSize == 6,
			"drops without a dropper did not merge: "
			+ $"older alive={older.Alive} size={older.Slot.Itemstack?.StackSize}, fresh alive={fresh.Alive}");
	}

	// deathTime is elapsed time. The large pile is the receiver (largest stacks receive).

	/// <summary>
	/// A pile that is far from expiry (deathTime 100 of 600) receives a stack that is close to it (500).
	/// The old behavior (keep the larger value) would give all six the clock of the stack about to
	/// expire, and the whole pile would vanish 100 s later instead of lasting about 500 s more.
	/// </summary>
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task Merge_Should_KeepTheClockWithMoreLifeLeft_WhenTheDonorIsCloserToExpiry()
	{
		await AssertMergedDeathTime("clump-dt-a", "game:stone-basalt", zOffset: 24, receiverTime: 100f, donorTime: 500f);
	}

	/// <summary>
	/// A fresh large stack (0) receives a small stack aged 540. With the old behavior all of them would
	/// be gone about 60 s later, where vanilla keeps the large stack for about 600 s.
	/// </summary>
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task Merge_Should_NotShortenAFreshStack_WhenTheDonorIsNearlyExpired()
	{
		await AssertMergedDeathTime("clump-dt-b", "game:stone-chert", zOffset: 32, receiverTime: 0f, donorTime: 540f);
	}

	private async Task AssertMergedDeathTime(string playerName, string itemCode, int zOffset, float receiverTime, float donorTime)
	{
		ICoreServerAPI api = World.Api;
		ITestPlayer player = await World.JoinPlayer(playerName);
		await World.Ticks(2);
		Vec3d pos = DropPos(player, zOffset);
		Item item = RequireItem(api, itemCode);

		EntityItem receiver = Spawn(api, new ItemStack(item, 5), pos, null);
		receiver.Attributes.SetFloat("deathTime", receiverTime);
		await World.Ticks(90);

		EntityItem donor = Spawn(api, new ItemStack(item, 1), pos, null);
		donor.Attributes.SetFloat("deathTime", donorTime);

		await World.Until(() => !donor.Alive, timeoutTicks: 300);
		Assert.True(receiver.Alive && receiver.Slot.Itemstack.StackSize == 6,
			$"setup failed: expected one pile of 6, got alive={receiver.Alive} size={receiver.Slot.Itemstack?.StackSize}");

		// The clock keeps ticking by a few seconds at most while the scenario runs.
		float expected = Math.Min(receiverTime, donorTime);
		float actual = receiver.Attributes.GetFloat("deathTime", -1f);
		Assert.True(
			Math.Abs(actual - expected) < 60f,
			$"merged pile deathTime is {actual}, expected about {expected} (the smaller of {receiverTime} and {donorTime}: "
			+ "deathTime is elapsed time, so the smaller value is the one with more life left)");
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task StacksWithDifferentDespawnTimes_Should_NotMerge()
	{
		ICoreServerAPI api = World.Api;
		ITestPlayer player = await World.JoinPlayer("clump-skip");
		await World.Ticks(2);
		Vec3d pos = DropPos(player, 40);
		Item item = RequireItem(api, "game:stone-peridotite");

		// A death drop style stack (kept for an hour) next to an ordinary one.
		EntityItem longLived = Spawn(api, new ItemStack(item, 5), pos, null);
		longLived.Attributes.SetInt("minsecondsToDespawn", 3600);
		EntityItem ordinary = Spawn(api, new ItemStack(item, 1), pos, null);

		// Well past the clump delay (1500 ms) and the settling time.
		await World.Ticks(240);

		Assert.True(
			longLived.Alive && ordinary.Alive
				&& longLived.Slot.Itemstack.StackSize == 5 && ordinary.Slot.Itemstack.StackSize == 1,
			"stacks with different minsecondsToDespawn were merged: "
			+ $"long-lived alive={longLived.Alive} size={longLived.Slot.Itemstack?.StackSize}, "
			+ $"ordinary alive={ordinary.Alive} size={ordinary.Slot.Itemstack?.StackSize}");
	}

	/// <summary>
	/// A merge across a chunk border must mark the receiver's chunk as modified: the donor's chunk is
	/// dirtied by its removal, but nothing else touches the receiver's, and the next save writes only
	/// chunks marked for saving. (A full save/restart scenario would hold this end to end; this one holds
	/// the flag directly.)
	/// </summary>
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task MergeAcrossAChunkBorder_Should_MarkTheReceiverChunkForSaving()
	{
		ICoreServerAPI api = World.Api;
		ITestPlayer player = await World.JoinPlayer("clump-chunk");
		await World.Ticks(2);
		Item item = RequireItem(api, "game:stone-limestone");

		// Two stacks on both sides of a chunk border (x = 32 * k), at the terrain surface.
		Vec3d basePos = DropPos(player, 48);
		int border = ((int)basePos.X / 32 + 1) * 32;
		double z = basePos.Z;
		int surface = World.Api.World.BlockAccessor.GetTerrainMapheightAt(new BlockPos(border, 1, (int)z, 0));
		double y = surface + 2;

		EntityItem receiver = Spawn(api, new ItemStack(item, 5), new Vec3d(border - 0.5, y, z), null);
		await World.Ticks(90);
		Assert.True(receiver.Alive, "setup failed: the receiver is gone");

		// Clear the flag on the receiver's chunk, then bring the donor from the other side.
		object receiverChunk = ChunkOf(api, receiver);
		SetDirtyForSaving(receiverChunk, false);

		EntityItem donor = Spawn(api, new ItemStack(item, 1), new Vec3d(border + 0.5, y, z), null);
		await World.Ticks(2);
		Assert.True(donor.InChunkIndex3d != receiver.InChunkIndex3d,
			"setup failed: both stacks are in the same chunk, the scenario needs a chunk border between them");
		SetDirtyForSaving(receiverChunk, false); // the spawn of the donor must not be what dirties it

		await World.Until(() => !donor.Alive, timeoutTicks: 300);
		Assert.True(receiver.Alive && receiver.Slot.Itemstack.StackSize == 6,
			$"setup failed: expected one pile of 6, got alive={receiver.Alive} size={receiver.Slot.Itemstack?.StackSize}");

		Assert.True(
			GetDirtyForSaving(receiverChunk),
			"the receiver's chunk is not marked for saving after a merge across a chunk border: "
			+ "the next save would write the old stack size");
	}

	// ---------------------------------------------------------------------------------------

	private static EntityItem Spawn(ICoreServerAPI api, ItemStack stack, Vec3d pos, string? dropper)
	{
		EntityItem entity = (EntityItem)api.World.SpawnItemEntity(stack, pos, new Vec3d());
		if (dropper != null) entity.ByPlayerUid = dropper;
		return entity;
	}

	private static Vec3d DropPos(ITestPlayer player, int zOffset)
	{
		return player.Player.Entity.Pos.XYZ.AddCopy(1.2, 0.2, 0.2 + zOffset);
	}

	private static Item RequireItem(ICoreServerAPI api, params string[] codes)
	{
		foreach (string code in codes)
		{
			Item item = api.World.GetItem(new AssetLocation(code));
			if (item != null) return item;
		}
		throw new InvalidOperationException("none of the item codes resolved: " + string.Join(", ", codes));
	}

	// IServerChunk.DirtyForSaving is reached by reflection so a rename shows up as a descriptive failure.
	private static object ChunkOf(ICoreServerAPI api, EntityItem entity)
	{
		object? chunk = api.WorldManager.GetChunk(entity.InChunkIndex3d);
		Assert.True(chunk != null, $"the chunk of the entity ({entity.InChunkIndex3d}) is not loaded.");
		return chunk!;
	}

	// DirtyForSaving may be a property or a field, public or not, declared on the chunk class or on one of
	// its base classes, so walk the hierarchy. A miss lists the members that mention "dirty" instead of
	// guessing.
	private static (System.Func<object, object?> Get, Action<object, object?> Set) DirtyAccessors(object chunk)
	{
		const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

		for (Type? t = chunk.GetType(); t != null; t = t.BaseType)
		{
			PropertyInfo? property = t.GetProperty("DirtyForSaving", flags);
			if (property != null && property.CanRead && property.CanWrite)
			{
				return (o => property.GetValue(o), (o, v) => property.SetValue(o, v));
			}

			FieldInfo? field = t.GetField("DirtyForSaving", flags);
			if (field != null)
			{
				return (o => field.GetValue(o), (o, v) => field.SetValue(o, v));
			}
		}

		var similar = new System.Collections.Generic.List<string>();
		for (Type? t = chunk.GetType(); t != null; t = t.BaseType)
		{
			foreach (MemberInfo m in t.GetMembers(flags))
			{
				if (m.Name.Contains("dirty", StringComparison.OrdinalIgnoreCase)) similar.Add($"{t.Name}.{m.Name}");
			}
		}
		Assert.Fail(
			$"DirtyForSaving not found on {chunk.GetType().Name} or its base classes. "
			+ "Members that mention 'dirty': " + (similar.Count == 0 ? "none" : string.Join(", ", similar)));
		throw new InvalidOperationException(); // unreachable
	}

	private static bool GetDirtyForSaving(object chunk) => (bool)DirtyAccessors(chunk).Get(chunk)!;

	private static void SetDirtyForSaving(object chunk, bool value) => DirtyAccessors(chunk).Set(chunk, value);
}
