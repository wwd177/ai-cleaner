using System.Diagnostics;
using System.IO;

namespace AICleaner;

public static class TaskSchedulerHelper
{
    public const string TaskName = "CodexSafeTempCleanup";

    private static string? ResolveScriptPath()
    {
        string baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, @"scripts\CodexCleaner\Manage-ScheduledTask.ps1"),
            Path.Combine(baseDir, @"CodexCleaner\Manage-ScheduledTask.ps1"),
            Path.GetFullPath(Path.Combine(baseDir, @"..\scripts\CodexCleaner\Manage-ScheduledTask.ps1")),
            Path.GetFullPath(Path.Combine(baseDir, @"..\CodexCleaner\Manage-ScheduledTask.ps1")),
            Path.GetFullPath(Path.Combine(baseDir, @"..\..\scripts\CodexCleaner\Manage-ScheduledTask.ps1")),
            Path.GetFullPath(Path.Combine(baseDir, @"..\..\CodexCleaner\Manage-ScheduledTask.ps1"))
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c) && !SafetyGuard.HasReparsePointInChain(c))
                return c;
        }
        return null;
    }

    private static (bool Completed, int ExitCode, string Stdout, string Stderr) RunPowerShellWithDeadline(IEnumerable<string> argList, int timeoutMs)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in argList)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi);
        if (p == null) return (false, -1, "", "无法启动 PowerShell 进程");

        // Drain stdout and stderr concurrently to eliminate 4KB OS pipe buffer deadlocks!
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            var waitTask = p.WaitForExitAsync(cts.Token);
            waitTask.GetAwaiter().GetResult();
            Task.WhenAll(stdoutTask, stderrTask).Wait(1500);
            string outStr = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result.Trim() : "";
            string errStr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result.Trim() : "";
            return (true, p.ExitCode, outStr, errStr);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            return (false, -1, "", $"操作超时（>{timeoutMs / 1000}s），已中止后台查询进程");
        }
    }

    public static (bool Installed, string Summary) GetStatus()
    {
        try
        {
            string psCmd =
                $"$t = Get-ScheduledTask -TaskName '{TaskName}' -ErrorAction SilentlyContinue; " +
                $"if (-not $t) {{ Write-Output 'MISSING' }} else {{ " +
                $"$i = Get-ScheduledTaskInfo -TaskName '{TaskName}' -ErrorAction SilentlyContinue; " +
                $"$next = if ($i -and $i.NextRunTime -and $i.NextRunTime.Year -gt 2000) {{ $i.NextRunTime.ToString('yyyy-MM-dd HH:mm') }} else {{ 'UNSCHEDULED' }}; " +
                $"Write-Output ($t.State.ToString() + '|' + $next) }}";

            var (completed, exitCode, output, err) = RunPowerShellWithDeadline(
                new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", psCmd },
                7000);
            if (!completed)
                return (false, Loc.IsEnglish ? $"Query timeout ({err})" : $"查询超时（{err}）");
            if (exitCode != 0 && string.IsNullOrWhiteSpace(output))
                return (false, string.IsNullOrWhiteSpace(err)
                    ? (Loc.IsEnglish ? "Scheduled task query returned non-zero status" : "计划任务查询返回非零状态")
                    : (Loc.IsEnglish ? $"Query failed: {err}" : $"查询失败: {err}"));

            if (string.IsNullOrWhiteSpace(output) || output == "MISSING")
                return (false, Loc.IsEnglish ? "Disabled (weekly auto-clean temp >7 days)" : "未启用（每周自动清理 temp >7 天）");

            var parts = output.Split('|');
            string state = parts[0];
            string rawNext = parts.Length > 1 ? parts[1] : "UNSCHEDULED";
            string nextRun = rawNext == "UNSCHEDULED" ? (Loc.IsEnglish ? "Not scheduled" : "未计划") : rawNext;
            return (true, Loc.IsEnglish ? $"Enabled ({state}) · Next run: {nextRun}" : $"已启用 ({state}) · 下次运行: {nextRun}");
        }
        catch (Exception ex)
        {
            return (false, Loc.IsEnglish ? $"Query failed: {ex.Message}" : $"查询失败: {ex.Message}");
        }
    }

    public static (bool Ok, string Message) SetEnabled(bool enable)
    {
        string? scriptPath = ResolveScriptPath();
        if (string.IsNullOrEmpty(scriptPath))
            return (false, Loc.IsEnglish
                ? "Manage-ScheduledTask.ps1 not found under scripts\\CodexCleaner or adjacent CodexCleaner directory."
                : "未找到 Manage-ScheduledTask.ps1（请确保 scripts\\CodexCleaner 或同级 CodexCleaner 目录存在）");

        var args = new List<string>
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", scriptPath,
            "-Action", enable ? "Install" : "Uninstall"
        };

        if (enable)
        {
            var customRoot = SettingsStore.Load().CustomCodexCacheRoot;
            if (!string.IsNullOrWhiteSpace(customRoot))
            {
                if (!SafetyGuard.ValidateCustomRootPath(customRoot, out var rootErr))
                    return (false, $"自定义 Codex 缓存根目录校验失败，未注册计划任务：{rootErr}");

                string tempDir = Path.Combine(customRoot, "temp");
                if (tempDir.IndexOfAny(new[] { '"', '\'', '`', '$', '\r', '\n', ';', '|' }) >= 0)
                    return (false, "自定义 temp 路径包含不允许的脚本元字符，已拦截计划任务注册。");
                if (Directory.Exists(tempDir) && SafetyGuard.HasReparsePointInChain(tempDir))
                    return (false, "自定义 Codex temp 目录或其祖先链包含 NTFS 目录联接，已拦截计划任务注册。");

                args.Add("-TempRoot");
                args.Add(tempDir);
            }
        }

        try
        {
            var (completed, exitCode, stdout, stderr) = RunPowerShellWithDeadline(args, 12000);
            if (!completed)
                return (false, stderr);

            return exitCode == 0
                ? (true, enable
                    ? (Loc.IsEnglish ? "Weekly safe cleanup task enabled (Sun 03:30, unlocked temp >7d)." : "已启用每周安全清理定时任务（周日 03:30，仅清 temp >7 天）")
                    : (Loc.IsEnglish ? "Weekly scheduled task disabled and removed." : "已停用并移除每周定时任务"))
                : (false, string.IsNullOrEmpty(stderr) ? stdout : stderr);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
