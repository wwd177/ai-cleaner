# AI Cleaner for Windows v3.0

[简体中文](./README.md) | English

[![CI & Release](https://github.com/wwd177/ai-cleaner/actions/workflows/ci-release.yml/badge.svg)](https://github.com/wwd177/ai-cleaner/actions/workflows/ci-release.yml)
[![Latest Release](https://img.shields.io/github/v/release/wwd177/ai-cleaner)](https://github.com/wwd177/ai-cleaner/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011%20x64-0284C7.svg)](#quick-start)
[![.NET 10](https://img.shields.io/badge/.NET-10%20WPF-2563EB.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-0A7A4E.svg)](./LICENSE)

A native `.NET 10 + WPF` frosted-glass disk maintenance & space diagnostic utility for Windows 10 / 11 AI developers, paired with a versioned **CLI JSON interface**, an official **read-only Agent Skill**, and zero-dependency `CodexCleaner` PowerShell automation scripts.

Built for heavy users of **Antigravity**, **GPT / Codex Desktop (ChatGPT)**, **DeepSeek Harness (DSH)**, **Claude Code**, and AI developer toolchains (**uv / pip / npm / pnpm / Playwright / Hugging Face / Ollama / LM Studio**). Reclaim gigabytes safely **without ever terminating running AI apps, touching conversations/edit rollback snapshots, corrupting login state, or traversing NTFS junctions**.

---

## What's New in v3.0

- **Extensible 5-Zone Engine (`ZoneCatalog`)**: Data-driven sidebar with automatic local installation detection and collapsible grouping for undetected zones.
- **Claude Code Zone (Strict Read-Only Diagnostic)**: Inspects `~/.claude` (`debug`, `file-history`, `shell-snapshots`, `projects`, and unknown directories) with interactive **Space Radar** drill-down — strictly **0 deletable candidates** so edit rollback history and credentials are never harmed.
- **AI Toolchains & Model Radar Zone**:
  - **Safe Online Clean (Green)**: Expired pure download caches for `uv`, `pip`, and `npm` (automatically skips NTFS junctions and locked files).
  - **Strict Read-Only Radar**: `pnpm/store` (hardlink store), `Playwright` / `Puppeteer` browser runtimes, and `Hugging Face` / `ModelScope` / `Ollama` / `LM Studio` model caches — never auto-deleted and never counted toward reclaimable space.
- **GUI Workspace & Protected-Directory Manager**: Two-tab Settings dialog using Windows native `OpenFolderDialog` to manage `CustomCodexCacheRoot`, `ExtraWorkspaceRoots`, and `ExtraProtectedSubtrees` without hand-editing JSON.
- **Versioned CLI JSON (`schemaVersion: "1.0"`)**: Clean UTF-8 stdout & `--output <path>` support with deterministic exit codes (`0 / 1 / 2 / 3`) and scan telemetry (`status`, `accessDeniedCount`, `elapsedMs`).
- **Official Read-Only Agent Skill**: Drop [skills/ai-cleaner/SKILL.md](./skills/ai-cleaner/SKILL.md) into Antigravity, Claude Code, or Codex to run safe disk audits on demand.
- **Automated CI & Verified GitHub Releases**: Builds single-file `win-x64` packages, generates `SHA256SUMS.txt`, extracts the exact release ZIP, and runs all 62 self-tests + non-empty CLI contract checks before publishing.

---

## Supported Zones & Safety Matrix

| Zone (`slug`) | Cleanable Targets | Read-Only Radar & Engine Redlines (Never Touched) |
| :--- | :--- | :--- |
| **Antigravity**<br>`antigravity` | Updater leftovers, runtime caches, crash/client logs, stale session artifacts, offline Electron/partition caches | `.gemini\antigravity\conversations`, `.gemini\config`, `.env`, auth state (`Network`, `Local Storage`, `IndexedDB`, `Service Worker\Database`), recent 72h sessions |
| **GPT / Codex**<br>`gpt-codex` | Optional custom Codex cache root (`temp`, `pip`, `npm`, `uv`, offline `browser-cache` & `desktop-logs`), custom workspace `_tmp`/`__pycache__`/`.pytest_cache`/`.ruff_cache` & stale heavy folders (Recycle Bin), built-in weekly task toggle | `%USERPROFILE%\.codex` & SQLite databases (O(1) read-only size check only), `OpenAI.Codex` MSIX package, all `.codex` NTFS junctions & real targets, `runtimes` |
| **DeepSeek Harness**<br>`deepseek-harness` | Updater leftovers, `.dsh\cache`, legacy config backups, >14-day attachments (Recycle Bin), offline desktop/partition caches | `.dsh\profiles` (including plugin junctions), `dsh-runtimes`, `skills` junctions, `sessions`, `.credentials.yaml`, auth state |
| **Claude Code**<br>`claude-code` | **None (Strict Read-Only Zone, 0 deletable candidates)** | `%USERPROFILE%\.claude` & `.claude.json` (`file-history`, `shell-snapshots`, `projects`, `settings.json`, credentials) |
| **AI Toolchains & Model Radar**<br>`ai-toolchains` | Expired `uv` / `pip` / `npm` download caches only (auto-skips NTFS junctions) | **Read-Only Radar**: `pnpm\store`, `ms-playwright`, `puppeteer`, `huggingface`, `modelscope`, `ollama`, `lm-studio` |

### Core Security Guarantees
- **Engine-Authorized Execution Gate**: `SafetyGuard.ExecuteCandidate` independently re-validates `(Zone, ActionType, Path)` against compiled targets and validated custom roots before any mutation; OS roots, `.ssh`, `.gnupg`, `.aws`, `.kube`, `Documents`, and `Desktop` are permanently hard-protected.
- **Full Ancestor-Chain ReparsePoint Guard & Fail-Closed**: Verifies the full ancestor chain up to drive root before sweeping or recycling, skipping NTFS junctions/symlinks and aborting on unverifiable paths.
- **Zero Process Killing & Live Mid-Sweep Abort**: Running AI apps lock Blue offline caches and auto-unlock upon exit; offline sweeps also re-check process state periodically and abort immediately if an app restarts mid-clean.
- **62 Built-In Regression Self-Tests + JSON Audit Logs**: Verified in isolated temporary fixtures with real NTFS junctions, forged unauthorized execution targets, and CLI mixed-status contracts.

---

## Quick Start

### Option 1: Download Prebuilt Binary (Recommended)
Download `AICleaner-win-x64.zip` and `SHA256SUMS.txt` from [GitHub Releases](https://github.com/wwd177/ai-cleaner/releases/latest), verify the checksum, unzip, and launch `AICleaner.exe`.
> Requires Windows 10 / 11 x64 and [.NET 10 Windows Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

### Option 2: Build from Source
```powershell
dotnet publish .\src\AICleaner.csproj -c Release -r win-x64 --no-self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
```

---

## CLI JSON & Self-Test Usage

```powershell
# List all 5 zones and installation detection state
.\publish\AICleaner.exe --list-zones --json

# Read-only audit of all zones (UTF-8 JSON stdout)
.\publish\AICleaner.exe --dry-run --json

# Audit a single zone by slug
.\publish\AICleaner.exe --dry-run --json --zone claude-code

# Write JSON directly to disk
.\publish\AICleaner.exe --dry-run --json --output .\audit.json

# Run all 62 isolated security & regression self-tests
.\publish\AICleaner.exe --self-test .\selftest.txt
```

**Exit Codes & Precedence**: `0` = `ok` · `1` = runtime error · `2` = invalid arguments / unknown zone · `3` = degraded/aborted (`error > cancelled > partial > ok`).

---

## Agent Skill Integration

See [skills/ai-cleaner/SKILL.md](./skills/ai-cleaner/SKILL.md) to connect AI Cleaner's read-only JSON diagnostics to **Antigravity**, **Claude Code**, or **Codex**. The skill strictly forbids autonomous deletion and directs users to review & confirm inside the desktop GUI.

---

## Contributing & License

Interested in adding support for another AI coding tool (starting in read-only diagnostic mode)? Read [CONTRIBUTING.md](./CONTRIBUTING.md) first.

Licensed under the [MIT License](./LICENSE).
