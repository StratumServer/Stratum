using System;

namespace Vintagestory.API.Server
{
    /// <summary>
    /// Lets a shipped generator ask whether a mod has Harmony patched one of its method bodies.
    /// Mod assemblies reference VintagestoryAPI and not VintagestoryLib, and VSEssentials has no Harmony reference of its own, so the server assigns this at startup.
    /// </summary>
    public static class StratumGeneratorCompat
    {
        /// <summary>
        /// Assigned by the server at startup. Null when no Stratum server is present.
        /// </summary>
        public static Func<Type, string, bool> IsMethodBodyPatchedByMod;

        /// <summary>
        /// True when a mod has patched the named method or its compiler-generated code.
        /// False outside Stratum, where the original method always runs.
        /// </summary>
        public static bool IsBodyPatchedByMod(Type declaringType, string methodName)
        {
            return IsMethodBodyPatchedByMod != null && IsMethodBodyPatchedByMod(declaringType, methodName);
        }
    }
}
