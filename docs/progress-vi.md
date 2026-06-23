# Nhật ký tiến độ: TqkLibrary.StreamRelay

> Log chạy tự động các milestone M1–M6. Code + identifier bằng tiếng Anh; nhật ký bằng tiếng Việt.

## Môi trường
- .NET SDK: 10.0.301 (và 8.0.422). Solution `.slnx` (cần SDK .NET 10 để build).
- Visual Studio 2026 (18) Community có CMake + VC++ tools → native build M3/M5 khả thi trên Windows.
- FFmpeg NuGet 8.0.1.48 (avformat-62/avcodec-62/avutil-60).
- OS: Windows 11.

## M1 — Core: StreamSession + StreamRegistry + Test — XONG

### Đã làm
- `Models/RelaySessionOptions.cs`: tunables (capacity channel/subscriber, idle timeout, max-resync-trước-khi-drop).
- `Models/SubscriberMessage.cs` + `Enums/SubscriberMessageKind.cs`: item trong hàng đợi gửi của 1 viewer — hoặc `Init` hoặc `Packet`. Gộp init+packet vào **một channel có thứ tự** → đảm bảo "init trước, rồi GOP, rồi live" nguyên tử, không race thứ tự giữa các luồng.
- `StreamSession.cs` + `StreamSession.Subscriber.cs` (partial): engine 1 producer (demux loop) → N subscriber.
  - `RunDemuxLoopAsync`: pull `ReadPacketAsync` → `GopBuffer.Append` → `Publish` fan-out → release ref demuxer trả về.
  - Fan-out zero-copy: mỗi packet `AddRef` 1 lần cho mỗi subscriber, đẩy vào `Channel<SubscriberMessage>` bounded.
  - Client chậm (channel đầy): `TryWrite` fail → flush sạch hàng đợi (release hết) → đánh dấu `NeedsResync`; keyframe kế tiếp re-prime init+GOP từ `GopBuffer.Snapshot()`. Vượt `MaxResyncBeforeDrop` → drop hẳn viewer.
  - Mid-stream joiner: `AddSubscriber` prime ngay nếu đã có GOP (không phải đợi hết 1 GOP).
  - `WriteToSinkAsync`: send loop chuẩn (1/connection) drain channel → `IPacketSink`, release từng packet sau khi gửi, complete cuối stream. Có finally drain tránh leak khi cancel.
- `StreamRegistry.cs`: `ConcurrentDictionary<Guid, StreamSession>`, vòng đời ingest/viewer, `SweepIdleAsync` + `StartIdleSweep` GC session rảnh (không ingest active + 0 viewer + quá idle timeout).

### Quyết định
- Dùng `Channel<SubscriberMessage>` thay vì `Channel<RelayPacket>` + cờ epoch để loại bỏ hoàn toàn race ordering init↔packet. Init không mang buffer nên không cần refcount.
- `BoundedChannelFullMode.Wait` nhưng KHÔNG bao giờ block-write — luôn `TryWrite`; đầy thì tự xử lý resync. Một send loop/connection đọc ra.
- Hợp đồng refcount: packet trong channel mang đúng 1 ref của subscriber; ai lấy ra (send loop khi gửi xong / session khi flush/drop/dispose) phải release.

### Test (16 pass, `dotnet test` xanh)
- `GopBufferTests`: snapshot trước init = null; keyframe mới evict GOP cũ + release buffer; snapshot chứa đúng GOP mới nhất; snapshot AddRef giữ buffer sống tới khi release; dispose release hết; keyframe audio không evict GOP video.
- `StreamSessionTests`: fan-out init+GOP cho 2 viewer; mid-stream joiner bắt đầu từ keyframe; slow viewer drop-to-keyframe + resync không leak; remove subscriber dừng gửi + release; EOS complete mọi viewer.
- `StreamRegistryTests`: GetOrCreate idempotent; TryGet false khi không có; sweep xóa session ended idle; giữ session khi ingest active / còn viewer.
- Fakes: `FakeStreamDemuxer`, `FakePacketSink` (có gate giả lập client chậm), `TestPacketFactory`, `RefCountAssert` (probe AddRef/Release để biết buffer đã release hết chưa, không đụng internal Core).

### Commit
- `9bf797e` feat(core): StreamSession + StreamRegistry
- `7b60f59` test(core)
- `7750372` docs

## M2 — AspNetCore: WS endpoints + DI + wire protocol — XONG

### Đã làm
- `Enums/DemuxMode.cs` (Auto/InProcess/OutOfProcess), `Enums/WireMessageType.cs` (Init/Packet/Control).
- `WireProtocol.cs`: serialize Init/Packet/Control theo plan §5 (little-endian). Packet header 28 byte: `[u8 ver][u8 Packet][u8 streamIndex][u8 flags(bit0=keyframe)][i64 pts][i64 dts][i32 duration][i32 payloadLen][payload]`. Packet frame rent từ `ArrayPool` (caller return). Init frame mang formatName + mỗi stream: index/kind/codecId/codecName/WxH/sampleRate/channels/timebase/extradata.
- `WireProtocolReader.cs` + `Models/WirePacket.cs`: parse ngược (cho native viewer M4 + test).
- `WebSocketPacketSink.cs` (`IPacketSink`): gửi frame Init/Packet/Control qua WS binary; return buffer pooled sau gửi.
- `StreamRelayOptions.cs`: DemuxMode, `RelaySessionOptions Session`, whitelist `AllowedFormats` (mpegts/mp4/mov/matroska/webm/flv/h264/hevc), buffer size, open timeout.
- `NotConfiguredStreamDemuxerFactory.cs`: factory mặc định fail-fast (M3 thay bằng FFmpeg thật qua thứ tự `TryAddSingleton`).
- `RelayConnectionHandler.cs`: engine cho cả 2 vai.
  - Ingest: `GetOrCreateForIngest` → tạo demuxer → pump WS bytes vào `demuxer.WriteAsync` trên 1 task + `RunDemuxLoopAsync` trên task khác; có open timeout; finally release ingest + close socket.
  - View: `TryGet` (404 nếu không có stream) → `AddSubscriber` → 1 send loop `WriteToSinkAsync` → `WebSocketPacketSink`; watch socket close để teardown; finally `RemoveSubscriber`.
- `Extensions/StreamRelayServiceCollectionExtensions.cs`: `AddStreamRelay(o => ...)` đăng ký options + singleton `StreamRegistry` (lấy `RelaySessionOptions` từ options) + handler + factory placeholder.
- `Extensions/StreamRelayEndpointRouteBuilderExtensions.cs`: `MapRelayIngest("/relay/ingest/{streamId:guid}")` (check `?format=` whitelist → 415 nếu sai), `MapRelayView("/relay/view/{streamId:guid}")`.

### Quyết định
- 1 native dll P/Invoke; factory resolve qua DI để M3 ghi đè không cần sửa M2.
- Init không gửi qua channel của Core dưới dạng frame — Core trả `SubscriberMessage(Init)` ở `WriteToSinkAsync`, sink mới đóng frame. Giữ Core sạch giao thức.
- View dùng 2 task: send loop + watch-close (đọc socket để bắt Close, hủy connCts).

### Test (19 pass tổng)
- `WireProtocolTests`: Init round-trip (video+audio+extradata), Packet round-trip (payload nguyên vẹn), Control = EOS.

### Commit
- `<m2-feat>` feat(aspnetcore), `<m2-test>` test, `a5f5206` docs

## M3 — Demux.FFmpeg + Native — XONG (build native Windows OK, integration test pass)

### Native (`src/TqkLibrary.StreamRelay.Demux.FFmpeg.Native`, CMake)
- `ByteFifo.h`: FIFO byte thread-safe (mutex + condition_variable). `Push`/`Read` (block tới khi có data/EOF/abort)/`SignalEof`/`Abort`. AVIO read callback chặn ở `Read`.
- `Demuxer.{h,cpp}`: custom `AVIOContext` (read callback kéo từ FIFO, no seek) → `avformat_open_input` + `avformat_find_stream_info`. Whitelist demuxer bằng `av_find_input_format(formatName)`. `AVFMT_FLAG_CUSTOM_IO`. `BuildStreamInfo` copy codecpar/extradata/codecName + tạo `av_parser` cho stream video (fallback keyframe). `ReadPacket`: `av_read_frame` → keyframe = `AV_PKT_FLAG_KEY` hoặc parser `key_frame`; data ptr valid tới read kế (managed copy ngay).
- `DemuxInterop.h`: struct POD `StreamInfoOut`/`MediaInitOut`/`PacketOut` (`#pragma pack(8)`), khớp mirror managed.
- `Exports.{h,cpp}`: C ABI `Demux_Alloc/PushBytes/SignalEof/Open/GetInit/ReadPacket/Free/GetLastError`. **Windows: `ReadPacketGuarded` bọc `av_read_frame` trong SEH `__try/__except`** → access-violation thành `AVERROR_EXTERNAL` (chỉ stream đó chết, host sống). Hàm SEH không có C++ object cần unwinding.
- `Worker.cpp`: exe out-of-process (cho M5) link thẳng demux core, cầu nối stdin/stdout length-prefixed: host→worker `[u8 cmd][i32 len][payload]` (1=Push,2=Eof,3=Open); worker→host `[u8 type][i32 len][payload]` (1=Init,2=Packet,3=Eof,4=Error). `_setmode(_O_BINARY)` cho stdio. 1 thread đọc stdin + main loop đọc packet.
- `CMakeLists.txt`: build SHARED lib + worker exe, link avformat/avcodec/avutil từ NuGet (Windows import lib / Linux .so). `version.rc` + `version.generated.h.in` (resource version Windows).
- `Build.ps1`: CMake qua VS (vswhere `-requires VC.Tools`), version qua GitVersion, build win x64/x86/arm64 → `native-artifacts/runtimes/<rid>/native/`. **Fix**: dùng args array tường minh cho cmake (`@cmakeArgs`) — form backtick-continued `-DVAR=$v` bị mis-tokenize trên host này → CMake nhận literal `$verMajor` → RC2104.

### Managed (`src/TqkLibrary.StreamRelay.Demux.FFmpeg`)
- `NativeWrapper.cs`: resolver preload ffmpeg (avutil→…→avformat) + load native lib; `FindWorkerExecutable` (cho M5); P/Invoke C ABI.
- `Interop/{StreamInfoOut,MediaInitOut,PacketOut}.cs`: mirror struct `Pack=8`.
- `Helpers/NativeInitMarshaler.cs`: `MediaInitOut` → `MediaInit` (đọc mảng stream, extradata, codecName; map AVMediaType→MediaCodecKind; chọn PrimaryVideoStreamIndex).
- `Helpers/WorkerInitSerializer.cs`: parse init payload từ worker (cho out-of-process).
- `InProcessFFmpegDemuxer.cs` (`IStreamDemuxer`): `Demux_Alloc`; `WriteAsync` pin + `PushBytes` (FIFO copy); `OpenAsync` chạy `Demux_Open` trên thread (block tới khi đủ byte); `ReadPacketAsync` `Demux_ReadPacket` trên thread, copy native→`RefCountedBuffer` qua `Buffer.MemoryCopy`. `_nativeLock` để PushBytes (ingest thread) an toàn với ReadPacket (demux thread) — thực ra FIFO tự lock, lock này bảo vệ ReadPacket/Free.
- `OutOfProcessFFmpegDemuxer.cs`: spawn worker exe, cầu nối stdin/stdout; parse Init/Packet/Eof/Error. (M5 thêm supervisor/warm-pool/job-object.)
- `FFmpegStreamDemuxerFactory.cs`: chọn In/OutOfProcess theo `DemuxMode` (Auto: Windows→InProcess, Linux→OutOfProcess); OutOfProcess thiếu worker → fallback InProcess.
- `Extensions/FFmpegDemuxServiceCollectionExtensions.cs`: `AddFFmpegDemuxer()` thay factory placeholder.

### Build native (Windows) — THÀNH CÔNG
- VS 2026 (18) Community có CMake+Ninja+VC. `Build.ps1` build cả 3 RID Windows. Native dll FileVersion `0.0.9.0`. Worker exe build OK.

### Test (20 pass, 0 skip)
- `InProcessFFmpegDemuxerTests` (SkippableFact): demux file `Assets/sample.ts` (mpegts H.264 2s 320x240, ffmpeg.exe sinh) qua native InProcess — **chạy thật, không skip**: probe init có stream video, đọc ra packet + có keyframe, payload > 0. Xác nhận toàn bộ đường native end-to-end.
- `.gitattributes`: `*.ts/*.mp4/...` = binary (tránh git normalize file mpegts như text). `.gitignore`: `native-build/`, `native-artifacts/`.

### Commit
- `ee4da3b` feat(demux-native), `1a64d15` chore(build), `9aefe2a` feat(demux managed), `f6c32b7` test, `6a0ae9a` docs

## M4 — Demo: host + device-pusher + native viewer — XONG (smoke end-to-end PASS)

### Đã làm (`src/TqkLibrary.StreamRelay.Demo`, ASP.NET Web SDK exe)
- `RelayHostFactory.cs`: build WebApplication `AddStreamRelay` + `AddFFmpegDemuxer` + `UseWebSockets` + static files + `MapRelayIngest`/`MapRelayView`.
- `DevicePusher.cs`: client WS → `/relay/ingest/{guid}`, stream byte file theo chunk, có pacing bytes/giây (giả lập real-time để viewer bắt kịp GOP live).
- `NativeViewerClient.cs`: client WS → `/relay/view/{guid}`, reassemble message, decode bằng `WireProtocolReader`; log init + đếm packet/keyframe; trả `Result` để smoke assert.
- `Program.cs`: subcommand `serve` / `push` / `view` / `smoke`. `smoke` = host + push + view trong 1 process, tự assert (init != null & packets>0 & keyframes>0).
- `wwwroot/index.html`: placeholder (M6 thêm MSE viewer). Sample `sample.ts` link từ test asset, copy cạnh exe.

### Smoke run thật (InProcess) — PASS
`dotnet run -- smoke --mode InProcess`:
- ingest WS → native demux: init `mpegts`, 1 stream Video h264 320x240, extradata 38B (SPS/PPS).
- viewer: 30 packet, 2 keyframe (pts 138000 & 228000), nhận `Control EndOfStream`. RESULT PASS.
- Log `non-existing PPS 0 referenced` từ `av_parser` (parse keyframe không có decoder đầy đủ) — **vô hại**, parser vẫn set `key_frame` đúng (2 keyframe đúng vị trí). Chỉ là debug log của libav.

### Commit
- `<m4-feat>` feat(demo), `f9e3c71` docs

## M5 — Out-of-process demux (supervisor + warm pool + chống orphan) — XONG (smoke PASS)

### Đã làm
- `Worker.cpp`: thêm chống orphan Linux — `prctl(PR_SET_PDEATHSIG, SIGKILL)` (host chết → kernel SIGKILL worker) + check `getppid()==1`.
- `Process/WindowsJobObject.cs`: Job Object `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` (P/Invoke CreateJobObject/SetInformationJobObject/AssignProcessToJobObject/CloseHandle). Đóng handle (host chết) → kill mọi worker → không orphan trên Windows.
- `Process/WorkerHandle.cs`: bọc process worker + stream stdin/stdout; `OnDisposed` callback để supervisor giảm đếm.
- `DemuxWorkerSupervisor.cs`: `Acquire()` lấy worker từ warm pool hoặc spawn mới, tôn trọng `MaxWorkers` (vượt → `DemuxCapacityExceededException`). Warm pool top-up bất đồng bộ (ThreadPool). Windows gán mọi worker vào Job Object.
- `Core/DemuxCapacityExceededException.cs`: factory ném khi vượt cap → AspNetCore map 503.
- `StreamRelayOptions`: thêm `MaxWorkers`, `WarmPoolSize`.
- `OutOfProcessFFmpegDemuxer`: refactor nhận `WorkerHandle` từ supervisor (ctor cũ `(format, path)` spawn standalone cho test). **Fix deadlock**: `OpenAsync` tự đọc frame tới khi nhận `Init` (trước đây chờ `_initTcs` mà init chỉ được đọc trong `ReadPacketAsync` chạy SAU Open → deadlock, 0 packet). Bỏ `_initTcs`.
- `FFmpegStreamDemuxerFactory`: OutOfProcess route qua supervisor singleton (lazy); `Acquire` ném capacity → 503.
- AspNetCore: `TryCreateDemuxer` tạo demuxer TRƯỚC khi upgrade WS → cap → **503** (không phải socket chết). `HandleIngestAsync` nhận demuxer dựng sẵn.

### Smoke OutOfProcess — PASS
`smoke --mode OutOfProcess`: init mpegts h264, 30 packet, 2 keyframe — y hệt InProcess. Worker process + supervisor + bridge stdin/stdout chạy thật end-to-end.

### Test (22 pass, 0 skip)
- `DemuxWorkerSupervisorTests`: Acquire vượt MaxWorkers=2 → `DemuxCapacityExceededException`; release 1 slot → acquire lại OK; unlimited (0) không ném.

### Commit
(ghi sau khi commit)
