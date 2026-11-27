# AI Cleaner for Windows v3.2

[简体中文](./README.md) | English

[![CI & Release](https://github.com/wwd177/ai-cleaner/actions/workflows/ci-release.yml/badge.svg)](https://github.com/wwd177/ai-cleaner/actions/workflows/ci-release.yml)
[![Latest Release](https://img.shields.io/github/v/release/wwd177/ai-cleaner)](https://github.com/wwd177/ai-cleaner/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011%20x64-0284C7.svg)](#quick-start)
[![.NET 10](https://img.shields.io/badge/.NET-10%20WPF-2563EB.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-0A7A4E.svg)](./LICENSE)

A native `.NET 10 + WPF` frosted-glass disk maintenance & space diagnostic utility for Windows 10 / 11 AI developers spanning **7 AI ecosystem zones**, complete with a **Cross-Zone Top-10 Overview Dashboard**, **live Chinese / English UI switching**, **controlled read-only community diagnostic packs**, **opt-in lightweight tray low-space alerts**, a versioned **CLI JSON interface**, an official **read-only Agent Skill**, and **Scoop / WinGet manifests**.

---

## What's New in v3.2 (Full Roadmap Complete)

- **7 Extensible AI Ecosystem Zones (`ZoneCatalog`)**:
  - **Antigravity (`antigravity`)**, **GPT / Codex (`gpt-codex`)**, **DeepSeek Harness (`deepseek-harness`)**, **Claude Code (`claude-code`, strict read-only)**, **AI Toolchains (`ai-toolchains`)**.
  - **AI IDEs Zone (`ai-ides`) — Cursor / Windsurf / Trae**: Verified top-level offline Electron cache cleanup with **per-IDE process locking** + O(1) read-only `state.vscdb` & `workspaceStorage` radar; permanently protects `User`, auth storage, extensions, and `CachedExtensionVSIXs`.
  - **Local AI & VHDX Radar (`local-ai-ecosystem`, strict read-only)**: Audits **WSL2 / Docker Desktop `ext4.vhdx`** virtual disk bloat (with `wsl --shutdown` + `Optimize-VHD` guidance), **Cline / Roo Code / Continue**, **ComfyUI**, and **controlled read-only community packs**.
- **Cross-Zone Overview Dashboard & Top-10 Heavyweights**: One-click global rollup across all 7 zones with clickable zone tiles and a Top-10 largest space consumers table.
- **Instant Bilingual UI (`EN / 中文`)**: Live zero-restart UI language toggle persisted in `settings.json`.
- **Controlled Read-Only Community Packs (`packs/`)**: Drop `mode: "read-only"` JSON packs into `%LOCALAPPDATA%\AICleaner\packs\` (see [packs/README.md](./packs/README.md) & [community-ai-radar.example.json](./packs/community-ai-radar.example.json)). Strictly blocks non-read-only modes, `..` traversal, wildcards, UNC paths, OS/credential dirs, and NTFS junctions.
- **Opt-In Lightweight Tray Low-Space Alert**: Off by default (zero background footprint, zero network telemetry). When explicitly enabled in Settings, monitors local fixed drives and notifies when free space drops below your threshold.
- **Scoop & WinGet Packaging Manifests**: Ready-to-submit manifests and sync script in [packaging/](./packaging/).

---

## Supported Zones & Safety Matrix

| Zone (`slug`) | Cleanable Targets | Read-Only Radar & Engine Redlines (Never Touched) |
| :--- | :--- | :--- |
| **Antigravity** (`antigravity`) | Updater leftovers, runtime caches, crash/client logs, stale brain artifacts, offline Electron/partition caches | `.gemini\antigravity\conversations`, `.gemini\config`, `.env`, auth storage, recent 72h sessions |
| **GPT / Codex** (`gpt-codex`) | Custom Codex cache root, validated workspace scratch & stale heavy dirs (Recycle Bin), weekly scheduled task | `%USERPROFILE%\.codex` & SQLite (O(1) telemetry only), MSIX package, `.codex` junctions, `runtimes` |
| **DeepSeek Harness** (`deepseek-harness`) | Updater leftovers, `.dsh\cache`, legacy backups, >14d attachments (Recycle Bin), offline caches | `.dsh\profiles`, `dsh-runtimes`, `skills` junctions, `sessions`, `.credentials.yaml`, auth storage |
| **Claude Code** (`claude-code`) | **None (Strict Read-Only, 0 deletable)** | `%USERPROFILE%\.claude` & `.claude.json` (`file-history`, `shell-snapshots`, `projects`, credentials) |
| **AI Toolchains** (`ai-toolchains`) | Expired `uv` / `pip` / `npm` download caches only (skipping NTFS junctions) | **Read-Only Radar**: `pnpm\store`, `ms-playwright`, `puppeteer`, `huggingface`, `modelscope`, `ollama`, `lm-studio` |
| **AI IDEs** (`ai-ides`) | Top-level offline Electron caches for Cursor / Windsurf / Trae (per-IDE process locked) | `User`, `state.vscdb`, `workspaceStorage`, extensions, `CachedExtensionVSIXs`, `.cursor`/`.codeium`/`.trae`, auth |
| **Local AI / VHDX** (`local-ai-ecosystem`) | **None (Strict Read-Only, 0 deletable)** | WSL2 / Docker `ext4.vhdx`, Cline / Roo Code / Continue, ComfyUI, controlled read-only community `packs/` |

---

## Quick Start

### Option 1: Download Prebuilt Binary
Grab `AICleaner-win-x64.zip` and `SHA256SUMS.txt` from [GitHub Releases](https://github.com/wwd177/ai-cleaner/releases/latest) (requires Windows 10/11 x64 + [.NET 10 Windows Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)).

### Option 2: Build from Source
```powershell
dotnet publish .\src\AICleaner.csproj -c Release -r win-x64 --no-self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
```

### CLI Read-Only Audit (`schemaVersion: 1.0`) & Self-Tests
```powershell
.\publish\AICleaner.exe --list-zones --json
.\publish\AICleaner.exe --dry-run --json
.\publish\AICleaner.exe --dry-run --json --zone local-ai-ecosystem
.\publish\AICleaner.exe --self-test .\selftest.txt
```

**Exit Codes & Precedence**: `0` = `ok` · `1` = runtime error · `2` = invalid arguments / unknown zone · `3` = degraded/aborted (`error > cancelled > partial > ok`).

---

## Contributing & License

See [CONTRIBUTING.md](./CONTRIBUTING.md), [skills/ai-cleaner/SKILL.md](./skills/ai-cleaner/SKILL.md), and [packs/README.md](./packs/README.md).
Licensed under the [MIT License](./LICENSE).
