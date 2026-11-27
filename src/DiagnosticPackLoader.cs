using System.IO;
using System.Text;
using System.Text.Json;

namespace AICleaner;

public sealed class CommunityDiagnosticPack
{
    public string SchemaVersion { get; set; } = "";
    public string PackId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Author { get; set; } = "";
    public List<CommunityDiagnosticEntry> Entries { get; set; } = new();
}

public sealed class CommunityDiagnosticEntry
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Category { get; set; } = "社区只读雷达";
    public string PathTemplate { get; set; } = "";
    public string Hint { get; set; } = "";
}

/// <summary>
/// Controlled Read-Only Community Diagnostic Pack loader (v3.2).
/// Strictly enforces GPT-6.2 Astra red-team rulings:
/// 1) Packs MUST declare mode="read-only" and NEVER produce selectable or deletable candidates.
/// 2) Only 4 allowlisted env vars (%USERPROFILE%, %LOCALAPPDATA%, %APPDATA%, %PROGRAMDATA%) are expanded.
/// 3) Rejects "..", UNC/device paths, drive roots, OS roots, personal sensitive trees (.ssh/.aws/Documents/Desktop),
///    and never traverses NTFS junctions/reparse-point chains.
/// </summary>
public static class DiagnosticPackLoader
{
    public const string SupportedSchemaVersion = "1.0";
    public const int MaxPackFileBytes = 256 * 1024;
    public const int MaxTotalPackBytesRead = 1024 * 1024;
    public const int MaxPackFilesScanned = 40;
    public const int MaxEntriesPerPack = 25;
    public const int MaxTotalEntries = 60;

    private static readonly JsonSerializerOptions PackJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly Dictionary<string, string> AllowedEnvRoots = BuildAllowedEnvRoots();

    private static Dictionary<string, string> BuildAllowedEnvRoots() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["USERPROFILE"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ["LOCALAPPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ["APPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ["PROGRAMDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
    };

    private static readonly string[] PackSensitiveBlocks = BuildPackSensitiveBlocks();

    private static string[] BuildPackSensitiveBlocks()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var raw = new[]
        {
            Path.Combine(home, ".ssh"),
            Path.Combine(home, ".gnupg"),
            Path.Combine(home, ".aws"),
            Path.Combine(home, ".kube"),
            Path.Combine(home, ".azure"),
            Path.Combine(home, ".gcloud"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        return raw.Select(SafetyGuard.NormalizePath).Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string UserPacksDir => Path.Combine(SafetyGuard.AppDataBase, "packs");
    public static string BundledPacksDir => Path.Combine(AppContext.BaseDirectory, "packs");

    public static bool HasAnyInstalledPacks()
    {
        foreach (var dir in new[] { UserPacksDir, BundledPacksDir })
        {
            try
            {
                if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.json").Any())
                    return true;
            }
            catch { }
        }
        return false;
    }

    /// <summary>
    /// Resolves a pack path template safely without calling Environment.ExpandEnvironmentVariables
    /// (prevents arbitrary env var exfiltration/injection) and rejects traversal, UNC, OS dirs, and sensitive personal roots.
    /// </summary>
    public static bool TryResolveSafePackPath(string? template, out string resolvedPath, out string rejectReason)
    {
        resolvedPath = "";
        rejectReason = "";
        if (string.IsNullOrWhiteSpace(template))
        {
            rejectReason = "empty pathTemplate";
            return false;
        }
        string raw = template.Trim();
        if (raw.IndexOf('\0') >= 0 || raw.StartsWith(@"\\", StringComparison.Ordinal) || raw.StartsWith("//", StringComparison.Ordinal))
        {
            rejectReason = "UNC/device/NUL paths are forbidden";
            return false;
        }

        // Split segments BEFORE expansion to reject any literal ".." traversal
        string[] rawSegs = raw.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (rawSegs.Length < 2)
        {
            rejectReason = "pathTemplate must include an allowed root token plus at least one concrete subdirectory";
            return false;
        }
        if (rawSegs.Any(s => s == ".." || s == "."))
        {
            rejectReason = "relative traversal segments (. or ..) are forbidden";
            return false;
        }

        string first = rawSegs[0];
        if (!first.StartsWith('%') || !first.EndsWith('%') || first.Length < 3)
        {
            rejectReason = "pathTemplate must start with an allowlisted token (%USERPROFILE%, %LOCALAPPDATA%, %APPDATA%, or %PROGRAMDATA%)";
            return false;
        }
        string token = first[1..^1];
        if (!AllowedEnvRoots.TryGetValue(token, out var baseDir) || string.IsNullOrWhiteSpace(baseDir))
        {
            rejectReason = $"env token '{first}' is not in allowlist";
            return false;
        }
        if (rawSegs.Skip(1).Any(s => s.Contains('%') || s.Contains(':') || s.Contains('*') || s.Contains('?')))
        {
            rejectReason = "wildcards, colons, or secondary env tokens are forbidden in relative segments";
            return false;
        }

        string candidate = Path.Combine(new[] { baseDir }.Concat(rawSegs.Skip(1)).ToArray());
        if (!SafetyGuard.TryNormalizePath(candidate, out var norm))
        {
            rejectReason = "failed path normalization";
            return false;
        }
        if (!SafetyGuard.TryNormalizePath(baseDir, out var baseNorm) ||
            !norm.StartsWith(baseNorm + "\\", StringComparison.OrdinalIgnoreCase))
        {
            rejectReason = "resolved path escaped allowed env root";
            return false;
        }

        foreach (var blocked in PackSensitiveBlocks)
        {
            if (norm == blocked || norm.StartsWith(blocked + "\\", StringComparison.OrdinalIgnoreCase) ||
                blocked.StartsWith(norm + "\\", StringComparison.OrdinalIgnoreCase))
            {
                rejectReason = "path touches sensitive personal/system tree";
                return false;
            }
        }

        resolvedPath = candidate;
        return true;
    }

    public static bool IsPackSensitiveDescendant(string path)
    {
        if (!SafetyGuard.TryNormalizePath(path, out var norm)) return true;
        foreach (var blocked in PackSensitiveBlocks)
        {
            if (norm == blocked || norm.StartsWith(blocked + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static DirStats MeasurePackDirectorySafely(string rootPath, CancellationToken ct)
    {
        var stats = new DirStats();
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(rootPath)) return stats;
        var rootInfo = new DirectoryInfo(rootPath);
        if (SafetyGuard.IsReparsePoint(rootInfo) || IsPackSensitiveDescendant(rootPath))
        {
            stats.RootIsJunction = true;
            stats.JunctionCount = 1;
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
            if (SafetyGuard.IsReparsePoint(di) || IsPackSensitiveDescendant(di.FullName))
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
                        if (SafetyGuard.IsReparsePoint(f)) { stats.JunctionCount++; continue; }
                        stats.TotalBytes += f.Length;
                        stats.FileCount++;
                        if (f.LastWriteTimeUtc > stats.NewestWriteUtc) stats.NewestWriteUtc = f.LastWriteTimeUtc;
                    }
                    catch (UnauthorizedAccessException) { stats.AccessDeniedCount++; }
                    catch { }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (UnauthorizedAccessException) { stats.AccessDeniedCount++; }
            catch { }

            try
            {
                foreach (var d in di.EnumerateDirectories())
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (SafetyGuard.IsReparsePoint(d) || IsPackSensitiveDescendant(d.FullName)) { stats.JunctionCount++; continue; }
                        stats.DirCount++;
                        stack.Push(d.FullName);
                    }
                    catch (UnauthorizedAccessException) { stats.AccessDeniedCount++; }
                    catch { }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (UnauthorizedAccessException) { stats.AccessDeniedCount++; }
            catch { }
        }
        return stats;
    }

    public static List<(string PackName, CommunityDiagnosticEntry Entry, string ResolvedPath)> LoadValidEntriesFromDirectory(
        string directoryPath, out List<string> rejectedReasons)
    {
        rejectedReasons = new List<string>();
        var valid = new List<(string PackName, CommunityDiagnosticEntry Entry, string ResolvedPath)>();
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            return valid;
        if (SafetyGuard.HasReparsePointInChain(directoryPath))
        {
            rejectedReasons.Add($"Pack directory is or traverses a reparse point: {directoryPath}");
            return valid;
        }

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(directoryPath).EnumerateFiles("*.json").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            rejectedReasons.Add($"Unable to enumerate packs: {ex.Message}");
            return valid;
        }

        int filesScanned = 0;
        long totalBytesRead = 0;
        foreach (var file in files)
        {
            if (valid.Count >= MaxTotalEntries) break;
            if (++filesScanned > MaxPackFilesScanned)
            {
                rejectedReasons.Add($"Pack file scan limit reached ({MaxPackFilesScanned}); remaining files skipped");
                break;
            }
            try
            {
                if (SafetyGuard.IsReparsePoint(file))
                {
                    rejectedReasons.Add($"{file.Name}: symlink pack rejected");
                    continue;
                }
                if (file.Length <= 0 || file.Length > MaxPackFileBytes)
                {
                    rejectedReasons.Add($"{file.Name}: size out of bounds ({file.Length} bytes)");
                    continue;
                }
                if (totalBytesRead + file.Length > MaxTotalPackBytesRead)
                {
                    rejectedReasons.Add($"{file.Name}: total pack read budget exceeded ({MaxTotalPackBytesRead} bytes)");
                    break;
                }
                totalBytesRead += file.Length;
                string rawText = File.ReadAllText(file.FullName, Encoding.UTF8);
                var pack = JsonSerializer.Deserialize<CommunityDiagnosticPack>(rawText, PackJsonOpts);
                if (pack == null)
                {
                    rejectedReasons.Add($"{file.Name}: invalid JSON");
                    continue;
                }
                if (!string.Equals(pack.SchemaVersion, SupportedSchemaVersion, StringComparison.Ordinal))
                {
                    rejectedReasons.Add($"{file.Name}: unsupported schemaVersion '{pack.SchemaVersion}'");
                    continue;
                }
                if (!string.Equals(pack.Mode, "read-only", StringComparison.OrdinalIgnoreCase))
                {
                    rejectedReasons.Add($"{file.Name}: mode MUST be 'read-only' (got '{pack.Mode}')");
                    continue;
                }
                string packName = string.IsNullOrWhiteSpace(pack.Name) ? Path.GetFileNameWithoutExtension(file.Name) : pack.Name.Trim();
                int perPack = 0;
                foreach (var entry in pack.Entries ?? new List<CommunityDiagnosticEntry>())
                {
                    if (perPack >= MaxEntriesPerPack || valid.Count >= MaxTotalEntries) break;
                    if (string.IsNullOrWhiteSpace(entry.Title))
                    {
                        rejectedReasons.Add($"{file.Name}: entry missing title");
                        continue;
                    }
                    if (!TryResolveSafePackPath(entry.PathTemplate, out var resolved, out var why))
                    {
                        rejectedReasons.Add($"{file.Name}/{entry.Title}: {why}");
                        continue;
                    }
                    string normKey = SafetyGuard.NormalizePath(resolved);
                    if (seenPaths.Add(normKey))
                    {
                        valid.Add((packName, entry, resolved));
                        perPack++;
                    }
                }
            }
            catch (Exception ex)
            {
                rejectedReasons.Add($"{file.Name}: parse error ({ex.Message})");
            }
        }
        return valid;
    }

    public static List<CleanCandidate> ScanPacksInDirectories(IEnumerable<string> packDirs, out int accessDeniedAccum, CancellationToken ct = default)
    {
        accessDeniedAccum = 0;
        var results = new List<CleanCandidate>();
        var seenResolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in packDirs)
        {
            ct.ThrowIfCancellationRequested();
            var entries = LoadValidEntriesFromDirectory(dir, out _);
            foreach (var (packName, entry, resolved) in entries)
            {
                ct.ThrowIfCancellationRequested();
                string norm = SafetyGuard.NormalizePath(resolved);
                if (!seenResolved.Add(norm)) continue;
                if (!Directory.Exists(resolved) && !File.Exists(resolved)) continue;

                if (SafetyGuard.HasReparsePointInChain(resolved))
                {
                    results.Add(new CleanCandidate
                    {
                        Zone = ZoneId.LocalAiEcosystem,
                        Tier = CleanTier.ProtectedSkipped,
                        ActionType = CleanActionType.None,
                        IsReadOnlyDiagnostic = true,
                        Title = $"{entry.Title}（联接跳过）",
                        Category = $"社区雷达 · {packName}",
                        Path = resolved,
                        SizeBytes = 0,
                        JunctionCount = 1,
                        CanSelect = false,
                        Reason = "社区只读诊断包目标含 NTFS 目录联接，已安全跳过不穿越",
                        ActionHint = "仅限只读诊断（社区规则包）"
                    });
                    continue;
                }

                long bytes = 0;
                long files = 0;
                long juncs = 0;
                DateTime newest = DateTime.MinValue;

                if (File.Exists(resolved))
                {
                    try
                    {
                        var fi = new FileInfo(resolved);
                        bytes = fi.Length;
                        files = 1;
                        newest = fi.LastWriteTime;
                    }
                    catch { continue; }
                }
                else
                {
                    var st = MeasurePackDirectorySafely(resolved, ct);
                    accessDeniedAccum += st.AccessDeniedCount;
                    if (st.TotalBytes <= 0 && st.FileCount == 0) continue;
                    bytes = st.TotalBytes;
                    files = st.FileCount;
                    juncs = st.JunctionCount;
                    newest = st.NewestWriteUtc == DateTime.MinValue ? Directory.GetLastWriteTime(resolved) : st.NewestWriteUtc.ToLocalTime();
                }

                string hint = string.IsNullOrWhiteSpace(entry.Hint)
                    ? $"社区只读规则包 [{packName}] 盘点结果，严禁自动删除；请用「空间雷达」按需查看"
                    : $"[{packName}] {entry.Hint}";

                results.Add(new CleanCandidate
                {
                    Zone = ZoneId.LocalAiEcosystem,
                    Tier = CleanTier.ProtectedSkipped,
                    ActionType = CleanActionType.None,
                    IsReadOnlyDiagnostic = true,
                    Title = entry.Title.Trim(),
                    Category = string.IsNullOrWhiteSpace(entry.Category) ? $"社区雷达 · {packName}" : entry.Category.Trim(),
                    Path = resolved,
                    SizeBytes = bytes,
                    FileCount = files,
                    JunctionCount = juncs,
                    NewestWriteLocal = newest,
                    CanSelect = false,
                    Reason = hint,
                    ActionHint = "只读社区规则包（绝不执行删除）"
                });
            }
        }
        return results;
    }

    public static List<CleanCandidate> ScanInstalledPacks(out int accessDeniedAccum, CancellationToken ct = default) =>
        ScanPacksInDirectories(new[] { UserPacksDir, BundledPacksDir }, out accessDeniedAccum, ct);
}
