using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TqkLibrary.StreamRelay.Demux.FFmpeg.Interop;
using TqkLibrary.StreamRelay.Enums;
using TqkLibrary.StreamRelay.Models;

namespace TqkLibrary.StreamRelay.Demux.FFmpeg.Helpers
{
    /// <summary>Marshals the native <see cref="MediaInitOut"/> into a managed <see cref="MediaInit"/>.</summary>
    internal static class NativeInitMarshaler
    {
        // AVMediaType values (stable in the FFmpeg ABI).
        const int AVMEDIA_TYPE_VIDEO = 0;
        const int AVMEDIA_TYPE_AUDIO = 1;
        const int AVMEDIA_TYPE_DATA = 2;
        const int AVMEDIA_TYPE_SUBTITLE = 3;

        public static MediaInit ToMediaInit(in MediaInitOut native)
        {
            string? formatName = native.FormatName == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(native.FormatName);

            var streams = new List<MediaStreamInfo>(native.StreamCount);
            int structSize = Marshal.SizeOf<StreamInfoOut>();
            int? primaryVideo = null;

            for (int i = 0; i < native.StreamCount; i++)
            {
                IntPtr ptr = native.Streams + i * structSize;
                StreamInfoOut s = Marshal.PtrToStructure<StreamInfoOut>(ptr);

                byte[] extradata = Array.Empty<byte>();
                if (s.Extradata != IntPtr.Zero && s.ExtradataSize > 0)
                {
                    extradata = new byte[s.ExtradataSize];
                    Marshal.Copy(s.Extradata, extradata, 0, s.ExtradataSize);
                }

                string? codecName = s.CodecName == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(s.CodecName);
                MediaCodecKind kind = MapKind(s.CodecType);
                if (kind == MediaCodecKind.Video && primaryVideo == null)
                    primaryVideo = s.Index;

                streams.Add(new MediaStreamInfo
                {
                    Index = s.Index,
                    Kind = kind,
                    CodecId = s.CodecId,
                    CodecName = codecName,
                    Width = s.Width,
                    Height = s.Height,
                    SampleRate = s.SampleRate,
                    Channels = s.Channels,
                    TimeBaseNum = s.TimeBaseNum,
                    TimeBaseDen = s.TimeBaseDen,
                    Extradata = extradata,
                });
            }

            return new MediaInit(formatName, streams) { PrimaryVideoStreamIndex = primaryVideo };
        }

        static MediaCodecKind MapKind(int avMediaType) => avMediaType switch
        {
            AVMEDIA_TYPE_VIDEO => MediaCodecKind.Video,
            AVMEDIA_TYPE_AUDIO => MediaCodecKind.Audio,
            AVMEDIA_TYPE_SUBTITLE => MediaCodecKind.Subtitle,
            AVMEDIA_TYPE_DATA => MediaCodecKind.Data,
            _ => MediaCodecKind.Unknown,
        };
    }
}
