// SPDX-License-Identifier: MIT
#pragma once
// One bit per 512-byte sector within a 4 KiB allocation. 4Kn requests naturally
// cover all bits. A clear bit is UNKNOWN, not zero, and must never reach disk.
constexpr unsigned QcSectorMask(unsigned offset, unsigned length)
{
    return length == 0 ? 0u : ((1u << (length / 512)) - 1u) << (offset / 512);
}
constexpr bool QcCovers(unsigned valid, unsigned offset, unsigned length)
{
    const auto wanted = QcSectorMask(offset, length);
    return (valid & wanted) == wanted;
}
constexpr unsigned QcValidBytes(unsigned valid)
{
    unsigned bytes = 0;
    for (; valid; valid &= valid - 1) bytes += 512;
    return bytes;
}
constexpr bool QcCoverageChecks()
{
    for (unsigned mask = 0; mask < 256; ++mask)
        for (unsigned first = 0; first < 8; ++first)
            for (unsigned count = 1; count <= 8 - first; ++count)
            {
                unsigned expected = 0;
                for (unsigned i = first; i < first + count; ++i) expected |= 1u << i;
                if (QcSectorMask(first * 512, count * 512) != expected ||
                    QcCovers(mask, first * 512, count * 512) != ((mask & expected) == expected)) return false;
            }
    return QcValidBytes(255) == 4096 && QcValidBytes(129) == 1024 && QcValidBytes(0) == 0;
}
static_assert(QcCoverageChecks());
static_assert(QcSectorMask(0, 4096) == 255);
static_assert(QcSectorMask(3584, 512) == 128);
static_assert(QcSectorMask(512, 1024) == 6);
static_assert(QcCovers(6, 512, 1024));
static_assert(!QcCovers(6, 0, 1024));
// Sectors of the 4 KiB block at blockStart that a sector-aligned TRIM range covers.
constexpr unsigned QcTrimMask(long long blockStart, long long rangeStart, unsigned long long rangeLength)
{
    const long long rangeEnd = rangeStart + static_cast<long long>(rangeLength);
    const long long from = rangeStart > blockStart ? rangeStart : blockStart;
    const long long to = rangeEnd < blockStart + 4096 ? rangeEnd : blockStart + 4096;
    return to <= from ? 0u : QcSectorMask(static_cast<unsigned>(from - blockStart), static_cast<unsigned>(to - from));
}
static_assert(QcTrimMask(8192, 8192, 4096) == 255);
static_assert(QcTrimMask(8192, 0, 1 << 20) == 255);
static_assert(QcTrimMask(8192, 8192 + 1024, 1024) == 0x0C);
static_assert(QcTrimMask(8192, 8192 + 3584, 4096) == 0x80);
static_assert(QcTrimMask(8192, 4096, 4608) == 0x01);
static_assert(QcTrimMask(8192, 12288, 512) == 0 && QcTrimMask(8192, 0, 8192) == 0);
