using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using TqkLibrary.StreamRelay.Models;

namespace TqkLibrary.StreamRelay
{
    /// <summary>
    /// The set of live <see cref="StreamSession"/>s keyed by stream id. An ingest device creates (or claims)
    /// a session via <see cref="GetOrCreateForIngest"/>; viewers attach via <see cref="TryGet"/>. A
    /// background sweep disposes sessions that have been ended (or idle with no ingest and no viewers) for
    /// longer than <see cref="RelaySessionOptions.IdleTimeout"/>.
    /// </summary>
    public sealed class StreamRegistry : IAsyncDisposable
    {
        readonly ConcurrentDictionary<Guid, StreamSession> _sessions = new ConcurrentDictionary<Guid, StreamSession>();
        readonly RelaySessionOptions _options;
        readonly object _ingestLock = new object();
        readonly HashSet<Guid> _activeIngests = new HashSet<Guid>();
        int _disposed;

        public StreamRegistry(RelaySessionOptions? options = null)
        {
            _options = (options ?? new RelaySessionOptions()).Normalized();
        }

        /// <summary>Number of live sessions.</summary>
        public int Count => _sessions.Count;

        /// <summary>Snapshot of the current stream ids.</summary>
        public IReadOnlyCollection<Guid> StreamIds => new List<Guid>(_sessions.Keys);

        /// <summary>
        /// Get an existing session or create a fresh one for an ingest device claiming this stream id. Marks
        /// the stream as having an active ingest (which keeps it alive regardless of viewer count).
        /// </summary>
        public StreamSession GetOrCreateForIngest(Guid streamId)
        {
            ThrowIfDisposed();
            StreamSession session = _sessions.GetOrAdd(streamId, id => new StreamSession(id, _options));
            lock (_ingestLock)
                _activeIngests.Add(streamId);
            return session;
        }

        /// <summary>Mark the ingest for a stream as finished (it no longer keeps the session alive).</summary>
        public void ReleaseIngest(Guid streamId)
        {
            lock (_ingestLock)
                _activeIngests.Remove(streamId);
            if (_sessions.TryGetValue(streamId, out StreamSession? session))
                session.MarkEnded();
        }

        /// <summary>Look up the session for a viewer; returns false if no device is streaming this id.</summary>
        public bool TryGet(Guid streamId, out StreamSession session)
        {
            ThrowIfDisposed();
            return _sessions.TryGetValue(streamId, out session!);
        }

        bool HasActiveIngest(Guid streamId)
        {
            lock (_ingestLock)
                return _activeIngests.Contains(streamId);
        }

        /// <summary>
        /// Remove and dispose sessions that are idle: no active ingest, no viewers, and last activity older
        /// than <see cref="RelaySessionOptions.IdleTimeout"/>. Returns the number removed. Call periodically
        /// (e.g. from a hosted timer) or rely on <see cref="StartIdleSweep"/>.
        /// </summary>
        public async Task<int> SweepIdleAsync()
        {
            if (_options.IdleTimeout <= TimeSpan.Zero)
                return 0;

            long cutoff = DateTime.UtcNow.Ticks - _options.IdleTimeout.Ticks;
            int removed = 0;

            foreach (KeyValuePair<Guid, StreamSession> kv in _sessions)
            {
                StreamSession session = kv.Value;
                bool idle = !HasActiveIngest(kv.Key)
                            && session.SubscriberCount == 0
                            && session.LastActivityTicks < cutoff;
                if (!idle)
                    continue;

                if (_sessions.TryRemove(new KeyValuePair<Guid, StreamSession>(kv.Key, session)))
                {
                    lock (_ingestLock)
                        _activeIngests.Remove(kv.Key);
                    await session.DisposeAsync().ConfigureAwait(false);
                    removed++;
                }
            }
            return removed;
        }

        /// <summary>
        /// Start a fire-and-forget loop that calls <see cref="SweepIdleAsync"/> on a fixed period until the
        /// registry is disposed. The period defaults to the idle timeout.
        /// </summary>
        public Task StartIdleSweep(TimeSpan? period = null, CancellationToken cancellationToken = default)
        {
            TimeSpan interval = period ?? (_options.IdleTimeout > TimeSpan.Zero ? _options.IdleTimeout : TimeSpan.FromSeconds(30));
            return Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
                {
                    try
                    {
                        await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                        await SweepIdleAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // Never let a sweep failure kill the loop.
                    }
                }
            }, cancellationToken);
        }

        void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(StreamRegistry));
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            foreach (KeyValuePair<Guid, StreamSession> kv in _sessions)
            {
                if (_sessions.TryRemove(kv.Key, out StreamSession? session))
                    await session.DisposeAsync().ConfigureAwait(false);
            }
            _sessions.Clear();
            lock (_ingestLock)
                _activeIngests.Clear();
        }
    }
}
