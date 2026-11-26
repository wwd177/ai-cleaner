# Contributing to AI Cleaner

Thank you for helping make AI Cleaner safer and more useful across the Windows AI developer ecosystem!

Because this tool handles developer workspaces, AI conversation histories, and local toolchains, **safety always comes before reclamation size**. Please review our engineering principles before opening a Pull Request.

---

## 1. Engine-Authorized Rules Policy

AI Cleaner intentionally does **not** support user-supplied or third-party external JSON deletion rules. Every cleanable target must be:

1. **Compiled and code-reviewed** inside `src/ZoneCatalog.cs`, `src/ZoneScanner.cs`, and `src/SafetyGuard.cs`.
2. **Paired with explicit redlines** in `SafetyGuard` protecting credentials, conversation databases, edit rollback history, runtime binaries, and NTFS junctions.
3. **Defaulting to Read-Only Diagnostic first**: When adding a new AI desktop client or CLI tool (e.g. Cursor, Windsurf, Cline, Roo Code, Zed), start with `IsReadOnlyZone = true` (0 deletable candidates) until its storage layout, lock behavior, and rollback files are thoroughly verified.

## 2. Hard Safety Requirements for Any New Zone

Before proposing any cleanable (Green / Yellow / Blue) target, verify all of the following:
- **Zero process killing**: Running processes must be detected via `ZoneDefinition.ProcessNames` and offline caches must auto-lock while running.
- **No NTFS junction / symlink traversal**: All recursive sweeps and directory deletions must respect `SafetyGuard.IsReparsePoint` and `HasReparsePointInChain`.
- **No hardlink / pinned-runtime / resumable-model deletion**: Caches such as `pnpm/store`, Playwright browsers, or Hugging Face / Ollama weights must remain `IsReadOnlyDiagnostic = true` and **never** contribute to reclaimable byte totals.
- **Reversible by default**: Large directories, installer leftovers, and user-facing artifacts should move to the Windows Recycle Bin rather than permanent deletion.
- **Cancellation & telemetry**: Respect `CancellationToken` during recursive walks and surface `AccessDeniedCount` / `ScanStatus.Partial` rather than silently swallowing errors.

## 3. Local Verification Checklist

Every PR must pass all checks locally (and in GitHub Actions CI):

```powershell
# 1. Build single-file release binary
dotnet publish .\src\AICleaner.csproj -c Release -r win-x64 --no-self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish

# 2. Run all built-in security & regression self-tests (must be 0 FAIL)
.\publish\AICleaner.exe --self-test .\selftest.txt

# 3. Verify CLI JSON schema contract
.\publish\AICleaner.exe --list-zones --json
.\publish\AICleaner.exe --dry-run --json --zone claude-code
```

If you add a new zone or redline rule, add corresponding isolated fixture assertions in `SafetyGuard.RunSelfTests()`.

## 4. Privacy Hygiene

Never commit local user paths (e.g. `C:\Users\<your-name>\...`), personal drive paths, machine names, API tokens, or private repository references. CI enforces automated checks against hardcoded user profile paths.
