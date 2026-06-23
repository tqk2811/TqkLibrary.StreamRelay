using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TqkLibrary.StreamRelay.Demux.FFmpeg.Interop;
using TqkLibrary.StreamRelay.Enums;
using TqkLibrary.StreamRelay.Models;

namespace TqkLibrary.StreamRelay.Demux.FFmpeg
{
    /// <summary>
    /// Remuxes already-demuxed <see cref="RelayPacket"/>s into fragmented MP4 (movflags
    /// frag_keyframe+empty_moov+default_base_moof) via the native libavformat muxer. <see cref="WriteHeader"/>
    /// returns the init segment (ftyp+moov); <see cref="WritePacket"/> returns each media fragment
    /// (moof+mdat). The output feeds a browser MediaSource. Not thread-safe; drive from one send loop.
    /// </summary>
    public sealed class FragmentedMp4Remuxer : IDisposable
    {
        // AVMediaType values (stable ABI).
        const int AVMEDIA_TYPE_VIDEO = 0;
        const int AVMEDIA_TYPE_AUDIO = 1;
        const int AVMEDIA_TYPE_DATA = 2;
        const int AVMEDIA_TYPE_SUBTITLE = 3;

        IntPtr _handle;
        bool _headerWritten;
        int _disposed;

        public FragmentedMp4Remuxer(MediaInit init)
        {
            if (init == null) throw new ArgumentNullException(nameof(init));
            _handle = NativeWrapper.Mux_Alloc();
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException("Mux_Alloc returned null.");

            foreach (MediaStreamInfo s in init.Streams)
                AddStream(s);
        }

        void AddStream(MediaStreamInfo s)
        {
            GCHandle pin = default;
            try
            {
                IntPtr extraPtr = IntPtr.Zero;
                int extraLen = 0;
                if (s.Extradata is { Length: > 0 })
                {
                    pin = GCHandle.Alloc(s.Extradata, GCHandleType.Pinned);
                    extraPtr = pin.AddrOfPinnedObject();
                    extraLen = s.Extradata.Length;
                }

                var mux = new MuxStreamIn
                {
                    CodecType = MapMediaType(s.Kind),
                    CodecId = s.CodecId,
                    Width = s.Width,
                    Height = s.Height,
                    SampleRate = s.SampleRate,
                    Channels = s.Channels,
                    TimeBaseNum = s.TimeBaseNum,
                    TimeBaseDen = s.TimeBaseDen,
                    ExtradataSize = extraLen,
                    Extradata = extraPtr,
                };

                int err = NativeWrapper.Mux_AddStream(_handle, in mux);
                if (err < 0)
                    throw new InvalidOperationException($"Mux_AddStream failed (averror {err}).");
            }
            finally
            {
                if (pin.IsAllocated)
                    pin.Free();
            }
        }

        /// <summary>Write the fMP4 header and return the init segment (ftyp+moov).</summary>
        public byte[] WriteHeader()
        {
            int err = NativeWrapper.Mux_WriteHeader(_handle, out IntPtr data, out int len);
            if (err < 0)
                throw new InvalidOperationException($"Mux_WriteHeader failed (averror {err}).");
            _headerWritten = true;
            return Copy(data, len);
        }

        /// <summary>Mux one packet and return any produced fragment bytes (may be empty until a fragment closes).</summary>
        public byte[] WritePacket(RelayPacket packet)
        {
            if (!_headerWritten)
                throw new InvalidOperationException("WriteHeader must be called before WritePacket.");

            GCHandle pin = GCHandle.Alloc(GetArray(packet, out int size), GCHandleType.Pinned);
            try
            {
                var mux = new MuxPacketIn
                {
                    StreamIndex = packet.StreamIndex,
                    IsKeyframe = packet.IsKeyframe ? 1 : 0,
                    Pts = packet.Pts,
                    Dts = packet.Dts,
                    Duration = packet.Duration,
                    Size = size,
                    Data = size > 0 ? pin.AddrOfPinnedObject() : IntPtr.Zero,
                };

                int err = NativeWrapper.Mux_WritePacket(_handle, in mux, out IntPtr data, out int len);
                if (err < 0)
                    throw new InvalidOperationException($"Mux_WritePacket failed (averror {err}).");
                return Copy(data, len);
            }
            finally
            {
                pin.Free();
            }
        }

        static byte[] GetArray(RelayPacket packet, out int size)
        {
            size = packet.Payload.Length;
            // Copy the payload into a managed array we can pin for the native call.
            byte[] arr = size > 0 ? new byte[size] : Array.Empty<byte>();
            if (size > 0)
                packet.Payload.Memory.Span.CopyTo(arr);
            return arr;
        }

        static byte[] Copy(IntPtr data, int len)
        {
            if (data == IntPtr.Zero || len <= 0)
                return Array.Empty<byte>();
            byte[] result = new byte[len];
            Marshal.Copy(data, result, 0, len);
            return result;
        }

        static int MapMediaType(MediaCodecKind kind) => kind switch
        {
            MediaCodecKind.Video => AVMEDIA_TYPE_VIDEO,
            MediaCodecKind.Audio => AVMEDIA_TYPE_AUDIO,
            MediaCodecKind.Subtitle => AVMEDIA_TYPE_SUBTITLE,
            MediaCodecKind.Data => AVMEDIA_TYPE_DATA,
            _ => AVMEDIA_TYPE_DATA,
        };

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (_handle != IntPtr.Zero)
                NativeWrapper.Mux_Free(ref _handle);
        }
    }
}
