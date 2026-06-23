using System;
using System.Collections.Generic;
using System.IO;
using TqkLibrary.StreamRelay;
using TqkLibrary.StreamRelay.Demux.FFmpeg;
using TqkLibrary.StreamRelay.Demux.FFmpeg.Process;
using Xunit;

namespace TqkLibrary.StreamRelay.Test
{
    /// <summary>Supervisor cap + warm-pool behaviour. Skips when the native worker is not present.</summary>
    public class DemuxWorkerSupervisorTests
    {
        static string? WorkerPath()
        {
            foreach (string dir in new[]
            {
                Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native"),
                Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x86", "native"),
                AppContext.BaseDirectory,
            })
            {
                string exe = Path.Combine(dir, "TqkLibrary.StreamRelay.DemuxWorker.exe");
                if (File.Exists(exe))
                    return exe;
                string nix = Path.Combine(dir, "TqkLibrary.StreamRelay.DemuxWorker");
                if (File.Exists(nix))
                    return nix;
            }
            return null;
        }

        [SkippableFact]
        public void Acquire_BeyondMaxWorkers_ThrowsCapacityExceeded()
        {
            string? worker = WorkerPath();
            Skip.If(worker == null, "demux worker executable not present.");

            using var supervisor = new DemuxWorkerSupervisor(worker!, maxWorkers: 2, warmPoolSize: 0);
            var held = new List<WorkerHandle>();
            try
            {
                held.Add(supervisor.Acquire());
                held.Add(supervisor.Acquire());
                Assert.Equal(2, supervisor.ActiveCount);

                Assert.Throws<DemuxCapacityExceededException>(() => supervisor.Acquire());

                // Releasing one frees a slot.
                held[0].Dispose();
                held.RemoveAt(0);

                // Now acquiring succeeds again.
                WorkerHandle again = supervisor.Acquire();
                held.Add(again);
                Assert.True(supervisor.ActiveCount <= 2);
            }
            finally
            {
                foreach (WorkerHandle h in held)
                    h.Dispose();
            }
        }

        [SkippableFact]
        public void Acquire_Unlimited_DoesNotThrow()
        {
            string? worker = WorkerPath();
            Skip.If(worker == null, "demux worker executable not present.");

            using var supervisor = new DemuxWorkerSupervisor(worker!, maxWorkers: 0, warmPoolSize: 0);
            var held = new List<WorkerHandle>();
            try
            {
                for (int i = 0; i < 3; i++)
                    held.Add(supervisor.Acquire());
                Assert.Equal(3, supervisor.ActiveCount);
            }
            finally
            {
                foreach (WorkerHandle h in held)
                    h.Dispose();
            }
        }
    }
}
