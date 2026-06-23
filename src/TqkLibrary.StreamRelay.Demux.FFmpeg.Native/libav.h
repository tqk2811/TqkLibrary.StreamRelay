#ifndef _H_libav_H_
#define _H_libav_H_
#include <cstdio>
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/avutil.h>
#include <libavutil/error.h>
}

// Thread-local last FFmpeg error code, surfaced to managed code through
// Demux_GetLastError(). 0 means "no error".
inline thread_local int g_lastAvError = 0;

inline int CheckErr(int err) {
    if (err < 0)
    {
        g_lastAvError = err;
#if defined(_DEBUG) || !defined(NDEBUG)
        char buffer[AV_ERROR_MAX_STRING_SIZE]{};
        av_strerror(err, buffer, sizeof(buffer));
        std::printf("[StreamRelay.Demux] av error %d: %s\n", err, buffer);
#endif
    }
    return err;
}
#endif // libav_H
