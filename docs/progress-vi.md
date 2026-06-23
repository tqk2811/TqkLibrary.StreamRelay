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
(ghi sau khi commit)
