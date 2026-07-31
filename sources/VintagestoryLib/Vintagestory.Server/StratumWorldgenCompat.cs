using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

// Keeps the Terrain pass in its vanilla shape whenever a mod is watching it.
// The split moves rock strata, caves and block layers into a later stage, so mod handlers
// at Terrain stop running after them, and anything walking OnChunkColumnGen[Terrain] to
// replace one of them finds nothing and quietly does nothing while Stratum still runs it.
internal static class StratumWorldgenCompat
{
	// Where the vanilla generators and the noise and map layers they call live.
	private const string worldgenNamespace = "Vintagestory.ServerMods";

	// Handlers bound for the late sub-stage, per world type, in registration order.
	private static readonly Dictionary<string, List<ChunkColumnGenerationDelegate>> pendingLateHandlers = new Dictionary<string, List<ChunkColumnGenerationDelegate>>();

	public static void RegisterTerrainLate(ServerMain server, ChunkColumnGenerationDelegate handler, string worldType)
	{
		// Vanilla position for now. ApplyTerrainSplitIfSafe moves it later if that turns out safe.
		server.ModEventManager.GetWorldGenHandler(worldType).OnChunkColumnGen[(int)EnumWorldGenPass.Terrain].Add(handler);

		string key = worldType ?? string.Empty;
		if (!pendingLateHandlers.TryGetValue(key, out List<ChunkColumnGenerationDelegate> late))
		{
			late = new List<ChunkColumnGenerationDelegate>();
			pendingLateHandlers[key] = late;
		}

		late.Add(handler);
	}

	// Called once every OnInitWorldGen handler has run, which is the first point where every
	// mod has finished registering.
	public static void ApplyTerrainSplitIfSafe(ServerMain server, WorldGenHandler worldgenHandler)
	{
		if (worldgenHandler == null) return;

		string key = server?.SaveGameData?.WorldType ?? string.Empty;
		if (!pendingLateHandlers.TryGetValue(key, out List<ChunkColumnGenerationDelegate> late) || late.Count == 0)
		{
			return;
		}

		StratumRuntime.Config.EnsurePopulated();
		string blocker = FindSplitBlocker(worldgenHandler, late, StratumRuntime.Config.Worldgen);
		if (blocker != null)
		{
			StratumModCompat.ReportFallback("worldgen", "the Terrain pass split", blocker, "Worldgen.AutoDisableSplitForMods");
			return;
		}

		List<ChunkColumnGenerationDelegate> terrain = worldgenHandler.OnChunkColumnGen[(int)EnumWorldGenPass.Terrain];
		for (int i = 0; i < late.Count; i++)
		{
			terrain.Remove(late[i]);
		}

		worldgenHandler.OnChunkColumnGenTerrainLate.AddRange(late);
		StratumRuntime.LogInfo($"worldgen: Terrain pass split in two, {late.Count} generator(s) moved to TerrainLate");
	}

	// Null when the split is safe, otherwise the reason, phrased for whoever reads the log.
	private static string FindSplitBlocker(WorldGenHandler worldgenHandler, List<ChunkColumnGenerationDelegate> late, StratumWorldgenConfig config)
	{
		if (!config.SplitTerrainPass) return "Worldgen.SplitTerrainPass is disabled in stratum.json";
		if (!config.AutoDisableSplitForMods) return null;

		// One foreign handler is enough. Even if it registered before every late generator and
		// would keep its order, it can still be walking the list looking for them.
		string foreign = StratumModCompat.FindForeignHandler(worldgenHandler.OnChunkColumnGen[(int)EnumWorldGenPass.Terrain]);
		if (foreign != null)
		{
			return "a worldgen handler from assembly '" + foreign + "' is registered at the Terrain pass";
		}

		for (int i = 0; i < late.Count; i++)
		{
			string patches = StratumModCompat.DescribeModPatches(late[i]?.Method);
			if (patches != null)
			{
				return "a generator Stratum would move to the late sub-stage is Harmony patched (" + patches + ")";
			}
		}

		string patched = StratumModCompat.FindPatchedMethodUnder(worldgenNamespace);
		if (patched != null)
		{
			return "a mod Harmony patches " + patched + ", which runs inside the pass Stratum would split";
		}

		return null;
	}
}
