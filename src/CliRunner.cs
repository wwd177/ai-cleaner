using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AICleaner;

public static class CliRunner
{
    public const string SchemaVersion = "1.0";
    public const string AppVersion = "3.2.0";

    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static bool IsCliInvocation(string[] args)
    {
        if (args.Length == 0) return false;
        string cmd = args[0].Trim().ToLowerInvariant();
        return cmd is "--self-test" or "--dry-run" or "--list-zones" or "--help" or "-h";
    }

    public static int Run(string[] args)
    {
        EnsureConsoleEncoding();
        try
        {
            string cmd = args[0].Trim().ToLowerInvariant();

            return cmd switch
            {
                "--help" or "-h" => RunHelp(),
                "--self-test" => RunSelfTest(args),
                "--list-zones" => RunListZones(args),
                "--dry-run" => RunDryRun(args),
                _ => 2
            };
        }
        catch (Exception ex)
        {
            WriteError($"CLI runtime error: {ex.Message}");
            return 1;
        }
    }

    private static void EnsureConsoleEncoding()
    {
        try
        {
            if (!Console.IsOutputRedirected && !Console.IsErrorRedirected)
                AttachConsole(AttachParentProcess);
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch { }
    }

    private static int RunHelp()
    {
        string text =
            $"AI Cleaner v{AppVersion} (Read-Only CLI & Safety Diagnostics)\n" +
            "Usage:\n" +
            "  AICleaner.exe --list-zones [--json] [--output <path>]\n" +
            "  AICleaner.exe --dry-run [--json] [--zone <slug>] [--output <path>]\n" +
            "  AICleaner.exe --self-test [<output-path>]\n" +
            "  AICleaner.exe --qa-capture <output-dir>\n\n" +
            "Zone slugs: " + string.Join(", ", ZoneCatalog.All.Select(z => z.Slug)) + "\n" +
            "Exit codes: 0=ok, 1=test/runtime failure, 2=invalid arguments, 3=partial scan (access denied/cancelled)";
        WriteOutput(text, outputPath: null, isJson: false);
        return 0;
    }

    private static int RunSelfTest(string[] args)
    {
        string? outPath = ParseOptionalPathArg(args);
        var (passed, lines) = SafetyGuard.RunSelfTests();
        string summary = string.Join(Environment.NewLine, lines) + Environment.NewLine +
                         (passed ? "SELF_TEST_RESULT=PASS" : "SELF_TEST_RESULT=FAIL");
        WriteOutput(summary, outPath, isJson: false, useBomForFile: false);
        return passed ? 0 : 1;
    }

    private static int RunListZones(string[] args)
    {
        if (!TryParseCommonFlags(args, out bool asJson, out string? outPath, out _, out string? parseErr))
        {
            WriteError(parseErr ?? "Invalid arguments");
            return 2;
        }

        var procs = SafetyGuard.GetAllZoneProcesses();
        var zoneDtos = ZoneCatalog.All.Select(def =>
        {
            var p = procs[def.Id];
            return new
            {
                slug = def.Slug,
                id = def.Id.ToString(),
                displayName = def.DisplayName,
                isReadOnlyZone = def.IsReadOnlyZone,
                detected = def.DetectInstalled(),
                running = p.IsRunning,
                runningProcessCount = p.RunningPids.Count,
                runningProcesses = p.RunningProcesses
            };
        }).ToList();

        if (asJson)
        {
            var payload = new
            {
                schemaVersion = SchemaVersion,
                appVersion = AppVersion,
                generatedAt = DateTime.Now.ToString("O"),
                status = "ok",
                zones = zoneDtos
            };
            WriteOutput(JsonSerializer.Serialize(payload, JsonOpts), outPath, isJson: true);
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine($"AI Cleaner v{AppVersion} — Registered Zones ({zoneDtos.Count})");
            foreach (var z in zoneDtos)
            {
                string mode = z.isReadOnlyZone ? "ReadOnly" : "Standard";
                string det = z.detected ? "Detected" : "NotInstalled";
                string run = z.running ? $"Running({z.runningProcessCount})" : "Stopped";
                sb.AppendLine($"  - {z.slug,-18} | {mode,-8} | {det,-12} | {run,-12} | {z.displayName}");
            }
            WriteOutput(sb.ToString(), outPath, isJson: false);
        }
        return 0;
    }

    /// <summary>
    /// Deterministic mixed-status precedence contract for CLI JSON v1.0:
    /// error > cancelled > partial > ok (exit code 0 for ok, 3 for any degraded/aborted status).
    /// </summary>
    public static (string Status, int ExitCode) ComputeOverallStatus(IEnumerable<ZoneScanReport> reports)
    {
        bool anyError = false;
        bool anyCancelled = false;
        bool anyPartial = false;
        foreach (var r in reports)
        {
            if (r.Status == ScanStatus.Error) anyError = true;
            else if (r.Status == ScanStatus.Cancelled) anyCancelled = true;
            else if (r.Status == ScanStatus.Partial) anyPartial = true;
        }
        if (anyError) return ("error", 3);
        if (anyCancelled) return ("cancelled", 3);
        if (anyPartial) return ("partial", 3);
        return ("ok", 0);
    }

    public static (int ExitCode, string Content) ExecuteDryRunInternal(bool asJson, ZoneId? singleZone, CancellationToken ct = default)
    {
        var procs = SafetyGuard.GetAllZoneProcesses();
        var (rbBytes, rbItems) = SafetyGuard.GetRecycleBinStats();
        var targetZones = singleZone.HasValue
            ? new[] { singleZone.Value }
            : ZoneCatalog.All.Select(z => z.Id).ToArray();

        var reports = new ConcurrentDictionary<ZoneId, ZoneScanReport>();
        Parallel.ForEach(targetZones, z =>
        {
            reports[z] = ZoneScanner.ScanZone(z, procs[z], ct);
        });

        var orderedReports = targetZones.Select(z => reports[z]).ToList();
        var (overallStatus, exitCode) = ComputeOverallStatus(orderedReports);

        long grandSafe = orderedReports.Sum(r => r.SafeOnlineBytes);
        long grandRecycle = orderedReports.Sum(r => r.RecycleCandidateBytes);
        long grandOffline = orderedReports.Sum(r => r.OfflineCacheBytes);
        long grandDiag = orderedReports.Sum(r => r.DiagnosticBytes);

        if (asJson)
        {
            var payload = new
            {
                schemaVersion = SchemaVersion,
                appVersion = AppVersion,
                generatedAt = DateTime.Now.ToString("O"),
                readOnly = true,
                status = overallStatus,
                recycleBin = new
                {
                    available = rbBytes >= 0,
                    bytes = Math.Max(0, rbBytes),
                    items = Math.Max(0, rbItems),
                    formatted = rbBytes >= 0 ? CleanCandidate.FormatBytes(rbBytes) : "unavailable"
                },
                summary = new
                {
                    safeOnlineBytes = grandSafe,
                    recycleCandidateBytes = grandRecycle,
                    offlineCacheBytes = grandOffline,
                    diagnosticBytes = grandDiag
                },
                zones = orderedReports.Select(r =>
                {
                    var def = ZoneCatalog.Get(r.Zone);
                    return new
                    {
                        slug = def.Slug,
                        id = r.Zone.ToString(),
                        displayName = def.DisplayName,
                        isReadOnlyZone = def.IsReadOnlyZone,
                        detected = r.IsDetected,
                        status = r.Status.ToString().ToLowerInvariant(),
                        accessDeniedCount = r.AccessDeniedCount,
                        elapsedMs = r.ElapsedMs,
                        errorMessage = r.ErrorMessage,
                        running = r.ProcessInfo.IsRunning,
                        runningProcessCount = r.ProcessInfo.RunningPids.Count,
                        safeOnlineBytes = r.SafeOnlineBytes,
                        recycleCandidateBytes = r.RecycleCandidateBytes,
                        offlineCacheBytes = r.OfflineCacheBytes,
                        diagnosticBytes = r.DiagnosticBytes,
                        candidates = r.Candidates.Select(c => new
                        {
                            title = c.Title,
                            category = c.Category,
                            path = c.Path,
                            tier = c.Tier.ToString(),
                            tierBadge = c.TierBadge,
                            actionType = c.ActionType.ToString(),
                            canSelect = c.CanSelect,
                            isReadOnlyDiagnostic = c.IsReadOnlyDiagnostic,
                            sizeBytes = c.SizeBytes,
                            sizeFormatted = c.SizeText,
                            fileCount = c.FileCount,
                            junctionCount = c.JunctionCount,
                            recentSkippedCount = c.RecentSkippedCount,
                            reason = c.Reason
                        }).ToList()
                    };
                }).ToList()
            };
            return (exitCode, JsonSerializer.Serialize(payload, JsonOpts));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"=== AI 清理助手 v{AppVersion} CLI Dry-Run 体检报告 ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===");
        sb.AppendLine($"扫描状态: {overallStatus.ToUpperInvariant()} | Windows 回收站占用: {(rbBytes >= 0 ? $"{CleanCandidate.FormatBytes(rbBytes)} ({rbItems} 项)" : "不可用")}");
        sb.AppendLine();

        foreach (var r in orderedReports)
        {
            var def = ZoneCatalog.Get(r.Zone);
            string procState = r.ProcessInfo.IsRunning ? $"运行中 ({r.ProcessInfo.RunningPids.Count} 进程)" : "已退出";
            string extra = r.AccessDeniedCount > 0 ? $" | 权限受限={r.AccessDeniedCount}" : "";
            sb.AppendLine($"[{def.Slug}] 状态: {procState} | 扫描: {r.Status} ({r.ElapsedMs} ms{extra}) | 绿区: {CleanCandidate.FormatBytes(r.SafeOnlineBytes)} | 黄区: {CleanCandidate.FormatBytes(r.RecycleCandidateBytes)} | 蓝区: {CleanCandidate.FormatBytes(r.OfflineCacheBytes)} | 只读诊断: {CleanCandidate.FormatBytes(r.DiagnosticBytes)}");
            foreach (var c in r.Candidates.Take(15))
                sb.AppendLine($"  - [{c.TierBadge}] {c.SizeText,10} | {c.Title} | {c.Path}");
            sb.AppendLine();
        }
        sb.AppendLine($"汇总: 绿区在线可清 {CleanCandidate.FormatBytes(grandSafe)} | 黄区回收站候选 {CleanCandidate.FormatBytes(grandRecycle)} | 蓝区离线缓存 {CleanCandidate.FormatBytes(grandOffline)} | 只读雷达 {CleanCandidate.FormatBytes(grandDiag)}");
        sb.AppendLine($"DRY_RUN_STATUS={overallStatus.ToUpperInvariant()} (只读体检，未改动任何文件)");
        return (exitCode, sb.ToString());
    }

    private static int RunDryRun(string[] args)
    {
        if (!TryParseCommonFlags(args, out bool asJson, out string? outPath, out ZoneId? zoneFilter, out string? parseErr))
        {
            WriteError(parseErr ?? "Invalid arguments");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            try { cts.Cancel(); } catch { }
        };
        try { Console.CancelKeyPress += cancelHandler; } catch { }

        try
        {
            var (exitCode, content) = ExecuteDryRunInternal(asJson, zoneFilter, cts.Token);
            WriteOutput(content, outPath, asJson);
            return exitCode;
        }
        finally
        {
            try { Console.CancelKeyPress -= cancelHandler; } catch { }
        }
    }

    private static string? ParseOptionalPathArg(string[] args)
    {
        for (int i = 1; i < args.Length; i++)
        {
            if ((args[i].Equals("--output", StringComparison.OrdinalIgnoreCase) || args[i].Equals("-o", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                return args[i + 1];
            if (!args[i].StartsWith('-'))
                return args[i];
        }
        return null;
    }

    private static bool TryParseCommonFlags(string[] args, out bool asJson, out string? outPath, out ZoneId? zoneFilter, out string? error)
    {
        asJson = false;
        outPath = null;
        zoneFilter = null;
        error = null;

        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a.Equals("--json", StringComparison.OrdinalIgnoreCase))
            {
                asJson = true;
            }
            else if (a.Equals("--output", StringComparison.OrdinalIgnoreCase) || a.Equals("-o", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    error = "Missing value for --output";
                    return false;
                }
                outPath = args[++i];
            }
            else if (a.Equals("--zone", StringComparison.OrdinalIgnoreCase) || a.Equals("-z", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !ZoneCatalog.TryParseSlug(args[i + 1], out var z))
                {
                    string bad = i + 1 < args.Length ? args[i + 1] : "(empty)";
                    error = $"Unknown --zone slug '{bad}'. Valid slugs: {string.Join(", ", ZoneCatalog.All.Select(x => x.Slug))}";
                    return false;
                }
                zoneFilter = z;
                i++;
            }
            else if (!a.StartsWith('-') && outPath == null)
            {
                // Backwards compatible positional output path: AICleaner.exe --dry-run report.txt
                outPath = a;
            }
            else
            {
                error = $"Unknown argument: {a}";
                return false;
            }
        }
        return true;
    }

    private static void WriteOutput(string text, string? outputPath, bool isJson, bool? useBomForFile = null)
    {
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            bool bom = useBomForFile ?? !isJson;
            File.WriteAllText(outputPath, text, new UTF8Encoding(bom));
        }
        else
        {
            Console.Out.WriteLine(text);
            Console.Out.Flush();
        }
    }

    private static void WriteError(string message)
    {
        try
        {
            Console.Error.WriteLine(message);
            Console.Error.Flush();
        }
        catch { }
    }
}
