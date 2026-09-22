# winget

Package identifier: **`nor0x.Floaty`** · installs `Floaty-win-Setup.exe`.

The real manifests live in
[`microsoft/winget-pkgs`](https://github.com/microsoft/winget-pkgs/tree/master/manifests/n/nor0x/Floaty).
The copies here are the reviewable reference for the shape we want, pinned to the version that was
submitted first. They are not read by the automation.

## Updating a version

Automatic. The `winget` job in `.github/workflows/release-windows.yml` runs
[`winget-releaser`](https://github.com/vedantmgoyal9/winget-releaser) after the release is
published; it drives Komac, which copies the previous version's manifests forward with the new
version, URL and hash.

## The first submission was manual

`winget-releaser` errors out until `manifests/n/nor0x/Floaty/` exists upstream. Bootstrap with:

```sh
komac new https://github.com/nor0x/Floaty/releases/download/v0.2.2/Floaty-win-Setup.exe
```

Use **Komac, not `wingetcreate`**. Velopack's `Setup.exe` stub is a 32-bit PE (`machine 0x14c`) even
for a win-x64 app; `wingetcreate` reads the stub and labels the package `Architecture: x86`. Komac
has a Velopack analyser that opens the nupkg embedded in the stub and reads the architecture from
`Floaty.exe`, and it emits the `--silent` / `--installto` switches and the `%LocalAppData%\Floaty`
install location for free.

## Testing locally

```sh
winget validate --manifest packaging/winget
winget install --manifest packaging/winget
winget list Floaty      # must report the version - this is the ProductCode correlation test
winget uninstall nor0x.Floaty
```

## Notes

- Schema **1.12.0**. Higher schema versions are documented but not supported by the shipping client.
- `AppsAndFeaturesEntries` pins `ProductCode` only - see the comment in the installer manifest.
- **Do not publish prerelease tags here.** Velopack strips the prerelease suffix when it writes
  `DisplayVersion` to the registry, so `0.3.0-beta.1` would never correlate.
- The installer is unsigned. That is not a policy violation (winget has no signing requirement, and
  other Velopack-packaged apps pass validation), but a Defender false positive in the validation
  pipeline is the most likely way a submission fails.
