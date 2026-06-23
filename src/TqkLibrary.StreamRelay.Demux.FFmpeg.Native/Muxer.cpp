#include "Muxer.h"
#include <cstring>

static const int kMuxAvioBufferSize = 32 * 1024;

Muxer::Muxer() {
}

Muxer::~Muxer() {
    if (_fmt) {
        avformat_free_context(_fmt);
        _fmt = nullptr;
    }
    if (_avio) {
        if (_avio->buffer) {
            av_freep(&_avio->buffer);
            _avioBuffer = nullptr;
        }
        avio_context_free(&_avio);
    }
    if (_avioBuffer)
        av_freep(&_avioBuffer);
}

int Muxer::WriteCallback(void* opaque, const uint8_t* buf, int buf_size) {
    Muxer* self = static_cast<Muxer*>(opaque);
    self->_sink.Append(buf, buf_size);
    return buf_size;
}

int Muxer::AddStream(const MuxStreamIn* in) {
    if (!in)
        return AVERROR(EINVAL);

    if (!_fmt) {
        int err = CheckErr(avformat_alloc_output_context2(&_fmt, nullptr, "mp4", nullptr));
        if (err < 0 || !_fmt)
            return err < 0 ? err : AVERROR(ENOMEM);
    }

    AVStream* st = avformat_new_stream(_fmt, nullptr);
    if (!st)
        return AVERROR(ENOMEM);

    AVCodecParameters* cp = st->codecpar;
    cp->codec_type = static_cast<AVMediaType>(in->codec_type);
    cp->codec_id = static_cast<AVCodecID>(in->codec_id);
    cp->width = in->width;
    cp->height = in->height;
    cp->sample_rate = in->sample_rate;
    av_channel_layout_default(&cp->ch_layout, in->channels > 0 ? in->channels : 0);

    if (in->extradata && in->extradata_size > 0) {
        cp->extradata = static_cast<uint8_t*>(av_mallocz(in->extradata_size + AV_INPUT_BUFFER_PADDING_SIZE));
        if (!cp->extradata)
            return AVERROR(ENOMEM);
        std::memcpy(cp->extradata, in->extradata, in->extradata_size);
        cp->extradata_size = in->extradata_size;
    }

    st->time_base.num = in->time_base_num > 0 ? in->time_base_num : 1;
    st->time_base.den = in->time_base_den > 0 ? in->time_base_den : 90000;

    _streamMap.push_back(static_cast<int>(_fmt->nb_streams) - 1);
    return 0;
}

int Muxer::WriteHeader(const uint8_t** out_data, int* out_len) {
    if (!_fmt || _headerWritten)
        return AVERROR(EINVAL);

    _avioBuffer = static_cast<uint8_t*>(av_malloc(kMuxAvioBufferSize));
    if (!_avioBuffer)
        return AVERROR(ENOMEM);

    _avio = avio_alloc_context(
        _avioBuffer, kMuxAvioBufferSize,
        1,            // write_flag
        this,
        nullptr,      // no read
        &Muxer::WriteCallback,
        nullptr);     // no seek
    if (!_avio) {
        av_freep(&_avioBuffer);
        return AVERROR(ENOMEM);
    }
    _fmt->pb = _avio;
    _fmt->flags |= AVFMT_FLAG_CUSTOM_IO;

    // Fragmented MP4 so the browser MSE can append init + media segments incrementally.
    AVDictionary* opts = nullptr;
    av_dict_set(&opts, "movflags", "frag_keyframe+empty_moov+default_base_moof+separate_moof", 0);

    int err = CheckErr(avformat_write_header(_fmt, &opts));
    av_dict_free(&opts);
    if (err < 0)
        return err;

    avio_flush(_avio);
    _last = _sink.Take();
    *out_data = _last.empty() ? nullptr : _last.data();
    *out_len = static_cast<int>(_last.size());
    _headerWritten = true;
    return 0;
}

int Muxer::WritePacket(const MuxPacketIn* in, const uint8_t** out_data, int* out_len) {
    if (!in || !_fmt || !_headerWritten)
        return AVERROR(EINVAL);
    if (in->stream_index < 0 || in->stream_index >= static_cast<int>(_streamMap.size()))
        return AVERROR(EINVAL);

    AVPacket* pkt = av_packet_alloc();
    if (!pkt)
        return AVERROR(ENOMEM);

    // Reference the caller's data without copying; av_interleaved_write_frame
    // consumes it. Use a copy via av_new_packet to be safe across the boundary.
    if (in->size > 0) {
        int err = av_new_packet(pkt, in->size);
        if (err < 0) {
            av_packet_free(&pkt);
            return err;
        }
        std::memcpy(pkt->data, in->data, in->size);
    }

    int outIndex = _streamMap[in->stream_index];
    pkt->stream_index = outIndex;
    pkt->pts = in->pts;
    pkt->dts = in->dts;
    pkt->duration = in->duration;
    pkt->flags = in->is_keyframe ? AV_PKT_FLAG_KEY : 0;

    int err = CheckErr(av_interleaved_write_frame(_fmt, pkt));
    av_packet_free(&pkt);
    if (err < 0)
        return err;

    avio_flush(_avio);
    _last = _sink.Take();
    *out_data = _last.empty() ? nullptr : _last.data();
    *out_len = static_cast<int>(_last.size());
    return 0;
}

int Muxer::WriteTrailer(const uint8_t** out_data, int* out_len) {
    if (!_fmt || !_headerWritten)
        return AVERROR(EINVAL);
    int err = CheckErr(av_write_trailer(_fmt));
    if (err < 0)
        return err;
    avio_flush(_avio);
    _last = _sink.Take();
    *out_data = _last.empty() ? nullptr : _last.data();
    *out_len = static_cast<int>(_last.size());
    return 0;
}
