<#
.SYNOPSIS
    Stamps a released version and its installer checksum into the Chocolatey package.

.DESCRIPTION
    Rewrites floaty.nuspec and tools/chocolateyinstall.ps1 in place so they point at the given
    release. Run by the `chocolatey` job in .github/workflows/release-windows.yml after the GitHub
    release is published; also runnable by hand for the first (manual) submission.

    The checksum is taken from the release's own checksums.txt, which the release job produced by
    hashing the artifact it had just built. If that asset is missing - releases before checksums.txt
    existed - the installer is downloaded and hashed instead. Either way the hash belongs to the
    file users will actually download, never to a local rebuild.

    Requires PowerShell 6+ for -Encoding utf8NoBOM: the Chocolatey package validator rejects a BOM
    (rule CPMR0054), and Windows PowerShell 5.1 writes one for "utf8".

.EXAMPLE
    ./packaging/chocolatey/update.ps1 -Version 0.2.3
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+')]
    [string] $Version
)

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 6) {
    throw 'Run this with PowerShell 6+ (pwsh): Windows PowerShell writes a UTF-8 BOM, which the Chocolatey validator rejects.'
}

$root      = $PSScriptRoot
$nuspec    = Join-Path $root 'floaty.nuspec'
$installPs = Join-Path $root 'tools/chocolateyinstall.ps1'

$base     = "https://github.com/nor0x/Floaty/releases/download/v$Version"
$setupUrl = "$base/Floaty-win-Setup.exe"

$sha = $null
try {
    $checksums = (Invoke-WebRequest "$base/checksums.txt" -UseBasicParsing).Content
    # sha256sum format: "<hash>  <filename>" (or " *<filename>" for binary mode)
    if ($checksums -match '(?m)^([a-fA-F0-9]{64})\s+\*?Floaty-win-Setup\.exe\s*$') {
        $sha = $Matches[1].ToUpperInvariant()
        Write-Host "Checksum from checksums.txt: $sha"
    }
}
catch {
    Write-Host "No checksums.txt on v$Version ($($_.Exception.Message)) - falling back to downloading the installer."
}

if (-not $sha) {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "Floaty-win-Setup-$Version.exe"
    Invoke-WebRequest $setupUrl -OutFile $tmp
    $sha = (Get-FileHash $tmp -Algorithm SHA256).Hash.ToUpperInvariant()
    Remove-Item $tmp -Force
    Write-Host "Checksum from downloaded installer: $sha"
}

# nuspec: version, release notes link, and the jsDelivr icon URL (pinned to the tag, because
# Chocolatey will not serve icons from raw.githubusercontent and an unpinned CDN URL is mutable).
(Get-Content $nuspec -Raw) `
    -replace '<version>[^<]*</version>', "<version>$Version</version>" `
    -replace 'releases/tag/v[^<]*', "releases/tag/v$Version" `
    -replace 'Floaty@v[\d.]+/packaging', "Floaty@v$Version/packaging" |
    Set-Content $nuspec -NoNewline -Encoding utf8NoBOM

# install script: the versioned download URL and its checksum.
(Get-Content $installPs -Raw) `
    -replace 'releases/download/v[\d.]+/', "releases/download/v$Version/" `
    -replace "(checksum64\s*=\s*')[A-Fa-f0-9]*(')", "`${1}$sha`${2}" |
    Set-Content $installPs -NoNewline -Encoding utf8NoBOM

Write-Host "Stamped floaty $Version into the Chocolatey package."
