# Packaging

Floaty ships through three Windows package managers. This folder holds the source of truth for the
two that need files in this repo, plus the manifest to copy into the scoop bucket.

| Manager | Artifact | Where the manifest lives | How it is updated |
| --- | --- | --- | --- |
| winget | `Floaty-win-Setup.exe` | `microsoft/winget-pkgs` (copy here for reference) | `winget` job in `release-windows.yml` |
| scoop | `Floaty-win-Portable.zip` | `nor0x/scoop-bucket` (copy in `scoop/`) | Excavator, in the bucket repo |
| Chocolatey | `Floaty-win-Setup.exe` | `chocolatey/` | `chocolatey` job in `release-windows.yml` |

## One-time setup

Secrets and accounts the automation needs. None of this is in the repo.

| What | Why |
| --- | --- |
| Fork `microsoft/winget-pkgs` to `nor0x` | `winget-releaser` pushes its branch there |
| Repo secret `WINGET_TOKEN` - a **classic** PAT with `public_repo` + `workflow` | fine-grained tokens do not work with `winget-releaser` |
| Repo `nor0x/scoop-bucket`, created from [`ScoopInstaller/BucketTemplate`](https://github.com/ScoopInstaller/BucketTemplate) | the bucket. Needs no secret - Excavator runs on `github.token` |
| Repo secret `CHOCO_API_KEY` from community.chocolatey.org | `choco push` |

## Things that are frozen

- **`vpk pack --packTitle` / `--packAuthors`.** They become `DisplayName` / `Publisher` in
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Floaty`, which is what package managers
  match an installed copy against. Changing either strands every existing install.
- **`vpk pack --channel`.** It names `releases.{channel}.json`, so changing it breaks Velopack
  self-update for every existing install.
- **Release assets, once published.** `Floaty-win-Setup.exe` and `Floaty-win-Portable.zip` have no
  version in the filename - their immutability comes entirely from the tag in the URL. Re-uploading
  an asset to an existing tag invalidates a published checksum, which is a hard Chocolatey
  moderation failure. Cut a new version instead.

## Self-update

winget and Chocolatey install the Velopack build, which updates itself from GitHub Releases on
launch, so those managers may report a version older than the one actually installed. This is
intentional. `choco pin add -n=floaty` stops Chocolatey from trying to "upgrade" it.

The scoop package is the portable build, where `UpdateService.IsSupported` is false, so scoop is in
sole charge of its version.
