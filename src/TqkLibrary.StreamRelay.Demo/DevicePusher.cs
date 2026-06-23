using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace TqkLibrary.StreamRelay.Demo
{
    /// <summary>
    /// A console "device": opens a client WebSocket to <c>/relay/ingest/{guid}</c> and streams a media
    /// file's bytes in chunks, pacing optionally to mimic real-time so viewers can follow a live GOP.
    /// </summary>
    public static class DevicePusher
    {
        public static async Task RunAsync(Uri ingestUri, string filePath, int chunkSize, int pacingBytesPerSecond, CancellationToken cancellationToken)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Sample file not found.", filePath);

            using var ws = new ClientWebSocket();
            Console.WriteLine($"[pusher] connecting to {ingestUri}");
            await ws.ConnectAsync(ingestUri, cancellationToken).ConfigureAwait(false);
            Console.WriteLine("[pusher] connected; streaming bytes...");

            byte[] buffer = new byte[chunkSize];
            long sent = 0;
            var started = DateTime.UtcNow;

            await using FileStream file = File.OpenRead(filePath);
            int read;
            while ((read = await file.ReadAsync(buffer.AsMemory(0, chunkSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await ws.SendAsync(new ReadOnlyMemory<byte>(buffer, 0, read), WebSocketMessageType.Binary, endOfMessage: true, cancellationToken)
                    .ConfigureAwait(false);
                sent += read;

                if (pacingBytesPerSecond > 0)
                {
                    double expectedElapsed = (double)sent / pacingBytesPerSecond;
                    double actualElapsed = (DateTime.UtcNow - started).TotalSeconds;
                    double sleep = expectedElapsed - actualElapsed;
                    if (sleep > 0)
                        await Task.Delay(TimeSpan.FromSeconds(sleep), cancellationToken).ConfigureAwait(false);
                }
            }

            Console.WriteLine($"[pusher] sent {sent} bytes; closing.");
            try
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "eof", cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException) { }
        }
    }
}
