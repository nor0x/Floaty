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
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="MIT license" /></a>
</p>

---

Floaty lives in your tray / menubar and as a draggable swimming-ring overlay that stays on top of your other windows. Through the ring you can capture what's on your screen - a screenshot plus the actual text content read via accessibility APIs - and everything you capture is embedded into a local memory that the assistant can search when you chat with it.

Everything stays on your machine: memory, conversations, skills, settings, and speech-to-text all live in `~/.floaty`. The only thing that leaves your computer is what you send to the LLM provider you configure - and with on-device embeddings plus a local model in Ollama, nothing has to.

## ✨ Features

- **Floating ring overlay** - draggable (with natural ring rotation), borderless, always on top, and click-through in its transparent regions. Buttons for screenshot capture, voice input, chat, and settings; the ring image itself is customizable. Capturing snaps the ring shut like a camera shutter.
- **Sound effects** 🔊 - a shutter sound on capture and a chime when a reply finishes, each toggleable and swappable for a built-in or your own file in `~/.floaty/sounds`.
- **Screen capture & reading** - grabs a screenshot *and* the text content of the active window via UI Automation, so captures are searchable by meaning, not just stored as pixels.
- **Automatic screen history** - optionally records the foreground window (title and/or content) as you work, feeding your local memory without any manual capturing.
- **Written to be read by an LLM** - in text-only mode the day's activity becomes a single markdown file of time-stamped blocks. Interface junk is filtered out, each line is written once per day no matter how often it's on screen, and every block records the URL or document path it was looking at so your assistant can open the real thing instead of trusting a fragment.
- **Redacted before it's written** - password managers and private-browsing windows are never captured, password fields are skipped at the source, and credentials, API keys and card-shaped numbers are scrubbed before anything touches disk.
- **Local memory with vector search** - captures are embedded and stored in an on-device LiteGraph/SQLite database. The chat exposes a `search_captures` tool so the assistant can recall what you've seen.
- **Any model provider** 🔌 - configure OpenAI, Anthropic, Gemini, Azure OpenAI, OpenRouter, Groq, Mistral, DeepSeek, xAI, Ollama, or any OpenAI-compatible endpoint side by side, each on its own tab. Chat, embeddings and screenshot captioning are assigned independently, so you can mix providers.
- **On-device embeddings** 💻 - download a small ONNX sentence-transformer (BGE, MiniLM) and Floaty embeds every capture in-process. Screen history runs constantly, so this is the difference between it costing something per window switch and costing nothing - and it keeps working with no API key at all. Pair it with a local vision model in Ollama for fully offline memory.
- **Chat interface** - conversations with the configured LLM, saved locally as JSON. A user-editable system prompt lives at `~/.floaty/floaty.md`.
- **Voice input** 🎤 - fully local speech-to-text: Silero VAD segments your speech and [transcribe.cpp](https://github.com/handy-computer/transcribe.cpp) transcribes it with GGUF models (Whisper, Voxtral, …) downloaded on demand.
- **Agent skills** - drop SKILL.md-based skills into `~/.floaty/skills` (also picks up `~/.claude/skills` and `~/.agents/skills`) and invoke them with `/name` in chat.
- **MCP support** - connect Model Context Protocol servers and scope the chat to a server's tools with a `/server` slash command.
- **Auto-updates** - the Windows build updates itself in place via Velopack, fed from GitHub Releases.

## 📥 Installation

```sh
winget install nor0x.Floaty
```

```sh
choco install floaty
```

```sh
scoop bucket add nor0x https://github.com/nor0x/scoop-bucket
scoop install nor0x/floaty
```

Or grab the installer straight from the [Releases page](https://github.com/nor0x/Floaty/releases).

winget, Chocolatey and the direct download all install the same thing: a per-user install in `%LocalAppData%\Floaty` that keeps itself up to date, so those package managers may report a version older than the one you are actually running. That is on purpose - run `choco pin add -n=floaty` if you would rather Chocolatey left it alone. The scoop package is the portable build instead, which does not self-update, so scoop owns the version.

On first run, open Settings > Model Provider and add a provider. OpenAI, Anthropic, Google Gemini, Azure OpenAI, OpenRouter, Groq, Mistral, DeepSeek and xAI have one-click presets; Ollama, LM Studio, llama.cpp's server or any other OpenAI-compatible endpoint work through the Ollama and Custom entries.

> **Platform support:** Windows is the primary platform with the full feature set. A Mac Catalyst target exists with the overlay working and other native features (screen capture, screen history, voice input, autostart) stubbed out for now.

## 📁 Local-first layout

Everything Floaty knows lives under `~/.floaty`:

| Path | Purpose |
| --- | --- |
| `config.json` | Settings and preferences |
| `floaty.md` | User-editable system prompt for the assistant |
| `floaty.db` | Local memory - capture embeddings + vector search (SQLite) |
| `captures/` | Screen history: one markdown file per day plus per-capture blocks (and screenshots, in screenshot mode). An `AGENTS.md` in there explains the format |
| `conversations/` | Saved chat threads, one JSON file each |
| `generated/` | Images made by the chat's image tools |
| `skills/` | Agent skills, each a folder with a `SKILL.md` |
| `models/` | Downloaded speech-to-text models (`models/embed/` for on-device embedding models) |
| `native/` | Downloaded native runtimes (transcribe.cpp) |
| `ring/` | Custom ring images for the overlay |
| `sounds/` | Custom sound effects for capture / reply-finished |

## 🛠️ Building from source

Prerequisites:

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (`app/global.json` pins `10.0.301`, rolling forward on feature bands)

No workloads to install.

```sh
cd app
dotnet build
dotnet run
```

Floaty currently builds for Windows only. macOS support is planned but not wired up: see the
"macOS" note in [AGENTS.md](AGENTS.md) for what it needs.

The solution file is `app/Floaty.slnx` if you prefer Visual Studio.

## 🧱 Tech stack

- **Avalonia 12** — all-native UI, no embedded browser anywhere
- **Microsoft.Extensions.AI** for chat, tools, and embeddings, over OpenAI / Azure / Anthropic / any OpenAI-compatible endpoint
- **LiteGraph** (SQLite) for local vector + graph memory
- **ModelContextProtocol** for MCP client support
- **Win32 interop**, UI Automation, and GDI for the Windows overlay and screen capture
- **NAudio** + **ONNX Runtime** (Silero VAD) + **transcribe.cpp** for local voice input
- **Velopack** for packaging and auto-updates

## 📂 Repository layout

```
app/       Avalonia application (Floaty.slnx, Floaty.csproj)
packaging/ winget / scoop / Chocolatey manifests (see packaging/README.md)
website/   Landing page deployed to GitHub Pages
.github/   CI: Windows release packaging + Pages deployment
```

See [AGENTS.md](AGENTS.md) for a deeper architectural guide (aimed at coding agents, useful for humans too).
