using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Vintagestory.API.Common.Entities
{
    /// <summary>
    /// Per-entity behavior, AI and entity-type timings. Stratum's own tick code switches it with the admin's
    /// /stratum timings switch (SetEnabled, twice a tick) and drains it into the admin's report (Drain).
    /// Any other reader (a monitoring mod) uses the contract below, which never touches the admin's switch or
    /// report: RequestRecording to turn recording on, Snapshot to read cumulative totals.
    /// Only BCL types cross the boundary, so a reader can bind it by reflection without naming Measurement.
    /// </summary>
    public static class StratumEntityBehaviorTimings
    {
        /// <summary>
        /// Version of the reading contract (ContractVersion, RequestRecording, Snapshot, and the key forms below).
        /// Bumped only when one of them changes shape or meaning, never when something is added.
        /// Key forms a reader may parse, where the last part is everything after the prefix, dots included:
        /// entity.behavior.players.{PropertyName()}, entity.behavior.threadsafe.{category}.{ProfilerName or type name},
        /// entity.ai.{category}.task.{task code} and entity.type.{Code.Path}. A new key nested under one of these
        /// forms bumps ContractVersion; a new kind of key gets a new prefix instead. Other keys, such as the AI phase
        /// keys (entity.ai.{category}.taskai.pathTraverser and the like), are not part of the contract.
        /// </summary>
        public const int ContractVersion = 1;

        public readonly struct Measurement
        {
            public readonly string Name;
            public readonly long ElapsedTicks;

            public Measurement(string name, long elapsedTicks)
            {
                Name = name;
                ElapsedTicks = elapsedTicks;
            }
        }

        private sealed class Bucket
        {
            // Cumulative, cleared only when nobody at all records (see Refresh).
            public long TotalTicks;
            public long Calls;

            // The engine's own drain cursor: only Drain and RebaseDrainCursors touch it, under gate.
            public long DrainedTicks;
        }

        private static readonly ConcurrentDictionary<string, Bucket> buckets = new ConcurrentDictionary<string, Bucket>(StringComparer.Ordinal);
        private static readonly object gate = new object();
        private static volatile bool enabled;
        private static volatile bool adminEnabled;
        private static int readers;

        public static bool Enabled => enabled;

        /// <summary>
        /// The engine's twice-per-tick assertion of the admin switch. It only sets the admin's half of the
        /// state: recording stays on while a reader holds a lease, whatever the admin does.
        /// </summary>
        public static void SetEnabled(bool value)
        {
            if (adminEnabled == value)
            {
                return;
            }

            lock (gate)
            {
                if (adminEnabled == value)
                {
                    return;
                }

                if (value)
                {
                    // The admin's report starts now, not from what a reader collected while nobody drained.
                    RebaseDrainCursors();
                }

                adminEnabled = value;
                Refresh();
            }
        }

        /// <summary>
        /// Asks for recording without touching the admin switch or the admin's report. Recording stays on until
        /// every lease is disposed and the admin switch is off. Dispose releases the lease once; further calls do nothing.
        /// </summary>
        public static IDisposable RequestRecording()
        {
            lock (gate)
            {
                readers++;
                Refresh();
            }

            return new Lease();
        }

        public static long GetTimestamp()
        {
            return enabled ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void Record(string entityCategory, string behaviorName, long startedTimestamp)
        {
            Record("entity.behavior.", entityCategory, behaviorName, startedTimestamp);
        }

        public static void RecordThreadSafe(string entityCategory, string behaviorName, long startedTimestamp)
        {
            Record("entity.behavior.threadsafe.", entityCategory, behaviorName, startedTimestamp);
        }

        public static void RecordNamed(string name, long startedTimestamp)
        {
            if (!enabled || startedTimestamp == 0L || string.IsNullOrEmpty(name))
            {
                return;
            }

            long elapsedTicks = Stopwatch.GetTimestamp() - startedTimestamp;
            if (elapsedTicks <= 0L)
            {
                return;
            }

            Bucket bucket = buckets.GetOrAdd(name, _ => new Bucket());
            Interlocked.Add(ref bucket.TotalTicks, elapsedTicks);
            Interlocked.Increment(ref bucket.Calls);
        }

        private static void Record(string prefix, string entityCategory, string behaviorName, long startedTimestamp)
        {
            RecordNamed(prefix + entityCategory + "." + behaviorName, startedTimestamp);
        }

        /// <summary>
        /// Engine only: what was recorded since the previous Drain. Nothing is cleared, so a Record can never
        /// be lost between the read and the clear, and a reader's Snapshot is never affected.
        /// </summary>
        public static List<Measurement> Drain()
        {
            lock (gate)
            {
                List<Measurement> measurements = new List<Measurement>(buckets.Count);
                foreach (KeyValuePair<string, Bucket> entry in buckets)
                {
                    long total = Interlocked.Read(ref entry.Value.TotalTicks);
                    long delta = total - entry.Value.DrainedTicks;
                    if (delta > 0L)
                    {
                        entry.Value.DrainedTicks = total;
                        measurements.Add(new Measurement(entry.Key, delta));
                    }
                }

                return measurements;
            }
        }

        /// <summary>
        /// Replaces the contents of into with the cumulative totals (Stopwatch ticks and call count) of every key
        /// recorded so far. Callable from any thread, any number of readers, never destructive.
        /// Totals restart from zero once recording has stopped, so take a new baseline after any gap without a lease.
        /// Ticks and Calls of one key are read separately and can differ by a record landing in between.
        /// </summary>
        public static void Snapshot(List<(string Key, long Ticks, long Calls)> into)
        {
            into.Clear();
            foreach (KeyValuePair<string, Bucket> entry in buckets)
            {
                into.Add((entry.Key, Interlocked.Read(ref entry.Value.TotalTicks), Interlocked.Read(ref entry.Value.Calls)));
            }
        }

        // Under gate.
        private static void Refresh()
        {
            bool on = adminEnabled || readers > 0;
            if (enabled == on)
            {
                return;
            }

            enabled = on;
            if (!on)
            {
                // Only when nobody at all records: memory back, as before.
                buckets.Clear();
            }
        }

        // Under gate.
        private static void RebaseDrainCursors()
        {
            foreach (KeyValuePair<string, Bucket> entry in buckets)
            {
                entry.Value.DrainedTicks = Interlocked.Read(ref entry.Value.TotalTicks);
            }
        }

        private sealed class Lease : IDisposable
        {
            private int released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref released, 1) != 0)
                {
                    return;
                }

                lock (gate)
                {
                    readers--;
                    Refresh();
                }
            }
        }
    }
}
