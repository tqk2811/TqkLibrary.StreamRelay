using System;
using System.Runtime.InteropServices;

namespace TqkLibrary.StreamRelay.Demux.FFmpeg.Interop
{
    /// <summary>Managed mirror of the native <c>MediaInitOut</c> (must match its sequential layout).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct MediaInitOut
    {
        public IntPtr FormatName;   // const char*
        public int StreamCount;
        public IntPtr Streams;      // const StreamInfoOut*
    }
}
