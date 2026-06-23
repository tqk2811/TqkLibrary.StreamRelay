using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TqkLibrary.StreamRelay.Buffers;
using TqkLibrary.StreamRelay.Demux.FFmpeg.Helpers;
using TqkLibrary.StreamRelay.Demux.FFmpeg.Process;
using TqkLibrary.StreamRelay.Interfaces;
using TqkLibrary.StreamRelay.Models;
using SysProcess = System.Diagnostics.Process;

namespace TqkLibrary.StreamRelay.Demux.FFmpeg
{
    /// <summary>
    /// Out-of-process FFmpeg demuxer: drives a native worker process over stdin/stdout with the
    /// length-prefixed protocol in <c>Worker.cpp</c>. A worker crash (corrupt/hostile stream) takes down only
    /// that process; the host and other streams keep running. The worker is normally supplied by
    /// <see cref="DemuxWorkerSupervisor"/> (cap + warm pool + OS orphan protection); a standalone constructor
    /// spawns its own worker for tests/simple hosts.
    /// </summary>
    public sealed class OutOfProcessFFmpegDemuxer : IStreamDemuxer
    {
        // Host -> worker commands.
        const byte CmdPushBytes = 1;
        const byte CmdSignalEof = 2;
        const byte CmdOpen = 3;
        // Worker -> host frame types.
        const byte TypeInit = 1;
        const byte TypePacket = 2;
        const byte TypeEof = 3;
        const byte TypeError = 4;

        readonly WorkerHandle _worker;
        readonly Stream _toWorker;
        readonly Stream _fromWorker;
        readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        MediaInit? _init;
        int _disposed;
        bool _openSent;

        /// <summary>Drive a worker supplied by the supervisor (preferred path).</summary>
        public OutOfProcessFFmpegDemuxer(WorkerHandle worker)
        {
            _worker = worker ?? throw new ArgumentNullException(nameof(worker));
            _toWorker = worker.Input;
            _fromWorker = worker.Output;
        }

        /// <summary>Spawn a standalone worker (no supervisor); used by tests/simple hosts.</summary>
        public OutOfProcessFFmpegDemuxer(string? formatName, string workerPath, string[]? nativeSearchDirectories = null)
            : this(SpawnStandalone(formatName, workerPath))
        {
        }

        static WorkerHandle SpawnStandalone(string? formatName, string workerPath)
        {
            if (string.IsNullOrEmpty(workerPath) || !File.Exists(workerPath))
                throw new FileNotFoundException("Demux worker executable not found.", workerPath);

            var psi = new ProcessStartInfo
            {
                FileName = workerPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(workerPath) ?? Environment.CurrentDirectory,
            };
            if (!string.IsNullOrEmpty(formatName))
                psi.ArgumentList.Add(formatName!);

            var process = new SysProcess { StartInfo = psi, EnableRaisingEvents = true };
            if (!process.Start())
                throw new InvalidOperationException("Failed to start the demux worker process.");
            return new WorkerHandle(process);
        }

        public MediaInit? Init => _init;

        public async Task OpenAsync(CancellationToken cancellationToken)
        {
            if (!_openSent)
            {
                _openSent = true;
                await SendCommandAsync(CmdOpen, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            }

            // Drive the reader until the Init frame arrives (the worker emits it right after Open succeeds).
            // The same sequential stream is then read by ReadPacketAsync for the packets that follow.
            while (true)
            {
                (byte type, byte[] payload)? frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
                if (frame == null)
                    return; // worker closed/crashed before init
                (byte type, byte[] payload) = frame.Value;
                if (type == TypeInit)
                {
                    _init = WorkerInitSerializer.Deserialize(payload);
                    return;
                }
                if (type == TypeError || type == TypeEof)
                    return; // open failed; Init stays null, ingest ends
                // Ignore any stray frame and keep waiting for Init.
            }
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> containerBytes, CancellationToken cancellationToken)
        {
            if (containerBytes.IsEmpty || Volatile.Read(ref _disposed) != 0)
                return;
            await SendCommandAsync(CmdPushBytes, containerBytes, cancellationToken).ConfigureAwait(false);
        }

        public void CompleteInput()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            try
            {
                SendCommandAsync(CmdSignalEof, ReadOnlyMemory<byte>.Empty, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch { /* worker may already be gone */ }
        }

        public async ValueTask<RelayPacket?> ReadPacketAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                (byte type, byte[] payload)? frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
                if (frame == null)
                    return null; // worker closed / crashed -> end this stream

                (byte type, byte[] payload) = frame.Value;
                switch (type)
                {
                    case TypeInit:
                        _init ??= WorkerInitSerializer.Deserialize(payload); // late init (rare); keep reading
                        continue;
                    case TypePacket:
                        return ParsePacket(payload);
                    case TypeEof:
                    case TypeError:
                        return null;
                    default:
                        continue;
                }
            }
            return null;
        }

        static RelayPacket ParsePacket(byte[] payload)
        {
            // [i32 streamIndex][u8 keyframe][i64 pts][i64 dts][i32 duration][i32 size][data]
            ReadOnlySpan<byte> span = payload;
            int o = 0;
            int streamIndex = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(o, 4)); o += 4;
            bool keyframe = span[o] != 0; o += 1;
            long pts = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(o, 8)); o += 8;
            long dts = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(o, 8)); o += 8;
            int duration = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(o, 4)); o += 4;
            int size = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(o, 4)); o += 4;

            RefCountedBuffer buffer = RefCountedBuffer.Rent(size);
            if (size > 0)
                span.Slice(o, size).CopyTo(buffer.WritableSpan);

            return new RelayPacket(streamIndex, keyframe, pts, dts, duration, buffer);
        }

        async Task SendCommandAsync(byte cmd, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            byte[] header = new byte[5];
            header[0] = cmd;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1, 4), payload.Length);

            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _toWorker.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                if (!payload.IsEmpty)
                    await _toWorker.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await _toWorker.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        async Task<(byte type, byte[] payload)?> ReadFrameAsync(CancellationToken cancellationToken)
        {
            byte[] header = new byte[5];
            if (!await ReadExactAsync(header, cancellationToken).ConfigureAwait(false))
                return null;

            byte type = header[0];
            int len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1, 4));
            byte[] payload = len > 0 ? new byte[len] : Array.Empty<byte>();
            if (len > 0 && !await ReadExactAsync(payload, cancellationToken).ConfigureAwait(false))
                return null;
            return (type, payload);
        }

        async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken cancellationToken)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read;
                try
                {
                    read = await _fromWorker.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    return false;
                }
                if (read == 0)
                    return false; // worker closed stdout
                offset += read;
            }
            return true;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return ValueTask.CompletedTask;

            _worker.Dispose(); // kills the worker (best effort) and notifies the supervisor
            _writeLock.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
