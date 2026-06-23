#ifndef _H_TqkLibraryStreamRelayDemuxFFmpegNative_H_
#define _H_TqkLibraryStreamRelayDemuxFFmpegNative_H_

#include "platform.h"
#include "DemuxInterop.h"

class Demuxer;

// Allocate a demuxer for the given (optional) container format hint.
DLL_EXPORT Demuxer* Demux_Alloc(const char* formatName);

// Feed container bytes (ingest thread). Returns the number of bytes accepted.
DLL_EXPORT int Demux_PushBytes(Demuxer* demuxer, const uint8_t* data, int len);

// Signal that no more bytes will arrive.
DLL_EXPORT void Demux_SignalEof(Demuxer* demuxer);

// avformat_open_input over the custom AVIO. Returns 0 on success, negative AVERROR otherwise.
DLL_EXPORT int Demux_Open(Demuxer* demuxer);

// Fill the init struct (valid until Demux_Free). Returns 0 on success.
DLL_EXPORT int Demux_GetInit(Demuxer* demuxer, MediaInitOut* out);

// Read the next packet. Returns 1 on a packet, 0 at EOF, negative on error.
// On Windows the av_read_frame body runs under SEH so an access violation in a
// corrupt stream becomes an error code instead of taking the process down.
DLL_EXPORT int Demux_ReadPacket(Demuxer* demuxer, PacketOut* out);

// Free the demuxer and null the caller's pointer.
DLL_EXPORT void Demux_Free(Demuxer** ppDemuxer);

// Last FFmpeg error captured on the current thread (0 = none).
DLL_EXPORT int Demux_GetLastError();

#endif // !_H_TqkLibraryStreamRelayDemuxFFmpegNative_H_
