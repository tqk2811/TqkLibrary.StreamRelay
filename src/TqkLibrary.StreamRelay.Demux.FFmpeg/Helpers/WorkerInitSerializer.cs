using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using TqkLibrary.StreamRelay.Enums;
using TqkLibrary.StreamRelay.Models;

namespace TqkLibrary.StreamRelay.Demux.FFmpeg.Helpers
{
    /// <summary>
    /// Deserialises the worker's init payload. Layout matches <c>Worker.cpp::SerializeInit</c>:
    /// <c>[u16 formatLen][format][u8 streamCount]( [u8 index][u8 kind][i32 codecId][u16 nameLen][name]
    /// [i32 w][i32 h][i32 sampleRate][i32 channels][i32 tbNum][i32 tbDen][i32 extraLen][extra] )*</c>.
    /// Kind is already mapped to <see cref="MediaCodecKind"/> by the worker.
    /// </summary>
    internal static class WorkerInitSerializer
    {
        public static MediaInit Deserialize(ReadOnlySpan<byte> body)
        {
            int o = 0;
            ushort formatLen = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(o, 2)); o += 2;
            string? formatName = formatLen == 0 ? null : Encoding.UTF8.GetString(body.Slice(o, formatLen)); o += formatLen;

            int streamCount = body[o++];
            var streams = new List<MediaStreamInfo>(streamCount);
            int? primaryVideo = null;

            for (int i = 0; i < streamCount; i++)
            {
                int index = body[o++];
                var kind = (MediaCodecKind)body[o++];
                int codecId = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(o, 4)); o += 4;

                ushort nameLen = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(o, 2)); o += 2;
                string? codecName = nameLen == 0 ? null : Encoding.UTF8.GetString(body.Slice(o, nameLen)); o += nameLen;

                int width = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(o, 4)); o += 4;
                int height = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(o, 4)); o += 4;
                int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(o, 4)); o += 4;
                int channels = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(o, 4)); o += 4;
                int tbNum = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(o, 4)); o += 4;
                int tbDen = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(o, 4)); o += 4;

                int extraLen = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(o, 4)); o += 4;
                byte[] extradata = extraLen == 0 ? Array.Empty<byte>() : body.Slice(o, extraLen).ToArray(); o += extraLen;

                if (kind == MediaCodecKind.Video && primaryVideo == null)
                    primaryVideo = index;

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

            return new MediaInit(formatName, streams) { PrimaryVideoStreamIndex = primaryVideo };
        }
    }
}
