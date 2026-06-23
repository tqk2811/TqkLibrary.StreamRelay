using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TqkLibrary.StreamRelay.Buffers;
using TqkLibrary.StreamRelay.Demux.FFmpeg.Helpers;
using TqkLibrary.StreamRelay.Demux.FFmpeg.Interop;
using TqkLibrary.StreamRelay.Interfaces;
using TqkLibrary.StreamRelay.Models;

namespace TqkLibrary.StreamRelay.Demux.FFmpeg
{
    /// <summary>
    /// In-process FFmpeg demuxer: P/Invokes the native demux core. Ingest bytes are pushed into a native
    /// FIFO; the demux loop pulls packets out. On Windows the native <c>av_read_frame</c> runs under an SEH
    /// guard, so a corrupt stream that would crash FFmpeg becomes an error code that fails only this stream.
    /// </summary>
    public sealed class InProcessFFmpegDemuxer : IStreamDemuxer
    {
        readonly object _nativeLock = new object();
        IntPtr _handle;
        MediaInit? _init;
        int _disposed;

        public InProcessFFmpegDemuxer(string? formatName)
        {
            byte[]? formatUtf8 = string.IsNullOrEmpty(formatName)
                ? null
                : Encoding.ASCII.GetBytes(formatName + "\0");
            _handle = NativeWrapper.Demux_Alloc(formatUtf8);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException("Demux_Alloc returned null (native library failed to load or allocate).");
        }

        public MediaInit? Init => _init;

        public Task OpenAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                int err = NativeWrapper.Demux_Open(_handle);
                if (err < 0)
                    throw new InvalidOperationException($"Demux_Open failed (averror {err}).");

                if (NativeWrapper.Demux_GetInit(_handle, out MediaInitOut native) == 0)
                    _init = NativeInitMarshaler.ToMediaInit(native);
            }, cancellationToken);
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> containerBytes, CancellationToken cancellationToken)
        {
            if (containerBytes.IsEmpty || Volatile.Read(ref _disposed) != 0)
                return ValueTask.CompletedTask;

            // Demux_PushBytes copies into the native FIFO, so the pin only needs to last the call.
            using (MemoryHandle pin = containerBytes.Pin())
            {
                unsafe
                {
                    NativeWrapper.Demux_PushBytes(_handle, (IntPtr)pin.Pointer, containerBytes.Length);
                }
            }
            return ValueTask.CompletedTask;
        }

        public void CompleteInput()
        {
            if (Volatile.Read(ref _disposed) == 0)
                NativeWrapper.Demux_SignalEof(_handle);
        }

        public ValueTask<RelayPacket?> ReadPacketAsync(CancellationToken cancellationToken)
        {
            return new ValueTask<RelayPacket?>(Task.Run(() =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int r;
                    PacketOut packet;
                    lock (_nativeLock)
                    {
                        if (Volatile.Read(ref _disposed) != 0)
                            return (RelayPacket?)null;
                        r = NativeWrapper.Demux_ReadPacket(_handle, out packet);
                    }

                    if (r == 0)
                        return null; // end of stream
                    if (r < 0)
                        return null; // error -> end this stream (host survives)

                    // Copy the native payload into a pooled, ref-counted buffer immediately;
                    // the native data pointer is only valid until the next read.
                    int size = packet.Size < 0 ? 0 : packet.Size;
                    RefCountedBuffer buffer = RefCountedBuffer.Rent(size);
                    if (size > 0 && packet.Data != IntPtr.Zero)
                    {
                        unsafe
                        {
                            Span<byte> dst = buffer.WritableSpan;
                            fixed (byte* pDst = dst)
                            {
                                Buffer.MemoryCopy((void*)packet.Data, pDst, dst.Length, size);
                            }
                        }
                    }

                    return new RelayPacket(
                        packet.StreamIndex,
                        packet.IsKeyframe != 0,
                        packet.Pts,
                        packet.Dts,
                        packet.Duration,
                        buffer);
                }
                return null;
            }, cancellationToken));
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return ValueTask.CompletedTask;

            lock (_nativeLock)
            {
                if (_handle != IntPtr.Zero)
                {
                    NativeWrapper.Demux_SignalEof(_handle);
                    NativeWrapper.Demux_Free(ref _handle);
                }
            }
            return ValueTask.CompletedTask;
        }
    }
}
