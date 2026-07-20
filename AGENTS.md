# AGENTS.md

Guidance for LLM-based coding agents working on Floaty. Humans welcome too.

## What Floaty is

A local-first AI desktop assistant built with .NET MAUI. It runs as a tray icon plus a borderless, always-on-top "swimming ring" overlay window. The overlay captures screenshots and screen text (via accessibility APIs), which get embedded into a local vector database; a chat window talks to an LLM (OpenAI via Microsoft.Extensions.AI) that can search that memory, invoke SKILL.md-based agent skills, and call tools from user-configured MCP servers. Voice input is transcribed fully locally.

## Tech stack

| Concern | Technology |
| --- | --- |
| UI framework | .NET MAUI (net10.0), XAML pages + one Blazor hybrid `BlazorWebView` for settings |
| Language / SDK | C# (nullable enabled, implicit usings), .NET SDK pinned in `app/global.json` (10.0.301, `rollForward: latestFeature`) |
| AI | `Microsoft.Extensions.AI` abstractions + `Microsoft.Extensions.AI.OpenAI` (chat, tool calls, embeddings) |
| Local memory | `LiteGraph` — embedded SQLite graph/vector store at `~/.floaty/litegraph.db` |
| MCP | `ModelContextProtocol` 2.0.0-preview.1 (`McpClientTool : AIFunction` plugs into `ChatOptions.Tools`) |
| Windows overlay | `WinUIEx` (transparent/borderless window), Win32 interop for click-through regions |
| Screen capture | UI Automation (`Interop.UIAutomationClient`) for text, GDI (`System.Drawing.Common`) for screenshots |
| Voice input | `NAudio` (mic) → Silero VAD via `Microsoft.ML.OnnxRuntime` → transcribe.cpp native lib (P/Invoke, GGUF models) |
| Packaging / updates | Velopack (unpackaged Windows build, `WindowsPackageType=None`), GitHub Releases feed |

## Repository layout

```
app/                        The MAUI app (Floaty.slnx solution, Floaty.csproj)
  App.xaml(.cs)             MAUI application entry, window management
  MainPage.xaml(.cs)        Chat UI (XAML)
  OverlayPage.xaml(.cs)     The floating ring overlay
  SettingsPage.xaml(.cs)    Hosts the BlazorWebView
  MauiProgram.cs            DI registrations (see "Service pattern" below)
  Components/               Blazor part (Settings.razor is the main settings UI)
  Services/                 All shared logic — interfaces + cross-platform services
  Platforms/Windows/        Real implementations of platform interfaces + VAD/STT interop
  Platforms/MacCatalyst/    MacOverlayWindowController (overlay only, rest is stubbed)
  wwwroot/                  Static assets for the Blazor settings UI
website/                    Static landing page (GitHub Pages)
.github/workflows/          release-windows.yml (tag v* → Velopack release), deploy-pages.yml
```

## Architecture

### Service pattern (the most important convention)

All features are defined as interfaces in `app/Services/` and registered as singletons in `MauiProgram.cs` behind `#if WINDOWS` / `#if MACCATALYST` blocks:

- `I<Feature>Service` interface in `app/Services/`
- `Windows<Feature>Service` in `app/Platforms/Windows/` — the real implementation
- `Null<Feature>Service` in `app/Services/` — no-op fallback for platforms without the feature

When adding platform functionality, follow this triple exactly: interface, Windows implementation, Null fallback, plus the conditional DI registration. Platform-specific services hook their own lifecycle; shared code resolves only the interface.

Key services:

- `ChatService` — builds an `IChatClient` from settings, rebuilds on config change; exposes the `search_captures` AI tool and, when the user scopes chat with `/server`, that MCP server's tools.
- `MemoryService` — OpenAI embeddings persisted to LiteGraph; one node per capture with embedding + metadata.
- `McpService` — connects/caches MCP clients per configured server; cache cleared on settings change.
- `SkillService` — scans `~/.floaty/skills`, `~/.claude/skills`, `~/.agents/skills` for SKILL.md folders (YAML frontmatter + markdown body); invoked via `/name` slash commands in chat.
- `SettingsService` / `FloatyConfig` — loads/persists `~/.floaty/config.json`; other services subscribe to change notifications.
- `FloatyPaths` — static accessors for every `~/.floaty` subdirectory (ensures dirs exist). Always use this instead of composing paths manually.
- `WindowsScreenHistoryService` — foreground-window/title watcher that auto-records into memory per `FloatyConfig.ScreenHistoryMode`.
- `NativeRuntimeService` / `ModelDownloadService` / `SttModelCatalog` — download transcribe.cpp native runtime (version pinned in `NativeRuntimeService.Version`) and GGUF STT models into `~/.floaty/native` and `~/.floaty/models` at first use; they are not packaged with the app.
- `UpdateService` — Velopack-based self-update from GitHub Releases; only active when running as an installed app.

### Windows overlay specifics

`WindowsOverlayWindowController` + `OverlayPage` implement the ring: borderless transparent window (WinUIEx), always-on-top toggle, drag with rotation, and click-through for transparent pixel regions (mouse events pass to the window below). Be careful editing this area — the transparency/click-through interop is fragile and Windows-specific.

### Voice input pipeline (Windows)

`WindowsVoiceInputService` orchestrates: `WindowsAudioCaptureService` (NAudio mic capture) → `SileroVadDetector` (ONNX Runtime, hand-rolled Silero VAD port) segments speech → `TranscribeNative` (P/Invoke bindings to transcribe.cpp) transcribes each segment. Events are raised on worker threads — marshal to the UI thread in consumers.

## Data: `~/.floaty`

All user data is local-first under the home directory: `config.json`, `floaty.md` (user system prompt), `litegraph.db`, `captures/`, `conversations/` (one JSON per thread), `skills/`, `models/`, `native/`, `ring/`. Never hardcode these paths — go through `FloatyPaths`.

## Build & run

```sh
cd app
dotnet workload install maui-windows   # once
dotnet build -f net10.0-windows10.0.19041.0
dotnet run -f net10.0-windows10.0.19041.0
```

Mac Catalyst: `dotnet build -f net10.0-maccatalyst` (requires macOS + `maui` workload).

There is no test suite currently; verify changes by building and, for UI/interop work, running the app.

## Version pins & gotchas

- **WinUIEx must stay aligned with the WindowsAppSDK version the MAUI workload pins** — see the comment in `Floaty.csproj`; bumping WinUIEx past what WindowsAppSDK supports breaks the build.
- **ModelContextProtocol is a preview package** (2.0.0-preview.1); its API surface may shift on update.
- **transcribe.cpp is pre-1.0** — its ABI can change between 0.x versions. Its version is pinned in `NativeRuntimeService.Version` and the runtime is downloaded at first use, so a bump there must match the P/Invoke signatures in `TranscribeNative.cs`.
- **XAML source generation is enabled** (`MauiXamlInflator=SourceGen`); XAML errors surface at compile time.
- `Floaty.slnx` is the newer XML solution format — some tooling only knows `.sln`.

## Conventions

- Comment style: services and non-obvious members carry `///` XML doc summaries explaining *why* and cross-referencing related types (see any file in `app/Services/`). Match this density.
- Emoji are used in UI-facing comments/labels where they aid recognition (e.g. "the 📷 button") — this is intentional.
- Config-reactive services subscribe to `SettingsService` change events and rebuild cached clients rather than reading config per call.

## CI / releases

- `release-windows.yml`: runs on `v*` tags or manual dispatch; builds the Windows app, packages with the `vpk` (Velopack) CLI, generates release notes with DiffLog, publishes a GitHub Release — which the in-app `UpdateService` consumes.
- `deploy-pages.yml`: manual dispatch; deploys `website/` to GitHub Pages.
