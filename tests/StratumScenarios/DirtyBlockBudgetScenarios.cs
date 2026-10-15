using System.Collections;
using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// The dirty-block pass publishes at most Performance.BlockTicks.MaxDirtyBlocksPerPass
/// entries (512). Modified queues are not under that cap: one pass empties them so
/// block packets stay ahead of block-entity packets.
/// </summary>
public class DirtyBlockBudgetScenarios : AtlasScenarioBase
{
	private const int Queued = 600;
	private const int Cap = 512;

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task OnePass_Should_LeaveDirtyBacklog_And_EmptyModified()
	{
		ITestPlayer player = await World.JoinPlayer("dirty-cap");
		await player.TeleportTo(World.Spawn);
		await World.Ticks(5);

		(int dirtyLeft, int dirtySecond, int modifiedAfter, int noRelightAfter, string report) =
			await Measure(World, player.Position);

		Assert.Equal(Queued - Cap, dirtyLeft);
		Assert.Equal(0, dirtySecond);
		Assert.Equal(0, modifiedAfter);
		Assert.Equal(0, noRelightAfter);
		Assert.Contains("modified=600->0", report, StringComparison.Ordinal);
		Assert.Contains("noRelight=600->0", report, StringComparison.Ordinal);
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task RepeatedPosition_Should_NotConsumeTheCap()
	{
		ITestPlayer player = await World.JoinPlayer("dirty-cap-dup");
		await player.TeleportTo(World.Spawn);
		await World.Ticks(5);

		int left = await MeasureDuplicates(World, player.Position);
		Assert.Equal(0, left);
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task DeepBacklog_Should_DrainFourTimesTheCap()
	{
		const int cap = 16;
		const int queued = 200;
		try
		{
			ITestPlayer player = await World.JoinPlayer("dirty-deep");
			await player.TeleportTo(World.Spawn);
			await World.Ticks(5);
			int left = await MeasureCustom(World, player.Position, queued, passes: 1, cap);
			Assert.Equal(queued - (cap * 4), left);
		}
		finally
		{
			SetDirtyCap(Cap);
		}
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task MaxIntCap_Should_EmptyTheQueueInOnePass()
	{
		try
		{
			ITestPlayer player = await World.JoinPlayer("dirty-max");
			await player.TeleportTo(World.Spawn);
			await World.Ticks(5);
			int left = await MeasureCustom(World, player.Position, queued: 200, passes: 1, cap: int.MaxValue);
			Assert.Equal(0, left);
		}
		finally
		{
			SetDirtyCap(Cap);
		}
	}

	[AtlasScenario(TimeoutMs = 180_000)]
	public async Task ExchangePacket_Should_SkipOnlyAnAgreedExceptClient()
	{
		ITestPlayer excepted = await World.JoinPlayer("dirty-except");
		ITestPlayer watcher = await World.JoinPlayer("dirty-watch");
		await excepted.TeleportTo(World.Spawn);
		await watcher.TeleportTo(World.Spawn);
		BlockPos origin = excepted.Position;
		await World.Until(() => ChunkSent(World, excepted, origin) && ChunkSent(World, watcher, origin), timeoutTicks: 400);

		var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int fired = 0;
		World.Api.Event.RegisterGameTickListener(_ =>
		{
			if (Interlocked.Exchange(ref fired, 1) != 0)
			{
				return;
			}

			try
			{
				AssertExchange(World, excepted, watcher, origin);
				done.TrySetResult();
			}
			catch (Exception ex)
			{
				done.TrySetException(ex);
			}
		}, 20);
		await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
	}

	internal static async Task<(int DirtyAfter, int DirtySecond, int ModifiedAfter, int NoRelightAfter, string Report)> Measure(
		IWorldSession world, BlockPos origin)
	{
		var done = new TaskCompletionSource<(int, int, int, int, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
		int fired = 0;
		world.Api.Event.RegisterGameTickListener(_ =>
		{
			if (Interlocked.Exchange(ref fired, 1) != 0)
			{
				return;
			}

			try
			{
				done.TrySetResult(MeasureOnGameThread(world, origin));
			}
			catch (Exception ex)
			{
				done.TrySetException(ex);
			}
		}, 20);

		return await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
	}

	private static (int DirtyAfter, int DirtySecond, int ModifiedAfter, int NoRelightAfter, string Report) MeasureOnGameThread(
		IWorldSession world, BlockPos origin)
	{
		object server = world.Api.World;
		object simulation = FindBlockSimulation(server);
		object dirty = Field(server, "DirtyBlocks");
		object modified = Field(server, "ModifiedBlocks");
		object noRelight = Field(server, "ModifiedBlocksNoRelight");
		MethodInfo pass = simulation.GetType().GetMethod(
			"HandleDirtyAndUpdatedBlocks",
			BindingFlags.Instance | BindingFlags.NonPublic)!;

		// The 100ms listener cannot run inside this callback. Clear anything already
		// queued, then the next call is the only pass that sees the 600 new entries.
		for (int i = 0; i < 40 && (Count(dirty) > 0 || Count(modified) > 0 || Count(noRelight) > 0); i++)
		{
			pass.Invoke(simulation, null);
		}

		int dirtyBefore = Count(dirty);
		EnqueueDirty(dirty, origin, Queued, repeats: false);
		pass.Invoke(simulation, null);
		int dirtyAfter = Count(dirty);
		int drained = dirtyBefore + Queued - dirtyAfter;
		if (drained != Cap)
		{
			throw new InvalidOperationException(
				$"one pass drained {drained} dirty blocks, expected {Cap} "
				+ $"(before {dirtyBefore}, after {dirtyAfter})");
		}

		pass.Invoke(simulation, null);
		int dirtySecond = Count(dirty);

		EnqueueModified(modified, origin, Queued);
		EnqueueModified(noRelight, origin, Queued);
		pass.Invoke(simulation, null);
		int modifiedAfter = Count(modified);
		int noRelightAfter = Count(noRelight);
		string report = PerformanceReport();
		return (dirtyAfter, dirtySecond, modifiedAfter, noRelightAfter, report);
	}

	private static async Task<int> MeasureDuplicates(IWorldSession world, BlockPos origin)
	{
		var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
		int fired = 0;
		world.Api.Event.RegisterGameTickListener(_ =>
		{
			if (Interlocked.Exchange(ref fired, 1) != 0)
			{
				return;
			}

			try
			{
				object server = world.Api.World;
				object simulation = FindBlockSimulation(server);
				object dirty = Field(server, "DirtyBlocks");
				MethodInfo pass = simulation.GetType().GetMethod(
					"HandleDirtyAndUpdatedBlocks",
					BindingFlags.Instance | BindingFlags.NonPublic)!;
				for (int i = 0; i < 40 && Count(dirty) > 0; i++)
				{
					pass.Invoke(simulation, null);
				}

				EnqueueDirty(dirty, origin, Queued, repeats: true);
				pass.Invoke(simulation, null);
				done.TrySetResult(Count(dirty));
			}
			catch (Exception ex)
			{
				done.TrySetException(ex);
			}
		}, 20);

		return await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
	}

	private static void EnqueueDirty(object queue, BlockPos origin, int count, bool repeats)
	{
		EnqueueDirty(queue, origin, count, repeats, exceptClientId: 0);
	}

	private static void EnqueueDirty(object queue, BlockPos origin, int count, bool repeats, int exceptClientId)
	{
		MethodInfo enqueue = queue.GetType().GetMethod("Enqueue")!;
		int x0 = origin.X & ~31;
		int z0 = origin.Z & ~31;
		for (int i = 0; i < count; i++)
		{
			int n = repeats ? 0 : i;
			enqueue.Invoke(queue, new object[] { new Vec4i(x0 + (n % 30), origin.Y, z0 + (n / 30), exceptClientId) });
		}
	}

	private static void EnqueueModified(object queue, BlockPos origin, int count)
	{
		MethodInfo enqueue = queue.GetType().GetMethod("Enqueue")!;
		int x0 = origin.X & ~31;
		int z0 = origin.Z & ~31;
		for (int i = 0; i < count; i++)
		{
			enqueue.Invoke(queue, new object[] { new BlockPos(x0 + (i % 30), origin.Y, z0 + (i / 30), 0) });
		}
	}

	private static int Count(object queue) => (int)queue.GetType().GetProperty("Count")!.GetValue(queue)!;

	private static object Field(object target, string name)
	{
		FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			?? throw new MissingFieldException(target.GetType().FullName, name);
		return field.GetValue(target)!;
	}

	private static object FindBlockSimulation(object server)
	{
		const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
		foreach (FieldInfo field in server.GetType().GetFields(flags))
		{
			if (field.GetValue(server) is not IEnumerable enumerable || field.GetValue(server) is string)
			{
				continue;
			}

			foreach (object? item in enumerable)
			{
				if (item != null && item.GetType().Name == "ServerSystemBlockSimulation")
				{
					return item;
				}
			}
		}

		throw new InvalidOperationException("ServerSystemBlockSimulation was not on the server");
	}

	private static string PerformanceReport()
	{
		Type runtime = Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib")!;
		object stats = runtime.GetProperty("PerformanceStats")!.GetValue(null)!;
		return (string)stats.GetType().GetMethod("BuildReport")!.Invoke(stats, null)!;
	}

	private static async Task<int> MeasureCustom(IWorldSession world, BlockPos origin, int queued, int passes, int cap)
	{
		var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
		int fired = 0;
		world.Api.Event.RegisterGameTickListener(_ =>
		{
			if (Interlocked.Exchange(ref fired, 1) != 0)
			{
				return;
			}

			try
			{
				SetDirtyCap(cap);
				object server = world.Api.World;
				object simulation = FindBlockSimulation(server);
				object dirty = Field(server, "DirtyBlocks");
				MethodInfo pass = simulation.GetType().GetMethod(
					"HandleDirtyAndUpdatedBlocks",
					BindingFlags.Instance | BindingFlags.NonPublic)!;
				for (int i = 0; i < 40 && Count(dirty) > 0; i++)
				{
					pass.Invoke(simulation, null);
				}

				EnqueueDirty(dirty, origin, queued, repeats: false);
				for (int i = 0; i < passes; i++)
				{
					pass.Invoke(simulation, null);
				}

				done.TrySetResult(Count(dirty));
			}
			catch (Exception ex)
			{
				done.TrySetException(ex);
			}
		}, 20);

		return await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
	}

	private static void SetDirtyCap(int cap)
	{
		Type runtime = Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib")!;
		object config = runtime.GetProperty("Config")!.GetValue(null)!;
		object performance = config.GetType().GetProperty("Performance")!.GetValue(config)!;
		object blockTicks = performance.GetType().GetProperty("BlockTicks")!.GetValue(performance)!;
		blockTicks.GetType().GetProperty("MaxDirtyBlocksPerPass")!.SetValue(blockTicks, cap);
	}

	private static bool ChunkSent(IWorldSession world, ITestPlayer player, BlockPos origin)
	{
		const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
		object server = world.Api.World;
		object clients = server.GetType().GetField("Clients", flags)!.GetValue(server)!;
		object client = clients.GetType().GetProperty("Item")!.GetValue(clients, new object[] { player.Player.ClientId })!;
		object map = server.GetType().GetField("WorldMap", flags)!.GetValue(server)!;
		int x = origin.X & ~31;
		int y = origin.Y;
		int z = origin.Z & ~31;
		long index = (long)map.GetType().GetMethod("ChunkIndex3D", new[] { typeof(int), typeof(int), typeof(int) })!.Invoke(map, new object[] { x / 32, y / 32, z / 32 })!;
		return (bool)client.GetType().GetMethod("DidSendChunk")!.Invoke(client, new object[] { index })!;
	}

	private static void AssertExchange(IWorldSession world, ITestPlayer excepted, ITestPlayer watcher, BlockPos origin)
	{
		object server = world.Api.World;
		object simulation = FindBlockSimulation(server);
		object dirty = Field(server, "DirtyBlocks");
		MethodInfo pass = simulation.GetType().GetMethod(
			"HandleDirtyAndUpdatedBlocks",
			BindingFlags.Instance | BindingFlags.NonPublic)!;
		for (int i = 0; i < 40 && Count(dirty) > 0; i++)
		{
			pass.Invoke(simulation, null);
		}

		int x0 = origin.X & ~31;
		int z0 = origin.Z & ~31;
		int y = origin.Y;
		var expected = new List<(int X, int Y, int Z)>();
		for (int i = 0; i < Queued; i++)
		{
			expected.Add((x0 + (i % 30), y, z0 + (i / 30)));
		}

		int extraY = (y & 31) == 31 ? y - 1 : y + 1;
		var extra = (x0, extraY, z0);
		PacketProbe exceptedProbe = PacketProbe.Attach(world, excepted);
		PacketProbe watcherProbe = PacketProbe.Attach(world, watcher);
		EnqueueDirty(dirty, origin, Queued, repeats: false, excepted.Player.ClientId);
		MethodInfo enqueue = dirty.GetType().GetMethod("Enqueue")!;
		enqueue.Invoke(dirty, new object[] { new Vec4i(extra.x0, extra.extraY, extra.z0, excepted.Player.ClientId) });
		enqueue.Invoke(dirty, new object[] { new Vec4i(extra.x0, extra.extraY, extra.z0, watcher.Player.ClientId) });
		pass.Invoke(simulation, null);
		pass.Invoke(simulation, null);

		IReadOnlyList<ExchangeBlock> exceptedPackets = exceptedProbe.NewExchangeBlocks();
		IReadOnlyList<ExchangeBlock> watcherPackets = watcherProbe.NewExchangeBlocks();
		Assert.Equal(Queued, watcherPackets.Count(p => expected.Contains((p.X, p.Y, p.Z))));
		Assert.DoesNotContain(exceptedPackets, p => expected.Contains((p.X, p.Y, p.Z)));
		Assert.Contains(watcherPackets, p => p.X == extra.x0 && p.Y == extra.extraY && p.Z == extra.z0);
		Assert.Contains(exceptedPackets, p => p.X == extra.x0 && p.Y == extra.extraY && p.Z == extra.z0);
		foreach (ExchangeBlock packet in watcherPackets)
		{
			Assert.Equal(BlockId(server, packet.X, packet.Y, packet.Z), packet.BlockId);
		}

		foreach (ExchangeBlock packet in exceptedPackets)
		{
			Assert.Equal(BlockId(server, packet.X, packet.Y, packet.Z), packet.BlockId);
		}
	}

	private static int BlockId(object server, int x, int y, int z)
	{
		object accessor = server.GetType().GetProperty("BlockAccessor")!.GetValue(server)!;
		object block = accessor.GetType().GetMethod("GetBlockRaw", new[] { typeof(int), typeof(int), typeof(int), typeof(int) })!.Invoke(accessor, new object[] { x, y, z, 0 })!;
		return (int)block.GetType().GetProperty("Id")!.GetValue(block)!;
	}
}
