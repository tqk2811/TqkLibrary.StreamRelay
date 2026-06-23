using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using TqkLibrary.StreamRelay.AspNetCore.Enums;
using TqkLibrary.StreamRelay.AspNetCore.Models;
using TqkLibrary.StreamRelay.Enums;
using TqkLibrary.StreamRelay.Models;

namespace TqkLibrary.StreamRelay.AspNetCore
{
    /// <summary>Parses egress wire frames (the inverse of <see cref="WireProtocol"/>) for viewer clients.</summary>
    public static class WireProtocolReader
    {
        /// <summary>The message type of a frame, read from its second byte. Frame must be at least 2 bytes.</summary>
        public static WireMessageType PeekType(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < 2)
                throw new ArgumentException("Frame too short.", nameof(frame));
            return (WireMessageType)frame[1];
        }

        /// <summary>Parse a <see cref="WireMessageType.Init"/> frame into a <see cref="MediaInit"/>.</summary>
        public static MediaInit ReadInit(ReadOnlySpan<byte> frame)
        {
            int o = 0;
            byte version = frame[o++];
            var type = (WireMessageType)frame[o++];
            if (type != WireMessageType.Init)
                throw new InvalidOperationException($"Not an init frame (type={type}).");
            o += 2; // reserved

            ushort formatLen = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(o, 2)); o += 2;
            string? formatName = formatLen == 0 ? null : Encoding.UTF8.GetString(frame.Slice(o, formatLen)); o += formatLen;

            int streamCount = frame[o++];
            var streams = new List<MediaStreamInfo>(streamCount);
            for (int i = 0; i < streamCount; i++)
            {
                int index = frame[o++];
                var kind = (MediaCodecKind)frame[o++];
                int codecId = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(o, 4)); o += 4;

                ushort nameLen = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(o, 2)); o += 2;
                string? codecName = nameLen == 0 ? null : Encoding.UTF8.GetString(frame.Slice(o, nameLen)); o += nameLen;

                int width = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(o, 4)); o += 4;
                int height = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(o, 4)); o += 4;
                int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(o, 4)); o += 4;
                int channels = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(o, 4)); o += 4;
                int tbNum = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(o, 4)); o += 4;
                int tbDen = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(o, 4)); o += 4;

                int extraLen = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(o, 4)); o += 4;
                byte[] extradata = extraLen == 0 ? Array.Empty<byte>() : frame.Slice(o, extraLen).ToArray(); o += extraLen;

                streams.Add(new MediaStreamInfo
                {
                    Index = index,
                    Kind = kind,
                    CodecId = codecId,
                    CodecName = codecName,
                    Width = width,
                    Height = height,
                    SampleRate = sampleRate,
                    Channels = channels,
                    TimeBaseNum = tbNum,
                    TimeBaseDen = tbDen,
                    Extradata = extradata,
                });
            }
            _ = version;
            return new MediaInit(formatName, streams);
        }

        /// <summary>
        /// Parse a <see cref="WireMessageType.Packet"/> frame. The returned payload is a slice of
        /// <paramref name="frame"/>; copy it if it must outlive the frame buffer.
        /// </summary>
        public static WirePacket ReadPacket(ReadOnlyMemory<byte> frame)
        {
            ReadOnlySpan<byte> span = frame.Span;
            var type = (WireMessageType)span[1];
            if (type != WireMessageType.Packet)
                throw new InvalidOperationException($"Not a packet frame (type={type}).");

            int streamIndex = span[2];
            bool keyframe = (span[3] & 0x01) != 0;
            long pts = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(4, 8));
            long dts = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(12, 8));
            int duration = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(20, 4));
            int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(24, 4));
            ReadOnlyMemory<byte> payload = frame.Slice(28, payloadLen);
            return new WirePacket(streamIndex, keyframe, pts, dts, duration, payload);
        }

        /// <summary>Read the control code from a <see cref="WireMessageType.Control"/> frame.</summary>
        public static byte ReadControlCode(ReadOnlySpan<byte> frame) => frame[2];
    }
}
