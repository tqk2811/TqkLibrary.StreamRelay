using System;
using System.Collections.Generic;

namespace TqkLibrary.StreamRelay.Models
{
    /// <summary>
    /// An immutable point-in-time view handed to a newly connected viewer: the media init plus every
    /// retained packet of the current GOP, each already AddRef'd for the viewer. The viewer MUST call
    /// <see cref="Release"/> (or release each packet) after sending them.
    /// </summary>
    public sealed class GopSnapshot
    {
        public GopSnapshot(MediaInit init, IReadOnlyList<RelayPacket> packets, long epoch)
        {
            Init = init ?? throw new ArgumentNullException(nameof(init));
            Packets = packets ?? throw new ArgumentNullException(nameof(packets));
            Epoch = epoch;
        }

        public MediaInit Init { get; }

        /// <summary>Packets from the most recent keyframe up to the snapshot moment.</summary>
        public IReadOnlyList<RelayPacket> Packets { get; }

        /// <summary>GOP generation counter; changes when a new keyframe evicts the previous GOP.</summary>
        public long Epoch { get; }

        /// <summary>Release every packet reference held by this snapshot.</summary>
        public void Release()
        {
            for (int i = 0; i < Packets.Count; i++)
                Packets[i].Release();
        }
    }
}
