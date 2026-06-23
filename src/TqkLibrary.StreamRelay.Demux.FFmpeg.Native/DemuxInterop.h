#ifndef _H_DemuxInterop_H_
#define _H_DemuxInterop_H_

#include <cstdint>

// Plain-old-data structs marshalled across the C ABI. Layout must match the
// managed [StructLayout(LayoutKind.Sequential)] mirrors in NativeWrapper.cs.
#pragma pack(push, 8)

// One elementary stream's metadata. Pointers (codec_name, extradata) point into
// memory owned by the demuxer instance and are valid until Demux_Free.
struct StreamInfoOut {
    int32_t  index;
    int32_t  codec_type;     // AVMediaType
    int32_t  codec_id;       // AVCodecID
    int32_t  width;
    int32_t  height;
    int32_t  sample_rate;
    int32_t  channels;
    int32_t  time_base_num;
    int32_t  time_base_den;
    int32_t  extradata_size;
    const uint8_t* extradata;
    const char*    codec_name;
};

// The container init: format name + a pointer to an array of StreamInfoOut.
struct MediaInitOut {
    const char*          format_name;
    int32_t              stream_count;
    const StreamInfoOut* streams;
};

// One demuxed packet. data points into FFmpeg-owned memory valid only until the
// next Demux_ReadPacket / Demux_Free; managed copies it immediately.
struct PacketOut {
    int32_t  stream_index;
    int32_t  is_keyframe;   // 0/1
    int64_t  pts;
    int64_t  dts;
    int32_t  duration;
    int32_t  size;
    const uint8_t* data;
};

#pragma pack(pop)

#endif // _H_DemuxInterop_H_
