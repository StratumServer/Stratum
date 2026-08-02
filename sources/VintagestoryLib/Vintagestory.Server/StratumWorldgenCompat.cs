using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

// Keeps the Terrain pass in its vanilla shape whenever a mod may observe it.
// The split moves rock strata, caves and block layers into a later stage.
// Mods could otherwise fail to find or replace the vanilla handlers.
internal static class StratumWorldgenCompat
{
	// Where the vanilla generators and the noise and map layers they call live.
	private const string worldgenNamespace = "Vintagestory.ServerMods";

	private readonly struct PendingLateHandler
	{
		public readonly ChunkColumnGenerationDelegate Vanilla;
		public readonly ChunkColumnGenerationDelegate Optimized;

		public PendingLateHandler(ChunkColumnGenerationDelegate vanilla, ChunkColumnGenerationDelegate optimized)
		{
			Vanilla = vanilla;
			Optimized = optimized;
		}
	}

	// Handler pairs bound for the late sub-stage, per world type, in registration order.
	private static readonly Dictionary<string, List<PendingLateHandler>> pendingLateHandlers = new Dictionary<string, List<PendingLateHandler>>();

	// Original shared generators need vanilla's serial scheduling when the worker swap is not applied.
	public static bool TerrainRequiresSerialFallback { get; private set; }

	public static void RegisterTerrainLate(ServerMain server, ChunkColumnGenerationDelegate handler, string worldType)
	{
		RegisterTerrainLate(server, handler, handler, worldType);
	}

	public static void RegisterTerrainLate(ServerMain server, ChunkColumnGenerationDelegate vanillaHandler, ChunkColumnGenerationDelegate optimizedHandler, string worldType)
	{
		// Keep the vanilla target visible until every mod has finished registering.
		server.ModEventManager.GetWorldGenHandler(worldType).OnChunkColumnGen[(int)EnumWorldGenPass.Terrain].Add(vanillaHandler);

		string key = worldType ?? string.Empty;
		if (!pendingLateHandlers.TryGetValue(key, out List<PendingLateHandler> late))
		{
			late = new List<PendingLateHandler>();
			pendingLateHandlers[key] = late;
		}

		late.Add(new PendingLateHandler(vanillaHandler, optimizedHandler));
	}

	// Called after every mod has finished registering its worldgen handlers.
	public static void ApplyTerrainSplitIfSafe(ServerMain server, WorldGenHandler worldgenHandler)
	{
		TerrainRequiresSerialFallback = false;
		if (worldgenHandler == null) return;

		string key = server?.SaveGameData?.WorldType ?? string.Empty;
		if (!pendingLateHandlers.TryGetValue(key, out List<PendingLateHandler> late) || late.Count == 0)
		{
			return;
		}

		StratumRuntime.Config.EnsurePopulated();
		string blocker = FindSplitBlocker(worldgenHandler, late, StratumRuntime.Config.Worldgen);
		if (blocker != null)
		{
			TerrainRequiresSerialFallback = HasActiveWorkerBackedVanillaHandler(worldgenHandler, late);
			StratumModCompat.ReportFallback("worldgen", "the Terrain pass split", blocker, "Worldgen.AutoDisableSplitForMods");
			return;
		}

		List<ChunkColumnGenerationDelegate> terrain = worldgenHandler.OnChunkColumnGen[(int)EnumWorldGenPass.Terrain];
		int moved = 0;
		for (int terrainIndex = 0; terrainIndex < terrain.Count;)
		{
			int pendingIndex = -1;
			for (int i = 0; i < late.Count; i++)
			{
				if (terrain[terrainIndex] != late[i].Vanilla) continue;

				pendingIndex = i;
				break;
			}

			if (pendingIndex < 0)
			{
				terrainIndex++;
				continue;
			}

			terrain.RemoveAt(terrainIndex);
			worldgenHandler.OnChunkColumnGenTerrainLate.Add(late[pendingIndex].Optimized);
			moved++;
		}

		StratumRuntime.LogInfo($"worldgen: Terrain pass split in two, {moved} generator(s) moved to TerrainLate");
	}

	private static bool HasActiveWorkerBackedVanillaHandler(WorldGenHandler worldgenHandler, List<PendingLateHandler> late)
	{
		List<ChunkColumnGenerationDelegate> terrain = worldgenHandler.OnChunkColumnGen[(int)EnumWorldGenPass.Terrain];
		for (int i = 0; i < late.Count; i++)
		{
			PendingLateHandler pending = late[i];
			if (pending.Vanilla != pending.Optimized && terrain.Contains(pending.Vanilla)) return true;
		}

		return false;
	}

	// Null when the split is safe, otherwise the reason, phrased for whoever reads the log.
	private static string FindSplitBlocker(WorldGenHandler worldgenHandler, List<PendingLateHandler> late, StratumWorldgenConfig config)
	{
		if (!config.SplitTerrainPass) return "Worldgen.SplitTerrainPass is disabled in stratum.json";
		if (!config.AutoDisableSplitForMods) return null;

		// One foreign handler is enough because it may inspect or replace the vanilla handlers.
		string foreign = StratumModCompat.FindForeignHandler(worldgenHandler.OnChunkColumnGen[(int)EnumWorldGenPass.Terrain]);
		if (foreign != null)
		{
			return "a worldgen handler from assembly '" + foreign + "' is registered at the Terrain pass";
		}

		for (int i = 0; i < late.Count; i++)
		{
			string patches = StratumModCompat.DescribeModPatches(late[i].Optimized?.Method);
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
