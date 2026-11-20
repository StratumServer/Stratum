using System.Threading;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;
using Xunit.Abstractions;

namespace StratumScenarios;

/// <summary>
/// The physics activation stride must leave entity motion on every physics step while
/// banking skipped time for thread-safe behaviors. Main-thread AI keeps its own distance
/// interval, and that interval must follow a player walking into a nearer band.
/// </summary>
public class MovementSplitScenarios : AtlasScenarioBase
{
	private const int MeasurementTicks = 120;
	private const int FarPhysicsStride = 3;
	private const double WalkStep = 0.25;
	private const int WalkSteps = 220;

	private readonly ITestOutputHelper output;

	public MovementSplitScenarios(ITestOutputHelper output) => this.output = output;

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task FarEntity_Should_MoveEveryTick_And_TickAiOnTheInterval()
	{
		ITestPlayer anchor = await World.JoinPlayer("move-split");
		Entity? dummy = null;
		PhysicsStepProbe? physics = null;
		try
		{
			await anchor.TeleportTo(World.Spawn);
			await World.Ticks(2);
			BlockPos anchorPos = anchor.Position;
			BlockPos farPos = anchorPos.AddCopy(70, 1, 0);
			await LoadColumn(farPos);

			dummy = World.SpawnEntity("game:strawdummy", farPos);
			var ai = new TickCounterBehavior(dummy);
			dummy.SidedProperties.Behaviors.Add(ai);
			physics = ReplacePhysicsTickable(dummy);
			await World.Ticks(40);
			Assert.True(
				dummy.IsTracked != 0,
				"untracked at distance " + dummy.NearestPlayerDistance + " pos " + dummy.Pos.X + "," + dummy.Pos.Y + "," + dummy.Pos.Z);

			int aiBefore = ai.Ticks;
			long physicsBefore = physics.Ticks;
			long simBefore = World.EntitySimulationTicks;
			await World.Ticks(MeasurementTicks);
			long simDelta = World.EntitySimulationTicks - simBefore;
			int aiDelta = ai.Ticks - aiBefore;
			long physicsDelta = physics.Ticks - physicsBefore;

			Assert.True(simDelta > 0, "no entity-simulation ticks elapsed");
			Assert.True(
				physicsDelta >= simDelta,
				$"physics did not keep up with the server tick: {physicsDelta} vs {simDelta}");
			long expectedAi = simDelta / 5;
			Assert.True(
				Math.Abs(aiDelta - expectedAi) <= 2,
				$"AI did not stay on the far interval: {aiDelta} vs {expectedAi} over {simDelta}");
			output.WriteLine($"far movement: physics {physicsDelta}/{simDelta} simulation ticks; AI {aiDelta}/{simDelta}");
		}
		finally
		{
			try
			{
				if (physics != null)
				{
					((IServerAPI)World.Api.Server).RemovePhysicsTickable(physics);
				}
				if (dummy is { Alive: true })
				{
					dummy.Die(EnumDespawnReason.Removed);
				}
				await World.Ticks(2);
			}
			finally
			{
				anchor.Player.Disconnect();
			}
		}
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task FarEntity_Should_UseNearAiInterval_AfterPlayerWalksUp()
	{
		ITestPlayer anchor = await World.JoinPlayer("move-walk-up");
		Entity? dummy = null;
		try
		{
			await anchor.TeleportTo(World.Spawn);
			await World.Ticks(2);
			BlockPos start = anchor.Position;
			BlockPos farPos = start.AddCopy(70, 1, 0);
			await LoadColumn(farPos);

			dummy = World.SpawnEntity("game:strawdummy", farPos);
			var ai = new TickCounterBehavior(dummy);
			dummy.SidedProperties.Behaviors.Add(ai);
			await World.Ticks(60);
			Assert.True(dummy.IsTracked != 0, $"dummy is not tracked at {dummy.NearestPlayerDistance:F1} blocks");

			long farSimBefore = World.EntitySimulationTicks;
			int farAiBefore = ai.Ticks;
			await World.Ticks(MeasurementTicks);
			long farSimTicks = World.EntitySimulationTicks - farSimBefore;
			int farAiTicks = ai.Ticks - farAiBefore;
			long expectedFarAi = farSimTicks / 5;
			Assert.True(Math.Abs(farAiTicks - expectedFarAi) <= 2,
				$"the dummy did not start in the far AI band: {farAiTicks} vs {expectedFarAi} over {farSimTicks}");

			// Move the live player position by less than one block per tick. This exercises
			// ordinary movement without TeleportTo's explicit cache invalidation path.
			double startX = anchor.Entity.Pos.X;
			for (int i = 0; i < WalkSteps; i++)
			{
				anchor.Entity.Pos.X += WalkStep;
				await World.Ticks(1);
			}

			double walked = anchor.Entity.Pos.X - startX;
			double finalDistance = dummy.NearestPlayerDistance;
			Assert.True(walked >= 50 && walked <= 60,
				$"the anchor moved {walked:F1} blocks during the gradual walk; setup is invalid");
			Assert.True(finalDistance < 24,
				$"the player ended {finalDistance:F1} blocks from the dummy, outside the near physics band; setup is invalid");

			long nearSimBefore = World.EntitySimulationTicks;
			int nearAiBefore = ai.Ticks;
			await World.Ticks(MeasurementTicks);
			long nearSimTicks = World.EntitySimulationTicks - nearSimBefore;
			int nearAiTicks = ai.Ticks - nearAiBefore;
			Assert.True(nearSimTicks > 0, "no entity-simulation ticks elapsed after the walk-up");
			Assert.True(Math.Abs(nearAiTicks - nearSimTicks) <= 2,
				$"the near AI interval did not take effect after walking up: {nearAiTicks} vs {nearSimTicks} over {nearSimTicks} simulation ticks; " +
				$"the player moved {walked:F1} blocks and ended {finalDistance:F1} blocks away");
			output.WriteLine($"walk-up: moved {walked:F1} blocks in {WalkSteps} x 0.25-block steps; distance {finalDistance:F1}; " +
				$"far AI {farAiTicks}/{farSimTicks}, near AI {nearAiTicks}/{nearSimTicks}");
		}
		finally
		{
			try
			{
				if (dummy is { Alive: true })
				{
					dummy.Die(EnumDespawnReason.Removed);
				}
				await World.Ticks(2);
			}
			finally
			{
				anchor.Player.Disconnect();
			}
		}
	}

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task ThreadSafeBehavior_Should_ReceiveBankedDeltaTime_OnFarStride()
	{
		ITestPlayer anchor = await World.JoinPlayer("move-threadsafe");
		Entity? near = null;
		Entity? far = null;
		PhysicsStepProbe? nearPhysics = null;
		PhysicsStepProbe? farPhysics = null;
		try
		{
			await anchor.TeleportTo(World.Spawn);
			await World.Ticks(2);
			BlockPos origin = anchor.Position;
			BlockPos nearPos = origin.AddCopy(8, 1, 0);
			BlockPos farPos = origin.AddCopy(70, 1, 0);
			await LoadColumn(farPos);

			near = World.SpawnEntity("game:strawdummy", nearPos);
			far = World.SpawnEntity("game:strawdummy", farPos);
			var nearBehavior = new ThreadSafeDeltaProbe(near);
			var farBehavior = new ThreadSafeDeltaProbe(far);
			near.SidedProperties.Behaviors.Add(nearBehavior);
			far.SidedProperties.Behaviors.Add(farBehavior);
			nearPhysics = ReplacePhysicsTickable(near);
			farPhysics = ReplacePhysicsTickable(far);
			await World.Ticks(60);
			Assert.True(near.IsTracked != 0 && far.IsTracked != 0,
				$"a control dummy is untracked: near {near.NearestPlayerDistance:F1}, far {far.NearestPlayerDistance:F1}");

			nearBehavior.Reset();
			farBehavior.Reset();
			nearPhysics.Reset();
			farPhysics.Reset();
			await World.Ticks(MeasurementTicks);

			long nearPasses = nearPhysics.Passes;
			long farPasses = farPhysics.Passes;
			long nearSteps = nearPhysics.Ticks;
			long farSteps = farPhysics.Ticks;
			int nearCalls = nearBehavior.Calls;
			int farCalls = farBehavior.Calls;
			double nearDelta = nearBehavior.DeltaSeconds;
			double farDelta = farBehavior.DeltaSeconds;
			long expectedFarCalls = nearPasses / FarPhysicsStride;

			Assert.True(nearPasses > 0 && nearPasses == farPasses,
				$"physics pass counts differ: near {nearPasses}, far {farPasses}");
			Assert.True(nearCalls >= nearPasses - 1,
				$"near thread-safe behavior ran {nearCalls} times for {nearPasses} physics passes");
			Assert.True(Math.Abs(farCalls - expectedFarCalls) <= 2,
				$"far thread-safe behavior ran {farCalls} times, expected about {expectedFarCalls} at stride {FarPhysicsStride} " +
				$"over {nearPasses} physics passes");
			Assert.True(nearSteps >= nearPasses - 1,
				$"near physics ran {nearSteps} steps for {nearPasses} physics passes");
			Assert.True(farSteps >= farPasses - 1,
				$"far physics ran {farSteps} steps for {farPasses} physics passes");
			Assert.True(Math.Abs(nearDelta - farDelta) <= 0.2,
				$"banked delta time differs by {Math.Abs(nearDelta - farDelta):F3}s: near {nearDelta:F3}s over {nearCalls} calls, " +
				$"far {farDelta:F3}s over {farCalls} calls");

			output.WriteLine($"thread-safe probe: {nearPasses} physics passes; steps near/far {nearSteps}/{farSteps}; " +
				$"calls near/far {nearCalls}/{farCalls} (far stride {FarPhysicsStride}); dt near/far {nearDelta:F3}/{farDelta:F3}s");
		}
		finally
		{
			try
			{
				IServerAPI server = (IServerAPI)World.Api.Server;
				if (nearPhysics != null) server.RemovePhysicsTickable(nearPhysics);
				if (farPhysics != null) server.RemovePhysicsTickable(farPhysics);
				if (near is { Alive: true }) near.Die(EnumDespawnReason.Removed);
				if (far is { Alive: true }) far.Die(EnumDespawnReason.Removed);
				await World.Ticks(2);
			}
			finally
			{
				anchor.Player.Disconnect();
			}
		}
	}

	private async Task LoadColumn(BlockPos position)
	{
		World.Api.WorldManager.LoadChunkColumnPriority(
			position.X / 32,
			position.Z / 32,
			new ChunkLoadOptions { KeepLoaded = true });
		await World.Until(
			() => World.Api.World.BlockAccessor.GetChunkAtBlockPos(position) != null,
			timeoutTicks: 600);
	}

	private PhysicsStepProbe ReplacePhysicsTickable(Entity entity)
	{
		IPhysicsTickable inner = Assert.Single(entity.SidedProperties.Behaviors.OfType<IPhysicsTickable>());
		var probe = new PhysicsStepProbe(inner);
		IServerAPI server = (IServerAPI)World.Api.Server;
		server.RemovePhysicsTickable(inner);
		server.AddPhysicsTickable(probe);
		return probe;
	}

	private sealed class PhysicsStepProbe : IPhysicsTickable
	{
		private long ticks;
		private long passes;

		public PhysicsStepProbe(IPhysicsTickable inner) => Inner = inner;

		public IPhysicsTickable Inner { get; }

		public long Ticks => Interlocked.Read(ref ticks);

		public long Passes => Interlocked.Read(ref passes);

		public Entity Entity => Inner.Entity;

		public bool Ticking
		{
			get => Inner.Ticking;
			set => Inner.Ticking = value;
		}

		public void OnPhysicsTick(float dt)
		{
			Inner.OnPhysicsTick(dt);
			Interlocked.Increment(ref ticks);
		}

		public void AfterPhysicsTick(float dt)
		{
			Inner.AfterPhysicsTick(dt);
			Interlocked.Increment(ref passes);
		}

		public void Reset()
		{
			Interlocked.Exchange(ref ticks, 0);
			Interlocked.Exchange(ref passes, 0);
		}
	}

	private sealed class ThreadSafeDeltaProbe : EntityBehavior
	{
		private long calls;
		private long deltaMicroseconds;

		public ThreadSafeDeltaProbe(Entity entity)
			: base(entity)
		{
		}

		public override bool ThreadSafe => true;

		public int Calls => (int)Interlocked.Read(ref calls);

		public double DeltaSeconds => Interlocked.Read(ref deltaMicroseconds) / 1_000_000d;

		public override void OnGameTick(float deltaTime)
		{
			Interlocked.Increment(ref calls);
			Interlocked.Add(ref deltaMicroseconds, (long)Math.Round(deltaTime * 1_000_000));
		}

		public void Reset()
		{
			Interlocked.Exchange(ref calls, 0);
			Interlocked.Exchange(ref deltaMicroseconds, 0);
		}

		public override string PropertyName() => "stratum:threadsavedeltaprobe";
	}
}
