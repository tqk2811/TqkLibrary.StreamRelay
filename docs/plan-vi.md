# Kế hoạch: TqkLibrary.StreamRelay

> Relay video stream chạy trên ASP.NET Core: thiết bị đẩy container stream lên server, server demux thành AVPacket và phân phát lại cho nhiều client xem. Phân biệt stream theo GUID cho cả thiết bị lẫn client.
>
> Trạng thái: **M1 (Core) — foundation đã xong, build net8.0 sạch.** Cập nhật: 2026-06-23.

## 1. Quyết định đã chốt

| Hạng mục | Lựa chọn |
|---|---|
| Transport | **WebSocket** cả hai chiều |
| Nguồn dữ liệu ingest | **Container stream** → server **phải demux bằng FFmpeg** (libavformat) để tách packet + phát hiện keyframe |
| Chế độ demux | Hỗ trợ **cả in-process (P/Invoke) lẫn out-of-process (worker exe)**, chọn qua `DemuxMode { Auto, InProcess, OutOfProcess }` |
| Nền tảng | **Windows + Linux** |
| Client xem | Native (raw AVPacket) trước; Web (fMP4/MSE hoặc WebCodecs) là phase 2 |
| Đóng gói | Core + Demux.FFmpeg(+Native) + AspNetCore + Demo + Test |

Còn mở: codec mục tiêu giai đoạn đầu (H.264 + H.265, có AAC audio?), kỹ thuật web client (fMP4/MSE vs WebCodecs).

## 2. Tổng quan luồng dữ liệu

```
                 WebSocket (container bytes)        WebSocket (framed AVPacket)
  [Stream Device] ──push──► /relay/ingest/{guid}                /relay/view/{guid} ◄──pull── [Native client]
                                  │                                     ▲
                                  ▼                                     │ (cùng buffer, zero-copy)
                          ┌────────────────────────────────────────────────────┐
                          │  StreamSession (1 / streamId)                       │
                          │   Ingest ring → Demux(ffmpeg) → GOP buffer → Fanout │
                          └────────────────────────────────────────────────────┘
                                  │
                                  └──► (Phase 2) Remux fMP4 / WebCodecs ──► /relay/view/{guid}.mp4 [Web/MSE]
```

**Nguyên tắc cốt lõi:** FFmpeg chỉ tồn tại ở **đúng một biên — bước demux ingest**. Sau demux, packet là buffer byte mờ + metadata trong managed; toàn bộ GOP buffer + fan-out là **C# thuần, không đụng ffmpeg → không có rủi ro crash ở phần nóng nhất**.

## 3. Phân rã solution

| Project | TFM | Vai trò | Phụ thuộc ffmpeg |
|---|---|---|---|
| `TqkLibrary.StreamRelay.Core` | net8.0 (có thể thêm netstandard2.0) | Models, interfaces, `GopBuffer`, `StreamSession`, `StreamRegistry`, pooling/refcount | **Không** |
| `TqkLibrary.StreamRelay.Demux.FFmpeg` | net8.0 | `InProcessFFmpegDemuxer`, `OutOfProcessFFmpegDemuxer`, factory, P/Invoke wrapper | Có (gián tiếp) |
| `TqkLibrary.StreamRelay.Demux.FFmpeg.Native` | CMake (win x64/x86/arm64, linux x64/arm64) | C ABI demux: AVIO ring + `av_read_frame`, whitelist demuxer, SEH guard; build **shared lib** + **worker exe** | **C++ ffmpeg** |
| `TqkLibrary.StreamRelay.AspNetCore` | net8.0 | `AddStreamRelay()`, `MapRelayIngest()`, `MapRelayView()`, WebSocket endpoints, options | Không |
| `TqkLibrary.StreamRelay.Demo` | net8.0 (host) | Host mẫu + console device-pusher + (phase 2) web viewer | — |
| `TqkLibrary.StreamRelay.Test` | net8.0 | Unit test GopBuffer/fanout/backpressure với demuxer giả | Không |

Quy ước namespace (theo `~/.claude/csharp.md`): `interface`→`.Interfaces`, `enum`→`.Enums`, POCO/struct dữ liệu→`.Models`, helper→`.Helpers`, extension→`.Extensions`; engine class (`GopBuffer`, `StreamSession`, `StreamRegistry`) ở namespace feature gốc. Mỗi type một file.

Đóng gói NuGet theo `~/.claude/BuildNuggetCsharpLibrary.md` (nuspec + GitVersion + `ProjectBuildProperties.targets`). `Demux.FFmpeg` kèm `Build.ps1` build native qua CMake và pack `runtimes/<rid>/native/` — tái dùng mẫu `TqkLibrary.AudioPlayer.FFmpegAudioReader`.

## 4. Mô hình runtime

- **`StreamRegistry`**: `ConcurrentDictionary<Guid, StreamSession>`. Device → `/relay/ingest/{streamId}` tạo/chiếm session; client → `/relay/view/{streamId}` để subscribe. Mỗi client có Guid riêng (quản lý/đuổi/đếm).
- **`StreamSession`** (1 producer + N consumer):
  - Ingest loop: nhận binary WS → đẩy byte vào ring buffer native.
  - Demux loop (Task): `IStreamDemuxer.ReadPacketAsync()` → `RelayPacket` → `GopBuffer.Append` → publish cho subscribers.
  - `MediaInit` (codecpar/extradata SPS/PPS) bắt lúc mở container → gửi đầu tiên cho mọi client mới.
- **`GopBuffer`** (đúng yêu cầu): giữ `MediaInit` + packet **từ keyframe video gần nhất**. Keyframe mới → giải phóng (decref) GOP cũ, mở GOP mới. Client mới: gửi init → replay GOP từ keyframe → nối live.
- **Fan-out zero-copy**: payload demux copy **một lần** vào `RefCountedBuffer` pooled; cùng một `ReadOnlyMemory<byte>` gửi tới mọi WebSocket. Refcount=0 → trả `ArrayPool`.
- **Backpressure**: mỗi subscriber có `Channel<RelayPacket>` bounded. Đầy (client chậm) → drop tới keyframe kế + **resync** (gửi lại init+GOP), hoặc ngắt nếu vượt ngưỡng. Một send loop / connection.
- **Vòng đời**: ingest ngắt → đánh dấu ended, báo client, dọn session khi hết client; client ngắt → gỡ subscriber; session rảnh → GC theo timeout.

## 5. Giao thức WebSocket

- **Ingest** `/relay/ingest/{streamId}?format=mpegts`: device stream thẳng byte container; `format` (whitelist) giúp bỏ probe mơ hồ + giảm bề mặt tấn công.
- **Egress** `/relay/view/{streamId}`: server gửi message khung hóa:
  ```
  [u8 version][u8 type: Init|Packet|Control][u8 streamIndex][u8 flags(bit0=keyframe)]
  [i64 pts][i64 dts][i32 duration][i32 payloadLen][payload...]
  ```
  `Init` mang codec id + extradata + WxH + timebase. Native client tự dựng AVPacket để decode; web phase 2 dùng đường riêng (mục 8).

## 6. Demux native (C ABI, mẫu AudioReader)

Byte tới dần qua WS, không có file path → dùng **custom `AVIOContext` + read callback** kéo từ FIFO native do managed nạp:

```c
void* Demux_Alloc(const char* formatName);
int   Demux_PushBytes(void*, const uint8_t*, int);   // ingest loop nạp; read callback block tới khi có data
int   Demux_Open(void*);                             // avformat_open_input qua AVIO
int   Demux_GetInit(void*, MediaInitOut*);           // codecpar/extradata
int   Demux_ReadPacket(void*, PacketOut*);           // av_read_frame → pts/dts/flags/streamIndex + payload ptr
void  Demux_SignalEof(void*);
void  Demux_Free(void**);
int   Demux_GetLastError();
```

- P/Invoke **một** thư viện duy nhất; resolver preload ffmpeg (avutil→avcodec→avformat) — tái dùng `NativeWrapper.cs` của AudioReader.
- Whitelist demuxer (`mpegts/mp4/matroska/flv/h264/hevc`), **tắt protocol mạng** của ffmpeg (chỉ đọc qua AVIO của ta).
- **Một Native core, dùng hai cách**: in-process P/Invoke shared lib; out-of-process là worker exe link đúng shared lib đó + vòng lặp `stdin→PushBytes` / `ReadPacket→stdout`.

## 7. Chống crash (cả in & out, cả Windows & Linux)

| | In-process | Out-of-process |
|---|---|---|
| **Windows** | SEH `__try/__except` quanh `av_read_frame` → access-violation thành mã lỗi, chỉ 1 stream chết, host sống | Job Object `KILL_ON_JOB_CLOSE` (host chết → worker chết, không orphan) |
| **Linux** | Best-effort (`sigaction`+`siglongjmp`, mong manh) → **khuyến nghị out-of-process** | `prctl(PR_SET_PDEATHSIG)` + process group |

Chọn qua options:
```csharp
services.AddStreamRelay(o => o.DemuxMode = DemuxMode.Auto);
// Auto: Windows → InProcess, Linux → OutOfProcess. Override được.
```
`IStreamDemuxerFactory` tạo `InProcessFFmpegDemuxer` hay `OutOfProcessFFmpegDemuxer` theo mode — relay core gọi qua `IStreamDemuxer`, không đổi dòng nào.

### Quản lý out-of-process

- **Delay khởi động**: một lần / mỗi stream (KHÔNG phải mỗi frame), ~10–40ms cho exe native; phần đắt nhất (probe container) **bằng nhau** in/out process. **Warm pool** (giữ sẵn vài worker idle) xóa sạch delay thấy được + chống thundering herd. Steady-state truyền packet qua pipe = microsecond.
- **Supervisor**: `ConcurrentDictionary<Guid, WorkerHandle>`; IPC qua **stdin/stdout** (pipe ẩn, cross-platform, OS tự dọn handle). Bắt `Process.Exited` → đánh dấu faulted, báo subscriber. Watchdog kill worker treo. `MaxWorkers` cap → vượt thì từ chối ingest (503). Giới hạn RAM/CPU qua Job Object (win) / cgroup (linux).
- **Recovery thực tế**: crash giữa chừng = drop đúng stream đó + yêu cầu device reconnect; **host + stream khác sống** — đúng mục tiêu "nhiều thiết bị không kéo sập server".
- **Khuyến nghị**: 1 worker / 1 stream (cô lập sạch). Scale rất lớn mới chuyển pool "1 worker gánh K stream".

## 8. Web client (Phase 2)

- **fMP4 + MSE**: remux packet đã demux → fragmented MP4 (`movflags=frag_keyframe+empty_moov+default_base_moof`), phát qua `/relay/view/{streamId}.mp4`. Mux packet đã hợp lệ → an toàn hơn demux nhiều.
- **WebCodecs**: gửi thẳng khung AVPacket (mục 5) qua WS, browser decode bằng `VideoDecoder`. Nhẹ hơn, không cần remux, phụ thuộc codec WebCodecs hỗ trợ.

## 9. Hiệu năng & độ bền

Zero-copy fan-out (1 buffer → N WS), `ArrayPool` + refcount, một send loop/connection, bounded channel + resync-từ-keyframe cho client chậm, swap GOP có lock ngắn để tránh race "keyframe mới vs snapshot client mới".

## 10. Lộ trình & trạng thái

| Milestone | Nội dung | Trạng thái |
|---|---|---|
| **M1** | Core: models, interfaces, `RefCountedBuffer`, `GopBuffer`, `StreamSession`, `StreamRegistry`, Test | **Xong** — build net8.0 sạch, 16 test pass |
| M2 | AspNetCore: WS endpoints ingest/view + DI + options + wire protocol | **Xong** — build sạch, +3 test wire protocol |
| M3 | Demux.FFmpeg + Native: CMake, AVIO ring, C ABI, SEH guard, `Build.ps1` | **Xong** — native build (win x64/x86/arm64) OK, integration test demux mpegts thật pass |
| M4 | Demo: host + console device-pusher + native viewer (end-to-end) | **Xong** — smoke chạy thật PASS (init h264 + 30 packet + 2 keyframe + EOS) |
| M5 | Out-of-process demux (worker exe + supervisor + warm pool) | Chưa |
| M6 | Web client (fMP4/MSE hoặc WebCodecs) | Chưa |

## 11. Cấu trúc thư mục hiện tại

```
TqkLibrary.StreamRelay/
  TqkLibrary.StreamRelay.slnx
  docs/plan-vi.md
  src/TqkLibrary.StreamRelay.Core/
    TqkLibrary.StreamRelay.Core.csproj   (net8.0, RootNamespace=TqkLibrary.StreamRelay)
    Enums/MediaCodecKind.cs
    Models/{MediaStreamInfo,MediaInit,RelayPacket,GopSnapshot}.cs
    Buffers/RefCountedBuffer.cs
    Interfaces/{IStreamDemuxer,IStreamDemuxerFactory,IPacketSink}.cs
    GopBuffer.cs
```

## 12. Tham chiếu

- FFmpeg toolchain: `TqkLibrary.FFmpeg.*` NuGet 8.0.1.48 (avformat-62/avcodec-62/avutil-60).
- Mẫu native binding: `D:\IT\Csharp\Libraries\TqkLibrary.AudioPlayer.FFmpegAudioReader` (CMake + 1 C ABI + `NativeWrapper.cs` resolver preload ffmpeg).
- Mẫu phát hiện keyframe: `D:\IT\Csharp\Libraries\TqkLibrary.Scrcpy` (`av_parser`, `ParsePacket.cpp`).
- Hướng dẫn đóng gói/đa nền native: `~/.claude/BuildNuggetCsharpLibrary.md` mục 6.
