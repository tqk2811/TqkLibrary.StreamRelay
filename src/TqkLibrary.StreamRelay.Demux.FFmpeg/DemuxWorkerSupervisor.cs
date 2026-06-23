using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using TqkLibrary.StreamRelay;
using TqkLibrary.StreamRelay.Demux.FFmpeg.Process;
using SysProcess = System.Diagnostics.Process;

namespace TqkLibrary.StreamRelay.Demux.FFmpeg
{
    /// <summary>
    /// Spawns and tracks out-of-process demux workers with a <see cref="MaxWorkers"/> cap and an optional
    /// warm pool of idle workers (so a new stream pays no process-launch latency). On Windows all workers
    /// are assigned to a <see cref="WindowsJobObject"/> with <c>KILL_ON_JOB_CLOSE</c>; on Linux each worker
    /// sets <c>PR_SET_PDEATHSIG</c> itself and runs in its own process group — either way a host crash leaks
    /// no orphans.
    /// </summary>
    public sealed class DemuxWorkerSupervisor : IDisposable
    {
        readonly string _workerPath;
        readonly int _maxWorkers;
        readonly int _warmPoolSize;
        readonly ConcurrentQueue<WorkerHandle> _warm = new ConcurrentQueue<WorkerHandle>();
        readonly object _jobLock = new object();
        readonly WindowsJobObject? _job;
        int _activeCount;     // workers handed out (in use)
        int _warmCount;       // workers idle in the pool
        int _disposed;

        public DemuxWorkerSupervisor(string workerPath, int maxWorkers, int warmPoolSize)
        {
            if (string.IsNullOrEmpty(workerPath) || !File.Exists(workerPath))
                throw new FileNotFoundException("Demux worker executable not found.", workerPath);
            _workerPath = workerPath;
            _maxWorkers = maxWorkers;
            _warmPoolSize = Math.Max(0, warmPoolSize);

            if (OperatingSystem.IsWindows())
            {
                try { _job = new WindowsJobObject(); }
                catch { _job = null; /* best effort */ }
            }

            RefillWarmPool();
        }

        /// <summary>Configured worker cap (0 = unlimited).</summary>
        public int MaxWorkers => _maxWorkers;

        /// <summary>Workers currently handed out to streams.</summary>
        public int ActiveCount => Volatile.Read(ref _activeCount);

        /// <summary>Idle workers waiting in the warm pool.</summary>
        public int WarmCount => Volatile.Read(ref _warmCount);

        /// <summary>
        /// Acquire a worker for a new stream: take one from the warm pool or spawn a fresh one, honouring the
        /// worker cap. Throws <see cref="DemuxCapacityExceededException"/> when at capacity.
        /// </summary>
        public WorkerHandle Acquire()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(DemuxWorkerSupervisor));

            // Reserve a slot against the cap (active + warm count must stay within MaxWorkers).
            int total = Interlocked.Increment(ref _activeCount);
            if (_maxWorkers > 0 && total > _maxWorkers)
            {
                Interlocked.Decrement(ref _activeCount);
                throw new DemuxCapacityExceededException(
                    $"Demux worker cap reached ({_maxWorkers}); rejecting ingest.");
            }

            WorkerHandle handle = TakeWarmOrSpawn();
            handle.OnDisposed = () =>
            {
                Interlocked.Decrement(ref _activeCount);
                RefillWarmPool();
            };

            RefillWarmPool();
            return handle;
        }

        WorkerHandle TakeWarmOrSpawn()
        {
            while (_warm.TryDequeue(out WorkerHandle? warm))
            {
                Interlocked.Decrement(ref _warmCount);
                if (!warm.HasExited)
                    return warm;
                warm.Dispose(); // dead warm worker; try the next
            }
            return Spawn();
        }

        WorkerHandle Spawn()
        {
            var psi = new ProcessStartInfo
            {
                FileName = _workerPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_workerPath) ?? Environment.CurrentDirectory,
            };

            var process = new SysProcess { StartInfo = psi, EnableRaisingEvents = true };
            if (!process.Start())
                throw new InvalidOperationException("Failed to start the demux worker process.");

            if (_job != null && OperatingSystem.IsWindows())
            {
                lock (_jobLock)
                {
                    try { _job.Assign(process.Handle); } catch { /* best effort */ }
                }
            }

            return new WorkerHandle(process);
        }

        void RefillWarmPool()
        {
            if (_warmPoolSize <= 0 || Volatile.Read(ref _disposed) != 0)
                return;

            // Top up asynchronously to avoid blocking the acquiring caller.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                while (Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _warmCount) < _warmPoolSize)
                {
                    // Respect the cap across warm + active workers.
                    if (_maxWorkers > 0 && Volatile.Read(ref _activeCount) + Volatile.Read(ref _warmCount) >= _maxWorkers)
                        break;
                    try
                    {
                        WorkerHandle warm = Spawn();
                        _warm.Enqueue(warm);
                        Interlocked.Increment(ref _warmCount);
                    }
                    catch
                    {
                        break;
                    }
                }
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            while (_warm.TryDequeue(out WorkerHandle? warm))
            {
                Interlocked.Decrement(ref _warmCount);
                warm.OnDisposed = null;
                warm.Dispose();
            }

            // Closing the job object handle kills any still-assigned workers (Windows).
            if (OperatingSystem.IsWindows())
                _job?.Dispose();
        }
    }
}
