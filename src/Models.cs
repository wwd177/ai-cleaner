using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace AICleaner;

public enum ZoneId
{
    Antigravity,
    GptCodex,
    DeepSeekHarness,
    ClaudeCode,
    AiToolchains
}

public enum ScanStatus
{
    Ok,
    Partial,
    Cancelled,
    Error
}

public enum CleanTier
{
    SafeOnline,       // 绿区：应用运行中也可安全清理超期未占用文件
    RecycleCandidate, // 黄区：人工勾选后移入 Windows 回收站（可撤销）
    OfflineCache,     // 蓝区：需先退出该专区应用后清理内部文件（保留目录骨架与联接）
    ProtectedSkipped  // 红区 / 只读诊断雷达 / 白名单 / 含联接 / 72h 活跃：自动保护跳过，禁止勾选
}

public enum CleanActionType
{
    None,
    SweepExpiredFiles,
    OfflineSweepFilesKeepSkeleton,
    RecycleDirectoryOrFile,
    RecycleExpiredChildren,
    RecycleBatchDirectories
}

public sealed class CleanCandidate : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _canSelect;
    private string _reason = "";
    private string _statusText = "";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ZoneId Zone { get; set; }
    public CleanTier Tier { get; set; }
    public CleanActionType ActionType { get; set; }
    public string Title { get; set; } = "";
    public string Category { get; set; } = "";
    public string Path { get; set; } = "";
    public List<string> BatchPaths { get; set; } = new();
    public long SizeBytes { get; set; }
    public long FileCount { get; set; }
    public long JunctionCount { get; set; }
    public long LockedCount { get; set; }
    public long RecentSkippedCount { get; set; }
    public DateTime NewestWriteLocal { get; set; }
    public int MinAgeDays { get; set; }
    public bool PruneEmptySubdirs { get; set; }
    public bool IsRecommended { get; set; }
    public bool IsUserWhitelisted { get; set; }
    public bool IsReadOnlyDiagnostic { get; set; }
    public bool EnforceActive72hFloor { get; set; } = true;
    public string ActionHint { get; set; } = "";

    /// <summary>Stable logical identity across rescans so user selections survive refreshes safely.</summary>
    public string StableKey => $"{Zone}|{ActionType}|{Path.Trim().ToLowerInvariant()}|{Title}";

    public bool CanSelect
    {
        get => _canSelect;
        set
        {
            bool effective = !IsReadOnlyDiagnostic && value;
            if (_canSelect != effective)
            {
                _canSelect = effective;
                if (!_canSelect && _isSelected)
                {
                    _isSelected = false;
                    OnPropertyChanged(nameof(IsSelected));
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(TierBadge));
                OnPropertyChanged(nameof(TierColorHex));
                OnPropertyChanged(nameof(TierBorderHex));
                OnPropertyChanged(nameof(TierInkHex));
                OnPropertyChanged(nameof(VerdictImpactText));
            }
        }
    }

    public string Reason
    {
        get => _reason;
        set
        {
            if (_reason != value)
            {
                _reason = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Atomically synchronizes OfflineCache lock state, selection, reason, and all derived UI bindings.</summary>
    public void ApplyProcessLockState(bool isRunning)
    {
        if (Tier != CleanTier.OfflineCache) return;
        CanSelect = !isRunning;
        if (isRunning) IsSelected = false;
        Reason = isRunning
            ? "当前应用正在运行，已自动加锁；请正常退出应用后刷新再清理"
            : "应用已退出，离线缓存已解锁，可安全清理（保留目录骨架与登录态）";
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            var next = CanSelect && !IsReadOnlyDiagnostic && value;
            if (_isSelected != next)
            {
                _isSelected = next;
                OnPropertyChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText != value)
            {
                _statusText = value;
                OnPropertyChanged();
            }
        }
    }

    public string SizeText => (Tier == CleanTier.ProtectedSkipped && SizeBytes == 0 && !IsReadOnlyDiagnostic) ? "受保护" : FormatBytes(SizeBytes);
    public string LastWriteText => NewestWriteLocal == DateTime.MinValue ? "-" : NewestWriteLocal.ToString("yyyy-MM-dd HH:mm");

    // Kleaner-style Plain-English Impact Verdict shown in the bottom Inspector Strip
    public string VerdictImpactText
    {
        get
        {
            if (IsUserWhitelisted)
                return "已加入用户永久白名单：扫描与清理均会自动跳过，如需取消可在右键或「规则/白名单」中移出。";
            if (IsReadOnlyDiagnostic)
                return "只读诊断雷达 · 绝不自动删除：此项涉及用户历史回滚、硬链接存储、锁定版本浏览器或断点续传模型，仅展示体积供「空间雷达」查看。";
            return Tier switch
            {
                CleanTier.SafeOnline => ActionType == CleanActionType.RecycleExpiredChildren || ActionType == CleanActionType.RecycleBatchDirectories
                    ? "在线安全 · 可撤销：执行时移入 Windows 回收站，不影响运行中的 AI 客户端，需要时可随时还原。"
                    : "在线安全 · 自动重建：仅清理超期且未被占用的临时/缓存文件，应用下次需要会自动重建。",
                CleanTier.RecycleCandidate => "回收站瘦身 · 可撤销：默认不自动勾选，确认后仅移入 Windows 回收站，不会绕过回收站永久销毁。",
                CleanTier.OfflineCache => CanSelect
                    ? "离线缓存 · 可重建：当前应用已退出，清理后仅释放浏览器渲染/着色器缓存，完整保留登录态、Cookies 与目录联接骨架。"
                    : "运行中自动加锁：检测到该专区应用正在运行，为防缓存锁冲突已禁止勾选；正常退出应用切回本窗口将自动解锁。",
                CleanTier.ProtectedSkipped => "红区硬保护 · 严禁触碰：该目录包含核心对话、凭据、运行环境或 NTFS 目录联接，引擎底层双次校验永久拦截。",
                _ => Reason
            };
        }
    }

    public string TierBadge
    {
        get
        {
            if (IsUserWhitelisted) return "白名单 · 保留";
            if (IsReadOnlyDiagnostic) return "只读 · 诊断雷达";
            return Tier switch
            {
                CleanTier.SafeOnline => "绿 · 在线安全",
                CleanTier.RecycleCandidate => IsRecommended ? "黄 · 推荐回收" : "黄 · 移入回收站",
                CleanTier.OfflineCache => CanSelect ? "蓝 · 离线可清" : "灰 · 需先退出",
                CleanTier.ProtectedSkipped => "红 · 硬保护",
                _ => ""
            };
        }
    }

    // Frosted White-Blue Light Glass Pill Colors (High Legibility WCAG AA+)
    public string TierColorHex
    {
        get
        {
            if (IsUserWhitelisted) return "#E6F3FF";
            if (IsReadOnlyDiagnostic) return "#E6F7F7";
            return Tier switch
            {
                CleanTier.SafeOnline => "#E4F8EF",
                CleanTier.RecycleCandidate => IsRecommended ? "#FFF1D6" : "#FFF8E6",
                CleanTier.OfflineCache => CanSelect ? "#E9ECFF" : "#EDF2F8",
                CleanTier.ProtectedSkipped => "#FDECEF",
                _ => "#EEF4FB"
            };
        }
    }

    public string TierBorderHex
    {
        get
        {
            if (IsUserWhitelisted) return "#A7D0FC";
            if (IsReadOnlyDiagnostic) return "#99E0DC";
            return Tier switch
            {
                CleanTier.SafeOnline => "#9FE3C5",
                CleanTier.RecycleCandidate => IsRecommended ? "#F3C36B" : "#F3D999",
                CleanTier.OfflineCache => CanSelect ? "#BCC5FC" : "#CFD9E8",
                CleanTier.ProtectedSkipped => "#F4B8C4",
                _ => "#CFE0F5"
            };
        }
    }

    public string TierInkHex
    {
        get
        {
            if (IsUserWhitelisted) return "#1B5BB8";
            if (IsReadOnlyDiagnostic) return "#0F766E";
            return Tier switch
            {
                CleanTier.SafeOnline => "#0A7548",
                CleanTier.RecycleCandidate => "#9A5B04",
                CleanTier.OfflineCache => CanSelect ? "#4238B8" : "#5B6E87",
                CleanTier.ProtectedSkipped => "#B4233C",
                _ => "#334E6E"
            };
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return string.Format(CultureInfo.InvariantCulture, "{0:F2} GB", bytes / (1024.0 * 1024 * 1024));
        if (bytes >= 1024L * 1024)
            return string.Format(CultureInfo.InvariantCulture, "{0:F1} MB", bytes / (1024.0 * 1024));
        if (bytes >= 1024L)
            return string.Format(CultureInfo.InvariantCulture, "{0:F1} KB", bytes / 1024.0);
        return $"{bytes} B";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ZoneProcessInfo
{
    public ZoneId Zone { get; set; }
    public bool IsRunning => RunningPids.Count > 0;
    public List<string> RunningProcesses { get; set; } = new();
    public List<int> RunningPids { get; set; } = new();
}

public sealed class ZoneScanReport
{
    public ZoneId Zone { get; set; }
    public DateTime ScannedAt { get; set; } = DateTime.Now;
    public ScanStatus Status { get; set; } = ScanStatus.Ok;
    public int AccessDeniedCount { get; set; }
    public long ElapsedMs { get; set; }
    public bool IsDetected { get; set; }
    public string? ErrorMessage { get; set; }
    public ZoneProcessInfo ProcessInfo { get; set; } = new();
    public List<CleanCandidate> Candidates { get; set; } = new();

    public long SafeOnlineBytes => Candidates.Where(c => c.Tier == CleanTier.SafeOnline && c.CanSelect && !c.IsReadOnlyDiagnostic).Sum(c => c.SizeBytes);
    public long RecycleCandidateBytes => Candidates.Where(c => c.Tier == CleanTier.RecycleCandidate && c.CanSelect && !c.IsReadOnlyDiagnostic).Sum(c => c.SizeBytes);
    public long RecommendedBytes => Candidates.Where(c => c.IsRecommended && c.CanSelect && !c.IsReadOnlyDiagnostic).Sum(c => c.SizeBytes);
    public long OfflineCacheBytes => Candidates.Where(c => c.Tier == CleanTier.OfflineCache && !c.IsReadOnlyDiagnostic).Sum(c => c.SizeBytes);
    public long DiagnosticBytes => Candidates.Where(c => c.IsReadOnlyDiagnostic).Sum(c => c.SizeBytes);
    public long ProtectedBytes => Candidates.Where(c => c.Tier == CleanTier.ProtectedSkipped).Sum(c => c.SizeBytes);
}

public sealed class CleanItemOutcome
{
    public string Title { get; set; } = "";
    public string Path { get; set; } = "";
    public string Action { get; set; } = "";
    public bool Succeeded { get; set; }
    public bool Blocked { get; set; }
    public long FreedBytes { get; set; }
    public long CleanedFiles { get; set; }
    public long SkippedLocked { get; set; }
    public long SkippedJunctions { get; set; }
    public string Message { get; set; } = "";
}

public sealed class CleanRunAudit
{
    public string Timestamp { get; set; } = DateTime.Now.ToString("O");
    public string Zone { get; set; } = "";
    public int SelectedCount { get; set; }
    public long TotalFreedBytes { get; set; }
    public List<CleanItemOutcome> Outcomes { get; set; } = new();

    public string DisplayTime =>
        DateTime.TryParse(Timestamp, out var dt) ? dt.ToString("yyyy-MM-dd HH:mm:ss") : Timestamp;
    public string FreedText => CleanCandidate.FormatBytes(TotalFreedBytes);
    public string ZoneLabel => ZoneCatalog.GetByNameOrSlug(Zone)?.DisplayName ?? Zone;
    public string SummaryLine =>
        $"成功 {Outcomes.Count(o => o.Succeeded)} 项 · 跳过/拦截 {Outcomes.Count(o => o.Blocked)} 项 · 释放 {FreedText}";
}

public sealed class SpaceMapEntry
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public bool IsReparsePoint { get; set; }
    public bool IsProtected { get; set; }
    public long SizeBytes { get; set; }
    public long FileCount { get; set; }
    public DateTime LastWriteLocal { get; set; }
    public double ShareRatio { get; set; } // 0..1

    public string TypeBadge => IsReparsePoint ? "联接" : (IsDirectory ? "目录" : "文件");
    public string SizeText => IsReparsePoint ? "联接跳过" : CleanCandidate.FormatBytes(SizeBytes);
    public string ShareText => IsReparsePoint ? "-" : string.Format(CultureInfo.InvariantCulture, "{0:F1}%", ShareRatio * 100.0);
    public double BarWidth => Math.Max(4, Math.Min(112, ShareRatio * 112.0));
    public string LastWriteText => LastWriteLocal == DateTime.MinValue ? "-" : LastWriteLocal.ToString("yyyy-MM-dd HH:mm");
    public string ProtectionNote => IsReparsePoint ? "NTFS 联接（禁止穿越）" : (IsProtected ? "红区/白名单保护" : "");
}

public sealed class ZoneSidebarItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isRunning;
    private bool _isDetected;
    private string _procText = "检测进程中...";
    private string _summaryText = "准备扫描...";

    public required ZoneDefinition Definition { get; init; }
    public ZoneId Zone => Definition.Id;
    public string ShortName => Definition.ShortName;
    public string AccentHex => Definition.AccentHex;
    public string IconBgHex => Definition.IconBgHex;
    public string IconPathData => Definition.IconPathData;
    public bool IconFillMode => Definition.IconFillMode;
    public bool IsReadOnlyZone => Definition.IsReadOnlyZone;

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set { if (_isRunning != value) { _isRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(DotColorHex)); } }
    }

    public bool IsDetected
    {
        get => _isDetected;
        set { if (_isDetected != value) { _isDetected = value; OnPropertyChanged(); } }
    }

    public string ProcText
    {
        get => _procText;
        set { if (_procText != value) { _procText = value; OnPropertyChanged(); } }
    }

    public string SummaryText
    {
        get => _summaryText;
        set { if (_summaryText != value) { _summaryText = value; OnPropertyChanged(); } }
    }

    public string DotColorHex => IsRunning ? "#10B981" : (IsReadOnlyZone ? "#D97706" : "#8AA0BC");

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
