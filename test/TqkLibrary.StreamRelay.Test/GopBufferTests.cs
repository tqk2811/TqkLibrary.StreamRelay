using System;
using TqkLibrary.StreamRelay.Models;
using TqkLibrary.StreamRelay.Test.Fakes;
using Xunit;

namespace TqkLibrary.StreamRelay.Test
{
    public class GopBufferTests
    {
        [Fact]
        public void Snapshot_ReturnsNull_BeforeInit()
        {
            using var gop = new GopBuffer();
            Assert.Null(gop.Snapshot());
        }

        [Fact]
        public void Append_NewKeyframe_EvictsPreviousGop_AndReleasesItsBuffers()
        {
            using var gop = new GopBuffer();
            gop.SetInit(TestPacketFactory.VideoInit());

            // First GOP: keyframe + 2 deltas.
            RelayPacket k1 = TestPacketFactory.Packet(0, keyframe: true, pts: 0);
            RelayPacket d1 = TestPacketFactory.Packet(0, keyframe: false, pts: 1);
            RelayPacket d2 = TestPacketFactory.Packet(0, keyframe: false, pts: 2);
            gop.Append(k1);
            gop.Append(d1);
            gop.Append(d2);

            long epochAfterFirst = gop.Epoch;

            // New keyframe evicts the first GOP.
            RelayPacket k2 = TestPacketFactory.Packet(0, keyframe: true, pts: 3);
            gop.Append(k2);

            Assert.Equal(epochAfterFirst + 1, gop.Epoch);

            // The producer still holds its original refs; release them now.
            k1.Release(); d1.Release(); d2.Release(); k2.Release();

            // First-GOP buffers should be fully released (GOP buffer dropped its retained refs on eviction).
            Assert.True(RefCountAssert.IsFullyReleased(k1.Payload));
            Assert.True(RefCountAssert.IsFullyReleased(d1.Payload));
            Assert.True(RefCountAssert.IsFullyReleased(d2.Payload));

            // k2 is still retained by the current GOP, so not yet released.
            Assert.False(RefCountAssert.IsFullyReleased(k2.Payload));
        }

        [Fact]
        public void Snapshot_ContainsCurrentGop_FromLatestKeyframe()
        {
            using var gop = new GopBuffer();
            gop.SetInit(TestPacketFactory.VideoInit());

            foreach (var p in new[]
            {
                TestPacketFactory.Packet(0, true, 0),
                TestPacketFactory.Packet(0, false, 1),
                TestPacketFactory.Packet(0, true, 2),  // starts a new GOP
                TestPacketFactory.Packet(0, false, 3),
            })
            {
                gop.Append(p);
                p.Release(); // producer releases its own ref after append
            }

            GopSnapshot? snap = gop.Snapshot();
            Assert.NotNull(snap);
            Assert.Equal(2, snap!.Packets.Count);          // only the latest GOP (pts 2,3)
            Assert.Equal(2, snap.Packets[0].Pts);
            Assert.True(snap.Packets[0].IsKeyframe);
            Assert.Equal(3, snap.Packets[1].Pts);

            snap.Release();
        }

        [Fact]
        public void Snapshot_AddRefsPackets_SoBuffersSurviveUntilSnapshotReleased()
        {
            using var gop = new GopBuffer();
            gop.SetInit(TestPacketFactory.VideoInit());

            RelayPacket k = TestPacketFactory.Packet(0, true, 0);
            gop.Append(k);
            k.Release(); // producer drops its ref; only the GOP holds one now

            GopSnapshot? snap = gop.Snapshot();
            Assert.NotNull(snap);

            // Buffer alive: held by both GOP and the snapshot.
            Assert.False(RefCountAssert.IsFullyReleased(k.Payload));

            snap!.Release();          // snapshot drops its ref
            Assert.False(RefCountAssert.IsFullyReleased(k.Payload)); // GOP still holds one

            gop.Dispose();            // GOP drops its ref
            Assert.True(RefCountAssert.IsFullyReleased(k.Payload));
        }

        [Fact]
        public void Dispose_ReleasesAllRetainedBuffers()
        {
            var gop = new GopBuffer();
            gop.SetInit(TestPacketFactory.VideoInit());

            RelayPacket k = TestPacketFactory.Packet(0, true, 0);
            RelayPacket d = TestPacketFactory.Packet(0, false, 1);
            gop.Append(k);
            gop.Append(d);
            k.Release(); d.Release();

            Assert.False(RefCountAssert.IsFullyReleased(k.Payload));

            gop.Dispose();

            Assert.True(RefCountAssert.IsFullyReleased(k.Payload));
            Assert.True(RefCountAssert.IsFullyReleased(d.Payload));
        }

        [Fact]
        public void Append_KeyframeOnNonPrimaryStream_DoesNotEvict()
        {
            using var gop = new GopBuffer();
            // Primary video is stream 0; stream 1 is audio.
            var video = new MediaStreamInfo { Index = 0, Kind = Enums.MediaCodecKind.Video };
            var audio = new MediaStreamInfo { Index = 1, Kind = Enums.MediaCodecKind.Audio };
            gop.SetInit(new MediaInit("mpegts", new[] { video, audio }) { PrimaryVideoStreamIndex = 0 });

            RelayPacket vk = TestPacketFactory.Packet(0, true, 0);
            RelayPacket ak = TestPacketFactory.Packet(1, true, 0); // audio "keyframe" must NOT evict video GOP
            gop.Append(vk);
            long epoch = gop.Epoch;
            gop.Append(ak);

            Assert.Equal(epoch, gop.Epoch); // unchanged: audio keyframe did not start a new GOP

            GopSnapshot? snap = gop.Snapshot();
            Assert.Equal(2, snap!.Packets.Count);
            snap.Release();

            vk.Release(); ak.Release();
        }
    }
}
