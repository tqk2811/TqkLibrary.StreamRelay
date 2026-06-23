using System;
using System.Collections.Generic;
using TqkLibrary.StreamRelay.AspNetCore.Enums;
using TqkLibrary.StreamRelay.Models;

namespace TqkLibrary.StreamRelay.AspNetCore
{
    /// <summary>Configuration for the stream relay, set via <c>AddStreamRelay(o =&gt; ...)</c>.</summary>
    public sealed class StreamRelayOptions
    {
        /// <summary>How the FFmpeg demuxer runs. See <see cref="Enums.DemuxMode"/>.</summary>
        public DemuxMode DemuxMode { get; set; } = DemuxMode.Auto;

        /// <summary>Per-session tunables (backpressure capacity, idle GC timeout, slow-client drop limit).</summary>
        public RelaySessionOptions Session { get; set; } = new RelaySessionOptions();

        /// <summary>
        /// Allowed container formats for ingest (the <c>?format=</c> query value). Anything else is rejected
        /// to keep the demuxer's attack surface small. Empty means "allow the demuxer to probe".
        /// </summary>
        public ISet<string> AllowedFormats { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mpegts", "mp4", "mov", "matroska", "webm", "flv", "h264", "hevc",
        };

        /// <summary>Receive buffer size (bytes) for reading ingest container chunks off the socket.</summary>
        public int IngestReceiveBufferSize { get; set; } = 64 * 1024;

        /// <summary>How long to wait for the demuxer to discover the container header before giving up.</summary>
        public TimeSpan OpenTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Maximum number of concurrent out-of-process demux workers. Ingest beyond this is rejected with
        /// 503. Zero or negative means unlimited. Only applies to <see cref="Enums.DemuxMode.OutOfProcess"/>.
        /// </summary>
        public int MaxWorkers { get; set; } = 0;

        /// <summary>
        /// Number of idle demux workers kept pre-spawned so a new stream starts without paying the process
        /// launch latency. Zero disables the warm pool. Only applies to out-of-process mode.
        /// </summary>
        public int WarmPoolSize { get; set; } = 0;
    }
}
