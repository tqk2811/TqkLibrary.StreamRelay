#ifndef _H_Demuxer_H_
#define _H_Demuxer_H_

#include <string>
#include <vector>
#include "libav.h"
#include "ByteFifo.h"
#include "DemuxInterop.h"

// One demuxing session driven by a custom AVIO read callback fed from a
// thread-safe byte FIFO. Bytes arrive via PushBytes (ingest thread); Open probes
// the container; ReadPacket pulls AVPackets (demux thread). Keyframe detection
// prefers AV_PKT_FLAG_KEY and falls back to an av_parser per video stream.
class Demuxer {
public:
    explicit Demuxer(const char* formatName);
    ~Demuxer();

    // Feed container bytes (ingest thread). Returns bytes accepted.
    int PushBytes(const uint8_t* data, int len);

    // Signal that no more bytes will arrive.
    void SignalEof();

    // avformat_open_input over the custom AVIO + avformat_find_stream_info.
    // Returns 0 on success, a negative AVERROR otherwise.
    int Open();

    // Fill the init struct (valid until destruction). Returns 0 on success.
    int GetInit(MediaInitOut* out);

    // Read the next packet (demux thread). Returns 1 on a packet, 0 at EOF,
    // negative AVERROR on error. On Windows the av_read_frame body is wrapped in
    // SEH by the Exports layer; this method assumes a benign environment.
    int ReadPacket(PacketOut* out);

private:
    static int ReadCallback(void* opaque, uint8_t* buf, int buf_size);

    void BuildStreamInfo();
    bool IsParsedKeyframe(int stream_index, AVPacket* pkt);

    std::string _formatName;
    ByteFifo _fifo;

    AVFormatContext* _fmt = nullptr;
    AVIOContext*     _avio = nullptr;
    uint8_t*         _avioBuffer = nullptr;
    AVPacket*        _pkt = nullptr;

    // Owned init storage (pointers handed to managed must outlive each call).
    std::string                 _formatNameOut;
    std::vector<StreamInfoOut>  _streams;
    std::vector<std::string>    _codecNames;       // backing storage for codec_name
    std::vector<std::vector<uint8_t>> _extradata;  // backing storage for extradata

    // Per-stream parser fallback for keyframe detection (index -> parser/ctx).
    std::vector<AVCodecParserContext*> _parsers;
    std::vector<AVCodecContext*>       _parserCtxs;

    bool _opened = false;
};

#endif // _H_Demuxer_H_
