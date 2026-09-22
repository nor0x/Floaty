$ErrorActionPreference = 'Stop'

# Chocolatey's AutoUninstaller already handles Floaty correctly on its own: it snapshots HKCU and
# prefers QuietUninstallString, which Velopack writes as `"<root>\Update.exe" --uninstall --silent`.
# This script is the fallback for users who have turned the AutoUninstaller off.
#
# The path is read back from the registry rather than hardcoded to %LocalAppData%\Floaty, so copies
# installed elsewhere with --installto still uninstall.

$key = @(Get-UninstallRegistryKey -SoftwareName 'Floaty')

if ($key.Count -eq 1) {
    $uninstall = $key[0].QuietUninstallString
    if (-not $uninstall) { $uninstall = "$($key[0].UninstallString) --silent" }

    if ($uninstall -match '^\s*"([^"]+)"\s*(.*)$') {
        $file = $Matches[1]
        $silentArgs = $Matches[2]
    }
    else {
        $parts = $uninstall -split '\s+', 2
        $file = $parts[0]
        $silentArgs = if ($parts.Count -gt 1) { $parts[1] } else { '' }
    }

    Uninstall-ChocolateyPackage -PackageName 'floaty' -FileType 'exe' `
        -SilentArgs $silentArgs -File $file -ValidExitCodes @(0)
}
elseif ($key.Count -eq 0) {
    Write-Warning 'Floaty is not installed for the current user - nothing to uninstall.'
}
else {
    Write-Warning "$($key.Count) registry entries match Floaty - skipping the automatic uninstall."
    $key | ForEach-Object { Write-Warning "- $($_.DisplayName)" }
}

Write-Host 'Your Floaty data in %USERPROFILE%\.floaty was left in place. Delete it by hand for a clean slate.'
