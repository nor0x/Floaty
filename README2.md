<p align="center">
  <img src="website/ring-spin.svg" alt="Floaty" width="160" />
</p>

<h1 align="center">Floaty 🛟</h1>

<p align="center">
  A local-first AI assistant that floats on top of your desktop.
</p>

<p align="center">
  <a href="https://github.com/nor0x/Floaty/releases"><img src="https://img.shields.io/github/v/release/nor0x/Floaty?include_prereleases&label=release" alt="Latest release" /></a>
  <a href="https://github.com/nor0x/Floaty/actions/workflows/release-windows.yml"><img src="https://github.com/nor0x/Floaty/actions/workflows/release-windows.yml/badge.svg" alt="Release build" /></a>
  <img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10" />
</p>

---

Floaty lives in your tray / menubar and as a draggable swimming-ring overlay that stays on top of your other windows. Through the ring you can capture what's on your screen — a screenshot plus the actual text content read via accessibility APIs — and everything you capture is embedded into a local memory that the assistant can search when you chat with it.

Everything stays on your machine: memory, conversations, skills, settings, and speech-to-text all live in `~/.floaty`. The only thing that leaves your computer is what you send to the LLM provider you configure.

## ✨ Features

- **Floating ring overlay** — draggable (with natural ring rotation), borderless, always on top, and click-through in its transparent regions. Buttons for screenshot capture, voice input, chat, and settings; the ring image itself is customizable.
- **Screen capture & reading** — grabs a screenshot *and* the text content of the active window via UI Automation, so captures are searchable by meaning, not just stored as pixels.
- **Automatic screen history** — optionally records the foreground window (title and/or content) as you work, feeding your local memory without any manual capturing.
- **Local memory with vector search** — captures are embedded and stored in an on-device LiteGraph/SQLite database. The chat exposes a `search_captures` tool so the assistant can recall what you've seen.
- **Chat interface** — conversations with the configured LLM, saved locally as JSON. A user-editable system prompt lives at `~/.floaty/floaty.md`.
- **Voice input** 🎤 — fully local speech-to-text: Silero VAD segments your speech and [transcribe.cpp](https://github.com/handy-computer/transcribe.cpp) transcribes it with GGUF models (Whisper, Voxtral, …) downloaded on demand.
- **Agent skills** — drop SKILL.md-based skills into `~/.floaty/skills` (also picks up `~/.claude/skills` and `~/.agents/skills`) and invoke them with `/name` in chat.
- **MCP support** — connect Model Context Protocol servers and scope the chat to a server's tools with a `/server` slash command.
- **Auto-updates** — the Windows build updates itself in place via Velopack, fed from GitHub Releases.

## 📥 Installation

Grab the latest Windows installer from the [Releases page](https://github.com/nor0x/Floaty/releases). The app checks for updates itself after that.

On first run, open Settings to add your OpenAI API key (more providers are planned — the AI layer is built on the provider-agnostic `Microsoft.Extensions.AI`).

> **Platform support:** Windows is the primary platform with the full feature set. A Mac Catalyst target exists with the overlay working and other native features (screen capture, screen history, voice input, autostart) stubbed out for now.

## 📁 Local-first layout

Everything Floaty knows lives under `~/.floaty`:

| Path | Purpose |
| --- | --- |
| `config.json` | Settings and preferences |
| `floaty.md` | User-editable system prompt for the assistant |
| `floaty.db` | Local memory — capture embeddings + vector search (SQLite) |
| `captures/` | Screenshot + screen-content pairs |
| `conversations/` | Saved chat threads, one JSON file each |
| `skills/` | Agent skills, each a folder with a `SKILL.md` |
| `models/` | Downloaded speech-to-text models |
| `native/` | Downloaded native runtimes (transcribe.cpp) |
| `ring/` | Custom ring images for the overlay |

## 🛠️ Building from source

Prerequisites:

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (`app/global.json` pins `10.0.301`, rolling forward on feature bands)
- MAUI workload: `dotnet workload install maui-windows` (or `maui` on macOS)

```sh
cd app

# Windows
dotnet build -f net10.0-windows10.0.19041.0
dotnet run -f net10.0-windows10.0.19041.0

# macOS (Mac Catalyst)
dotnet build -f net10.0-maccatalyst
```

The solution file is `app/Floaty.slnx` if you prefer Visual Studio.

## 🧱 Tech stack

- **.NET MAUI** (with a Blazor hybrid `BlazorWebView` for the settings UI)
- **Microsoft.Extensions.AI** + OpenAI for chat, tools, and embeddings
- **LiteGraph** (SQLite) for local vector + graph memory
- **ModelContextProtocol** for MCP client support
- **WinUIEx**, UI Automation, and GDI for the Windows overlay and screen capture
- **NAudio** + **ONNX Runtime** (Silero VAD) + **transcribe.cpp** for local voice input
- **Velopack** for packaging and auto-updates

## 📂 Repository layout

```
app/       .NET MAUI application (Floaty.slnx, Floaty.csproj)
website/   Landing page deployed to GitHub Pages
.github/   CI: Windows release packaging + Pages deployment
```

See [AGENTS.md](AGENTS.md) for a deeper architectural guide (aimed at coding agents, useful for humans too).
