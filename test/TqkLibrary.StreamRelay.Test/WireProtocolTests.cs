using System;
using System.Buffers;
using System.Linq;
using TqkLibrary.StreamRelay.AspNetCore;
using TqkLibrary.StreamRelay.AspNetCore.Enums;
using TqkLibrary.StreamRelay.AspNetCore.Models;
using TqkLibrary.StreamRelay.Enums;
using TqkLibrary.StreamRelay.Models;
using TqkLibrary.StreamRelay.Test.Fakes;
using Xunit;

namespace TqkLibrary.StreamRelay.Test
{
    public class WireProtocolTests
    {
        [Fact]
        public void InitFrame_RoundTrips()
        {
            var video = new MediaStreamInfo
            {
                Index = 0,
                Kind = MediaCodecKind.Video,
                CodecId = 27,
                CodecName = "h264",
                Width = 1280,
                Height = 720,
                TimeBaseNum = 1,
                TimeBaseDen = 90000,
                Extradata = new byte[] { 1, 2, 3, 4, 5 },
            };
            var audio = new MediaStreamInfo
            {
                Index = 1,
                Kind = MediaCodecKind.Audio,
                CodecId = 86018,
                CodecName = "aac",
                SampleRate = 48000,
                Channels = 2,
                TimeBaseNum = 1,
                TimeBaseDen = 48000,
                Extradata = Array.Empty<byte>(),
            };
            var init = new MediaInit("mpegts", new[] { video, audio });

            byte[] frame = WireProtocol.BuildInitFrame(init);
            Assert.Equal(WireMessageType.Init, WireProtocolReader.PeekType(frame));

            MediaInit parsed = WireProtocolReader.ReadInit(frame);
            Assert.Equal("mpegts", parsed.FormatName);
            Assert.Equal(2, parsed.Streams.Count);

            Assert.Equal(0, parsed.Streams[0].Index);
            Assert.Equal(MediaCodecKind.Video, parsed.Streams[0].Kind);
            Assert.Equal(27, parsed.Streams[0].CodecId);
            Assert.Equal("h264", parsed.Streams[0].CodecName);
            Assert.Equal(1280, parsed.Streams[0].Width);
            Assert.Equal(720, parsed.Streams[0].Height);
            Assert.Equal(90000, parsed.Streams[0].TimeBaseDen);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, parsed.Streams[0].Extradata);

            Assert.Equal(MediaCodecKind.Audio, parsed.Streams[1].Kind);
            Assert.Equal(48000, parsed.Streams[1].SampleRate);
            Assert.Equal(2, parsed.Streams[1].Channels);
        }

        [Fact]
        public void PacketFrame_RoundTrips()
        {
            RelayPacket packet = TestPacketFactory.Packet(3, keyframe: true, pts: 123456789, length: 32, fill: 0x5A);

            byte[] frame = WireProtocol.RentPacketFrame(packet, out int length);
            try
            {
                Assert.Equal(WireMessageType.Packet, WireProtocolReader.PeekType(frame.AsSpan(0, length)));
                WirePacket parsed = WireProtocolReader.ReadPacket(new ReadOnlyMemory<byte>(frame, 0, length));

                Assert.Equal(3, parsed.StreamIndex);
                Assert.True(parsed.IsKeyframe);
                Assert.Equal(123456789, parsed.Pts);
                Assert.Equal(32, parsed.Payload.Length);
                Assert.True(parsed.Payload.Span.ToArray().All(b => b == 0x5A));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frame);
            }

            packet.Release();
        }

        [Fact]
        public void ControlFrame_CarriesEndOfStream()
        {
            byte[] frame = WireProtocol.BuildControlFrame(WireProtocol.ControlEndOfStream);
            Assert.Equal(WireMessageType.Control, WireProtocolReader.PeekType(frame));
            Assert.Equal(WireProtocol.ControlEndOfStream, WireProtocolReader.ReadControlCode(frame));
        }
    }
}
