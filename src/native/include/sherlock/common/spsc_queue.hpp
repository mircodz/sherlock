#pragma once

#include <atomic>
#include <cstddef>
#include <new>
#include <vector>

namespace Sherlock {

inline constexpr std::size_t kCacheLine = 64;

template <class T>
class SpscQueue {
public:
    explicit SpscQueue(std::size_t capacity) : data_(capacity) {}

    SpscQueue(const SpscQueue&) = delete;
    SpscQueue& operator=(const SpscQueue&) = delete;

    /// Producer side. Returns false if the queue is full (caller drops the item).
    bool push(const T& value) {
        std::size_t writeIdx = writeIdx_.load(std::memory_order_relaxed);
        std::size_t nextWrite = writeIdx + 1;
        if (nextWrite == data_.size()) {
            nextWrite = 0;
        }
        if (nextWrite == readIdxCached_) {
            readIdxCached_ = readIdx_.load(std::memory_order_acquire);
            if (nextWrite == readIdxCached_) {
                return false; // full
            }
        }
        data_[writeIdx] = value;
        writeIdx_.store(nextWrite, std::memory_order_release);
        return true;
    }

    /// Consumer side. Returns false if the queue is empty.
    bool pop(T& out) {
        std::size_t readIdx = readIdx_.load(std::memory_order_relaxed);
        if (readIdx == writeIdxCached_) {
            writeIdxCached_ = writeIdx_.load(std::memory_order_acquire);
            if (readIdx == writeIdxCached_) {
                return false; // empty
            }
        }
        out = data_[readIdx];
        std::size_t nextRead = readIdx + 1;
        if (nextRead == data_.size()) {
            nextRead = 0;
        }
        readIdx_.store(nextRead, std::memory_order_release);
        return true;
    }

private:
    std::vector<T> data_;
    alignas(kCacheLine) std::atomic<std::size_t> readIdx_{0};
    alignas(kCacheLine) std::size_t writeIdxCached_{0};
    alignas(kCacheLine) std::atomic<std::size_t> writeIdx_{0};
    alignas(kCacheLine) std::size_t readIdxCached_{0};
};

} // namespace Sherlock
