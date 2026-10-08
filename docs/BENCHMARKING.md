# Benchmarking with CrystalDiskMark

How the README's performance numbers are measured, and the pitfalls that made
earlier runs come out too low. Follow this whenever the numbers or the
CrystalDiskMark screenshots are redone. For QueueCache's own verification suites
(DiskSpd in XML mode, telemetry, regression matrices) see
[developer verification](DEVELOPER_VERIFICATION.md).

## Setup

- The installed release, Driver Verifier off (`verifier /query`), Windows'
  Balanced power plan.
- **Cache:** the drive with a 2 GiB Fast cache:
  `qcache policy apply Q: --budget-mib 2048 --accept-volatile-flush`.
- **RAM disk:** `qcache disk create --mode ram --size-mib 4096 --letter T`
  (Direct access, the default). Remove it afterwards with
  `qcache disk stop <id> --discard` and `qcache disk remove <id>`.
- **Without cache:** the same drive with its cache paused
  (`qcache policy pause Q:`, afterwards `qcache policy resume Q:`).
- CrystalDiskMark 9.0.3 with its defaults: 5 passes, 1 GiB, MB/s, the Default
  (green) theme, **Theme → Zoom → 150%** for the screenshots. Type a one-line
  comment in the box at the bottom, for example
  `QueueCache RAM cache on Q: (Fast mode, 2 GiB)`.
- Run **All** twice per column and keep the faster run. Capture just the
  CrystalDiskMark window and save it as `docs/images/cdm-cache.png` or
  `cdm-ram-disk.png`; the README table copies the numbers.

## Pitfalls (each measured on the 4-vCPU test VM, 2026-10-08)

| Pitfall | What it did | Avoid it by |
|---|---|---|
| **Below-normal priority.** Task Scheduler starts programs at priority 7 (below normal, with low I/O and memory priorities) unless told otherwise, and DiskSpd inherits CrystalDiskMark's priority. | Cached SEQ1M Q8 read 16.4-17.7 instead of 28.9-30.6 GB/s; RAM disk SEQ1M Q8 write 16.3 instead of 24.7 GB/s; the uncached disk also improved from 262/116 to 640/270 MB/s. The experiment changed CPU, I/O and memory priorities together; it did not isolate their individual contributions. | Start CrystalDiskMark normally. When starting it from a scheduled task, use `New-ScheduledTaskSettingsSet -Priority 4` and check `Get-Process DiskMark64` shows `PriorityClass Normal`. |
| **Leftovers in the cache.** The 2 GiB cache still held earlier test files. | Part of the new 1 GiB test file was pushed out and read from the disk: 5-20 GB/s instead of 26-30 GB/s. | `qcache policy drop-clean Q:` before each run (drops clean read/retained-write data, never unwritten data). |
| **Allocation history.** Drop-clean empties data but preserves a free-slot order affected by earlier use. | Fully resident sequential reuse measured 29.8/10.8 GB/s (Q8/Q1), versus 36.9/15.5 with a fresh allocation; random reuse was slower still. | Keep and report allocation preparation consistently. Use the maintained `cache-layout` comparison below to distinguish reuse from fresh-allocation peaks. |
| **Right after a restart.** Windows' startup work (Defender, indexing) still running. | Lower and noisier results. | Wait at least 15 minutes after a restart; check the processor is idle. |
| **Other programs.** A second benchmark, a game launcher updating, a VM host busy. | Lower results. | Close them; on a VM, check the host's load. |
| **A notification over the window** (AutoPlay when a RAM disk appears). | Covers part of the screenshot. | Dismiss it before capturing. |

Keep preparation and machine conditions consistent: CrystalDiskMark's first
pass reads while the freshly written test file is still being written to the
disk (it reports the best of 5 passes), and one-request-at-a-time results (Q1)
depend on how fast one processor core copies memory. On the test VM, SEQ1M Q1
read varied from 10 to 15 GB/s between sessions. The controlled allocation-history
comparison below now reproduces that gap without a reboot or priority change;
the older session comparison alone did not establish a CPU-clock cause.

## Checking a surprising result

CrystalDiskMark 9.0.3 runs DiskSpd (`CdmResource\DiskSpd\DiskSpd64.exe`) per
test; these are its exact options (the file is its test file):

```text
SEQ1M Q8T1 read   -b1024K -o8  -t1 -W0 -S -w0         -ag -d5 -L <file>
SEQ1M Q1T1 read   -b1024K -o1  -t1 -W0 -S -w0         -ag -d5 -L <file>
RND4K Q32T1 read  -b4K    -o32 -t1 -W0 -S -w0   -r    -ag -d5 -L <file>
RND4K Q1T1 read   -b4K    -o1  -t1 -W0 -S -w0   -r    -ag -d5 -L <file>
writes            the same with -w100 -Z1024K
```

Run one by hand (normal priority, `-Rxml` for parseable output) against a 1 GiB
file to separate CrystalDiskMark from QueueCache. `qcache policy status Q: --json`
shows what the cache holds (`CleanReadBytes`, `CleanWriteBytes`, `DirtyBytes`);
`qcache diagnostics Q:` shows how reads were served (`CallerPath.Reads` on the
program's thread, `CopyOffloadReads` copied by the cache's threads).

## Driving CrystalDiskMark on a remote test VM

CrystalDiskMark is a desktop program, so it must run in the signed-in user's
session, not in an SSH session:

- Settings live in `DiskMark64.ini` next to the program; back it up and restore
  it afterwards. Keys: `TestCount` is an index (`4` = 5 passes), `TestSize`
  (`6` = 1 GiB), `DriveLetter` is an index from A (`16` = Q, `19` = T),
  `Theme` (`Default`, `Dark`, …), `ZoomType` (`150`).
- Start it with a scheduled task for the signed-in user:
  `New-ScheduledTaskPrincipal -UserId <user> -LogonType Interactive -RunLevel Highest`
  and **`-Priority 4`** (see the first pitfall).
- Pressing buttons: mouse input through the Proxmox VNC console did not reach the
  VM; a small PowerShell helper run the same way in the user's session
  (`SetCursorPos` and `mouse_event` from user32, `SendKeys` for typing) does.
  In SendKeys text, `(` `)` `+` `^` `%` `~` must be escaped as `{(}` and so on.
- Close CrystalDiskMark with its own close button so it saves its settings.
- Screenshots can come from the hypervisor console (a whole-screen capture,
  then cropped to the window).

These helpers are lab tooling and are not part of the repository.

Microsoft documents Task Scheduler's combined CPU, I/O and memory settings in
the [priority table](https://learn.microsoft.com/en-us/windows/win32/taskschd/taskschedulerschema-priority-settingstype-element).
Use priority **4** specifically; 5 and 6 also have normal CPU priority but lower
memory priority.

## Investigating allocation history

`drop-clean` does not restore a newly allocated cache: clean slots return to its
free list in eviction order. Reapplying the same budget also keeps the allocation.
To compare fresh allocation with reuse, use the maintained
[`cache-layout` suite](DEVELOPER_VERIFICATION.md#cache-allocation-history-comparison-plan-92).
It verifies allocation generations, complete residency and zero lower I/O around
scores, and brackets sequential/random reuse with fresh allocations. Do not infer
that a fast or slow score alone proves a clock, priority, or disk-miss cause.
The completed 24-case [investigation](CACHE_LAYOUT_INVESTIGATION_20261008.md)
records all medians/ranges and restores the old peak after each reuse group.
Until allocator behavior changes, report fresh-allocation peaks separately from
reused-cache results. The existing README screenshots remain real measurements
of their documented preparation; they have not been replaced by DiskSpd scores.
