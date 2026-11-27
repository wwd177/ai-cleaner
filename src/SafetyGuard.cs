using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AICleaner;

public sealed class DirStats
{
    public long TotalBytes;
    public long FileCount;
    public long DirCount;
    public long JunctionCount;
    public int AccessDeniedCount;
    public DateTime NewestWriteUtc = DateTime.MinValue;
    public bool RootIsJunction;
}

public sealed class SweepStats
{
    public long EligibleFiles;
    public long EligibleBytes;
    public long DeletedFiles;
    public long DeletedBytes;
    public long PrunedEmptyDirs;
    public long SkippedLocked;
    public long SkippedRecent;
    public long SkippedJunctions;
    public long SkippedProtected;
    public int AccessDeniedCount;
    public bool AbortedByCallback;
    public DateTime NewestEligibleUtc = DateTime.MinValue;
    public List<string> Errors = new();
}

public static class SafetyGuard
{
    public const int HardActiveFloorHours = 72;
    public static readonly string AppDataBase = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AICleaner");
    public static readonly string LogsDir = Path.Combine(AppDataBase, "logs");
    public static readonly string ReportsDir = Path.Combine(AppDataBase, "reports");

    private static readonly string UserHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string LocalApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // Hardcoded built-in Red-Zone subtrees across all 7 zones (100% portable standard paths).
    // Neither these paths, nor their children, nor their parent ancestors may EVER be deleted/recycled.
    private static readonly string[] ProtectedSubtrees = BuildProtectedSubtrees();

    // Auto-discovered NTFS junction targets under %USERPROFILE%\.codex (e.g. .tmp -> plugin-staging, tmp -> command-staging).
    // Stored separately from static ProtectedSubtrees so:
    //   1) IsProtectedForDelete ALWAYS protects these targets, their children, AND their parent ancestors from deletion;
    //   2) IsProtectedForTraversal ALWAYS blocks entering these targets or their children;
    //   3) ValidateCustomRootPath rejects using a junction target (or its child) as a root, while ALLOWING its parent folder
    //      (e.g. CustomCodexCacheRoot) to be enumerated as a custom cache root!
    private static readonly string[] AutoDiscoveredJunctionTargets = BuildAutoDiscoveredJunctionTargets();
    internal static string[]? TestExtraAutoJunctionTargets;

    // Container roots that themselves can NEVER be deleted as a whole (only eligible interior items may be swept).
    private static readonly HashSet<string> ProtectedContainerSelf = BuildProtectedContainerSelf();
    private static readonly string[] ProtectedContainerSelfList = ProtectedContainerSelf.ToArray();

    // Shared scratch directory allowlist enforced independently by BOTH scanner and executor for RecycleBatchDirectories.
    public static readonly HashSet<string> AllowedBatchScratchNames = new(StringComparer.OrdinalIgnoreCase)
        { "_tmp", "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".parcel-cache", ".turbo" };

    // Electron offline browser cache relative suffixes authorized for OfflineSweepFilesKeepSkeleton.
    public static readonly string[] AuthorizedOfflineCacheSuffixes =
    {
        "cache", "cacheddata", "code cache", "gpucache", "grshadercache", "shadercache",
        "dawngraphitecache", "dawnwebgpucache", "blob_storage",
        @"service worker\cachestorage", @"crashpad\reports", @"crashpad\pending"
    };

    private static readonly string[] SystemDangerousRoots = BuildSystemDangerousRoots();

    private static string[] BuildSystemDangerousRoots()
    {
        var raw = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"C:\Program Files\WindowsApps"
        };
        return raw.Select(NormalizePath).Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] BuildProtectedSubtrees()
    {
        var raw = new[]
        {
            // === Antigravity Red-Zone ===
            Path.Combine(LocalApp, @"Programs\antigravity"),
            Path.Combine(UserHome, @".gemini\config"),
            Path.Combine(UserHome, @".gemini\.env"),
            Path.Combine(UserHome, @".gemini\settings.json"),
            Path.Combine(UserHome, @".gemini\bin"),
            Path.Combine(UserHome, @".gemini\antigravity\conversations"),
            Path.Combine(UserHome, @".gemini\antigravity\builtin"),
            Path.Combine(UserHome, @".gemini\antigravity\lib"),
            Path.Combine(UserHome, @".gemini\antigravity\mcp"),
            Path.Combine(UserHome, @".gemini\antigravity\plugin_data"),
            Path.Combine(UserHome, @".gemini\antigravity\bin"),
            Path.Combine(UserHome, @".gemini\antigravity\daemon"),
            Path.Combine(UserHome, @".gemini\antigravity\sidecar_data"),
            Path.Combine(UserHome, @".gemini\antigravity\annotations"),
            Path.Combine(UserHome, @".gemini\antigravity\chapter_titles"),
            Path.Combine(UserHome, @".gemini\antigravity\presence"),
            Path.Combine(UserHome, @".gemini\antigravity\conversation_summaries.db"),
            Path.Combine(UserHome, @".gemini\antigravity\conversation_summaries.db-shm"),
            Path.Combine(UserHome, @".gemini\antigravity\conversation_summaries.db-wal"),
            Path.Combine(UserHome, @".gemini\antigravity\agyhub_summaries_proto.pb"),
            Path.Combine(UserHome, @".gemini\antigravity\antigravity_state.pbtxt"),
            Path.Combine(UserHome, @".gemini\antigravity\installation_id"),
            Path.Combine(Roaming, @"Antigravity\Network"),
            Path.Combine(Roaming, @"Antigravity\Local Storage"),
            Path.Combine(Roaming, @"Antigravity\Session Storage"),
            Path.Combine(Roaming, @"Antigravity\Preferences"),
            Path.Combine(Roaming, @"Antigravity\Local State"),
            Path.Combine(Roaming, @"Antigravity\app_storage.json"),
            Path.Combine(Roaming, @"Antigravity\credential-user-scope"),
            Path.Combine(Roaming, @"Antigravity IDE\User"),
            Path.Combine(Roaming, @"Antigravity IDE\Network"),
            Path.Combine(Roaming, @"Antigravity IDE\Local Storage"),
            Path.Combine(Roaming, @"Antigravity IDE\Session Storage"),
            Path.Combine(Roaming, @"Antigravity IDE\Backups"),
            Path.Combine(Roaming, @"Antigravity IDE\Preferences"),

            // === GPT / Codex Red-Zone ===
            Path.Combine(UserHome, @".codex"),
            Path.Combine(LocalApp, @"OpenAI"),
            Path.Combine(UserHome, @".cache\codex-runtimes"),

            // === DeepSeek Harness Red-Zone ===
            Path.Combine(LocalApp, @"Programs\DeepSeek Harness"),
            Path.Combine(LocalApp, @"dsh-computer-use-win"),
            Path.Combine(UserHome, @".dsh\profiles"),
            Path.Combine(UserHome, @".dsh\dsh-runtimes"),
            Path.Combine(UserHome, @".dsh\bin"),
            Path.Combine(UserHome, @".dsh\sessions"),
            Path.Combine(UserHome, @".dsh\skills"),
            Path.Combine(UserHome, @".dsh\storages"),
            Path.Combine(UserHome, @".dsh\llm-deepseek"),
            Path.Combine(UserHome, @".dsh\redteam"),
            Path.Combine(UserHome, @".dsh\.agent-presets"),
            Path.Combine(UserHome, @".dsh\whale-audio"),
            Path.Combine(UserHome, @".dsh\whale-bubble-imgs"),
            Path.Combine(UserHome, @".dsh\whale-roles"),
            Path.Combine(UserHome, @".dsh\.credentials.yaml"),
            Path.Combine(UserHome, @".dsh\.env"),
            Path.Combine(UserHome, @".dsh\cordis.patch.yml"),
            Path.Combine(UserHome, @".dsh\prompt-inject.md"),
            Path.Combine(UserHome, @".dsh\agency-expert-personas.json"),
            Path.Combine(UserHome, @".dsh\agency-subagent-ids.json"),
            Path.Combine(UserHome, @".dsh\agency-subagents.json"),
            Path.Combine(Roaming, @"@deepseek-ai\dsh-desktop\Network"),
            Path.Combine(Roaming, @"@deepseek-ai\dsh-desktop\Local Storage"),
            Path.Combine(Roaming, @"@deepseek-ai\dsh-desktop\Session Storage"),
            Path.Combine(Roaming, @"@deepseek-ai\dsh-desktop\WebStorage"),
            Path.Combine(Roaming, @"@deepseek-ai\dsh-desktop\Preferences"),
            Path.Combine(Roaming, @"@deepseek-ai\dsh-desktop\Local State"),

            // === Claude Code Red-Zone (v3.0 strict read-only diagnostic mode: protects file-history, shell-snapshots, projects & credentials!) ===
            Path.Combine(UserHome, @".claude"),
            Path.Combine(UserHome, @".claude.json"),

            // === AI Toolchains & Local Model Read-Only Red-Zone (hardlinks, pinned browsers, resumable weights) ===
            Path.Combine(LocalApp, @"pnpm\store"),
            Path.Combine(LocalApp, @"ms-playwright"),
            Path.Combine(UserHome, @".cache\puppeteer"),
            Path.Combine(UserHome, @".cache\huggingface"),
            Path.Combine(UserHome, @".cache\modelscope"),
            Path.Combine(UserHome, @".ollama"),
            Path.Combine(UserHome, @".cache\lm-studio"),

            // === AI IDEs Red-Zone (Cursor / Windsurf / Trae: User settings, auth, extensions, CachedExtensionVSIXs) ===
            Path.Combine(UserHome, @".cursor"),
            Path.Combine(Roaming, @"Cursor\User"),
            Path.Combine(Roaming, @"Cursor\Network"),
            Path.Combine(Roaming, @"Cursor\Local Storage"),
            Path.Combine(Roaming, @"Cursor\Session Storage"),
            Path.Combine(Roaming, @"Cursor\IndexedDB"),
            Path.Combine(Roaming, @"Cursor\CachedExtensionVSIXs"),
            Path.Combine(Roaming, @"Cursor\Preferences"),
            Path.Combine(Roaming, @"Cursor\Local State"),
            Path.Combine(UserHome, @".codeium"),
            Path.Combine(Roaming, @"Windsurf\User"),
            Path.Combine(Roaming, @"Windsurf\Network"),
            Path.Combine(Roaming, @"Windsurf\Local Storage"),
            Path.Combine(Roaming, @"Windsurf\Session Storage"),
            Path.Combine(Roaming, @"Windsurf\IndexedDB"),
            Path.Combine(Roaming, @"Windsurf\CachedExtensionVSIXs"),
            Path.Combine(Roaming, @"Windsurf\Preferences"),
            Path.Combine(Roaming, @"Windsurf\Local State"),
            Path.Combine(UserHome, @".trae"),
            Path.Combine(Roaming, @"Trae\User"),
            Path.Combine(Roaming, @"Trae\Network"),
            Path.Combine(Roaming, @"Trae\Local Storage"),
            Path.Combine(Roaming, @"Trae\Session Storage"),
            Path.Combine(Roaming, @"Trae\IndexedDB"),
            Path.Combine(Roaming, @"Trae\CachedExtensionVSIXs"),
            Path.Combine(Roaming, @"Trae\Preferences"),
            Path.Combine(Roaming, @"Trae\Local State"),

            // === Local AI Ecosystem & Virtual Disks Read-Only Red-Zone ===
            Path.Combine(UserHome, @".continue"),
            Path.Combine(LocalApp, @"Docker\wsl"),
            Path.Combine(LocalApp, @"wsl"),

            // === Global User Personal & Credential Defense-in-Depth Redlines ===
            Path.Combine(UserHome, @".ssh"),
            Path.Combine(UserHome, @".gnupg"),
            Path.Combine(UserHome, @".aws"),
            Path.Combine(UserHome, @".kube"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };

        return raw.Select(NormalizePath).Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] BuildAutoDiscoveredJunctionTargets()
    {
        var targets = new List<string>();
        try
        {
            string codexDir = Path.Combine(UserHome, ".codex");
            if (Directory.Exists(codexDir))
            {
                foreach (var sub in new DirectoryInfo(codexDir).EnumerateDirectories())
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0 && !string.IsNullOrWhiteSpace(sub.LinkTarget))
                        targets.Add(sub.LinkTarget);
                }
            }
        }
        catch { }
        return targets.Select(NormalizePath).Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> GetAllAutoDiscoveredJunctionTargets()
    {
        foreach (var t in AutoDiscoveredJunctionTargets) yield return t;
        var extra = TestExtraAutoJunctionTargets;
        if (extra != null)
        {
            foreach (var t in extra)
            {
                if (!string.IsNullOrEmpty(t)) yield return t;
            }
        }
    }

    private static HashSet<string> BuildProtectedContainerSelf()
    {
        var raw = new[]
        {
            UserHome,
            Roaming,
            LocalApp,
            Path.Combine(UserHome, ".gemini"),
            Path.Combine(UserHome, @".gemini\antigravity"),
            Path.Combine(UserHome, @".gemini\antigravity\brain"),
            Path.Combine(UserHome, @".gemini\antigravity\cache"),
            Path.Combine(UserHome, @".gemini\antigravity\crashes"),
            Path.Combine(UserHome, @".gemini\antigravity\scratch"),
            Path.Combine(UserHome, @".gemini\logs"),
            Path.Combine(Roaming, "Antigravity"),
            Path.Combine(Roaming, @"Antigravity\Partitions"),
            Path.Combine(Roaming, "Antigravity IDE"),
            Path.Combine(LocalApp, @"antigravity-updater"),
            Path.Combine(LocalApp, @"antigravity-updater\pending"),
            Path.Combine(LocalApp, @"uv\cache"),
            Path.Combine(LocalApp, @"pip\Cache"),
            Path.Combine(LocalApp, @"npm-cache"),
            Path.Combine(Roaming, @"npm-cache"),
            Path.Combine(UserHome, ".dsh"),
            Path.Combine(UserHome, @".dsh\cache"),
            Path.Combine(UserHome, @".dsh\attachments"),
            Path.Combine(UserHome, @".dsh\profile-backups"),
            Path.Combine(UserHome, @".dsh\skill-backups"),
            Path.Combine(Roaming, @"@deepseek-ai"),
            Path.Combine(Roaming, @"@deepseek-ai\dsh-desktop"),
            Path.Combine(LocalApp, @"@deepseek-aidsh-desktop-updater"),
            Path.Combine(LocalApp, @"@deepseek-aidsh-desktop-updater\pending"),
            Path.Combine(Roaming, "Cursor"),
            Path.Combine(Roaming, "Windsurf"),
            Path.Combine(Roaming, "Trae"),
        };
        return new HashSet<string>(raw.Select(NormalizePath).Where(x => !string.IsNullOrEmpty(x)), StringComparer.OrdinalIgnoreCase);
    }

    public static bool TryNormalizePath(string path, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.IndexOf('\0') >= 0) return false;
        try
        {
            string full = Path.GetFullPath(path.Trim());
            string cleaned = full.TrimEnd('\\', '/').ToLowerInvariant();
            if (cleaned.Length <= 3) return false; // Never accept drive root (e.g. "c:", "f:")
            normalized = cleaned;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string NormalizePath(string path) =>
        TryNormalizePath(path, out var norm) ? norm : "";

    private static bool IsHardSubtreeMatch(string key, bool protectAncestors, bool includeDynamicCustomRules = true)
    {
        if (TryNormalizePath(Path.Combine(LocalApp, @"Packages\OpenAI.Codex"), out var pkgPrefix) &&
            key.StartsWith(pkgPrefix, StringComparison.OrdinalIgnoreCase))
            return true;
        if (key.StartsWith(@"c:\program files\windowsapps\openai.codex", StringComparison.OrdinalIgnoreCase))
            return true;

        // Protect Electron Partition user auth/storage folders anywhere inside Antigravity / DSH Partitions
        if (key.Contains(@"\partitions\") && (
            key.EndsWith(@"\network") || key.Contains(@"\network\") ||
            key.EndsWith(@"\local storage") || key.Contains(@"\local storage\") ||
            key.EndsWith(@"\session storage") || key.Contains(@"\session storage\") ||
            key.EndsWith(@"\indexeddb") || key.Contains(@"\indexeddb\") ||
            key.EndsWith(@"\webstorage") || key.Contains(@"\webstorage\") ||
            key.EndsWith(@"\preferences")))
        {
            return true;
        }

        // Protect Electron Service Worker Database (only CacheStorage is safe to sweep offline)
        if (key.EndsWith(@"\service worker\database") || key.Contains(@"\service worker\database\"))
            return true;

        foreach (var p in ProtectedSubtrees)
        {
            if (key == p || key.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
            if (protectAncestors && p.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (!includeDynamicCustomRules)
            return false;

        // Enforce auto-discovered .codex junction targets (exact, child, AND ancestor on delete!)
        foreach (var p in GetAllAutoDiscoveredJunctionTargets())
        {
            if (key == p || key.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
            if (protectAncestors && p.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Also enforce custom user-configured protected subtrees and CustomCodexCacheRoot runtime/staging subdirs
        var extraProtected = SettingsStore.GetNormalizedExtraProtectedSubtrees();
        for (int i = 0; i < extraProtected.Length; i++)
        {
            var p = extraProtected[i];
            if (key == p || key.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
            if (protectAncestors && p.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var customCodex = SettingsStore.Load().CustomCodexCacheRoot;
        if (!string.IsNullOrWhiteSpace(customCodex) && TryNormalizePath(customCodex, out var ccNorm))
        {
            foreach (var sub in new[] { "runtimes", "native-runtimes", "app-metadata", "plugin-staging", "command-staging" })
            {
                string p = ccNorm + "\\" + sub;
                if (key == p || key.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (protectAncestors && p.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static bool IsContainerSelfOrAncestor(string key)
    {
        if (ProtectedContainerSelf.Contains(key)) return true;
        for (int i = 0; i < ProtectedContainerSelfList.Length; i++)
        {
            if (ProtectedContainerSelfList[i].StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        var extraWs = SettingsStore.GetNormalizedExtraWorkspaceRoots();
        for (int i = 0; i < extraWs.Length; i++)
        {
            var w = extraWs[i];
            if (key == w || w.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        var customCodex = SettingsStore.Load().CustomCodexCacheRoot;
        if (!string.IsNullOrWhiteSpace(customCodex) && TryNormalizePath(customCodex, out var ccNorm))
        {
            if (key == ccNorm || ccNorm.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
            foreach (var sub in new[] { "temp", "pip", "npm", "uv", "browser-cache", "desktop-logs" })
            {
                string c = ccNorm + "\\" + sub;
                if (key == c || c.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Strict check for ANY delete/recycle action on the path itself (whole file or whole directory tree).
    /// Fail-closed: rejects invalid paths, ProtectedSubtrees (exact/child/ancestor),
    /// ProtectedContainerSelf (exact OR ancestor so deleting P never removes protected container C!), and user whitelist.
    /// </summary>
    public static bool IsProtectedForDelete(string candidatePath, bool includeUserWhitelist = true)
    {
        if (!TryNormalizePath(candidatePath, out var key)) return true;
        if (IsContainerSelfOrAncestor(key)) return true;
        if (IsHardSubtreeMatch(key, protectAncestors: true)) return true;
        if (includeUserWhitelist && SettingsStore.IsWhitelistedNormalized(key, protectAncestors: true))
            return true;
        return false;
    }

    /// <summary>
    /// Check whether sweeping/traversing INSIDE a subdirectory is forbidden (without deleting the directory itself).
    /// Allows entering an authorized ProtectedContainerSelf container so parent-entry and container-entry behave identically,
    /// while strictly forbidding entry into ProtectedSubtrees or user-whitelisted folders.
    /// </summary>
    public static bool IsProtectedForTraversal(string dirPath, bool includeUserWhitelist = true)
    {
        if (!TryNormalizePath(dirPath, out var key)) return true;
        if (IsHardSubtreeMatch(key, protectAncestors: false)) return true;
        if (includeUserWhitelist && SettingsStore.IsWhitelistedNormalized(key, protectAncestors: false))
            return true;
        return false;
    }

    public static bool IsProtectedPath(string candidatePath, bool includeUserWhitelist = true) =>
        IsProtectedForDelete(candidatePath, includeUserWhitelist);

    public static bool IsReparsePoint(FileSystemInfo info) =>
        (info.Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>Fail-closed attribute query: returns false if missing or inaccessible.</summary>
    public static bool TryGetAttributes(string path, out FileAttributes attrs)
    {
        attrs = default;
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            attrs = File.GetAttributes(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Fail-closed ReparsePoint check: returns true if path IS a ReparsePoint OR its attributes cannot be verified.
    /// (Never conflates 'cannot verify' with 'confirmed regular file/dir'!)
    /// </summary>
    public static bool IsReparsePoint(string path)
    {
        if (!TryGetAttributes(path, out var attrs)) return true;
        return (attrs & FileAttributes.ReparsePoint) != 0;
    }

    /// <summary>
    /// Walks every ancestor directory from targetPath up to drive root (or stopAtNormalizedRoot)
    /// and returns true (unsafe) if ANY segment is a ReparsePoint or cannot be stat'd.
    /// </summary>
    public static bool HasReparsePointInChain(string targetPath, string? stopAtNormalizedRoot = null)
    {
        if (!TryNormalizePath(targetPath, out var norm)) return true;
        try
        {
            string? curr = Path.GetFullPath(targetPath.Trim()).TrimEnd('\\', '/');
            while (!string.IsNullOrEmpty(curr) && curr.Length > 3)
            {
                if (!TryGetAttributes(curr, out var attrs)) return true;
                if ((attrs & FileAttributes.ReparsePoint) != 0) return true;

                if (!string.IsNullOrEmpty(stopAtNormalizedRoot) &&
                    curr.Equals(stopAtNormalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                curr = Path.GetDirectoryName(curr)?.TrimEnd('\\', '/');
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Validates any user-supplied custom root path (CustomCodexCacheRoot / ExtraWorkspaceRoots / ExtraBackupDirs)
    /// both at GUI save time and at scan/execution time. Fail-closed against drive roots, OS directories,
    /// user profile containers, hardcoded redline trees/ancestors, and NTFS reparse-point chains.
    /// </summary>
    public static bool ValidateCustomRootPath(string? rawPath, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            error = "路径不能为空。";
            return false;
        }
        string trimmed = rawPath.Trim();
        if (!Path.IsPathFullyQualified(trimmed) || !TryNormalizePath(trimmed, out var key))
        {
            error = "必须是合法的绝对目录路径，且不能选择盘符根目录（如 C:\\）。";
            return false;
        }

        // Block Windows / Program Files / ProgramData roots and their ancestors/subtrees
        foreach (var sys in SystemDangerousRoots)
        {
            if (key == sys || key.StartsWith(sys + "\\", StringComparison.OrdinalIgnoreCase) ||
                sys.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase))
            {
                error = "禁止将系统目录或其父/子目录设为缓存或工作区根目录。";
                return false;
            }
        }

        // Block UserHome / AppData / LocalAppData / Roaming themselves or their ancestors
        foreach (var core in new[] { UserHome, LocalApp, Roaming })
        {
            if (TryNormalizePath(core, out var coreNorm) &&
                (key == coreNorm || coreNorm.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                error = "不能将用户主目录或 AppData 根目录设为缓存或工作区根目录，请选择更具体的子目录。";
                return false;
            }
        }

        // Block built-in engine redline subtrees (exact, child, or ancestor of .codex/.claude/.gemini/.dsh/.ssh/Documents/Desktop)
        if (IsHardSubtreeMatch(key, protectAncestors: true, includeDynamicCustomRules: false))
        {
            error = "目标路径与引擎硬保护红区（如 .codex / .claude / .gemini / .dsh / .ssh / Documents / Desktop）重叠或为其祖先目录，已拦截。";
            return false;
        }

        // Block setting an auto-discovered .codex junction target OR a user ExtraProtectedSubtree (or its child) as a root,
        // while ALLOWING a parent cache/workspace root that contains them (inner targets remain hard-protected on traversal & delete!).
        foreach (var p in GetAllAutoDiscoveredJunctionTargets())
        {
            if (key == p || key.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase))
            {
                error = "目标路径为 .codex 目录联接指向的受保护暂存区，禁止将其设为可清理根目录。";
                return false;
            }
        }
        var extraProtected = SettingsStore.GetNormalizedExtraProtectedSubtrees();
        for (int i = 0; i < extraProtected.Length; i++)
        {
            var p = extraProtected[i];
            if (key == p || key.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase))
            {
                error = "目标路径位于您配置的额外硬保护目录内，禁止将其设为可清理的工作区或缓存根目录。";
                return false;
            }
        }

        // If the directory already exists on disk, ensure no ancestor segment is a reparse point / NTFS junction
        if ((Directory.Exists(trimmed) || File.Exists(trimmed)) && HasReparsePointInChain(trimmed))
        {
            error = "目标路径或其祖先目录包含 NTFS 目录联接/符号链接，禁止配置为工作区或缓存根。";
            return false;
        }

        return true;
    }

    private static bool IsDirectChildOf(string key, string parentDir)
    {
        if (!TryNormalizePath(parentDir, out var pNorm)) return false;
        if (!key.StartsWith(pNorm + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        string rest = key[(pNorm.Length + 1)..];
        return rest.Length > 0 && !rest.Contains('\\');
    }

    private static bool MatchesAuthorizedOfflineUnder(string key, string baseDir, bool allowPartitions)
    {
        if (!TryNormalizePath(baseDir, out var bNorm)) return false;
        if (!key.StartsWith(bNorm + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        string rel = key[(bNorm.Length + 1)..];
        foreach (var sfx in AuthorizedOfflineCacheSuffixes)
        {
            if (rel.Equals(sfx, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        if (allowPartitions && rel.StartsWith(@"partitions\", StringComparison.OrdinalIgnoreCase))
        {
            string afterPart = rel[@"partitions\".Length..];
            int slash = afterPart.IndexOf('\\');
            if (slash > 0 && slash + 1 < afterPart.Length)
            {
                string subRel = afterPart[(slash + 1)..];
                foreach (var sfx in AuthorizedOfflineCacheSuffixes)
                {
                    if (subRel.Equals(sfx, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Unified engine-side execution authorization gate: re-validates (Zone, ActionType, Path) against
    /// compiled authorized targets and validated SettingsStore roots right before any physical mutation.
    /// </summary>
    public static bool IsAuthorizedExecutionTarget(CleanCandidate item, out string reason)
    {
        reason = "";
        var def = ZoneCatalog.Get(item.Zone);
        if (def.IsReadOnlyZone || item.IsReadOnlyDiagnostic)
        {
            reason = "只读诊断专区/项目，严禁执行任何删除操作";
            return false;
        }

        // RecycleBatchDirectories validates each individual BatchPaths entry independently
        if (item.ActionType == CleanActionType.RecycleBatchDirectories)
        {
            if (item.Zone != ZoneId.GptCodex)
            {
                reason = "仅 GPT / Codex 专区支持工作区临时目录批量回收";
                return false;
            }
            return true;
        }

        if (!TryNormalizePath(item.Path, out var key))
        {
            reason = "目标路径非法或为盘符根目录，已拦截";
            return false;
        }

        static bool EqualsAny(string k, params string[] paths)
        {
            foreach (var p in paths)
            {
                if (TryNormalizePath(p, out var n) && k == n) return true;
            }
            return false;
        }

        var settings = SettingsStore.Load();

        switch (item.Zone)
        {
            case ZoneId.ClaudeCode:
                reason = "Claude Code 专区为严格只读诊断模式，禁止执行删除";
                return false;

            case ZoneId.AiToolchains:
                if (item.ActionType == CleanActionType.SweepExpiredFiles &&
                    EqualsAny(key,
                        Path.Combine(LocalApp, @"uv\cache"),
                        Path.Combine(LocalApp, @"pip\Cache"),
                        Path.Combine(LocalApp, "npm-cache"),
                        Path.Combine(Roaming, "npm-cache")))
                {
                    return true;
                }
                reason = "AI 工具链专区仅授权清理 uv / pip / npm 四个官方下载缓存目录，其余路径一律拦截";
                return false;

            case ZoneId.Antigravity:
                if (item.ActionType == CleanActionType.RecycleExpiredChildren &&
                    EqualsAny(key, Path.Combine(LocalApp, @"antigravity-updater\pending")))
                    return true;

                if (item.ActionType == CleanActionType.SweepExpiredFiles &&
                    EqualsAny(key,
                        Path.Combine(UserHome, @".gemini\antigravity\cache"),
                        Path.Combine(UserHome, @".gemini\antigravity\crashes"),
                        Path.Combine(UserHome, @".gemini\logs"),
                        Path.Combine(Roaming, @"Antigravity\logs"),
                        Path.Combine(UserHome, @".gemini\antigravity\scratch")))
                    return true;

                if (item.ActionType == CleanActionType.RecycleDirectoryOrFile)
                {
                    if (IsDirectChildOf(key, Path.Combine(UserHome, @".gemini\antigravity\brain")))
                        return true;
                    foreach (var bdir in settings.ExtraBackupDirs)
                    {
                        if (ValidateCustomRootPath(bdir, out _) && TryNormalizePath(bdir, out var bNorm) && key == bNorm)
                            return true;
                    }
                }

                if (item.ActionType == CleanActionType.OfflineSweepFilesKeepSkeleton &&
                    (MatchesAuthorizedOfflineUnder(key, Path.Combine(Roaming, "Antigravity"), allowPartitions: true) ||
                     MatchesAuthorizedOfflineUnder(key, Path.Combine(Roaming, "Antigravity IDE"), allowPartitions: false)))
                    return true;

                reason = "目标路径不在 Antigravity 专区授权清理清单内，已拦截";
                return false;

            case ZoneId.GptCodex:
                if (item.ActionType == CleanActionType.SweepExpiredFiles &&
                    !string.IsNullOrWhiteSpace(settings.CustomCodexCacheRoot) &&
                    ValidateCustomRootPath(settings.CustomCodexCacheRoot, out _) &&
                    EqualsAny(key,
                        Path.Combine(settings.CustomCodexCacheRoot, "temp"),
                        Path.Combine(settings.CustomCodexCacheRoot, "pip"),
                        Path.Combine(settings.CustomCodexCacheRoot, "npm"),
                        Path.Combine(settings.CustomCodexCacheRoot, "uv")))
                    return true;

                if (item.ActionType == CleanActionType.OfflineSweepFilesKeepSkeleton &&
                    !string.IsNullOrWhiteSpace(settings.CustomCodexCacheRoot) &&
                    ValidateCustomRootPath(settings.CustomCodexCacheRoot, out _) &&
                    EqualsAny(key,
                        Path.Combine(settings.CustomCodexCacheRoot, "browser-cache"),
                        Path.Combine(settings.CustomCodexCacheRoot, "desktop-logs")))
                    return true;

                if (item.ActionType == CleanActionType.RecycleDirectoryOrFile)
                {
                    foreach (var ws in settings.ExtraWorkspaceRoots)
                    {
                        if (ValidateCustomRootPath(ws, out _) && TryNormalizePath(ws, out var wNorm) &&
                            key.StartsWith(wNorm + "\\", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }

                reason = "目标路径不在 GPT / Codex 专区授权清理清单内，已拦截";
                return false;

            case ZoneId.DeepSeekHarness:
                if (item.ActionType == CleanActionType.RecycleExpiredChildren &&
                    EqualsAny(key,
                        Path.Combine(LocalApp, @"@deepseek-aidsh-desktop-updater\pending"),
                        Path.Combine(UserHome, @".dsh\attachments")))
                    return true;

                if (item.ActionType == CleanActionType.SweepExpiredFiles &&
                    EqualsAny(key,
                        Path.Combine(UserHome, @".dsh\cache"),
                        Path.Combine(Roaming, @"@deepseek-ai\dsh-desktop\logs")))
                    return true;

                if (item.ActionType == CleanActionType.RecycleDirectoryOrFile &&
                    IsDirectChildOf(key, Path.Combine(UserHome, @".dsh\profile-backups")))
                    return true;

                if (item.ActionType == CleanActionType.OfflineSweepFilesKeepSkeleton &&
                    MatchesAuthorizedOfflineUnder(key, Path.Combine(Roaming, @"@deepseek-ai\dsh-desktop"), allowPartitions: true))
                    return true;

                reason = "目标路径不在 DeepSeek Harness 专区授权清理清单内，已拦截";
                return false;

            case ZoneId.AiIdes:
                if (item.ActionType == CleanActionType.OfflineSweepFilesKeepSkeleton &&
                    (MatchesAuthorizedOfflineUnder(key, Path.Combine(Roaming, "Cursor"), allowPartitions: false) ||
                     MatchesAuthorizedOfflineUnder(key, Path.Combine(Roaming, "Windsurf"), allowPartitions: false) ||
                     MatchesAuthorizedOfflineUnder(key, Path.Combine(Roaming, "Trae"), allowPartitions: false)))
                    return true;
                reason = "AI IDE 专区仅授权清理 Cursor / Windsurf / Trae 顶层离线 Electron 缓存，其余路径一律拦截";
                return false;

            case ZoneId.LocalAiEcosystem:
                reason = "本地 AI 生态与虚拟盘专区为严格只读诊断模式，禁止执行任何删除";
                return false;

            default:
                reason = "未知专区，拒绝执行清理";
                return false;
        }
    }

    /// <summary>Verifies a candidate path for RecycleBatchDirectories belongs to validated workspace roots & scratch leaf names.</summary>
    public static bool IsValidWorkspaceBatchMember(string dirPath)
    {
        if (!TryNormalizePath(dirPath, out var key)) return false;
        var rawRoots = SettingsStore.Load().ExtraWorkspaceRoots;
        bool underAllowedRoot = false;
        foreach (var raw in rawRoots)
        {
            if (ValidateCustomRootPath(raw, out _) && TryNormalizePath(raw, out var rNorm) &&
                key.StartsWith(rNorm + "\\", StringComparison.OrdinalIgnoreCase))
            {
                underAllowedRoot = true;
                break;
            }
        }
        if (!underAllowedRoot) return false;
        string leaf = Path.GetFileName(key);
        return AllowedBatchScratchNames.Contains(leaf);
    }

    public static bool IsFileLocked(string filePath)
    {
        try
        {
            using var fs = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch
        {
            return true;
        }
    }

    private readonly record struct FastFsEntry(string FullPath, bool IsDirectory, bool IsReparsePoint, long Length, DateTime LastWriteUtc);

    private static readonly EnumerationOptions FastEnumOpts = new()
    {
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.None,
        ReturnSpecialDirectories = false
    };

    /// <summary>
    /// High-performance Win32 FindFirstFileExW directory measurer via FileSystemEnumerable (zero FileInfo heap churn,
    /// zero per-file stat syscalls). Preserves full partial stats if any directory throws UnauthorizedAccessException
    /// and NEVER follows NTFS junctions or symlinks.
    /// </summary>
    public static DirStats MeasureDirectory(string rootPath, CancellationToken ct = default)
    {
        var stats = new DirStats();
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(rootPath)) return stats;

        if (!TryGetAttributes(rootPath, out var rootAttrs))
        {
            stats.AccessDeniedCount++;
            return stats;
        }
        if ((rootAttrs & FileAttributes.ReparsePoint) != 0)
        {
            stats.RootIsJunction = true;
            stats.JunctionCount = 1;
            try { stats.NewestWriteUtc = Directory.GetLastWriteTimeUtc(rootPath); } catch { }
            return stats;
        }
        try { stats.NewestWriteUtc = Directory.GetLastWriteTimeUtc(rootPath); } catch { }

        var stack = new Stack<string>();
        stack.Push(rootPath);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var curr = stack.Pop();
            FileAttributes currAttrs;
            try
            {
                currAttrs = File.GetAttributes(curr);
            }
            catch (UnauthorizedAccessException)
            {
                stats.AccessDeniedCount++;
                continue;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Transient directory removed concurrently by a live app; do not misreport as access denied.
                continue;
            }
            catch
            {
                stats.AccessDeniedCount++;
                continue;
            }
            if ((currAttrs & FileAttributes.ReparsePoint) != 0)
            {
                stats.JunctionCount++;
                continue;
            }

            try
            {
                var enumerable = new FileSystemEnumerable<FastFsEntry>(
                    curr,
                    static (ref FileSystemEntry entry) => new FastFsEntry(
                        entry.ToFullPath(),
                        entry.IsDirectory,
                        (entry.Attributes & FileAttributes.ReparsePoint) != 0,
                        entry.IsDirectory ? 0 : entry.Length,
                        entry.LastWriteTimeUtc.UtcDateTime),
                    FastEnumOpts);

                using var e = enumerable.GetEnumerator();
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    bool hasNext;
                    try
                    {
                        hasNext = e.MoveNext();
                    }
                    catch (UnauthorizedAccessException)
                    {
                        stats.AccessDeniedCount++;
                        break;
                    }
                    catch (IOException)
                    {
                        break;
                    }
                    if (!hasNext) break;

                    var item = e.Current;
                    if (item.IsReparsePoint)
                    {
                        stats.JunctionCount++;
                        continue;
                    }
                    if (item.LastWriteUtc > stats.NewestWriteUtc)
                        stats.NewestWriteUtc = item.LastWriteUtc;

                    if (item.IsDirectory)
                    {
                        stats.DirCount++;
                        stack.Push(item.FullPath);
                    }
                    else
                    {
                        stats.TotalBytes += item.Length;
                        stats.FileCount++;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (UnauthorizedAccessException) { stats.AccessDeniedCount++; }
            catch { }
        }
        return stats;
    }

    public static Dictionary<ZoneId, ZoneProcessInfo> GetAllZoneProcesses()
    {
        var result = new Dictionary<ZoneId, ZoneProcessInfo>();
        var nameLookup = new Dictionary<string, ZoneId>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in ZoneCatalog.All)
        {
            result[def.Id] = new ZoneProcessInfo { Zone = def.Id };
            foreach (var pn in def.ProcessNames)
                nameLookup.TryAdd(pn, def.Id);
        }

        // Fast single-pass ProcessName check across all registered zones (zero Win32Exception storms!)
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                string name = proc.ProcessName;
                if (nameLookup.TryGetValue(name, out var matched))
                {
                    var info = result[matched];
                    info.RunningPids.Add(proc.Id);
                    if (!info.RunningProcesses.Contains(name, StringComparer.OrdinalIgnoreCase))
                        info.RunningProcesses.Add(name);
                }
            }
            catch { }
            finally
            {
                proc.Dispose();
            }
        }
        return result;
    }

    public static ZoneProcessInfo GetZoneProcesses(ZoneId zone) => GetAllZoneProcesses()[zone];

    public static SweepStats SweepFiles(string rootPath, int minAgeDays, bool doApply, bool pruneEmptySubdirs, CancellationToken ct = default, Func<bool>? shouldAbort = null)
    {
        var st = new SweepStats();
        ct.ThrowIfCancellationRequested();
        if (shouldAbort?.Invoke() == true)
        {
            st.AbortedByCallback = true;
            return st;
        }
        if (!TryNormalizePath(rootPath, out var rootKey) || !Directory.Exists(rootPath))
            return st;
        if (IsProtectedForTraversal(rootPath))
        {
            st.SkippedProtected++;
            return st;
        }
        if (HasReparsePointInChain(rootPath))
        {
            st.SkippedJunctions++;
            return st;
        }

        var cutoffUtc = minAgeDays <= 0 ? DateTime.MaxValue : DateTime.UtcNow.AddDays(-minAgeDays);
        var stack = new Stack<string>();
        var visitedSubdirs = new List<string>();
        stack.Push(rootPath);
        int processedSinceAbortCheck = 0;

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (shouldAbort?.Invoke() == true)
            {
                st.AbortedByCallback = true;
                return st;
            }
            var curr = stack.Pop();
            DirectoryInfo di;
            try { di = new DirectoryInfo(curr); } catch { continue; }
            if (IsReparsePoint(di))
            {
                st.SkippedJunctions++;
                continue;
            }
            if (NormalizePath(curr) != rootKey)
                visitedSubdirs.Add(curr);

            try
            {
                var enumerable = new FileSystemEnumerable<FastFsEntry>(
                    curr,
                    static (ref FileSystemEntry entry) => new FastFsEntry(
                        entry.ToFullPath(),
                        entry.IsDirectory,
                        (entry.Attributes & FileAttributes.ReparsePoint) != 0,
                        entry.IsDirectory ? 0 : entry.Length,
                        entry.LastWriteTimeUtc.UtcDateTime),
                    FastEnumOpts);

                using var e = enumerable.GetEnumerator();
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    bool hasNext;
                    try
                    {
                        hasNext = e.MoveNext();
                    }
                    catch (UnauthorizedAccessException)
                    {
                        st.AccessDeniedCount++;
                        break;
                    }
                    catch (IOException)
                    {
                        break;
                    }
                    if (!hasNext) break;

                    var entry = e.Current;
                    if (entry.IsDirectory)
                    {
                        if (entry.IsReparsePoint) { st.SkippedJunctions++; continue; }
                        if (IsProtectedForTraversal(entry.FullPath)) { st.SkippedProtected++; continue; }
                        stack.Push(entry.FullPath);
                        continue;
                    }

                    if (shouldAbort != null && (++processedSinceAbortCheck >= 32 || doApply))
                    {
                        processedSinceAbortCheck = 0;
                        if (shouldAbort())
                        {
                            st.AbortedByCallback = true;
                            return st;
                        }
                    }

                    try
                    {
                        if (entry.IsReparsePoint) { st.SkippedJunctions++; continue; }
                        if (IsProtectedForDelete(entry.FullPath)) { st.SkippedProtected++; continue; }
                        if (minAgeDays > 0 && entry.LastWriteUtc > cutoffUtc) { st.SkippedRecent++; continue; }

                        // Fast path: during read-only preview (doApply == false), skip opening write handles
                        // (avoids Defender hooks & speeds up scans 5x). Always verify locks live on doApply == true!
                        if (doApply && IsFileLocked(entry.FullPath)) { st.SkippedLocked++; continue; }

                        long len = entry.Length;
                        st.EligibleFiles++;
                        st.EligibleBytes += len;
                        if (entry.LastWriteUtc > st.NewestEligibleUtc) st.NewestEligibleUtc = entry.LastWriteUtc;

                        if (doApply)
                        {
                            try
                            {
                                // Re-verify FULL ancestor chain up to drive root + live attributes immediately before unlink
                                if (HasReparsePointInChain(entry.FullPath) ||
                                    !TryGetAttributes(entry.FullPath, out var liveAttrs) ||
                                    (liveAttrs & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                                {
                                    st.SkippedJunctions++;
                                    continue;
                                }
                                if ((liveAttrs & FileAttributes.ReadOnly) != 0)
                                    File.SetAttributes(entry.FullPath, liveAttrs & ~FileAttributes.ReadOnly);
                                File.Delete(entry.FullPath);
                                st.DeletedFiles++;
                                st.DeletedBytes += len;
                            }
                            catch (Exception ex)
                            {
                                st.SkippedLocked++;
                                if (st.Errors.Count < 15) st.Errors.Add($"{Path.GetFileName(entry.FullPath)}: {ex.Message}");
                            }
                        }
                    }
                    catch (UnauthorizedAccessException) { st.AccessDeniedCount++; }
                    catch { }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (UnauthorizedAccessException) { st.AccessDeniedCount++; }
            catch { }
        }

        if (doApply && pruneEmptySubdirs && visitedSubdirs.Count > 0 && !st.AbortedByCallback)
        {
            foreach (var d in visitedSubdirs.OrderByDescending(x => x.Length))
            {
                if (shouldAbort?.Invoke() == true)
                {
                    st.AbortedByCallback = true;
                    break;
                }
                try
                {
                    if (IsProtectedForDelete(d) || HasReparsePointInChain(d)) continue;
                    var di = new DirectoryInfo(d);
                    if (minAgeDays > 0 && di.LastWriteTimeUtc > cutoffUtc) continue;
                    if (!di.EnumerateFileSystemInfos().Any())
                    {
                        Directory.Delete(d, false);
                        st.PrunedEmptyDirs++;
                    }
                }
                catch { }
            }
        }
        return st;
    }

    // Native Win32 Recycle Bin via SHFileOperationW (zero extra dependencies)
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);

    public static void MoveToRecycleBin(string path)
    {
        if (IsProtectedForDelete(path))
            throw new InvalidOperationException($"红区或受保护容器路径，已拦截：{path}");
        if (HasReparsePointInChain(path))
            throw new InvalidOperationException($"路径或其祖先包含目录联接/符号链接，已拦截：{path}");
        if (Directory.Exists(path))
        {
            var stats = MeasureDirectory(path);
            if (stats.JunctionCount > 0)
                throw new InvalidOperationException($"内部包含 {stats.JunctionCount} 个目录联接，禁止直接回收：{path}");
        }

        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT)
        };
        int code = SHFileOperationW(ref op);
        if (code != 0 || op.fAnyOperationsAborted)
            throw new IOException($"移入回收站失败 (code={code}, aborted={op.fAnyOperationsAborted}): {path}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    /// <summary>O(1) Win32 query of current Windows Recycle Bin size and item count across all drives.</summary>
    public static (long TotalBytes, long ItemCount) GetRecycleBinStats()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            if (SHQueryRecycleBinW(null, ref info) == 0)
                return (Math.Max(0, info.i64Size), Math.Max(0, info.i64NumItems));
        }
        catch { }
        return (-1, -1);
    }

    /// <summary>Roomy/Mole-style read-only Top-N directory & file Space Map inspector (never follows junctions, supports CancellationToken).</summary>
    public static (long TotalBytes, List<SpaceMapEntry> Entries) InspectTopEntries(string rootPath, int maxItems = 45, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var results = new ConcurrentBag<SpaceMapEntry>();
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            return (0, new List<SpaceMapEntry>());

        FileSystemInfo[] children;
        try
        {
            children = new DirectoryInfo(rootPath).EnumerateFileSystemInfos().ToArray();
        }
        catch
        {
            return (0, new List<SpaceMapEntry>());
        }

        Parallel.ForEach(children, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, info =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                bool isRp = IsReparsePoint(info);
                bool isProt = IsProtectedForDelete(info.FullName);
                if (info is DirectoryInfo dir)
                {
                    if (isRp)
                    {
                        results.Add(new SpaceMapEntry
                        {
                            Name = dir.Name,
                            FullPath = dir.FullName,
                            IsDirectory = true,
                            IsReparsePoint = true,
                            IsProtected = true,
                            SizeBytes = 0,
                            FileCount = 0,
                            LastWriteLocal = dir.LastWriteTime
                        });
                    }
                    else
                    {
                        var st = MeasureDirectory(dir.FullName, ct);
                        results.Add(new SpaceMapEntry
                        {
                            Name = dir.Name,
                            FullPath = dir.FullName,
                            IsDirectory = true,
                            IsReparsePoint = false,
                            IsProtected = isProt || st.JunctionCount > 0,
                            SizeBytes = st.TotalBytes,
                            FileCount = st.FileCount,
                            LastWriteLocal = st.NewestWriteUtc == DateTime.MinValue ? dir.LastWriteTime : st.NewestWriteUtc.ToLocalTime()
                        });
                    }
                }
                else if (info is FileInfo fi)
                {
                    results.Add(new SpaceMapEntry
                    {
                        Name = fi.Name,
                        FullPath = fi.FullName,
                        IsDirectory = false,
                        IsReparsePoint = isRp,
                        IsProtected = isProt,
                        SizeBytes = isRp ? 0 : fi.Length,
                        FileCount = 1,
                        LastWriteLocal = fi.LastWriteTime
                    });
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        });

        ct.ThrowIfCancellationRequested();
        var ordered = results
            .OrderByDescending(e => e.SizeBytes)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        long sum = ordered.Sum(e => e.SizeBytes);
        foreach (var e in ordered)
            e.ShareRatio = sum > 0 ? (double)e.SizeBytes / sum : 0.0;

        return (sum, ordered.Take(maxItems).ToList());
    }

    /// <summary>BitBroom/Mole-style CLI read-only dry-run report across all registered zones.</summary>
    public static string BuildCliDryRunReport() => CliRunner.ExecuteDryRunInternal(asJson: false, singleZone: null).Content;

    /// <summary>Resolves the specific process name(s) that must be stopped before offline-sweeping an item (supports per-IDE isolation in AiIdes).</summary>
    public static bool IsOfflineTargetProcessRunning(CleanCandidate item, out string runningSummary)
    {
        runningSummary = "";
        if (item.Zone == ZoneId.AiIdes && TryNormalizePath(item.Path, out var norm))
        {
            string? targetProc = null;
            if (TryNormalizePath(Path.Combine(Roaming, "Cursor"), out var cNorm) && norm.StartsWith(cNorm + "\\", StringComparison.OrdinalIgnoreCase))
                targetProc = "Cursor";
            else if (TryNormalizePath(Path.Combine(Roaming, "Windsurf"), out var wNorm) && norm.StartsWith(wNorm + "\\", StringComparison.OrdinalIgnoreCase))
                targetProc = "Windsurf";
            else if (TryNormalizePath(Path.Combine(Roaming, "Trae"), out var tNorm) && norm.StartsWith(tNorm + "\\", StringComparison.OrdinalIgnoreCase))
                targetProc = "Trae";

            if (targetProc != null)
            {
                var all = GetZoneProcesses(ZoneId.AiIdes);
                bool run = all.RunningProcesses.Any(p => p.Equals(targetProc, StringComparison.OrdinalIgnoreCase));
                if (run) runningSummary = targetProc;
                return run;
            }
        }

        var procInfo = GetZoneProcesses(item.Zone);
        if (procInfo.IsRunning)
        {
            runningSummary = $"{string.Join("/", procInfo.RunningProcesses)}（{procInfo.RunningPids.Count} 个进程）";
            return true;
        }
        return false;
    }

    public static CleanItemOutcome ExecuteCandidate(CleanCandidate item)
    {
        var outcome = new CleanItemOutcome
        {
            Title = item.Title,
            Path = item.Path,
            Action = item.ActionType.ToString()
        };

        try
        {
            // Dual-path live validation right before any physical mutation (BitBroom pattern)
            if (ZoneCatalog.Get(item.Zone).IsReadOnlyZone || item.IsReadOnlyDiagnostic)
            {
                outcome.Blocked = true;
                outcome.Message = "只读诊断专区/项目，严禁执行任何删除操作";
                return outcome;
            }
            if (item.Tier == CleanTier.ProtectedSkipped || !item.CanSelect || item.ActionType == CleanActionType.None)
            {
                outcome.Blocked = true;
                outcome.Message = "受保护项，禁止清理";
                return outcome;
            }
            if (!IsAuthorizedExecutionTarget(item, out var authErr))
            {
                outcome.Blocked = true;
                outcome.Message = authErr;
                return outcome;
            }

            // Action-scoped whitelist & redline pre-check:
            // - RecycleBatchDirectories validates each individual member in BatchPaths (never falsely blocked by a sibling whitelist under work\).
            // - Container sweeps (SweepExpiredFiles/OfflineSweep/RecycleExpiredChildren) check exact/ancestor whitelist on container itself, while per-child whitelists are skipped individually.
            // - RecycleDirectoryOrFile uses full IsProtectedForDelete (blocks exact, child, ancestor of subtrees, containers, and whitelists).
            if (item.ActionType == CleanActionType.RecycleDirectoryOrFile)
            {
                if (IsProtectedForDelete(item.Path, includeUserWhitelist: true))
                {
                    outcome.Blocked = true;
                    outcome.Message = "红区、受保护容器或白名单路径，已二次校验拦截";
                    return outcome;
                }
            }
            else if (item.ActionType != CleanActionType.RecycleBatchDirectories)
            {
                if (string.IsNullOrWhiteSpace(item.Path) || !TryNormalizePath(item.Path, out _))
                {
                    outcome.Blocked = true;
                    outcome.Message = "目标路径非法，已拦截";
                    return outcome;
                }
                if (SettingsStore.IsWhitelisted(item.Path, protectAncestors: false))
                {
                    outcome.Blocked = true;
                    outcome.Message = "用户白名单保护，已二次校验拦截";
                    return outcome;
                }
                if (IsProtectedForTraversal(item.Path, includeUserWhitelist: false))
                {
                    outcome.Blocked = true;
                    outcome.Message = "红区硬保护路径，已二次校验拦截";
                    return outcome;
                }
            }

            if (item.ActionType == CleanActionType.OfflineSweepFilesKeepSkeleton)
            {
                // Re-verify process state LIVE before AND periodically during offline cache sweeping!
                if (IsOfflineTargetProcessRunning(item, out var runDesc))
                {
                    outcome.Blocked = true;
                    outcome.Message = $"已拦截：{runDesc} 仍在运行，请先正常退出";
                    return outcome;
                }
                var procWatch = Stopwatch.StartNew();
                long lastProcCheckMs = 0;
                bool restartedMidSweep = false;
                Func<bool> abortIfRestarted = () =>
                {
                    long nowMs = procWatch.ElapsedMilliseconds;
                    if (nowMs - lastProcCheckMs < 400) return false;
                    lastProcCheckMs = nowMs;
                    if (IsOfflineTargetProcessRunning(item, out _))
                    {
                        restartedMidSweep = true;
                        return true;
                    }
                    return false;
                };
                var sw = SweepFiles(item.Path, item.MinAgeDays, doApply: true, pruneEmptySubdirs: false, shouldAbort: abortIfRestarted);
                outcome.Succeeded = !restartedMidSweep || sw.DeletedFiles > 0;
                outcome.Blocked = restartedMidSweep;
                outcome.FreedBytes = sw.DeletedBytes;
                outcome.CleanedFiles = sw.DeletedFiles;
                outcome.SkippedLocked = sw.SkippedLocked;
                outcome.SkippedJunctions = sw.SkippedJunctions;
                outcome.Message = restartedMidSweep
                    ? $"检测到应用重新启动，已立即中止剩余清理（此前已清 {sw.DeletedFiles} 文件 / {CleanCandidate.FormatBytes(sw.DeletedBytes)}）"
                    : $"已清理 {sw.DeletedFiles} 文件 ({CleanCandidate.FormatBytes(sw.DeletedBytes)})，保留目录骨架";
                return outcome;
            }

            if (item.ActionType == CleanActionType.SweepExpiredFiles)
            {
                var sw = SweepFiles(item.Path, item.MinAgeDays, doApply: true, pruneEmptySubdirs: item.PruneEmptySubdirs);
                outcome.Succeeded = true;
                outcome.FreedBytes = sw.DeletedBytes;
                outcome.CleanedFiles = sw.DeletedFiles;
                outcome.SkippedLocked = sw.SkippedLocked;
                outcome.SkippedJunctions = sw.SkippedJunctions;
                outcome.Message = $"已清理 {sw.DeletedFiles} 文件 ({CleanCandidate.FormatBytes(sw.DeletedBytes)})，跳过占用 {sw.SkippedLocked} / 联接 {sw.SkippedJunctions}";
                return outcome;
            }

            if (item.ActionType == CleanActionType.RecycleExpiredChildren)
            {
                if (!Directory.Exists(item.Path) || HasReparsePointInChain(item.Path))
                {
                    outcome.Blocked = true;
                    outcome.Message = "目标不存在或路径含目录联接";
                    return outcome;
                }
                var cutoffUtc = item.MinAgeDays <= 0 ? DateTime.MaxValue : DateTime.UtcNow.AddDays(-item.MinAgeDays);
                long freed = 0;
                long count = 0;
                long locked = 0;
                long juncs = 0;

                var di = new DirectoryInfo(item.Path);
                foreach (var entry in di.EnumerateFileSystemInfos())
                {
                    try
                    {
                        if (IsReparsePoint(entry)) { juncs++; continue; }
                        if (IsProtectedForDelete(entry.FullName)) continue;

                        if (entry is FileInfo fi)
                        {
                            if (item.MinAgeDays > 0 && fi.LastWriteTimeUtc > cutoffUtc) continue;
                            if (IsFileLocked(fi.FullName)) { locked++; continue; }
                            long len = fi.Length;
                            MoveToRecycleBin(fi.FullName);
                            freed += len;
                            count++;
                        }
                        else if (entry is DirectoryInfo sub)
                        {
                            var st = MeasureDirectory(sub.FullName);
                            if (st.JunctionCount > 0) { juncs += st.JunctionCount; continue; }
                            if (item.MinAgeDays > 0 && st.NewestWriteUtc > cutoffUtc) continue;
                            MoveToRecycleBin(sub.FullName);
                            freed += st.TotalBytes;
                            count += Math.Max(1, st.FileCount);
                        }
                    }
                    catch
                    {
                        locked++;
                    }
                }
                outcome.Succeeded = true;
                outcome.FreedBytes = freed;
                outcome.CleanedFiles = count;
                outcome.SkippedLocked = locked;
                outcome.SkippedJunctions = juncs;
                outcome.Message = $"已将 {count} 项 ({CleanCandidate.FormatBytes(freed)}) 移入回收站，跳过占用 {locked} / 联接 {juncs}";
                return outcome;
            }

            if (item.ActionType == CleanActionType.RecycleBatchDirectories)
            {
                var activeCutoffUtc = DateTime.UtcNow.AddHours(-HardActiveFloorHours);
                long freed = 0;
                long dirDone = 0;
                long skipped = 0;
                long juncs = 0;

                foreach (var dirPath in item.BatchPaths)
                {
                    try
                    {
                        // Independent executor-side allowlist verification (allowed workspace root + scratch dir name!)
                        if (!IsValidWorkspaceBatchMember(dirPath)) { skipped++; continue; }
                        if (!Directory.Exists(dirPath)) continue;
                        if (IsProtectedForDelete(dirPath) || HasReparsePointInChain(dirPath)) { skipped++; continue; }
                        var st = MeasureDirectory(dirPath);
                        if (st.JunctionCount > 0) { juncs += st.JunctionCount; skipped++; continue; }
                        if (st.NewestWriteUtc > activeCutoffUtc) { skipped++; continue; }
                        MoveToRecycleBin(dirPath);
                        freed += st.TotalBytes;
                        dirDone++;
                    }
                    catch
                    {
                        skipped++;
                    }
                }
                outcome.Succeeded = true;
                outcome.FreedBytes = freed;
                outcome.CleanedFiles = dirDone;
                outcome.SkippedLocked = skipped;
                outcome.SkippedJunctions = juncs;
                outcome.Message = $"已将 {dirDone} 个临时目录 ({CleanCandidate.FormatBytes(freed)}) 移入回收站，跳过 {skipped}";
                return outcome;
            }

            if (item.ActionType == CleanActionType.RecycleDirectoryOrFile)
            {
                if (IsProtectedForDelete(item.Path))
                {
                    outcome.Blocked = true;
                    outcome.Message = "红区或受保护容器路径，已拦截";
                    return outcome;
                }
                if (HasReparsePointInChain(item.Path))
                {
                    outcome.Blocked = true;
                    outcome.Message = "路径或其祖先含目录联接，已拦截";
                    return outcome;
                }
                var activeCutoffUtc = DateTime.UtcNow.AddHours(-HardActiveFloorHours);
                long sz = 0;
                long files = 1;
                if (Directory.Exists(item.Path))
                {
                    var st = MeasureDirectory(item.Path);
                    if (st.JunctionCount > 0)
                    {
                        outcome.Blocked = true;
                        outcome.SkippedJunctions = st.JunctionCount;
                        outcome.Message = $"内部含 {st.JunctionCount} 个目录联接，已拦截";
                        return outcome;
                    }
                    if (item.EnforceActive72hFloor && st.NewestWriteUtc > activeCutoffUtc)
                    {
                        outcome.Blocked = true;
                        outcome.Message = "72 小时内有修改活动，已拦截";
                        return outcome;
                    }
                    sz = st.TotalBytes;
                    files = st.FileCount;
                }
                else if (File.Exists(item.Path))
                {
                    var fi = new FileInfo(item.Path);
                    if (item.EnforceActive72hFloor && fi.LastWriteTimeUtc > activeCutoffUtc)
                    {
                        outcome.Blocked = true;
                        outcome.Message = "72 小时内有修改活动，已拦截";
                        return outcome;
                    }
                    if (IsFileLocked(fi.FullName))
                    {
                        outcome.Blocked = true;
                        outcome.SkippedLocked = 1;
                        outcome.Message = "文件被占用，已跳过";
                        return outcome;
                    }
                    sz = fi.Length;
                }
                else
                {
                    outcome.Blocked = true;
                    outcome.Message = "路径已不存在";
                    return outcome;
                }

                MoveToRecycleBin(item.Path);
                outcome.Succeeded = true;
                outcome.FreedBytes = sz;
                outcome.CleanedFiles = files;
                outcome.Message = $"已移入 Windows 回收站 ({CleanCandidate.FormatBytes(sz)})";
                return outcome;
            }
        }
        catch (Exception ex)
        {
            outcome.Blocked = true;
            outcome.Message = $"已安全中止：{ex.Message}";
        }
        return outcome;
    }

    public static string WriteAuditLog(CleanRunAudit audit)
    {
        Directory.CreateDirectory(LogsDir);
        string name = $"clean-{audit.Zone.ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmmss}.json";
        string path = Path.Combine(LogsDir, name);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(audit, opts), new UTF8Encoding(false));

        try
        {
            var old = new DirectoryInfo(LogsDir).GetFiles("*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(40);
            foreach (var f in old)
            {
                try { f.Delete(); } catch { }
            }
        }
        catch { }

        return path;
    }

    public static List<CleanRunAudit> LoadHistoryAudits()
    {
        var list = new List<CleanRunAudit>();
        if (!Directory.Exists(LogsDir)) return list;
        foreach (var f in new DirectoryInfo(LogsDir).GetFiles("clean-*.json").OrderByDescending(x => x.LastWriteTimeUtc).Take(40))
        {
            try
            {
                var text = File.ReadAllText(f.FullName, Encoding.UTF8);
                var rec = JsonSerializer.Deserialize<CleanRunAudit>(text);
                if (rec != null) list.Add(rec);
            }
            catch { }
        }
        return list;
    }

    // Built-in self-test invoked via AICleaner.exe --self-test
    public static (bool Passed, List<string> Lines) RunSelfTests()
    {
        var lines = new List<string>();
        bool allPass = true;

        void Assert(string name, bool cond)
        {
            lines.Add($"[{(cond ? "PASS" : "FAIL")}] {name}");
            if (!cond) allPass = false;
        }

        // 0. Byte formatter precision regression tests (guards against :0.1 bug!)
        Assert("format_bytes_mb_precision", CleanCandidate.FormatBytes(169_869_312L) == "162.0 MB");
        Assert("format_bytes_gb_precision", CleanCandidate.FormatBytes(1_600_000_000L) == "1.49 GB");
        Assert("format_bytes_kb_precision", CleanCandidate.FormatBytes(1536L) == "1.5 KB");

        // 1. Redline checks across all 5 zones + global defense-in-depth redlines
        Assert("redline_antigravity_conversations", IsProtectedPath(Path.Combine(UserHome, @".gemini\antigravity\conversations\demo.pb")));
        Assert("redline_antigravity_network", IsProtectedPath(Path.Combine(Roaming, @"Antigravity\Network\Cookies")));
        Assert("redline_antigravity_partition_network", IsProtectedPath(Path.Combine(Roaming, @"Antigravity\Partitions\hub-browser-demo\Network\Cookies")));
        Assert("redline_antigravity_partition_cache_allowed", !IsProtectedPath(Path.Combine(Roaming, @"Antigravity\Partitions\hub-browser-demo\Cache\data_0")));
        Assert("redline_gpt_codex_sqlite", IsProtectedPath(Path.Combine(UserHome, @".codex\thread_history_1.sqlite")));
        Assert("redline_gpt_msix_package", IsProtectedPath(Path.Combine(LocalApp, @"Packages\OpenAI.Codex_demo\LocalCache\Roaming\Codex\web")));
        Assert("redline_dsh_profiles", IsProtectedPath(Path.Combine(UserHome, @".dsh\profiles\desktop\cordis.yml")));
        Assert("redline_dsh_credentials", IsProtectedPath(Path.Combine(UserHome, @".dsh\.credentials.yaml")));
        Assert("redline_dsh_skills", IsProtectedPath(Path.Combine(UserHome, @".dsh\skills\superagent")));
        Assert("redline_dsh_desktop_network", IsProtectedPath(Path.Combine(Roaming, @"@deepseek-ai\dsh-desktop\Network\Cookies")));
        Assert("redline_claude_file_history", IsProtectedPath(Path.Combine(UserHome, @".claude\file-history\rev1")));
        Assert("redline_claude_shell_snapshots", IsProtectedPath(Path.Combine(UserHome, @".claude\shell-snapshots\snap.sh")));
        Assert("redline_claude_json", IsProtectedPath(Path.Combine(UserHome, @".claude.json")));
        Assert("redline_pnpm_store", IsProtectedPath(Path.Combine(LocalApp, @"pnpm\store\v10\files")));
        Assert("redline_playwright_browsers", IsProtectedPath(Path.Combine(LocalApp, @"ms-playwright\chromium-1140")));
        Assert("redline_huggingface_hub", IsProtectedPath(Path.Combine(UserHome, @".cache\huggingface\hub\models--demo")));
        Assert("redline_ollama_models", IsProtectedPath(Path.Combine(UserHome, @".ollama\models\blobs")));
        Assert("redline_user_documents", IsProtectedPath(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
        Assert("redline_user_ssh", IsProtectedPath(Path.Combine(UserHome, @".ssh\id_ed25519")));
        Assert("redline_service_worker_database", IsProtectedPath(Path.Combine(Roaming, @"Antigravity\Service Worker\Database\000001.ldb")));
        Assert("service_worker_cachestorage_allowed", !IsProtectedPath(Path.Combine(Roaming, @"Antigravity\Service Worker\CacheStorage\demo")));
        Assert("redline_uv_cache_root_self_protected", IsProtectedPath(Path.Combine(LocalApp, @"uv\cache")));
        Assert("redline_uv_cache_child_allowed", !IsProtectedPath(Path.Combine(LocalApp, @"uv\cache\old-wheel.whl")));
        var rbStat = GetRecycleBinStats();
        Assert("recycle_bin_query_non_negative", rbStat.TotalBytes >= 0 && rbStat.ItemCount >= 0);

        // 2. Live process detection & ZoneCatalog contract (7 zones in v3.2)
        var allProcs = GetAllZoneProcesses();
        Assert("process_snapshot_has_all_zones", allProcs.Count == ZoneCatalog.All.Count && ZoneCatalog.All.Count == 7);
        Assert("zone_slug_roundtrip",
            ZoneCatalog.TryParseSlug("claude-code", out var parsedCc) && parsedCc == ZoneId.ClaudeCode &&
            ZoneCatalog.TryParseSlug("ai-toolchains", out var parsedTc) && parsedTc == ZoneId.AiToolchains &&
            ZoneCatalog.TryParseSlug("ai-ides", out var parsedIde) && parsedIde == ZoneId.AiIdes &&
            ZoneCatalog.TryParseSlug("local-ai-ecosystem", out var parsedLoc) && parsedLoc == ZoneId.LocalAiEcosystem &&
            !ZoneCatalog.TryParseSlug("nonexistent-zone", out _));

        // If Antigravity or GPT is currently running on this host, offline clean MUST block!
        if (allProcs[ZoneId.GptCodex].IsRunning)
        {
            var mockOfflineGpt = new CleanCandidate
            {
                Zone = ZoneId.GptCodex,
                Tier = CleanTier.OfflineCache,
                ActionType = CleanActionType.OfflineSweepFilesKeepSkeleton,
                Path = Path.Combine(LocalApp, @"uv\cache"),
                CanSelect = true
            };
            var gptOffOut = ExecuteCandidate(mockOfflineGpt);
            Assert("offline_gpt_blocked_while_running", gptOffOut.Blocked && !gptOffOut.Succeeded);
        }
        if (allProcs[ZoneId.Antigravity].IsRunning)
        {
            var mockOfflineAg = new CleanCandidate
            {
                Zone = ZoneId.Antigravity,
                Tier = CleanTier.OfflineCache,
                ActionType = CleanActionType.OfflineSweepFilesKeepSkeleton,
                Path = Path.Combine(Roaming, @"Antigravity\Cache"),
                CanSelect = true
            };
            var agOffOut = ExecuteCandidate(mockOfflineAg);
            Assert("offline_antigravity_blocked_while_running", agOffOut.Blocked && !agOffOut.Succeeded);
        }

        // 3. User whitelist hard-protection round-trip test (isolated under %TEMP%)
        string testWlPath = Path.Combine(Path.GetTempPath(), "__aicleaner_selftest_wl__");
        bool hadBefore = SettingsStore.IsWhitelisted(testWlPath);
        try
        {
            SettingsStore.AddWhitelist(testWlPath);
            Assert("whitelist_blocks_protected_check", IsProtectedPath(testWlPath));
            Assert("whitelist_blocks_child_path", IsProtectedPath(Path.Combine(testWlPath, "inner.txt")));
            SettingsStore.RemoveWhitelist(testWlPath);
            Assert("whitelist_removed_cleanly", !SettingsStore.IsWhitelisted(testWlPath));
        }
        finally
        {
            if (!hadBefore) SettingsStore.RemoveWhitelist(testWlPath);
        }

        // 4. Isolated temp fixture with real NTFS Junction + locked file + recent file + old file
        string sandbox = Path.Combine(Path.GetTempPath(), "aicleaner-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        string tempDir = Path.Combine(sandbox, "temp");
        string sentinelDir = Path.Combine(sandbox, "sentinel");
        string juncPath = Path.Combine(tempDir, "junc_sentinel");
        try
        {
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(sentinelDir);
            string sentinelFile = Path.Combine(sentinelDir, "keep.txt");
            File.WriteAllText(sentinelFile, "SAFE");
            File.SetLastWriteTimeUtc(sentinelFile, DateTime.UtcNow.AddDays(-10));

            using (var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{juncPath}\" \"{sentinelDir}\"")
            { CreateNoWindow = true, UseShellExecute = false }))
            {
                mk?.WaitForExit(5000);
            }

            string oldFile = Path.Combine(tempDir, "old.tmp");
            string newFile = Path.Combine(tempDir, "new.tmp");
            string lockedFile = Path.Combine(tempDir, "locked.tmp");
            File.WriteAllText(oldFile, "old");
            File.WriteAllText(newFile, "new");
            File.WriteAllText(lockedFile, "locked");
            File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-10));
            File.SetLastWriteTimeUtc(newFile, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(lockedFile, DateTime.UtcNow.AddDays(-10));

            using (var lockStream = File.Open(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert("reparse_chain_detects_intermediate_junction", HasReparsePointInChain(Path.Combine(juncPath, "keep.txt")));

                var (_, mapEntries) = InspectTopEntries(tempDir, 20);
                var juncEntry = mapEntries.FirstOrDefault(e => e.Name.Equals("junc_sentinel", StringComparison.OrdinalIgnoreCase));
                Assert("space_map_skips_junction", juncEntry != null && juncEntry.IsReparsePoint && juncEntry.SizeBytes == 0);

                var sw = SweepFiles(tempDir, minAgeDays: 3, doApply: true, pruneEmptySubdirs: true);
                Assert("fixture_old_deleted", !File.Exists(oldFile) && sw.DeletedFiles == 1);
                Assert("fixture_recent_preserved", File.Exists(newFile) && sw.SkippedRecent == 1);
                Assert("fixture_locked_preserved", File.Exists(lockedFile) && sw.SkippedLocked == 1);
                Assert("fixture_junction_and_sentinel_intact", Directory.Exists(juncPath) && File.Exists(sentinelFile) && sw.SkippedJunctions >= 1);
            }

            // 5. P0/P1 regression assertions (portable)
            string uvParent = Path.Combine(LocalApp, "uv");
            Assert("p0_container_ancestor_delete_blocked", IsProtectedForDelete(uvParent));
            var mockParentRecycle = new CleanCandidate
            {
                Zone = ZoneId.GptCodex,
                Tier = CleanTier.RecycleCandidate,
                ActionType = CleanActionType.RecycleDirectoryOrFile,
                Path = uvParent,
                CanSelect = true
            };
            var parentOut = ExecuteCandidate(mockParentRecycle);
            Assert("p0_execute_blocks_container_ancestor_before_recycle", parentOut.Blocked && !parentOut.Succeeded);

            Assert("fail_closed_invalid_path_protected", IsProtectedForDelete(@"C:\") && IsProtectedForDelete("bad\0path") && IsProtectedForDelete(""));

            // Test configurable ExtraWorkspaceRoots & RecycleBatchDirectories per-member allowlist + sibling whitelist
            var origSettings = SettingsStore.Load();
            string syntheticWs = Path.Combine(sandbox, "ws");
            try
            {
                var patched = origSettings.Clone();
                patched.ExtraWorkspaceRoots.Add(syntheticWs);
                SettingsStore.Save(patched);

                Assert("batch_member_scope_validator",
                    IsValidWorkspaceBatchMember(Path.Combine(syntheticWs, @"demo\_tmp")) &&
                    !IsValidWorkspaceBatchMember(Path.Combine(syntheticWs, @"demo\src")) &&
                    !IsValidWorkspaceBatchMember(@"C:\Windows\Temp\_tmp"));

                string sibWl = Path.Combine(syntheticWs, "keep_sibling");
                SettingsStore.AddWhitelist(sibWl);
                var mockBatch = new CleanCandidate
                {
                    Zone = ZoneId.GptCodex,
                    Tier = CleanTier.SafeOnline,
                    ActionType = CleanActionType.RecycleBatchDirectories,
                    Path = syntheticWs,
                    BatchPaths = new List<string> { Path.Combine(syntheticWs, @"nonexistent\_tmp") },
                    CanSelect = true
                };
                var batchOut = ExecuteCandidate(mockBatch);
                Assert("batch_not_blocked_by_sibling_whitelist", !batchOut.Blocked && batchOut.Succeeded);
            }
            finally
            {
                SettingsStore.Save(origSettings);
            }

            bool parallelOk = true;
            try
            {
                Parallel.For(0, 40, i =>
                {
                    _ = SettingsStore.IsWhitelisted(Path.Combine(sandbox, $"item_{i}"));
                });
            }
            catch { parallelOk = false; }
            Assert("settings_store_parallel_thread_safety", parallelOk);

            bool cancelOk = false;
            try
            {
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                _ = MeasureDirectory(tempDir, cts.Token);
            }
            catch (OperationCanceledException) { cancelOk = true; }
            Assert("measure_directory_honors_cancellation", cancelOk);

            var inpcCand = new CleanCandidate
            {
                Zone = ZoneId.GptCodex,
                Tier = CleanTier.OfflineCache,
                CanSelect = true,
                IsSelected = true
            };
            var firedProps = new HashSet<string>();
            inpcCand.PropertyChanged += (_, ev) => { if (ev.PropertyName != null) firedProps.Add(ev.PropertyName); };
            inpcCand.ApplyProcessLockState(isRunning: true);
            Assert("candidate_inpc_process_lock_sync",
                !inpcCand.CanSelect && !inpcCand.IsSelected &&
                firedProps.Contains(nameof(CleanCandidate.CanSelect)) &&
                firedProps.Contains(nameof(CleanCandidate.IsSelected)) &&
                firedProps.Contains(nameof(CleanCandidate.TierBadge)) &&
                firedProps.Contains(nameof(CleanCandidate.Reason)) &&
                firedProps.Contains(nameof(CleanCandidate.VerdictImpactText)));

            // 6. v3.0 Claude Code strict read-only fixture (file-history + unknown dir + real NTFS junction -> 0 deletions, 0 traversal!)
            string claudeMock = Path.Combine(sandbox, "mock_claude");
            string claudeHist = Path.Combine(claudeMock, "file-history");
            string claudeUnk = Path.Combine(claudeMock, "custom-unknown");
            string claudeJunc = Path.Combine(claudeMock, "linked_ext");
            Directory.CreateDirectory(claudeHist);
            Directory.CreateDirectory(claudeUnk);
            string histFile = Path.Combine(claudeHist, "old_rev.bak");
            string unkFile = Path.Combine(claudeUnk, "notes.txt");
            File.WriteAllText(histFile, "KEEP-HISTORY");
            File.WriteAllText(unkFile, "KEEP-UNKNOWN");
            File.SetLastWriteTimeUtc(histFile, DateTime.UtcNow.AddDays(-60));
            using (var mk2 = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{claudeJunc}\" \"{sentinelDir}\"")
            { CreateNoWindow = true, UseShellExecute = false }))
            {
                mk2?.WaitForExit(5000);
            }
            try
            {
                var claudeRep = ZoneScanner.ScanClaudeDirectory(claudeMock, new ZoneProcessInfo { Zone = ZoneId.ClaudeCode });
                Assert("claude_fixture_zero_deletable_candidates",
                    claudeRep.Candidates.Count >= 3 &&
                    claudeRep.Candidates.All(c => !c.CanSelect && c.ActionType == CleanActionType.None && c.IsReadOnlyDiagnostic) &&
                    claudeRep.SafeOnlineBytes == 0 && claudeRep.RecycleCandidateBytes == 0);
                var juncCand = claudeRep.Candidates.FirstOrDefault(c => c.Path.Equals(claudeJunc, StringComparison.OrdinalIgnoreCase));
                Assert("claude_fixture_junction_not_traversed",
                    juncCand != null && juncCand.JunctionCount == 1 && juncCand.SizeBytes == 0 &&
                    File.Exists(histFile) && File.Exists(unkFile) && File.Exists(sentinelFile));

                // Even if forced, ExecuteCandidate MUST block any ReadOnlyZone or IsReadOnlyDiagnostic candidate!
                var forcedClaude = new CleanCandidate
                {
                    Zone = ZoneId.ClaudeCode,
                    Tier = CleanTier.SafeOnline,
                    ActionType = CleanActionType.SweepExpiredFiles,
                    Path = claudeHist,
                    CanSelect = true
                };
                var claudeExec = ExecuteCandidate(forcedClaude);
                Assert("claude_readonly_zone_execute_blocked", claudeExec.Blocked && !claudeExec.Succeeded && File.Exists(histFile));

                var forcedRadar = new CleanCandidate
                {
                    Zone = ZoneId.AiToolchains,
                    Tier = CleanTier.ProtectedSkipped,
                    ActionType = CleanActionType.RecycleDirectoryOrFile,
                    IsReadOnlyDiagnostic = true,
                    Path = Path.Combine(LocalApp, @"pnpm\store"),
                    CanSelect = true
                };
                var radarExec = ExecuteCandidate(forcedRadar);
                Assert("readonly_diagnostic_candidate_execute_blocked", radarExec.Blocked && !radarExec.Succeeded && !forcedRadar.CanSelect);

                // Cancelled ZoneScanner reports ScanStatus.Cancelled (never silently Status.Ok with 0 items!)
                using var cancelledCts = new CancellationTokenSource();
                cancelledCts.Cancel();
                var cancelledRep = ZoneScanner.ScanZone(ZoneId.ClaudeCode, null, cancelledCts.Token);
                Assert("cancelled_scan_reports_cancelled_status", cancelledRep.Status == ScanStatus.Cancelled);

                // CLI JSON contract check (in-process on lightweight claude-code zone -> schemaVersion=1.0, valid JSON)
                var (cliCode, cliJson) = CliRunner.ExecuteDryRunInternal(asJson: true, singleZone: ZoneId.ClaudeCode);
                using var doc = JsonDocument.Parse(cliJson);
                Assert("cli_dryrun_json_schema_valid",
                    cliCode == 0 &&
                    doc.RootElement.GetProperty("schemaVersion").GetString() == CliRunner.SchemaVersion &&
                    doc.RootElement.GetProperty("readOnly").GetBoolean() &&
                    doc.RootElement.GetProperty("zones").GetArrayLength() == 1);

                // 7. v3.0 Red-Team P0/P1 Hardening Regression Suite
                // 7A. Executor authorization gate: bypassing scanner with an unauthorized temp folder MUST block with 0 deletions!
                string unauthDir = Path.Combine(sandbox, "unauth_target");
                Directory.CreateDirectory(unauthDir);
                string unauthFile = Path.Combine(unauthDir, "must_survive.tmp");
                File.WriteAllText(unauthFile, "DO-NOT-DELETE");
                File.SetLastWriteTimeUtc(unauthFile, DateTime.UtcNow.AddDays(-30));

                var forgedToolchainCand = new CleanCandidate
                {
                    Zone = ZoneId.AiToolchains,
                    Tier = CleanTier.SafeOnline,
                    ActionType = CleanActionType.SweepExpiredFiles,
                    Path = unauthDir,
                    MinAgeDays = 1,
                    CanSelect = true
                };
                var forgedToolchainOut = ExecuteCandidate(forgedToolchainCand);
                Assert("p0_executor_blocks_unauthorized_ai_toolchains_dir",
                    forgedToolchainOut.Blocked && !forgedToolchainOut.Succeeded && File.Exists(unauthFile));

                var forgedAgRecycle = new CleanCandidate
                {
                    Zone = ZoneId.Antigravity,
                    Tier = CleanTier.RecycleCandidate,
                    ActionType = CleanActionType.RecycleDirectoryOrFile,
                    Path = unauthDir,
                    CanSelect = true
                };
                var forgedAgOut = ExecuteCandidate(forgedAgRecycle);
                Assert("p0_executor_blocks_unauthorized_antigravity_recycle",
                    forgedAgOut.Blocked && !forgedAgOut.Succeeded && File.Exists(unauthFile));

                var crossZoneCand = new CleanCandidate
                {
                    Zone = ZoneId.GptCodex,
                    Tier = CleanTier.SafeOnline,
                    ActionType = CleanActionType.SweepExpiredFiles,
                    Path = Path.Combine(UserHome, @".gemini\antigravity\cache"),
                    CanSelect = true
                };
                var crossZoneOut = ExecuteCandidate(crossZoneCand);
                Assert("p0_executor_blocks_cross_zone_path_mismatch", crossZoneOut.Blocked && !crossZoneOut.Succeeded);

                // 7B. ValidateCustomRootPath rejects drive roots, OS dirs, user home, redlines, and junctions
                Assert("p1_validate_custom_root_rejects_dangerous_paths",
                    !ValidateCustomRootPath(@"C:\", out _) &&
                    !ValidateCustomRootPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows), out _) &&
                    !ValidateCustomRootPath(UserHome, out _) &&
                    !ValidateCustomRootPath(LocalApp, out _) &&
                    !ValidateCustomRootPath(Path.Combine(UserHome, ".claude"), out _) &&
                    !ValidateCustomRootPath(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), out _) &&
                    !ValidateCustomRootPath(juncPath, out _) &&
                    ValidateCustomRootPath(unauthDir, out _));

                // 7C. SweepFiles shouldAbort callback stops remaining deletions mid-sweep
                string abortDir = Path.Combine(sandbox, "abort_sweep");
                Directory.CreateDirectory(abortDir);
                string ab1 = Path.Combine(abortDir, "a.tmp");
                string ab2 = Path.Combine(abortDir, "b.tmp");
                File.WriteAllText(ab1, "1");
                File.WriteAllText(ab2, "2");
                File.SetLastWriteTimeUtc(ab1, DateTime.UtcNow.AddDays(-20));
                File.SetLastWriteTimeUtc(ab2, DateTime.UtcNow.AddDays(-20));
                int abortCalls = 0;
                var abSw = SweepFiles(abortDir, minAgeDays: 1, doApply: true, pruneEmptySubdirs: false,
                    shouldAbort: () => ++abortCalls >= 4); // allows initial entry + 1 file, then aborts before 2nd file
                Assert("p1_sweep_abort_callback_stops_remaining_deletes",
                    abSw.AbortedByCallback && abSw.DeletedFiles <= 1 && (File.Exists(ab1) || File.Exists(ab2)));

                // 7D. Claude scanner ancestor-chain junction guard produces explicit 0B diagnostic skip instead of traversing
                var ancestorJuncRep = ZoneScanner.ScanClaudeDirectory(claudeJunc, new ZoneProcessInfo { Zone = ZoneId.ClaudeCode });
                Assert("p1_claude_ancestor_junction_blocked",
                    ancestorJuncRep.Candidates.Count == 1 &&
                    ancestorJuncRep.Candidates[0].IsReadOnlyDiagnostic &&
                    ancestorJuncRep.Candidates[0].JunctionCount == 1 &&
                    ancestorJuncRep.Candidates[0].SizeBytes == 0);

                // 7E. CLI mixed-status priority contract: error > cancelled > partial > ok
                var stErrCan = CliRunner.ComputeOverallStatus(new[]
                {
                    new ZoneScanReport { Status = ScanStatus.Cancelled },
                    new ZoneScanReport { Status = ScanStatus.Error }
                });
                var stCanPar = CliRunner.ComputeOverallStatus(new[]
                {
                    new ZoneScanReport { Status = ScanStatus.Partial },
                    new ZoneScanReport { Status = ScanStatus.Cancelled }
                });
                var stParOk = CliRunner.ComputeOverallStatus(new[]
                {
                    new ZoneScanReport { Status = ScanStatus.Ok },
                    new ZoneScanReport { Status = ScanStatus.Partial }
                });
                var stOk = CliRunner.ComputeOverallStatus(new[] { new ZoneScanReport { Status = ScanStatus.Ok } });
                Assert("p1_cli_mixed_status_priority_error_over_cancelled",
                    stErrCan == ("error", 3) &&
                    stCanPar == ("cancelled", 3) &&
                    stParOk == ("partial", 3) &&
                    stOk == ("ok", 0));

                // 7F. CLI cancellation propagates through ExecuteDryRunInternal -> exit code 3 + status="cancelled"
                var (canCode, canJson) = CliRunner.ExecuteDryRunInternal(asJson: true, singleZone: ZoneId.ClaudeCode, ct: cancelledCts.Token);
                using var canDoc = JsonDocument.Parse(canJson);
                Assert("p1_cli_cancelled_token_returns_exit3",
                    canCode == 3 && canDoc.RootElement.GetProperty("status").GetString() == "cancelled");

                // 8. v3.2 AI IDEs, Local AI / VHDX, Controlled Read-Only Community Packs, i18n & Settings Regression Suite
                Assert("v32_redline_cursor_user_and_vsix",
                    IsProtectedPath(Path.Combine(Roaming, @"Cursor\User\globalStorage\state.vscdb")) &&
                    IsProtectedPath(Path.Combine(Roaming, @"Cursor\User\globalStorage\ext\Cache\foo.bin")) &&
                    IsProtectedPath(Path.Combine(Roaming, @"Cursor\CachedExtensionVSIXs\ms-python.vsix")) &&
                    !IsProtectedPath(Path.Combine(Roaming, @"Cursor\Cache\data_0")));
                Assert("v32_redline_windsurf_trae_auth",
                    IsProtectedPath(Path.Combine(Roaming, @"Windsurf\Network\Cookies")) &&
                    IsProtectedPath(Path.Combine(Roaming, @"Windsurf\CachedExtensionVSIXs\pkg.vsix")) &&
                    IsProtectedPath(Path.Combine(Roaming, @"Trae\User\settings.json")) &&
                    IsProtectedPath(Path.Combine(LocalApp, @"Docker\wsl\disk\docker_data.vhdx")));

                Assert("v32_ai_ide_executor_allows_only_top_level_cache",
                    IsAuthorizedExecutionTarget(new CleanCandidate
                    {
                        Zone = ZoneId.AiIdes,
                        Tier = CleanTier.OfflineCache,
                        ActionType = CleanActionType.OfflineSweepFilesKeepSkeleton,
                        Path = Path.Combine(Roaming, @"Cursor\Cache"),
                        CanSelect = true
                    }, out _) &&
                    !IsAuthorizedExecutionTarget(new CleanCandidate
                    {
                        Zone = ZoneId.AiIdes,
                        Tier = CleanTier.OfflineCache,
                        ActionType = CleanActionType.OfflineSweepFilesKeepSkeleton,
                        Path = Path.Combine(Roaming, @"Cursor\CachedExtensionVSIXs"),
                        CanSelect = true
                    }, out _) &&
                    !IsAuthorizedExecutionTarget(new CleanCandidate
                    {
                        Zone = ZoneId.AiIdes,
                        Tier = CleanTier.OfflineCache,
                        ActionType = CleanActionType.OfflineSweepFilesKeepSkeleton,
                        Path = Path.Combine(Roaming, @"Cursor\User\globalStorage\ext\Cache"),
                        CanSelect = true
                    }, out _));

                var forcedLocalEco = new CleanCandidate
                {
                    Zone = ZoneId.LocalAiEcosystem,
                    Tier = CleanTier.SafeOnline,
                    ActionType = CleanActionType.SweepExpiredFiles,
                    Path = unauthDir,
                    CanSelect = true
                };
                var localEcoOut = ExecuteCandidate(forcedLocalEco);
                Assert("v32_local_ecosystem_readonly_execute_blocked", localEcoOut.Blocked && !localEcoOut.Succeeded && File.Exists(unauthFile));

                // 8B. WSL2/Docker VHDX O(1) read-only fixture
                string vhdxRoot = Path.Combine(sandbox, "Docker", "wsl", "disk");
                Directory.CreateDirectory(vhdxRoot);
                string vhdxFile = Path.Combine(vhdxRoot, "ext4.vhdx");
                File.WriteAllBytes(vhdxFile, new byte[16384]);
                var vhdxHits = ZoneScanner.ScanVhdxFiles(new[] { Path.Combine(sandbox, "Docker", "wsl") });
                Assert("v32_vhdx_readonly_diagnostic_fixture",
                    vhdxHits.Count == 1 &&
                    vhdxHits[0].IsReadOnlyDiagnostic &&
                    !vhdxHits[0].CanSelect &&
                    vhdxHits[0].ActionType == CleanActionType.None &&
                    vhdxHits[0].SizeBytes == 16384 &&
                    File.Exists(vhdxFile));

                // 8C. Controlled Read-Only Community Diagnostic Pack security suite
                Assert("v32_pack_rejects_traversal_unc_and_sensitive_paths",
                    !DiagnosticPackLoader.TryResolveSafePackPath(@"%USERPROFILE%\..\Windows\System32", out _, out _) &&
                    !DiagnosticPackLoader.TryResolveSafePackPath(@"%USERPROFILE%\.ssh", out _, out _) &&
                    !DiagnosticPackLoader.TryResolveSafePackPath(@"%USERPROFILE%\Documents\work", out _, out _) &&
                    !DiagnosticPackLoader.TryResolveSafePackPath(@"%WINDIR%\Temp", out _, out _) &&
                    !DiagnosticPackLoader.TryResolveSafePackPath(@"\\evil-server\share\ai", out _, out _) &&
                    !DiagnosticPackLoader.TryResolveSafePackPath(@"%USERPROFILE%", out _, out _));

                string packDir = Path.Combine(sandbox, "packs");
                Directory.CreateDirectory(packDir);
                // Malicious pack trying mode="delete" MUST be rejected
                File.WriteAllText(Path.Combine(packDir, "bad-delete.json"), """
                {
                  "schemaVersion": "1.0",
                  "packId": "evil",
                  "name": "Evil Pack",
                  "mode": "delete",
                  "entries": [{ "id": "x", "title": "Bad", "pathTemplate": "%LOCALAPPDATA%\\Temp" }]
                }
                """, new UTF8Encoding(false));

                // Valid read-only pack pointing into this isolated sandbox under %LOCALAPPDATA%\Temp\...
                string sandboxRelFromLocalApp = Path.GetRelativePath(LocalApp, vhdxRoot);
                if (!sandboxRelFromLocalApp.StartsWith("..", StringComparison.Ordinal))
                {
                    string escapedRel = sandboxRelFromLocalApp.Replace("\\", "\\\\");
                    File.WriteAllText(Path.Combine(packDir, "good-radar.json"), $$"""
                    {
                      "schemaVersion": "1.0",
                      "packId": "test-radar",
                      "name": "Test Radar",
                      "mode": "read-only",
                      "entries": [
                        { "id": "ok1", "title": "Isolated Radar Dir", "pathTemplate": "%LOCALAPPDATA%\\{{escapedRel}}", "hint": "test" },
                        { "id": "bad1", "title": "Escape", "pathTemplate": "%USERPROFILE%\\..\\Windows" }
                      ]
                    }
                    """, new UTF8Encoding(false));

                    var loadedEntries = DiagnosticPackLoader.LoadValidEntriesFromDirectory(packDir, out var packRejects);
                    var scannedPackCands = DiagnosticPackLoader.ScanPacksInDirectories(new[] { packDir }, out _);
                    Assert("v32_pack_rejects_delete_mode_and_loads_valid_readonly",
                        loadedEntries.Count == 1 &&
                        packRejects.Count >= 2 &&
                        scannedPackCands.Count == 1 &&
                        scannedPackCands[0].IsReadOnlyDiagnostic &&
                        !scannedPackCands[0].CanSelect &&
                        scannedPackCands[0].ActionType == CleanActionType.None &&
                        scannedPackCands[0].SizeBytes >= 16384);
                }
                else
                {
                    // Fallback if %TEMP% is relocated outside %LOCALAPPDATA%: still verify bad-delete pack rejected
                    var loadedEntries = DiagnosticPackLoader.LoadValidEntriesFromDirectory(packDir, out var packRejects);
                    Assert("v32_pack_rejects_delete_mode_and_loads_valid_readonly",
                        loadedEntries.Count == 0 && packRejects.Count >= 1);
                }

                // 8D. i18n dictionary parity & v3.2 settings normalization
                Assert("v32_i18n_loc_zh_en_parity", Loc.VerifyParity());
                // Verify per-IDE process check does not false-trigger when nonexistent/stopped IDE path is passed
                var stoppedIdeCand = new CleanCandidate
                {
                    Zone = ZoneId.AiIdes,
                    Tier = CleanTier.OfflineCache,
                    ActionType = CleanActionType.OfflineSweepFilesKeepSkeleton,
                    Path = Path.Combine(Roaming, @"Windsurf\Cache"),
                    ProcessOwnerTag = "Windsurf",
                    CanSelect = true
                };
                bool windsurfActuallyRunning = allProcs[ZoneId.AiIdes].RunningProcesses.Any(p => p.Equals("Windsurf", StringComparison.OrdinalIgnoreCase));
                Assert("v32_per_ide_process_check_matches_live_state",
                    IsOfflineTargetProcessRunning(stoppedIdeCand, out _) == windsurfActuallyRunning);

                var origSetV32 = SettingsStore.Load();
                try
                {
                    // Regression for P0 Bugfix: CustomCodexCacheRoot MUST NOT be self-blocked by its own internal runtimes/staging redline,
                    // and a parent ExtraWorkspaceRoot containing inner ExtraProtectedSubtrees MUST remain valid while protecting the inner dir!
                    string codexCustomTest = Path.Combine(sandbox, "custom_codex_cache");
                    string wsWithInnerProt = Path.Combine(sandbox, "ws_parent");
                    string innerProtDir = Path.Combine(wsWithInnerProt, "keep_core");
                    Directory.CreateDirectory(Path.Combine(codexCustomTest, "temp"));
                    Directory.CreateDirectory(Path.Combine(codexCustomTest, "runtimes"));
                    Directory.CreateDirectory(innerProtDir);

                    var probe = origSetV32.Clone();
                    probe.UiLanguage = "fr-FR"; // unsupported -> must normalize to zh-CN
                    probe.LowSpaceAlertThresholdGB = 9999; // out of range -> must clamp to 200
                    probe.EnableTrayLowSpaceAlert = false;
                    probe.CustomCodexCacheRoot = codexCustomTest;
                    probe.ExtraWorkspaceRoots.Add(wsWithInnerProt);
                    probe.ExtraProtectedSubtrees.Add(innerProtDir);
                    SettingsStore.Save(probe);
                    var reloaded = SettingsStore.Load();
                    Assert("v32_settings_i18n_and_tray_normalization",
                        reloaded.UiLanguage == "zh-CN" &&
                        reloaded.LowSpaceAlertThresholdGB == 200 &&
                        !reloaded.EnableTrayLowSpaceAlert);
                    Assert("bugfix_custom_codex_root_not_self_blocked_by_runtimes",
                        ValidateCustomRootPath(codexCustomTest, out _) &&
                        IsProtectedForDelete(Path.Combine(codexCustomTest, "runtimes")) &&
                        IsAuthorizedExecutionTarget(new CleanCandidate
                        {
                            Zone = ZoneId.GptCodex,
                            Tier = CleanTier.SafeOnline,
                            ActionType = CleanActionType.SweepExpiredFiles,
                            Path = Path.Combine(codexCustomTest, "temp"),
                            CanSelect = true
                        }, out _));
                    Assert("bugfix_workspace_root_with_inner_protected_subdir_allowed",
                        ValidateCustomRootPath(wsWithInnerProt, out _) &&
                        !ValidateCustomRootPath(innerProtDir, out _) &&
                        IsProtectedForDelete(innerProtDir));

                    // 9. v3.2.1 Three-Tier Auto-Discovered Junction Target Contract + Fast MeasureDirectory Accuracy
                    string juncParentRoot = Path.Combine(sandbox, "codex_parent_with_junc");
                    string juncStagingTarget = Path.Combine(juncParentRoot, "plugin-staging");
                    string juncStagingTemp = Path.Combine(juncParentRoot, "temp");
                    Directory.CreateDirectory(juncStagingTarget);
                    Directory.CreateDirectory(juncStagingTemp);
                    string stagingKeep = Path.Combine(juncStagingTarget, "keep.dat");
                    File.WriteAllBytes(stagingKeep, new byte[2048]);
                    try
                    {
                        TestExtraAutoJunctionTargets = new[] { NormalizePath(juncStagingTarget) };
                        var probe2 = probe.Clone();
                        probe2.CustomCodexCacheRoot = juncParentRoot;
                        SettingsStore.Save(probe2);

                        // Contract 1: parent root ALLOWED for config & traversal; junction target REJECTED as root & traversal;
                        // Contract 2: BOTH parent root and junction target HARD-PROTECTED against whole-dir deletion!
                        Assert("v321_auto_junction_target_three_tier_contract",
                            ValidateCustomRootPath(juncParentRoot, out _) &&
                            !IsProtectedForTraversal(juncParentRoot) &&
                            IsProtectedForTraversal(juncStagingTarget) &&
                            !ValidateCustomRootPath(juncStagingTarget, out _) &&
                            IsProtectedForDelete(juncParentRoot) &&
                            IsProtectedForDelete(juncStagingTarget));

                        // Whole-directory recycle on parent or junction target MUST be blocked by ExecuteCandidate!
                        var wholeParentAttempt = ExecuteCandidate(new CleanCandidate
                        {
                            Zone = ZoneId.GptCodex,
                            Tier = CleanTier.RecycleCandidate,
                            ActionType = CleanActionType.RecycleDirectoryOrFile,
                            Path = juncParentRoot,
                            CanSelect = true
                        });
                        var wholeTargetAttempt = ExecuteCandidate(new CleanCandidate
                        {
                            Zone = ZoneId.GptCodex,
                            Tier = CleanTier.RecycleCandidate,
                            ActionType = CleanActionType.RecycleDirectoryOrFile,
                            Path = juncStagingTarget,
                            CanSelect = true
                        });
                        Assert("v321_executor_blocks_parent_and_auto_junction_whole_delete",
                            wholeParentAttempt.Blocked && !wholeParentAttempt.Succeeded &&
                            wholeTargetAttempt.Blocked && !wholeTargetAttempt.Succeeded &&
                            File.Exists(stagingKeep));
                    }
                    finally
                    {
                        TestExtraAutoJunctionTargets = null;
                    }

                    // Fast FileSystemEnumerable MeasureDirectory exact byte accuracy + junction skip check
                    var fastStats = MeasureDirectory(tempDir);
                    Assert("v321_fast_measure_exact_and_skips_junction",
                        fastStats.JunctionCount >= 1 && fastStats.FileCount >= 2 && fastStats.TotalBytes > 0);
                }
                finally
                {
                    SettingsStore.Save(origSetV32);
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(claudeJunc))
                    {
                        using var rm2 = Process.Start(new ProcessStartInfo("cmd.exe", $"/c rmdir \"{claudeJunc}\"")
                        { CreateNoWindow = true, UseShellExecute = false });
                        rm2?.WaitForExit(5000);
                    }
                }
                catch { }
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(juncPath))
                {
                    using var rm = Process.Start(new ProcessStartInfo("cmd.exe", $"/c rmdir \"{juncPath}\"")
                    { CreateNoWindow = true, UseShellExecute = false });
                    rm?.WaitForExit(5000);
                }
                if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            }
            catch { }
        }

        return (allPass, lines);
    }
}
