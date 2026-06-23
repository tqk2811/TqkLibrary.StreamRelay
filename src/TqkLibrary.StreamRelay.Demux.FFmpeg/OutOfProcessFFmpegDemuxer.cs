using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TqkLibrary.StreamRelay.Buffers;
using TqkLibrary.StreamRelay.Demux.FFmpeg.Helpers;
using TqkLibrary.StreamRelay.Interfaces;
using TqkLibrary.StreamRelay.Models;

namespace TqkLibrary.StreamRelay.Demux.FFmpeg
{
    /// <summary>
    /// Out-of-process FFmpeg demuxer: spawns the native worker executable and bridges it over stdin/stdout
    /// with the length-prefixed protocol in <c>Worker.cpp</c>. A worker crash (corrupt/hostile stream) takes
    /// down only that process; the host and other streams keep running. M5 layers a supervisor/warm-pool and
    /// OS-level orphan protection (Job Object on Windows, PR_SET_PDEATHSIG on Linux) on top of this.
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

        readonly Process _process;
        readonly Stream _toWorker;
        readonly Stream _fromWorker;
        readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        readonly TaskCompletionSource<MediaInit?> _initTcs =
            new TaskCompletionSource<MediaInit?>(TaskCreationOptions.RunContinuationsAsynchronously);

        MediaInit? _init;
        int _disposed;
        bool _openSent;

        public OutOfProcessFFmpegDemuxer(string? formatName, string workerPath, string[]? nativeSearchDirectories = null)
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

            _process = new Process { StartInfo = psi };
            if (!_process.Start())
                throw new InvalidOperationException("Failed to start the demux worker process.");

            _toWorker = _process.StandardInput.BaseStream;
            _fromWorker = _process.StandardOutput.BaseStream;
        }

        public MediaInit? Init => _init;

        public async Task OpenAsync(CancellationToken cancellationToken)
        {
            if (!_openSent)
            {
                _openSent = true;
                await SendCommandAsync(CmdOpen, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            }

            using (cancellationToken.Register(() => _initTcs.TrySetCanceled()))
            {
                _init = await _initTcs.Task.ConfigureAwait(false);
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
                        _init ??= WorkerInitSerializer.Deserialize(payload);
                        _initTcs.TrySetResult(_init);
                        continue; // keep reading until a packet/eof
                    case TypePacket:
                        return ParsePacket(payload);
                    case TypeEof:
                        _initTcs.TrySetResult(_init);
                        return null;
                    case TypeError:
                        _initTcs.TrySetResult(_init);
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

            _initTcs.TrySetResult(_init);
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            try { _process.Dispose(); } catch { }
            _writeLock.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
