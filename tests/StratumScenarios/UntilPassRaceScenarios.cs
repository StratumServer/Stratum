using System.Collections;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// untilPass only moves upward. A request that is already in the index at -1
/// is queued by exactly one raiser. The first round races all addChunkColumnRequest
/// raisers; the second round mixes EnsureMinimumWorldgenPassAt with an add raiser.
/// A raiser that misses the barrier does not touch the queue after the test has moved on.
/// </summary>
public class UntilPassRaceScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task ConcurrentRaises_Should_KeepTheHigherPass_And_QueueOnce()
	{
		await World.Ticks(1);
		object server = World.Api.World;
		object chunkThread = server.GetType().GetField("chunkThread", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
		object supply = chunkThread.GetType().GetField("loadsavechunks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chunkThread)!;
		MethodInfo pause = supply.GetType().GetMethod("PauseAllWorldgenThreads")!;
		MethodInfo resume = supply.GetType().GetMethod("ResumeAllWorldgenThreads")!;
		Assert.True((bool)pause.Invoke(supply, new object[] { 5000 })!, "worldgen did not pause");

		const long column1 = long.MaxValue - 17;
		const long column2 = long.MaxValue - 18;
		Task[]? tasks1 = null;
		Task[]? tasks2 = null;
		try
		{
			Type requestType = Type.GetType("Vintagestory.Server.ChunkColumnLoadRequest, VintagestoryLib")!;
			FieldInfo untilPass = requestType.GetField("untilPass", BindingFlags.Instance | BindingFlags.NonPublic)!;
			object queue = chunkThread.GetType().GetField("requestedChunkColumns", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chunkThread)!;
			object index = queue.GetType().GetField("elementsByIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(queue)!;
			MethodInfo add = chunkThread.GetType().GetMethod("addChunkColumnRequest", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { requestType }, null)!;
			MethodInfo ensure = chunkThread.GetType().GetMethod("EnsureMinimumWorldgenPassAt", BindingFlags.Instance | BindingFlags.NonPublic)!;

			// Round 1: race addChunkColumnRequest raisers against an indexed -1 request.
			object parked1 = Activator.CreateInstance(requestType, column1, 4, 4, 0, -1, server)!;
			Assert.True((bool)index.GetType().GetMethod("TryAdd")!.Invoke(index, new[] { column1, parked1 })!);

			var start1 = new Barrier(8);
			tasks1 = new Task[8];
			for (int i = 0; i < tasks1.Length; i++)
			{
				int pass = i == 0 ? 7 : 4;
				tasks1[i] = Task.Run(() =>
				{
					try
					{
						if (!start1.SignalAndWait(TimeSpan.FromSeconds(10)))
						{
							return;
						}
					}
					catch (BarrierPostPhaseException)
					{
						return;
					}
					object challenger = Activator.CreateInstance(requestType, column1, 4, 4, 0, pass, server)!;
					add.Invoke(chunkThread, new[] { challenger });
				});
			}
			Assert.True(Task.WaitAll(tasks1, TimeSpan.FromSeconds(10)), "round 1 raises did not finish");
			Assert.Equal(7, (int)untilPass.GetValue(parked1)!);
			Assert.Equal(1, CountQueued(queue, parked1));

			// Round 2: mix seven EnsureMinimumWorldgenPassAt raisers at pass 6 with one add at pass 7.
			object parked2 = Activator.CreateInstance(requestType, column2, 4, 4, 0, -1, server)!;
			Assert.True((bool)index.GetType().GetMethod("TryAdd")!.Invoke(index, new[] { column2, parked2 })!);

			var start2 = new Barrier(8);
			tasks2 = new Task[8];
			for (int i = 0; i < tasks2.Length; i++)
			{
				int pass = i == 0 ? 7 : 6;
				bool throughGate = i != 0;
				tasks2[i] = Task.Run(() =>
				{
					try
					{
						if (!start2.SignalAndWait(TimeSpan.FromSeconds(10)))
						{
							return;
						}
					}
					catch (BarrierPostPhaseException)
					{
						return;
					}
					if (throughGate)
					{
						ensure.Invoke(chunkThread, new object[] { column2, 4, 4, pass, 0L });
						return;
					}
					object challenger = Activator.CreateInstance(requestType, column2, 4, 4, 0, pass, server)!;
					add.Invoke(chunkThread, new[] { challenger });
				});
			}
			Assert.True(Task.WaitAll(tasks2, TimeSpan.FromSeconds(10)), "round 2 raises did not finish");
			Assert.Equal(7, (int)untilPass.GetValue(parked2)!);
			Assert.Equal(1, CountQueued(queue, parked2));
		}
		finally
		{
			if (tasks1 != null)
			{
				try
				{
					Task.WaitAll(tasks1, TimeSpan.FromSeconds(10));
				}
				catch (AggregateException)
				{
				}
			}
			if (tasks2 != null)
			{
				try
				{
					Task.WaitAll(tasks2, TimeSpan.FromSeconds(10));
				}
				catch (AggregateException)
				{
				}
			}
			object queue = chunkThread.GetType().GetField("requestedChunkColumns", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chunkThread)!;
			MethodInfo remove = queue.GetType().GetMethod("Remove")!;
			remove.Invoke(queue, new object[] { column1 });
			remove.Invoke(queue, new object[] { column2 });
			resume.Invoke(supply, null);
		}
	}

	private static int CountQueued(object queue, object request)
	{
		int count = 0;
		foreach (object item in (IEnumerable)queue)
		{
			if (ReferenceEquals(item, request))
			{
				count++;
			}
		}
		return count;
	}
}
