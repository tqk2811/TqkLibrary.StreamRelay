using System;
using System.Collections.Generic;
using TqkLibrary.StreamRelay.Enums;
using TqkLibrary.StreamRelay.Models;

namespace TqkLibrary.StreamRelay
{
    /// <summary>
    /// Holds the media init plus the packets of the most recent group-of-pictures (GOP). When a new video
    /// keyframe arrives the previous GOP is evicted (its buffers released), so a freshly connecting viewer
    /// always starts from a keyframe and can decode immediately. Thread-safe.
    /// </summary>
    public sealed class GopBuffer : IDisposable
    {
        readonly object _lock = new object();
        readonly List<RelayPacket> _currentGop = new List<RelayPacket>();
        MediaInit? _init;
        int _videoStreamIndex = -1;
        long _epoch;
        bool _disposed;

        /// <summary>Current GOP generation; increments each time a keyframe evicts the previous GOP.</summary>
        public long Epoch
        {
            get { lock (_lock) return _epoch; }
        }

        /// <summary>Set (or replace) the media init and resolve which video stream's keyframes anchor the GOP.</summary>
        public void SetInit(MediaInit init)
        {
            if (init == null) throw new ArgumentNullException(nameof(init));
            lock (_lock)
            {
                _init = init;
                _videoStreamIndex = init.PrimaryVideoStreamIndex ?? FindVideoIndex(init);
            }
        }

        /// <summary>
        /// Append a packet to the current GOP. Takes its own reference; the caller keeps ownership of the
        /// reference it passed in. A video keyframe first evicts the previous GOP.
        /// </summary>
        public void Append(RelayPacket packet)
        {
            if (packet == null) throw new ArgumentNullException(nameof(packet));
            lock (_lock)
            {
                if (_disposed)
                    return;

                bool isVideoKeyframe = packet.IsKeyframe &&
                    (_videoStreamIndex < 0 || packet.StreamIndex == _videoStreamIndex);
                if (isVideoKeyframe)
                {
                    ReleaseAllLocked();
                    _epoch++;
                }

                _currentGop.Add(packet.AddRef());
            }
        }

        /// <summary>
        /// Take a snapshot for a newly connected viewer: the init plus every current-GOP packet, each
        /// AddRef'd for the caller. Returns null until an init is set. The caller must release the snapshot.
        /// </summary>
        public GopSnapshot? Snapshot()
        {
            lock (_lock)
            {
                if (_disposed || _init == null)
                    return null;

                var packets = new RelayPacket[_currentGop.Count];
                for (int i = 0; i < _currentGop.Count; i++)
                    packets[i] = _currentGop[i].AddRef();

                return new GopSnapshot(_init, packets, _epoch);
            }
        }

        static int FindVideoIndex(MediaInit init)
        {
            for (int i = 0; i < init.Streams.Count; i++)
                if (init.Streams[i].Kind == MediaCodecKind.Video)
                    return init.Streams[i].Index;
            return -1;
        }

        void ReleaseAllLocked()
        {
            for (int i = 0; i < _currentGop.Count; i++)
                _currentGop[i].Release();
            _currentGop.Clear();
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                    return;
                _disposed = true;
                ReleaseAllLocked();
            }
        }
    }
}
