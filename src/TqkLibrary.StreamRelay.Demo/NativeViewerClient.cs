using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using TqkLibrary.StreamRelay.AspNetCore;
using TqkLibrary.StreamRelay.AspNetCore.Enums;
using TqkLibrary.StreamRelay.AspNetCore.Models;
using TqkLibrary.StreamRelay.Models;

namespace TqkLibrary.StreamRelay.Demo
{
    /// <summary>
    /// A console viewer: connects to <c>/relay/view/{guid}</c>, reassembles framed messages and decodes them
    /// with <see cref="WireProtocolReader"/>, logging the init and a running packet/keyframe count. Returns
    /// the totals so a smoke test can assert on them.
    /// </summary>
    public static class NativeViewerClient
    {
        public sealed record Result(MediaInit? Init, int PacketCount, int KeyframeCount, bool EndOfStream);

        public static async Task<Result> RunAsync(Uri viewUri, int maxPackets, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var ws = new ClientWebSocket();
            Console.WriteLine($"[viewer] connecting to {viewUri}");
            await ws.ConnectAsync(viewUri, cancellationToken).ConfigureAwait(false);
            Console.WriteLine("[viewer] connected; receiving frames...");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            CancellationToken token = timeoutCts.Token;

            MediaInit? init = null;
            int packets = 0;
            int keyframes = 0;
            bool eos = false;

            var buffer = new MemoryStream();
            byte[] chunk = new byte[64 * 1024];

            try
            {
                while (!token.IsCancellationRequested)
                {
                    buffer.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(chunk), token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            eos = true;
                            break;
                        }
                        buffer.Write(chunk, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (eos)
                        break;

                    byte[] frame = buffer.ToArray();
                    if (frame.Length < 2)
                        continue;

                    switch (WireProtocolReader.PeekType(frame))
                    {
                        case WireMessageType.Init:
                            init = WireProtocolReader.ReadInit(frame);
                            Console.WriteLine($"[viewer] init: format={init.FormatName}, streams={init.Streams.Count}");
                            foreach (MediaStreamInfo s in init.Streams)
                                Console.WriteLine($"          #{s.Index} {s.Kind} {s.CodecName} {s.Width}x{s.Height} extradata={s.Extradata.Length}B");
                            break;

                        case WireMessageType.Packet:
                            WirePacket pkt = WireProtocolReader.ReadPacket(frame);
                            packets++;
                            if (pkt.IsKeyframe)
                                keyframes++;
                            if (packets % 30 == 0 || pkt.IsKeyframe)
                                Console.WriteLine($"[viewer] packet #{packets} stream={pkt.StreamIndex} key={pkt.IsKeyframe} pts={pkt.Pts} len={pkt.Payload.Length}");
                            if (maxPackets > 0 && packets >= maxPackets)
                            {
                                Console.WriteLine($"[viewer] reached maxPackets={maxPackets}; stopping.");
                                return new Result(init, packets, keyframes, eos);
                            }
                            break;

                        case WireMessageType.Control:
                            if (WireProtocolReader.ReadControlCode(frame) == WireProtocol.ControlEndOfStream)
                            {
                                eos = true;
                                Console.WriteLine("[viewer] end of stream control received.");
                            }
                            break;
                    }

                    if (eos)
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[viewer] receive loop ended (timeout/cancel).");
            }
            catch (WebSocketException ex)
            {
                Console.WriteLine($"[viewer] socket closed: {ex.WebSocketErrorCode}");
            }

            Console.WriteLine($"[viewer] done: packets={packets}, keyframes={keyframes}, eos={eos}");
            return new Result(init, packets, keyframes, eos);
        }
    }
}
