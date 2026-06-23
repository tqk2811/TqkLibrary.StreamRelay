using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using TqkLibrary.StreamRelay.AspNetCore.Enums;
using TqkLibrary.StreamRelay.Demo;

// Subcommands:
//   serve [--url http://localhost:5080] [--mode Auto|InProcess|OutOfProcess]
//   push  <ingestWsUrl> <file> [--chunk 4096] [--rate <bytes/s>]
//   view  <viewWsUrl> [--max <packets>] [--timeout <seconds>]
//   smoke [--mode ...] [--file <sample.ts>]   (host + push + view in one process)

string command = args.Length > 0 ? args[0].ToLowerInvariant() : "smoke";
string[] rest = args.Length > 1 ? args[1..] : Array.Empty<string>();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    switch (command)
    {
        case "serve":
            await ServeAsync(rest, cts.Token);
            break;
        case "push":
            await PushAsync(rest, cts.Token);
            break;
        case "view":
            await ViewAsync(rest, cts.Token);
            break;
        case "smoke":
            return await SmokeAsync(rest, cts.Token);
        default:
            Console.WriteLine("Unknown command. Use: serve | push | view | smoke");
            return 2;
    }
    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine("cancelled.");
    return 0;
}

static DemuxMode ParseMode(string[] a)
{
    string? v = GetOption(a, "--mode");
    return v != null && Enum.TryParse<DemuxMode>(v, true, out DemuxMode m) ? m : DemuxMode.Auto;
}

static string? GetOption(string[] a, string name)
{
    for (int i = 0; i < a.Length - 1; i++)
        if (string.Equals(a[i], name, StringComparison.OrdinalIgnoreCase))
            return a[i + 1];
    return null;
}

static async Task ServeAsync(string[] a, CancellationToken token)
{
    string url = GetOption(a, "--url") ?? "http://localhost:5080";
    DemuxMode mode = ParseMode(a);
    WebApplication app = RelayHostFactory.Build(Array.Empty<string>(), url, mode);
    Console.WriteLine($"[serve] relay listening on {url} (DemuxMode={mode})");
    Console.WriteLine($"[serve] ingest: ws://.../relay/ingest/<guid> | view: ws://.../relay/view/<guid> | browser: {url}");
    await app.RunAsync();
}

static async Task PushAsync(string[] a, CancellationToken token)
{
    if (a.Length < 2)
    {
        Console.WriteLine("usage: push <ingestWsUrl> <file> [--chunk N] [--rate bytesPerSec]");
        return;
    }
    var uri = new Uri(a[0]);
    string file = a[1];
    int chunk = int.TryParse(GetOption(a, "--chunk"), out int c) ? c : 4096;
    int rate = int.TryParse(GetOption(a, "--rate"), out int r) ? r : 0;
    await DevicePusher.RunAsync(uri, file, chunk, rate, token);
}

static async Task ViewAsync(string[] a, CancellationToken token)
{
    if (a.Length < 1)
    {
        Console.WriteLine("usage: view <viewWsUrl> [--max N] [--timeout seconds]");
        return;
    }
    var uri = new Uri(a[0]);
    int max = int.TryParse(GetOption(a, "--max"), out int m) ? m : 0;
    int timeout = int.TryParse(GetOption(a, "--timeout"), out int t) ? t : 30;
    await NativeViewerClient.RunAsync(uri, max, TimeSpan.FromSeconds(timeout), token);
}

static async Task<int> SmokeAsync(string[] a, CancellationToken token)
{
    DemuxMode mode = ParseMode(a);
    string url = GetOption(a, "--url") ?? "http://localhost:5099";
    string file = GetOption(a, "--file") ?? FindSampleFile();
    if (!File.Exists(file))
    {
        Console.WriteLine($"[smoke] sample file not found: {file}. Pass --file <path>.");
        return 3;
    }

    Guid streamId = Guid.NewGuid();
    var baseWs = url.Replace("http://", "ws://").Replace("https://", "wss://").TrimEnd('/');
    var ingestUri = new Uri($"{baseWs}/relay/ingest/{streamId}?format=mpegts");
    var viewUri = new Uri($"{baseWs}/relay/view/{streamId}");

    WebApplication app = RelayHostFactory.Build(Array.Empty<string>(), url, mode);
    await app.StartAsync(token);
    Console.WriteLine($"[smoke] host started on {url}; stream {streamId}; DemuxMode={mode}");

    try
    {
        // Start the device pusher (paced) so a GOP is live while the viewer joins.
        Task pushTask = DevicePusher.RunAsync(ingestUri, file, 4096, pacingBytesPerSecond: 16 * 1024, token);

        // Give ingest a moment to create the session + probe the header.
        await Task.Delay(800, token);

        NativeViewerClient.Result result = await NativeViewerClient.RunAsync(
            viewUri, maxPackets: 50, timeout: TimeSpan.FromSeconds(20), token);

        try { await pushTask; } catch (Exception ex) { Console.WriteLine($"[smoke] pusher: {ex.Message}"); }

        bool ok = result.Init != null && result.PacketCount > 0 && result.KeyframeCount > 0;
        Console.WriteLine($"[smoke] RESULT: init={(result.Init != null)} packets={result.PacketCount} keyframes={result.KeyframeCount} -> {(ok ? "PASS" : "FAIL")}");
        return ok ? 0 : 1;
    }
    finally
    {
        await app.StopAsync(TimeSpan.FromSeconds(3));
    }
}

static string FindSampleFile()
{
    // Prefer the test asset; fall back to a sample beside the exe.
    string[] candidates =
    {
        Path.Combine(AppContext.BaseDirectory, "sample.ts"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test", "TqkLibrary.StreamRelay.Test", "Assets", "sample.ts"),
    };
    foreach (string c in candidates)
    {
        string full = Path.GetFullPath(c);
        if (File.Exists(full))
            return full;
    }
    return candidates[0];
}
