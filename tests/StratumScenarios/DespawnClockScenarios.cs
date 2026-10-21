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

	[AtlasScenario(TimeoutMs = 120_000)]
	public async Task StallOnThePayingPass_Should_PayHealthInFull_And_ClampDespawn()
	{
		ITestPlayer player = await World.JoinPlayer("despawn-stall");
		await player.TeleportTo(World.Spawn);
		await World.Ticks(2);
		BlockPos farPos = player.Position.AddCopy(200, 1, 0);
		World.Api.WorldManager.LoadChunkColumnPriority(farPos.X / 32, farPos.Z / 32, new Vintagestory.API.Server.ChunkLoadOptions { KeepLoaded = true });
		await World.Until(() => World.Api.World.BlockAccessor.GetChunkAtBlockPos(farPos) != null, timeoutTicks: 600);
		Entity far = SpawnRestingItem(farPos);
		await World.Ticks(5);

		object sim = Simulation();
		MethodInfo throttle = sim.GetType().GetMethod("ShouldThrottleStratumEntityTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
		object config = EntityTickingConfig();
		float raw = 0f;
		float clamped = float.NaN;
		bool paid = false;
		for (int i = 0; i < 400 && !paid; i++)
		{
			object[] args = { far, config, 1f, 0f, 0f, false };
			bool skip = (bool)throttle.Invoke(sim, args)!;
			if (!skip)
			{
				raw = (float)args[3]!;
				clamped = (float)args[4]!;
				paid = raw > 1f;
			}
		}

		Assert.True(paid, "the far item never paid out a stalled tick");
		Assert.True(raw > clamped + 0.5f, "raw " + raw + " clamped " + clamped);
		Assert.True(clamped > 0.2f, "despawn clamp collapsed the payout to one tick");

		object behavior = null;
		foreach (EntityBehavior candidate in far.SidedProperties.Behaviors)
		{
			if (candidate.GetType().Name == "EntityBehaviorDespawn")
			{
				behavior = candidate;
				break;
			}
		}
		Assert.NotNull(behavior);
		FieldInfo accum = behavior.GetType().GetField("accumSeconds", BindingFlags.Instance | BindingFlags.NonPublic)!;
		float before = (float)accum.GetValue(behavior)!;
		StratumEntityBehaviorTimings.DespawnDeltaTime = clamped;
		try
		{
			((EntityBehavior)behavior).OnGameTick(raw);
		}
		finally
		{
			StratumEntityBehaviorTimings.DespawnDeltaTime = float.NaN;
		}
		float gained = (float)accum.GetValue(behavior)! - before;
		Assert.True(gained > clamped - 0.05f && gained < raw, "despawn gained " + gained + " from raw " + raw + " clamped " + clamped);
	}

	private static object EntityTickingConfig()
	{
		Type runtime = Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib")!;
		object config = runtime.GetProperty("Config")!.GetValue(null)!;
		object performance = config.GetType().GetProperty("Performance")!.GetValue(config)!;
		return performance.GetType().GetProperty("EntityTicking")!.GetValue(performance)!;
	}

	private object Simulation()
	{
		object server = World.Api.World;
		Array systems = (Array)server.GetType().GetField("Systems", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(server)!;
		foreach (object system in systems)
		{
			if (system != null && system.GetType().Name == "ServerSystemEntitySimulation")
			{
				return system;
			}
		}

		throw new InvalidOperationException("entity simulation was not loaded");
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
