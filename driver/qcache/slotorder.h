// SPDX-License-Identifier: MIT
#pragma once

// Free-slot orders for the QcLabResetFreeOrder layout experiment. For each order,
// position k of the rebuilt free list receives slot QcSlotOrder(k, ...), and every
// order is a bijection on [0, count) (slotorder-check.h), so no slot is lost or repeated.
//   0: ascending (a fresh allocation)
//   1: whole chunks in scattered order, slots ascending inside each chunk
//   2: chunks ascending, slots descending inside each chunk
//   3: every consecutive position in a different, scattered slot
// A partial last chunk keeps ascending order for orders 1 and 2.
constexpr unsigned long QcGcd(unsigned long a, unsigned long b)
{
    while (b)
    {
        const auto t = a % b;
        a = b;
        b = t;
    }
    return a;
}

// Multiplier near 0.618 * count and coprime with count: k * step % count is then a
// bijection that puts neighbouring positions far apart.
constexpr unsigned long QcScatterStep(unsigned long count)
{
    if (count <= 1)
        return 1;
    auto step = static_cast<unsigned long>(static_cast<unsigned long long>(count) * 618 / 1000);
    if (!step)
        step = 1;
    while (QcGcd(step, count) != 1)
        ++step;
    return step;
}

constexpr unsigned long QcSlotOrder(unsigned long k, unsigned long count, unsigned long perChunk, unsigned long order,
                                    unsigned long chunkStep, unsigned long slotStep)
{
    const auto chunks = count / perChunk, whole = chunks * perChunk;
    if (order == 1 && k < whole)
        return static_cast<unsigned long>(static_cast<unsigned long long>(k / perChunk) * chunkStep % chunks) * perChunk +
               k % perChunk;
    if (order == 2 && k < whole)
        return k / perChunk * perChunk + perChunk - 1 - k % perChunk;
    if (order == 3)
        return static_cast<unsigned long>(static_cast<unsigned long long>(k) * slotStep % count);
    return k;
}
