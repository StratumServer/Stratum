using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// A resting item beyond 96 blocks ages on the same clock as a near item.
/// The throttle pays the skipped time instead of losing it to the 0.2s clamp.
/// </summary>
public class DespawnClockScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task FarItem_Should_AgeLikeANearItem()
	{
		ITestPlayer player = await World.JoinPlayer("despawn-clock");
		await player.TeleportTo(World.Spawn);
		await World.Ticks(2);
		BlockPos nearPos = player.Position.AddCopy(8, 1, 0);
		BlockPos farPos = player.Position.AddCopy(200, 1, 0);
		World.Api.WorldManager.LoadChunkColumnPriority(farPos.X / 32, farPos.Z / 32, new Vintagestory.API.Server.ChunkLoadOptions { KeepLoaded = true });
		await World.Until(() => World.Api.World.BlockAccessor.GetChunkAtBlockPos(farPos) != null, timeoutTicks: 600);

		Entity near = SpawnRestingItem(nearPos);
		Entity far = SpawnRestingItem(farPos);
		await World.Ticks(30);
		float nearStart = Clock(near);
		float farStart = Clock(far);
		await World.Ticks(180);
		float nearGain = Clock(near) - nearStart;
		float farGain = Clock(far) - farStart;

		Assert.True(nearGain > 1f, "the near item did not age");
		Assert.True(farGain > nearGain * 0.85f, "far clock " + farGain + " near clock " + nearGain);
	}

	private Entity SpawnRestingItem(BlockPos pos)
	{
		Item item = World.Api.World.GetItem(new AssetLocation("game:stone-granite"));
		Entity entity = World.Api.World.SpawnItemEntity(new ItemStack(item, 1), pos.ToVec3d());
		entity.Pos.Motion.Set(0, 0, 0);
		entity.ServerPos.Motion.Set(0, 0, 0);
		return entity;
	}

	private static float Clock(Entity entity)
	{
		float deathTime = entity.Attributes.GetFloat("deathTime");
		object behavior = null;
		foreach (EntityBehavior candidate in entity.SidedProperties.Behaviors)
		{
			if (candidate.GetType().Name == "EntityBehaviorDespawn")
			{
				behavior = candidate;
				break;
			}
		}
		if (behavior == null) return deathTime;
		FieldInfo accum = behavior.GetType().GetField("accumSeconds", BindingFlags.Instance | BindingFlags.NonPublic);
		float pending = accum == null ? 0f : (float)accum.GetValue(behavior)!;
		return deathTime + pending;
	}
}
