#Requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
try
{
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $headers = @{ 'User-Agent' = 'QueueCache-Updater'; Accept = 'application/vnd.github+json' }
    # Releases (master, tag <version>) and pre-releases (other branches, tag <version>-<branch>)
    # share one build counter: install whichever has the highest version. /latest would skip
    # pre-releases, so list the recent ones (newest first) instead.
    $releases = Invoke-RestMethod 'https://api.github.com/repos/devedse/QueueCache/releases?per_page=100' -Headers $headers
    $candidates = foreach ($candidate in $releases)
    {
        if (-not $candidate.draft -and $candidate.tag_name -match '^(\d+\.\d+\.\d+\.\d+)(-[A-Za-z0-9._-]+)?$')
        {
            [pscustomobject]@{ Release = $candidate; Version = [version]$Matches[1] }
        }
    }
    # Same version twice should not happen; if it does, prefer the full release.
    $selected = $candidates | Sort-Object @{ Expression = { $_.Version }; Descending = $true }, @{ Expression = { [bool]$_.Release.prerelease } } |
        Select-Object -First 1
    if (-not $selected)
    {
        throw 'No published QueueCache installer release found.'
    }
    $release = $selected.Release
    $version = $selected.Version.ToString()
    $assets = @($release.assets | Where-Object { $_.name -eq "QueueCache-$version-setup.exe" })
    if ($assets.Count -ne 1)
    {
        throw 'Release does not contain exactly one supported installer.'
    }
    $asset = $assets[0]
    $expectedUrl = "https://github.com/devedse/QueueCache/releases/download/$($release.tag_name)/$($asset.name)"
    if ($asset.browser_download_url -cne $expectedUrl -or $asset.digest -notmatch '^sha256:[0-9a-fA-F]{64}$')
    {
        throw 'Invalid installer URL or missing SHA-256 digest.'
    }
    $folder = Join-Path ([IO.Path]::GetTempPath()) ('QueueCache-Update-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $folder | Out-Null
    $installer = Join-Path $folder $asset.name
    Write-Host "Downloading QueueCache $version$(if ($release.prerelease) { " (pre-release $($release.tag_name))" })..."
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest $expectedUrl -OutFile $installer -UseBasicParsing
    if ((Get-FileHash -LiteralPath $installer).Hash -ine $asset.digest.Substring(7))
    {
        throw 'Checksum mismatch. Installer will not run.'
    }
    Write-Host 'Checksum verified. Opening installer; approve the Windows elevation prompt. Driver is test-signed.'
    # Deliberately interactive: the installer, not this helper, owns reboot choice.
    $process = Start-Process -FilePath $installer -Verb RunAs -PassThru -Wait
    if ($process.ExitCode -notin @(0, 3010))
    {
        throw "Installer exited with code $($process.ExitCode)."
    }
    Write-Host "Installer finished. Download retained at $installer"
}
catch
{
    Write-Error $_; exit 1
}
