using System;
using System.Threading;
using System.Threading.Channels;
using TqkLibrary.StreamRelay.Models;

namespace TqkLibrary.StreamRelay
{
    public sealed partial class StreamSession
    {
        /// <summary>
        /// Per-viewer state inside a <see cref="StreamSession"/>: a bounded channel of ordered
        /// <see cref="SubscriberMessage"/> items (init + packets) plus the bookkeeping the fan-out path uses
        /// to detect a slow client and trigger a keyframe resync.
        /// </summary>
        /// <remarks>
        /// Ownership contract: every <see cref="RelayPacket"/> placed in the channel carries one reference
        /// owned by this subscriber. Whoever takes it out (the send loop on success, or the session on
        /// flush/drop) MUST <c>Release()</c> it. <see cref="MediaInit"/> carries no buffer so it needs no
        /// refcount.
        /// </remarks>
        public sealed class Subscriber
        {
            internal Subscriber(Guid id, Channel<SubscriberMessage> channel)
            {
                Id = id;
                Channel = channel;
            }

            /// <summary>Unique id of this viewer (used to add/remove/count).</summary>
            public Guid Id { get; }

            /// <summary>Bounded ordered queue of messages pending send.</summary>
            internal Channel<SubscriberMessage> Channel { get; }

            /// <summary>True once the viewer has been primed (init + current GOP) and is following live.</summary>
            internal bool Primed;

            /// <summary>Number of times this viewer fell behind and had to resync from a keyframe.</summary>
            internal int ResyncCount;

            /// <summary>Set when the session decides this viewer must be dropped (too slow / session ended).</summary>
            int _faulted;

            /// <summary>Marks the viewer as needing a fresh init+GOP replay before the next live packet.</summary>
            internal volatile bool NeedsResync;

            /// <summary>Reader the send loop pulls messages from.</summary>
            public ChannelReader<SubscriberMessage> Reader => Channel.Reader;

            internal bool TryMarkFaulted() => Interlocked.Exchange(ref _faulted, 1) == 0;

            internal bool IsFaulted => Volatile.Read(ref _faulted) != 0;
        }
    }
}
