# TqkLibrary.StreamRelay

A video **stream relay** for ASP.NET Core. A device pushes a live container stream up over a WebSocket; the
server demuxes it once with FFmpeg and fans the compressed packets out to many viewers. Streams are addressed
by **GUID** for both the device (ingest) and the viewers.

```
                 WebSocket (container bytes)         WebSocket (framed AVPacket)
  [ device ] ──push──► /relay/ingest/{guid}                  /relay/view/{guid} ◄──pull── [ native client ]
                              │                                       ▲
                              ▼                                       │ (one pooled buffer, zero-copy fan-out)
                  ┌──────────────────────────────────────────────────────────┐
                  │  StreamSession (1 per streamId)                           │
                  │   ingest FIFO → FFmpeg demux → GOP buffer → fan-out        │
                  └──────────────────────────────────────────────────────────┘
                              │
                              └──► fMP4 remux ──► GET /relay/view/{guid}.mp4  [ browser / MSE ]
```

> **Status:** milestones M1–M6 complete — solution builds clean, 23 unit/integration tests pass, in-process &
> out-of-process demux and the fMP4 HTTP path verified by smoke runs. See [docs/plan-vi.md](docs/plan-vi.md)
> (architecture, Vietnamese) and [docs/progress-vi.md](docs/progress-vi.md) (build log).

## Highlights

- **WebSocket in and out** — device pushes raw container bytes; viewers receive framed packets (or fMP4).
- **FFmpeg only at the demux/mux edge** — the GOP buffer and fan-out are pure C#, so the hot path can't be
  crashed by a bad input.
- **GOP buffering** — the server keeps the media init plus the packets since the last video keyframe; a new
  viewer gets init → current GOP → live, so it can start decoding immediately.
- **Crash isolation, both modes, both OSes** — in-process (P/Invoke, Windows SEH guard) or out-of-process
  (worker process, supervised). One bad stream never takes the host down. Runs on Windows and Linux.
- **Two viewer surfaces** — native (raw `AVPacket` over a small binary wire format) and browser (fragmented
  MP4 over MSE).
- **Codec-agnostic** — the relay carries codec ids/extradata through opaquely (validated with H.264, H.265, AAC).

## Projects

| Project | TFM | Role |
|---|---|---|
| `TqkLibrary.StreamRelay.Core` | net8.0 | Models, `GopBuffer`, `StreamSession`, `StreamRegistry`, interfaces — no FFmpeg |
| `TqkLibrary.StreamRelay.AspNetCore` | net8.0 | `AddStreamRelay`, `MapRelayIngest`/`MapRelayView`, wire protocol, WebSocket sink |
| `TqkLibrary.StreamRelay.Demux.FFmpeg` | net8.0 | In/out-of-process demuxer + factory, fMP4 remuxer, worker supervisor |
| `TqkLibrary.StreamRelay.Demux.FFmpeg.Native` | CMake | C ABI demux/mux over libav* → shared lib + worker exe |
| `TqkLibrary.StreamRelay.Demo` | net8.0 | Host + device-pusher + native viewer + browser MSE viewer |
| `TqkLibrary.StreamRelay.Test` | net8.0 | xUnit unit/integration tests |

## Requirements

- .NET SDK **10** to build the solution (`.slnx`); the libraries target **net8.0**.
- FFmpeg 8.0.x native libraries via the `TqkLibrary.FFmpeg.*` NuGet packages (avformat-62 / avcodec-62 / avutil-60).
- For the native component: a C++ toolchain + CMake (Visual Studio C++ tools on Windows).

> NuGet packages are not published yet — consume the library via project reference (clone this repo) for now.

## Quick start

Add the relay to an ASP.NET Core host:

```csharp
using TqkLibrary.StreamRelay.AspNetCore.Enums;
using TqkLibrary.StreamRelay.AspNetCore.Extensions;
using TqkLibrary.StreamRelay.Demux.FFmpeg.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddStreamRelay(o =>
    {
        o.DemuxMode = DemuxMode.Auto;   // Windows -> InProcess, Linux -> OutOfProcess
        o.MaxWorkers = 0;               // out-of-process worker cap (0 = unlimited); excess ingest -> 503
        o.WarmPoolSize = 0;             // pre-spawned idle workers to hide launch latency
        // o.AllowedFormats / o.OpenTimeout / o.IngestReceiveBufferSize / o.Session ...
    })
    .AddFFmpegDemuxer();                // plug the FFmpeg-backed demuxer

var app = builder.Build();

app.UseWebSockets();                    // required

app.MapRelayIngest();                   // WS   /relay/ingest/{streamId:guid}?format=mpegts
app.MapRelayView();                     // WS   /relay/view/{streamId:guid}        (native, framed AVPacket)
app.MapRelayViewMp4();                  // GET  /relay/view/{streamId:guid}.mp4     (browser MSE / fMP4)

app.Run();
```

### Endpoints

| Endpoint | Transport | Direction | Notes |
|---|---|---|---|
| `/relay/ingest/{streamId:guid}?format=<fmt>` | WebSocket (binary) | device → server | `format` must be in `AllowedFormats` (default: `mpegts, mp4, mov, matroska, webm, flv, h264, hevc`) else **415**. Send raw container bytes, no framing. |
| `/relay/view/{streamId:guid}` | WebSocket (binary) | server → native client | Framed `Init` then live packets. **404** if no device is streaming that id. |
| `/relay/view/{streamId:guid}.mp4` | HTTP GET | server → browser | fragmented MP4 (init segment + media fragments) for `MediaSource`. **404** if no stream. |

### Egress wire format (native view)

Each binary WebSocket message is one framed message (little-endian). Packet frame header is 28 bytes:

```
[u8 version][u8 type: Init|Packet|Control][u8 streamIndex][u8 flags (bit0 = keyframe)]
[i64 pts][i64 dts][i32 duration][i32 payloadLen][payload …]
```

`Init` carries the format name plus, per stream, the codec id/name, width/height, sample rate/channels,
time base and extradata (SPS/PPS, AudioSpecificConfig). `Control` signals end-of-stream.

## Run the demo

```bash
# 1) Host the relay
dotnet run --project src/TqkLibrary.StreamRelay.Demo -- serve --url http://localhost:5080 --mode InProcess

# 2) Act as a device: stream a container file in (paced, to mimic real time)
dotnet run --project src/TqkLibrary.StreamRelay.Demo -- push \
    "ws://localhost:5080/relay/ingest/<guid>?format=mpegts" input.ts --rate 200000

# 3a) Native viewer
dotnet run --project src/TqkLibrary.StreamRelay.Demo -- view "ws://localhost:5080/relay/view/<guid>"

# 3b) Browser viewer: open http://localhost:5080 and enter <guid>  (MSE / fMP4)
```

One-shot end-to-end check (host + push + view in a single process):

```bash
dotnet run --project src/TqkLibrary.StreamRelay.Demo -- smoke --mode InProcess
# or: --mode OutOfProcess
```

## Demux modes & crash isolation

`DemuxMode` selects how FFmpeg runs, behind the `IStreamDemuxer` interface:

| | In-process | Out-of-process |
|---|---|---|
| **Windows** | P/Invoke; `av_read_frame` wrapped in SEH `__try/__except` → access violations become an error (only that stream dies) | worker process; Job Object `KILL_ON_JOB_CLOSE` (no orphans) |
| **Linux** | P/Invoke (best effort) | worker process; `prctl(PR_SET_PDEATHSIG)` + process group |

`DemuxMode.Auto` picks in-process on Windows and out-of-process on Linux. Out-of-process adds a one-time
per-stream launch cost (hidden by `WarmPoolSize`), not per-frame latency.

## Building the native library

The managed `dotnet build` does **not** build the native component. Build it (Windows) with:

```powershell
pwsh src/TqkLibrary.StreamRelay.Demux.FFmpeg.Native/Build.ps1
```

This uses CMake + the C++ toolchain and the `TqkLibrary.FFmpeg.Native.*` packages to produce the shared lib
and the worker exe under `native-artifacts/runtimes/<rid>/native/`. Windows x64/x86/arm64 are built locally;
Linux/macOS `.so`/`.dylib` are produced in CI (the CMake config has the cross-platform branches). The FFmpeg
demuxer needs this native library present at runtime; integration tests that use it are skippable when it is absent.

## Roadmap / follow-ups

- NuGet packaging (nuspec + GitVersion + `ProjectBuildProperties.targets`) and a release tag — not wired yet.
- CI to build the Linux/macOS native binaries.
- Optional WebCodecs viewer path (current browser path is fMP4/MSE).

## License

Not set yet. Note the native demux/mux links an FFmpeg **GPL** shared build, which has distribution
implications for any binaries that bundle it; choose a project license accordingly.
