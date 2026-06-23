#ifndef _H_Muxer_H_
#define _H_Muxer_H_

#include <vector>
#include "libav.h"
#include "FragmentSink.h"
#include "MuxInterop.h"

// Fragmented-MP4 muxer: takes already-demuxed packets and remuxes them into an
// fMP4 byte stream (movflags=frag_keyframe+empty_moov+default_base_moof) through
// a custom write-only AVIO. WriteHeader yields the init segment (ftyp+moov);
// WritePacket yields a fragment (moof+mdat). Remuxing valid packets is far safer
// than demuxing untrusted input, so no SEH guard is needed here.
class Muxer {
public:
    Muxer();
    ~Muxer();

    // Add an output stream. Must be called for every input stream before WriteHeader.
    int AddStream(const MuxStreamIn* in);

    // Write the fMP4 header; returns the init segment via out_data/out_len (owned
    // by this Muxer, valid until the next call). Returns 0 on success.
    int WriteHeader(const uint8_t** out_data, int* out_len);

    // Mux one packet; returns any produced fragment bytes via out_data/out_len.
    int WritePacket(const MuxPacketIn* in, const uint8_t** out_data, int* out_len);

    // Flush the trailer (optional). Returns trailing bytes.
    int WriteTrailer(const uint8_t** out_data, int* out_len);

private:
    static int WriteCallback(void* opaque, const uint8_t* buf, int buf_size);

    AVFormatContext* _fmt = nullptr;
    AVIOContext*     _avio = nullptr;
    uint8_t*         _avioBuffer = nullptr;
    FragmentSink     _sink;
    std::vector<uint8_t> _last;     // backing storage for the last returned fragment
    std::vector<int> _streamMap;    // input stream index -> output stream index
    bool _headerWritten = false;
};

#endif // _H_Muxer_H_
