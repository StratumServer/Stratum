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
		MethodInfo enqueue = queue.GetType().GetMethod("Enqueue")!;
		int x0 = origin.X & ~31;
		int z0 = origin.Z & ~31;
		for (int i = 0; i < count; i++)
		{
			int n = repeats ? 0 : i;
			enqueue.Invoke(queue, new object[] { new Vec4i(x0 + (n % 30), origin.Y, z0 + (n / 30), 0) });
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
}
