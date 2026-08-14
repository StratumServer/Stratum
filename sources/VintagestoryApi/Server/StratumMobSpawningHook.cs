namespace Vintagestory.API.Server;

// Server-side bridge for Stratum's natural mob spawning controls. The server assembly owns the
// persisted configuration. Worldgen code reads this bridge because it lives in VSEssentials, which
// references the API assembly but not VintagestoryLib.
public static class StratumMobSpawningHook
{
	public static volatile bool Enabled = true;
	public static volatile bool HostileEnabled = true;
	public static volatile bool NeutralEnabled = true;
	public static volatile bool PassiveEnabled = true;
	public static volatile float NaturalSpawnMultiplier = 1f;

	public static float GetNaturalSpawnMultiplier(string group)
	{
		if (!Enabled || NaturalSpawnMultiplier <= 0f)
		{
			return 0f;
		}

		if (string.Equals(group, "hostile", System.StringComparison.OrdinalIgnoreCase))
		{
			return HostileEnabled ? NaturalSpawnMultiplier : 0f;
		}

		if (string.Equals(group, "neutral", System.StringComparison.OrdinalIgnoreCase))
		{
			return NeutralEnabled ? NaturalSpawnMultiplier : 0f;
		}

		if (string.Equals(group, "passive", System.StringComparison.OrdinalIgnoreCase))
		{
			return PassiveEnabled ? NaturalSpawnMultiplier : 0f;
		}

		// Keep custom entities with no vanilla group compatible with existing servers.
		return NaturalSpawnMultiplier;
	}
}
