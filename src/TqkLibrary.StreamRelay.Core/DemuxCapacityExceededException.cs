using System;

namespace TqkLibrary.StreamRelay
{
    /// <summary>
    /// Thrown by an <see cref="Interfaces.IStreamDemuxerFactory"/> when it cannot create another demuxer
    /// because a capacity limit (e.g. the out-of-process worker cap) is reached. The ingest endpoint maps
    /// this to HTTP 503 Service Unavailable so the device can retry later.
    /// </summary>
    public sealed class DemuxCapacityExceededException : Exception
    {
        public DemuxCapacityExceededException(string message) : base(message) { }
    }
}
