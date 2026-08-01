using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Vintagestory.API.Server
{
    /// <summary>
    /// One copy of T per thread, instead of moving T's fields into ThreadLocal.
    /// A generator is already safe on one thread, so giving each thread its own keeps vanilla's field shape.
    /// That matters because a field turned into a property is invisible to mods that look it up by name.
    /// Anything genuinely shared must be captured in the factory so every copy points at it.
    /// </summary>
    public sealed class StratumWorkerInstances<T> where T : class
    {
        private readonly Func<T> factory;
        private readonly ThreadLocal<T> perThread;
        private readonly ConcurrentBag<T> created = new ConcurrentBag<T>();

        public StratumWorkerInstances(Func<T> factory)
        {
            this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
            perThread = new ThreadLocal<T>(Create, trackAllValues: false);
        }

        /// <summary>
        /// The calling thread's instance, built on first use.
        /// </summary>
        public T Current => perThread.Value;

        /// <summary>
        /// Every instance handed out so far, for shutdown and diagnostics. Not for hot paths.
        /// </summary>
        public IReadOnlyCollection<T> Created => created;

        private T Create()
        {
            T instance = factory();
            created.Add(instance);
            return instance;
        }
    }
}
