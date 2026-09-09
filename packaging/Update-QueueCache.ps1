#Requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $headers = @{ 'User-Agent'='QueueCache-Updater'; Accept='application/vnd.github+json' }
    # /latest excludes prereleases. Explicitly include published versioned builds.
    $releases = Invoke-RestMethod 'https://api.github.com/repos/devedse/QueueCache/releases?per_page=100' -Headers $headers
    $release = $releases | Where-Object { -not $_.draft -and $_.tag_name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.tag_name } -Descending | Select-Object -First 1
    if (-not $release) { throw 'No published QueueCache installer release found.' }
    $assets = @($release.assets | Where-Object { $_.name -eq "QueueCache-$($release.tag_name)-setup.exe" })
    if ($assets.Count -ne 1) { throw 'Release does not contain exactly one supported installer.' }
    $asset = $assets[0]
    $expectedUrl = "https://github.com/devedse/QueueCache/releases/download/$($release.tag_name)/$($asset.name)"
    if ($asset.browser_download_url -cne $expectedUrl -or $asset.digest -notmatch '^sha256:[0-9a-fA-F]{64}$') { throw 'Invalid installer URL or missing SHA-256 digest.' }
    $folder = Join-Path ([IO.Path]::GetTempPath()) ('QueueCache-Update-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $folder | Out-Null
    $installer = Join-Path $folder $asset.name
    Write-Host "Downloading QueueCache $($release.tag_name)..."
    $ProgressPreference='SilentlyContinue'
    Invoke-WebRequest $expectedUrl -OutFile $installer -UseBasicParsing
    if ((Get-FileHash -LiteralPath $installer).Hash -ine $asset.digest.Substring(7)) { throw 'Checksum mismatch. Installer will not run.' }
    Write-Host 'Checksum verified. Opening installer; approve the Windows elevation prompt. Driver is test-signed.'
    # Deliberately interactive: the installer, not this helper, owns reboot choice.
    $process = Start-Process -FilePath $installer -Verb RunAs -PassThru -Wait
    if ($process.ExitCode -notin @(0,3010)) { throw "Installer exited with code $($process.ExitCode)." }
    Write-Host "Installer finished. Download retained at $installer"
} catch { Write-Error $_; exit 1 }
