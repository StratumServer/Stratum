using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

internal static class StratumMobSpawning
{
	public static void Refresh()
	{
		StratumRuntime.Config.EnsurePopulated();
		StratumMobSpawningConfig config = StratumRuntime.Config.MobSpawning;
		StratumMobSpawningHook.Enabled = config.Enabled;
		StratumMobSpawningHook.HostileEnabled = config.HostileEnabled;
		StratumMobSpawningHook.NeutralEnabled = config.NeutralEnabled;
		StratumMobSpawningHook.PassiveEnabled = config.PassiveEnabled;
		StratumMobSpawningHook.NaturalSpawnMultiplier = config.NaturalSpawnMultiplier;
	}

	public static bool IsCategory(Entity entity, string category)
	{
		if (entity == null || !entity.IsCreature)
		{
			return false;
		}

		if (string.Equals(category, "all", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		string group = entity.Properties?.Server?.SpawnConditions?.Runtime?.Group;
		return string.Equals(group, category, StringComparison.OrdinalIgnoreCase);
	}

	public static bool IsKnownCategory(string category)
	{
		return string.Equals(category, "all", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(category, "hostile", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(category, "neutral", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(category, "passive", StringComparison.OrdinalIgnoreCase);
	}
}

internal sealed class StratumMobSpawningSystem
{
	public StratumMobSpawningSystem(ServerMain server)
	{
		StratumMobSpawning.Refresh();
	}
}
