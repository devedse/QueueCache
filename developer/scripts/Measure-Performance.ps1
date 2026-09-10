#Requires -Version 5.1
<#
.SYNOPSIS
    Repeatable QueueCache performance experiments on a disposable cached volume.

.DESCRIPTION
    Runs the focused experiment set the performance plan requires (docs/PERFORMANCE_PLAN.md):
    small-request overhead, foreground/background interference, capacity pressure, controlled
    slow storage, random-drain efficiency, drain parallelism and flush-under-load.

    Configurations are interleaved and repeated, and the summary reports medians with spread,
    because same-configuration run-to-run drift of more than 2x has been observed on shared
    virtual storage. Never point this at an OS, boot or data volume: every test file lives in
    one directory on the target volume and write experiments overwrite them.

    This script measures. It never formats, partitions or signs anything, and it restores the
    original cache policy and clears the lab delay hook even after a failure.

.EXAMPLE
    ./Measure-Performance.ps1 -Volume Q: -DiskSpd C:\tools\diskspd.exe -Repeats 3

.EXAMPLE
    ./Measure-Performance.ps1 -Volume Q: -DiskSpd C:\tools\diskspd.exe -Experiments interference,slow-storage
#>
[CmdletBinding()]
param(
    # Cached target volume, for example Q:. Must not be the OS, boot or system volume.
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z]:$')][string]$Volume,
    # Path to diskspd.exe (Microsoft DiskSpd, or the copy bundled with CrystalDiskMark).
    [Parameter(Mandatory)][string]$DiskSpd,
    [ValidateRange(64, 131072)][int]$BudgetMiB = 4096,
    # Drain algorithms to compare. Each is applied once per repeat, interleaved.
    [ValidateSet('off', 'Eager', 'Balanced', 'Idle')][string[]]$Configs = @('Eager', 'Idle'),
    [ValidateSet('small-request', 'interference', 'capacity', 'slow-storage', 'random-drain', 'drain-parallelism', 'flush-under-load')]
    [string[]]$Experiments = @('small-request', 'interference', 'capacity', 'slow-storage', 'random-drain', 'drain-parallelism', 'flush-under-load'),
    [ValidateRange(1, 10)][int]$Repeats = 3,
    # Measured seconds per DiskSpd window. Warm-up is added on top.
    [ValidateRange(3, 120)][int]$Duration = 10,
    # Hot read set, kept well inside the budget so it stays a RAM-hit workload.
    [ValidateRange(64, 4096)][int]$ReadSetMiB = 256,
    # Fresh-data set for capacity pressure. Must exceed the usable payload to throttle.
    [ValidateRange(256, 65536)][int]$CapacitySetMiB = 8192,
    [ValidateRange(1, 2000)][int]$SlowStorageDelayMs = 25,
    [string]$OutputDirectory = (Join-Path $PWD "qcache-perf-$(Get-Date -Format yyyyMMdd-HHmmss)"),
    [string]$Qcache = 'qcache'
)

$ErrorActionPreference = 'Stop'
$script:rows = @()
$script:started = Get-Date

function Say($text) {
    $line = '{0:HH:mm:ss}  {1}' -f (Get-Date), $text
    Add-Content -Path (Join-Path $OutputDirectory 'run.log') -Value $line
    Write-Host $line
}

function Qc {
    # Native stderr must not abort the run; callers check state instead.
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & $Qcache @args 2>&1 | Out-String } finally { $ErrorActionPreference = $old }
}

function Get-State { Qc 'policy' 'status' $Volume '--json' | ConvertFrom-Json }

function Assert-Target {
    # Local names must not shadow $Volume: PowerShell variable names are case-insensitive.
    $letter = $Volume.Substring(0, 1)
    $target = Get-Volume -DriveLetter $letter -ErrorAction Stop
    if (-not $target.DriveLetter) { throw "$Volume is not mounted." }
    $system = (Get-Item $env:SystemRoot).PSDrive.Name + ':'
    if ($Volume -ieq $system) { throw "Refusing to benchmark the Windows volume ($system)." }
    $partition = Get-Partition -DriveLetter $letter
    $disk = Get-Disk -Number $partition.DiskNumber
    if ($disk.IsBoot -or $disk.IsSystem -or $disk.Number -eq 0) { throw "Refusing to benchmark boot/system disk $($disk.Number)." }
    if (-not (Test-Path -LiteralPath $DiskSpd)) { throw "DiskSpd not found: $DiskSpd" }
    $state = Get-State
    if (-not $state) { throw "No QueueCache state for $Volume. Install the driver and restart Windows first." }
    Say "target $Volume on disk $($disk.Number) ($($disk.FriendlyName)); driver instance $($state.Instance) revision $($state.Generation); free $([math]::Round($target.SizeRemaining/1GB,1)) GiB"
    $state
}

function Apply-Config([string]$config, [int]$parallelism = 1) {
    if ($config -eq 'off') { Qc 'policy' 'pause' $Volume | Out-Null }
    else {
        Qc 'policy' 'apply' $Volume '--budget-mib' $BudgetMiB '--preset' 'Fast' '--drain' $config `
            '--drain-parallelism' $parallelism '--save' | Out-Null
    }
    $state = Get-State
    $expected = if ($config -eq 'off') { 'Paused' } else { 'Active' }
    if ($state.RuntimeStatus -ne $expected) { throw "policy $config did not reach $expected (got $($state.RuntimeStatus))" }
    $state
}

function New-TestFile([string]$name, [int]$sizeMiB) {
    $path = Join-Path "$Volume\QueueCache-Perf" $name
    $null = New-Item -ItemType Directory -Force -Path (Split-Path $path)
    if (-not (Test-Path $path) -or (Get-Item $path).Length -ne ($sizeMiB * 1MB)) {
        Remove-Item $path -ErrorAction SilentlyContinue
        Say "creating $name ($sizeMiB MiB)"
        & $DiskSpd "-c$($sizeMiB)M" -b1M -o4 -t1 -w100 -d1 -S $path | Out-Null
        Qc 'policy' 'flush' $Volume | Out-Null
    }
    $path
}

# Parses a DiskSpd text report. "total:" shares its field with the byte count, so MiB/s is index 2.
function Read-DiskSpd([string]$text) {
    $lines = $text -split "`r?`n"
    $total = @($lines | Where-Object { $_ -match '^total:' })
    $fields = @()
    if ($total.Count) { $fields = @($total[0] -split '\|' | ForEach-Object { $_.Trim() }) }
    $result = [ordered]@{
        MiBps    = if ($fields.Count -gt 2) { [double]$fields[2] } else { $null }
        IOPS     = if ($fields.Count -gt 3) { [double]$fields[3] } else { $null }
        AvgLatMs = if ($fields.Count -gt 4) { [double]$fields[4] } else { $null }
    }
    foreach ($p in '50th', '95th', '99th', '3-nines', 'max') {
        $row = @($lines | Where-Object { $_ -match "^\s*$([regex]::Escape($p))\s*\|" })
        $value = $null
        if ($row.Count) { $cols = @($row[0] -split '\|' | ForEach-Object { $_.Trim() }); $value = $cols[-1] }
        $result["Lat_$($p -replace '-', '')"] = $value
    }
    $result
}

function Invoke-DiskSpd([string]$label, [string[]]$spdArgs, [string]$path) {
    $text = (& $DiskSpd @spdArgs $path 2>&1 | Out-String)
    Set-Content -Path (Join-Path $OutputDirectory "diskspd-$label.txt") -Value $text
    Read-DiskSpd $text
}

function Start-BackgroundLoad([string[]]$spdArgs, [string]$path, [string]$label) {
    $out = Join-Path $OutputDirectory "diskspd-$label.txt"
    Start-Process -FilePath $DiskSpd -ArgumentList ($spdArgs + $path) -RedirectStandardOutput $out `
        -WindowStyle Hidden -PassThru
}

# One result row: DiskSpd metrics plus driver counter deltas over the same window.
function Add-Row([hashtable]$fields, $before, $after, [double]$seconds) {
    $row = [ordered]@{
        Timestamp = (Get-Date).ToString('s'); Volume = $Volume; BudgetMiB = $BudgetMiB
        DriverInstance = $after.Instance; DriverRevision = $after.Generation; Seconds = [math]::Round($seconds, 2)
    }
    foreach ($k in $fields.Keys) { $row[$k] = $fields[$k] }
    $delta = {
        param($name) [math]::Round((($after.$name) - ($before.$name)) / 1MB, 1)
    }
    $row.AcceptedMiB = & $delta 'AcceptedBytes'
    $row.DrainedMiB = & $delta 'DrainedBytes'
    $row.CoalescedMiB = [math]::Round(((($after.AcceptedBytes - $before.AcceptedBytes) - ($after.DrainedBytes - $before.DrainedBytes)) / 1MB), 1)
    $row.ReadHitMiB = & $delta 'ReadHitBytes'
    $row.ReadMissMiB = & $delta 'ReadMissBytes'
    $row.LowerWrites = $after.LowerWrites - $before.LowerWrites
    $row.BatchedWrites = $after.BatchedWrites - $before.BatchedWrites
    $row.ThrottleWaits = $after.ThrottleWaits - $before.ThrottleWaits
    $row.Evictions = $after.Evictions - $before.Evictions
    $row.DirtyMiB = [math]::Round($after.DirtyBytes / 1MB, 1)
    $row.PeakDirtyMiB = [math]::Round($after.PeakDirtyBytes / 1MB, 1)
    $row.CleanReadMiB = [math]::Round($after.CleanReadBytes / 1MB, 1)
    $row.CleanWriteMiB = [math]::Round($after.CleanWriteBytes / 1MB, 1)
    $row.Errors = $after.Errors - $before.Errors
    $script:rows += [pscustomobject]$row
    [pscustomobject]$row
}

function Measure-Window([hashtable]$fields, [string]$label, [string[]]$spdArgs, [string]$path) {
    $before = Get-State
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $metrics = Invoke-DiskSpd $label $spdArgs $path
    $sw.Stop()
    $all = @{} + $fields
    foreach ($k in $metrics.Keys) { $all[$k] = $metrics[$k] }
    $row = Add-Row $all $before (Get-State) $sw.Elapsed.TotalSeconds
    Say ('{0,-18} {1,-26} {2,10} MiB/s {3,10} IOPS  avg {4,7} p95 {5,8} p99 {6,8} | miss {7,7} MiB thr {8,5} evc {9,7}' -f `
            $fields.Experiment, $fields.Case, $row.MiBps, $row.IOPS, $row.AvgLatMs, $row.Lat_95th, $row.Lat_99th, $row.ReadMissMiB, $row.ThrottleWaits, $row.Evictions)
    $row
}

# Timed explicit drain, reported separately from any score.
function Measure-Flush([hashtable]$fields) {
    $before = Get-State
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Qc 'policy' 'flush' $Volume | Out-Null
    $sw.Stop()
    $after = Get-State
    $drained = ($after.DrainedBytes - $before.DrainedBytes) / 1MB
    $all = @{} + $fields
    $all.MiBps = if ($sw.Elapsed.TotalSeconds -gt 0) { [math]::Round($drained / $sw.Elapsed.TotalSeconds, 2) } else { $null }
    $all.DirtyBeforeMiB = [math]::Round($before.DirtyBytes / 1MB, 1)
    $row = Add-Row $all $before $after $sw.Elapsed.TotalSeconds
    Say ('{0,-18} {1,-26} drained {2,8} MiB in {3,7}s = {4,8} MiB/s | lower {5,8} batched {6,8}' -f `
            $fields.Experiment, $fields.Case, $row.DrainedMiB, $row.Seconds, $all.MiBps, $row.LowerWrites, $row.BatchedWrites)
    $row
}

function Reset-Cache {
    # Comparable starting state: no dirty payload, no warm clean blocks.
    Qc 'policy' 'flush' $Volume | Out-Null
    Qc 'policy' 'drop-clean' $Volume | Out-Null
}

function Warm-ReadSet([string]$path) {
    # Sequential warm pass, then confirm the set is actually resident.
    & $DiskSpd -b1M -o4 -t1 -w0 -d3 -S $path | Out-Null
    $state = Get-State
    Say ("warm read set: clean read $([math]::Round($state.CleanReadBytes/1MB,1)) MiB, retained $([math]::Round($state.CleanWriteBytes/1MB,1)) MiB")
}

$null = New-Item -ItemType Directory -Force -Path $OutputDirectory
$original = Assert-Target
$originalDrain = if ($original.Options) { [string]$original.Options.Drain } else { 'Eager' }
$readFile = New-TestFile 'hot-read.dat' $ReadSetMiB
$writeFile = New-TestFile 'writer.dat' 2048
$capacityFile = if ($Experiments -contains 'capacity') { New-TestFile 'capacity.dat' $CapacitySetMiB } else { $null }
$warm = @('-b4K', '-r4K', '-S', '-L', '-Zr')

try {
    foreach ($repeat in 1..$Repeats) {
        foreach ($config in $Configs) {
            Say "=== repeat $repeat / config $config ==="
            $null = Apply-Config $config
            Reset-Cache
            $base = @{ Repeat = $repeat; Config = $config }

            if ($Experiments -contains 'small-request') {
                Warm-ReadSet $readFile
                foreach ($shape in @(@{ n = 'read-o1-t1'; a = @('-o1', '-t1', '-w0') }, @{ n = 'read-o32-t1'; a = @('-o32', '-t1', '-w0') },
                        @{ n = 'read-o8-t4'; a = @('-o8', '-t4', '-w0') }, @{ n = 'write-o1-t1'; a = @('-o1', '-t1', '-w100') },
                        @{ n = 'write-o32-t1'; a = @('-o32', '-t1', '-w100') }, @{ n = 'write-o8-t4'; a = @('-o8', '-t4', '-w100') })) {
                    # Equal total outstanding I/O is only comparable within o32-t1 versus o8-t4.
                    $f = $base + @{ Experiment = 'small-request'; Case = $shape.n }
                    $null = Measure-Window $f "r$repeat-$config-small-$($shape.n)" ($warm + $shape.a + @("-d$Duration", '-W3')) $readFile
                    Reset-Cache; Warm-ReadSet $readFile
                }
            }

            if ($Experiments -contains 'interference' -or $Experiments -contains 'slow-storage') {
                $delays = @(0)
                if ($Experiments -contains 'slow-storage') { $delays += $SlowStorageDelayMs }
                if ($Experiments -notcontains 'interference') { $delays = @($SlowStorageDelayMs) }
                foreach ($delay in $delays) {
                    $experiment = if ($delay -eq 0) { 'interference' } else { 'slow-storage' }
                    Reset-Cache; Warm-ReadSet $readFile
                    if ($delay -gt 0) { Qc 'lab-delay' $Volume $delay | Out-Null; Say "lab-delay $delay ms enabled" }
                    $f = $base + @{ Experiment = $experiment; Case = "reader-alone-d$delay" }
                    $null = Measure-Window $f "r$repeat-$config-$experiment-alone" ($warm + @('-o8', '-t2', '-w0', "-d$Duration", '-W3')) $readFile
                    # Independent writer on a separate file; the reader must stay a RAM-hit workload.
                    $writer = Start-BackgroundLoad @('-b1M', '-o8', '-t1', '-w100', "-d$($Duration*3)", '-W0', '-S', '-Zr') $writeFile "r$repeat-$config-$experiment-writer"
                    Start-Sleep -Seconds 3
                    $f = $base + @{ Experiment = $experiment; Case = "reader-with-writer-d$delay" }
                    $null = Measure-Window $f "r$repeat-$config-$experiment-loaded" ($warm + @('-o8', '-t2', '-w0', "-d$Duration", '-W3')) $readFile
                    if (-not $writer.HasExited) { $writer.WaitForExit(120000) | Out-Null }
                    if ($delay -gt 0) { Qc 'lab-delay' $Volume 0 | Out-Null; Say 'lab-delay cleared' }
                    $null = Measure-Flush ($base + @{ Experiment = $experiment; Case = "post-writer-flush-d$delay" })
                }
            }

            if ($Experiments -contains 'capacity') {
                Reset-Cache
                # Fresh data across a working set larger than the payload: verify it really throttles.
                $f = $base + @{ Experiment = 'capacity'; Case = 'fresh-write-4K-o8-t4' }
                $row = Measure-Window $f "r$repeat-$config-capacity" ($warm + @('-o8', '-t4', '-w100', "-d$($Duration*2)", '-W3')) $capacityFile
                if ($row.ThrottleWaits -eq 0) { Say "WARNING: capacity experiment did not throttle (accepted $($row.AcceptedMiB) MiB, budget $BudgetMiB MiB). Increase CapacitySetMiB or Duration." }
                $null = Measure-Flush ($base + @{ Experiment = 'capacity'; Case = 'post-capacity-flush' })
            }

            if ($Experiments -contains 'random-drain') {
                Reset-Cache
                $f = $base + @{ Experiment = 'random-drain'; Case = 'random-write-phase' }
                $null = Measure-Window $f "r$repeat-$config-randomdrain" ($warm + @('-o8', '-t4', '-w100', "-d$Duration", '-W3')) $writeFile
                $null = Measure-Flush ($base + @{ Experiment = 'random-drain'; Case = 'timed-drain' })
            }

            if ($Experiments -contains 'flush-under-load') {
                Reset-Cache
                $writer = Start-BackgroundLoad @('-b4K', '-r4K', '-o8', '-t4', '-w100', "-d$($Duration*2)", '-W0', '-S', '-Zr') $writeFile "r$repeat-$config-flushload-writer"
                Start-Sleep -Seconds ([math]::Max(3, [int]($Duration / 2)))
                $null = Measure-Flush ($base + @{ Experiment = 'flush-under-load'; Case = 'flush-while-writing' })
                if (-not $writer.HasExited) { $writer.WaitForExit(120000) | Out-Null }
                $null = Measure-Flush ($base + @{ Experiment = 'flush-under-load'; Case = 'flush-after-writing' })
            }
        }

        if ($Experiments -contains 'drain-parallelism') {
            foreach ($parallelism in 1, 2, 4) {
                Say "=== repeat $repeat / drain parallelism $parallelism ==="
                $null = Apply-Config ($Configs | Where-Object { $_ -ne 'off' } | Select-Object -First 1) $parallelism
                Reset-Cache
                $f = @{ Repeat = $repeat; Config = "parallelism-$parallelism"; Experiment = 'drain-parallelism'; Case = 'random-write-phase' }
                $null = Measure-Window $f "r$repeat-par$parallelism-write" ($warm + @('-o8', '-t4', '-w100', "-d$Duration", '-W3')) $writeFile
                $null = Measure-Flush @{ Repeat = $repeat; Config = "parallelism-$parallelism"; Experiment = 'drain-parallelism'; Case = 'timed-drain' }
            }
        }
    }
}
finally {
    # Always clear the diagnostic delay and restore the original policy, including after a failure.
    Qc 'lab-delay' $Volume 0 | Out-Null
    $restore = switch ($originalDrain) { '1' { 'Balanced' } '2' { 'Idle' } default { 'Eager' } }
    if ($original.BudgetBytes -gt 0) {
        Qc 'policy' 'apply' $Volume '--budget-mib' ([int]($original.BudgetBytes / 1MB)) '--preset' `
            $(if ($original.UnsafeDefer) { 'Fast' } else { 'Strict' }) '--drain' $restore '--save' | Out-Null
    }
    if ($script:rows.Count) {
        $csv = Join-Path $OutputDirectory 'results.csv'
        $script:rows | Export-Csv -Path $csv -NoTypeInformation
        $script:rows | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory 'results.json')
        # Median and spread across repeats: a single sample cannot separate a change from drift.
        $summary = $script:rows | Group-Object Experiment, Case, Config | ForEach-Object {
            $values = @($_.Group.MiBps | Where-Object { $null -ne $_ } | Sort-Object)
            $p99 = @($_.Group.Lat_99th | Where-Object { $_ } | ForEach-Object { [double]$_ } | Sort-Object)
            [pscustomobject]@{
                Experiment  = $_.Group[0].Experiment; Case = $_.Group[0].Case; Config = $_.Group[0].Config
                Samples     = $_.Count
                MedianMiBps = if ($values.Count) { $values[[int]([math]::Floor($values.Count / 2))] } else { $null }
                MinMiBps    = if ($values.Count) { $values[0] } else { $null }
                MaxMiBps    = if ($values.Count) { $values[-1] } else { $null }
                MedianP99Ms = if ($p99.Count) { $p99[[int]([math]::Floor($p99.Count / 2))] } else { $null }
                MedianThrottles = ($_.Group.ThrottleWaits | Measure-Object -Average).Average
            }
        }
        $summary | Export-Csv -Path (Join-Path $OutputDirectory 'summary.csv') -NoTypeInformation
        $summary | Format-Table -AutoSize | Out-String -Width 220 | Write-Host
        Say "results: $csv"
    }
    Say ("finished in {0:0.0} minutes; policy restored to {1}" -f ((Get-Date) - $script:started).TotalMinutes, $restore)
}
