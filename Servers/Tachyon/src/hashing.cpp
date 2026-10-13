// The single translation unit that compiles the official xxHash implementation, and the thin
// seed-0 wrappers the rest of Tachyon calls. Keeping XXH_IMPLEMENTATION here (and nowhere else)
// means the ~6k-line header is compiled exactly once.
#define XXH_IMPLEMENTATION
#define XXH_STATIC_LINKING_ONLY
#include "xxhash.h"

#include "wire.h"

namespace wire {
    uint32_t xxh32(const void* data, size_t len) { return (uint32_t) XXH32(data, len, 0); }
    uint64_t xxh64(const void* data, size_t len) { return (uint64_t) XXH64(data, len, 0); }
}
