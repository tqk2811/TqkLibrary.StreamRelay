using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TqkLibrary.StreamRelay.Enums;
using TqkLibrary.StreamRelay.Interfaces;
using TqkLibrary.StreamRelay.Models;

namespace TqkLibrary.StreamRelay
{
    /// <summary>
    /// One live stream: a single producer (the demux loop that pulls <see cref="RelayPacket"/>s out of an
    /// <see cref="IStreamDemuxer"/>) fanning out to N subscribers. Packets are retained once in a
    /// <see cref="GopBuffer"/> and shared zero-copy via <see cref="Buffers.RefCountedBuffer"/> ref counting.
    /// </summary>
    /// <remarks>
    /// Threading model:
    /// <list type="bullet">
    /// <item>Exactly one demux loop (started by <see cref="RunDemuxLoopAsync"/>) is the only writer to the
    /// GOP buffer and to every subscriber channel.</item>
    /// <item><see cref="AddSubscriber"/>/<see cref="RemoveSubscriber"/> mutate the subscriber set under a
    /// short lock; the demux loop snapshots the set per publish.</item>
    /// <item>Each subscriber is drained by exactly one external send loop (one per connection), most simply
    /// via <see cref="WriteToSinkAsync"/>; that loop owns releasing each packet after it is sent.</item>
    /// </list>
    /// Refcount ownership: a packet enqueued to a subscriber channel carries one ref owned by that
    /// subscriber. Whoever removes it from the channel (send loop on success, or this class on flush/drop)
    /// releases it.
    /// </remarks>
    public sealed partial class StreamSession : IAsyncDisposable
    {
        readonly RelaySessionOptions _options;
        readonly GopBuffer _gop = new GopBuffer();
        readonly object _subLock = new object();
        readonly Dictionary<Guid, Subscriber> _subscribers = new Dictionary<Guid, Subscriber>();
        readonly CancellationTokenSource _cts = new CancellationTokenSource();

        MediaInit? _init;
        volatile bool _ended;
        long _lastActivityTicks;
        int _disposed;

        public StreamSession(Guid streamId, RelaySessionOptions? options = null)
        {
            StreamId = streamId;
            _options = (options ?? new RelaySessionOptions()).Normalized();
            Touch();
        }

        /// <summary>Stream identifier shared by the ingest device and all viewers.</summary>
        public Guid StreamId { get; }

        /// <summary>The media init discovered when the container was opened; null until the demuxer opens.</summary>
        public MediaInit? Init => _init;

        /// <summary>True once ingest has finished or the session faulted; viewers get a completion signal.</summary>
        public bool IsEnded => _ended;

        /// <summary>Current number of attached viewers.</summary>
        public int SubscriberCount
        {
            get { lock (_subLock) return _subscribers.Count; }
        }

        /// <summary>Token cancelled when the session is disposed; used to stop the demux loop.</summary>
        public CancellationToken SessionToken => _cts.Token;

        /// <summary>UTC ticks of the last ingest/subscriber activity, for idle GC.</summary>
        public long LastActivityTicks => Interlocked.Read(ref _lastActivityTicks);

        void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

        // -----------------------------------------------------------------------------------------------
        // Producer side: the demux loop.
        // -----------------------------------------------------------------------------------------------

        /// <summary>
        /// Drive the demux loop until end of stream, cancellation, or fault. The demuxer must already be
        /// open (its <see cref="IStreamDemuxer.Init"/> populated) and have bytes flowing in on another task;
        /// this method does not own writing bytes into the demuxer — the ingest connection does.
        /// </summary>
        public async Task RunDemuxLoopAsync(IStreamDemuxer demuxer, CancellationToken cancellationToken)
        {
            if (demuxer == null) throw new ArgumentNullException(nameof(demuxer));

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            CancellationToken token = linked.Token;

            try
            {
                if (demuxer.Init != null)
                    SetInit(demuxer.Init);

                while (!token.IsCancellationRequested)
                {
                    RelayPacket? packet = await demuxer.ReadPacketAsync(token).ConfigureAwait(false);
                    if (packet == null)
                        break; // end of stream

                    if (_init == null && demuxer.Init != null)
                        SetInit(demuxer.Init);

                    Publish(packet);
                    packet.Release(); // release the ref returned by the demuxer
                    Touch();
                }
            }
            finally
            {
                MarkEnded();
            }
        }

        /// <summary>Record (or replace) the media init and propagate it to the GOP buffer.</summary>
        public void SetInit(MediaInit init)
        {
            if (init == null) throw new ArgumentNullException(nameof(init));
            _init = init;
            _gop.SetInit(init);
            Touch();
        }

        /// <summary>
        /// Append a packet to the GOP buffer and fan it out to every subscriber. The caller keeps ownership
        /// of the reference it passed in (this method takes its own refs as needed).
        /// </summary>
        public void Publish(RelayPacket packet)
        {
            if (packet == null) throw new ArgumentNullException(nameof(packet));

            // Retain in the GOP buffer first so a resync triggered below sees this keyframe.
            _gop.Append(packet);

            Subscriber[] subs;
            lock (_subLock)
            {
                if (_subscribers.Count == 0)
                    return;
                subs = new Subscriber[_subscribers.Count];
                _subscribers.Values.CopyTo(subs, 0);
            }

            bool isKeyframe = packet.IsKeyframe;

            for (int i = 0; i < subs.Length; i++)
            {
                Subscriber sub = subs[i];
                if (sub.IsFaulted)
                    continue;

                if (!sub.Primed || sub.NeedsResync)
                {
                    // Wait for a clean entry point, then replay init + GOP (which already includes this keyframe).
                    if (!isKeyframe)
                        continue;
                    if (TryPrime(sub))
                    {
                        sub.Primed = true;
                        sub.NeedsResync = false;
                    }
                    continue;
                }

                EnqueueLive(sub, packet);
            }
        }

        /// <summary>Enqueue one live packet to a primed subscriber; on backpressure, schedule a resync.</summary>
        void EnqueueLive(Subscriber sub, RelayPacket packet)
        {
            RelayPacket retained = packet.AddRef();
            if (sub.Channel.Writer.TryWrite(SubscriberMessage.ForPacket(retained)))
                return;

            // Channel full -> slow client. Drop everything queued and resync from the next keyframe.
            retained.Release();
            FlushChannel(sub);

            if (_options.MaxResyncBeforeDrop > 0 && ++sub.ResyncCount > _options.MaxResyncBeforeDrop)
            {
                DropSubscriber(sub);
                return;
            }

            sub.Primed = false;
            sub.NeedsResync = true;
        }

        /// <summary>
        /// Stage init + the current GOP for a subscriber so it (re)enters at a keyframe. Returns false if no
        /// init/GOP exists yet, or if the channel cannot hold the whole GOP (caller retries on next keyframe).
        /// </summary>
        bool TryPrime(Subscriber sub)
        {
            GopSnapshot? snapshot = _gop.Snapshot();
            if (snapshot == null)
                return false;

            // Drop any stale queued items first so the replay is clean.
            FlushChannel(sub);

            if (!sub.Channel.Writer.TryWrite(SubscriberMessage.ForInit(snapshot.Init)))
            {
                snapshot.Release();
                return false;
            }

            for (int i = 0; i < snapshot.Packets.Count; i++)
            {
                // Each snapshot packet already carries a ref for us; hand that ref to the channel.
                if (!sub.Channel.Writer.TryWrite(SubscriberMessage.ForPacket(snapshot.Packets[i])))
                {
                    // Channel too small to hold the whole GOP: release the remaining snapshot refs and bail.
                    for (int j = i; j < snapshot.Packets.Count; j++)
                        snapshot.Packets[j].Release();
                    return false;
                }
            }
            return true;
        }

        /// <summary>Release and discard every message currently queued for a subscriber.</summary>
        static void FlushChannel(Subscriber sub)
        {
            while (sub.Channel.Reader.TryRead(out SubscriberMessage queued))
            {
                if (queued.Kind == SubscriberMessageKind.Packet)
                    queued.Packet!.Release();
            }
        }

        // -----------------------------------------------------------------------------------------------
        // Consumer side: subscribers and the send loop.
        // -----------------------------------------------------------------------------------------------

        /// <summary>
        /// Attach a new viewer. If a GOP already exists the viewer is primed immediately; otherwise it is
        /// primed on the next keyframe by the demux loop. The returned <see cref="Subscriber"/> is drained
        /// by the caller's send loop (typically <see cref="WriteToSinkAsync"/>).
        /// </summary>
        public Subscriber AddSubscriber(Guid subscriberId)
        {
            var channel = Channel.CreateBounded<SubscriberMessage>(new BoundedChannelOptions(_options.SubscriberChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait, // we never block-write; TryWrite + manual resync instead
            });

            var sub = new Subscriber(subscriberId, channel)
            {
                NeedsResync = true, // prime on the next keyframe (or immediately if a GOP already exists)
            };

            lock (_subLock)
            {
                _subscribers[subscriberId] = sub;
            }
            Touch();

            // Prime immediately if a GOP already exists, so a mid-stream joiner doesn't wait a whole GOP.
            if (TryPrime(sub))
            {
                sub.Primed = true;
                sub.NeedsResync = false;
            }

            if (_ended)
                sub.Channel.Writer.TryComplete();

            return sub;
        }

        /// <summary>Detach a viewer, releasing every message still queued for it.</summary>
        public void RemoveSubscriber(Guid subscriberId)
        {
            Subscriber? sub;
            lock (_subLock)
            {
                if (!_subscribers.TryGetValue(subscriberId, out sub))
                    return;
                _subscribers.Remove(subscriberId);
            }
            sub.TryMarkFaulted();
            sub.Channel.Writer.TryComplete();
            FlushChannel(sub);
            Touch();
        }

        /// <summary>Forcefully drop a slow subscriber: mark faulted, complete its channel, flush its queue.</summary>
        void DropSubscriber(Subscriber sub)
        {
            lock (_subLock)
            {
                _subscribers.Remove(sub.Id);
            }
            sub.TryMarkFaulted();
            sub.Channel.Writer.TryComplete();
            FlushChannel(sub);
        }

        /// <summary>
        /// The canonical send loop for one viewer: drains the subscriber channel to a sink, sending init
        /// messages via <see cref="IPacketSink.SendInitAsync"/> and packets via
        /// <see cref="IPacketSink.SendPacketAsync"/>, releasing each packet after it is sent, and signalling
        /// completion at end of stream. Exactly one of these should run per connection.
        /// </summary>
        public async Task WriteToSinkAsync(Subscriber subscriber, IPacketSink sink, CancellationToken cancellationToken)
        {
            if (subscriber == null) throw new ArgumentNullException(nameof(subscriber));
            if (sink == null) throw new ArgumentNullException(nameof(sink));

            ChannelReader<SubscriberMessage> reader = subscriber.Reader;
            try
            {
                while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (reader.TryRead(out SubscriberMessage message))
                    {
                        if (message.Kind == SubscriberMessageKind.Init)
                        {
                            await sink.SendInitAsync(message.Init!, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            RelayPacket packet = message.Packet!;
                            try
                            {
                                await sink.SendPacketAsync(packet, cancellationToken).ConfigureAwait(false);
                            }
                            finally
                            {
                                packet.Release();
                            }
                        }
                    }
                }
                await sink.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Drain anything still queued (e.g. on cancellation) so no buffer leaks.
                while (reader.TryRead(out SubscriberMessage leftover))
                {
                    if (leftover.Kind == SubscriberMessageKind.Packet)
                        leftover.Packet!.Release();
                }
            }
        }

        /// <summary>Mark the stream ended and complete every subscriber channel so send loops finish.</summary>
        public void MarkEnded()
        {
            _ended = true;
            Subscriber[] subs;
            lock (_subLock)
            {
                subs = new Subscriber[_subscribers.Count];
                _subscribers.Values.CopyTo(subs, 0);
            }
            for (int i = 0; i < subs.Length; i++)
                subs[i].Channel.Writer.TryComplete();
            Touch();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _ended = true;
            try { _cts.Cancel(); } catch { /* ignore */ }

            Subscriber[] subs;
            lock (_subLock)
            {
                subs = new Subscriber[_subscribers.Count];
                _subscribers.Values.CopyTo(subs, 0);
                _subscribers.Clear();
            }
            for (int i = 0; i < subs.Length; i++)
            {
                subs[i].TryMarkFaulted();
                subs[i].Channel.Writer.TryComplete();
                FlushChannel(subs[i]);
            }

            _gop.Dispose();
            _cts.Dispose();
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
