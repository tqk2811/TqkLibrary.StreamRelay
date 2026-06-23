#ifndef _H_MuxInterop_H_
#define _H_MuxInterop_H_

#include <cstdint>

// POD structs for the fMP4 muxer ABI. Layout must match the managed mirrors.
#pragma pack(push, 8)

// Describes one output stream to add to the fMP4 muxer.
struct MuxStreamIn {
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
};

// One input packet to mux.
struct MuxPacketIn {
    int32_t  stream_index;
    int32_t  is_keyframe;
    int64_t  pts;
    int64_t  dts;
    int32_t  duration;
    int32_t  size;
    const uint8_t* data;
};

#pragma pack(pop)

#endif // _H_MuxInterop_H_
