using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TqkLibrary.StreamRelay.Demux.FFmpeg;
using TqkLibrary.StreamRelay.Models;
using Xunit;

namespace TqkLibrary.StreamRelay.Test
{
    /// <summary>
    /// Demuxes the mpegts sample and remuxes it to fragmented MP4, asserting the output begins with a valid
    /// init segment (ftyp + moov) and contains media fragments (moof). Skips when native libs are absent.
    /// </summary>
    public class FragmentedMp4RemuxerTests
    {
        static string? SamplePath()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "sample.ts");
            return File.Exists(path) ? path : null;
        }

        static bool NativePresent()
        {
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

        static bool ContainsBox(byte[] data, string fourcc)
        {
            byte[] needle = Encoding.ASCII.GetBytes(fourcc);
            for (int i = 0; i + needle.Length <= data.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                    if (data[i + j] != needle[j]) { match = false; break; }
                if (match) return true;
            }
            return false;
        }

        [SkippableFact]
        public async Task Remux_MpegTs_To_FragmentedMp4_ProducesInitAndFragments()
        {
            string? sample = SamplePath();
            Skip.If(sample == null, "sample.ts asset not present.");
            Skip.IfNot(OperatingSystem.IsWindows() && NativePresent(), "native library not present.");

            byte[] bytes = await File.ReadAllBytesAsync(sample!);

            await using var demuxer = new InProcessFFmpegDemuxer("mpegts");
            var feed = Task.Run(async () =>
            {
                const int chunk = 4096;
                for (int o = 0; o < bytes.Length; o += chunk)
                    await demuxer.WriteAsync(new ReadOnlyMemory<byte>(bytes, o, Math.Min(chunk, bytes.Length - o)), CancellationToken.None);
                demuxer.CompleteInput();
            });

            using var openCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await demuxer.OpenAsync(openCts.Token);
            Assert.NotNull(demuxer.Init);

            using var remuxer = new FragmentedMp4Remuxer(demuxer.Init!);
            byte[] header = remuxer.WriteHeader();
            Assert.True(header.Length > 0, "init segment should be non-empty");
            Assert.True(ContainsBox(header, "ftyp"), "init segment should contain an ftyp box");
            Assert.True(ContainsBox(header, "moov"), "init segment should contain a moov box");

            var fragments = new MemoryStream();
            int packetCount = 0;
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                RelayPacket? packet = await demuxer.ReadPacketAsync(readCts.Token);
                if (packet == null)
                    break;
                byte[] frag = remuxer.WritePacket(packet);
                if (frag.Length > 0)
                    fragments.Write(frag, 0, frag.Length);
                packet.Release();
                packetCount++;
            }
            await feed;

            Assert.True(packetCount > 0, "expected packets");
            byte[] fragBytes = fragments.ToArray();
            Assert.True(fragBytes.Length > 0, "expected fMP4 media fragments");
            Assert.True(ContainsBox(fragBytes, "moof"), "fragments should contain moof boxes");
            Assert.True(ContainsBox(fragBytes, "mdat"), "fragments should contain mdat boxes");
        }
    }
}
