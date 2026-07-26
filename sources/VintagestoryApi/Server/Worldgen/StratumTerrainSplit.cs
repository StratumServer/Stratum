using Vintagestory.API.Common;

namespace Vintagestory.API.Server
{
    /// <summary>
    /// Stratum: registration point for the second half of the Terrain worldgen
    /// pass. Stratum schedules Terrain as two internal sub-stages so two threads
    /// can share the heaviest pass. Handlers registered here run after the 3d
    /// terrain generators, in registration order, with the same guarantees the
    /// vanilla Terrain pass gives (no neighbour chunks required). Registering
    /// through the vanilla ChunkColumnGeneration API is unchanged and keeps
    /// vanilla pass numbering.
    /// </summary>
    public static class StratumTerrainSplit
    {
        /// <summary>
        /// Assigned by the server at startup; null when no Stratum scheduler is present.
        /// </summary>
        public static System.Action<ChunkColumnGenerationDelegate, string> RegisterLate;

        /// <summary>
        /// Registers a generator for the late Terrain sub-stage. Falls back to the
        /// vanilla Terrain pass when no Stratum scheduler is present, so callers
        /// behave like vanilla registrations outside Stratum.
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
    }
}
