using System;
using System.Runtime.InteropServices;

namespace TqkLibrary.StreamRelay.Demux.FFmpeg.Interop
{
    /// <summary>Managed mirror of the native <c>PacketOut</c> (must match its sequential layout).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct PacketOut
    {
        public int StreamIndex;
        public int IsKeyframe;     // 0/1
        public long Pts;
        public long Dts;
        public int Duration;
        public int Size;
        public IntPtr Data;        // const uint8_t* (valid until next read)
    }
}
