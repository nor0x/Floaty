# AGENTS.md

Guidance for LLM-based coding agents working on Floaty. Humans welcome too.

## What Floaty is

A local-first AI desktop assistant built with Avalonia. It runs as a tray icon plus a borderless, always-on-top "swimming ring" overlay window. The overlay captures screenshots and screen text (via accessibility APIs), which get embedded into a local vector database; a chat window talks to an LLM (any configured provider, via Microsoft.Extensions.AI) that can search that memory, invoke SKILL.md-based agent skills, and call tools from user-configured MCP servers. Embeddings and screenshot captioning can run on-device. Voice input is transcribed fully locally.

## Tech stack

| Concern | Technology |
| --- | --- |
| UI framework | Avalonia 12 (`net10.0-windows10.0.19041.0`), all-native axaml - no WebView anywhere |
| Language / SDK | C# (nullable enabled, implicit usings), .NET SDK pinned in `app/global.json` (10.0.301, `rollForward: latestFeature`) |
| AI | `Microsoft.Extensions.AI` abstractions over several provider bindings - `Microsoft.Extensions.AI.OpenAI` (OpenAI plus every OpenAI-compatible endpoint), `Azure.AI.OpenAI`, `Anthropic` (official C# SDK), and in-process ONNX embeddings |
| Local memory | `LiteGraph` - embedded SQLite graph/vector store at `~/.floaty/floaty.db` |
| MCP | `ModelContextProtocol` 2.0.0-preview.1 (`McpClientTool : AIFunction` plugs into `ChatOptions.Tools`) |
| Windows overlay | Avalonia `Window` (`WindowDecorations=None`, `TransparencyLevelHint=Transparent`) + Win32 interop for click-through regions |
| Screen capture | UI Automation (`Interop.UIAutomationClient`) for text, GDI (`System.Drawing.Common`) for screenshots |
| Voice input | `NAudio` (mic) → Silero VAD via `Microsoft.ML.OnnxRuntime` → transcribe.cpp native lib (P/Invoke, GGUF models) |
| Packaging / updates | Velopack (unpackaged Windows build, `WindowsPackageType=None`), GitHub Releases feed |

## Repository layout

```
app/                        The Avalonia app (Floaty.slnx solution, Floaty.csproj)
  Program.cs                Entry point: Velopack hooks, then Avalonia bootstrap
  App.axaml(.cs)            Application + composition root (DI, tray icon, accent resources)
  Views/OverlayWindow       The floating ring overlay - a top-level Window, not a hosted page
  Views/Chat/               ChatPanelView, ChatWindow(+Host), MarkdownPresenter
  Views/Settings/           SettingsWindow + one view per section
  ViewModels/Settings/      SettingsViewModel (+ .Surface.cs binding projection)
  Ui/Anim.cs                Tick-loop tweens (replaces MAUI's FadeTo/RotateTo/Animation)
  Services/                 All shared logic - interfaces + cross-platform services
  Platforms/Windows/        Real implementations of platform interfaces + VAD/STT interop
  Platforms/MacCatalyst/    Reference only, NOT compiled (see "Build & run" below)
website/                    Static landing page (GitHub Pages)
.github/workflows/          release-windows.yml (tag v* → Velopack release), deploy-pages.yml
```

## Architecture

### Service pattern (the most important convention)

All features are defined as interfaces in `app/Services/` and registered as singletons in
`App.axaml.cs`'s `ConfigureServices` behind `#if WINDOWS` blocks:

- `I<Feature>Service` interface in `app/Services/`
- `Windows<Feature>Service` in `app/Platforms/Windows/` - the real implementation
- `Null<Feature>Service` in `app/Services/` - no-op fallback for platforms without the feature

When adding platform functionality, follow this triple exactly: interface, Windows implementation, Null fallback, plus the conditional DI registration. Platform-specific services hook their own lifecycle; shared code resolves only the interface.

Key services:

- `AiClientFactory` - **the only place a model client is constructed.** Turns `FloatyConfig.Providers` plus the three role assignments (`ChatRole` / `EmbeddingRole` / `VisionRole`) into `IChatClient` / `IEmbeddingGenerator`, caching one per role and dropping them on `SettingsService.Changed`. Dispatch is by `ProviderKind`, and `OpenAiCompatible` is deliberately one branch for many vendors: Gemini, OpenRouter, Groq, Mistral, DeepSeek, xAI, Ollama, LM Studio and llama.cpp's server differ only by base URL. `IsConfigured(role)` is the single answer to "is this feature usable" - never test an API key directly.
- `ProviderPresets` / `ConfigMigration` - the preset table behind the "+ Add" list, and the load-time upgrade that folds a pre-multi-provider `config.json` (one OpenAI key plus three model ids) into a provider profile. `ConfigMigration.Apply` runs on **every** load, so every step must be idempotent.
- `ChatService` - gets its `IChatClient` from `AiClientFactory`; exposes the `search_captures` and `read_capture` AI tools and, when the user scopes chat with `/server`, that MCP server's tools. Search hits show the passage that matched (`TextChunker.BestWindow`), not the opening; `read_capture` reads the rest from the saved file, resolved by name inside Floaty's own capture folders only.
- `MemoryService` - embeddings (from whichever provider holds the embedding role) persisted to LiteGraph. One node per capture carrying **one vector per chunk** (`TextChunker`), embedded in batches; nothing is truncated. Search over-fetches, groups hits by capture, and re-scores the capture's chunks locally to return the passage that matched — LiteGraph's result names the node but not which vector won. Enables an HNSW vector index (`~/.floaty/floaty.vectors.db`) after the first store; auto-capture count/delete filter `Source` through LiteGraph rather than reading every node. `ReindexCapturesAsync` re-chunks existing captures from their saved text file (Settings → Screen History). `DeleteAutoCapturesAsync` also clears the day logs, which no node references.
- `McpService` - connects/caches MCP clients per configured server; cache cleared on settings change.
- `SkillService` - scans `~/.floaty/skills`, `~/.claude/skills`, `~/.agents/skills` for SKILL.md folders (YAML frontmatter + markdown body); invoked via `/name` slash commands in chat.
- `SettingsService` / `FloatyConfig` - loads/persists `~/.floaty/config.json`; other services subscribe to change notifications.
- `FloatyPaths` - static accessors for every `~/.floaty` subdirectory (ensures dirs exist). Always use this instead of composing paths manually.
- `WindowsScreenHistoryService` - foreground-window/title watcher that auto-records into memory per `FloatyConfig.ScreenHistoryMode`. Tunables (dwell, per-window cooldown, global floor) are constants at the top. **Text-only** mode takes its own path (`CaptureTextOnlyAsync`): the window is read without touching disk (`IScreenCaptureService.ReadWindowAsync`), then redacted, pruned and reduced to the day's novel lines before anything is stored. `TextAndScreenshot` and every manual capture still go through `CaptureWindowAsync` and its raw `.txt` dump.
- `CaptureDedupe` - bounded LRU ledger of recently captured windows plus a SimHash content fingerprint, so screen history doesn't re-embed screens it already stored when the user cycles between windows. In text-only mode the fingerprint is taken over *pruned* text, which is a far better signal than the raw walk.
- `CapturePruner` / `CaptureRedactor` - the text-only pipeline's two pure stages, in that call order: scrub credential-shaped strings (and drop password-manager / private-browsing windows whole), then throw away interface junk. A line survives pruning only if it reads like content (4+ words) or is short but identifying (URL, path, digit, email). Both are dependency-free, so they can be exercised in isolation.
- `CaptureDayLog` - owns `~/.floaty/captures/YYYY-MM-DD.md` and the ledger of lines already written today, so a line is stored and embedded once per day however many windows repeat it. Two-phase on purpose: `SelectNovel` asks what's new without recording, `Commit` runs only after the capture actually reached memory. The ledger is rebuilt by re-reading the day file rather than kept in a sidecar, so it survives restarts and self-heals if the user edits the file. Also renders the markdown blocks and seeds the folder's `AGENTS.md`.
- `TextChunker` - splits capture text into overlapping line-aware chunks for embedding, and picks the query-relevant window to display. Pure and dependency-free, so it can be exercised in isolation.
- `NativeRuntimeService` / `ModelDownloadService` / `SttModelCatalog` / `LocalModelCatalog` - download the transcribe.cpp native runtime (version pinned in `NativeRuntimeService.Version`), GGUF STT models, and ONNX embedding models into `~/.floaty/native`, `~/.floaty/models` and `~/.floaty/models/embed` at first use; none are packaged with the app. `ModelDownloadService.DownloadFilesAsync` is the shared core both catalogs go through.
- `ILocalEmbeddingFactory` / `OnnxEmbeddingGenerator` - on-device embeddings: an ONNX sentence-transformer run by the same ONNX Runtime as the voice VAD, tokenized by `Microsoft.ML.Tokenizers`' `BertTokenizer` from the model's `vocab.txt`. This is what lets memory and screen history run with no cloud key at all. Catalog entries must be WordPiece/BERT models - XLM-R multilingual encoders ship SentencePiece and would need a second tokenizer path.
- `OllamaProbe` - reads `/api/tags` off a local Ollama so its provider tab can list pulled models. Inference still goes through Ollama's OpenAI-compatible endpoint like every other provider.
- `ISoundService` / `WindowsSoundService` - Floaty's own feedback sounds (capture shutter, assistant-reply finished). One long-lived NAudio `WaveOutEvent` + `MixingSampleProvider` with decoded clips cached per selection; built-ins ship in `app/Resources/Sounds` (CC0, see its CREDITS.md) and users can drop their own into `~/.floaty/sounds`. Fire-and-forget and failure-swallowing by design — audio must never break a capture or a chat turn. Also serves the Settings audition button via `SettingsService.SoundPreviewRequested`.
- `UpdateService` - Velopack-based self-update from GitHub Releases; only active when running as an installed app.

### Windows overlay specifics

`AvaloniaOverlayWindowController` + `OverlayWindow` implement the ring: borderless transparent window,
always-on-top toggle, drag with rotation, and click-through for transparent pixel regions (mouse events
pass to the window below). Be careful editing this area - the click-through interop is fragile and
Windows-specific.

Three non-obvious rules govern it, all found the hard way:

- **`InputHitTest` returning non-null does not mean "interactive".** Avalonia's window template owns a
  chrome `Panel` with a Transparent background that is hit at *every* point in the window, whatever
  `Window.Background` is. The result must be checked for ancestry under `ContentRoot`.
- **`Image` hit-tests by its layout rect, not by alpha**, so the ring needs an explicit circle test. And
  because `TranslatePoint` applies render transforms, that test translates the ring's *centre* - the
  fixed point of the rotate/scale - not its origin, which orbits as the idle spin turns it.
- **`AvaloniaBorderlessWindowController` tracks the size it last asked for** rather than reading
  `ClientSize` back. Avalonia applies `Width`/`Height` through layout, so an anchored resize issued
  before the previous one has been laid out would anchor against a stale rect and walk the window
  across the screen.

### Voice input pipeline (Windows)

`WindowsVoiceInputService` orchestrates: `WindowsAudioCaptureService` (NAudio mic capture) → `SileroVadDetector` (ONNX Runtime, hand-rolled Silero VAD port) segments speech → `TranscribeNative` (P/Invoke bindings to transcribe.cpp) transcribes each segment. Events are raised on worker threads - marshal to the UI thread in consumers.

## Data: `~/.floaty`

All user data is local-first under the home directory: `config.json`, `floaty.md` (user system prompt), `floaty.db`, `captures/`, `conversations/` (one JSON per thread), `skills/`, `models/` (with `models/embed/` for on-device embedding models), `native/`, `ring/`, `sounds/`. Never hardcode these paths - go through `FloatyPaths`.

## Build & run

```sh
cd app
dotnet build
dotnet run
```

No workload install is needed. After changing any `.axaml`, use `dotnet build -t:Rebuild` - see the
gotchas below.

**macOS is not currently built.** `Platforms/MacCatalyst/` is kept as reference only: its
`MacOverlayWindowController` reaches AppKit through UIKit's semi-private `UIWindow.nsWindow` KVC bridge,
which does not exist under Avalonia. macOS also has no per-region click-through
(`NSWindow.ignoresMouseEvents` is window-wide), so the ring's hit-test model needs a fresh design there
rather than a port. Starting a macOS head means adding a `net10.0` TFM back and restoring the
Null-service registrations.

There is no test suite currently; verify changes by building and, for UI/interop work, running the app.

## Version pins & gotchas

- **`dotnet build` skips the Avalonia XAML compiler when its inputs are unchanged**, so an incremental
  "Build succeeded" can hide real XAML errors. Use `-t:Rebuild` after touching `.axaml`.
- **Avalonia's font matching is substring-based at two levels, and both bite.** A `FontFamily` source
  ending in `.ttf` is a *pattern*, not a path — `FontFamilyLoader` filters the folder's assets with
  `IndexOf(pattern) >= 0` — so `tabler-icons.ttf` would also pull in a sibling `tabler-icons-filled.ttf`.
  And family lookup is `glyphTypeface.FamilyName.Contains(familyName)`, so a font *named*
  `tabler-icons-filled` still satisfies a request for `tabler-icons`. Two fonts whose names or
  filenames are substrings of each other therefore resolve to whichever wins, silently, with no error
  and no tofu — just nothing. This blanked every icon in the app once (see `App.axaml`, which is why
  the filled font is `tabler-solid`). Keep icon font names and filenames mutually non-overlapping.
- **There is no DevTools on Avalonia 12**: `Avalonia.Diagnostics` stops at 11.3.20 and the visual-tree
  inspector is not in core. Debug layout by probing from code.
- **Verification harnesses must be DPI-aware.** A DPI-unaware `GetWindowRect` reports *virtualised*
  coordinates (physical / RenderScaling), which makes correct behaviour look broken.
- **ModelContextProtocol is a preview package** (2.0.0-preview.1); its API surface may shift on update.
- **The `Anthropic` package is versioned 10+ but still beta upstream** - breaking changes can land in minor releases, so it stays pinned.
- **Never gate a feature on an API key.** `FloatyConfig`'s `OpenAiApiKey` / `Model` / `EmbeddingModel` / `SnapshotModel` are legacy migration inputs only (nullable + `WhenWritingNull`, so they vanish from `config.json` after one save). Ask `AiClientFactory.IsConfigured` or `IMemoryService.CanRemember` instead.
- **`SettingsViewModel.Initialize` hand-copies every `FloatyConfig` property into a working
  clone**, and the clone is saved wholesale - a new property omitted there is silently reset to its
  default on Save. This trap survived the Blazor to Avalonia port unchanged.
- **transcribe.cpp is pre-1.0** - its ABI can change between 0.x versions. Its version is pinned in `NativeRuntimeService.Version` and the runtime is downloaded at first use, so a bump there must match the P/Invoke signatures in `TranscribeNative.cs`.
- `Floaty.slnx` is the newer XML solution format - some tooling only knows `.sln`.

## Conventions

- Comment style: services and non-obvious members carry `///` XML doc summaries explaining *why* and cross-referencing related types (see any file in `app/Services/`). Match this density.
- Emoji are used in UI-facing comments/labels where they aid recognition (e.g. "the 📷 button") - this is intentional.
- UI animation goes through `Ui/Anim.cs` rather than Avalonia's styling-driven animation system: the
  ring's angle is written by drags, the wheel, the idle spin and four separate flourishes, which a
  declarative animation cannot share.
- Config-reactive services subscribe to `SettingsService` change events and rebuild cached clients rather than reading config per call.

## CI / releases

- `release-windows.yml`: runs on `v*` tags or manual dispatch; publishes a self-contained win-x64 build
  (`-p:Version=$VERSION`, which `UpdateService` reads back from the assembly), packages with the `vpk`
  (Velopack) CLI, generates release notes with DiffLog, publishes a GitHub Release - which the in-app
  `UpdateService` consumes. A publish target drops native `.pdb` symbols in Release: SkiaSharp and
  HarfBuzz ship ~100 MB of them.
- `deploy-pages.yml`: manual dispatch; deploys `website/` to GitHub Pages.
