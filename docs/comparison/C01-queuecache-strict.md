# QueueCache 0.4.604.1, Strict, 2 GiB

[Back to the comparison](../COMPARISON.md). Status: **Partly measured**.

| | |
|---|---|
| Product | QueueCache |
| Version | 0.4.604.1 |
| Mode | Strict, 2 GiB |
| Kind | Volume filter, write-through |
| Price | Free (MIT) |

## CrystalDiskMark

<img src="../images/comparison/C01-queuecache-strict.png" width="480" alt="CrystalDiskMark, QueueCache 0.4.604.1, Strict, 2 GiB">

Kept: run 2 (the faster SEQ1M Q8T1 read of two). MB/s.

| Test | Run 1 read / write | Run 2 read / write |
|---|---:|---:|
| SEQ1M Q8T1 | 39,743.57 / 19,716.73 | 39,989.21 / 21,310.04 |
| SEQ1M Q1T1 | 20,909.02 / 14,037.49 | 19,413.95 / 12,937.26 |
| RND4K Q32T1 | 1,682.95 / 1,301.37 | 1,659.49 / 331.45 |
| RND4K Q1T1 | 1,277.07 / 897.67 | 1,266.80 / 871.17 |

## Detailed measurements (DiskSpd and timed steps)

Not run yet.

## Empty cache

Not run yet.

## Power loss

Not run yet.
