using System.IO;
using System.Text;
using System.Text.Json;

namespace AICleaner;

public sealed class CleanerSettings
{
    public int TempMinAgeDays { get; set; } = 3;
    public int WorkspaceMinSizeMB { get; set; } = 200;
    public int WorkspaceMinAgeDays { get; set; } = 7;
    public string UiLanguage { get; set; } = "zh-CN";
    public bool EnableTrayLowSpaceAlert { get; set; } = false;
    public int LowSpaceAlertThresholdGB { get; set; } = 30;
    public string CustomCodexCacheRoot { get; set; } = "";
    public List<string> ExtraWorkspaceRoots { get; set; } = new();
    public List<string> ExtraProtectedSubtrees { get; set; } = new();
    public List<string> ExtraBackupDirs { get; set; } = new();
    public List<string> WhitelistedPaths { get; set; } = new();

    public CleanerSettings Clone() => new()
    {
        TempMinAgeDays = TempMinAgeDays,
        WorkspaceMinSizeMB = WorkspaceMinSizeMB,
        WorkspaceMinAgeDays = WorkspaceMinAgeDays,
        UiLanguage = UiLanguage,
        EnableTrayLowSpaceAlert = EnableTrayLowSpaceAlert,
        LowSpaceAlertThresholdGB = LowSpaceAlertThresholdGB,
        CustomCodexCacheRoot = CustomCodexCacheRoot,
        ExtraWorkspaceRoots = new List<string>(ExtraWorkspaceRoots),
        ExtraProtectedSubtrees = new List<string>(ExtraProtectedSubtrees),
        ExtraBackupDirs = new List<string>(ExtraBackupDirs),
        WhitelistedPaths = new List<string>(WhitelistedPaths)
    };
}

public static class SettingsStore
{
    private sealed class SettingsSnapshot
    {
        public required CleanerSettings Data { get; init; }
        public required string[] NormalizedWhitelist { get; init; }
        public required string[] NormalizedExtraProtected { get; init; }
        public required string[] NormalizedExtraWorkspaces { get; init; }
    }

    public static readonly string SettingsPath = Path.Combine(SafetyGuard.AppDataBase, "settings.json");
    private static readonly object Sync = new();
    private static volatile SettingsSnapshot? _snapshot;

    private static SettingsSnapshot GetOrLoadSnapshot()
    {
        var snap = _snapshot;
        if (snap != null) return snap;

        lock (Sync)
        {
            if (_snapshot != null) return _snapshot;
            CleanerSettings loaded = new();
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var text = File.ReadAllText(SettingsPath, Encoding.UTF8);
                    var parsed = JsonSerializer.Deserialize<CleanerSettings>(text);
                    if (parsed != null)
                        loaded = NormalizeSettings(parsed);
                }
            }
            catch { }

            _snapshot = BuildSnapshot(loaded);
            return _snapshot;
        }
    }

    private static List<string> CleanPathList(List<string>? raw) =>
        (raw ?? new List<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static CleanerSettings NormalizeSettings(CleanerSettings input) => new()
    {
        TempMinAgeDays = Math.Clamp(input.TempMinAgeDays, 1, 90),
        WorkspaceMinSizeMB = Math.Clamp(input.WorkspaceMinSizeMB, 20, 5000),
        WorkspaceMinAgeDays = Math.Clamp(input.WorkspaceMinAgeDays, 3, 180),
        UiLanguage = Loc.NormalizeLanguage(input.UiLanguage),
        EnableTrayLowSpaceAlert = input.EnableTrayLowSpaceAlert,
        LowSpaceAlertThresholdGB = Math.Clamp(input.LowSpaceAlertThresholdGB, 5, 200),
        CustomCodexCacheRoot = (input.CustomCodexCacheRoot ?? "").Trim(),
        ExtraWorkspaceRoots = CleanPathList(input.ExtraWorkspaceRoots),
        ExtraProtectedSubtrees = CleanPathList(input.ExtraProtectedSubtrees),
        ExtraBackupDirs = CleanPathList(input.ExtraBackupDirs),
        WhitelistedPaths = CleanPathList(input.WhitelistedPaths)
    };

    private static string[] NormalizeToArray(IEnumerable<string> paths)
    {
        var list = new List<string>();
        foreach (var p in paths)
        {
            if (SafetyGuard.TryNormalizePath(p, out var norm))
                list.Add(norm);
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static SettingsSnapshot BuildSnapshot(CleanerSettings clean) => new()
    {
        Data = clean,
        NormalizedWhitelist = NormalizeToArray(clean.WhitelistedPaths),
        NormalizedExtraProtected = NormalizeToArray(clean.ExtraProtectedSubtrees),
        NormalizedExtraWorkspaces = NormalizeToArray(clean.ExtraWorkspaceRoots)
    };

    private static void AtomicPersistLocked(CleanerSettings clean)
    {
        Directory.CreateDirectory(SafetyGuard.AppDataBase);
        var json = JsonSerializer.Serialize(clean, new JsonSerializerOptions { WriteIndented = true });
        string tmpPath = SettingsPath + $".tmp.{Environment.ProcessId}.{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(tmpPath, json, new UTF8Encoding(false));
            File.Move(tmpPath, SettingsPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
        }
    }

    /// <summary>Returns an isolated clone so callers can never mutate shared state outside lock.</summary>
    public static CleanerSettings Load() => GetOrLoadSnapshot().Data.Clone();

    public static string[] GetNormalizedExtraProtectedSubtrees() => GetOrLoadSnapshot().NormalizedExtraProtected;
    public static string[] GetNormalizedExtraWorkspaceRoots() => GetOrLoadSnapshot().NormalizedExtraWorkspaces;

    public static void Save(CleanerSettings settings)
    {
        lock (Sync)
        {
            var clean = NormalizeSettings(settings);
            AtomicPersistLocked(clean);
            _snapshot = BuildSnapshot(clean);
        }
    }

    public static bool IsWhitelisted(string path, bool protectAncestors = true)
    {
        if (!SafetyGuard.TryNormalizePath(path, out var key)) return false;
        return IsWhitelistedNormalized(key, protectAncestors);
    }

    /// <summary>Lock-free O(W) check against pre-normalized immutable snapshot.</summary>
    public static bool IsWhitelistedNormalized(string normalizedKey, bool protectAncestors = true)
    {
        if (string.IsNullOrEmpty(normalizedKey)) return false;
        var normList = GetOrLoadSnapshot().NormalizedWhitelist;
        for (int i = 0; i < normList.Length; i++)
        {
            var wk = normList[i];
            if (normalizedKey.Equals(wk, StringComparison.OrdinalIgnoreCase) ||
                normalizedKey.StartsWith(wk + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (protectAncestors && wk.StartsWith(normalizedKey + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public static bool AddWhitelist(string path)
    {
        if (!SafetyGuard.TryNormalizePath(path, out var norm)) return false;
        lock (Sync)
        {
            var snap = GetOrLoadSnapshot();
            if (snap.NormalizedWhitelist.Any(wk => wk.Equals(norm, StringComparison.OrdinalIgnoreCase)))
                return false;

            var next = snap.Data.Clone();
            next.WhitelistedPaths.Add(path.Trim());
            var clean = NormalizeSettings(next);
            AtomicPersistLocked(clean);
            _snapshot = BuildSnapshot(clean);
            return true;
        }
    }

    public static bool RemoveWhitelist(string path)
    {
        if (!SafetyGuard.TryNormalizePath(path, out var norm)) return false;
        lock (Sync)
        {
            var snap = GetOrLoadSnapshot();
            var next = snap.Data.Clone();
            int removed = next.WhitelistedPaths.RemoveAll(w =>
                SafetyGuard.TryNormalizePath(w, out var wn) && wn.Equals(norm, StringComparison.OrdinalIgnoreCase));
            if (removed <= 0) return false;

            var clean = NormalizeSettings(next);
            AtomicPersistLocked(clean);
            _snapshot = BuildSnapshot(clean);
            return true;
        }
    }
}
