<#
.SYNOPSIS
    管理 CodexSafeTempCleanup 每周安全清理定时任务（仅清理超期且未占用的临时缓存文件，绝不干扰 ChatGPT 桌面端）
#>
[CmdletBinding()]
param(
    [ValidateSet('Status', 'Install', 'Uninstall', 'TestDryRun', 'Interactive')]
    [string]$Action = 'Status',

    [int]$TempMinAgeDays = 7,
    [string]$TaskName = 'CodexSafeTempCleanup',
    [string]$TempRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$CleanerPath = Join-Path $ScriptDir 'CodexCleaner.ps1'

function Show-TaskStatus {
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if (-not $task) {
        Write-Host "定时任务 [$TaskName]: 未安装" -ForegroundColor Yellow
        return $null
    }
    $info = Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction SilentlyContinue
    Write-Host "定时任务 [$TaskName]: 已安装 | 状态=$($task.State)" -ForegroundColor Green
    foreach ($a in $task.Actions) {
        Write-Host "  命令: $($a.Execute) $($a.Arguments)" -ForegroundColor DarkGray
    }
    if ($info) {
        Write-Host "  下次运行: $($info.NextRunTime) | 上次运行: $($info.LastRunTime) | 上次结果: $($info.LastTaskResult)" -ForegroundColor DarkGray
    }
    return $task
}

function Install-CleanupTask {
    if (-not (Test-Path -LiteralPath $CleanerPath)) {
        throw "未找到主脚本: $CleanerPath"
    }
    $extraTemp = if (-not [string]::IsNullOrWhiteSpace($TempRoot)) { " -TempRoot `"$TempRoot`"" } else { '' }
    $args = "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$CleanerPath`" -Mode ScheduledTemp -TempMinAgeDays $TempMinAgeDays$extraTemp -Apply"
    $actionObj = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $args
    $triggerObj = New-ScheduledTaskTrigger -Weekly -DaysOfWeek Sunday -At '03:30'
    $settingsObj = New-ScheduledTaskSettingsSet `
        -StartWhenAvailable `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 20) `
        -MultipleInstances IgnoreNew
    $principalObj = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited

    Register-ScheduledTask `
        -TaskName $TaskName `
        -Action $actionObj `
        -Trigger $triggerObj `
        -Settings $settingsObj `
        -Principal $principalObj `
        -Description 'Weekly safe cleanup of unlocked temp files (>7 days) for Codex/ChatGPT Desktop. Never touches active sessions or workspaces.' `
        -Force | Out-Null

    Write-Host "已注册/更新每周安全清理定时任务 [$TaskName]（每周日 03:30，错过开机补跑，仅清 temp >${TempMinAgeDays} 天未占用文件）。" -ForegroundColor Green
    Show-TaskStatus | Out-Null
}

function Uninstall-CleanupTask {
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if (-not $task) {
        Write-Host "定时任务 [$TaskName] 本来就不存在。" -ForegroundColor Yellow
        return
    }
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host "已移除定时任务 [$TaskName]（脚本与日志保留）。" -ForegroundColor Green
}

function Invoke-DryRunTest {
    Write-Host '以完全一致的定时任务目标执行只读预览 (Dry-Run)...' -ForegroundColor Cyan
    if (-not [string]::IsNullOrWhiteSpace($TempRoot)) {
        & $CleanerPath -Mode ScheduledTemp -TempMinAgeDays $TempMinAgeDays -TempRoot $TempRoot
    } else {
        & $CleanerPath -Mode ScheduledTemp -TempMinAgeDays $TempMinAgeDays
    }
}

switch ($Action) {
    'Status'     { Show-TaskStatus | Out-Null }
    'Install'    { Install-CleanupTask }
    'Uninstall'  { Uninstall-CleanupTask }
    'TestDryRun' { Invoke-DryRunTest }
    'Interactive' {
        Show-TaskStatus | Out-Null
        Write-Host ''
        Write-Host '请选择操作：' -ForegroundColor Cyan
        Write-Host '  1) 只读预览定时任务将会清理的内容 (Dry-Run)'
        Write-Host '  2) 安装 / 重新启用每周定时任务'
        Write-Host '  3) 卸载每周定时任务'
        Write-Host '  q) 退出'
        $choice = Read-Host '输入选项'
        switch ($choice) {
            '1' { Invoke-DryRunTest }
            '2' { Install-CleanupTask }
            '3' { Uninstall-CleanupTask }
            default { Write-Host '已退出。' }
        }
    }
}
