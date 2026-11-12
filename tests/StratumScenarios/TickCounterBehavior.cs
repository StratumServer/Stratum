using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace StratumScenarios;

/// <summary>
/// Counts how many times the server ticks an entity. Attached at runtime from scenarios;
/// Entity.OnGameTick drives behavior ticks, so when the distance-band throttling skips an
/// entity's tick, this counter freezes with it.
/// </summary>
internal sealed class TickCounterBehavior : EntityBehavior
{
	public int Ticks;

	public TickCounterBehavior(Entity entity)
		: base(entity)
	{
	}

	public override void OnGameTick(float deltaTime) => Ticks++;

	public override string PropertyName() => "stratum:tickcounter";
}
