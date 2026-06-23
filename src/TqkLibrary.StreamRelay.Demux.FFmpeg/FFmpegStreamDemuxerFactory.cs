using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TqkLibrary.StreamRelay.AspNetCore;
using TqkLibrary.StreamRelay.AspNetCore.Enums;
using TqkLibrary.StreamRelay.Demux.FFmpeg.Process;
using TqkLibrary.StreamRelay.Interfaces;

namespace TqkLibrary.StreamRelay.Demux.FFmpeg
{
    /// <summary>
    /// Creates the FFmpeg <see cref="IStreamDemuxer"/> for each stream, choosing in-process vs out-of-process
    /// per the configured <see cref="DemuxMode"/>. <see cref="DemuxMode.Auto"/> picks in-process on Windows
    /// (SEH guard protects the host) and out-of-process on Linux (a worker crash cannot take the host down).
    /// For out-of-process it routes through a shared <see cref="DemuxWorkerSupervisor"/> (worker cap + warm
    /// pool + OS orphan protection).
    /// </summary>
    public sealed class FFmpegStreamDemuxerFactory : IStreamDemuxerFactory, IDisposable
    {
        readonly DemuxMode _mode;
        readonly StreamRelayOptions _options;
        readonly ILogger<FFmpegStreamDemuxerFactory>? _logger;
        readonly object _supervisorLock = new object();
        DemuxWorkerSupervisor? _supervisor;
        bool _supervisorInitFailed;

        public FFmpegStreamDemuxerFactory(IOptions<StreamRelayOptions> options, ILogger<FFmpegStreamDemuxerFactory>? logger = null)
        {
            _options = options.Value;
            _mode = _options.DemuxMode;
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
                DemuxWorkerSupervisor? supervisor = GetSupervisor();
                if (supervisor != null)
                {
                    WorkerHandle worker = supervisor.Acquire(); // throws DemuxCapacityExceededException at the cap
                    return new OutOfProcessFFmpegDemuxer(worker);
                }
                _logger?.LogWarning("Out-of-process demux requested but worker executable not found; falling back to in-process.");
            }
            return new InProcessFFmpegDemuxer(formatName);
        }

        DemuxWorkerSupervisor? GetSupervisor()
        {
            if (_supervisor != null)
                return _supervisor;
            if (_supervisorInitFailed)
                return null;

            lock (_supervisorLock)
            {
                if (_supervisor != null)
                    return _supervisor;
                if (_supervisorInitFailed)
                    return null;

                string? worker = NativeWrapper.FindWorkerExecutable();
                if (worker == null)
                {
                    _supervisorInitFailed = true;
                    return null;
                }
                _supervisor = new DemuxWorkerSupervisor(worker, _options.MaxWorkers, _options.WarmPoolSize);
                return _supervisor;
            }
        }

        public void Dispose() => _supervisor?.Dispose();
    }
}
