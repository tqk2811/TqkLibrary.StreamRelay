namespace TqkLibrary.StreamRelay.AspNetCore.Enums
{
    /// <summary>
    /// Selects how the FFmpeg demuxer runs. <see cref="Auto"/> picks in-process on Windows (SEH guard
    /// protects the host) and out-of-process on Linux (a crashed worker cannot take the host down).
    /// </summary>
    public enum DemuxMode
    {
        /// <summary>Windows -&gt; <see cref="InProcess"/>, Linux -&gt; <see cref="OutOfProcess"/>.</summary>
        Auto = 0,

        /// <summary>P/Invoke the native demuxer in the host process.</summary>
        InProcess = 1,

        /// <summary>Spawn a worker process per stream; the host talks to it over stdin/stdout.</summary>
        OutOfProcess = 2,
    }
}
