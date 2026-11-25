using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
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

    // Hardcoded Red-Zone subtrees across all 3 zones (100% portable standard paths + auto-discovered .codex junction targets).
    // Neither these paths, nor their children, nor their parent ancestors may EVER be deleted/recycled.
    private static readonly string[] ProtectedSubtrees = BuildProtectedSubtrees();

    // Container roots that themselves can NEVER be deleted as a whole (only eligible interior items may be swept).
    private static readonly HashSet<string> ProtectedContainerSelf = BuildProtectedContainerSelf();
    private static readonly string[] ProtectedContainerSelfList = ProtectedContainerSelf.ToArray();

    // Shared scratch directory allowlist enforced independently by BOTH scanner and executor for RecycleBatchDirectories.
    public static readonly HashSet<string> AllowedBatchScratchNames = new(StringComparer.OrdinalIgnoreCase)
        { "_tmp", "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".parcel-cache", ".turbo" };

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
        };

        var all = new List<string>(raw);
        // Auto-discover & hard-protect NTFS junction targets under %USERPROFILE%\.codex (e.g. .tmp -> plugin-staging, tmp -> command-staging)
        try
        {
            string codexDir = Path.Combine(UserHome, ".codex");
            if (Directory.Exists(codexDir))
            {
                foreach (var sub in new DirectoryInfo(codexDir).EnumerateDirectories())
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0 && !string.IsNullOrWhiteSpace(sub.LinkTarget))
                    {
                        all.Add(sub.LinkTarget);
                    }
                }
            }
        }
        catch { }

        return all.Select(NormalizePath).Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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

    private static bool IsHardSubtreeMatch(string key, bool protectAncestors)
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

    /// <summary>Verifies a candidate path for RecycleBatchDirectories belongs to configured workspace roots & scratch leaf names.</summary>
    public static bool IsValidWorkspaceBatchMember(string dirPath)
    {
        if (!TryNormalizePath(dirPath, out var key)) return false;
        var allowedRoots = SettingsStore.GetNormalizedExtraWorkspaceRoots();
        bool underAllowedRoot = allowedRoots.Any(r => key.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase));
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

    public static DirStats MeasureDirectory(string rootPath, CancellationToken ct = default)
    {
        var stats = new DirStats();
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(rootPath)) return stats;

        var rootInfo = new DirectoryInfo(rootPath);
        if (IsReparsePoint(rootInfo))
        {
            stats.RootIsJunction = true;
            stats.JunctionCount = 1;
            stats.NewestWriteUtc = rootInfo.LastWriteTimeUtc;
            return stats;
        }
        stats.NewestWriteUtc = rootInfo.LastWriteTimeUtc;

        var stack = new Stack<string>();
        stack.Push(rootPath);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var curr = stack.Pop();
            DirectoryInfo di;
            try { di = new DirectoryInfo(curr); } catch { continue; }
            if (IsReparsePoint(di))
            {
                stats.JunctionCount++;
                continue;
            }

            try
            {
                foreach (var f in di.EnumerateFiles())
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (IsReparsePoint(f)) { stats.JunctionCount++; continue; }
                        stats.TotalBytes += f.Length;
                        stats.FileCount++;
                        if (f.LastWriteTimeUtc > stats.NewestWriteUtc) stats.NewestWriteUtc = f.LastWriteTimeUtc;
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            try
            {
                foreach (var d in di.EnumerateDirectories())
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (IsReparsePoint(d)) { stats.JunctionCount++; continue; }
                        stats.DirCount++;
                        if (d.LastWriteTimeUtc > stats.NewestWriteUtc) stats.NewestWriteUtc = d.LastWriteTimeUtc;
                        stack.Push(d.FullName);
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return stats;
    }

    public static Dictionary<ZoneId, ZoneProcessInfo> GetAllZoneProcesses()
    {
        var result = new Dictionary<ZoneId, ZoneProcessInfo>
        {
            [ZoneId.Antigravity] = new ZoneProcessInfo { Zone = ZoneId.Antigravity },
            [ZoneId.GptCodex] = new ZoneProcessInfo { Zone = ZoneId.GptCodex },
            [ZoneId.DeepSeekHarness] = new ZoneProcessInfo { Zone = ZoneId.DeepSeekHarness }
        };

        // Fast single-pass ProcessName check (zero Win32Exception storms from MainModule on system processes!)
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                string name = proc.ProcessName;
                ZoneId? matched = null;

                if (name.Equals("Antigravity", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Antigravity IDE", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("language_server", StringComparison.OrdinalIgnoreCase))
                {
                    matched = ZoneId.Antigravity;
                }
                else if (name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("codex", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("codex-code-mode-host", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("codex-computer-use-swift", StringComparison.OrdinalIgnoreCase))
                {
                    matched = ZoneId.GptCodex;
                }
                else if (name.Equals("DeepSeek Harness", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("dsh", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("dsh-desktop", StringComparison.OrdinalIgnoreCase))
                {
                    matched = ZoneId.DeepSeekHarness;
                }

                if (matched.HasValue)
                {
                    var info = result[matched.Value];
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

    public static SweepStats SweepFiles(string rootPath, int minAgeDays, bool doApply, bool pruneEmptySubdirs)
    {
        var st = new SweepStats();
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

        while (stack.Count > 0)
        {
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
                foreach (var f in di.EnumerateFiles())
                {
                    try
                    {
                        if (IsReparsePoint(f)) { st.SkippedJunctions++; continue; }
                        if (IsProtectedForDelete(f.FullName)) { st.SkippedProtected++; continue; }
                        if (minAgeDays > 0 && f.LastWriteTimeUtc > cutoffUtc) { st.SkippedRecent++; continue; }

                        // Fast path: during read-only preview (doApply == false), avoid opening tens of thousands
                        // of file handles (triggers Defender hooks and takes seconds). Always check locks on doApply == true!
                        if (doApply && IsFileLocked(f.FullName)) { st.SkippedLocked++; continue; }

                        long len = f.Length;
                        st.EligibleFiles++;
                        st.EligibleBytes += len;
                        if (f.LastWriteTimeUtc > st.NewestEligibleUtc) st.NewestEligibleUtc = f.LastWriteTimeUtc;

                        if (doApply)
                        {
                            try
                            {
                                // Re-verify ancestor chain + live attributes immediately before permanent unlink
                                if (HasReparsePointInChain(f.FullName, rootKey) ||
                                    !TryGetAttributes(f.FullName, out var liveAttrs) ||
                                    (liveAttrs & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                                {
                                    st.SkippedJunctions++;
                                    continue;
                                }
                                if ((liveAttrs & FileAttributes.ReadOnly) != 0)
                                    File.SetAttributes(f.FullName, liveAttrs & ~FileAttributes.ReadOnly);
                                File.Delete(f.FullName);
                                st.DeletedFiles++;
                                st.DeletedBytes += len;
                            }
                            catch (Exception ex)
                            {
                                st.SkippedLocked++;
                                if (st.Errors.Count < 15) st.Errors.Add($"{f.Name}: {ex.Message}");
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                foreach (var sub in di.EnumerateDirectories())
                {
                    try
                    {
                        if (IsReparsePoint(sub)) { st.SkippedJunctions++; continue; }
                        if (IsProtectedForTraversal(sub.FullName)) { st.SkippedProtected++; continue; }
                        stack.Push(sub.FullName);
                    }
                    catch { }
                }
            }
            catch { }
        }

        if (doApply && pruneEmptySubdirs && visitedSubdirs.Count > 0)
        {
            foreach (var d in visitedSubdirs.OrderByDescending(x => x.Length))
            {
                try
                {
                    if (IsProtectedForDelete(d) || HasReparsePointInChain(d, rootKey)) continue;
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

    /// <summary>BitBroom/Mole-style CLI read-only dry-run report across all 3 zones.</summary>
    public static string BuildCliDryRunReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== AI 清理助手 CLI Dry-Run 体检报告 ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===");
        var (rbBytes, rbItems) = GetRecycleBinStats();
        sb.AppendLine($"Windows 回收站当前占用: {(rbBytes >= 0 ? $"{CleanCandidate.FormatBytes(rbBytes)} ({rbItems} 项)" : "不可用")}");
        sb.AppendLine();

        var procs = GetAllZoneProcesses();
        var zones = new[] { ZoneId.Antigravity, ZoneId.GptCodex, ZoneId.DeepSeekHarness };
        var reports = new ConcurrentDictionary<ZoneId, ZoneScanReport>();
        Parallel.ForEach(zones, z => reports[z] = ZoneScanner.ScanZone(z, procs[z]));

        long grandSafe = 0, grandRecycle = 0, grandOffline = 0;
        foreach (var z in zones)
        {
            var r = reports[z];
            grandSafe += r.SafeOnlineBytes;
            grandRecycle += r.RecycleCandidateBytes;
            grandOffline += r.OfflineCacheBytes;
            string procState = r.ProcessInfo.IsRunning ? $"运行中 ({r.ProcessInfo.RunningPids.Count} 进程)" : "已退出";
            sb.AppendLine($"[{z}] 状态: {procState} | 在线安全: {CleanCandidate.FormatBytes(r.SafeOnlineBytes)} | 回收站候选: {CleanCandidate.FormatBytes(r.RecycleCandidateBytes)} | 离线缓存: {CleanCandidate.FormatBytes(r.OfflineCacheBytes)}");
            foreach (var c in r.Candidates.Where(x => x.Tier != CleanTier.ProtectedSkipped).Take(12))
            {
                sb.AppendLine($"  - [{c.TierBadge}] {c.SizeText,10} | {c.Title} | {c.Path}");
            }
            sb.AppendLine();
        }
        sb.AppendLine($"汇总: 在线安全可清 {CleanCandidate.FormatBytes(grandSafe)} | 回收站候选 {CleanCandidate.FormatBytes(grandRecycle)} | 离线缓存 {CleanCandidate.FormatBytes(grandOffline)}");
        sb.AppendLine("DRY_RUN_STATUS=OK (只读体检，未改动任何文件)");
        return sb.ToString();
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
            if (item.Tier == CleanTier.ProtectedSkipped || !item.CanSelect || item.ActionType == CleanActionType.None)
            {
                outcome.Blocked = true;
                outcome.Message = "受保护项，禁止清理";
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
                // Re-verify process state LIVE right before touching any offline cache!
                var procInfo = GetZoneProcesses(item.Zone);
                if (procInfo.IsRunning)
                {
                    outcome.Blocked = true;
                    outcome.Message = $"已拦截：{string.Join("/", procInfo.RunningProcesses)} 仍在运行（{procInfo.RunningPids.Count} 个进程），请先正常退出";
                    return outcome;
                }
                var sw = SweepFiles(item.Path, item.MinAgeDays, doApply: true, pruneEmptySubdirs: false);
                outcome.Succeeded = true;
                outcome.FreedBytes = sw.DeletedBytes;
                outcome.CleanedFiles = sw.DeletedFiles;
                outcome.SkippedLocked = sw.SkippedLocked;
                outcome.SkippedJunctions = sw.SkippedJunctions;
                outcome.Message = $"已清理 {sw.DeletedFiles} 文件 ({CleanCandidate.FormatBytes(sw.DeletedBytes)})，保留目录骨架";
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

        // 1. Redline checks across all 3 zones (100% portable standard paths + custom config)
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
        Assert("redline_service_worker_database", IsProtectedPath(Path.Combine(Roaming, @"Antigravity\Service Worker\Database\000001.ldb")));
        Assert("service_worker_cachestorage_allowed", !IsProtectedPath(Path.Combine(Roaming, @"Antigravity\Service Worker\CacheStorage\demo")));
        Assert("redline_uv_cache_root_self_protected", IsProtectedPath(Path.Combine(LocalApp, @"uv\cache")));
        Assert("redline_uv_cache_child_allowed", !IsProtectedPath(Path.Combine(LocalApp, @"uv\cache\old-wheel.whl")));
        var rbStat = GetRecycleBinStats();
        Assert("recycle_bin_query_non_negative", rbStat.TotalBytes >= 0 && rbStat.ItemCount >= 0);

        // 2. Live process detection & offline safety contract
        var allProcs = GetAllZoneProcesses();
        Assert("process_snapshot_has_all_zones", allProcs.Count == 3);

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
