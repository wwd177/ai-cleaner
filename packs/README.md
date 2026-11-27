# Controlled Read-Only Community Diagnostic Packs (`packs/`)

Starting in **v3.2.0**, AI Cleaner supports drop-in **strictly read-only** community diagnostic packs to surface disk usage for additional AI tools inside the **Local AI & VHDX Radar (`local-ai-ecosystem`)** zone.

## Hard Security Guarantees

1. **Read-Only Only**: Every pack MUST set `"mode": "read-only"` and `"schemaVersion": "1.0"`. Packs claiming any other mode are rejected at load time, and loaded entries are hardcoded to `IsReadOnlyDiagnostic = true`, `CanSelect = false`, `ActionType = None` (`0` deletions possible).
2. **Allowlisted Path Tokens**: `pathTemplate` must begin with one of 4 allowed tokens (`%USERPROFILE%`, `%LOCALAPPDATA%`, `%APPDATA%`, `%PROGRAMDATA%`) followed by at least one concrete subdirectory.
3. **Strict Path Filtering**: Rejects `..` traversal, wildcards, UNC/device paths, OS directories, personal folders (`Documents`, `Desktop`), credential folders (`.ssh`, `.gnupg`, `.aws`, `.kube`, `.azure`, `.gcloud`), and NTFS junction/symlink chains.
4. **Resource Limits**: Max `256 KB` per JSON file, max `25` entries per pack, max `60` entries total.

## Installation

Copy any `.json` diagnostic pack (for example `community-ai-radar.example.json`) into:

```text
%LOCALAPPDATA%\AICleaner\packs\
```

Click **Overview -> Open Packs Folder** inside AI Cleaner to open this directory directly, then click **Scan All Zones**.
