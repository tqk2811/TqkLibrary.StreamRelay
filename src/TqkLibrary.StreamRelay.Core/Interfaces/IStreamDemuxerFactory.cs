namespace TqkLibrary.StreamRelay.Interfaces
{
    /// <summary>
    /// Creates an <see cref="IStreamDemuxer"/> per ingest stream. The concrete factory decides between an
    /// in-process or out-of-process FFmpeg implementation (see DemuxMode), keeping that choice out of the
    /// relay core.
    /// </summary>
    public interface IStreamDemuxerFactory
    {
        /// <param name="formatName">
        /// Optional container format hint (e.g. "mpegts", "matroska"); null to let FFmpeg probe.
        /// </param>
        IStreamDemuxer Create(string? formatName);
    }
}
