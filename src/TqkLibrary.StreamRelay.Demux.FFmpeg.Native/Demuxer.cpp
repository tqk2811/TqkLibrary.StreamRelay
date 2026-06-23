#include "Demuxer.h"
#include <cstring>

// Size of the AVIO read buffer FFmpeg pulls through our callback.
static const int kAvioBufferSize = 32 * 1024;

Demuxer::Demuxer(const char* formatName)
    : _formatName(formatName ? formatName : "") {
}

Demuxer::~Demuxer() {
    _fifo.Abort();

    if (_pkt)
        av_packet_free(&_pkt);

    for (auto* p : _parsers)
        if (p) av_parser_close(p);
    _parsers.clear();
    for (auto* c : _parserCtxs)
        if (c) avcodec_free_context(&c);
    _parserCtxs.clear();

    if (_fmt) {
        // With custom IO, avformat_close_input frees the format context but not
        // our AVIO context/buffer; free those ourselves afterwards.
        avformat_close_input(&_fmt);
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

int Demuxer::PushBytes(const uint8_t* data, int len) {
    return _fifo.Push(data, len);
}

void Demuxer::SignalEof() {
    _fifo.SignalEof();
}

int Demuxer::ReadCallback(void* opaque, uint8_t* buf, int buf_size) {
    Demuxer* self = static_cast<Demuxer*>(opaque);
    int n = self->_fifo.Read(buf, buf_size);
    if (n <= 0)
        return AVERROR_EOF; // no more data
    return n;
}

int Demuxer::Open() {
    g_lastAvError = 0;

    _avioBuffer = static_cast<uint8_t*>(av_malloc(kAvioBufferSize));
    if (!_avioBuffer)
        return AVERROR(ENOMEM);

    _avio = avio_alloc_context(
        _avioBuffer, kAvioBufferSize,
        0,            // write_flag = 0 (read only)
        this,         // opaque
        &Demuxer::ReadCallback,
        nullptr,      // no write
        nullptr);     // no seek (streamed input)
    if (!_avio) {
        av_freep(&_avioBuffer);
        return AVERROR(ENOMEM);
    }

    _fmt = avformat_alloc_context();
    if (!_fmt)
        return AVERROR(ENOMEM);

    _fmt->pb = _avio;
    _fmt->flags |= AVFMT_FLAG_CUSTOM_IO;

    // Pick the demuxer explicitly when a format hint was provided. This avoids
    // ambiguous probing and shrinks the attack surface.
    const AVInputFormat* inputFormat = nullptr;
    if (!_formatName.empty())
        inputFormat = av_find_input_format(_formatName.c_str());

    int err = CheckErr(avformat_open_input(&_fmt, nullptr, inputFormat, nullptr));
    if (err < 0)
        return err;

    err = CheckErr(avformat_find_stream_info(_fmt, nullptr));
    if (err < 0)
        return err;

    BuildStreamInfo();

    _pkt = av_packet_alloc();
    if (!_pkt)
        return AVERROR(ENOMEM);

    _opened = true;
    return 0;
}

void Demuxer::BuildStreamInfo() {
    unsigned int n = _fmt->nb_streams;
    _streams.resize(n);
    _codecNames.resize(n);
    _extradata.resize(n);
    _parsers.assign(n, nullptr);
    _parserCtxs.assign(n, nullptr);

    for (unsigned int i = 0; i < n; i++) {
        AVStream* st = _fmt->streams[i];
        AVCodecParameters* cp = st->codecpar;

        StreamInfoOut& s = _streams[i];
        s.index = static_cast<int32_t>(i);
        s.codec_type = static_cast<int32_t>(cp->codec_type);
        s.codec_id = static_cast<int32_t>(cp->codec_id);
        s.width = cp->width;
        s.height = cp->height;
        s.sample_rate = cp->sample_rate;
        s.channels = cp->ch_layout.nb_channels;
        s.time_base_num = st->time_base.num;
        s.time_base_den = st->time_base.den;

        if (cp->extradata && cp->extradata_size > 0) {
            _extradata[i].assign(cp->extradata, cp->extradata + cp->extradata_size);
        } else {
            _extradata[i].clear();
        }
        s.extradata_size = static_cast<int32_t>(_extradata[i].size());
        s.extradata = _extradata[i].empty() ? nullptr : _extradata[i].data();

        const char* name = avcodec_get_name(cp->codec_id);
        _codecNames[i] = name ? name : "";
        s.codec_name = _codecNames[i].c_str();

        // Set up a keyframe-detection parser for video streams (fallback when the
        // container does not flag keyframes, e.g. raw elementary streams).
        if (cp->codec_type == AVMEDIA_TYPE_VIDEO) {
            const AVCodec* dec = avcodec_find_decoder(cp->codec_id);
            if (dec) {
                AVCodecParserContext* parser = av_parser_init(dec->id);
                if (parser) {
                    parser->flags |= PARSER_FLAG_COMPLETE_FRAMES;
                    AVCodecContext* ctx = avcodec_alloc_context3(dec);
                    if (ctx) {
                        ctx->flags |= AV_CODEC_FLAG_LOW_DELAY;
                        _parsers[i] = parser;
                        _parserCtxs[i] = ctx;
                    } else {
                        av_parser_close(parser);
                    }
                }
            }
        }
    }
}

bool Demuxer::IsParsedKeyframe(int stream_index, AVPacket* pkt) {
    if (stream_index < 0 || stream_index >= static_cast<int>(_parsers.size()))
        return false;
    AVCodecParserContext* parser = _parsers[stream_index];
    AVCodecContext* ctx = _parserCtxs[stream_index];
    if (!parser || !ctx)
        return false;

    uint8_t* out_data = nullptr;
    int out_len = 0;
    av_parser_parse2(parser, ctx, &out_data, &out_len,
                     pkt->data, pkt->size,
                     AV_NOPTS_VALUE, AV_NOPTS_VALUE, -1);
    return parser->key_frame != 0;
}

int Demuxer::GetInit(MediaInitOut* out) {
    if (!out || !_opened)
        return AVERROR(EINVAL);

    _formatNameOut = (_fmt && _fmt->iformat && _fmt->iformat->name) ? _fmt->iformat->name : _formatName;
    out->format_name = _formatNameOut.empty() ? nullptr : _formatNameOut.c_str();
    out->stream_count = static_cast<int32_t>(_streams.size());
    out->streams = _streams.empty() ? nullptr : _streams.data();
    return 0;
}

int Demuxer::ReadPacket(PacketOut* out) {
    if (!out || !_opened || !_pkt)
        return AVERROR(EINVAL);

    g_lastAvError = 0;
    av_packet_unref(_pkt); // release the previous packet's data

    int err = av_read_frame(_fmt, _pkt);
    if (err == AVERROR_EOF)
        return 0; // end of stream
    if (err < 0) {
        CheckErr(err);
        return err;
    }

    int si = _pkt->stream_index;
    bool keyframe = (_pkt->flags & AV_PKT_FLAG_KEY) != 0;

    // For video without a container keyframe flag, consult the parser.
    if (!keyframe && si >= 0 && si < static_cast<int>(_fmt->nb_streams)) {
        AVCodecParameters* cp = _fmt->streams[si]->codecpar;
        if (cp->codec_type == AVMEDIA_TYPE_VIDEO)
            keyframe = IsParsedKeyframe(si, _pkt);
    }

    out->stream_index = si;
    out->is_keyframe = keyframe ? 1 : 0;
    out->pts = _pkt->pts;
    out->dts = _pkt->dts;
    out->duration = static_cast<int32_t>(_pkt->duration);
    out->size = _pkt->size;
    out->data = _pkt->data;
    return 1;
}
