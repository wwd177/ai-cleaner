using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace AICleaner;

public static class ZoneScanner
{
    private static readonly string UserHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string LocalApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly Regex HighConfidenceRegex = new(
        @"^(clean-windows|e2e-isolated-.*|fixtures-.*|native-e2e-.*|stage-qa.*|build-tmp.*|tmp-stage.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ZoneScanReport ScanZone(ZoneId zone, ZoneProcessInfo? preloadedProc = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var proc = preloadedProc ?? SafetyGuard.GetZoneProcesses(zone);
        var def = ZoneCatalog.Get(zone);
        var settings = SettingsStore.Load();

        try
        {
            ct.ThrowIfCancellationRequested();
            var report = zone switch
            {
                ZoneId.Antigravity => ScanAntigravity(settings, proc, ct),
                ZoneId.GptCodex => ScanGptCodex(settings, proc, ct),
                ZoneId.DeepSeekHarness => ScanDeepSeekHarness(settings, proc, ct),
                ZoneId.ClaudeCode => ScanClaudeCode(proc, ct),
                ZoneId.AiToolchains => ScanAiToolchains(settings, proc, ct),
                _ => new ZoneScanReport { Zone = zone, ProcessInfo = proc }
            };
            sw.Stop();
            report.ElapsedMs = sw.ElapsedMilliseconds;
            report.IsDetected = def.DetectInstalled() || report.Candidates.Count > 0;
            report.Status = ct.IsCancellationRequested
                ? ScanStatus.Cancelled
                : (report.AccessDeniedCount > 0 ? ScanStatus.Partial : ScanStatus.Ok);
            return report;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new ZoneScanReport
            {
                Zone = zone,
                ProcessInfo = proc,
                IsDetected = def.DetectInstalled(),
                Status = ScanStatus.Cancelled,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ZoneScanReport
            {
                Zone = zone,
                ProcessInfo = proc,
                IsDetected = def.DetectInstalled(),
                Status = ScanStatus.Error,
                ErrorMessage = ex.Message,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }
    }

    private static ZoneScanReport ScanAntigravity(CleanerSettings settings, ZoneProcessInfo proc, CancellationToken ct)
    {
        var report = new ZoneScanReport { Zone = ZoneId.Antigravity, ProcessInfo = proc };
        var list = report.Candidates;
        int tempDays = settings.TempMinAgeDays;

        // 1. Safe Online: Updater pending installer residual
        string updaterPending = Path.Combine(LocalApp, @"antigravity-updater\pending");
        if (Directory.Exists(updaterPending))
        {
            var st = SafetyGuard.MeasureDirectory(updaterPending, ct);
            report.AccessDeniedCount += st.AccessDeniedCount;
            if (st.TotalBytes > 0)
            {
                bool wl = SettingsStore.IsWhitelisted(updaterPending);
                list.Add(new CleanCandidate
                {
                    Zone = ZoneId.Antigravity,
                    Tier = wl ? CleanTier.ProtectedSkipped : CleanTier.SafeOnline,
                    ActionType = wl ? CleanActionType.None : CleanActionType.RecycleExpiredChildren,
                    Title = "旧版更新安装包残留 (antigravity-updater/pending)",
                    Category = "更新器残留",
                    Path = updaterPending,
                    SizeBytes = st.TotalBytes,
                    FileCount = st.FileCount,
                    JunctionCount = st.JunctionCount,
                    NewestWriteLocal = st.NewestWriteUtc.ToLocalTime(),
                    MinAgeDays = 1,
                    CanSelect = !wl && st.JunctionCount == 0,
                    IsRecommended = !wl,
                    IsUserWhitelisted = wl,
                    Reason = wl ? "已加入用户白名单，永久保留" : "安装完新版本后残留的离线安装包，移入回收站可撤销",
                    ActionHint = "移入 Windows 回收站（保留目录）"
                });
            }
        }

        // 2. Safe Online: .gemini/antigravity/cache, crashes, logs, scratch
        AddOnlineSweepCandidate(report, ZoneId.Antigravity,
            "Antigravity 内部缓存 (.gemini/antigravity/cache)",
            "运行缓存",
            Path.Combine(UserHome, @".gemini\antigravity\cache"),
            minAgeDays: tempDays, pruneEmpty: true,
            reason: $"超 {tempDays} 天未占用的临时缓存文件，在线可安全清理", ct);

        AddOnlineSweepCandidate(report, ZoneId.Antigravity,
            "崩溃转储与日志 (.gemini/antigravity/crashes)",
            "崩溃日志",
            Path.Combine(UserHome, @".gemini\antigravity\crashes"),
            minAgeDays: tempDays, pruneEmpty: true,
            reason: $"超 {tempDays} 天历史崩溃日志，在线可安全清理", ct);

        AddOnlineSweepCandidate(report, ZoneId.Antigravity,
            "Gemini 日志 (.gemini/logs)",
            "运行日志",
            Path.Combine(UserHome, @".gemini\logs"),
            minAgeDays: tempDays, pruneEmpty: true,
            reason: $"超 {tempDays} 天旧日志文件", ct);

        AddOnlineSweepCandidate(report, ZoneId.Antigravity,
            "桌面客户端日志 (Roaming/Antigravity/logs)",
            "运行日志",
            Path.Combine(Roaming, @"Antigravity\logs"),
            minAgeDays: tempDays, pruneEmpty: true,
            reason: $"超 {tempDays} 天旧客户端日志", ct);

        AddOnlineSweepCandidate(report, ZoneId.Antigravity,
            "旧草稿区 (.gemini/antigravity/scratch)",
            "临时草稿",
            Path.Combine(UserHome, @".gemini\antigravity\scratch"),
            minAgeDays: settings.WorkspaceMinAgeDays, pruneEmpty: true,
            reason: $"超 {settings.WorkspaceMinAgeDays} 天未修改的临时草稿文件", ct);

        // 3. Yellow Zone: Old conversation brain artifacts (>WorkspaceMinAgeDays) & custom ExtraBackupDirs
        string brainDir = Path.Combine(UserHome, @".gemini\antigravity\brain");
        if (Directory.Exists(brainDir))
        {
            var nowUtc = DateTime.UtcNow;
            foreach (var sub in new DirectoryInfo(brainDir).EnumerateDirectories())
            {
                ct.ThrowIfCancellationRequested();
                if (SafetyGuard.IsReparsePoint(sub)) continue;
                var st = SafetyGuard.MeasureDirectory(sub.FullName, ct);
                report.AccessDeniedCount += st.AccessDeniedCount;
                if (st.TotalBytes < 10L * 1024 * 1024) continue;

                bool wl = SettingsStore.IsWhitelisted(sub.FullName);
                double ageHours = (nowUtc - st.NewestWriteUtc).TotalHours;
                bool active = ageHours < SafetyGuard.HardActiveFloorHours;
                bool withinGrace = (nowUtc - st.NewestWriteUtc).TotalDays < settings.WorkspaceMinAgeDays;

                if (wl || active || withinGrace)
                {
                    list.Add(new CleanCandidate
                    {
                        Zone = ZoneId.Antigravity,
                        Tier = CleanTier.ProtectedSkipped,
                        ActionType = CleanActionType.None,
                        Title = $"会话产物 brain/{sub.Name[..Math.Min(8, sub.Name.Length)]}...",
                        Category = "会话产物",
                        Path = sub.FullName,
                        SizeBytes = st.TotalBytes,
                        FileCount = st.FileCount,
                        NewestWriteLocal = st.NewestWriteUtc.ToLocalTime(),
                        CanSelect = false,
                        IsUserWhitelisted = wl,
                        Reason = wl ? "已加入用户白名单，永久保留" : (active ? "72 小时内活跃会话，硬保护跳过" : $"不足 {settings.WorkspaceMinAgeDays} 天宽限期，暂保护跳过")
                    });
                }
                else
                {
                    list.Add(new CleanCandidate
                    {
                        Zone = ZoneId.Antigravity,
                        Tier = CleanTier.RecycleCandidate,
                        ActionType = CleanActionType.RecycleDirectoryOrFile,
                        Title = $"历史会话产物 brain/{sub.Name[..Math.Min(8, sub.Name.Length)]}...",
                        Category = "历史会话产物",
                        Path = sub.FullName,
                        SizeBytes = st.TotalBytes,
                        FileCount = st.FileCount,
                        NewestWriteLocal = st.NewestWriteUtc.ToLocalTime(),
                        CanSelect = st.JunctionCount == 0,
                        Reason = $"超 {settings.WorkspaceMinAgeDays} 天未活动的会话附件/日志（不影响主对话索引），移入回收站可恢复",
                        ActionHint = "移入 Windows 回收站"
                    });
                }
            }
        }

        // 3.5 Optional user-configured backup folders (via settings.json ExtraBackupDirs)
        foreach (var backupDir in settings.ExtraBackupDirs)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(backupDir) || !SafetyGuard.ValidateCustomRootPath(backupDir, out _) || !Directory.Exists(backupDir)) continue;
            var st = SafetyGuard.MeasureDirectory(backupDir, ct);
            report.AccessDeniedCount += st.AccessDeniedCount;
            if (st.TotalBytes <= 0) continue;
            bool wl = SettingsStore.IsWhitelisted(backupDir);
            list.Add(new CleanCandidate
            {
                Zone = ZoneId.Antigravity,
                Tier = wl ? CleanTier.ProtectedSkipped : CleanTier.RecycleCandidate,
                ActionType = wl ? CleanActionType.None : CleanActionType.RecycleDirectoryOrFile,
                Title = $"自定义备份目录 ({Path.GetFileName(backupDir.TrimEnd('\\', '/'))})",
                Category = "历史备份",
                Path = backupDir,
                SizeBytes = st.TotalBytes,
                FileCount = st.FileCount,
                NewestWriteLocal = st.NewestWriteUtc.ToLocalTime(),
                EnforceActive72hFloor = false,
                CanSelect = !wl && st.JunctionCount == 0,
                IsUserWhitelisted = wl,
                Reason = wl ? "已加入用户白名单，永久保留" : "用户在 settings.json 配置的自定义备份目录，确认无需留档再勾选移入回收站",
                ActionHint = "移入 Windows 回收站"
            });
        }

        // 4. Offline Cache: Roaming/Antigravity & Partitions & Antigravity IDE (preserves Network/LocalStorage/IndexedDB/Service Worker Database)
        string agRoaming = Path.Combine(Roaming, "Antigravity");
        AddOfflineCacheDirs(report, ZoneId.Antigravity, proc.IsRunning, agRoaming,
            new[] { "Cache", "CachedData", "Code Cache", "GPUCache", "GrShaderCache", "ShaderCache", "DawnGraphiteCache", "DawnWebGPUCache", "blob_storage", @"Service Worker\CacheStorage", @"Crashpad\reports", @"Crashpad\pending" },
            "Antigravity 浏览器缓存", ct);

        string agPartitions = Path.Combine(agRoaming, "Partitions");
        if (Directory.Exists(agPartitions))
        {
            foreach (var part in new DirectoryInfo(agPartitions).EnumerateDirectories())
            {
                ct.ThrowIfCancellationRequested();
                AddOfflineCacheDirs(report, ZoneId.Antigravity, proc.IsRunning, part.FullName,
                    new[] { "Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache", @"Service Worker\CacheStorage" },
                    $"内置浏览器分区 ({part.Name})", ct);
            }
        }

        string agIdeRoaming = Path.Combine(Roaming, "Antigravity IDE");
        if (Directory.Exists(agIdeRoaming))
        {
            AddOfflineCacheDirs(report, ZoneId.Antigravity, proc.IsRunning, agIdeRoaming,
                new[] { "Cache", "CachedData", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache", @"Crashpad\reports", @"Crashpad\pending" },
                "Antigravity IDE 缓存", ct);
        }

        // 5. Red-Zone transparency items
        AddProtectedInfoItem(list, ZoneId.Antigravity, "核心对话记录 (.gemini/antigravity/conversations)",
            Path.Combine(UserHome, @".gemini\antigravity\conversations"), "红区硬保护：包含全部历史对话，永不触碰");
        AddProtectedInfoItem(list, ZoneId.Antigravity, "账号与扩展配置 (.gemini/config)",
            Path.Combine(UserHome, @".gemini\config"), "红区硬保护：包含配置与自定义技能");

        report.Candidates = list
            .OrderBy(c => c.Tier)
            .ThenByDescending(c => c.IsRecommended)
            .ThenByDescending(c => c.SizeBytes)
            .ToList();
        return report;
    }

    private static ZoneScanReport ScanGptCodex(CleanerSettings settings, ZoneProcessInfo proc, CancellationToken ct)
    {
        var report = new ZoneScanReport { Zone = ZoneId.GptCodex, ProcessInfo = proc };
        var list = report.Candidates;
        int tempDays = settings.TempMinAgeDays;

        // 1. Safe Online: CustomCodexCacheRoot (if configured & validated) + workspace scratch
        string customCache = settings.CustomCodexCacheRoot;
        bool validCustomCache = !string.IsNullOrWhiteSpace(customCache) &&
            SafetyGuard.ValidateCustomRootPath(customCache, out _) &&
            Directory.Exists(customCache);
        if (validCustomCache)
        {
            AddOnlineSweepCandidate(report, ZoneId.GptCodex,
                "Codex 临时缓存 (temp)",
                "运行临时文件",
                Path.Combine(customCache, "temp"),
                minAgeDays: tempDays, pruneEmpty: true,
                reason: $"超过 {tempDays} 天且未被 ChatGPT.exe 占用的临时文件，自动跳过所有目录联接与占用锁", ct);

            AddOnlineSweepCandidate(report, ZoneId.GptCodex,
                "Pip 下载缓存 (pip)",
                "包管理缓存",
                Path.Combine(customCache, "pip"),
                minAgeDays: 14, pruneEmpty: true,
                reason: "超过 14 天的 Python 包下载缓存", ct);

            AddOnlineSweepCandidate(report, ZoneId.GptCodex,
                "NPM 下载缓存 (npm)",
                "包管理缓存",
                Path.Combine(customCache, "npm"),
                minAgeDays: 14, pruneEmpty: true,
                reason: "超过 14 天的 Node 包下载缓存", ct);

            AddOnlineSweepCandidate(report, ZoneId.GptCodex,
                "UV 下载缓存 (uv)",
                "包管理缓存",
                Path.Combine(customCache, "uv"),
                minAgeDays: 14, pruneEmpty: true,
                reason: "超过 14 天的 uv Python 包下载缓存", ct);
        }

        // Aggregated safe workspace scratch across validated ExtraWorkspaceRoots (>72h & 0 junctions)
        var validWsRoots = settings.ExtraWorkspaceRoots
            .Where(r => SafetyGuard.ValidateCustomRootPath(r, out _) && Directory.Exists(r))
            .ToArray();
        if (validWsRoots.Length > 0)
        {
            AddWorkspaceScratchBatchCandidate(list, validWsRoots, ct);
            ScanGptWorkspaceCandidates(report, settings, validWsRoots, ct);
        }

        // 3. Offline Cache: CustomCodexCacheRoot browser-cache & desktop-logs (keeps full folder skeleton for MSIX junctions)
        if (validCustomCache)
        {
            AddOfflineSingleDir(report, ZoneId.GptCodex, proc.IsRunning,
                "ChatGPT/Codex 浏览器缓存 (browser-cache)",
                Path.Combine(customCache, "browser-cache"),
                minAgeDays: 0,
                reason: "仅清内部缓存文件，完整保留全部子目录骨架与 MSIX 联接目标", ct);

            AddOfflineSingleDir(report, ZoneId.GptCodex, proc.IsRunning,
                "Codex 桌面旧日志 (desktop-logs)",
                Path.Combine(customCache, "desktop-logs"),
                minAgeDays: tempDays,
                reason: $"超过 {tempDays} 天的历史桌面日志，保留目录本身", ct);

            AddProtectedInfoItem(list, ZoneId.GptCodex, "Codex 运行环境 (runtimes)",
                Path.Combine(customCache, "runtimes"), "红区硬保护：桌面端内置 Node/运行时，严禁删除");
            AddProtectedInfoItem(list, ZoneId.GptCodex, "Codex 联接暂存区 (plugin-staging & command-staging)",
                Path.Combine(customCache, "plugin-staging"), "红区硬保护：被 .codex\\.tmp 与 .codex\\tmp 联接引用，严禁触碰");
        }

        // 4. Red-Zone O(1) SQLite telemetry for %USERPROFILE%\.codex
        AddCodexSqliteTelemetryItem(list);

        report.Candidates = list
            .OrderBy(c => c.Tier)
            .ThenByDescending(c => c.IsRecommended)
            .ThenByDescending(c => c.SizeBytes)
            .ToList();
        return report;
    }

    private static void AddWorkspaceScratchBatchCandidate(List<CleanCandidate> list, string[] roots, CancellationToken ct)
    {
        var targetNames = SafetyGuard.AllowedBatchScratchNames;
        var skipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "node_modules", ".git", ".venv", "venv", "runtime", "runtimes", "_缓存", "_文件中心", "store", "pnpm-home" };
        var cutoffUtc = DateTime.UtcNow.AddHours(-SafetyGuard.HardActiveFloorHours);

        var matchedPaths = new List<string>();
        long totalBytes = 0;
        long totalFiles = 0;
        DateTime newest = DateTime.MinValue;

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;
            var stack = new Stack<(string Dir, int Depth)>();
            stack.Push((root, 0));

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (curr, depth) = stack.Pop();
                if (depth > 5) continue;
                DirectoryInfo di;
                try { di = new DirectoryInfo(curr); } catch { continue; }
                if (SafetyGuard.IsReparsePoint(di)) continue;

                try
                {
                    foreach (var d in di.EnumerateDirectories())
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            if (SafetyGuard.IsReparsePoint(d) || skipNames.Contains(d.Name)) continue;
                            if (targetNames.Contains(d.Name))
                            {
                                if (SafetyGuard.IsProtectedPath(d.FullName)) continue;
                                var st = SafetyGuard.MeasureDirectory(d.FullName, ct);
                                if (st.JunctionCount > 0 || st.NewestWriteUtc > cutoffUtc) continue;
                                matchedPaths.Add(d.FullName);
                                totalBytes += st.TotalBytes;
                                totalFiles += st.FileCount;
                                if (st.NewestWriteUtc > newest) newest = st.NewestWriteUtc;
                                continue;
                            }
                            if (depth + 1 <= 5) stack.Push((d.FullName, depth + 1));
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
        }

        if (matchedPaths.Count > 0 && totalBytes > 0)
        {
            list.Add(new CleanCandidate
            {
                Zone = ZoneId.GptCodex,
                Tier = CleanTier.SafeOnline,
                ActionType = CleanActionType.RecycleBatchDirectories,
                Title = $"工作区约定临时目录汇总 (_tmp / __pycache__ · 共 {matchedPaths.Count} 处)",
                Category = "临时编译缓存",
                Path = roots.FirstOrDefault(Directory.Exists) ?? matchedPaths[0],
                BatchPaths = matchedPaths,
                SizeBytes = totalBytes,
                FileCount = totalFiles,
                NewestWriteLocal = newest == DateTime.MinValue ? DateTime.MinValue : newest.ToLocalTime(),
                CanSelect = true,
                IsRecommended = true,
                Reason = $"超过 72 小时无改动且不含目录联接的 {matchedPaths.Count} 个临时缓存目录，移入回收站可还原",
                ActionHint = $"批量将 {matchedPaths.Count} 个临时目录移入 Windows 回收站"
            });
        }
    }

    private static void ScanGptWorkspaceCandidates(ZoneScanReport report, CleanerSettings settings, string[] validWsRoots, CancellationToken ct)
    {
        var list = report.Candidates;
        var nowUtc = DateTime.UtcNow;
        long minBytes = (long)settings.WorkspaceMinSizeMB * 1024L * 1024L;
        int minAgeDays = settings.WorkspaceMinAgeDays;
        var targets = new List<(string Path, string Category, bool IsFile)>();

        foreach (var wsMain in validWsRoots)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(wsMain) || !Directory.Exists(wsMain)) continue;

            string workDir = Path.Combine(wsMain, "work");
            if (Directory.Exists(workDir))
            {
                foreach (var d in new DirectoryInfo(workDir).EnumerateDirectories())
                    targets.Add((d.FullName, "工作区任务目录 (work)", false));
            }

            string outDir = Path.Combine(wsMain, "outputs");
            if (Directory.Exists(outDir))
            {
                foreach (var d in new DirectoryInfo(outDir).EnumerateDirectories())
                    targets.Add((d.FullName, "交付目录 (outputs)", false));
                foreach (var f in new DirectoryInfo(outDir).EnumerateFiles())
                {
                    if (f.Length >= Math.Min(minBytes, 80L * 1024 * 1024))
                        targets.Add((f.FullName, "大型归档文件 (outputs)", true));
                }
            }

            if (!Directory.Exists(workDir) && !Directory.Exists(outDir))
            {
                foreach (var d in new DirectoryInfo(wsMain).EnumerateDirectories())
                    targets.Add((d.FullName, $"工作区子目录 ({Path.GetFileName(wsMain.TrimEnd('\\', '/'))})", false));
            }
        }

        var bag = new System.Collections.Concurrent.ConcurrentBag<CleanCandidate>();
        int deniedAccum = 0;
        Parallel.ForEach(targets, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, t =>
        {
            ct.ThrowIfCancellationRequested();
            string leaf = Path.GetFileName(t.Path);
            bool isHardProt = SafetyGuard.IsProtectedPath(t.Path, includeUserWhitelist: false);
            if (isHardProt)
            {
                bag.Add(new CleanCandidate
                {
                    Zone = ZoneId.GptCodex,
                    Tier = CleanTier.ProtectedSkipped,
                    ActionType = CleanActionType.None,
                    Title = leaf,
                    Category = t.Category,
                    Path = t.Path,
                    SizeBytes = 0,
                    FileCount = 0,
                    NewestWriteLocal = Directory.Exists(t.Path) ? Directory.GetLastWriteTime(t.Path) : DateTime.MinValue,
                    CanSelect = false,
                    Reason = "红区受保护（源码或工具依赖，免深度遍历）"
                });
                return;
            }

            long sz;
            long files;
            long juncs;
            DateTime newestUtc;

            if (t.IsFile)
            {
                var fi = new FileInfo(t.Path);
                if (!fi.Exists) return;
                sz = fi.Length;
                files = 1;
                juncs = SafetyGuard.IsReparsePoint(fi) ? 1 : 0;
                newestUtc = fi.LastWriteTimeUtc;
            }
            else
            {
                var st = SafetyGuard.MeasureDirectory(t.Path, ct);
                if (st.AccessDeniedCount > 0) Interlocked.Add(ref deniedAccum, st.AccessDeniedCount);
                sz = st.TotalBytes;
                files = st.FileCount;
                juncs = st.JunctionCount;
                newestUtc = st.NewestWriteUtc;
                if (sz < minBytes) return;
            }

            bool isUserWl = SettingsStore.IsWhitelisted(t.Path);
            double ageHours = (nowUtc - newestUtc).TotalHours;
            double ageDays = (nowUtc - newestUtc).TotalDays;

            if (isUserWl)
            {
                bag.Add(new CleanCandidate
                {
                    Zone = ZoneId.GptCodex,
                    Tier = CleanTier.ProtectedSkipped,
                    ActionType = CleanActionType.None,
                    Title = leaf,
                    Category = t.Category,
                    Path = t.Path,
                    SizeBytes = sz,
                    FileCount = files,
                    JunctionCount = juncs,
                    NewestWriteLocal = newestUtc.ToLocalTime(),
                    CanSelect = false,
                    IsUserWhitelisted = true,
                    Reason = "已加入用户白名单，永久保留（右键可移除）"
                });
            }
            else if (juncs > 0)
            {
                bag.Add(new CleanCandidate
                {
                    Zone = ZoneId.GptCodex,
                    Tier = CleanTier.ProtectedSkipped,
                    ActionType = CleanActionType.None,
                    Title = leaf,
                    Category = t.Category,
                    Path = t.Path,
                    SizeBytes = sz,
                    FileCount = files,
                    JunctionCount = juncs,
                    NewestWriteLocal = newestUtc.ToLocalTime(),
                    CanSelect = false,
                    Reason = $"内部含 {juncs} 个目录联接，自动跳过防误删"
                });
            }
            else if (ageHours < SafetyGuard.HardActiveFloorHours || ageDays < minAgeDays)
            {
                bag.Add(new CleanCandidate
                {
                    Zone = ZoneId.GptCodex,
                    Tier = CleanTier.ProtectedSkipped,
                    ActionType = CleanActionType.None,
                    Title = leaf,
                    Category = t.Category,
                    Path = t.Path,
                    SizeBytes = sz,
                    FileCount = files,
                    NewestWriteLocal = newestUtc.ToLocalTime(),
                    CanSelect = false,
                    Reason = ageHours < SafetyGuard.HardActiveFloorHours ? "72 小时内有写入活动，自动保护" : $"不足 {minAgeDays} 天宽限期，暂保护跳过"
                });
            }
            else
            {
                bool recommended = false;
                string reason = $"超过 {minAgeDays} 天未改动、无联接，移入回收站可还原";
                if (HighConfidenceRegex.IsMatch(leaf))
                {
                    recommended = true;
                    reason = "高置信一次性构建/测试暂存，推荐移入回收站";
                }

                bag.Add(new CleanCandidate
                {
                    Zone = ZoneId.GptCodex,
                    Tier = CleanTier.RecycleCandidate,
                    ActionType = CleanActionType.RecycleDirectoryOrFile,
                    Title = leaf,
                    Category = t.Category,
                    Path = t.Path,
                    SizeBytes = sz,
                    FileCount = files,
                    NewestWriteLocal = newestUtc.ToLocalTime(),
                    CanSelect = true,
                    IsRecommended = recommended,
                    Reason = reason,
                    ActionHint = "移入 Windows 回收站（可撤销）"
                });
            }
        });
        report.AccessDeniedCount += deniedAccum;
        list.AddRange(bag);
    }

    private static ZoneScanReport ScanDeepSeekHarness(CleanerSettings settings, ZoneProcessInfo proc, CancellationToken ct)
    {
        var report = new ZoneScanReport { Zone = ZoneId.DeepSeekHarness, ProcessInfo = proc };
        var list = report.Candidates;
        int tempDays = settings.TempMinAgeDays;

        // 1. Safe Online: Updater pending installer residual
        string dshPending = Path.Combine(LocalApp, @"@deepseek-aidsh-desktop-updater\pending");
        if (Directory.Exists(dshPending))
        {
            var st = SafetyGuard.MeasureDirectory(dshPending, ct);
            report.AccessDeniedCount += st.AccessDeniedCount;
            if (st.TotalBytes > 0)
            {
                bool wl = SettingsStore.IsWhitelisted(dshPending);
                list.Add(new CleanCandidate
                {
                    Zone = ZoneId.DeepSeekHarness,
                    Tier = wl ? CleanTier.ProtectedSkipped : CleanTier.SafeOnline,
                    ActionType = wl ? CleanActionType.None : CleanActionType.RecycleExpiredChildren,
                    Title = "旧版更新安装包残留 (dsh-desktop-updater/pending)",
                    Category = "更新器残留",
                    Path = dshPending,
                    SizeBytes = st.TotalBytes,
                    FileCount = st.FileCount,
                    NewestWriteLocal = st.NewestWriteUtc.ToLocalTime(),
                    MinAgeDays = 1,
                    CanSelect = !wl && st.JunctionCount == 0,
                    IsRecommended = !wl,
                    IsUserWhitelisted = wl,
                    Reason = wl ? "已加入用户白名单，永久保留" : "安装完毕后残留的更新安装程序，移入回收站可还原",
                    ActionHint = "移入 Windows 回收站（保留目录）"
                });
            }
        }

        // 2. Safe Online: .dsh/cache & desktop logs
        AddOnlineSweepCandidate(report, ZoneId.DeepSeekHarness,
            "DSH 内部缓存 (.dsh/cache)",
            "运行缓存",
            Path.Combine(UserHome, @".dsh\cache"),
            minAgeDays: Math.Max(tempDays, 7), pruneEmpty: true,
            reason: $"超过 {Math.Max(tempDays, 7)} 天未占用的附件与临时缓存", ct);

        AddOnlineSweepCandidate(report, ZoneId.DeepSeekHarness,
            "DSH 桌面日志 (Roaming/@deepseek-ai/dsh-desktop/logs)",
            "运行日志",
            Path.Combine(Roaming, @"@deepseek-ai\dsh-desktop\logs"),
            minAgeDays: tempDays, pruneEmpty: true,
            reason: $"超过 {tempDays} 天的历史客户端日志", ct);

        // 3. Yellow Zone: profile-backups & >14d old attachments
        string profBackups = Path.Combine(UserHome, @".dsh\profile-backups");
        if (Directory.Exists(profBackups))
        {
            foreach (var sub in new DirectoryInfo(profBackups).EnumerateDirectories())
            {
                ct.ThrowIfCancellationRequested();
                var st = SafetyGuard.MeasureDirectory(sub.FullName, ct);
                report.AccessDeniedCount += st.AccessDeniedCount;
                if (st.TotalBytes < 1024 * 1024) continue;
                bool wl = SettingsStore.IsWhitelisted(sub.FullName);
                bool active = (DateTime.UtcNow - st.NewestWriteUtc).TotalHours < SafetyGuard.HardActiveFloorHours;
                bool blocked = wl || active || st.JunctionCount > 0;
                list.Add(new CleanCandidate
                {
                    Zone = ZoneId.DeepSeekHarness,
                    Tier = blocked ? CleanTier.ProtectedSkipped : CleanTier.RecycleCandidate,
                    ActionType = blocked ? CleanActionType.None : CleanActionType.RecycleDirectoryOrFile,
                    Title = $"历史配置备份 (.dsh/profile-backups/{sub.Name})",
                    Category = "历史备份",
                    Path = sub.FullName,
                    SizeBytes = st.TotalBytes,
                    FileCount = st.FileCount,
                    JunctionCount = st.JunctionCount,
                    NewestWriteLocal = st.NewestWriteUtc.ToLocalTime(),
                    CanSelect = !blocked,
                    IsUserWhitelisted = wl,
                    Reason = wl ? "已加入用户白名单，永久保留" : (st.JunctionCount > 0 ? $"含 {st.JunctionCount} 个联接，跳过" : "历史清理时自动生成的配置备份，确认无需回滚后可移入回收站"),
                    ActionHint = "移入 Windows 回收站"
                });
            }
        }

        string dshAttach = Path.Combine(UserHome, @".dsh\attachments");
        if (Directory.Exists(dshAttach))
        {
            var sw = SafetyGuard.SweepFiles(dshAttach, minAgeDays: 14, doApply: false, pruneEmptySubdirs: false, ct);
            report.AccessDeniedCount += sw.AccessDeniedCount;
            if (sw.EligibleBytes > 0)
            {
                bool wl = SettingsStore.IsWhitelisted(dshAttach);
                list.Add(new CleanCandidate
                {
                    Zone = ZoneId.DeepSeekHarness,
                    Tier = wl ? CleanTier.ProtectedSkipped : CleanTier.RecycleCandidate,
                    ActionType = wl ? CleanActionType.None : CleanActionType.RecycleExpiredChildren,
                    Title = "超 14 天聊天旧附件 (.dsh/attachments)",
                    Category = "历史附件",
                    Path = dshAttach,
                    SizeBytes = sw.EligibleBytes,
                    FileCount = sw.EligibleFiles,
                    LockedCount = sw.SkippedLocked,
                    RecentSkippedCount = sw.SkippedRecent,
                    NewestWriteLocal = sw.NewestEligibleUtc == DateTime.MinValue ? DateTime.MinValue : sw.NewestEligibleUtc.ToLocalTime(),
                    MinAgeDays = 14,
                    CanSelect = !wl,
                    IsUserWhitelisted = wl,
                    Reason = wl ? "已加入用户白名单，永久保留" : $"仅回收超过 14 天的旧附件（保留近 14 天 {sw.SkippedRecent} 个文件），移入回收站可恢复",
                    ActionHint = "超期附件移入 Windows 回收站"
                });
            }
        }

        // 4. Offline Cache: Roaming/@deepseek-ai/dsh-desktop & Partitions
        string dshRoaming = Path.Combine(Roaming, @"@deepseek-ai\dsh-desktop");
        if (Directory.Exists(dshRoaming))
        {
            AddOfflineCacheDirs(report, ZoneId.DeepSeekHarness, proc.IsRunning, dshRoaming,
                new[] { "Cache", "CachedData", "Code Cache", "GPUCache", "GrShaderCache", "ShaderCache", "DawnGraphiteCache", "DawnWebGPUCache", "blob_storage", @"Service Worker\CacheStorage", @"Crashpad\reports", @"Crashpad\pending" },
                "DeepSeek Harness 浏览器缓存", ct);

            string dshPartitions = Path.Combine(dshRoaming, "Partitions");
            if (Directory.Exists(dshPartitions))
            {
                foreach (var part in new DirectoryInfo(dshPartitions).EnumerateDirectories())
                {
                    ct.ThrowIfCancellationRequested();
                    AddOfflineCacheDirs(report, ZoneId.DeepSeekHarness, proc.IsRunning, part.FullName,
                        new[] { "Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache", @"Service Worker\CacheStorage" },
                        $"DSH 浏览器分区 ({part.Name})", ct);
                }
            }
        }

        // 5. Red-Zone transparency items
        AddProtectedInfoItem(list, ZoneId.DeepSeekHarness, "DSH 主配置与插件 (.dsh/profiles · 含联接)",
            Path.Combine(UserHome, @".dsh\profiles"), "红区硬保护：桌面配置、插件与目录联接，严禁触碰");
        AddProtectedInfoItem(list, ZoneId.DeepSeekHarness, "DSH 运行时与会话 (.dsh/dsh-runtimes & sessions)",
            Path.Combine(UserHome, @".dsh\dsh-runtimes"), "红区硬保护：运行环境、凭据、技能联接与会话记录");

        report.Candidates = list
            .OrderBy(c => c.Tier)
            .ThenByDescending(c => c.IsRecommended)
            .ThenByDescending(c => c.SizeBytes)
            .ToList();
        return report;
    }

    // === ZONE 4: Claude Code (v3.0 Strictly Read-Only Diagnostic Mode — ZERO Deletions!) ===
    private static ZoneScanReport ScanClaudeCode(ZoneProcessInfo proc, CancellationToken ct) =>
        ScanClaudeDirectory(Path.Combine(UserHome, ".claude"), proc, ct);

    /// <summary>Testable entry point for Claude Code strict read-only diagnostic scanning (never traverses junctions, submits ZERO deletable candidates).</summary>
    public static ZoneScanReport ScanClaudeDirectory(string claudeRoot, ZoneProcessInfo proc, CancellationToken ct = default)
    {
        var report = new ZoneScanReport { Zone = ZoneId.ClaudeCode, ProcessInfo = proc };
        var list = report.Candidates;
        if (!Directory.Exists(claudeRoot))
            return report;
        if (SafetyGuard.HasReparsePointInChain(claudeRoot))
        {
            list.Add(new CleanCandidate
            {
                Zone = ZoneId.ClaudeCode,
                Tier = CleanTier.ProtectedSkipped,
                ActionType = CleanActionType.None,
                IsReadOnlyDiagnostic = true,
                Title = "Claude 根目录含 NTFS 目录联接（已跳过）",
                Category = "联接保护",
                Path = claudeRoot,
                SizeBytes = 0,
                FileCount = 0,
                JunctionCount = 1,
                CanSelect = false,
                Reason = "检测到根目录或其祖先链包含 NTFS 目录联接/符号链接，已严格跳过遍历",
                ActionHint = "只读保护（不穿越联接）"
            });
            return report;
        }

        var knownMeta = new Dictionary<string, (string Title, string Category, string Reason)>(StringComparer.OrdinalIgnoreCase)
        {
            ["debug"] = ("Claude 调试日志 (.claude/debug)", "只读诊断", "只读体积盘点：v3.0 暂不自动清理，可右键「空间雷达」查看明细"),
            ["file-history"] = ("编辑历史回滚副本 (.claude/file-history)", "回滚保护", "硬保护只读：保存用户文件编辑历史，清理会永久丢失历史回滚版本"),
            ["shell-snapshots"] = ("Shell 环境快照 (.claude/shell-snapshots)", "环境快照", "硬保护只读：生命周期未声明为临时缓存，v3.0 严格只读展示"),
            ["projects"] = ("项目会话记录 (.claude/projects)", "核心会话", "红区硬保护：包含全部 Claude Code 会话历史，严禁清理"),
            ["todos"] = ("任务列表 (.claude/todos)", "会话状态", "只读展示：保存历史任务状态，严禁自动删除"),
            ["plans"] = ("规划文档 (.claude/plans)", "规划记录", "只读展示：保存历史工程计划，严禁自动删除"),
            ["cache"] = ("Claude 内部缓存 (.claude/cache)", "只读诊断", "只读盘点：支持通过「空间雷达」下钻查看")
        };

        try
        {
            foreach (var sub in new DirectoryInfo(claudeRoot).EnumerateDirectories())
            {
                ct.ThrowIfCancellationRequested();
                bool isRp = SafetyGuard.IsReparsePoint(sub);
                if (isRp)
                {
                    list.Add(new CleanCandidate
                    {
                        Zone = ZoneId.ClaudeCode,
                        Tier = CleanTier.ProtectedSkipped,
                        ActionType = CleanActionType.None,
                        IsReadOnlyDiagnostic = true,
                        Title = $"目录联接 (.claude/{sub.Name})",
                        Category = "联接保护",
                        Path = sub.FullName,
                        SizeBytes = 0,
                        FileCount = 0,
                        JunctionCount = 1,
                        NewestWriteLocal = sub.LastWriteTime,
                        CanSelect = false,
                        Reason = "NTFS 目录联接：已自动跳过遍历与任何清理操作",
                        ActionHint = "只读保护（不穿越联接）"
                    });
                    continue;
                }

                var st = SafetyGuard.MeasureDirectory(sub.FullName, ct);
                report.AccessDeniedCount += st.AccessDeniedCount;

                bool known = knownMeta.TryGetValue(sub.Name, out var meta);
                string title = known ? meta.Title : $"其他子目录 (.claude/{sub.Name})";
                string cat = known ? meta.Category : "只读诊断";
                string reason = known ? meta.Reason : "未知或扩展目录：遵循红队安全原则仅做只读展示，严禁猜测清理";
                if (st.JunctionCount > 0) reason += $"（内部已跳过 {st.JunctionCount} 个目录联接）";

                list.Add(new CleanCandidate
                {
                    Zone = ZoneId.ClaudeCode,
                    Tier = CleanTier.ProtectedSkipped,
                    ActionType = CleanActionType.None,
                    IsReadOnlyDiagnostic = true,
                    Title = title,
                    Category = cat,
                    Path = sub.FullName,
                    SizeBytes = st.TotalBytes,
                    FileCount = st.FileCount,
                    JunctionCount = st.JunctionCount,
                    NewestWriteLocal = st.NewestWriteUtc == DateTime.MinValue ? sub.LastWriteTime : st.NewestWriteUtc.ToLocalTime(),
                    CanSelect = false,
                    Reason = reason,
                    ActionHint = "仅限只读诊断与空间雷达透视"
                });
            }
        }
        catch (UnauthorizedAccessException)
        {
            report.AccessDeniedCount++;
        }

        // O(1) core config / credentials transparency item
        string claudeSettings = Path.Combine(claudeRoot, "settings.json");
        if (File.Exists(claudeSettings))
        {
            AddProtectedInfoItem(list, ZoneId.ClaudeCode, "Claude 核心配置与凭据 (.claude/settings.json)",
                claudeRoot, "红区硬保护：包含账号配置、MCP 设置与凭据，严禁触碰");
        }

        report.Candidates = list
            .OrderByDescending(c => c.SizeBytes)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return report;
    }

    // === ZONE 5: AI Toolchains & Local Model Radar ===
    private static ZoneScanReport ScanAiToolchains(CleanerSettings settings, ZoneProcessInfo proc, CancellationToken ct)
    {
        var report = new ZoneScanReport { Zone = ZoneId.AiToolchains, ProcessInfo = proc };
        var list = report.Candidates;
        int tempDays = settings.TempMinAgeDays;

        // 1. Safe Online: verified ephemeral package download caches (STRICTLY skip if NTFS Junction anywhere in ancestor chain!)
        void AddSharedPkgCache(string title, string path, int ageDays, string reason)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(path)) return;
            if (SafetyGuard.HasReparsePointInChain(path))
            {
                list.Add(new CleanCandidate
                {
                    Zone = ZoneId.AiToolchains,
                    Tier = CleanTier.ProtectedSkipped,
                    ActionType = CleanActionType.None,
                    IsReadOnlyDiagnostic = true,
                    Title = $"{title}（NTFS 联接已跳过）",
                    Category = "联接保护",
                    Path = path,
                    SizeBytes = 0,
                    JunctionCount = 1,
                    CanSelect = false,
                    Reason = "检测到该缓存目录或其祖先链包含 NTFS 目录联接（已重定向至其他磁盘），自动跳过防止重复统计与误操作",
                    ActionHint = "只读保护（联接）"
                });
                return;
            }
            AddOnlineSweepCandidate(report, ZoneId.AiToolchains, title, "AI 工具链缓存", path, ageDays, pruneEmpty: true, reason, ct);
        }

        AddSharedPkgCache(@"UV 全局包下载缓存 (AppData\Local\uv\cache)", Path.Combine(LocalApp, @"uv\cache"),
            Math.Max(tempDays, 7), $"超过 {Math.Max(tempDays, 7)} 天的 uv 下载缓存，清理后按需自动重下（不影响已建虚拟环境）");
        AddSharedPkgCache(@"Pip 全局下载缓存 (AppData\Local\pip\Cache)", Path.Combine(LocalApp, @"pip\Cache"),
            14, "超过 14 天的 Python pip 下载缓存");
        AddSharedPkgCache(@"NPM 全局下载缓存 (AppData\Local\npm-cache)", Path.Combine(LocalApp, "npm-cache"),
            14, "超过 14 天的 Node npm 下载缓存");
        AddSharedPkgCache(@"NPM 全局下载缓存 (AppData\Roaming\npm-cache)", Path.Combine(Roaming, "npm-cache"),
            14, "超过 14 天的 Node npm 下载缓存");

        // 2. Strictly Read-Only Diagnostic Radar (NEVER auto-deleted, NEVER counted in reclaimable bytes!)
        void AddReadOnlyRadarDir(string title, string category, string path, string reason)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(path)) return;
            if (SafetyGuard.HasReparsePointInChain(path))
            {
                list.Add(new CleanCandidate
                {
                    Zone = ZoneId.AiToolchains,
                    Tier = CleanTier.ProtectedSkipped,
                    ActionType = CleanActionType.None,
                    IsReadOnlyDiagnostic = true,
                    Title = $"{title}（联接跳过）",
                    Category = category,
                    Path = path,
                    SizeBytes = 0,
                    JunctionCount = 1,
                    CanSelect = false,
                    Reason = "目录或其祖先链含 NTFS 目录联接，仅记录不穿越",
                    ActionHint = "只读雷达"
                });
                return;
            }
            var st = SafetyGuard.MeasureDirectory(path, ct);
            report.AccessDeniedCount += st.AccessDeniedCount;
            if (st.TotalBytes <= 0 && st.FileCount == 0) return;
            list.Add(new CleanCandidate
            {
                Zone = ZoneId.AiToolchains,
                Tier = CleanTier.ProtectedSkipped,
                ActionType = CleanActionType.None,
                IsReadOnlyDiagnostic = true,
                Title = title,
                Category = category,
                Path = path,
                SizeBytes = st.TotalBytes,
                FileCount = st.FileCount,
                JunctionCount = st.JunctionCount,
                NewestWriteLocal = st.NewestWriteUtc == DateTime.MinValue ? Directory.GetLastWriteTime(path) : st.NewestWriteUtc.ToLocalTime(),
                CanSelect = false,
                Reason = reason,
                ActionHint = "仅限只读空间雷达查看，不参与自动清理"
            });
        }

        // pnpm store: hardlinked content-addressable store — NEVER sweep!
        AddReadOnlyRadarDir(@"pnpm 内容寻址库 (AppData\Local\pnpm\store)", "硬链接只读雷达",
            Path.Combine(LocalApp, @"pnpm\store"),
            "只读诊断（不计入可释放量）：pnpm 使用 NTFS 硬链接，直接删文件会破坏离线安装且虚报释放空间；如需瘦身请在终端手动执行 pnpm store prune");

        // Playwright & Puppeteer: pinned browser revisions — NEVER delete by age!
        string pwDir = Path.Combine(LocalApp, "ms-playwright");
        if (Directory.Exists(pwDir))
        {
            if (SafetyGuard.HasReparsePointInChain(pwDir))
            {
                AddReadOnlyRadarDir("Playwright 浏览器运行时", "浏览器只读雷达", pwDir,
                    "只读盘点：严禁按天数自动删除项目依赖的浏览器修订版");
            }
            else
            {
                var revs = new DirectoryInfo(pwDir).EnumerateDirectories().Select(d => d.Name).Take(6).ToList();
                string revHint = revs.Count > 0 ? $"（已安装：{string.Join(", ", revs)}{(revs.Count >= 6 ? "..." : "")}）" : "";
                AddReadOnlyRadarDir($"Playwright 浏览器运行时 {revHint}", "浏览器只读雷达", pwDir,
                    "只读盘点：旧版本浏览器可能是其他项目锁定的离线测试依赖，严禁按天数自动删除；请用「空间雷达」按需核查");
            }
        }
        AddReadOnlyRadarDir(@"Puppeteer 浏览器缓存 (.cache\puppeteer)", "浏览器只读雷达",
            Path.Combine(UserHome, @".cache\puppeteer"),
            "只读盘点：严禁按天数自动删除项目依赖的浏览器修订版");

        // Local Model Caches: Hugging Face / ModelScope / Ollama / LM Studio — NEVER auto-delete weights or resumable .incomplete!
        AddReadOnlyRadarDir(@"Hugging Face 模型缓存 (.cache\huggingface)", "本地模型雷达",
            Path.Combine(UserHome, @".cache\huggingface"),
            "只读盘点：含模型权重、断点续传 .incomplete 与锁文件，严禁自动清理；可点「空间雷达」查看单个模型占用");
        AddReadOnlyRadarDir(@"ModelScope 模型缓存 (.cache\modelscope)", "本地模型雷达",
            Path.Combine(UserHome, @".cache\modelscope"),
            "只读盘点：本地模型权重与下载暂存，严禁自动删除");
        AddReadOnlyRadarDir(@"Ollama 本地模型库 (.ollama\models)", "本地模型雷达",
            Path.Combine(UserHome, @".ollama\models"),
            "只读盘点：如需移除旧模型请使用官方命令 ollama rm <model>");
        AddReadOnlyRadarDir(@"LM Studio 本地模型 (.cache\lm-studio)", "本地模型雷达",
            Path.Combine(UserHome, @".cache\lm-studio"),
            "只读盘点：本地模型权重库，请使用 LM Studio 内置管理卸载");

        report.Candidates = list
            .OrderBy(c => c.Tier)
            .ThenByDescending(c => c.IsRecommended)
            .ThenByDescending(c => c.SizeBytes)
            .ToList();
        return report;
    }

    private static void AddOnlineSweepCandidate(
        ZoneScanReport report, ZoneId zone, string title, string category,
        string path, int minAgeDays, bool pruneEmpty, string reason, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(path)) return;
        var sw = SafetyGuard.SweepFiles(path, minAgeDays, doApply: false, pruneEmptySubdirs: false, ct);
        report.AccessDeniedCount += sw.AccessDeniedCount;
        if (sw.EligibleBytes <= 0 && sw.EligibleFiles <= 0) return;

        bool wl = SettingsStore.IsWhitelisted(path);
        string extra = $"（跳过近期 {sw.SkippedRecent} / 联接 {sw.SkippedJunctions}，清理时自动跳过占用文件）";
        report.Candidates.Add(new CleanCandidate
        {
            Zone = zone,
            Tier = wl ? CleanTier.ProtectedSkipped : CleanTier.SafeOnline,
            ActionType = wl ? CleanActionType.None : CleanActionType.SweepExpiredFiles,
            Title = title,
            Category = category,
            Path = path,
            SizeBytes = sw.EligibleBytes,
            FileCount = sw.EligibleFiles,
            LockedCount = sw.SkippedLocked,
            RecentSkippedCount = sw.SkippedRecent,
            JunctionCount = sw.SkippedJunctions,
            NewestWriteLocal = sw.NewestEligibleUtc == DateTime.MinValue ? DateTime.MinValue : sw.NewestEligibleUtc.ToLocalTime(),
            MinAgeDays = minAgeDays,
            PruneEmptySubdirs = pruneEmpty,
            CanSelect = !wl,
            IsRecommended = !wl,
            IsUserWhitelisted = wl,
            Reason = wl ? "已加入用户白名单，永久保留" : (reason + extra),
            ActionHint = "直接安全清理超期未占用文件"
        });
    }

    private static void AddOfflineSingleDir(
        ZoneScanReport report, ZoneId zone, bool appRunning,
        string title, string path, int minAgeDays, string reason, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(path)) return;
        var sw = SafetyGuard.SweepFiles(path, minAgeDays, doApply: false, pruneEmptySubdirs: false, ct);
        report.AccessDeniedCount += sw.AccessDeniedCount;
        if (sw.EligibleBytes <= 0 && sw.EligibleFiles <= 0) return;

        report.Candidates.Add(new CleanCandidate
        {
            Zone = zone,
            Tier = CleanTier.OfflineCache,
            ActionType = CleanActionType.OfflineSweepFilesKeepSkeleton,
            Title = title,
            Category = "离线浏览器/日志缓存",
            Path = path,
            SizeBytes = sw.EligibleBytes,
            FileCount = sw.EligibleFiles,
            LockedCount = sw.SkippedLocked,
            NewestWriteLocal = sw.NewestEligibleUtc == DateTime.MinValue ? DateTime.MinValue : sw.NewestEligibleUtc.ToLocalTime(),
            MinAgeDays = minAgeDays,
            CanSelect = !appRunning,
            Reason = appRunning ? "当前应用正在运行，已自动加锁；请正常退出应用后刷新再清理" : reason,
            ActionHint = "清理内部缓存文件（保留目录骨架与联接）"
        });
    }

    private static void AddOfflineCacheDirs(
        ZoneScanReport report, ZoneId zone, bool appRunning,
        string baseDir, string[] subNames, string groupTitle, CancellationToken ct)
    {
        if (!Directory.Exists(baseDir)) return;
        foreach (var sub in subNames)
        {
            ct.ThrowIfCancellationRequested();
            string full = Path.Combine(baseDir, sub);
            if (!Directory.Exists(full)) continue;
            var sw = SafetyGuard.SweepFiles(full, minAgeDays: 0, doApply: false, pruneEmptySubdirs: false, ct);
            report.AccessDeniedCount += sw.AccessDeniedCount;
            if (sw.EligibleBytes < 1024 * 1024) continue;

            report.Candidates.Add(new CleanCandidate
            {
                Zone = zone,
                Tier = CleanTier.OfflineCache,
                ActionType = CleanActionType.OfflineSweepFilesKeepSkeleton,
                Title = $"{groupTitle} / {sub}",
                Category = "离线浏览器缓存",
                Path = full,
                SizeBytes = sw.EligibleBytes,
                FileCount = sw.EligibleFiles,
                LockedCount = sw.SkippedLocked,
                NewestWriteLocal = sw.NewestEligibleUtc == DateTime.MinValue ? DateTime.MinValue : sw.NewestEligibleUtc.ToLocalTime(),
                MinAgeDays = 0,
                CanSelect = !appRunning,
                Reason = appRunning ? "应用运行中自动加锁；正常退出该应用后可安全清理（保留登录态）" : "应用已退出，可安全清理渲染/网页缓存（不删 Cookies/登录态）",
                ActionHint = "清理内部缓存文件（保留目录骨架）"
            });
        }
    }

    private static void AddCodexSqliteTelemetryItem(List<CleanCandidate> list)
    {
        string codexDir = Path.Combine(UserHome, ".codex");
        if (!Directory.Exists(codexDir)) return;

        // O(1) FileInfo.Length inspection (<0.2ms, zero recursive crawl!)
        static long SafeFileLen(string p)
        {
            try { return File.Exists(p) ? new FileInfo(p).Length : 0; } catch { return 0; }
        }

        long threadDb = SafeFileLen(Path.Combine(codexDir, "thread_history_1.sqlite"))
                      + SafeFileLen(Path.Combine(codexDir, "thread_history_1.sqlite-wal"));
        long logsDb = SafeFileLen(Path.Combine(codexDir, "logs_2.sqlite"))
                    + SafeFileLen(Path.Combine(codexDir, "logs_2.sqlite-wal"));
        long stateDb = SafeFileLen(Path.Combine(codexDir, "state_5.sqlite"))
                     + SafeFileLen(Path.Combine(codexDir, "state_5.sqlite-wal"));
        long totalDb = threadDb + logsDb + stateDb;

        string telemetry = totalDb > 0
            ? $"红区硬保护：含会话库 thread_history ({CleanCandidate.FormatBytes(threadDb)})、logs_2 ({CleanCandidate.FormatBytes(logsDb)}) 与 7 个联接；依 openai/codex #51361/#34061 建议严禁删库，可右键「透视大文件」只读查看"
            : "红区硬保护：会话 SQLite、插件、技能与 7 个目录联接";

        list.Add(new CleanCandidate
        {
            Zone = ZoneId.GptCodex,
            Tier = CleanTier.ProtectedSkipped,
            ActionType = CleanActionType.None,
            Title = @"Codex 核心目录与 SQLite 数据库 (%USERPROFILE%\.codex)",
            Category = "红区保护项",
            Path = codexDir,
            SizeBytes = totalDb,
            FileCount = 0,
            CanSelect = false,
            Reason = telemetry,
            ActionHint = "仅限只读诊断，绝不自动删除"
        });
    }

    private static void AddProtectedInfoItem(List<CleanCandidate> list, ZoneId zone, string title, string path, string reason)
    {
        if (!Directory.Exists(path)) return;
        // Fast O(1) info entry: avoid scanning 100k+ files inside protected runtimes/profiles/.codex!
        list.Add(new CleanCandidate
        {
            Zone = zone,
            Tier = CleanTier.ProtectedSkipped,
            ActionType = CleanActionType.None,
            Title = title,
            Category = "红区保护项",
            Path = path,
            SizeBytes = 0,
            FileCount = 0,
            NewestWriteLocal = Directory.GetLastWriteTime(path),
            CanSelect = false,
            Reason = reason
        });
    }
}
