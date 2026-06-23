using System;
using System.Runtime.InteropServices;

namespace TqkLibrary.StreamRelay.Demux.FFmpeg.Interop
{
    /// <summary>Managed mirror of the native <c>MuxPacketIn</c> (must match its sequential layout).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct MuxPacketIn
    {
        public int StreamIndex;
        public int IsKeyframe;
        public long Pts;
        public long Dts;
        public int Duration;
        public int Size;
        public IntPtr Data;       // const uint8_t*
    }
}
