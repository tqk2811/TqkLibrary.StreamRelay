using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TqkLibrary.StreamRelay.Demux.FFmpeg;
using TqkLibrary.StreamRelay.Enums;
using TqkLibrary.StreamRelay.Models;
using Xunit;

namespace TqkLibrary.StreamRelay.Test
{
    /// <summary>
    /// End-to-end exercise of the native in-process demuxer against a real mpegts sample. Skips cleanly when
    /// the native libraries / sample asset are not present (e.g. CI without the native build).
    /// </summary>
    public class InProcessFFmpegDemuxerTests
    {
        static string? SamplePath()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "sample.ts");
            return File.Exists(path) ? path : null;
        }

        static bool NativePresent()
        {
            // The resolver looks beside the assembly and under runtimes/<rid>/native.
            foreach (string dir in new[]
            {
                AppContext.BaseDirectory,
                Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native"),
                Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x86", "native"),
            })
            {
                if (File.Exists(Path.Combine(dir, "TqkLibrary.StreamRelay.Demux.FFmpeg.Native.dll")))
                    return true;
            }
            return false;
        }

        [SkippableFact]
        public async Task Demuxes_RealMpegTs_ProducesKeyframeAndPackets()
        {
            string? sample = SamplePath();
            Skip.If(sample == null, "sample.ts asset not present.");
            Skip.IfNot(OperatingSystem.IsWindows() && NativePresent(), "native demux library not present.");

            byte[] bytes = await File.ReadAllBytesAsync(sample!);

            await using var demuxer = new InProcessFFmpegDemuxer("mpegts");

            // Feed all bytes on a background task (Open blocks until the header is probed).
            var feed = Task.Run(async () =>
            {
                const int chunk = 4096;
                for (int o = 0; o < bytes.Length; o += chunk)
                {
                    int len = Math.Min(chunk, bytes.Length - o);
                    await demuxer.WriteAsync(new ReadOnlyMemory<byte>(bytes, o, len), CancellationToken.None);
                }
                demuxer.CompleteInput();
            });

            using var openCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await demuxer.OpenAsync(openCts.Token);

            Assert.NotNull(demuxer.Init);
            MediaInit init = demuxer.Init!;
            Assert.Contains(init.Streams, s => s.Kind == MediaCodecKind.Video);

            int packetCount = 0;
            int keyframeCount = 0;
            int videoPackets = 0;
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                RelayPacket? packet = await demuxer.ReadPacketAsync(readCts.Token);
                if (packet == null)
                    break;
                packetCount++;
                if (packet.IsKeyframe)
                    keyframeCount++;
                if (packet.StreamIndex == (init.PrimaryVideoStreamIndex ?? 0))
                    videoPackets++;
                Assert.True(packet.Payload.Length > 0);
                packet.Release();
            }

            await feed;

            Assert.True(packetCount > 0, "expected at least one packet");
            Assert.True(videoPackets > 0, "expected video packets");
            Assert.True(keyframeCount > 0, "expected at least one keyframe");
        }
    }
}
