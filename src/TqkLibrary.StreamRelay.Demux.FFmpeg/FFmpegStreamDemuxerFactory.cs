using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TqkLibrary.StreamRelay.AspNetCore;
using TqkLibrary.StreamRelay.AspNetCore.Enums;
using TqkLibrary.StreamRelay.Interfaces;

namespace TqkLibrary.StreamRelay.Demux.FFmpeg
{
    /// <summary>
    /// Creates the FFmpeg <see cref="IStreamDemuxer"/> for each stream, choosing in-process vs out-of-process
    /// per the configured <see cref="DemuxMode"/>. <see cref="DemuxMode.Auto"/> picks in-process on Windows
    /// (SEH guard protects the host) and out-of-process on Linux (a worker crash cannot take the host down).
    /// </summary>
    public sealed class FFmpegStreamDemuxerFactory : IStreamDemuxerFactory
    {
        readonly DemuxMode _mode;
        readonly ILogger<FFmpegStreamDemuxerFactory>? _logger;

        public FFmpegStreamDemuxerFactory(IOptions<StreamRelayOptions> options, ILogger<FFmpegStreamDemuxerFactory>? logger = null)
        {
            _mode = options.Value.DemuxMode;
            _logger = logger;
        }

        /// <summary>The effective mode after resolving <see cref="DemuxMode.Auto"/> for the current OS.</summary>
        public DemuxMode EffectiveMode => Resolve(_mode);

        static DemuxMode Resolve(DemuxMode mode)
        {
            if (mode != DemuxMode.Auto)
                return mode;
            return OperatingSystem.IsWindows() ? DemuxMode.InProcess : DemuxMode.OutOfProcess;
        }

        public IStreamDemuxer Create(string? formatName)
        {
            DemuxMode mode = Resolve(_mode);
            if (mode == DemuxMode.OutOfProcess)
            {
                string? worker = NativeWrapper.FindWorkerExecutable();
                if (worker != null)
                    return new OutOfProcessFFmpegDemuxer(formatName, worker, NativeWrapper.NativeDirectories);

                _logger?.LogWarning("Out-of-process demux requested but worker executable not found; falling back to in-process.");
            }
            return new InProcessFFmpegDemuxer(formatName);
        }
    }
}
