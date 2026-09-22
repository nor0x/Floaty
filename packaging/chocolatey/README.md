# Chocolatey

Package id: **`floaty`** · downloads `Floaty-win-Setup.exe` from the GitHub release.

```sh
choco install floaty
```

One package, not the `floaty.install` / `floaty.portable` split - that convention is for offering an
installer *and* an archive, and a 131 MB portable zip unpacked into `lib\floaty\tools` is not worth
the second package or the two copies fighting over `%USERPROFILE%\.floaty`.

## Layout

| File | What it is |
| --- | --- |
| `floaty.nuspec` | package metadata |
| `tools/chocolateyinstall.ps1` | downloads the installer and runs it with `--silent` |
| `tools/chocolateyuninstall.ps1` | fallback uninstall for users who disabled the AutoUninstaller |
| `update.ps1` | stamps a version + its checksum into the two files above |
| `icon.png` | 256x256, served through jsDelivr pinned to the release tag |

The installer is downloaded rather than embedded because it is ~136 MB; only embedded content
counts against the 200 MB package limit, and downloading from an official release URL is the
documented approach for anything this size.

## Releasing

Automatic, via the `chocolatey` job in `.github/workflows/release-windows.yml`: it runs
`update.ps1`, then `choco pack`, then `choco push` with `CHOCO_API_KEY`.

To do it by hand:

```sh
pwsh ./packaging/chocolatey/update.ps1 -Version 0.2.3
choco pack packaging/chocolatey/floaty.nuspec --out .
choco push floaty.0.2.3.nupkg --source https://push.chocolatey.org/
```

Test before pushing - a bad version costs a moderation round trip:

```sh
choco install floaty -s . -y --debug   # must exit 0 and leave no window open
choco list --local-only
choco uninstall floaty -y              # exercises the AutoUninstaller path
```

## Moderation

Every version is reviewed by a human, and this package has the profile that attracts questions: an
unsigned binary, a per-user install and a built-in updater. Expect the first few versions to need a
reply, and push the first two or three by hand rather than discovering a problem through CI. A
package sitting in "Waiting" is auto-rejected after 35 days of maintainer silence.

Things the package does deliberately, because they come up:

- **`validExitCodes = @(0)`**, not the MSI boilerplate `@(0, 3010, 1641)`. Velopack's setup exits 0
  or 1 and has no reboot path.
- **The per-user install is stated plainly in `<description>`** rather than left to be discovered.
- **The self-updater is stated too**, along with `choco pin add -n=floaty` for anyone who wants
  Chocolatey to own the version.
- **`requireLicenseAcceptance` is `false`** - MIT needs no acceptance.
- **No hardcoded uninstall path.** The uninstall script reads `QuietUninstallString` out of HKCU, so
  copies installed elsewhere with `--installto` still uninstall.
