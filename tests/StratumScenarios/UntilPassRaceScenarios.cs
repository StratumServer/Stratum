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
/// is queued by exactly one call to addChunkColumnRequest.
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
		try
		{
			Type requestType = Type.GetType("Vintagestory.Server.ChunkColumnLoadRequest, VintagestoryLib")!;
			FieldInfo untilPass = requestType.GetField("untilPass", BindingFlags.Instance | BindingFlags.NonPublic)!;
			object queue = chunkThread.GetType().GetField("requestedChunkColumns", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chunkThread)!;
			object index = queue.GetType().GetField("elementsByIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(queue)!;
			MethodInfo add = chunkThread.GetType().GetMethod("addChunkColumnRequest", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { requestType }, null)!;
			const long column = long.MaxValue - 17;
			object parked = Activator.CreateInstance(requestType, column, 4, 4, 0, -1, server)!;
			Assert.True((bool)index.GetType().GetMethod("TryAdd")!.Invoke(index, new[] { column, parked })!);

			var start = new Barrier(8);
			Task[] tasks = new Task[8];
			for (int i = 0; i < tasks.Length; i++)
			{
				int pass = i == 0 ? 7 : 4;
				tasks[i] = Task.Run(() =>
				{
					start.SignalAndWait();
					object challenger = Activator.CreateInstance(requestType, column, 4, 4, 0, pass, server)!;
					add.Invoke(chunkThread, new[] { challenger });
				});
			}
			Assert.True(Task.WaitAll(tasks, TimeSpan.FromSeconds(10)), "raises did not finish");

			Assert.Equal(7, (int)untilPass.GetValue(parked)!);
			Assert.Equal(1, CountQueued(queue, parked));
		}
		finally
		{
			object queue = chunkThread.GetType().GetField("requestedChunkColumns", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chunkThread)!;
			queue.GetType().GetMethod("Remove")!.Invoke(queue, new object[] { long.MaxValue - 17 });
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
