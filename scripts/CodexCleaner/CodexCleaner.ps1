<#
.SYNOPSIS
    Codex / ChatGPT 桌面端安全缓存与工作区清理工具 (CodexCleaner)

.DESCRIPTION
    严格遵循红/黄/绿三区隔离原则：
    - 红区硬编码保护：绝不触碰 ChatGPT.exe / codex 核心数据库、会话、配置、插件、运行环境与任何 NTFS 目录联接 (Junction)。
    - 绿区 (DailySafe / ScheduledTemp)：ChatGPT 桌面端运行中也可安全清理超期且未被占用的临时缓存。
    - 黄区 (WorkspaceAudit)：扫描超期大体积工作区残留，输出可读报告，经人工勾选后移入 Windows 回收站（可撤销）。
    - 离线档 (OfflineBrowserCache)：仅在确认 ChatGPT / codex 进程全部退出后，清理浏览器缓存与旧日志的内部文件（保留目录骨架与全部联接）。

.NOTES
    默认全部为只读预览 (Dry-Run)；必须显式传入 -Apply 或在交互模式下输入 y 才会执行清理。
#>
[CmdletBinding()]
param(
    [ValidateSet('DailySafe', 'ScheduledTemp', 'WorkspaceAudit', 'OfflineBrowserCache')]
    [string]$Mode = 'DailySafe',

    [switch]$Apply,
    [switch]$Interactive,

    [int]$TempMinAgeDays = -1,
    [int]$PackageCacheMinAgeDays = 14,
    [int]$WorkspaceMinAgeDays = 7,
    [int]$MinSizeMB = 200,
    [int]$DesktopLogsMinAgeDays = 3,

    [string]$ConfigPath = '',
    [string]$TempRoot = '',
    [string[]]$PackageCacheRoots = @(),
    [string[]]$WorkspaceRoots = @(),
    [string]$BrowserCacheRoot = '',
    [string]$DesktopLogsRoot = '',
    [string]$LogDir = '',
    [string]$ReportDir = '',

    [string[]]$RecyclePaths = @(),
    [switch]$SkipWorkspaceScratchInDaily,
    [int]$MaxLogFiles = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Ensure UTF-8 console output without throwing when redirected
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ConfigPath)) { $ConfigPath = Join-Path $ScriptDir 'config.json' }
$script:ExtraProtectedFromConfig = @()
if (Test-Path -LiteralPath $ConfigPath -PathType Leaf) {
    try {
        $cfg = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ([string]::IsNullOrWhiteSpace($TempRoot) -and $cfg.TempRoot) { $TempRoot = [string]$cfg.TempRoot }
        if ($PackageCacheRoots.Count -eq 0 -and $cfg.PackageCacheRoots) { $PackageCacheRoots = @($cfg.PackageCacheRoots) }
        if ($WorkspaceRoots.Count -eq 0 -and $cfg.WorkspaceRoots) { $WorkspaceRoots = @($cfg.WorkspaceRoots) }
        if ([string]::IsNullOrWhiteSpace($BrowserCacheRoot) -and $cfg.BrowserCacheRoot) { $BrowserCacheRoot = [string]$cfg.BrowserCacheRoot }
        if ([string]::IsNullOrWhiteSpace($DesktopLogsRoot) -and $cfg.DesktopLogsRoot) { $DesktopLogsRoot = [string]$cfg.DesktopLogsRoot }
        if ($cfg.ExtraProtectedSubtrees) { $script:ExtraProtectedFromConfig = @($cfg.ExtraProtectedSubtrees) }
    } catch {}
}

# Portable defaults (100% standard Windows paths if not configured via params or config.json)
if ([string]::IsNullOrWhiteSpace($TempRoot)) { $TempRoot = Join-Path $env:LOCALAPPDATA 'CodexCleaner\temp' }
if ($PackageCacheRoots.Count -eq 0) {
    $PackageCacheRoots = @(
        (Join-Path $env:LOCALAPPDATA 'uv\cache'),
        (Join-Path $env:LOCALAPPDATA 'pip\Cache')
    )
}
if ([string]::IsNullOrWhiteSpace($LogDir)) { $LogDir = Join-Path $ScriptDir 'logs' }
if ([string]::IsNullOrWhiteSpace($ReportDir)) { $ReportDir = Join-Path $ScriptDir 'reports' }

Add-Type -AssemblyName Microsoft.VisualBasic

if (-not ('CodexCleanerNative.Scanner' -as [type])) {
    $csharp = @'
using System;
using System.IO;
using System.Collections.Generic;

namespace CodexCleanerNative {
    public class DirStats {
        public long TotalBytes;
        public long FileCount;
        public long DirCount;
        public long JunctionCount;
        public DateTime NewestWriteUtc;
        public bool RootIsJunction;
    }

    public static class Scanner {
        public static bool IsReparsePoint(FileSystemInfo info) {
            return (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }

        public static DirStats MeasureDirectory(string rootPath) {
            var stats = new DirStats();
            stats.NewestWriteUtc = DateTime.MinValue;
            if (!Directory.Exists(rootPath)) return stats;

            var rootInfo = new DirectoryInfo(rootPath);
            if (IsReparsePoint(rootInfo)) {
                stats.RootIsJunction = true;
                stats.JunctionCount = 1;
                stats.NewestWriteUtc = rootInfo.LastWriteTimeUtc;
                return stats;
            }
            stats.NewestWriteUtc = rootInfo.LastWriteTimeUtc;

            var stack = new Stack<string>();
            stack.Push(rootPath);

            while (stack.Count > 0) {
                string curr = stack.Pop();
                DirectoryInfo di;
                try { di = new DirectoryInfo(curr); } catch { continue; }
                if (IsReparsePoint(di)) {
                    stats.JunctionCount++;
                    continue;
                }

                try {
                    foreach (var f in di.EnumerateFiles()) {
                        try {
                            if (IsReparsePoint(f)) {
                                stats.JunctionCount++;
                                continue;
                            }
                            stats.TotalBytes += f.Length;
                            stats.FileCount++;
                            if (f.LastWriteTimeUtc > stats.NewestWriteUtc) {
                                stats.NewestWriteUtc = f.LastWriteTimeUtc;
                            }
                        } catch {}
                    }
                } catch {}

                try {
                    foreach (var d in di.EnumerateDirectories()) {
                        try {
                            if (IsReparsePoint(d)) {
                                stats.JunctionCount++;
                                continue;
                            }
                            stats.DirCount++;
                            if (d.LastWriteTimeUtc > stats.NewestWriteUtc) {
                                stats.NewestWriteUtc = d.LastWriteTimeUtc;
                            }
                            stack.Push(d.FullName);
                        } catch {}
                    }
                } catch {}
            }
            return stats;
        }

        public static List<string> FindNamedDirectories(string rootPath, HashSet<string> targetNames, HashSet<string> skipNames, int maxDepth) {
            var found = new List<string>();
            if (!Directory.Exists(rootPath)) return found;

            var stack = new Stack<Tuple<string, int>>();
            stack.Push(Tuple.Create(rootPath, 0));

            while (stack.Count > 0) {
                var item = stack.Pop();
                string curr = item.Item1;
                int depth = item.Item2;
                if (depth > maxDepth) continue;

                DirectoryInfo di;
                try { di = new DirectoryInfo(curr); } catch { continue; }
                if (IsReparsePoint(di)) continue;

                try {
                    foreach (var d in di.EnumerateDirectories()) {
                        try {
                            if (IsReparsePoint(d)) continue;
                            string name = d.Name;
                            if (skipNames.Contains(name)) continue;
                            if (targetNames.Contains(name)) {
                                found.Add(d.FullName);
                                continue;
                            }
                            if (depth + 1 <= maxDepth) {
                                stack.Push(Tuple.Create(d.FullName, depth + 1));
                            }
                        } catch {}
                    }
                } catch {}
            }
            return found;
        }
    }
}
'@
    Add-Type -TypeDefinition $csharp -Language CSharp
}

# Resolve effective TempMinAgeDays
if ($TempMinAgeDays -lt 0) {
    $TempMinAgeDays = if ($Mode -eq 'ScheduledTemp') { 7 } else { 3 }
}
# Hard floor: workspace items younger than 72 hours (3 days) are never eligible
$HardActiveFloorHours = 72
if ($WorkspaceMinAgeDays -lt 3) {
    $WorkspaceMinAgeDays = 3
}

function Normalize-PathKey([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
    if ($Path.IndexOf([char]0) -ge 0) { return '' }
    try {
        $full = [System.IO.Path]::GetFullPath($Path.Trim())
        $cleaned = $full.TrimEnd('\', '/').ToLowerInvariant()
        if ($cleaned.Length -le 3) { return '' }
        return $cleaned
    } catch {
        # Fail-closed: unnormalizable path returns empty string, which Test-IsProtectedPath treats as protected
        return ''
    }
}

# Hardcoded Red-Zone Protected Subtrees (100% portable + auto-discovered .codex junction targets + config.json extras)
$codexHome = Join-Path $env:USERPROFILE '.codex'
$autoJunctionTargets = @()
if (Test-Path -LiteralPath $codexHome -PathType Container) {
    Get-ChildItem -LiteralPath $codexHome -Directory -Force -ErrorAction SilentlyContinue | ForEach-Object {
        if (($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -and $_.Target) {
            $autoJunctionTargets += @($_.Target)
        }
    }
}

$customSiblingProtected = @()
if (-not [string]::IsNullOrWhiteSpace($TempRoot)) {
    $tempParent = Split-Path -Parent $TempRoot
    if (-not [string]::IsNullOrWhiteSpace($tempParent)) {
        foreach ($sub in @('runtimes', 'native-runtimes', 'app-metadata', 'plugin-staging', 'command-staging')) {
            $customSiblingProtected += (Join-Path $tempParent $sub)
        }
    }
}

$script:ProtectedSubtrees = @(
    $codexHome,
    (Join-Path $env:LOCALAPPDATA 'OpenAI'),
    (Join-Path $env:USERPROFILE '.cache\codex-runtimes'),
    (Join-Path $env:USERPROFILE '.claude'),
    (Join-Path $env:USERPROFILE '.claude.json'),
    (Join-Path $env:USERPROFILE '.ssh'),
    (Join-Path $env:USERPROFILE '.gnupg'),
    (Join-Path $env:USERPROFILE '.aws'),
    (Join-Path $env:USERPROFILE '.kube'),
    [Environment]::GetFolderPath('MyDocuments'),
    [Environment]::GetFolderPath('DesktopDirectory')
) + $autoJunctionTargets + $customSiblingProtected + $script:ExtraProtectedFromConfig |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    ForEach-Object { Normalize-PathKey $_ } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -Unique

# Container roots themselves can NEVER be deleted as a whole (only eligible interior items)
$script:ProtectedContainerSelf = @(
    $env:USERPROFILE,
    $env:LOCALAPPDATA,
    $env:APPDATA,
    $TempRoot,
    $BrowserCacheRoot,
    $DesktopLogsRoot
) + $PackageCacheRoots + $WorkspaceRoots |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    ForEach-Object { Normalize-PathKey $_ } |
    Select-Object -Unique

$script:MsixPackagesPrefix = Normalize-PathKey (Join-Path $env:LOCALAPPDATA 'Packages\OpenAI.Codex')

function Test-IsProtectedPath {
    param(
        [Parameter(Mandatory)][string]$CandidatePath,
        [switch]$AllowDescendantOfContainer
    )
    $key = Normalize-PathKey $CandidatePath
    if ([string]::IsNullOrWhiteSpace($key)) { return $true }

    # Block WindowsApp / MSIX OpenAI packages by dynamic prefix
    if (-not [string]::IsNullOrWhiteSpace($script:MsixPackagesPrefix) -and $key.StartsWith($script:MsixPackagesPrefix)) { return $true }
    if ($key -like 'c:\program files\windowsapps\openai.codex*') { return $true }

    # Block any drive root like c: or d:
    if ($key.Length -le 3) { return $true }

    # Never allow deleting container roots themselves or their parent ancestors
    foreach ($c in $script:ProtectedContainerSelf) {
        if ($key -eq $c -or $c.StartsWith("$key\")) { return $true }
    }

    foreach ($p in $script:ProtectedSubtrees) {
        # Candidate IS protected or INSIDE protected subtree
        if ($key -eq $p -or $key.StartsWith("$p\")) { return $true }
        # Candidate is an ANCESTOR of a protected subtree (deleting it would destroy protected subtree)
        if ($p.StartsWith("$key\")) { return $true }
    }

    return $false
}

function Test-IsReparsePoint([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $true }
    try {
        if (-not (Test-Path -LiteralPath $Path)) { return $true }
        $attr = [System.IO.File]::GetAttributes($Path)
        return (($attr -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
    } catch {
        # Fail-closed: unverifiable attributes are treated as unsafe
        return $true
    }
}

function Test-HasReparsePointInChain([string]$TargetPath) {
    $norm = Normalize-PathKey $TargetPath
    if ([string]::IsNullOrWhiteSpace($norm)) { return $true }
    try {
        $curr = [System.IO.Path]::GetFullPath($TargetPath.Trim()).TrimEnd('\', '/')
        while (-not [string]::IsNullOrEmpty($curr) -and $curr.Length -gt 3) {
            if (Test-IsReparsePoint $curr) { return $true }
            $parent = [System.IO.Path]::GetDirectoryName($curr)
            if ([string]::IsNullOrEmpty($parent)) { break }
            $curr = $parent.TrimEnd('\', '/')
        }
        return $false
    } catch {
        return $true
    }
}

function Test-FileLocked([string]$FilePath) {
    try {
        $fs = [System.IO.File]::Open($FilePath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        $fs.Close()
        return $false
    } catch {
        return $true
    }
}

function Format-Size([long]$Bytes) {
    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N1} MB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N1} KB' -f ($Bytes / 1KB)) }
    return "$Bytes B"
}

function Get-RunningCodexProcesses {
    $names = @('ChatGPT', 'codex', 'codex-code-mode-host', 'codex-computer-use-swift')
    $procs = @()
    foreach ($n in $names) {
        $p = Get-Process -Name $n -ErrorAction SilentlyContinue
        if ($p) { $procs += $p }
    }
    return $procs
}

function Move-ToRecycleBinSafe([string]$TargetPath) {
    if (Test-IsProtectedPath $TargetPath) {
        throw "拒绝操作受保护路径: $TargetPath"
    }
    if (Test-HasReparsePointInChain $TargetPath) {
        throw "拒绝操作包含目录联接/符号链接的路径链: $TargetPath"
    }
    if (Test-Path -LiteralPath $TargetPath -PathType Container) {
        $stats = [CodexCleanerNative.Scanner]::MeasureDirectory($TargetPath)
        if ($stats.JunctionCount -gt 0) {
            throw "目录内部包含 $($stats.JunctionCount) 个联接点，禁止直接回收: $TargetPath"
        }
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory(
            $TargetPath,
            [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,
            [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin
        )
    } elseif (Test-Path -LiteralPath $TargetPath -PathType Leaf) {
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile(
            $TargetPath,
            [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,
            [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin
        )
    }
}

function Ensure-Directory([string]$Dir) {
    if (-not (Test-Path -LiteralPath $Dir)) {
        New-Item -ItemType Directory -Path $Dir -Force | Out-Null
    }
}

function Write-AuditLog([hashtable]$Record, [string]$Prefix) {
    Ensure-Directory $LogDir
    $ts = Get-Date -Format 'yyyyMMdd-HHmmss'
    $logPath = Join-Path $LogDir "$Prefix-$ts.json"
    $json = $Record | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText($logPath, $json, [System.Text.UTF8Encoding]::new($false))

    # Rotate old logs so logs/ itself never bloats
    $existing = @(Get-ChildItem -LiteralPath $LogDir -Filter '*.json' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending)
    if ($existing.Count -gt $MaxLogFiles) {
        $existing | Select-Object -Skip $MaxLogFiles | ForEach-Object {
            try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue } catch {}
        }
    }
    return $logPath
}

function Invoke-CacheDirFileSweep {
    param(
        [Parameter(Mandatory)][string]$RootPath,
        [Parameter(Mandatory)][int]$MinAgeDays,
        [switch]$DoApply,
        [switch]$PruneEmptyDirs
    )
    $result = [ordered]@{
        root             = $RootPath
        exists           = $false
        eligibleFiles    = 0
        eligibleBytes    = [long]0
        deletedFiles     = 0
        deletedBytes     = [long]0
        prunedEmptyDirs  = 0
        skippedLocked    = 0
        skippedJunctions = 0
        skippedRecent    = 0
        skippedProtected = 0
        errors           = @()
    }

    if (-not (Test-Path -LiteralPath $RootPath -PathType Container)) { return $result }
    $result.exists = $true

    if (Test-HasReparsePointInChain $RootPath) {
        # Refuse to traverse if root or any ancestor is a junction/symlink
        $result.skippedJunctions++
        return $result
    }

    $cutoffUtc = (Get-Date).ToUniversalTime().AddDays(-$MinAgeDays)
    $stack = New-Object System.Collections.Generic.Stack[string]
    $visitedDirs = New-Object System.Collections.Generic.List[string]
    $stack.Push($RootPath)

    while ($stack.Count -gt 0) {
        $curr = $stack.Pop()
        $di = $null
        try { $di = [System.IO.DirectoryInfo]::new($curr) } catch { continue }
        if ([CodexCleanerNative.Scanner]::IsReparsePoint($di)) {
            $result.skippedJunctions++
            continue
        }

        if ((Normalize-PathKey $curr) -ne (Normalize-PathKey $RootPath)) {
            $visitedDirs.Add($curr)
        }

        try {
            foreach ($f in $di.EnumerateFiles()) {
                try {
                    if ([CodexCleanerNative.Scanner]::IsReparsePoint($f)) {
                        $result.skippedJunctions++
                        continue
                    }
                    if (Test-IsProtectedPath $f.FullName) {
                        $result.skippedProtected++
                        continue
                    }
                    if ($f.LastWriteTimeUtc -gt $cutoffUtc) {
                        $result.skippedRecent++
                        continue
                    }
                    if ($DoApply -and (Test-FileLocked $f.FullName)) {
                        $result.skippedLocked++
                        continue
                    }

                    $len = $f.Length
                    $result.eligibleFiles++
                    $result.eligibleBytes += $len

                    if ($DoApply) {
                        try {
                            # Re-verify full ancestor chain + live attributes right before unlink
                            if (Test-HasReparsePointInChain $f.FullName) {
                                $result.skippedJunctions++
                                continue
                            }
                            $liveAttr = [System.IO.File]::GetAttributes($f.FullName)
                            if (($liveAttr -band ([System.IO.FileAttributes]::ReparsePoint -bor [System.IO.FileAttributes]::Directory)) -ne 0) {
                                $result.skippedJunctions++
                                continue
                            }
                            if (($liveAttr -band [System.IO.FileAttributes]::ReadOnly) -ne 0) {
                                [System.IO.File]::SetAttributes($f.FullName, ($liveAttr -band (-bnot [System.IO.FileAttributes]::ReadOnly)))
                            }
                            [System.IO.File]::Delete($f.FullName)
                            $result.deletedFiles++
                            $result.deletedBytes += $len
                        } catch {
                            $result.skippedLocked++
                            if ($result.errors.Count -lt 20) {
                                $result.errors += "$($f.FullName): $($_.Exception.Message)"
                            }
                        }
                    }
                } catch {}
            }
        } catch {}

        try {
            foreach ($sub in $di.EnumerateDirectories()) {
                try {
                    if ([CodexCleanerNative.Scanner]::IsReparsePoint($sub)) {
                        $result.skippedJunctions++
                        continue
                    }
                    if (Test-IsProtectedPath $sub.FullName) {
                        $result.skippedProtected++
                        continue
                    }
                    $stack.Push($sub.FullName)
                } catch {}
            }
        } catch {}
    }

    if ($PruneEmptyDirs -and $DoApply -and $visitedDirs.Count -gt 0) {
        # Sort deepest paths first to prune bottom-up without ever touching RootPath itself
        $ordered = $visitedDirs | Sort-Object { $_.Length } -Descending
        foreach ($d in $ordered) {
            try {
                if (Test-IsProtectedPath $d) { continue }
                if (Test-IsReparsePoint $d) { continue }
                $di = [System.IO.DirectoryInfo]::new($d)
                if ($di.LastWriteTimeUtc -gt $cutoffUtc) { continue }
                $hasAny = $false
                foreach ($x in $di.EnumerateFileSystemInfos()) { $hasAny = $true; break }
                if (-not $hasAny) {
                    [System.IO.Directory]::Delete($d, $false)
                    $result.prunedEmptyDirs++
                }
            } catch {}
        }
    }

    return $result
}

function Find-WorkspaceScratchCandidates {
    param([string[]]$Roots, [int]$FloorHours = 72)

    $targetNames = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    @('_tmp', '__pycache__', '.pytest_cache') | ForEach-Object { [void]$targetNames.Add($_) }

    $skipNames = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    @('node_modules', '.git', '.venv', 'venv', 'runtime', 'runtimes', '_缓存', '_文件中心', 'store', 'pnpm-home') | ForEach-Object { [void]$skipNames.Add($_) }

    $cutoffUtc = (Get-Date).ToUniversalTime().AddHours(-$FloorHours)
    $items = @()

    foreach ($r in $Roots) {
        if (-not (Test-Path -LiteralPath $r -PathType Container)) { continue }
        $dirs = [CodexCleanerNative.Scanner]::FindNamedDirectories($r, $targetNames, $skipNames, 5)
        foreach ($d in $dirs) {
            if (Test-IsProtectedPath $d) { continue }
            if (Test-IsReparsePoint $d) { continue }
            $stats = [CodexCleanerNative.Scanner]::MeasureDirectory($d)
            if ($stats.RootIsJunction -or $stats.JunctionCount -gt 0) { continue }
            if ($stats.NewestWriteUtc -gt $cutoffUtc) { continue }
            $items += [PSCustomObject]@{
                Path          = $d
                SizeBytes     = $stats.TotalBytes
                FileCount     = $stats.FileCount
                NewestLocal   = $stats.NewestWriteUtc.ToLocalTime().ToString('yyyy-MM-dd HH:mm')
                JunctionCount = $stats.JunctionCount
            }
        }
    }
    return $items
}

function Get-SumBytes($Items, [string]$PropertyName) {
    [long]$sum = 0
    if ($null -eq $Items) { return $sum }
    foreach ($it in $Items) {
        if ($null -ne $it) {
            $sum += [long]$it.$PropertyName
        }
    }
    return $sum
}

function Invoke-DailySafeMode {
    param(
        [switch]$ScheduledOnly,
        [switch]$ForceApply
    )
    $doApply = [bool]($Apply -or $ForceApply)

    $modeName = if ($ScheduledOnly) { 'ScheduledTemp' } else { 'DailySafe' }
    Write-Host ''
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "  Codex 安全清理器 | 模式: $modeName | 执行: $(if ($doApply) { '实际清理' } else { '只读预览 (Dry-Run)' })" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan

    $running = @(Get-RunningCodexProcesses)
    Write-Host ("当前运行中的 ChatGPT/codex 进程数: {0}（安全模式不会干扰或关闭任何进程）" -f $running.Count) -ForegroundColor DarkGray

    $tempRes = Invoke-CacheDirFileSweep -RootPath $TempRoot -MinAgeDays $TempMinAgeDays -DoApply:$doApply -PruneEmptyDirs
    $pkgResults = @()
    $scratchItems = @()
    $scratchRecycled = @()
    $scratchErrors = @()

    if (-not $ScheduledOnly) {
        foreach ($pr in $PackageCacheRoots) {
            if (Test-Path -LiteralPath $pr) {
                $pkgResults += (Invoke-CacheDirFileSweep -RootPath $pr -MinAgeDays $PackageCacheMinAgeDays -DoApply:$doApply -PruneEmptyDirs)
            }
        }
        if (-not $SkipWorkspaceScratchInDaily) {
            $scratchItems = @(Find-WorkspaceScratchCandidates -Roots $WorkspaceRoots -FloorHours $HardActiveFloorHours)
            if ($doApply) {
                foreach ($si in $scratchItems) {
                    try {
                        Move-ToRecycleBinSafe $si.Path
                        $scratchRecycled += $si
                    } catch {
                        $scratchErrors += "$($si.Path): $($_.Exception.Message)"
                    }
                }
            }
        }
    }

    Write-Host ''
    Write-Host ("1) Temp 缓存 ({0} | 超过 {1} 天且未占用):" -f $TempRoot, $TempMinAgeDays) -ForegroundColor Yellow
    Write-Host ("   可清理: {0} 个文件 ({1}) | 跳过占用: {2} | 跳过近期: {3} | 跳过联接: {4}" -f `
        $tempRes.eligibleFiles, (Format-Size $tempRes.eligibleBytes), $tempRes.skippedLocked, $tempRes.skippedRecent, $tempRes.skippedJunctions)
    if ($doApply) {
        Write-Host ("   已清理: {0} 个文件 ({1})，移除空子目录: {2}" -f `
            $tempRes.deletedFiles, (Format-Size $tempRes.deletedBytes), $tempRes.prunedEmptyDirs) -ForegroundColor Green
    }

    $totalEligibleBytes = [long]$tempRes.eligibleBytes
    $totalDeletedBytes = [long]$tempRes.deletedBytes

    if (-not $ScheduledOnly) {
        Write-Host ("2) 包管理器缓存 (pip/npm/uv | 超过 {0} 天):" -f $PackageCacheMinAgeDays) -ForegroundColor Yellow
        foreach ($p in $pkgResults) {
            $totalEligibleBytes += $p.eligibleBytes
            $totalDeletedBytes += $p.deletedBytes
            Write-Host ("   - {0}: 可清理 {1} 文件 ({2}) | 跳过近期 {3}" -f `
                $p.root, $p.eligibleFiles, (Format-Size $p.eligibleBytes), $p.skippedRecent)
            if ($doApply) {
                Write-Host ("     已清理: {0} 文件 ({1})" -f $p.deletedFiles, (Format-Size $p.deletedBytes)) -ForegroundColor Green
            }
        }

        Write-Host ("3) 工作区约定临时目录 (_tmp / __pycache__ / .pytest_cache | >72小时且无联接，移入回收站):") -ForegroundColor Yellow
        $scratchBytes = Get-SumBytes $scratchItems 'SizeBytes'
        $totalEligibleBytes += $scratchBytes
        Write-Host ("   命中目录数: {0} ({1})" -f $scratchItems.Count, (Format-Size $scratchBytes))
        foreach ($si in ($scratchItems | Select-Object -First 10)) {
            Write-Host ("   - [{0}] {1} (最新: {2})" -f (Format-Size $si.SizeBytes), $si.Path, $si.NewestLocal)
        }
        if ($scratchItems.Count -gt 10) {
            Write-Host ("   ... 其余 {0} 个已写入日志" -f ($scratchItems.Count - 10))
        }
        if ($doApply) {
            $recBytes = Get-SumBytes $scratchRecycled 'SizeBytes'
            $totalDeletedBytes += $recBytes
            Write-Host ("   已移入回收站: {0} 个目录 ({1})" -f $scratchRecycled.Count, (Format-Size $recBytes)) -ForegroundColor Green
        }
    }

    Write-Host ''
    Write-Host ("总计可释放: {0}" -f (Format-Size $totalEligibleBytes)) -ForegroundColor Cyan
    if ($doApply) {
        Write-Host ("实际已释放/移入回收站: {0}" -f (Format-Size $totalDeletedBytes)) -ForegroundColor Green
    }

    $record = [ordered]@{
        timestamp          = (Get-Date).ToString('o')
        mode               = $modeName
        applied            = $doApply
        runningCodexCount  = $running.Count
        tempMinAgeDays     = $TempMinAgeDays
        temp               = $tempRes
        packageCaches      = $pkgResults
        workspaceScratch   = @{
            foundCount    = $scratchItems.Count
            recycledCount = $scratchRecycled.Count
            items         = $scratchItems
            errors        = $scratchErrors
        }
        totalEligibleBytes = $totalEligibleBytes
        totalDeletedBytes  = $totalDeletedBytes
    }
    $logPath = Write-AuditLog -Record $record -Prefix $modeName.ToLowerInvariant()
    Write-Host "日志已保存: $logPath" -ForegroundColor DarkGray

    if ($Interactive -and (-not $doApply)) {
        if ($totalEligibleBytes -eq 0 -and $scratchItems.Count -eq 0) {
            Write-Host '当前没有需要清理的超期安全缓存。' -ForegroundColor Green
            return
        }
        Write-Host ''
        $ans = Read-Host '确认执行以上安全清理吗？(不会影响运行中的 ChatGPT 桌面端) [y/N]'
        if ($ans -match '^(y|yes)$') {
            Invoke-DailySafeMode -ScheduledOnly:$ScheduledOnly -ForceApply
        } else {
            Write-Host '已取消，未修改任何文件。' -ForegroundColor Yellow
        }
    }
}

function Get-WorkspaceAuditCandidates {
    $nowUtc = (Get-Date).ToUniversalTime()
    $minBytes = [long]$MinSizeMB * 1MB
    $rawTargets = New-Object System.Collections.Generic.List[object]

    foreach ($wsRoot in $WorkspaceRoots) {
        if (-not (Test-Path -LiteralPath $wsRoot -PathType Container)) { continue }
        $wsKey = Normalize-PathKey $wsRoot

        if (Test-Path -LiteralPath (Join-Path $wsRoot 'work') -PathType Container) {
            $workDir = Join-Path $wsRoot 'work'
            foreach ($d in ([System.IO.DirectoryInfo]::new($workDir)).EnumerateDirectories()) {
                $rawTargets.Add([PSCustomObject]@{ Path = $d.FullName; Category = 'work 任务目录'; IsFile = $false })
            }
        }

        if (Test-Path -LiteralPath (Join-Path $wsRoot 'outputs') -PathType Container) {
            $outDir = Join-Path $wsRoot 'outputs'
            foreach ($d in ([System.IO.DirectoryInfo]::new($outDir)).EnumerateDirectories()) {
                $rawTargets.Add([PSCustomObject]@{ Path = $d.FullName; Category = 'outputs 交付目录'; IsFile = $false })
            }
            foreach ($f in ([System.IO.DirectoryInfo]::new($outDir)).EnumerateFiles()) {
                if ($f.Length -ge 50MB) {
                    $rawTargets.Add([PSCustomObject]@{ Path = $f.FullName; Category = 'outputs 大型归档文件'; IsFile = $true })
                }
            }
        }

        if (-not (Test-Path -LiteralPath (Join-Path $wsRoot 'work') -PathType Container) -and
            -not (Test-Path -LiteralPath (Join-Path $wsRoot 'outputs') -PathType Container)) {
            foreach ($d in ([System.IO.DirectoryInfo]::new($wsRoot)).EnumerateDirectories()) {
                $rawTargets.Add([PSCustomObject]@{ Path = $d.FullName; Category = '工作区子目录'; IsFile = $false })
            }
        }
    }

    $candidates = @()
    foreach ($t in $rawTargets) {
        $p = $t.Path
        $protected = Test-IsProtectedPath $p
        if ($t.IsFile) {
            $fi = [System.IO.FileInfo]::new($p)
            $sizeBytes = $fi.Length
            $fileCount = 1
            $junctionCount = if ([CodexCleanerNative.Scanner]::IsReparsePoint($fi)) { 1 } else { 0 }
            $newestUtc = $fi.LastWriteTimeUtc
        } else {
            $st = [CodexCleanerNative.Scanner]::MeasureDirectory($p)
            $sizeBytes = $st.TotalBytes
            $fileCount = $st.FileCount
            $junctionCount = $st.JunctionCount
            $newestUtc = $st.NewestWriteUtc
        }

        if ($sizeBytes -lt $minBytes -and (-not $t.IsFile)) { continue }

        $ageDays = [math]::Round(($nowUtc - $newestUtc).TotalDays, 1)
        $ageHours = ($nowUtc - $newestUtc).TotalHours

        $status = '候选-可移入回收站'
        $canRecycle = $true
        $note = '超过宽限期、无联接、非红区'

        if ($protected) {
            $status = '红区受保护'
            $canRecycle = $false
            $note = '包含受保护源码/工具依赖，禁止清理'
        } elseif ($junctionCount -gt 0) {
            $status = '含目录联接-跳过'
            $canRecycle = $false
            $note = "内部含 $junctionCount 个目录联接，不直接回收"
        } elseif ($ageHours -lt $HardActiveFloorHours) {
            $status = '72小时内活跃-跳过'
            $canRecycle = $false
            $note = '近期有写入活动，硬保护跳过'
        } elseif ($ageDays -lt $WorkspaceMinAgeDays) {
            $status = '宽限期内-跳过'
            $canRecycle = $false
            $note = "不足 $WorkspaceMinAgeDays 天宽限期"
        }

        # Add helpful hint for known cleanable patterns
        $leaf = [System.IO.Path]::GetFileName($p)
        if ($canRecycle) {
            if ($leaf -match '^(clean-windows|e2e-isolated-.*|fixtures-.*|native-e2e-.*|stage-qa.*|build-tmp.*|tmp-stage.*)$') {
                $note = '高置信一次性构建/测试暂存，推荐移入回收站'
            }
        }

        $candidates += [PSCustomObject]@{
            Path          = $p
            Category      = $t.Category
            SizeBytes     = $sizeBytes
            SizeMB        = [math]::Round($sizeBytes / 1MB, 1)
            FileCount     = $fileCount
            NewestLocal   = $newestUtc.ToLocalTime().ToString('yyyy-MM-dd HH:mm')
            AgeDays       = $ageDays
            JunctionCount = $junctionCount
            Status        = $status
            CanRecycle    = $canRecycle
            Note          = $note
        }
    }

    return @($candidates | Sort-Object SizeBytes -Descending)
}

function Invoke-WorkspaceAuditMode {
    Write-Host ''
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "  Codex 工作区瘦身体检 | 阈值: >= ${MinSizeMB} MB, >= ${WorkspaceMinAgeDays} 天未改动" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan

    $all = @(Get-WorkspaceAuditCandidates)
    $eligible = @($all | Where-Object { $_.CanRecycle })
    $blocked = @($all | Where-Object { -not $_.CanRecycle })

    $eligibleBytes = Get-SumBytes $eligible 'SizeBytes'
    Ensure-Directory $ReportDir

    $reportObj = [ordered]@{
        generatedAt         = (Get-Date).ToString('o')
        minSizeMB           = $MinSizeMB
        workspaceMinAgeDays = $WorkspaceMinAgeDays
        eligibleCount       = $eligible.Count
        eligibleTotalBytes  = $eligibleBytes
        eligible            = $eligible
        skippedOrProtected  = $blocked
    }
    $jsonReportPath = Join-Path $ReportDir 'latest-audit.json'
    [System.IO.File]::WriteAllText($jsonReportPath, ($reportObj | ConvertTo-Json -Depth 6), [System.Text.UTF8Encoding]::new($false))

    # Build human-readable Markdown report
    $md = New-Object System.Text.StringBuilder
    [void]$md.AppendLine("# Codex 工作区瘦身体检报告")
    [void]$md.AppendLine("")
    [void]$md.AppendLine("- 生成时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    [void]$md.AppendLine("- 筛选条件：体积 `>= ${MinSizeMB} MB`（含 `outputs/` 中 `>= 50 MB` 的大型归档文件），最新文件修改距今 `>= ${WorkspaceMinAgeDays} 天`，且不含目录联接、不属红区。")
    [void]$md.AppendLine("- 可安全移入回收站候选：**$($eligible.Count) 项**，合计 **$(Format-Size $eligibleBytes)**")
    [void]$md.AppendLine("")
    [void]$md.AppendLine("## 一、可移入回收站候选（按体积降序）")
    [void]$md.AppendLine("")
    [void]$md.AppendLine("| 序号 | 体积 | 文件数 | 最新修改 | 分类 | 说明 | 路径 |")
    [void]$md.AppendLine("|---:|---:|---:|---|---|---|---|")
    for ($i = 0; $i -lt $eligible.Count; $i++) {
        $c = $eligible[$i]
        [void]$md.AppendLine("| $($i+1) | $(Format-Size $c.SizeBytes) | $($c.FileCount) | $($c.NewestLocal) | $($c.Category) | $($c.Note) | ``$($c.Path)`` |")
    }
    [void]$md.AppendLine("")
    [void]$md.AppendLine("## 二、已自动跳过的大体积目录（受保护 / 含联接 / 近期活跃）")
    [void]$md.AppendLine("")
    [void]$md.AppendLine("| 体积 | 文件数 | 最新修改 | 状态 | 说明 | 路径 |")
    [void]$md.AppendLine("|---:|---:|---|---|---|---|")
    foreach ($b in ($blocked | Select-Object -First 30)) {
        [void]$md.AppendLine("| $(Format-Size $b.SizeBytes) | $($b.FileCount) | $($b.NewestLocal) | $($b.Status) | $($b.Note) | ``$($b.Path)`` |")
    }
    $mdReportPath = Join-Path $ReportDir 'latest-audit.md'
    [System.IO.File]::WriteAllText($mdReportPath, $md.ToString(), [System.Text.UTF8Encoding]::new($false))

    Write-Host ("扫描完成！可移入回收站候选: {0} 项（合计 {1}）；已跳过/受保护: {2} 项。" -f `
        $eligible.Count, (Format-Size $eligibleBytes), $blocked.Count) -ForegroundColor Green
    Write-Host "完整报告: $mdReportPath" -ForegroundColor DarkGray
    Write-Host ''

    $showCount = [math]::Min($eligible.Count, 25)
    for ($i = 0; $i -lt $showCount; $i++) {
        $c = $eligible[$i]
        Write-Host ("[{0,2}] {1,9} | {2,7} 文件 | {3} | {4}" -f `
            ($i + 1), (Format-Size $c.SizeBytes), $c.FileCount, $c.NewestLocal, $c.Path)
        Write-Host ("     -> {0}" -f $c.Note) -ForegroundColor DarkGray
    }
    if ($eligible.Count -gt $showCount) {
        Write-Host ("... 共 {0} 项，更多请查看 {1}" -f $eligible.Count, $mdReportPath) -ForegroundColor DarkGray
    }

    $targetsToRecycle = @()
    if ($Apply -and $RecyclePaths.Count -gt 0) {
        $targetsToRecycle = $RecyclePaths
    } elseif ($Interactive -and $eligible.Count -gt 0) {
        Write-Host ''
        $sel = Read-Host '输入要移入【Windows 回收站】的序号（如 1,2,3；直接回车退出不清理）'
        if (-not [string]::IsNullOrWhiteSpace($sel)) {
            $indices = $sel -split '[,\s]+' | Where-Object { $_ -match '^\d+$' } | ForEach-Object { [int]$_ } | Select-Object -Unique
            foreach ($idx in $indices) {
                if ($idx -ge 1 -and $idx -le $eligible.Count) {
                    $targetsToRecycle += $eligible[$idx - 1].Path
                }
            }
        }
    }

    if ($targetsToRecycle.Count -gt 0) {
        Write-Host ''
        Write-Host '即将移入 Windows 回收站（可随时在回收站还原）：' -ForegroundColor Yellow
        foreach ($tp in $targetsToRecycle) { Write-Host "  - $tp" }
        if ($Interactive) {
            $confirm = Read-Host '再次确认移入回收站吗？[y/N]'
            if ($confirm -notmatch '^(y|yes)$') {
                Write-Host '已取消，未移动任何文件。' -ForegroundColor Yellow
                return
            }
        }

        $recycled = @()
        $failed = @()
        $cutoffUtc = (Get-Date).ToUniversalTime().AddHours(-$HardActiveFloorHours)
        foreach ($tp in $targetsToRecycle) {
            try {
                if (-not (Test-Path -LiteralPath $tp)) {
                    $failed += "${tp}: 路径不存在"
                    Write-Host "  [跳过] ${tp}: 路径不存在" -ForegroundColor Yellow
                    continue
                }
                if (Test-IsProtectedPath $tp) {
                    $failed += "${tp}: 红区受保护，已拦截"
                    Write-Host "  [已拦截] ${tp}: 红区受保护" -ForegroundColor Red
                    continue
                }
                if (Test-IsReparsePoint $tp) {
                    $failed += "${tp}: 目录联接，已拦截"
                    Write-Host "  [已拦截] ${tp}: 目录联接" -ForegroundColor Red
                    continue
                }
                if (Test-Path -LiteralPath $tp -PathType Container) {
                    $st = [CodexCleanerNative.Scanner]::MeasureDirectory($tp)
                    if ($st.JunctionCount -gt 0) {
                        $failed += "${tp}: 内部含 $($st.JunctionCount) 个联接，已拦截"
                        Write-Host "  [已拦截] ${tp}: 内部含 $($st.JunctionCount) 个联接" -ForegroundColor Red
                        continue
                    }
                    if ($st.NewestWriteUtc -gt $cutoffUtc) {
                        $failed += "${tp}: 72 小时内有修改，已拦截"
                        Write-Host "  [已拦截] ${tp}: 72 小时内有修改" -ForegroundColor Yellow
                        continue
                    }
                    $sz = $st.TotalBytes
                } else {
                    $fi = [System.IO.FileInfo]::new($tp)
                    if ($fi.LastWriteTimeUtc -gt $cutoffUtc) {
                        $failed += "${tp}: 72 小时内有修改，已拦截"
                        Write-Host "  [已拦截] ${tp}: 72 小时内有修改" -ForegroundColor Yellow
                        continue
                    }
                    $sz = $fi.Length
                }

                Move-ToRecycleBinSafe $tp
                $recycled += [PSCustomObject]@{ Path = $tp; SizeBytes = $sz }
                Write-Host ("  [已进回收站] {0} ({1})" -f $tp, (Format-Size $sz)) -ForegroundColor Green
            } catch {
                $failed += "${tp}: $($_.Exception.Message)"
                Write-Host ("  [跳过/拦截] {0}: {1}" -f $tp, $_.Exception.Message) -ForegroundColor Red
            }
        }

        $logPath = Write-AuditLog -Record ([ordered]@{
            timestamp = (Get-Date).ToString('o')
            mode      = 'WorkspaceAuditRecycle'
            recycled  = $recycled
            failed    = $failed
        }) -Prefix 'workspace-recycle'
        Write-Host "回收记录已保存: $logPath" -ForegroundColor DarkGray
    }
}

function Invoke-OfflineBrowserCacheMode {
    param([switch]$ForceApply)
    $doApply = [bool]($Apply -or $ForceApply)
    Write-Host ''
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "  Codex 离线浏览器缓存与旧日志清理 | 执行: $(if ($doApply) { '实际清理' } else { '只读预览 (Dry-Run)' })" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan

    $running = @(Get-RunningCodexProcesses)
    if ($running.Count -gt 0) {
        Write-Host ''
        Write-Host '[已阻止] 检测到 ChatGPT / Codex 桌面端或 CLI 进程仍在运行：' -ForegroundColor Red
        foreach ($p in ($running | Select-Object -First 10)) {
            Write-Host ("  - {0} (PID={1})" -f $p.ProcessName, $p.Id) -ForegroundColor Red
        }
        Write-Host '为保护 ChatGPT 桌面端数据完整性，本工具绝不强杀进程。请先正常退出桌面端后重试。' -ForegroundColor Yellow

        $logPath = Write-AuditLog -Record ([ordered]@{
            timestamp = (Get-Date).ToString('o')
            mode      = 'OfflineBrowserCache'
            status    = 'blocked_process_running'
            processes = @($running | ForEach-Object { @{ name = $_.ProcessName; pid = $_.Id } })
        }) -Prefix 'offline-blocked'
        Write-Host "拦截记录已保存: $logPath" -ForegroundColor DarkGray
        exit 2
    }

    # Note: NEVER prune directories in BrowserCacheRoot because MSIX junctions target specific leaf folders!
    $bcRes = Invoke-CacheDirFileSweep -RootPath $BrowserCacheRoot -MinAgeDays 0 -DoApply:$doApply
    $logRes = Invoke-CacheDirFileSweep -RootPath $DesktopLogsRoot -MinAgeDays $DesktopLogsMinAgeDays -DoApply:$doApply

    $totalEligible = [long]$bcRes.eligibleBytes + [long]$logRes.eligibleBytes
    $totalDeleted = [long]$bcRes.deletedBytes + [long]$logRes.deletedBytes

    Write-Host ("1) 浏览器缓存 ({0} | 仅清内部文件，完整保留目录与联接):" -f $BrowserCacheRoot) -ForegroundColor Yellow
    Write-Host ("   可清理: {0} 文件 ({1}) | 跳过锁定: {2}" -f $bcRes.eligibleFiles, (Format-Size $bcRes.eligibleBytes), $bcRes.skippedLocked)
    if ($doApply) {
        Write-Host ("   已清理: {0} 文件 ({1})" -f $bcRes.deletedFiles, (Format-Size $bcRes.deletedBytes)) -ForegroundColor Green
    }

    Write-Host ("2) 桌面旧日志 ({0} | 超过 {1} 天):" -f $DesktopLogsRoot, $DesktopLogsMinAgeDays) -ForegroundColor Yellow
    Write-Host ("   可清理: {0} 文件 ({1}) | 跳过近期: {2}" -f $logRes.eligibleFiles, (Format-Size $logRes.eligibleBytes), $logRes.skippedRecent)
    if ($doApply) {
        Write-Host ("   已清理: {0} 文件 ({1})" -f $logRes.deletedFiles, (Format-Size $logRes.deletedBytes)) -ForegroundColor Green
    }

    $logPath = Write-AuditLog -Record ([ordered]@{
        timestamp          = (Get-Date).ToString('o')
        mode               = 'OfflineBrowserCache'
        status             = 'ok'
        applied            = $doApply
        browserCache       = $bcRes
        desktopLogs        = $logRes
        totalEligibleBytes = $totalEligible
        totalDeletedBytes  = $totalDeleted
    }) -Prefix 'offline-cache'
    Write-Host "日志已保存: $logPath" -ForegroundColor DarkGray

    if ($Interactive -and (-not $doApply)) {
        if ($totalEligible -eq 0) {
            Write-Host '当前没有可清理的离线缓存文件。' -ForegroundColor Green
            return
        }
        Write-Host ''
        $ans = Read-Host '确认清理以上离线缓存内部文件吗？(目录结构与联接将完整保留) [y/N]'
        if ($ans -match '^(y|yes)$') {
            Invoke-OfflineBrowserCacheMode -ForceApply
        } else {
            Write-Host '已取消，未修改任何文件。' -ForegroundColor Yellow
        }
    }
}

switch ($Mode) {
    'DailySafe'           { Invoke-DailySafeMode }
    'ScheduledTemp'       { Invoke-DailySafeMode -ScheduledOnly }
    'WorkspaceAudit'      { Invoke-WorkspaceAuditMode }
    'OfflineBrowserCache' { Invoke-OfflineBrowserCacheMode }
}
