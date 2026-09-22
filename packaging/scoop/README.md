# scoop

Bucket: **[`nor0x/scoop-bucket`](https://github.com/nor0x/scoop-bucket)** · installs
`Floaty-win-Portable.zip`.

```sh
scoop bucket add nor0x https://github.com/nor0x/scoop-bucket
scoop install nor0x/floaty
```

`bucket/floaty.json` here is the copy to drop into `bucket/` in the bucket repo. After that, this
repo's CI has nothing to do with scoop: the bucket's own Excavator workflow polls every four hours,
picks up new tags through `checkver.github`, downloads the zip, hashes it and commits the bump.

Create the bucket repo from
[`ScoopInstaller/BucketTemplate`](https://github.com/ScoopInstaller/BucketTemplate) - it ships the
`ci`, `excavator`, `issues` and `pull_request` workflows already wired up, and needs no secret
beyond the built-in `github.token`.

## Why the portable build

`UpdateService.IsSupported` is false outside a Velopack install, so the portable build never tries
to update itself and scoop stays in sole charge of the version. The `Setup.exe` build would fight
scoop over that.

## Manifest choices

- **No `extract_dir`** - `Floaty.exe` sits at the zip root.
- **No `bin`** - Floaty is a GUI app with no command-line surface, so a shim would only add a
  pointless `floaty` command to PATH. `shortcuts` is the right mechanism.
- **No `persist`** - state lives in `~/.floaty`, outside the scoop tree, and survives updates and
  uninstalls on its own. `notes` covers the uninstall caveat instead.
- **No `hash` block under `autoupdate`** - omitted deliberately, so Excavator downloads and hashes
  the zip itself. That has no format to drift, unlike parsing a checksums file.

Expect `scoop install` to take a while: the zip is ~131 MB and holds around a thousand files.

Submitting to `ScoopInstaller/Extras` later would improve discovery, since most people already have
that bucket. The personal bucket works today without waiting on review.
