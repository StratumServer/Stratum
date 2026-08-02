using Vintagestory.API.Common;

namespace Vintagestory.API.Server
{
    /// <summary>
    /// Stratum: registration point for the second half of the Terrain worldgen pass.
    /// Stratum schedules Terrain as two internal stages so two threads can share the heaviest pass.
    /// Late handlers keep their registration order and require no neighbour chunks.
    /// Vanilla registrations keep the original pass numbering.
    /// </summary>
    public static class StratumTerrainSplit
    {
        /// <summary>
        /// Assigned by the server at startup; null when no Stratum scheduler is present.
        /// </summary>
        public static System.Action<ChunkColumnGenerationDelegate, string> RegisterLate;

        /// <summary>
        /// Assigned by the server for generators whose clean-stock handler runs on a worker instance.
        /// </summary>
        public static System.Action<ChunkColumnGenerationDelegate, ChunkColumnGenerationDelegate, string> RegisterLateOptimized;

        /// <summary>
        /// Registers a generator for the late Terrain stage.
        /// Outside Stratum, it registers through the vanilla Terrain pass.
        /// </summary>
        public static void Register(ICoreServerAPI api, ChunkColumnGenerationDelegate handler, string forWorldType)
        {
            if (RegisterLate != null)
            {
                RegisterLate(handler, forWorldType);
            }
            else
            {
                api.Event.ChunkColumnGeneration(handler, EnumWorldGenPass.Terrain, forWorldType);
            }
        }

        /// <summary>
        /// Keeps the vanilla handler visible until the server confirms the optimized handler is compatible.
        /// </summary>
        public static void Register(ICoreServerAPI api, ChunkColumnGenerationDelegate vanillaHandler, ChunkColumnGenerationDelegate optimizedHandler, string forWorldType)
        {
            if (RegisterLateOptimized != null)
            {
                RegisterLateOptimized(vanillaHandler, optimizedHandler, forWorldType);
            }
            else
            {
                api.Event.ChunkColumnGeneration(vanillaHandler, EnumWorldGenPass.Terrain, forWorldType);
            }
        }
    }
}
