using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// An entity that despawns while its physics activation is still queued must
/// not be registered later.
/// </summary>
public class PhysicsDespawnScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 60_000)]
	public async Task DeferredAdd_Should_SkipAnEntityThatAlreadyDespawned()
	{
		object physics = PhysicsConfig();
		PropertyInfo cap = physics.GetType().GetProperty("MaxActivationsPerTick")
			?? throw new System.InvalidOperationException("MaxActivationsPerTick was missing");
		PropertyInfo enabled = physics.GetType().GetProperty("Enabled")
			?? throw new System.InvalidOperationException("Physics.Enabled was missing");
		int previousCap = (int)cap.GetValue(physics)!;
		bool previousEnabled = (bool)enabled.GetValue(physics)!;
		cap.SetValue(physics, 1);
		enabled.SetValue(physics, true);
		try
		{
			Entity first = World.SpawnEntity("game:strawdummy", World.Spawn.AddCopy(2, 1, 0));
			Entity second = World.SpawnEntity("game:strawdummy", World.Spawn.AddCopy(3, 1, 0));
			Entity third = World.SpawnEntity("game:strawdummy", World.Spawn.AddCopy(4, 1, 0));
			await World.Ticks(1);
			second.Die(EnumDespawnReason.Removed, null);
			third.Die(EnumDespawnReason.Removed, null);
			await World.Ticks(8);

			Assert.DoesNotContain(second.EntityId, TickableIds());
			Assert.DoesNotContain(third.EntityId, TickableIds());
			_ = first;
		}
		finally
		{
			cap.SetValue(physics, previousCap);
			enabled.SetValue(physics, previousEnabled);
		}
	}

	private System.Collections.Generic.List<long> TickableIds()
	{
		object manager = PhysicsManager();
		var tickables = (System.Collections.IEnumerable)manager.GetType()
			.GetField("tickables", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(manager)!;
		var ids = new System.Collections.Generic.List<long>();
		foreach (object tickable in tickables)
		{
			Entity entity = (Entity)tickable.GetType().GetProperty("Entity")!.GetValue(tickable)!;
			ids.Add(entity.EntityId);
		}
		return ids;
	}

	private object PhysicsManager()
	{
		object server = World.Api.World;
		var systems = (System.Collections.IEnumerable)server.GetType()
			.GetField("Systems", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(server)!;
		foreach (object system in systems)
		{
			FieldInfo field = system.GetType().GetField("physicsManager", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field != null) return field.GetValue(system)!;
		}
		throw new System.InvalidOperationException("physicsManager was not on a server system");
	}

	private static object PhysicsConfig()
	{
		Type runtime = Type.GetType("Vintagestory.Server.StratumRuntime, VintagestoryLib")
			?? throw new System.InvalidOperationException("StratumRuntime was not loaded");
		object config = runtime.GetProperty("Config")!.GetValue(null)!;
		object performance = config.GetType().GetProperty("Performance")!.GetValue(config)!;
		return performance.GetType().GetProperty("Physics")!.GetValue(performance)!;
	}
}
