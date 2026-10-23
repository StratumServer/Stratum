using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// A far entity keeps its physics step every tick. Its AI tick stays on the
/// distance interval.
/// </summary>
public class MovementSplitScenarios : AtlasScenarioBase
{
	private const int MeasurementTicks = 60;

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task FarEntity_Should_MoveEveryTick_And_TickAiOnTheInterval()
	{
		ITestPlayer anchor = await World.JoinPlayer("move-split");
		await anchor.TeleportTo(World.Spawn);
		await World.Ticks(2);
		BlockPos anchorPos = anchor.Position;
		BlockPos farPos = anchorPos.AddCopy(70, 1, 0);
		World.Api.WorldManager.LoadChunkColumnPriority(
			farPos.X / 32,
			farPos.Z / 32,
			new ChunkLoadOptions { KeepLoaded = true });
		await World.Until(
			() => World.Api.World.BlockAccessor.GetChunkAtBlockPos(farPos) != null,
			timeoutTicks: 600);

		Entity dummy = World.SpawnEntity("game:strawdummy", farPos);
		var ai = new TickCounterBehavior(dummy);
		dummy.SidedProperties.Behaviors.Add(ai);
		var physics = new PhysicsStepProbe(dummy);
		((IServerAPI)World.Api.Server).AddPhysicsTickable(physics);
		await World.Ticks(40);
		Assert.True(
			dummy.IsTracked != 0,
			"untracked at distance " + dummy.NearestPlayerDistance + " pos " + dummy.Pos.X + "," + dummy.Pos.Y + "," + dummy.Pos.Z);

		int aiBefore = ai.Ticks;
		int physicsBefore = physics.Ticks;
		long simBefore = World.EntitySimulationTicks;
		await World.Ticks(MeasurementTicks);
		long simDelta = World.EntitySimulationTicks - simBefore;
		int aiDelta = ai.Ticks - aiBefore;
		int physicsDelta = physics.Ticks - physicsBefore;

		Assert.True(simDelta > 0, "no entity-simulation ticks elapsed");
		Assert.True(
			physicsDelta >= simDelta,
			$"physics did not keep up with the server tick: {physicsDelta} vs {simDelta}");
		long expectedAi = simDelta / 5;
		Assert.True(
			Math.Abs(aiDelta - expectedAi) <= 2,
			$"AI did not stay on the far interval: {aiDelta} vs {expectedAi} over {simDelta}");
	}

	private sealed class PhysicsStepProbe : EntityBehavior, IPhysicsTickable
	{
		public int Ticks;

		public PhysicsStepProbe(Entity entity)
			: base(entity)
		{
		}

		public Entity Entity => entity;

		public bool Ticking { get; set; } = true;

		public void OnPhysicsTick(float dt) => Ticks++;

		public void AfterPhysicsTick(float dt)
		{
		}

		public override string PropertyName() => "stratum:physicsstep";
	}
}
