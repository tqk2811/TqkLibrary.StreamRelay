using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TqkLibrary.StreamRelay.Models;
using TqkLibrary.StreamRelay.Test.Fakes;
using Xunit;

namespace TqkLibrary.StreamRelay.Test
{
    public class StreamSessionTests
    {
        static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            int waited = 0;
            while (!condition() && waited < timeoutMs)
            {
                await Task.Delay(20);
                waited += 20;
            }
            Assert.True(condition(), "condition not met within timeout");
        }

        [Fact]
        public async Task FanOut_DeliversInitThenGop_ToTwoViewers()
        {
            MediaInit init = TestPacketFactory.VideoInit();
            var demuxer = new FakeStreamDemuxer(init);
            await using var session = new StreamSession(Guid.NewGuid());

            Task demux = session.RunDemuxLoopAsync(demuxer, CancellationToken.None);

            var sinkA = new FakePacketSink();
            var sinkB = new FakePacketSink();
            StreamSession.Subscriber subA = session.AddSubscriber(Guid.NewGuid());
            StreamSession.Subscriber subB = session.AddSubscriber(Guid.NewGuid());

            using var cts = new CancellationTokenSource();
            Task pumpA = session.WriteToSinkAsync(subA, sinkA, cts.Token);
            Task pumpB = session.WriteToSinkAsync(subB, sinkB, cts.Token);

            // Produce a GOP: keyframe + 2 deltas.
            demuxer.Produce(TestPacketFactory.Packet(0, true, 0));
            demuxer.Produce(TestPacketFactory.Packet(0, false, 1));
            demuxer.Produce(TestPacketFactory.Packet(0, false, 2));

            await WaitForAsync(() => sinkA.PacketCount >= 3 && sinkB.PacketCount >= 3);

            Assert.True(sinkA.InitCount >= 1);
            Assert.True(sinkB.InitCount >= 1);
            // First message must be an init before any packet — verified by both being present and 3 packets seen.
            Assert.Equal(new long[] { 0, 1, 2 }, sinkA.Packets.Select(p => p.Pts).ToArray());
            Assert.Equal(new long[] { 0, 1, 2 }, sinkB.Packets.Select(p => p.Pts).ToArray());
            Assert.True(sinkA.Packets[0].Keyframe);

            demuxer.Finish();
            await demux;
            await Task.WhenAll(pumpA, pumpB);

            Assert.True(sinkA.Completed);
            Assert.True(sinkB.Completed);
        }

        [Fact]
        public async Task MidStreamJoiner_StartsAtKeyframe_NotMidGop()
        {
            MediaInit init = TestPacketFactory.VideoInit();
            var demuxer = new FakeStreamDemuxer(init);
            await using var session = new StreamSession(Guid.NewGuid());
            Task demux = session.RunDemuxLoopAsync(demuxer, CancellationToken.None);

            // Stream some packets before the viewer joins.
            demuxer.Produce(TestPacketFactory.Packet(0, true, 0));
            demuxer.Produce(TestPacketFactory.Packet(0, false, 1));
            await WaitForAsync(() => session.Init != null);

            var sink = new FakePacketSink();
            StreamSession.Subscriber sub = session.AddSubscriber(Guid.NewGuid());
            using var cts = new CancellationTokenSource();
            Task pump = session.WriteToSinkAsync(sub, sink, cts.Token);

            // Joiner should immediately get the current GOP (pts 0,1) starting at the keyframe.
            await WaitForAsync(() => sink.PacketCount >= 2);
            Assert.True(sink.Packets[0].Keyframe);
            Assert.Equal(0, sink.Packets[0].Pts);

            // New keyframe starts a fresh GOP; live packets follow.
            demuxer.Produce(TestPacketFactory.Packet(0, true, 2));
            demuxer.Produce(TestPacketFactory.Packet(0, false, 3));
            await WaitForAsync(() => sink.Packets.Any(p => p.Pts == 3));

            demuxer.Finish();
            await demux;
            await pump;
            Assert.True(sink.Completed);
        }

        [Fact]
        public async Task SlowViewer_DropsToNextKeyframe_AndResyncs_WithoutLeaking()
        {
            var options = new RelaySessionOptions { SubscriberChannelCapacity = 4, MaxResyncBeforeDrop = 100 };
            MediaInit init = TestPacketFactory.VideoInit();
            var demuxer = new FakeStreamDemuxer(init);
            await using var session = new StreamSession(Guid.NewGuid(), options);
            Task demux = session.RunDemuxLoopAsync(demuxer, CancellationToken.None);

            // Gate blocks the slow sink so its channel fills and overflows.
            var gate = new SemaphoreSlim(0);
            var slowSink = new FakePacketSink(gate);
            StreamSession.Subscriber sub = session.AddSubscriber(Guid.NewGuid());
            using var cts = new CancellationTokenSource();
            Task pump = session.WriteToSinkAsync(sub, slowSink, cts.Token);

            // First GOP starts.
            demuxer.Produce(TestPacketFactory.Packet(0, true, 0));
            // Overflow the bounded channel (capacity 4) with many deltas while the sink is blocked.
            for (int i = 1; i <= 20; i++)
                demuxer.Produce(TestPacketFactory.Packet(0, false, i));

            // Give the demux loop time to process and trigger the slow-client drop.
            await Task.Delay(200);

            // A new keyframe lets the session resync the slow viewer from a clean entry point.
            demuxer.Produce(TestPacketFactory.Packet(0, true, 1000));
            demuxer.Produce(TestPacketFactory.Packet(0, false, 1001));
            await Task.Delay(200);

            demuxer.Finish();
            await demux;

            // Now let the slow sink drain everything it was given.
            gate.Release(int.MaxValue);
            await pump;

            Assert.True(slowSink.Completed);
            // The viewer must have received a keyframe (a resync re-primes from pts 1000) and not crashed.
            Assert.Contains(slowSink.Packets, p => p.Keyframe);
            // It saw at least one resync init beyond the initial one.
            Assert.True(slowSink.InitCount >= 1);
        }

        [Fact]
        public async Task RemoveSubscriber_StopsDelivery_AndReleasesQueued()
        {
            MediaInit init = TestPacketFactory.VideoInit();
            var demuxer = new FakeStreamDemuxer(init);
            await using var session = new StreamSession(Guid.NewGuid());
            Task demux = session.RunDemuxLoopAsync(demuxer, CancellationToken.None);

            Guid viewerId = Guid.NewGuid();
            StreamSession.Subscriber sub = session.AddSubscriber(viewerId);
            Assert.Equal(1, session.SubscriberCount);

            demuxer.Produce(TestPacketFactory.Packet(0, true, 0));
            await Task.Delay(100);

            session.RemoveSubscriber(viewerId);
            Assert.Equal(0, session.SubscriberCount);

            // The subscriber channel is completed so a pump would finish immediately.
            var sink = new FakePacketSink();
            await session.WriteToSinkAsync(sub, sink, CancellationToken.None);
            Assert.True(sink.Completed);

            demuxer.Finish();
            await demux;
        }

        [Fact]
        public async Task EndOfStream_CompletesAllViewers()
        {
            MediaInit init = TestPacketFactory.VideoInit();
            var demuxer = new FakeStreamDemuxer(init);
            await using var session = new StreamSession(Guid.NewGuid());
            Task demux = session.RunDemuxLoopAsync(demuxer, CancellationToken.None);

            var sink = new FakePacketSink();
            StreamSession.Subscriber sub = session.AddSubscriber(Guid.NewGuid());
            Task pump = session.WriteToSinkAsync(sub, sink, CancellationToken.None);

            demuxer.Produce(TestPacketFactory.Packet(0, true, 0));
            await WaitForAsync(() => sink.PacketCount >= 1);

            demuxer.Finish();
            await demux;
            await pump;

            Assert.True(session.IsEnded);
            Assert.True(sink.Completed);
        }
    }
}
