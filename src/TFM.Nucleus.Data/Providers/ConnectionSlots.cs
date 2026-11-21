using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data.Providers
{
    /// <summary>
    /// One provider's budget of connections to its database:
    /// <see cref="RelationalProviderOptions.MaxConnectionPoolSize"/> slots, shared by its open root
    /// contexts and its live change subscriptions, each of which holds one connection. It is the
    /// same number the driver's pool is sized to, so a provider that cannot take a slot fails with
    /// <see cref="DataContextErrorKind.PoolExhausted"/> instead of waiting on the driver's pool.
    /// </summary>
    public sealed class ConnectionSlots
    {
        private const int PollMilliseconds = 100;
        private readonly RelationalDataProvider _provider;
        private int _inUse;

        internal ConnectionSlots(RelationalDataProvider provider)
        {
            _provider = provider;
        }

        /// <summary>The most slots held at once: the provider's <see cref="RelationalProviderOptions.MaxConnectionPoolSize"/>.</summary>
        public int Capacity => _provider.Options.MaxConnectionPoolSize;

        /// <summary>The slots held now.</summary>
        public int InUse => Volatile.Read(ref _inUse);

        /// <summary>The slots free now.</summary>
        public int Available => Capacity - InUse;

        /// <summary>Takes a slot if one is free.</summary>
        public bool TryAcquire(out ConnectionSlot? slot)
        {
            var capacity = Capacity;
            while (true)
            {
                var inUse = Volatile.Read(ref _inUse);
                if (inUse >= capacity)
                {
                    slot = null;
                    return false;
                }
                if (Interlocked.CompareExchange(ref _inUse, inUse + 1, inUse) == inUse)
                {
                    slot = new ConnectionSlot(this);
                    return true;
                }
            }
        }

        /// <summary>Takes a slot, or fails at once: the synchronous path does not wait.</summary>
        public ConnectionSlot Acquire() =>
            TryAcquire(out var slot) ? slot! : throw Exhausted("and the synchronous path does not wait");

        /// <summary>
        /// Takes a slot, waiting up to <see cref="RelationalProviderOptions.PoolWaitTimeoutSeconds"/>
        /// for one to come free.
        /// </summary>
        public async ValueTask<ConnectionSlot> AcquireAsync(CancellationToken token)
        {
            if (TryAcquire(out var slot))
                return slot!;
            var waitSeconds = _provider.Options.PoolWaitTimeoutSeconds;
            var limit = TimeSpan.FromSeconds(waitSeconds);
            var clock = Stopwatch.StartNew();
            while (true)
            {
                var left = limit - clock.Elapsed;
                if (left <= TimeSpan.Zero)
                    throw Exhausted($"after waiting {waitSeconds} s (PoolWaitTimeoutSeconds)");
                var pause = TimeSpan.FromMilliseconds(PollMilliseconds);
                await Task.Delay(left < pause ? left : pause, token).ConfigureAwait(false);
                if (TryAcquire(out slot))
                    return slot!;
            }
        }

        internal void Release() => Interlocked.Decrement(ref _inUse);

        private DataContextException Exhausted(string detail) =>
            new($"No connection is free: all {Capacity} (MaxConnectionPoolSize) are held by open contexts and change subscriptions, {detail}. " +
                "Dispose contexts and subscriptions sooner, or raise the limit.", DataContextErrorKind.PoolExhausted);
    }

    /// <summary>One slot of a provider's <see cref="ConnectionSlots"/>; disposing it gives the slot back, once.</summary>
    public sealed class ConnectionSlot : IDisposable
    {
        private ConnectionSlots? _owner;

        internal ConnectionSlot(ConnectionSlots owner)
        {
            _owner = owner;
        }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
