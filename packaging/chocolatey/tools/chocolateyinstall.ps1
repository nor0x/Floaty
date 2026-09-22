$ErrorActionPreference = 'Stop'

# Floaty's installer is ~136 MB, so the package downloads it from the GitHub release rather than
# embedding it. The URL is pinned to a tag, which is what makes the checksum below meaningful -
# release assets are never re-uploaded to an existing tag.
$packageArgs = @{
    packageName    = 'floaty'
    fileType       = 'exe'
    url64bit       = 'https://github.com/nor0x/Floaty/releases/download/v0.2.2/Floaty-win-Setup.exe'
    checksum64     = 'D1936C24C94234D2A91BB0EE64094C7EAB4BC3AA0DF0503F45D669291FE3EB0E'
    checksumType64 = 'sha256'
    # Velopack: hides every dialog and, importantly, skips launching the app afterwards, so the
    # install does not leave a window open behind an unattended `choco install`.
    silentArgs     = '--silent'
    # Velopack's setup exits 0 or 1 and has no reboot path - none of the MSI 3010/1641 codes apply.
    validExitCodes = @(0)
}

if ((Get-OSArchitectureWidth) -ne 64) {
    throw 'Floaty ships for Windows x64 only.'
}

Install-ChocolateyPackage @packageArgs
