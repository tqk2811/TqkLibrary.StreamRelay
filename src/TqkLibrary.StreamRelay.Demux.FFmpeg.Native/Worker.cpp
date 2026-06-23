// Out-of-process demux worker (M5). Links the demux core directly and bridges
// it to the host over stdin/stdout with a tiny length-prefixed binary protocol,
// so a crash here (corrupt/hostile stream) takes down only this process.
//
// Host -> worker (stdin) frames:   [u8 cmd][i32 len][payload]
//   cmd 1 = PushBytes  (payload = container bytes)
//   cmd 2 = SignalEof  (len 0)
//   cmd 3 = Open       (len 0)        -> worker replies Init or Error, then starts the read loop
//
// Worker -> host (stdout) frames:  [u8 type][i32 len][payload]
//   type 1 = Init    (payload = serialised MediaInit, same layout as the AspNetCore wire init body)
//   type 2 = Packet  (payload = [i32 streamIndex][u8 keyframe][i64 pts][i64 dts][i32 duration][i32 size][data])
//   type 3 = Eof     (len 0)
//   type 4 = Error   (payload = [i32 averror])
//
// Multi-byte integers are little-endian. Bytes flow in on the stdin thread while
// the read loop pulls packets on the main thread once Open succeeds.

#include "Demuxer.h"
#include "libav.h"
#include <cstdio>
#include <cstring>
#include <cstdint>
#include <thread>
#include <atomic>
#include <vector>
#include <mutex>

#ifdef _WIN32
#include <io.h>
#include <fcntl.h>
#endif

static std::mutex g_outMutex;

static bool ReadFull(FILE* f, void* buf, size_t n) {
    size_t got = std::fread(buf, 1, n, f);
    return got == n;
}

static void WriteLE32(uint8_t* p, int32_t v) { std::memcpy(p, &v, 4); }
static void WriteLE64(uint8_t* p, int64_t v) { std::memcpy(p, &v, 8); }

static void WriteFrame(uint8_t type, const uint8_t* payload, int32_t len) {
    std::lock_guard<std::mutex> lock(g_outMutex);
    uint8_t header[5];
    header[0] = type;
    WriteLE32(header + 1, len);
    std::fwrite(header, 1, 5, stdout);
    if (len > 0 && payload)
        std::fwrite(payload, 1, static_cast<size_t>(len), stdout);
    std::fflush(stdout);
}

static void WriteError(int32_t averror) {
    uint8_t buf[4];
    WriteLE32(buf, averror);
    WriteFrame(4, buf, 4);
}

// Serialise init in the same layout as WireProtocol.BuildInitFrame's body
// (without the 4-byte version/type header, which the managed side re-adds).
static std::vector<uint8_t> SerializeInit(const MediaInitOut* init) {
    std::vector<uint8_t> out;
    auto put8 = [&](uint8_t v) { out.push_back(v); };
    auto put16 = [&](uint16_t v) { out.push_back((uint8_t)(v & 0xFF)); out.push_back((uint8_t)((v >> 8) & 0xFF)); };
    auto put32 = [&](int32_t v) { uint8_t b[4]; WriteLE32(b, v); out.insert(out.end(), b, b + 4); };
    auto putBytes = [&](const uint8_t* p, int n) { if (n > 0 && p) out.insert(out.end(), p, p + n); };

    const char* fmt = init->format_name ? init->format_name : "";
    uint16_t fmtLen = (uint16_t)std::strlen(fmt);
    put16(fmtLen);
    putBytes((const uint8_t*)fmt, fmtLen);

    put8((uint8_t)init->stream_count);
    for (int i = 0; i < init->stream_count; i++) {
        const StreamInfoOut& s = init->streams[i];
        // Map AVMediaType -> MediaCodecKind (Video=1, Audio=2, Subtitle=3, Data=4, else 0).
        uint8_t kind = 0;
        switch (s.codec_type) {
            case AVMEDIA_TYPE_VIDEO: kind = 1; break;
            case AVMEDIA_TYPE_AUDIO: kind = 2; break;
            case AVMEDIA_TYPE_SUBTITLE: kind = 3; break;
            case AVMEDIA_TYPE_DATA: kind = 4; break;
            default: kind = 0; break;
        }
        put8((uint8_t)(s.index & 0xFF));
        put8(kind);
        put32(s.codec_id);
        const char* cn = s.codec_name ? s.codec_name : "";
        uint16_t cnLen = (uint16_t)std::strlen(cn);
        put16(cnLen);
        putBytes((const uint8_t*)cn, cnLen);
        put32(s.width);
        put32(s.height);
        put32(s.sample_rate);
        put32(s.channels);
        put32(s.time_base_num);
        put32(s.time_base_den);
        put32(s.extradata_size);
        putBytes(s.extradata, s.extradata_size);
    }
    return out;
}

static void WritePacket(const PacketOut* pkt) {
    int32_t headerLen = 4 + 1 + 8 + 8 + 4 + 4; // streamIndex,key,pts,dts,duration,size
    int32_t total = headerLen + pkt->size;
    std::vector<uint8_t> buf(total);
    uint8_t* p = buf.data();
    WriteLE32(p, pkt->stream_index); p += 4;
    *p++ = (uint8_t)(pkt->is_keyframe ? 1 : 0);
    WriteLE64(p, pkt->pts); p += 8;
    WriteLE64(p, pkt->dts); p += 8;
    WriteLE32(p, pkt->duration); p += 4;
    WriteLE32(p, pkt->size); p += 4;
    if (pkt->size > 0 && pkt->data)
        std::memcpy(p, pkt->data, (size_t)pkt->size);
    WriteFrame(2, buf.data(), total);
}

int main(int argc, char** argv) {
#ifdef _WIN32
    // Binary stdio so byte streams are not mangled by CRLF translation.
    _setmode(_fileno(stdin), _O_BINARY);
    _setmode(_fileno(stdout), _O_BINARY);
#endif

    const char* formatName = (argc > 1 && argv[1] && argv[1][0]) ? argv[1] : nullptr;
    Demuxer demuxer(formatName);

    std::atomic<bool> opened{false};
    std::atomic<bool> stop{false};

    // stdin thread: feed commands into the demuxer.
    std::thread stdinThread([&]() {
        std::vector<uint8_t> payload;
        while (!stop.load()) {
            uint8_t header[5];
            if (!ReadFull(stdin, header, 5)) { stop.store(true); break; }
            uint8_t cmd = header[0];
            int32_t len; std::memcpy(&len, header + 1, 4);
            payload.resize(len > 0 ? (size_t)len : 0);
            if (len > 0 && !ReadFull(stdin, payload.data(), (size_t)len)) { stop.store(true); break; }

            switch (cmd) {
                case 1: // PushBytes
                    if (len > 0)
                        demuxer.PushBytes(payload.data(), len);
                    break;
                case 2: // SignalEof
                    demuxer.SignalEof();
                    break;
                case 3: // Open
                    opened.store(true);
                    break;
                default:
                    break;
            }
        }
        demuxer.SignalEof();
    });

    // Wait until Open is requested (Push usually precedes it so bytes are ready).
    while (!opened.load() && !stop.load())
        std::this_thread::sleep_for(std::chrono::milliseconds(2));

    if (!stop.load()) {
        int err = demuxer.Open();
        if (err < 0) {
            WriteError(err);
            stop.store(true);
        } else {
            MediaInitOut init{};
            if (demuxer.GetInit(&init) == 0) {
                std::vector<uint8_t> body = SerializeInit(&init);
                WriteFrame(1, body.data(), (int32_t)body.size());
            }
            // Read loop on the main thread.
            PacketOut pkt{};
            while (!stop.load()) {
                int r = demuxer.ReadPacket(&pkt);
                if (r == 1) {
                    WritePacket(&pkt);
                } else if (r == 0) {
                    WriteFrame(3, nullptr, 0); // EOF
                    break;
                } else {
                    WriteError(r);
                    break;
                }
            }
        }
    }

    stop.store(true);
    if (stdinThread.joinable())
        stdinThread.join();
    return 0;
}
