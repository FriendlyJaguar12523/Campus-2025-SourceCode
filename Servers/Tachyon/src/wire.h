#pragma once
//
// NGO 1.x wire primitives (re-derived from the
// MIT-licensed com.unity.netcode.gameobjects sources, tag ngo/1.14.1).
//
//   batch   = [u16 magic=0x1160][u16 count][i32 size][u64 hash] then messages (stock layout);
//             the Rec Room fork uses a 24-byte header instead (see tachyon_server.cpp).
//   message = [bitpacked uint type][bitpacked uint size][payload]
//   ints are LITTLE-ENDIAN; signed values are ZigZag-encoded before bit-packing.
//
#include <cstdint>
#include <cstddef>
#include <vector>
#include <string>

namespace wire {

    inline constexpr uint16_t BATCH_MAGIC = 0x1160;
    inline constexpr int BATCH_HEADER_SIZE = 16;

    // hashing wrappers over the official single-header xxHash (seed 0), defined in hashing.cpp.
    uint32_t xxh32(const void* data, size_t len);
    uint64_t xxh64(const void* data, size_t len);

    inline uint32_t message_hash(const std::string& full_name) {
        return xxh32(full_name.data(), full_name.size());
    }
    inline uint64_t hash64(const uint8_t* data, size_t offset, size_t length) {
        return xxh64(data + offset, length);
    }

    // ---- raw little-endian writers (append to a byte vector) ----
    inline void put_u16(std::vector<uint8_t>& b, uint16_t v) {
        b.push_back((uint8_t) v);
        b.push_back((uint8_t) (v >> 8));
    }
    inline void put_u32(std::vector<uint8_t>& b, uint32_t v) {
        for (int i = 0; i < 4; i++) b.push_back((uint8_t) (v >> (i * 8)));
    }
    inline void put_i32(std::vector<uint8_t>& b, int32_t v) { put_u32(b, (uint32_t) v); }
    inline void put_u64(std::vector<uint8_t>& b, uint64_t v) {
        for (int i = 0; i < 8; i++) b.push_back((uint8_t) (v >> (i * 8)));
    }

    // ---- raw little-endian readers (advance pos) ----
    inline uint16_t get_u16(const uint8_t* d, size_t& p) {
        uint16_t v = (uint16_t) (d[p] | (d[p + 1] << 8));
        p += 2;
        return v;
    }
    inline uint32_t get_u32(const uint8_t* d, size_t& p) {
        uint32_t v = (uint32_t) (d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | ((uint32_t) d[p + 3] << 24));
        p += 4;
        return v;
    }
    inline int32_t get_i32(const uint8_t* d, size_t& p) { return (int32_t) get_u32(d, p); }
    inline uint64_t get_u64(const uint8_t* d, size_t& p) {
        uint64_t v = 0;
        for (int i = 0; i < 8; i++) v |= (uint64_t) d[p + i] << (i * 8);
        p += 8;
        return v;
    }

    // ---- ZigZag ----
    inline uint32_t zigzag(int32_t v) { return (uint32_t) ((v << 1) ^ (v >> 31)); }
    inline int32_t unzigzag(uint32_t v) { return (int32_t) (v >> 1) ^ -(int32_t) (v & 1); }

    inline int used_byte_count(uint64_t v) {
        int n = 0;
        do { n++; v >>= 8; } while (v != 0);
        return n;
    }

    // ---- bit-packed integers (BytePacker/ByteUnpacker) ----
    inline void put_packed(std::vector<uint8_t>& b, uint32_t value) {
        if (value > (1u << 29) - 1) {
            b.push_back(5);
            put_u32(b, value);
            return;
        }
        uint64_t v = (uint64_t) value << 3;
        int num = used_byte_count(v);
        v |= (uint32_t) num;
        for (int i = 0; i < num; i++) b.push_back((uint8_t) (v >> (i * 8)));
    }

    inline void put_packed(std::vector<uint8_t>& b, uint64_t value) {
        if (value > (1ULL << 60) - 1) {
            b.push_back(9);
            put_u64(b, value);
            return;
        }
        uint64_t v = value << 4;
        int num = used_byte_count(v);
        v |= (uint32_t) num;
        for (int i = 0; i < num; i++) b.push_back((uint8_t) (v >> (i * 8)));
    }

    inline void put_packed_i32(std::vector<uint8_t>& b, int32_t value) { put_packed(b, zigzag(value)); }

    inline uint32_t get_packed_u32(const uint8_t* d, size_t& p) {
        uint8_t first = d[p];
        int num = first & 0x07;
        if (num == 5) { p++; return get_u32(d, p); }
        uint64_t v = 0;
        for (int i = 0; i < num; i++) v |= (uint64_t) d[p + i] << (i * 8);
        p += num;
        return (uint32_t) (v >> 3);
    }

    inline uint64_t get_packed_u64(const uint8_t* d, size_t& p) {
        uint8_t first = d[p];
        int num = first & 0x0F;
        if (num == 9) { p++; return get_u64(d, p); }
        uint64_t v = 0;
        for (int i = 0; i < num; i++) v |= (uint64_t) d[p + i] << (i * 8);
        p += num;
        return v >> 4;
    }

    inline int32_t get_packed_i32(const uint8_t* d, size_t& p) { return unzigzag(get_packed_u32(d, p)); }

    // ---- bounds-checked readers (for untrusted UDP input) ----
    // `end` is the absolute index one past the last readable byte. Each returns false (leaving p
    // unchanged) rather than reading out of bounds.
    inline bool try_get_u32(const uint8_t* d, size_t& p, size_t end, uint32_t& out) {
        if (p + 4 > end) return false;
        out = get_u32(d, p);
        return true;
    }
    inline bool try_get_packed_u32(const uint8_t* d, size_t& p, size_t end, uint32_t& out) {
        if (p >= end) return false;
        int num = d[p] & 0x07;
        if (num == 5) {
            if (p + 5 > end) return false;
        } else if (p + num > end) {
            return false;
        }
        out = get_packed_u32(d, p);
        return true;
    }
    inline bool try_get_packed_i32(const uint8_t* d, size_t& p, size_t end, int32_t& out) {
        uint32_t u;
        if (!try_get_packed_u32(d, p, end, u)) return false;
        out = unzigzag(u);
        return true;
    }

} // namespace wire
