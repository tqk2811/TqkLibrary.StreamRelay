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
(sẽ ghi hash sau khi commit)
