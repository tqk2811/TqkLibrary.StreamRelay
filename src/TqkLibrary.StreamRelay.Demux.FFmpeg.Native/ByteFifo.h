#ifndef _H_ByteFifo_H_
#define _H_ByteFifo_H_

#include <cstdint>
#include <cstddef>
#include <deque>
#include <mutex>
#include <condition_variable>

// A thread-safe byte FIFO feeding the custom AVIO read callback. The ingest
// thread pushes container bytes; the demux thread's read callback blocks in
// Read() until bytes are available, EOF is signalled, or the FIFO is aborted.
class ByteFifo {
public:
    ByteFifo() = default;

    // Append bytes. Returns the count accepted (always len unless aborted).
    int Push(const uint8_t* data, int len) {
        if (!data || len <= 0)
            return 0;
        {
            std::lock_guard<std::mutex> lock(_mutex);
            if (_aborted)
                return 0;
            _buffer.insert(_buffer.end(), data, data + len);
        }
        _cv.notify_all();
        return len;
    }

    // Block until at least one byte is available (then copy up to maxLen), or
    // until EOF/abort. Returns the number of bytes copied; 0 means EOF/abort.
    int Read(uint8_t* dst, int maxLen) {
        if (!dst || maxLen <= 0)
            return 0;
        std::unique_lock<std::mutex> lock(_mutex);
        _cv.wait(lock, [this] { return !_buffer.empty() || _eof || _aborted; });
        if (_aborted)
            return 0;
        if (_buffer.empty())
            return 0; // EOF with no remaining data

        int n = static_cast<int>(std::min<size_t>(static_cast<size_t>(maxLen), _buffer.size()));
        for (int i = 0; i < n; i++)
            dst[i] = _buffer[i];
        _buffer.erase(_buffer.begin(), _buffer.begin() + n);
        return n;
    }

    // Mark end of input; pending and future reads drain remaining bytes then return 0.
    void SignalEof() {
        {
            std::lock_guard<std::mutex> lock(_mutex);
            _eof = true;
        }
        _cv.notify_all();
    }

    // Wake every blocked reader and refuse further data (used on teardown).
    void Abort() {
        {
            std::lock_guard<std::mutex> lock(_mutex);
            _aborted = true;
            _buffer.clear();
        }
        _cv.notify_all();
    }

    size_t Size() {
        std::lock_guard<std::mutex> lock(_mutex);
        return _buffer.size();
    }

private:
    std::mutex _mutex;
    std::condition_variable _cv;
    std::deque<uint8_t> _buffer;
    bool _eof = false;
    bool _aborted = false;
};

#endif // _H_ByteFifo_H_
