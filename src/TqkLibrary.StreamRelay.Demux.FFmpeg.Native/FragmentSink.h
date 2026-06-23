#ifndef _H_FragmentSink_H_
#define _H_FragmentSink_H_

#include <cstdint>
#include <vector>

// Accumulates bytes written by the muxer's custom AVIO write callback. The
// managed side drains the buffer after each header/packet write to get the
// init segment (ftyp+moov) and subsequent fragments (moof+mdat).
class FragmentSink {
public:
    void Append(const uint8_t* data, int len) {
        if (data && len > 0)
            _buffer.insert(_buffer.end(), data, data + len);
    }

    // Move out the accumulated bytes, leaving the sink empty.
    std::vector<uint8_t> Take() {
        std::vector<uint8_t> out;
        out.swap(_buffer);
        return out;
    }

    size_t Size() const { return _buffer.size(); }

private:
    std::vector<uint8_t> _buffer;
};

#endif // _H_FragmentSink_H_
