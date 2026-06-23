using System;

namespace TqkLibrary.StreamRelay.Models
{
    /// <summary>
    /// Tunables for a single <see cref="StreamSession"/>: per-subscriber backpressure capacity, the
    /// idle timeout used by <see cref="StreamRegistry"/> to garbage-collect dead sessions, and the limit
    /// at which a chronically slow viewer is dropped instead of being resynced again.
    /// </summary>
    public sealed class RelaySessionOptions
    {
        /// <summary>
        /// Bounded capacity of each subscriber's packet channel. When full the viewer is "slow": its queue
        /// is flushed and it is resynced from the latest keyframe. Must be at least 1.
        /// </summary>
        public int SubscriberChannelCapacity { get; init; } = 256;

        /// <summary>
        /// How long a session with no ingest and no viewers may sit idle before
        /// <see cref="StreamRegistry"/> disposes it. Zero or negative disables idle GC.
        /// </summary>
        public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Maximum number of resyncs (keyframe-drops) tolerated for one viewer before it is disconnected as
        /// hopelessly slow. Zero or negative means never disconnect for slowness.
        /// </summary>
        public int MaxResyncBeforeDrop { get; init; } = 8;

        /// <summary>Validate and return a defensive copy with sane minimums.</summary>
        public RelaySessionOptions Normalized()
        {
            return new RelaySessionOptions
            {
                SubscriberChannelCapacity = Math.Max(1, SubscriberChannelCapacity),
                IdleTimeout = IdleTimeout,
                MaxResyncBeforeDrop = MaxResyncBeforeDrop,
            };
        }
    }
}
