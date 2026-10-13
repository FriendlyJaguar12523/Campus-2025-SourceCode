#pragma once
#include <string>
#include <vector>
#include <stdint.h>

// Converts a pattern string like "FF 23 ?? 45" into a vector of integers.
// '?' wildcards become -69 (matches any byte).
static std::vector<int> PatternToIntVector(const char* Pattern)
{
    std::vector<int> PatternVector = std::vector<int>{};

    char* Start = const_cast<char*>(Pattern);
    char* End = const_cast<char*>(Pattern) + strlen(Pattern);

    for (char* Current = Start; Current < End; ++Current)
    {
        if (*Current == '?')
        {
            ++Current;
            PatternVector.push_back(-69);  // Wildcard
        }
        else
        {
            PatternVector.push_back(strtoul(Current, &Current, 16));
        }
    }

    return PatternVector;
}

// Follows a RIP-relative pointer at Address+i and returns the target address.
// Used when bFindPointer is true in PatternScan.
static inline void* FindPointer(std::vector<int> Pattern, uint8_t* Address, int Addition = 0)
{
    int i = 0;
    while (Pattern[i] != -69) i++;

    int Offset = *reinterpret_cast<int*>(Address + i);
    return reinterpret_cast<void*>(Address + i + 0x4 + Offset + Addition);
}

// Scans memory for a byte pattern and returns the address of the match.
//
// Parameters:
//   Pattern      - Hex string with '?' wildcards, e.g. "FF 23 ?? 45"
//   Start        - Base address to scan from
//   Size         - Number of bytes to scan
//   bFindPointer - If true, follows the RIP-relative pointer at the match
//   Addition     - Extra offset added when following a pointer
//
// Returns: Address of the pattern match, or nullptr if not found.
template<typename T>
static T PatternScan(const char* Pattern, uint64_t Start, size_t Size, bool bFindPointer = false, int Addition = 0)
{
    std::vector<int> PatternVector = PatternToIntVector(Pattern);
    uint8_t* Search = reinterpret_cast<uint8_t*>(Start);

    const size_t SizeOfPattern = PatternVector.size();
    const int* PatternData = PatternVector.data();

    for (int i = 0; i < Size - SizeOfPattern; ++i)
    {
        bool FoundSignature = true;

        // Compare each byte, skipping wildcards (-69)
        for (int j = 0; j < SizeOfPattern; ++j)
        {
            if (Search[i + j] != PatternData[j] && PatternData[j] != -69)
            {
                FoundSignature = false;
                break;
            }
        }

        if (FoundSignature)
        {
            if (bFindPointer)
            {
                return reinterpret_cast<T>(FindPointer(PatternVector, &Search[i], Addition));
            }
            return reinterpret_cast<T>(&Search[i]);
        }
    }

    return reinterpret_cast<T>(nullptr);
}