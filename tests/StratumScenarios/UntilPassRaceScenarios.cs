using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// untilPass only moves upward. A request that starts below zero is queued
/// by exactly one raiser.
/// </summary>
public class UntilPassRaceScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task ConcurrentRaises_Should_KeepTheHigherPass_And_QueueOnce()
	{
		await World.Ticks(1);
		Type requestType = Type.GetType("Vintagestory.Server.ChunkColumnLoadRequest, VintagestoryLib")
			?? throw new InvalidOperationException("ChunkColumnLoadRequest was not loaded");
		MethodInfo raise = requestType.GetMethod("StratumTryRaiseUntilPass", BindingFlags.Static | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("StratumTryRaiseUntilPass was not found");
		FieldInfo untilPass = requestType.GetField("untilPass", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("untilPass was not found");

		object request = Activator.CreateInstance(requestType, 1L, 4, 4, 0, -1, World.Api.World)!;
		int enqueueSignals = 0;
		var start = new Barrier(8);
		Task[] tasks = new Task[8];
		for (int i = 0; i < tasks.Length; i++)
		{
			int pass = i == 0 ? 7 : 4;
			tasks[i] = Task.Run(() =>
			{
				start.SignalAndWait();
				object[] args = { request, pass, 0 };
				bool stored = (bool)raise.Invoke(null, args)!;
				int replaced = (int)args[2]!;
				if (stored && replaced < 0)
				{
					Interlocked.Increment(ref enqueueSignals);
				}
			});
		}
		Task.WaitAll(tasks);

		Assert.Equal(7, (int)untilPass.GetValue(request)!);
		Assert.Equal(1, enqueueSignals);

		object parked = Activator.CreateInstance(requestType, 2L, 5, 5, 0, 3, World.Api.World)!;
		int parkedSignals = 0;
		Task.WaitAll(
			Task.Run(() => Raise(raise, parked, 2, ref parkedSignals)),
			Task.Run(() => Raise(raise, parked, 5, ref parkedSignals)));
		Assert.Equal(5, (int)untilPass.GetValue(parked)!);
		Assert.Equal(0, parkedSignals);
	}

	private static void Raise(MethodInfo raise, object request, int pass, ref int signals)
	{
		object[] args = { request, pass, 0 };
		bool stored = (bool)raise.Invoke(null, args)!;
		int replaced = (int)args[2]!;
		if (stored && replaced < 0)
		{
			Interlocked.Increment(ref signals);
		}
	}
}
