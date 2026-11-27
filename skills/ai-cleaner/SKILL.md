---
name: ai-cleaner
description: Read-only Windows AI workspace, IDE, toolchain & WSL2/VHDX disk diagnostic skill powered by AI Cleaner (AICleaner.exe v3.2+). Use when the user asks to inspect disk usage across Antigravity, GPT/Codex, DeepSeek Harness, Claude Code, AI Toolchains, AI IDEs (Cursor/Windsurf/Trae), or Local AI / WSL2 VHDX and produce an actionable cleanup summary without modifying any files.
---

# AI Cleaner — Read-Only Disk Diagnostic Skill

This skill integrates **AI Cleaner (`AICleaner.exe` v3.0+)** with AI coding agents (**Antigravity**, **Claude Code**, **OpenAI Codex**, and compatible skill runners) to run fast, deterministic, **strictly read-only** disk audits on Windows.

## Hard Safety Boundary

- **Read-only only**: Never delete, purge, recycle, or modify user files or AI data directories autonomously from this skill.
- **No process killing**: Never terminate `Antigravity`, `ChatGPT`, `codex`, `dsh`, `claude`, or local model servers (`ollama`, `LM Studio`).
- **Human-in-the-loop cleanup**: Present a concise audit report grouped by zone and safety tier, then instruct the user to open `AICleaner.exe` (GUI) to review and confirm any cleanup interactively.

## Locating `AICleaner.exe`

Locate the binary in order (stop at the first hit):
1. Environment variable `$env:AI_CLEANER_EXE` (if set and exists).
2. `AICleaner.exe` on `PATH` (`Get-Command AICleaner.exe -ErrorAction SilentlyContinue`).
3. A local release/publish folder specified by the user.

If `AICleaner.exe` is not installed yet, direct the user to download `AICleaner-win-x64.zip` from the latest GitHub Release or build it with:
```powershell
dotnet publish .\src\AICleaner.csproj -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
```

## Read-Only CLI Commands (Schema `1.0`)

Always pass `--json` (UTF-8 output) and inspect the process exit code:

```powershell
# 1. Discover supported zones and installation status
& $exe --list-zones --json

# 2. Run a full read-only audit across all 7 zones
& $exe --dry-run --json

# 3. Audit a single zone by slug:
#    antigravity | gpt-codex | deepseek-harness | claude-code | ai-toolchains | ai-ides | local-ai-ecosystem
& $exe --dry-run --json --zone ai-ides

# 4. Optional: write JSON directly to a file (useful when stdout is captured elsewhere)
& $exe --dry-run --json --output "$env:TEMP\ai-cleaner-audit.json"
```

### Exit Code & Read-Only Semantics
| Exit Code | Meaning | How the Agent Should Respond |
| :---: | :--- | :--- |
| `0` | Success (`status: "ok"`) | Parse JSON and summarize findings cleanly. |
| `1` | Unexpected runtime error (e.g. unwritable `--output` path) | Report stderr/error summary; do not guess disk numbers. |
| `2` | Invalid arguments or unknown `--zone` slug | Run `--list-zones --json` first to verify valid slugs. |
| `3` | Degraded scan (`error` > `cancelled` > `partial` precedence) | Still parse available JSON fields, and explicitly note which zones failed, were cancelled, or had `accessDeniedCount > 0`. |

Distinguish the three read-only flags in the JSON payload:
- Top-level `readOnly: true` describes the `--dry-run` CLI invocation itself (no files modified).
- Zone-level `isReadOnlyZone: true` (e.g. `claude-code`) means the entire zone is read-only even inside the GUI (`0` deletable candidates).
- Candidate-level `isReadOnlyDiagnostic: true` (e.g. `pnpm/store`, `Playwright`, `Hugging Face`, `Ollama` inside `ai-toolchains`) marks diagnostic radar entries inside a mixed zone that also reports cleanable Green caches (`uv`/`pip`/`npm`); **never add `diagnosticBytes` into reclaimable totals**.

## Reporting Format

When summarizing the `--dry-run --json` (`schemaVersion: "1.0"`) output for the user:
1. Inspect top-level `status` (`"ok"` / `"partial"` / `"cancelled"` / `"error"`), `recycleBin`, and `summary`.
2. Show a compact table per item in `zones[]`:
   - **Zone** (`displayName` / `slug`) & **Running State** (`running`, `runningProcessCount`)
   - **Green (Safe Online)** (`safeOnlineBytes`) — safe to clean while apps are running
   - **Yellow (Recycle Candidate)** (`recycleCandidateBytes`) — reviewable items moved to Recycle Bin
   - **Blue (Offline Cache)** (`offlineCacheBytes`) — locked while app is running; cleanable after exit
   - **Read-Only Radar** (`diagnosticBytes`) — informational only (`Claude Code`, `pnpm/store`, `Playwright`, `Hugging Face`, `Ollama`, `LM Studio`); **never add `diagnosticBytes` into reclaimable totals**
3. Highlight the top 3–5 largest entries in `candidates[]` by `sizeBytes` (citing `title`, `tier`, `canSelect`, `isReadOnlyDiagnostic`, and `reason`).
4. If `status == "partial"` or `accessDeniedCount > 0`, clearly mention the access-denied count.
5. Close by inviting the user to launch `AICleaner.exe` to visually inspect via **Space Radar** or selectively clean approved Green/Yellow/Blue items.
