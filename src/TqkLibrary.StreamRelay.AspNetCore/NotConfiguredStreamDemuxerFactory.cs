using System;
using TqkLibrary.StreamRelay.Interfaces;

namespace TqkLibrary.StreamRelay.AspNetCore
{
    /// <summary>
    /// The default <see cref="IStreamDemuxerFactory"/> registered when no real demuxer (e.g. the FFmpeg one
    /// from <c>TqkLibrary.StreamRelay.Demux.FFmpeg</c>) has been added. It fails fast on first use so a
    /// misconfigured host gets a clear error instead of a silent no-op.
    /// </summary>
    public sealed class NotConfiguredStreamDemuxerFactory : IStreamDemuxerFactory
    {
        public IStreamDemuxer Create(string? formatName)
        {
            throw new InvalidOperationException(
                "No IStreamDemuxerFactory is configured. Register a demuxer (e.g. call AddFFmpegDemuxer() from " +
                "TqkLibrary.StreamRelay.Demux.FFmpeg) before mapping relay ingest endpoints.");
        }
    }
}
