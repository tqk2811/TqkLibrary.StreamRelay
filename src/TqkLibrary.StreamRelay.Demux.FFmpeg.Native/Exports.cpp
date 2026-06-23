#include "Exports.h"
#include "Demuxer.h"
#include "libav.h"

// On Windows, isolate the packet read under SEH so an access violation from a
// corrupt/hostile stream is converted into an error code (only this stream dies,
// the host survives). The SEH frame must not own C++ objects that need unwinding,
// so it does nothing but forward to Demuxer::ReadPacket.
#ifdef _WIN32
#include <windows.h>
static int ReadPacketGuarded(Demuxer* demuxer, PacketOut* out) {
    __try {
        return demuxer->ReadPacket(out);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        g_lastAvError = AVERROR_EXTERNAL;
        return AVERROR_EXTERNAL;
    }
}
#else
static int ReadPacketGuarded(Demuxer* demuxer, PacketOut* out) {
    return demuxer->ReadPacket(out);
}
#endif

Demuxer* Demux_Alloc(const char* formatName) {
    g_lastAvError = 0;
    return new (std::nothrow) Demuxer(formatName);
}

int Demux_PushBytes(Demuxer* demuxer, const uint8_t* data, int len) {
    if (!demuxer)
        return 0;
    return demuxer->PushBytes(data, len);
}

void Demux_SignalEof(Demuxer* demuxer) {
    if (demuxer)
        demuxer->SignalEof();
}

int Demux_Open(Demuxer* demuxer) {
    if (!demuxer)
        return AVERROR(EINVAL);
    return demuxer->Open();
}

int Demux_GetInit(Demuxer* demuxer, MediaInitOut* out) {
    if (!demuxer)
        return AVERROR(EINVAL);
    return demuxer->GetInit(out);
}

int Demux_ReadPacket(Demuxer* demuxer, PacketOut* out) {
    if (!demuxer)
        return AVERROR(EINVAL);
    return ReadPacketGuarded(demuxer, out);
}

void Demux_Free(Demuxer** ppDemuxer) {
    if (ppDemuxer && *ppDemuxer) {
        delete *ppDemuxer;
        *ppDemuxer = nullptr;
    }
}

int Demux_GetLastError() {
    return g_lastAvError;
}
