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
            Path.GetFullPath(Path.Combine(baseDir, @"..\..\scripts\CodexCleaner\Manage-ScheduledTask.ps1"))
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }

    private static (bool Completed, int ExitCode, string Stdout, string Stderr) RunPowerShellWithDeadline(string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo("powershell.exe", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
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
            string args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"$t = Get-ScheduledTask -TaskName '{TaskName}' -ErrorAction SilentlyContinue; if (-not $t) {{ Write-Output 'MISSING' }} else {{ $i = Get-ScheduledTaskInfo -TaskName '{TaskName}' -ErrorAction SilentlyContinue; Write-Output ($t.State.ToString() + '|' + $i.NextRunTime.ToString('yyyy-MM-dd HH:mm')) }}\"";
            var (completed, exitCode, output, err) = RunPowerShellWithDeadline(args, 7000);
            if (!completed)
                return (false, $"查询超时（{err}）");
            if (exitCode != 0 && string.IsNullOrWhiteSpace(output))
                return (false, string.IsNullOrWhiteSpace(err) ? "计划任务查询返回非零状态" : $"查询失败: {err}");

            if (string.IsNullOrWhiteSpace(output) || output == "MISSING")
                return (false, "未启用（每周自动清理 temp >7 天）");

            var parts = output.Split('|');
            string state = parts[0];
            string nextRun = parts.Length > 1 ? parts[1] : "";
            return (true, $"已启用 ({state}) · 下次运行: {nextRun}");
        }
        catch (Exception ex)
        {
            return (false, $"查询失败: {ex.Message}");
        }
    }

    public static (bool Ok, string Message) SetEnabled(bool enable)
    {
        string? scriptPath = ResolveScriptPath();
        if (string.IsNullOrEmpty(scriptPath))
            return (false, "未找到 Manage-ScheduledTask.ps1（请确保 scripts\\CodexCleaner 目录位于程序同级或上级目录）");

        string action = enable ? "Install" : "Uninstall";
        try
        {
            string args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\" -Action {action}";
            var (completed, exitCode, stdout, stderr) = RunPowerShellWithDeadline(args, 12000);
            if (!completed)
                return (false, stderr);

            return exitCode == 0
                ? (true, enable ? "已启用每周安全清理定时任务（周日 03:30，仅清 temp >7 天）" : "已停用并移除每周定时任务")
                : (false, string.IsNullOrEmpty(stderr) ? stdout : stderr);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
