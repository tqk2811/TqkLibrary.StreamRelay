using System;
using System.IO;
using SysProcess = System.Diagnostics.Process;

namespace TqkLibrary.StreamRelay.Demux.FFmpeg.Process
{
    /// <summary>
    /// A spawned demux worker process plus its stdin/stdout streams. Owned by the supervisor until handed to
    /// an <see cref="OutOfProcessFFmpegDemuxer"/>, which drives the I/O and disposes the handle when done.
    /// </summary>
    public sealed class WorkerHandle : IDisposable
    {
        int _disposed;

        internal WorkerHandle(SysProcess process)
        {
            Process = process;
            Input = process.StandardInput.BaseStream;
            Output = process.StandardOutput.BaseStream;
        }

        public SysProcess Process { get; }
        public Stream Input { get; }
        public Stream Output { get; }

        /// <summary>True if the worker process has exited.</summary>
        public bool HasExited
        {
            get { try { return Process.HasExited; } catch { return true; } }
        }

        /// <summary>Optional callback the supervisor sets to decrement its active-worker count exactly once.</summary>
        internal Action? OnDisposed { get; set; }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); } catch { }
            try { Process.Dispose(); } catch { }
            OnDisposed?.Invoke();
        }
    }
}
