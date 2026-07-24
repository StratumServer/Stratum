using System;
using System.Threading;

namespace Vintagestory.ServerMods
{
    // Stratum: shared, non-generic toggle for StratumWorldgenThreadGuard<T> below.
    // Kept separate from the generic wrapper because static fields on a generic
    // type are per-closed-type in .NET (StratumWorldgenThreadGuard<LCGRandom> and
    // StratumWorldgenThreadGuard<QueueOfInt> would each get their own copy), which
    // would silently defeat a single on/off switch meant to cover every guarded
    // resource at once.
    public static class StratumWorldgenThreadGuard
    {
        // Off by default. Not wired into stratum.json's config system: StratumConfig
        // and StratumRuntime are internal to VintagestoryLib with no
        // InternalsVisibleTo toward this assembly, so there is no compile-time path
        // from here to that config today. Flip these in code, or set the
        // environment variables below, while actively chasing a worldgen threading
        // bug; wiring a real stratum.json toggle through is a small, separate
        // follow-up (would need either a public bridge property on the
        // VintagestoryLib side, or moving this guard there instead).
        public static bool Enabled = string.Equals(
            Environment.GetEnvironmentVariable("STRATUM_ENFORCE_SAFE_WORLDGEN_THREAD_ACCESS"),
            "true", StringComparison.OrdinalIgnoreCase);

        // When Enabled is also true: throw instead of just logging a warning and
        // continuing. Matches C2ME-fabric's own enforceSafeWorldRandomAccess split
        // between "warn and fall back" and "hard crash" (see CheckedThreadLocalRandom
        // in github.com/RelativityMC/C2ME-fabric), so a violation can be caught live
        // in a debugger instead of only leaving a log line to find later.
        public static bool Strict = string.Equals(
            Environment.GetEnvironmentVariable("STRATUM_ENFORCE_SAFE_WORLDGEN_THREAD_ACCESS_STRICT"),
            "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Canary for worldgen state meant to be [ThreadStatic]-confined (per-column
    /// Random instances, scratch buffers), modeled on C2ME-fabric's
    /// CheckedThreadLocalRandom. A [ThreadStatic] field cannot be read from the
    /// wrong thread by construction, so a correctly-behaving caller should never
    /// trip this; if it does, a reference to the guarded value escaped across a
    /// thread hop (a captured delegate, an async continuation resuming on a
    /// different thread) instead of being re-fetched fresh from the [ThreadStatic]
    /// field on every use, or something bypassed the field via reflection.
    /// </summary>
    /// <example>
    /// Store the guard itself, not the raw resource, in the [ThreadStatic] field:
    /// <code>
    /// [ThreadStatic] private static StratumWorldgenThreadGuard&lt;LCGRandom&gt; stratumRandGuard;
    /// ...
    /// LCGRandom rand = (stratumRandGuard ??= new StratumWorldgenThreadGuard&lt;LCGRandom&gt;(
    ///     "GenPonds.rand", new LCGRandom(worldSeed))).Get();
    /// </code>
    /// </example>
    public sealed class StratumWorldgenThreadGuard<T> where T : class
    {
        private readonly string resourceName;
        private readonly Thread owner;
        private readonly T value;

        public StratumWorldgenThreadGuard(string resourceName, T value)
        {
            this.resourceName = resourceName;
            this.value = value;
            this.owner = Thread.CurrentThread;
        }

        public T Get()
        {
            if (StratumWorldgenThreadGuard.Enabled && !ReferenceEquals(Thread.CurrentThread, owner))
            {
                HandleViolation();
            }
            return value;
        }

        private void HandleViolation()
        {
            string message = $"Stratum: {resourceName} accessed from thread '{Thread.CurrentThread.Name}' " +
                $"but constructed on thread '{owner.Name}'. This resource lives in a [ThreadStatic] field, " +
                "so this almost certainly means a reference to it escaped across a thread hop (a captured " +
                "delegate, an async continuation resuming elsewhere) rather than the field declaration itself " +
                "being unsafe. The stack trace below is the offending access, not the original construction.";

            if (StratumWorldgenThreadGuard.Strict)
            {
                throw new InvalidOperationException(message);
            }

            Console.Error.WriteLine(message);
            Console.Error.WriteLine(Environment.StackTrace);
        }
    }
}
