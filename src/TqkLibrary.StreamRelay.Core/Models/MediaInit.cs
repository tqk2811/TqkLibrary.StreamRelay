using System;
using System.Collections.Generic;

namespace TqkLibrary.StreamRelay.Models
{
    /// <summary>
    /// The "init" handed to every newly connected viewer before any packet: the container format and the
    /// set of streams (with codec extradata). A viewer uses this to set up decoders.
    /// </summary>
    public sealed class MediaInit
    {
        public MediaInit(string? formatName, IReadOnlyList<MediaStreamInfo> streams)
        {
            FormatName = formatName;
            Streams = streams ?? throw new ArgumentNullException(nameof(streams));
        }

        /// <summary>Container/demuxer name (e.g. "mpegts", "matroska"); null if unknown.</summary>
        public string? FormatName { get; }

        public IReadOnlyList<MediaStreamInfo> Streams { get; }

        /// <summary>
        /// Index of the video stream whose keyframes anchor the GOP buffer. Null lets the buffer pick the
        /// first <see cref="Enums.MediaCodecKind.Video"/> stream.
        /// </summary>
        public int? PrimaryVideoStreamIndex { get; init; }
    }
}
