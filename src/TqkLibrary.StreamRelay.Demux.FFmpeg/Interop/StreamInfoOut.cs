using System;
using System.Runtime.InteropServices;

namespace TqkLibrary.StreamRelay.Demux.FFmpeg.Interop
{
    /// <summary>Managed mirror of the native <c>StreamInfoOut</c> (must match its sequential layout).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct StreamInfoOut
    {
        public int Index;
        public int CodecType;     // AVMediaType
        public int CodecId;       // AVCodecID
        public int Width;
        public int Height;
        public int SampleRate;
        public int Channels;
        public int TimeBaseNum;
        public int TimeBaseDen;
        public int ExtradataSize;
        public IntPtr Extradata;  // const uint8_t*
        public IntPtr CodecName;  // const char*
    }
}
