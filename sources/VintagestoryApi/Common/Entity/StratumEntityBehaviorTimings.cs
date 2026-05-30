using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Vintagestory.API.Common.Entities
{
    public static class StratumEntityBehaviorTimings
    {
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
            public long ElapsedTicks;
        }

        private static readonly ConcurrentDictionary<string, Bucket> buckets = new ConcurrentDictionary<string, Bucket>(StringComparer.Ordinal);
        private static volatile bool enabled;

        public static bool Enabled => enabled;

        public static void SetEnabled(bool value)
        {
            if (enabled == value)
            {
                return;
            }

            enabled = value;
            if (!value)
            {
                buckets.Clear();
            }
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
            Interlocked.Add(ref bucket.ElapsedTicks, elapsedTicks);
        }

        private static void Record(string prefix, string entityCategory, string behaviorName, long startedTimestamp)
        {
            RecordNamed(prefix + entityCategory + "." + behaviorName, startedTimestamp);
        }

        public static List<Measurement> Drain()
        {
            List<Measurement> measurements = new List<Measurement>(buckets.Count);
            foreach (KeyValuePair<string, Bucket> entry in buckets)
            {
                long elapsedTicks = Interlocked.Read(ref entry.Value.ElapsedTicks);
                if (elapsedTicks > 0L)
                {
                    measurements.Add(new Measurement(entry.Key, elapsedTicks));
                }
            }

            buckets.Clear();
            return measurements;
        }
    }
}