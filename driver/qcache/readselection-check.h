// SPDX-License-Identifier: MIT
#pragma once
#include "readselection.h"

namespace QcSelectionChecks
{
struct Node
{
    Node* Next;
    unsigned Kind;
    unsigned Range;
}; // 1 read, 2 write, 3 fence
struct Queue
{
    Node Nodes[145]{};
    QcReadSelection<Node> Scan{};
    unsigned Visits = 0;
    unsigned Flags = 1; // Strict by default; kind 4 is an application flush.
    constexpr Queue()
    {
        for (unsigned i = 0; i < 145; ++i)
        {
            Nodes[i].Next = &Nodes[(i + 1) % 145];
            Nodes[i].Kind = 2;
            Nodes[i].Range = 1;
        }
        Scan.Reset(Nodes[0].Next);
    }
    constexpr Node* Next(Node* node)
    {
        return node->Next;
    }
    constexpr bool Fence(Node* node)
    {
        ++Visits;
        return node->Kind == 3 || (node->Kind == 4 && !QcReadMayPassApplicationFlush(Flags));
    }
    constexpr bool Eligible(Node* node)
    {
        return node->Kind == 1;
    }
    constexpr bool Conflict(Node* read, Node* older)
    {
        return older->Kind == 2 && read->Range == older->Range;
    }
    constexpr void Remove(unsigned index)
    {
        auto node = &Nodes[index];
        Scan.Removing(node, node->Next);
        auto previous = &Nodes[0];
        while (previous->Next != node)
            previous = previous->Next;
        previous->Next = node->Next;
    }
    constexpr Node* Step(bool& more)
    {
        Visits = 0;
        return Scan.Step(&Nodes[0], *this, 64, more);
    }
};
constexpr bool DeepQueue()
{
    Queue q;
    q.Nodes[140].Kind = 1;
    q.Nodes[140].Range = 2;
    bool more = false;
    for (unsigned i = 0; i < 8; ++i)
    {
        auto result = q.Step(more);
        if (q.Visits > 64)
            return false;
        if (result)
            return result == &q.Nodes[140] && i >= 4;
        if (!more)
            return false;
    }
    return false;
}
constexpr bool OlderWriteAndFence()
{
    Queue q;
    q.Nodes[140].Kind = 1; // Conflicts with older writes.
    bool more = false;
    for (unsigned i = 0; i < 8; ++i)
    {
        if (q.Step(more))
            return false;
        if (!more)
            break;
    }
    if (more)
        return false;
    q.Scan.Reset(q.Nodes[0].Next);
    q.Nodes[140].Range = 2;
    q.Nodes[70].Kind = 3;
    if (q.Step(more) || !more)
        return false;
    if (q.Step(more) || more)
        return false;
    if (q.Step(more) || more)
        return false; // Stable fence doesn't spin.
    q.Remove(70);     // Cancellation opens the cursor, without a new insertion.
    for (unsigned i = 0; i < 8; ++i)
    {
        auto result = q.Step(more);
        if (result)
            return result == &q.Nodes[140];
        if (!more)
            return false;
    }
    return false;
}
constexpr bool CancelAndReinsert()
{
    Queue q;
    q.Nodes[140].Kind = 1;
    q.Nodes[140].Range = 2;
    bool more = false;
    q.Step(more);
    q.Step(more);
    q.Step(more); // Candidate is being validated.
    if (q.Scan.Candidate != &q.Nodes[140])
        return false;
    q.Remove(140);
    if (q.Scan.Candidate)
        return false;
    if (q.Step(more) || more)
        return false;
    // Reinsert at head as a previously missed read; now ineligible. The owner
    // resets cursors so the new prefix cannot escape dependency validation.
    q.Nodes[140].Next = q.Nodes[0].Next;
    q.Nodes[0].Next = &q.Nodes[140];
    q.Nodes[140].Kind = 2;
    q.Scan.Reset(q.Nodes[0].Next);
    q.Nodes[144].Kind = 1;
    q.Nodes[144].Range = 2;
    for (unsigned i = 0; i < 10; ++i)
    {
        if (q.Step(more))
            return false;
        if (!more)
            return true;
    }
    return false;
}
constexpr bool CursorRemovalAndAppend()
{
    Queue q;
    bool more = false;
    q.Step(more); // Find points at 65.
    q.Remove(65);
    if (q.Scan.Find != &q.Nodes[66])
        return false;
    while (more)
        if (q.Step(more))
            return false;
    // Reuse removed node as a new tail read.
    q.Nodes[144].Next = &q.Nodes[65];
    q.Nodes[65].Next = &q.Nodes[0];
    q.Nodes[65].Kind = 1;
    q.Nodes[65].Range = 2;
    q.Scan.Appended(&q.Nodes[0], &q.Nodes[65]);
    for (unsigned i = 0; i < 8; ++i)
    {
        auto result = q.Step(more);
        if (result)
            return result == &q.Nodes[65];
        if (!more)
            return false;
    }
    return false;
}
constexpr bool LateDependencyAndValidationCancellation()
{
    Queue q;
    bool more = false;
    q.Nodes[140].Kind = 1;
    q.Nodes[140].Range = 2;
    q.Nodes[120].Range = 2; // Conflict beyond the old 64-entry boundary.
    for (unsigned i = 0; i < 10; ++i)
    {
        if (q.Step(more))
            return false;
        if (!more)
            break;
    }
    if (more)
        return false;
    q.Remove(120);
    q.Scan.Reset(q.Nodes[0].Next);
    q.Step(more);
    q.Step(more);
    q.Step(more);
    if (!q.Scan.Candidate || !q.Scan.Check)
        return false;
    // Cancellation of the next dependency cursor must not dereference its
    // unlinked node on the next chunk.
    auto check = q.Scan.Check;
    unsigned index = 1;
    while (&q.Nodes[index] != check)
        ++index;
    auto successor = check->Next;
    q.Remove(index);
    if (q.Scan.Check != successor)
        return false;
    for (unsigned i = 0; i < 8; ++i)
    {
        auto result = q.Step(more);
        if (result)
            return result == &q.Nodes[140];
        if (!more)
            return false;
    }
    return false;
}
constexpr bool NewPrefixInvalidatesValidation()
{
    Queue q;
    bool more = false;
    q.Nodes[140].Kind = 1;
    q.Nodes[140].Range = 2;
    q.Step(more);
    q.Step(more);
    q.Step(more);
    if (!q.Scan.Candidate)
        return false;
    q.Remove(144);
    q.Nodes[144].Kind = 3;
    q.Nodes[144].Next = q.Nodes[0].Next;
    q.Nodes[0].Next = &q.Nodes[144];
    q.Scan.Reset(q.Nodes[0].Next);
    return !q.Step(more) && !more;
}
static_assert(DeepQueue(), "Reach deep reads with bounded dependency validation");
static_assert(OlderWriteAndFence(), "Preserve older-write and fence ordering");
static_assert(CancelAndReinsert(), "Cancellation and reinsertion invalidate borrowed cursors");
static_assert(CursorRemovalAndAppend(), "Maintain cursor lifetime and tail progress");
static_assert(LateDependencyAndValidationCancellation(), "Validate beyond 64 and survive dependency cancellation");
static_assert(NewPrefixInvalidatesValidation(), "Head insertion must restart dependency validation");
constexpr bool FlushChecks(unsigned flags, unsigned conflictAt, bool hardFence)
{
    Queue q;
    q.Flags = flags;
    q.Nodes[70].Kind = 4;
    q.Nodes[100].Kind = 4;
    q.Nodes[140].Kind = 1;
    q.Nodes[140].Range = 2;
    if (conflictAt)
        q.Nodes[conflictAt].Range = 2;
    if (hardFence)
        q.Nodes[110].Kind = 3;
    bool more = false;
    for (unsigned i = 0; i < 12; ++i)
    {
        auto found = q.Step(more);
        if (q.Visits > 64)
            return false;
        if (found)
            return !conflictAt && !hardFence && flags == 33 && found == &q.Nodes[140] &&
                   q.Nodes[69].Next == &q.Nodes[70]; // Flush was never removed.
        if (!more)
            return conflictAt || hardFence || flags != 33;
    }
    return false;
}
static_assert(FlushChecks(33, 0, false), "Fast reads cross multiple flushes beyond entry 64");
static_assert(FlushChecks(1, 0, false), "Strict flush remains a fence");
static_assert(FlushChecks(32, 0, false), "Disabled cache cannot cross flush");
static_assert(FlushChecks(35, 0, false) && FlushChecks(37, 0, false) && FlushChecks(41, 0, false) &&
                  FlushChecks(49, 0, false),
              "Unhealthy cache cannot cross flush");
static_assert(FlushChecks(33, 20, false) && FlushChecks(33, 120, false),
              "Overlaps before and after flush still block read");
static_assert(FlushChecks(33, 0, true), "Fast flush exception never bypasses other barriers");
} // namespace QcSelectionChecks
