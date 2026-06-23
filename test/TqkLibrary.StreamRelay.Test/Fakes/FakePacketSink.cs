using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TqkLibrary.StreamRelay.Interfaces;
using TqkLibrary.StreamRelay.Models;

namespace TqkLibrary.StreamRelay.Test.Fakes
{
    /// <summary>Records everything a session sends to a viewer; optionally delays each send to simulate a slow client.</summary>
    internal sealed class FakePacketSink : IPacketSink
    {
        readonly object _lock = new object();
        readonly List<MediaInit> _inits = new List<MediaInit>();
        readonly List<(long Pts, bool Keyframe, int Length)> _packets = new List<(long, bool, int)>();
        readonly SemaphoreSlim? _gate;

        public FakePacketSink(SemaphoreSlim? gate = null)
        {
            _gate = gate;
        }

        public bool Completed { get; private set; }

        public int InitCount { get { lock (_lock) return _inits.Count; } }

        public int PacketCount { get { lock (_lock) return _packets.Count; } }

        public IReadOnlyList<(long Pts, bool Keyframe, int Length)> Packets
        {
            get { lock (_lock) return new List<(long, bool, int)>(_packets); }
        }

        public IReadOnlyList<MediaInit> Inits
        {
            get { lock (_lock) return new List<MediaInit>(_inits); }
        }

        public ValueTask SendInitAsync(MediaInit init, CancellationToken cancellationToken)
        {
            lock (_lock) _inits.Add(init);
            return ValueTask.CompletedTask;
        }

        public async ValueTask SendPacketAsync(RelayPacket packet, CancellationToken cancellationToken)
        {
            if (_gate != null)
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            // Read the payload while we still hold the ref the session lent us, to catch use-after-free.
            int length = packet.Payload.Memory.Length;
            lock (_lock)
                _packets.Add((packet.Pts, packet.IsKeyframe, length));
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            lock (_lock) Completed = true;
            return ValueTask.CompletedTask;
        }
    }
}
