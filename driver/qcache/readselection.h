// SPDX-License-Identifier: MIT
#pragma once

// QC_STATE flag contract: enabled + Fast, without fault/suspend/barrier/removal.
// Only application flushes use this exception; controls/TRIM never do.
constexpr bool QcReadMayPassApplicationFlush(unsigned flags)
{
    return (flags & 33u) == 33u && (flags & 30u) == 0;
}

// Intrusive queue cursors, accessed ONLY while the queue lock is held. The owner
// must call Removing before unlink/free, Appended after tail insertion, and Reset
// after head insertion or a new foreground admission. No request is pinned here.
template <class Node> struct QcReadSelection
{
    Node* Find;
    Node* Check;
    Node* Candidate;

    constexpr void Reset(Node* first)
    {
        Find = first;
        Check = Candidate = nullptr;
    }
    constexpr void Appended(Node* head, Node* node)
    {
        if (Find == head)
            Find = node;
    }
    constexpr void Removing(Node* node, Node* next)
    {
        if (Find == node)
            Find = next;
        if (Check == node)
            Check = next;
        if (Candidate == node)
            Check = Candidate = nullptr;
    }

    // Adapter: Next, Fence, Eligible, Conflict. Each call examines at most budget
    // entries, INCLUDING dependency validation. more means useful continuation,
    // not a cache hit: the caller must release the lock before continuing.
    template <class Queue> constexpr Node* Step(Node* head, Queue& queue, unsigned budget, bool& more)
    {
        more = false;
        while (budget)
        {
            if (Candidate)
            {
                if (Check == Candidate)
                {
                    auto result = Candidate;
                    Candidate = Check = nullptr;
                    return result;
                }
                --budget;
                auto older = Check;
                Check = queue.Next(older);
                if (queue.Fence(older) || queue.Conflict(Candidate, older))
                    Candidate = Check = nullptr;
            }
            else
            {
                if (Find == head)
                    return nullptr;
                --budget;
                auto node = Find;
                // Keep the cursor on a real fence until its removal. Tail
                // arrivals cannot reopen a lane across that fence.
                if (queue.Fence(node))
                    return nullptr;
                Find = queue.Next(node);
                if (queue.Eligible(node))
                {
                    Candidate = node;
                    Check = queue.Next(head);
                }
            }
        }
        more = Candidate != nullptr || Find != head;
        return nullptr;
    }
};
