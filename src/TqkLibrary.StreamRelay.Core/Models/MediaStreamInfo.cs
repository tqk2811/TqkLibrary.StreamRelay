using System;
using TqkLibrary.StreamRelay.Enums;

namespace TqkLibrary.StreamRelay.Models
{
    /// <summary>
    /// Describes a single elementary stream discovered when the container header is parsed. This is the
    /// information a viewer needs to initialise a decoder before any packet arrives.
    /// </summary>
    public sealed class MediaStreamInfo
    {
        /// <summary>Stream index inside the container (matches <see cref="RelayPacket.StreamIndex"/>).</summary>
        public int Index { get; init; }

        public MediaCodecKind Kind { get; init; }

        /// <summary>FFmpeg AVCodecID value, kept as a plain int so Core stays FFmpeg-free.</summary>
        public int CodecId { get; init; }

        /// <summary>Codec short name (e.g. "h264", "hevc", "aac"); informational.</summary>
        public string? CodecName { get; init; }

        /// <summary>Codec extradata (SPS/PPS for H.264/H.265, AudioSpecificConfig for AAC). May be empty.</summary>
        public byte[] Extradata { get; init; } = Array.Empty<byte>();

        /// <summary>Time base numerator of this stream's timestamps.</summary>
        public int TimeBaseNum { get; init; }

        /// <summary>Time base denominator of this stream's timestamps.</summary>
        public int TimeBaseDen { get; init; }

        // Video
        public int Width { get; init; }
        public int Height { get; init; }

        // Audio
        public int SampleRate { get; init; }
        public int Channels { get; init; }
    }
}
