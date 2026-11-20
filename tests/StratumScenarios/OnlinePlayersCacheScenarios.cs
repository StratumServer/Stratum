using System.Collections.Concurrent;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// ServerMain.AllOnlinePlayers is cached per server pass (and dropped on every join or leave),
/// and the engine reads it from more than the game thread: the chunk thread (priority list),
/// the mob spawner thread, worldgen and any mod thread. The first cache rebuilt one shared
/// list and rewrote one shared array in place with no synchronization, so two concurrent
/// rebuilds made the list longer than the array (ArgumentException "Destination array was
/// not long enough") or let a reader see a half written array (a null entry). On the game
/// thread that exception aborted the whole entity-simulation pass: no entity ticked, no tick
/// listener ran. On the chunk thread it was unhandled and shut the server down.
///
/// This scenario hammers the getter from several plain threads with small random gaps
/// while the server ticks, and asserts four things: no call threw, no returned array held
/// a null, the joined player was in every array, and the engine logged no error while the
/// readers ran (the game thread reads the getter every pass, so a race that lands there is
/// logged by ServerMain.Process, as the ArgumentException itself or as the
/// NullReferenceException of a consumer that got a null entry, instead of reaching a reader).
///
/// The entry is also exact about membership: a join or a leave drops it at once, not at the
/// next pass. Vanilla builds the list on every call, so a mod or a system that reads it right
/// after a kick or a join, in the same pass, must see the change. The two membership scenarios
/// read the list in a pass, change membership later in that same pass, and read it again before
/// the pass ends.
/// </summary>
public class OnlinePlayersCacheScenarios : AtlasScenarioBase
{
	private const int ReaderThreads = 4;
	private const int MeasurementTicks = 300;

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task AllOnlinePlayers_Should_NeverTearOrThrow_When_ReadFromBackgroundThreads()
	{
		ITestPlayer player = await World.JoinPlayer("online-reader");
		await World.Ticks(5);

		// Read on the game thread: Atlas members and engine objects are game-thread
		// surfaces, only the getter under test is called from the readers.
		string uid = player.Player.PlayerUID;
		IWorldAccessor world = World.Api.World;

		var problems = new ConcurrentQueue<string>();
		long[] reads = new long[1];
		int logBefore = World.BootDiagnostics.Count;
		using var stop = new CancellationTokenSource();

		Thread[] readers = Enumerable.Range(0, ReaderThreads)
			.Select(i => new Thread(() => ReadLoop(world, uid, problems, stop.Token, reads, seed: i))
			{
				IsBackground = true,
				Name = $"online-reader-{i}",
			})
			.ToArray();

		try
		{
			foreach (Thread reader in readers)
			{
				reader.Start();
			}

			await World.Ticks(MeasurementTicks);
		}
		finally
		{
			stop.Cancel();
			foreach (Thread reader in readers.Where(t => t.IsAlive))
			{
				reader.Join();
			}
		}

		Assert.True(
			reads[0] > MeasurementTicks,
			$"readers barely ran ({reads[0]} reads over {MeasurementTicks} ticks); setup is broken");

		BootDiagnosticEntry[] errors = World.BootDiagnostics
			.Skip(logBefore)
			.Where(entry => entry.Level is EnumLogType.Error or EnumLogType.Fatal)
			.ToArray();

		Assert.True(
			problems.IsEmpty,
			$"{problems.Count} bad reads out of {reads[0]}, {errors.Length} engine error(s) logged meanwhile; "
			+ string.Join(" | ", problems.GroupBy(p => p).Select(g => $"{g.Count()}x {g.Key}")));

		Assert.True(
			errors.Length == 0,
			$"the engine logged {errors.Length} error(s) while the readers ran: "
			+ string.Join(" | ", errors.Select(e => $"{e.Level} {e.DescribeSource()}: {e.Message}")));
	}

	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task AllOnlinePlayers_Should_DropPlayer_When_KickedLaterInTheSamePass()
	{
		ITestPlayer player = await World.JoinPlayer("online-leaver");
		await World.Ticks(5);

		string uid = player.Player.PlayerUID;
		IWorldAccessor world = World.Api.World;
		Assert.Contains(world.AllOnlinePlayers, p => p.PlayerUID == uid);

		// The kick runs from the Until predicate: Atlas polls it in the pass's tick listeners, after
		// the entity simulation has read AllOnlinePlayers for this pass and before the scenario
		// resumes at the end of the same pass, so no later pass can rebuild the list first.
		bool kicked = false;
		await World.Until(() =>
		{
			if (!kicked)
			{
				kicked = true;
				_ = world.AllOnlinePlayers; // this pass's list exists and still holds the player
				player.Player.Disconnect();
			}

			return !player.IsConnected;
		});

		Assert.DoesNotContain(world.AllOnlinePlayers, p => p.PlayerUID == uid);
	}

	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task AllOnlinePlayers_Should_ListPlayer_When_JoinedLaterInTheSamePass()
	{
		ICoreServerAPI api = World.Api;
		IWorldAccessor world = api.World;
		const string name = "online-joiner";
		bool? listedOnSpawn = null;

		void OnSpawn(Entity entity)
		{
			if (entity is EntityPlayer { Player: { } spawned } && spawned.PlayerName == name)
			{
				listedOnSpawn = world.AllOnlinePlayers.Any(p => p.PlayerName == name);
			}
		}

		// One read per pass keeps this pass's list cached, without the joiner, ahead of the packet
		// that admits it.
		long prime = api.Event.RegisterGameTickListener(dt => { _ = world.AllOnlinePlayers; }, 1);
		api.Event.OnEntitySpawn += OnSpawn;
		try
		{
			await World.JoinPlayer(name);
		}
		finally
		{
			api.Event.OnEntitySpawn -= OnSpawn;
			api.Event.UnregisterGameTickListener(prime);
		}

		Assert.NotNull(listedOnSpawn);
		Assert.True(listedOnSpawn, "the joiner was missing from AllOnlinePlayers when its entity spawned");
	}

	private static void ReadLoop(
		IWorldAccessor world,
		string uid,
		ConcurrentQueue<string> problems,
		CancellationToken stop,
		long[] reads,
		int seed)
	{
		var random = new Random(seed);
		while (!stop.IsCancellationRequested)
		{
			try
			{
				bool present = false;
				foreach (IPlayer? entry in world.AllOnlinePlayers)
				{
					if (entry == null)
					{
						problems.Enqueue("null entry in AllOnlinePlayers");
					}
					else if (entry.PlayerUID == uid)
					{
						present = true;
					}
				}

				if (!present)
				{
					problems.Enqueue("joined player missing from AllOnlinePlayers");
				}
			}
			catch (Exception e)
			{
				problems.Enqueue($"{e.GetType().Name}: {e.Message}");
			}

			Interlocked.Increment(ref reads[0]);

			// Small random gap: the race needs two readers to miss the cache at the same moment,
			// and uneven pacing keeps the threads from settling into lockstep.
			Thread.SpinWait(random.Next(0, 300));
		}
	}
}
