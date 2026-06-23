using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using TqkLibrary.StreamRelay.AspNetCore.Enums;
using TqkLibrary.StreamRelay.Models;

namespace TqkLibrary.StreamRelay.AspNetCore
{
    /// <summary>
    /// Serialises media init and packets to the egress wire format (plan §5). All multi-byte integers are
    /// little-endian. Every message starts with <c>[u8 version][u8 type]</c>; the rest depends on the type.
    /// </summary>
    /// <remarks>
    /// Packet frame:
    /// <c>[u8 version][u8 Packet][u8 streamIndex][u8 flags(bit0=keyframe)][i64 pts][i64 dts][i32 duration][i32 payloadLen][payload]</c>.
    /// Init frame carries the container name and each stream's codec id, name, dimensions, timebase and extradata.
    /// </remarks>
    public static class WireProtocol
    {
        /// <summary>Wire format version; bump on any breaking layout change.</summary>
        public const byte Version = 1;

        const int PacketHeaderSize = 1 + 1 + 1 + 1 + 8 + 8 + 4 + 4; // 28 bytes before payload

        /// <summary>Control codes carried in a <see cref="WireMessageType.Control"/> frame.</summary>
        public const byte ControlEndOfStream = 1;

        /// <summary>Serialise a packet frame into a pooled byte array; caller must <see cref="ArrayPool{T}.Return"/> it.</summary>
        public static byte[] RentPacketFrame(RelayPacket packet, out int length)
        {
            ReadOnlySpan<byte> payload = packet.Payload.Memory.Span;
            length = PacketHeaderSize + payload.Length;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
            Span<byte> span = buffer.AsSpan(0, length);

            span[0] = Version;
            span[1] = (byte)WireMessageType.Packet;
            span[2] = (byte)(packet.StreamIndex & 0xFF);
            span[3] = (byte)(packet.IsKeyframe ? 0x01 : 0x00);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(4, 8), packet.Pts);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(12, 8), packet.Dts);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(20, 4), packet.Duration);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24, 4), payload.Length);
            payload.CopyTo(span.Slice(PacketHeaderSize));
            return buffer;
        }

        /// <summary>Serialise an init frame into a freshly allocated byte array sized exactly to the content.</summary>
        public static byte[] BuildInitFrame(MediaInit init)
        {
            if (init == null) throw new ArgumentNullException(nameof(init));

            byte[] formatBytes = init.FormatName == null
                ? Array.Empty<byte>()
                : Encoding.UTF8.GetBytes(init.FormatName);

            IReadOnlyList<MediaStreamInfo> streams = init.Streams;

            int size = 4;                 // version, type, reserved, reserved
            size += 2 + formatBytes.Length; // formatNameLen + bytes
            size += 1;                    // streamCount

            var codecNameBytes = new byte[streams.Count][];
            for (int i = 0; i < streams.Count; i++)
            {
                MediaStreamInfo s = streams[i];
                codecNameBytes[i] = s.CodecName == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(s.CodecName);
                size += 1 + 1 + 4;                 // index, kind, codecId
                size += 2 + codecNameBytes[i].Length; // codecNameLen + bytes
                size += 4 * 4;                     // width, height, sampleRate, channels
                size += 4 + 4;                     // timeBaseNum, timeBaseDen
                size += 4 + (s.Extradata?.Length ?? 0); // extradataLen + bytes
            }

            byte[] buffer = new byte[size];
            Span<byte> span = buffer;
            int o = 0;

            span[o++] = Version;
            span[o++] = (byte)WireMessageType.Init;
            span[o++] = 0; // reserved
            span[o++] = 0; // reserved

            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(o, 2), (ushort)formatBytes.Length); o += 2;
            formatBytes.CopyTo(span.Slice(o)); o += formatBytes.Length;

            span[o++] = (byte)streams.Count;

            for (int i = 0; i < streams.Count; i++)
            {
                MediaStreamInfo s = streams[i];
                span[o++] = (byte)(s.Index & 0xFF);
                span[o++] = (byte)s.Kind;
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(o, 4), s.CodecId); o += 4;

                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(o, 2), (ushort)codecNameBytes[i].Length); o += 2;
                codecNameBytes[i].CopyTo(span.Slice(o)); o += codecNameBytes[i].Length;

                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(o, 4), s.Width); o += 4;
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(o, 4), s.Height); o += 4;
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(o, 4), s.SampleRate); o += 4;
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(o, 4), s.Channels); o += 4;
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(o, 4), s.TimeBaseNum); o += 4;
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(o, 4), s.TimeBaseDen); o += 4;

                byte[] extra = s.Extradata ?? Array.Empty<byte>();
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(o, 4), extra.Length); o += 4;
                extra.CopyTo(span.Slice(o)); o += extra.Length;
            }

            return buffer;
        }

        /// <summary>Build a 4-byte control frame.</summary>
        public static byte[] BuildControlFrame(byte controlCode)
        {
            return new byte[] { Version, (byte)WireMessageType.Control, controlCode, 0 };
        }
    }
}
