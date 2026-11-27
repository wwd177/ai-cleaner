using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AICleaner;

public partial class MainWindow : Window
{
    // Frozen static brushes -> zero per-click / per-selection brush heap churn!
    private static SolidColorBrush FreezeBrush(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    private static readonly SolidColorBrush BrEmerald = FreezeBrush(16, 185, 129);
    private static readonly SolidColorBrush BrSlateDot = FreezeBrush(138, 160, 188);
    private static readonly SolidColorBrush BrAmberText = FreezeBrush(180, 83, 9);
    private static readonly SolidColorBrush BrAmberBar = FreezeBrush(245, 158, 11);
    private static readonly SolidColorBrush BrBlueBar = FreezeBrush(37, 99, 235);
    private static readonly SolidColorBrush BrCyanBar = FreezeBrush(14, 165, 233);
    private static readonly SolidColorBrush BrCyanText = FreezeBrush(3, 105, 161);
    private static readonly SolidColorBrush BrGreenText = FreezeBrush(10, 122, 78);
    private static readonly SolidColorBrush BrDetectBg = FreezeBrush(236, 243, 252);
    private static readonly SolidColorBrush BrDetectBorder = FreezeBrush(200, 223, 248);
    private static readonly SolidColorBrush BrDetectInk = FreezeBrush(71, 98, 130);
    private static readonly SolidColorBrush BrRunBg = FreezeBrush(226, 248, 238);
    private static readonly SolidColorBrush BrRunBorder = FreezeBrush(158, 226, 196);
    private static readonly SolidColorBrush BrRunInk = FreezeBrush(11, 122, 75);
    private static readonly SolidColorBrush BrOffBg = FreezeBrush(233, 236, 255);
    private static readonly SolidColorBrush BrOffBorder = FreezeBrush(188, 197, 252);
    private static readonly SolidColorBrush BrOffInk = FreezeBrush(66, 56, 184);
    private static readonly SolidColorBrush BrInspDefaultBg = FreezeBrush(230, 243, 255);
    private static readonly SolidColorBrush BrInspDefaultBorder = FreezeBrush(167, 208, 252);
    private static readonly SolidColorBrush BrInspDefaultInk = FreezeBrush(27, 91, 184);
    private static readonly SolidColorBrush BrReadOnlyBg = FreezeBrush(230, 247, 247);
    private static readonly SolidColorBrush BrReadOnlyBorder = FreezeBrush(153, 224, 220);
    private static readonly SolidColorBrush BrReadOnlyInk = FreezeBrush(15, 118, 110);
    private static readonly ConcurrentDictionary<string, Brush> HexBrushCache = new(StringComparer.OrdinalIgnoreCase);

    private static Brush GetFrozenHexBrush(string hex, Brush fallback) =>
        HexBrushCache.GetOrAdd(hex, h =>
        {
            try
            {
                if (ColorConverter.ConvertFromString(h) is Color col)
                {
                    var br = new SolidColorBrush(col);
                    br.Freeze();
                    return br;
                }
            }
            catch { }
            return fallback;
        });

    private ZoneId _currentZone = ZoneId.Antigravity;
    private readonly Dictionary<ZoneId, ZoneScanReport> _reports = new();
    private Dictionary<ZoneId, ZoneProcessInfo> _procCache = new();
    private readonly HashSet<ZoneId> _scanningZones = new();
    private readonly Dictionary<ZoneId, CancellationTokenSource> _zoneScanCts = new();
    private readonly Dictionary<ZoneId, ZoneSidebarItem> _sidebarByZone = new();
    private readonly ObservableCollection<ZoneSidebarItem> _activeSidebarZones = new();
    private readonly ObservableCollection<ZoneSidebarItem> _inactiveSidebarZones = new();
    private bool _isCleaning;
    private bool _scheduleInstalled;
    private bool _scheduleLoaded;
    private bool _suppressFilterEvents;
    private long _procRefreshSeq;
    private DateTime _lastActivatedRefreshUtc = DateTime.MinValue;
    private readonly string? _qaCaptureDir;
    private TrayAlertService? _trayService;
    private bool _explicitExitRequested;

    public MainWindow(string? qaCaptureDir = null)
    {
        _qaCaptureDir = qaCaptureDir;
        Loc.SetLanguage(SettingsStore.Load().UiLanguage);
        InitializeComponent();
        GlassDialogs.ApplyIceBlueTitleBar(this);
        InitializeDynamicSidebar();
        ApplyLocalization();
        _trayService = new TrayAlertService(this, () =>
        {
            _explicitExitRequested = true;
            Dispatcher.Invoke(Close);
        });
        _trayService.SyncWithSettings(SettingsStore.Load(), suppressForQa: !string.IsNullOrWhiteSpace(_qaCaptureDir));

        // Never block Windows logoff/shutdown when minimized to tray
        if (Application.Current != null)
        {
            Application.Current.SessionEnding += (_, _) =>
            {
                _explicitExitRequested = true;
                _trayService?.Dispose();
            };
        }

        // Live auto-unlock: when user exits ChatGPT/Antigravity/DSH and switches back,
        // asynchronously re-check processes (~7ms, 0ms UI lag) and auto-unlock Blue offline caches!
        Activated += async (_, _) =>
        {
            if (!IsLoaded || _isCleaning || !string.IsNullOrWhiteSpace(_qaCaptureDir)) return;
            if ((DateTime.UtcNow - _lastActivatedRefreshUtc).TotalSeconds < 2.5) return;
            _lastActivatedRefreshUtc = DateTime.UtcNow;
            await RefreshAllProcessBadgesAsync(syncOfflineLocks: true);
            _ = RefreshRecycleBinAsync();
        };

        Loaded += async (_, _) =>
        {
            RefreshDiskSpace();
            RefreshCumulativeFreed();
            RefreshPolicyButtonLabel();
            UpdateZoneChrome();
            _ = RefreshRecycleBinAsync();

            // Single-pass process check first (~7ms), then pass the snapshot to all parallel zone scans!
            var procs = await RefreshAllProcessBadgesAsync(syncOfflineLocks: false);
            var schedTask = RefreshScheduleStatusAsync();
            var scanTasks = ZoneCatalog.All
                .Select(def => ScanSingleZoneAsync(def.Id, silent: true, preloadedProc: procs[def.Id]))
                .ToList();

            await Task.WhenAll(scanTasks.Append(schedTask));

            if (!string.IsNullOrWhiteSpace(_qaCaptureDir))
            {
                await RunQaVisualCaptureAndBenchAsync(_qaCaptureDir);
                Close();
            }
        };
    }

    private void InitializeDynamicSidebar()
    {
        _activeSidebarZones.Clear();
        _inactiveSidebarZones.Clear();
        _sidebarByZone.Clear();

        foreach (var def in ZoneCatalog.All)
        {
            bool detected = def.DetectInstalled();
            var item = new ZoneSidebarItem
            {
                Definition = def,
                IsDetected = detected,
                IsSelected = def.Id == _currentZone,
                ProcText = def.IsReadOnlyZone ? "只读诊断模式" : "检测进程中...",
                SummaryText = detected ? "准备扫描..." : "未检测到安装目录"
            };
            _sidebarByZone[def.Id] = item;
            if (detected) _activeSidebarZones.Add(item);
            else _inactiveSidebarZones.Add(item);
        }

        if (_activeSidebarZones.Count == 0 && _inactiveSidebarZones.Count > 0)
        {
            // Ensure at least the first zone is visible at top on a completely clean host
            var first = _inactiveSidebarZones[0];
            _inactiveSidebarZones.RemoveAt(0);
            _activeSidebarZones.Add(first);
        }
        if (_activeSidebarZones.Count > 0 && !_activeSidebarZones.Any(x => x.Zone == _currentZone))
        {
            _currentZone = _activeSidebarZones[0].Zone;
            foreach (var it in _sidebarByZone.Values) it.IsSelected = it.Zone == _currentZone;
        }

        ListActiveZones.ItemsSource = _activeSidebarZones;
        ListInactiveZones.ItemsSource = _inactiveSidebarZones;
        ExpUninstalledZones.Visibility = _inactiveSidebarZones.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExpUninstalledZones.Header = $"未检测到 / 其他专区 ({_inactiveSidebarZones.Count})";
    }

    private void RefreshSidebarDetectionGroups()
    {
        _activeSidebarZones.Clear();
        _inactiveSidebarZones.Clear();
        foreach (var def in ZoneCatalog.All)
        {
            if (!_sidebarByZone.TryGetValue(def.Id, out var item)) continue;
            bool detected = def.DetectInstalled();
            item.IsDetected = detected;
            if (detected) _activeSidebarZones.Add(item);
            else _inactiveSidebarZones.Add(item);
        }
        if (_activeSidebarZones.Count == 0 && _inactiveSidebarZones.Count > 0)
        {
            var first = _inactiveSidebarZones[0];
            _inactiveSidebarZones.RemoveAt(0);
            _activeSidebarZones.Add(first);
        }
        ExpUninstalledZones.Visibility = _inactiveSidebarZones.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExpUninstalledZones.Header = $"{Loc.T("UninstalledGroup")} ({_inactiveSidebarZones.Count})";
    }

    private void ApplyLocalization()
    {
        Title = Loc.T("AppTitle");
        TxtBrandName.Text = Loc.T("BrandName");
        TxtBrandSub.Text = Loc.T("BrandSub");
        TxtChooseZone.Text = Loc.T("ChooseZone");
        TxtBtnScanAll.Text = Loc.T("BtnScanAll");
        TxtDiskTitle.Text = Loc.T("DiskTitle");
        TxtDriveCLabel.Text = Loc.T("DriveC");
        TxtRecycleLabel.Text = Loc.T("RecycleBin");
        TxtBtnHistory.Text = Loc.T("BtnHistory");
        TxtBtnSettings.Text = Loc.T("BtnSettings");
        TxtBtnRecycle.Text = Loc.T("BtnRecycleBin");
        TxtBtnRedlines.Text = Loc.T("BtnRedlines");
        TxtBtnOverview.Text = Loc.IsEnglish ? "Overview" : "跨区总览";
        TxtBtnRescan.Text = Loc.T("BtnRescan");
        TxtBtnExport.Text = Loc.T("BtnExport");
        BtnLangToggle.Content = Loc.T("BtnLangToggle");
        TxtKpiSafeLabel.Text = Loc.T("KpiSafeTitle");
        TxtKpiRecycleLabel.Text = Loc.T("KpiRecycleTitle");
        TxtBreakdownLabel.Text = Loc.T("BreakdownLabel");
        RbFilterAll.Content = Loc.T("FilterAll");
        RbFilterSafe.Content = Loc.T("FilterSafe");
        RbFilterRecycle.Content = Loc.T("FilterRecycle");
        RbFilterOffline.Content = Loc.T("FilterOffline");
        RbFilterProtected.Content = Loc.T("FilterProtected");
        TxtSearchPlaceholder.Text = Loc.T("SearchPlaceholder");
        if (CmbSort.Items.Count >= 4)
        {
            ((ComboBoxItem)CmbSort.Items[0]).Content = Loc.T("SortDefault");
            ((ComboBoxItem)CmbSort.Items[1]).Content = Loc.T("SortSize");
            ((ComboBoxItem)CmbSort.Items[2]).Content = Loc.T("SortTime");
            ((ComboBoxItem)CmbSort.Items[3]).Content = Loc.T("SortName");
        }
        BtnSelectSafe.Content = Loc.T("BtnSelectSafe");
        BtnClearSelection.Content = Loc.T("BtnClearSel");
        BtnSpaceMapToolbar.Content = Loc.T("BtnSpaceRadar");
        BtnLocateToolbar.Content = Loc.T("BtnLocate");
        BtnWhitelistToolbar.Content = Loc.T("BtnWhitelist");
        BtnInspectorSpaceMap.Content = Loc.T("BtnInspectRadar");
        BtnInspectorCopy.Content = Loc.T("BtnCopyPath");
        if (GridCandidates.Columns.Count >= 7)
        {
            GridCandidates.Columns[0].Header = Loc.T("ColSelect");
            GridCandidates.Columns[1].Header = Loc.T("ColTier");
            GridCandidates.Columns[2].Header = Loc.T("ColItemPath");
            GridCandidates.Columns[3].Header = Loc.T("ColSize");
            GridCandidates.Columns[4].Header = Loc.T("ColFiles");
            GridCandidates.Columns[5].Header = Loc.T("ColModified");
            GridCandidates.Columns[6].Header = Loc.T("ColReason");
        }
        ExpUninstalledZones.Header = $"{Loc.T("UninstalledGroup")} ({_inactiveSidebarZones.Count})";
        foreach (var item in _sidebarByZone.Values) item.NotifyLocalizationChanged();
        foreach (var rep in _reports.Values)
        {
            foreach (var c in rep.Candidates) c.NotifyLocalizationChanged();
            UpdateSidebarZoneSummary(rep);
            UpdateSingleSideProc(rep.Zone, rep.ProcessInfo);
        }
        foreach (var (z, p) in _procCache)
            UpdateSingleSideProc(z, p);
        _trayService?.RefreshMenuLocalization();
        RefreshDiskSpace();
        RefreshCumulativeFreed();
        _ = RefreshRecycleBinAsync();
        UpdateZoneChrome();
        UpdateGlobalSafeCleanButton();
        RefreshPolicyButtonLabel();
        UpdateCleanButtonState();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isCleaning)
        {
            e.Cancel = true;
            MessageBox.Show(this,
                "正在执行安全清理，为避免中断当前清理批次与审计日志写入，请等待几秒完成后再关闭窗口。",
                "清理进行中", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // When user opted into Tray Low-Space Alert, minimize to tray on window X unless explicitly exiting or running QA capture
        if (!_explicitExitRequested && string.IsNullOrWhiteSpace(_qaCaptureDir) && _trayService is { IsActive: true })
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _trayService?.Dispose();
        _trayService = null;
        foreach (var cts in _zoneScanCts.Values)
        {
            // Cancel only; each running ScanSingleZoneAsync owns and disposes its own CTS in finally.
            try { cts.Cancel(); } catch { }
        }
        _zoneScanCts.Clear();
        base.OnClosing(e);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            TxtSearch.Focus();
            TxtSearch.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !string.IsNullOrEmpty(TxtSearch.Text))
        {
            TxtSearch.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && !_isCleaning && !_scanningZones.Contains(_currentZone))
        {
            BtnRescanZone_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private async Task RunQaVisualCaptureAndBenchAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var bench = new StringBuilder();
        var swProc = Stopwatch.StartNew();
        _ = SafetyGuard.GetAllZoneProcesses();
        swProc.Stop();
        bench.AppendLine($"GetAllZoneProcesses_ms={swProc.ElapsedMilliseconds}");

        foreach (var def in ZoneCatalog.All)
        {
            var swScan = Stopwatch.StartNew();
            _ = await Task.Run(() => ZoneScanner.ScanZone(def.Id));
            swScan.Stop();
            bench.AppendLine($"Scan_{def.Slug}_ms={swScan.ElapsedMilliseconds}");
        }

        // Measure pure UI switching latency across all 7 zones!
        foreach (var z in new[] { ZoneId.GptCodex, ZoneId.DeepSeekHarness, ZoneId.ClaudeCode, ZoneId.AiToolchains, ZoneId.AiIdes, ZoneId.LocalAiEcosystem, ZoneId.Antigravity, ZoneId.GptCodex })
        {
            var swSwitch = Stopwatch.StartNew();
            SwitchToZoneInstant(z);
            UpdateLayout();
            swSwitch.Stop();
            bench.AppendLine($"UiSwitchTo_{ZoneCatalog.Get(z).Slug}_ms={swSwitch.ElapsedMilliseconds}");
        }

        foreach (var def in ZoneCatalog.All)
        {
            SwitchToZoneInstant(def.Id);
            if (GridCandidates.Items.Count > 0)
                GridCandidates.SelectedIndex = 0;
            UpdateLayout();
            await Task.Delay(120);
            SaveVisualPng(Path.Combine(outDir, $"glass-{def.Slug}.png"));
        }

        File.WriteAllText(Path.Combine(outDir, "bench.txt"), bench.ToString(), new UTF8Encoding(false));
    }

    private void SaveVisualPng(string path)
    {
        if (Content is not FrameworkElement root) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = Math.Max(1, (int)Math.Round(root.ActualWidth * dpi.DpiScaleX));
        int h = Math.Max(1, (int)Math.Round(root.ActualHeight * dpi.DpiScaleY));
        var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private void UpdateBusyIndicators(string? status = null)
    {
        bool anyBusy = _isCleaning || _scanningZones.Count > 0;
        bool currentBusy = _isCleaning || _scanningZones.Contains(_currentZone);

        BarBusy.Visibility = anyBusy ? Visibility.Visible : Visibility.Collapsed;
        BarBusy.IsIndeterminate = anyBusy;
        BtnRescanZone.IsEnabled = !currentBusy;
        BtnScanAllZones.IsEnabled = !anyBusy;
        BtnExportReport.IsEnabled = !currentBusy;
        BtnSettingsSide.IsEnabled = !_isCleaning;
        BtnPolicyQuick.IsEnabled = !_isCleaning;
        BtnWhitelistToolbar.IsEnabled = !_isCleaning;
        BtnSpaceMapToolbar.IsEnabled = !_isCleaning;
        BtnToggleSchedule.IsEnabled = !_isCleaning;
        UpdateGlobalSafeCleanButton();
        if (status != null) TxtStatusLine.Text = status;
        UpdateCleanButtonState();
    }

    private void UpdateGlobalSafeCleanButton()
    {
        bool anyBusy = _isCleaning || _scanningZones.Count > 0;
        long totalSafe = 0;
        int totalItems = 0;
        foreach (var rep in _reports.Values)
        {
            var items = rep.Candidates.Where(c => c.Tier == CleanTier.SafeOnline && c.CanSelect).ToList();
            totalItems += items.Count;
            totalSafe += items.Sum(c => c.SizeBytes);
        }
        BtnQuickCleanAllSafe.IsEnabled = !anyBusy && totalItems > 0;
        TxtQuickCleanAllSafe.Text = totalItems > 0
            ? (Loc.IsEnglish ? $"Clean All Green ({CleanCandidate.FormatBytes(totalSafe)})" : $"一键清全区绿区 ({CleanCandidate.FormatBytes(totalSafe)})")
            : Loc.T("BtnQuickSafeCleanDone");
    }

    private async Task RefreshRecycleBinAsync()
    {
        try
        {
            var (bytes, items) = await Task.Run(SafetyGuard.GetRecycleBinStats);
            if (bytes < 0)
            {
                TxtRecycleBinSize.Text = Loc.IsEnglish ? "N/A" : "不可用";
                return;
            }
            if (bytes > 0 || items > 0)
            {
                TxtRecycleBinSize.Text = Loc.IsEnglish
                    ? $"{CleanCandidate.FormatBytes(bytes)} · {items} items"
                    : $"{CleanCandidate.FormatBytes(bytes)} · {items} 项";
                TxtRecycleBinSize.Foreground = bytes >= 5L * 1024 * 1024 * 1024 ? BrAmberText : BrCyanText;
            }
            else
            {
                TxtRecycleBinSize.Text = Loc.IsEnglish ? "Empty (0 B)" : "空 (0 B)";
                TxtRecycleBinSize.Foreground = BrGreenText;
            }
        }
        catch { }
    }

    private void RefreshCumulativeFreed()
    {
        try
        {
            var audits = SafetyGuard.LoadHistoryAudits();
            long total = audits.Sum(a => a.TotalFreedBytes);
            TxtTotalFreed.Text = Loc.IsEnglish
                ? $"Freed {CleanCandidate.FormatBytes(total)}"
                : $"累计释放 {CleanCandidate.FormatBytes(total)}";
        }
        catch { }
    }

    private void RefreshPolicyButtonLabel()
    {
        var s = SettingsStore.Load();
        BtnPolicyQuick.Content = Loc.IsEnglish
            ? $"Policy: {s.TempMinAgeDays}d/{s.WorkspaceMinSizeMB}M"
            : $"阈值: {s.TempMinAgeDays}天/{s.WorkspaceMinSizeMB}M";
    }

    private void RefreshDiskSpace()
    {
        try
        {
            var c = new DriveInfo("C");
            if (c.IsReady)
            {
                double freeGb = c.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                double totalGb = c.TotalSize / (1024.0 * 1024 * 1024);
                double usedPct = (1.0 - freeGb / totalGb) * 100.0;
                bool low = freeGb < 30.0 || usedPct >= 88.0;
                TxtDriveC.Text = Loc.IsEnglish
                    ? (low ? $"Low · {freeGb:F1} GB free" : $"{freeGb:F1} GB free")
                    : (low ? $"偏紧 · 余 {freeGb:F1} GB" : $"余 {freeGb:F1} GB");
                TxtDriveC.Foreground = low ? BrAmberText : (Brush)FindResource("InkSecondary");
                BarDriveC.Value = Math.Clamp(usedPct, 0, 100);
                BarDriveC.Foreground = low ? BrAmberBar : BrBlueBar;
            }
            var dataDrive = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady &&
                            !d.Name.StartsWith("C:", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.TotalSize)
                .FirstOrDefault();

            if (dataDrive != null)
            {
                PanelDrive2.Visibility = Visibility.Visible;
                string letter = dataDrive.Name.TrimEnd('\\', ':');
                TxtDrive2Label.Text = Loc.IsEnglish ? $"Drive {letter} (Data)" : $"{letter} 盘 (数据/工作区)";
                double freeGb = dataDrive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                double totalGb = dataDrive.TotalSize / (1024.0 * 1024 * 1024);
                double usedPct = (1.0 - freeGb / totalGb) * 100.0;
                bool low = freeGb < 30.0 || usedPct >= 88.0;
                TxtDriveF.Text = Loc.IsEnglish
                    ? (low ? $"Low · {freeGb:F1} GB free" : $"{freeGb:F1} GB free")
                    : (low ? $"偏紧 · 余 {freeGb:F1} GB" : $"余 {freeGb:F1} GB");
                TxtDriveF.Foreground = low ? BrAmberText : (Brush)FindResource("InkSecondary");
                BarDriveF.Value = Math.Clamp(usedPct, 0, 100);
                BarDriveF.Foreground = low ? BrAmberBar : BrCyanBar;
            }
            else
            {
                PanelDrive2.Visibility = Visibility.Collapsed;
            }
        }
        catch { }
    }

    private async Task<Dictionary<ZoneId, ZoneProcessInfo>> RefreshAllProcessBadgesAsync(bool syncOfflineLocks = false)
    {
        long mySeq = Interlocked.Increment(ref _procRefreshSeq);
        var all = await Task.Run(SafetyGuard.GetAllZoneProcesses);
        if (mySeq != Volatile.Read(ref _procRefreshSeq))
            return _procCache.Count > 0 ? _procCache : all;

        _procCache = all;
        foreach (var (zone, info) in all)
            UpdateSingleSideProc(zone, info);

        if (syncOfflineLocks)
        {
            string? unlockedNotice = null;
            bool currentChanged = false;
            foreach (var (zone, newProc) in all)
            {
                if (!_reports.TryGetValue(zone, out var rep)) continue;
                bool wasRunning = rep.ProcessInfo.IsRunning;
                rep.ProcessInfo = newProc;
                if (wasRunning != newProc.IsRunning || zone == ZoneId.AiIdes)
                {
                    foreach (var c in rep.Candidates.Where(x => x.Tier == CleanTier.OfflineCache))
                    {
                        bool itemRunning = !string.IsNullOrEmpty(c.ProcessOwnerTag)
                            ? newProc.RunningProcesses.Any(p => p.Equals(c.ProcessOwnerTag, StringComparison.OrdinalIgnoreCase))
                            : newProc.IsRunning;
                        c.ApplyProcessLockState(itemRunning);
                    }

                    UpdateSidebarZoneSummary(rep);
                    if (!newProc.IsRunning)
                        unlockedNotice = $"检测到 {GetZoneName(zone)} 应用已退出，蓝区离线缓存已自动解锁！";
                    if (zone == _currentZone)
                        currentChanged = true;
                }
            }
            if (currentChanged)
            {
                ApplyCurrentReportToView();
                UpdateInspector(GridCandidates.SelectedItem as CleanCandidate);
            }
            if (unlockedNotice != null) TxtStatusLine.Text = unlockedNotice;
        }

        ApplyHeaderProcessBadge();
        return all;
    }

    private void UpdateZoneChrome()
    {
        var def = ZoneCatalog.Get(_currentZone);
        foreach (var it in _sidebarByZone.Values)
            it.IsSelected = it.Zone == _currentZone;

        if (_inactiveSidebarZones.Any(x => x.Zone == _currentZone))
            ExpUninstalledZones.IsExpanded = true;

        BringSelectedZoneCardIntoView(_currentZone);

        BarGptSchedule.Visibility = def.HasScheduledTaskStrip ? Visibility.Visible : Visibility.Collapsed;
        TxtZoneTitle.Text = def.LocalizedDisplayName;
        TxtZoneSubtitle.Text = def.LocalizedSubtitle;
        ApplyCurrentReportToView();
    }

    private void BringSelectedZoneCardIntoView(ZoneId zone)
    {
        if (!_sidebarByZone.TryGetValue(zone, out var item)) return;
        var host = _activeSidebarZones.Contains(item) ? ListActiveZones : ListInactiveZones;
        if (host.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement container)
            container.BringIntoView();
    }

    private void SwitchToZoneInstant(ZoneId target)
    {
        if (_isCleaning) return; // Only block during active destructive cleaning, NEVER during scans!
        if (_currentZone == target && GridCandidates.ItemsSource != null) return;
        _currentZone = target;
        if (RbFilterAll.IsChecked != true)
        {
            // Suppress redundant intermediate ApplyFilter; UpdateZoneChrome() applies the final view ONCE!
            _suppressFilterEvents = true;
            try { RbFilterAll.IsChecked = true; }
            finally { _suppressFilterEvents = false; }
        }
        UpdateZoneChrome();
        if (_scanningZones.Contains(target))
        {
            UpdateBusyIndicators($"正在后台扫描 {GetZoneName(target)}，完成后自动刷新...");
        }
        else if (_reports.TryGetValue(target, out var ready))
        {
            var def = ZoneCatalog.Get(target);
            string msg = def.IsReadOnlyZone
                ? $"{def.DisplayName} 已就绪（严格只读诊断：共盘点 {CleanCandidate.FormatBytes(ready.DiagnosticBytes)}，0 删除候选，耗时 {ready.ElapsedMs} ms）。"
                : $"{def.DisplayName} 已就绪（绿区 {CleanCandidate.FormatBytes(ready.SafeOnlineBytes)}，黄区 {CleanCandidate.FormatBytes(ready.RecycleCandidateBytes)}，蓝区 {CleanCandidate.FormatBytes(ready.OfflineCacheBytes)}，耗时 {ready.ElapsedMs} ms）。";
            if (ready.Status == ScanStatus.Partial)
                msg += $" [部分目录权限受限: {ready.AccessDeniedCount}]";
            UpdateBusyIndicators(msg);
        }
        else
        {
            UpdateBusyIndicators(null);
        }
    }

    private async Task ScanSingleZoneAsync(ZoneId zone, bool silent = false, ZoneProcessInfo? preloadedProc = null)
    {
        // Cancel any previous in-flight scan without disposing its CTS (the previous owner disposes its own CTS in finally!)
        if (_zoneScanCts.TryGetValue(zone, out var prevCts))
        {
            try { prevCts.Cancel(); } catch { }
        }
        var cts = new CancellationTokenSource();
        _zoneScanCts[zone] = cts;
        var ct = cts.Token;

        bool IsCurrentOwner() => _zoneScanCts.TryGetValue(zone, out var cur) && ReferenceEquals(cur, cts);

        _scanningZones.Add(zone);
        string zoneLabel = GetZoneName(zone);
        if (!silent || _currentZone == zone)
            UpdateBusyIndicators($"正在扫描 {zoneLabel}（只读体检，不阻塞切换）...");
        else
            UpdateBusyIndicators();

        HashSet<string>? prevSelectedKeys = null;
        if (_reports.TryGetValue(zone, out var oldRep))
        {
            var keys = oldRep.Candidates.Where(c => c.IsSelected && c.CanSelect).Select(c => c.StableKey).ToList();
            if (keys.Count > 0) prevSelectedKeys = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        }

        string? finalStatusMsg = null;
        try
        {
            long seqAtStart = Volatile.Read(ref _procRefreshSeq);
            var report = await Task.Run(() => ZoneScanner.ScanZone(zone, preloadedProc, ct), ct);
            if (!IsCurrentOwner() || ct.IsCancellationRequested || report.Status == ScanStatus.Cancelled)
                return;

            // Reconcile commit order: NEVER let stale scan status overwrite fresher live process locks!
            if (Volatile.Read(ref _procRefreshSeq) > seqAtStart && _procCache.TryGetValue(zone, out var latestProc))
            {
                report.ProcessInfo = latestProc;
                foreach (var c in report.Candidates.Where(x => x.Tier == CleanTier.OfflineCache))
                {
                    bool itemRunning = !string.IsNullOrEmpty(c.ProcessOwnerTag)
                        ? latestProc.RunningProcesses.Any(p => p.Equals(c.ProcessOwnerTag, StringComparison.OrdinalIgnoreCase))
                        : latestProc.IsRunning;
                    c.ApplyProcessLockState(itemRunning);
                }
            }
            else
            {
                _procCache[zone] = report.ProcessInfo;
            }

            if (prevSelectedKeys != null)
            {
                foreach (var c in report.Candidates)
                {
                    if (c.CanSelect && prevSelectedKeys.Contains(c.StableKey))
                        c.IsSelected = true;
                }
            }

            _reports[zone] = report;
            UpdateSidebarZoneSummary(report);
            UpdateSingleSideProc(zone, report.ProcessInfo);
            RefreshDiskSpace();

            if (_currentZone == zone)
                ApplyCurrentReportToView();

            string statusSuffix = report.Status == ScanStatus.Partial ? $"（权限受限 {report.AccessDeniedCount} 处）" : "";
            finalStatusMsg = _scanningZones.Count <= 1
                ? $"全部专区已就绪（{zoneLabel}：耗时 {report.ElapsedMs} ms{statusSuffix}）。"
                : $"{zoneLabel} 扫描完成，其余专区仍在后台扫描中...";
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer scan or window closing; finally cleans up if still current owner
        }
        catch (Exception ex)
        {
            if (IsCurrentOwner())
                finalStatusMsg = $"{zoneLabel} 扫描出错：{ex.Message}";
        }
        finally
        {
            if (IsCurrentOwner())
            {
                _zoneScanCts.Remove(zone);
                _scanningZones.Remove(zone);
                UpdateBusyIndicators(finalStatusMsg);
            }
            try { cts.Dispose(); } catch { }
        }
    }

    private void UpdateSingleSideProc(ZoneId zone, ZoneProcessInfo info)
    {
        if (!_sidebarByZone.TryGetValue(zone, out var item)) return;
        item.IsRunning = info.IsRunning;
        bool en = Loc.IsEnglish;
        if (item.IsReadOnlyZone)
        {
            item.ProcText = info.IsRunning
                ? (en ? $"Running ({info.RunningPids.Count}) · Read-Only" : $"运行中 ({info.RunningPids.Count} 进程) · 只读诊断")
                : (en ? "Stopped · Read-Only Radar" : "已退出 · 只读安全诊断");
        }
        else if (info.IsRunning)
        {
            item.ProcText = en
                ? $"Running ({info.RunningPids.Count}) · Offline Locked"
                : $"运行中 ({info.RunningPids.Count} 进程) · 离线项已锁";
        }
        else
        {
            item.ProcText = en ? "Stopped · Offline Ready" : "已退出 · 可清离线缓存";
        }
    }

    private void UpdateSidebarZoneSummary(ZoneScanReport report)
    {
        if (!_sidebarByZone.TryGetValue(report.Zone, out var item)) return;
        item.IsDetected = report.IsDetected;
        bool en = Loc.IsEnglish;
        if (report.Status == ScanStatus.Error)
        {
            item.SummaryText = en ? "Scan Restricted" : "扫描受限";
            return;
        }
        if (item.IsReadOnlyZone)
        {
            item.SummaryText = report.DiagnosticBytes > 0
                ? (en ? $"Read-Only · {CleanCandidate.FormatBytes(report.DiagnosticBytes)}" : $"只读诊断 · 占用 {CleanCandidate.FormatBytes(report.DiagnosticBytes)}")
                : (report.IsDetected ? (en ? "Read-Only · 0 large dirs" : "只读诊断 · 0 项大目录") : (en ? "Not detected" : "未检测到安装目录"));
            return;
        }
        // Uses CleanableOfflineCacheBytes so per-IDE locking in AiIdes counts exited IDEs accurately!
        long totalCleanable = report.SafeOnlineBytes + report.RecycleCandidateBytes + report.CleanableOfflineCacheBytes;
        if (totalCleanable > 0)
            item.SummaryText = en
                ? $"Reclaim {CleanCandidate.FormatBytes(totalCleanable)}"
                : $"可清理 {CleanCandidate.FormatBytes(totalCleanable)}（含回收站项）";
        else if (report.DiagnosticBytes > 0)
            item.SummaryText = en
                ? $"Radar {CleanCandidate.FormatBytes(report.DiagnosticBytes)} · 0 B clean"
                : $"只读雷达 {CleanCandidate.FormatBytes(report.DiagnosticBytes)} · 可清 0 B";
        else
            item.SummaryText = report.IsDetected
                ? (en ? "Clean (0 B reclaimable)" : "已洁净（可清理 0 B）")
                : (en ? "Not detected" : "未检测到安装目录");
    }

    private void ApplyHeaderProcessBadge()
    {
        var def = ZoneCatalog.Get(_currentZone);
        bool en = Loc.IsEnglish;
        ZoneProcessInfo? proc = null;
        if (_reports.TryGetValue(_currentZone, out var rep))
            proc = rep.ProcessInfo;
        else if (_procCache.TryGetValue(_currentZone, out var cached))
            proc = cached;

        if (def.IsReadOnlyZone)
        {
            BadgeProcStatus.Background = BrReadOnlyBg;
            BadgeProcStatus.BorderBrush = BrReadOnlyBorder;
            TxtProcBadge.Foreground = BrReadOnlyInk;
            TxtProcBadge.Text = proc is { IsRunning: true }
                ? (en ? $"Running ({proc.RunningPids.Count}) · Read-Only (0 Deletes)" : $"运行中 ({proc.RunningPids.Count} 进程) · 只读诊断模式（0 删除）")
                : (en ? "Read-Only Mode (Zero Deletions)" : "只读诊断模式（绝不执行删除）");
            return;
        }

        if (proc == null)
        {
            BadgeProcStatus.Background = BrDetectBg;
            BadgeProcStatus.BorderBrush = BrDetectBorder;
            TxtProcBadge.Foreground = BrDetectInk;
            TxtProcBadge.Text = en ? "Checking processes..." : "检测进程中...";
            return;
        }

        if (proc.IsRunning)
        {
            BadgeProcStatus.Background = BrRunBg;
            BadgeProcStatus.BorderBrush = BrRunBorder;
            TxtProcBadge.Foreground = BrRunInk;
            string procHint = _currentZone == ZoneId.AiIdes && proc.RunningProcesses.Count > 0
                ? $" [{string.Join("/", proc.RunningProcesses)}]"
                : "";
            TxtProcBadge.Text = en
                ? $"Running ({proc.RunningPids.Count}){procHint} · Safe Online Mode"
                : $"运行中 ({proc.RunningPids.Count} 进程){procHint} · 在线安全模式";
        }
        else
        {
            BadgeProcStatus.Background = BrOffBg;
            BadgeProcStatus.BorderBrush = BrOffBorder;
            TxtProcBadge.Foreground = BrOffInk;
            TxtProcBadge.Text = en ? "Stopped · Online & Offline Ready" : "已退出 · 在线/离线均可清理";
        }
    }

    private void ApplyCurrentReportToView()
    {
        ApplyHeaderProcessBadge();

        if (!_reports.TryGetValue(_currentZone, out var rep))
        {
            bool scanning = _scanningZones.Contains(_currentZone);
            TxtKpiSafe.Text = scanning ? "扫描中..." : "未扫描";
            TxtKpiSafeSub.Text = scanning ? "后台高速体检中，可自由切换" : "点击右上角「重新扫描本区」";
            TxtKpiRecycle.Text = scanning ? "扫描中..." : "未扫描";
            TxtKpiRecycleSub.Text = "默认不自动勾选";
            TxtKpiOffline.Text = scanning ? "扫描中..." : "未扫描";
            TxtKpiOfflineSub.Text = "读取缓存状态...";
            GridCandidates.ItemsSource = null;
            UpdateBreakdownBar(null);
            UpdateInspector(null);
            UpdateCleanButtonState();
            return;
        }

        var def = ZoneCatalog.Get(_currentZone);
        int safeCnt = rep.Candidates.Count(c => c.Tier == CleanTier.SafeOnline && c.CanSelect && !c.IsReadOnlyDiagnostic);
        int recCnt = rep.Candidates.Count(c => c.Tier == CleanTier.RecycleCandidate && c.CanSelect && !c.IsReadOnlyDiagnostic);
        int offCnt = rep.Candidates.Count(c => c.Tier == CleanTier.OfflineCache && !c.IsReadOnlyDiagnostic);
        int diagCnt = rep.Candidates.Count(c => c.IsReadOnlyDiagnostic);
        int recoCnt = rep.Candidates.Count(c => c.IsRecommended && c.CanSelect && !c.IsReadOnlyDiagnostic);

        TxtKpiSafe.Text = CleanCandidate.FormatBytes(rep.SafeOnlineBytes);
        TxtKpiSafeSub.Text = def.IsReadOnlyZone
            ? (Loc.IsEnglish ? "Read-only zone · 0 deletable" : "只读诊断专区 · 0 删除候选")
            : (Loc.IsEnglish ? $"{safeCnt} items · Clean while running" : $"{safeCnt} 项 · 应用开着即可清");

        TxtKpiRecycle.Text = CleanCandidate.FormatBytes(rep.RecycleCandidateBytes);
        TxtKpiRecycleSub.Text = def.IsReadOnlyZone
            ? (Loc.IsEnglish ? "Protected · Never auto-recycled" : "受保护 · 绝不自动回收")
            : (Loc.IsEnglish ? $"{recCnt} items · Reversible Recycle Bin" : $"{recCnt} 项 · 移入回收站可恢复");

        bool showDiagInKpi3 = def.IsReadOnlyZone || (offCnt == 0 && diagCnt > 0);
        if (showDiagInKpi3)
        {
            TxtKpiOfflineLabel.Text = Loc.T("KpiDiagTitle");
            TxtKpiOffline.Text = CleanCandidate.FormatBytes(rep.DiagnosticBytes);
            TxtKpiOfflineSub.Text = Loc.IsEnglish
                ? $"{diagCnt} items · Click Space Radar to inspect"
                : $"{diagCnt} 项 · 右键/点「空间雷达」下钻查看";
        }
        else
        {
            TxtKpiOfflineLabel.Text = Loc.T("KpiOfflineTitle");
            TxtKpiOffline.Text = CleanCandidate.FormatBytes(rep.OfflineCacheBytes);
            TxtKpiOfflineSub.Text = rep.ProcessInfo.IsRunning
                ? (Loc.IsEnglish ? $"{offCnt} items · Locked while app runs" : $"{offCnt} 项 · 应用运行中已自动加锁")
                : (Loc.IsEnglish ? $"{offCnt} items · App stopped, ready to clean" : $"{offCnt} 项 · 应用已退出，可勾选清理");
        }

        BtnSelectRecommended.Content = recoCnt > 0
            ? (Loc.IsEnglish ? $"Select Recommended ({recoCnt})" : $"勾选高置信推荐 ({recoCnt})")
            : Loc.T("BtnSelectReco");

        UpdateBreakdownBar(rep);
        UpdateGlobalSafeCleanButton();
        ApplyFilter();
    }

    private void UpdateBreakdownBar(ZoneScanReport? rep)
    {
        bool en = Loc.IsEnglish;
        if (rep == null)
        {
            ColBarSafe.Width = new GridLength(1, GridUnitType.Star);
            ColBarRecycle.Width = new GridLength(1, GridUnitType.Star);
            ColBarOffline.Width = new GridLength(1, GridUnitType.Star);
            TxtBreakdownSummary.Text = en ? "Calculating space breakdown..." : "正在统计空间分布...";
            return;
        }

        long s = Math.Max(0, rep.SafeOnlineBytes);
        long r = Math.Max(0, rep.RecycleCandidateBytes);
        long o = Math.Max(0, rep.OfflineCacheBytes);
        long total = s + r + o;

        if (total <= 0)
        {
            ColBarSafe.Width = new GridLength(rep.DiagnosticBytes > 0 ? 0 : 1, GridUnitType.Star);
            ColBarRecycle.Width = new GridLength(0, GridUnitType.Star);
            ColBarOffline.Width = new GridLength(rep.DiagnosticBytes > 0 ? 1 : 0, GridUnitType.Star);
            TxtBreakdownSummary.Text = rep.DiagnosticBytes > 0
                ? (en ? $"Read-only radar total {CleanCandidate.FormatBytes(rep.DiagnosticBytes)} (never auto-deleted)"
                      : $"只读雷达总计 {CleanCandidate.FormatBytes(rep.DiagnosticBytes)}（严禁自动删除，不计入可释放量）")
                : (en ? "This zone is clean (0 B reclaimable)" : "当前专区已非常洁净（0 B 待清理）");
            return;
        }

        ColBarSafe.Width = new GridLength(s > 0 ? Math.Max(0.03, (double)s / total) : 0, GridUnitType.Star);
        ColBarRecycle.Width = new GridLength(r > 0 ? Math.Max(0.03, (double)r / total) : 0, GridUnitType.Star);
        ColBarOffline.Width = new GridLength(o > 0 ? Math.Max(0.03, (double)o / total) : 0, GridUnitType.Star);

        // Uses CleanableOfflineCacheBytes for accurate per-IDE unlocked accounting!
        long nowCleanable = s + r + Math.Max(0, rep.CleanableOfflineCacheBytes);
        string diagNote = rep.DiagnosticBytes > 0
            ? (en ? $" · Radar {CleanCandidate.FormatBytes(rep.DiagnosticBytes)}" : $" · 只读雷达 {CleanCandidate.FormatBytes(rep.DiagnosticBytes)}")
            : "";
        TxtBreakdownSummary.Text = en
            ? $"Reclaim now {CleanCandidate.FormatBytes(nowCleanable)} / Total {CleanCandidate.FormatBytes(total)} (G {CleanCandidate.FormatBytes(s)} · Y {CleanCandidate.FormatBytes(r)} · B {CleanCandidate.FormatBytes(o)}{diagNote})"
            : $"可立即释放 {CleanCandidate.FormatBytes(nowCleanable)} / 总发现 {CleanCandidate.FormatBytes(total)}（绿 {CleanCandidate.FormatBytes(s)} · 黄 {CleanCandidate.FormatBytes(r)} · 蓝 {CleanCandidate.FormatBytes(o)}{diagNote}）";
    }

    private void UpdateInspector(CleanCandidate? item)
    {
        if (item == null)
        {
            BadgeInspectorTier.Background = BrInspDefaultBg;
            BadgeInspectorTier.BorderBrush = BrInspDefaultBorder;
            TxtInspectorTier.Foreground = BrInspDefaultInk;
            TxtInspectorTier.Text = Loc.T("InspectorDefaultTitle");
            TxtInspectorPath.Text = Loc.T("InspectorDefaultPath");
            TxtInspectorVerdict.Text = Loc.T("InspectorDefaultVerdict");
            BtnInspectorCopy.IsEnabled = false;
            return;
        }

        BadgeInspectorTier.Background = GetFrozenHexBrush(item.TierColorHex, BrInspDefaultBg);
        BadgeInspectorTier.BorderBrush = GetFrozenHexBrush(item.TierBorderHex, BrInspDefaultBorder);
        TxtInspectorTier.Foreground = GetFrozenHexBrush(item.TierInkHex, BrInspDefaultInk);

        TxtInspectorTier.Text = item.TierBadge;
        TxtInspectorPath.Text = item.Path;
        string statsSuffix = item.JunctionCount > 0 || item.RecentSkippedCount > 0
            ? (Loc.IsEnglish ? $" (Protected skips: recent {item.RecentSkippedCount} / junctions {item.JunctionCount})" : $"（自动保护跳过：近期 {item.RecentSkippedCount} / 联接 {item.JunctionCount}）")
            : "";
        TxtInspectorVerdict.Text = item.VerdictImpactText + statsSuffix;
        BtnInspectorCopy.IsEnabled = !string.IsNullOrWhiteSpace(item.Path);
    }

    private void ApplyFilter()
    {
        if (!_reports.TryGetValue(_currentZone, out var rep))
        {
            GridCandidates.ItemsSource = null;
            UpdateCleanButtonState();
            return;
        }

        IEnumerable<CleanCandidate> filtered = rep.Candidates;
        if (RbFilterSafe.IsChecked == true)
            filtered = filtered.Where(c => c.Tier == CleanTier.SafeOnline);
        else if (RbFilterRecycle.IsChecked == true)
            filtered = filtered.Where(c => c.Tier == CleanTier.RecycleCandidate);
        else if (RbFilterOffline.IsChecked == true)
            filtered = filtered.Where(c => c.Tier == CleanTier.OfflineCache);
        else if (RbFilterProtected.IsChecked == true)
            filtered = filtered.Where(c => c.Tier == CleanTier.ProtectedSkipped);

        string kw = (TxtSearch.Text ?? "").Trim();
        if (!string.IsNullOrEmpty(kw))
        {
            filtered = filtered.Where(c =>
                c.Title.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                c.Path.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                c.Reason.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                c.Category.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }

        filtered = CmbSort.SelectedIndex switch
        {
            1 => filtered.OrderByDescending(c => c.SizeBytes),
            2 => filtered.OrderBy(c => c.NewestWriteLocal),
            3 => filtered.OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderBy(c => c.Tier).ThenByDescending(c => c.IsRecommended).ThenByDescending(c => c.SizeBytes)
        };

        GridCandidates.ItemsSource = filtered.ToList();
        UpdateInspector(GridCandidates.SelectedItem as CleanCandidate);
        UpdateCleanButtonState();
    }

    private void UpdateCleanButtonState()
    {
        if (_isCleaning || _scanningZones.Contains(_currentZone) || !_reports.TryGetValue(_currentZone, out var rep))
        {
            BtnCleanSelected.IsEnabled = false;
            BtnCleanSelected.Content = Loc.T("BtnCleanZero");
            return;
        }
        var selected = rep.Candidates.Where(c => c.IsSelected && c.CanSelect).ToList();
        long bytes = selected.Sum(c => c.SizeBytes);
        BtnCleanSelected.IsEnabled = selected.Count > 0;
        BtnCleanSelected.Content = selected.Count > 0
            ? (Loc.IsEnglish ? $"Clean Selected ({selected.Count} · {CleanCandidate.FormatBytes(bytes)})" : $"执行已勾选清理 ({selected.Count} 项 · {CleanCandidate.FormatBytes(bytes)})")
            : Loc.T("BtnCleanZero");
    }

    private async Task RefreshScheduleStatusAsync()
    {
        var (installed, summary) = await Task.Run(TaskSchedulerHelper.GetStatus);
        _scheduleInstalled = installed;
        _scheduleLoaded = true;
        TxtScheduleStatus.Text = summary;
        BtnToggleSchedule.Content = installed ? "停用定时任务" : "启用每周定时任务";
    }

    // === Instant Non-Blocking Dynamic Zone Switching ===
    private void ZoneCard_Dynamic_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ZoneId target) return;
        SwitchToZoneInstant(target);
        if (ZoneCatalog.Get(target).HasScheduledTaskStrip && !_scheduleLoaded)
            _ = RefreshScheduleStatusAsync();
        if (!_reports.ContainsKey(target) && !_scanningZones.Contains(target))
            _ = ScanSingleZoneAsync(target);
    }

    private async void BtnRescanZone_Click(object sender, RoutedEventArgs e)
    {
        if (ZoneCatalog.Get(_currentZone).HasScheduledTaskStrip)
            _ = RefreshScheduleStatusAsync();
        await ScanSingleZoneAsync(_currentZone);
    }

    private async void BtnScanAllZones_Click(object sender, RoutedEventArgs e)
    {
        if (_isCleaning || _scanningZones.Count > 0) return;
        var procs = await RefreshAllProcessBadgesAsync(syncOfflineLocks: false);
        await Task.WhenAll(ZoneCatalog.All.Select(def =>
            ScanSingleZoneAsync(def.Id, silent: true, preloadedProc: procs[def.Id])));
        ApplyCurrentReportToView();
        UpdateBusyIndicators($"全部 {ZoneCatalog.All.Count} 个专区并行扫描完成。可随时瞬间切换专区并勾选清理。");
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && !_suppressFilterEvents) ApplyFilter();
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        TxtSearchPlaceholder.Visibility = string.IsNullOrEmpty(TxtSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded) ApplyFilter();
    }

    private void CmbSort_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ApplyFilter();
    }

    private void KpiSafe_Click(object sender, MouseButtonEventArgs e) => RbFilterSafe.IsChecked = true;
    private void KpiRecycle_Click(object sender, MouseButtonEventArgs e) => RbFilterRecycle.IsChecked = true;
    private void KpiOffline_Click(object sender, MouseButtonEventArgs e)
    {
        var def = ZoneCatalog.Get(_currentZone);
        bool diagMode = def.IsReadOnlyZone;
        if (!diagMode && _reports.TryGetValue(_currentZone, out var rep))
        {
            int offCnt = rep.Candidates.Count(c => c.Tier == CleanTier.OfflineCache && !c.IsReadOnlyDiagnostic);
            int diagCnt = rep.Candidates.Count(c => c.IsReadOnlyDiagnostic);
            diagMode = offCnt == 0 && diagCnt > 0;
        }
        if (diagMode) RbFilterProtected.IsChecked = true;
        else RbFilterOffline.IsChecked = true;
    }

    private void ItemCheckbox_Changed(object sender, RoutedEventArgs e) => UpdateCleanButtonState();

    private void BtnSelectSafe_Click(object sender, RoutedEventArgs e)
    {
        if (GridCandidates.ItemsSource is not IEnumerable<CleanCandidate> visible) return;
        int cnt = 0;
        foreach (var c in visible)
        {
            if (c.Tier == CleanTier.SafeOnline && c.CanSelect) { c.IsSelected = true; cnt++; }
        }
        UpdateCleanButtonState();
        TxtStatusLine.Text = cnt > 0
            ? $"已勾选当前视图中 {cnt} 项绿区在线安全缓存（不含被筛选隐藏的项目）。"
            : "当前视图中暂无可勾选的绿区在线安全项。";
    }

    private void BtnSelectRecommended_Click(object sender, RoutedEventArgs e)
    {
        if (GridCandidates.ItemsSource is not IEnumerable<CleanCandidate> visible) return;
        int cnt = 0;
        foreach (var c in visible)
        {
            if (c.IsRecommended && c.CanSelect) { c.IsSelected = true; cnt++; }
        }
        UpdateCleanButtonState();
        TxtStatusLine.Text = cnt > 0
            ? $"已勾选当前视图中 {cnt} 项高置信推荐清理项（大目录均走 Windows 回收站，点击右侧主按钮确认后执行）。"
            : "当前视图中暂无额外高置信推荐项。";
    }

    private void BtnClearSelection_Click(object sender, RoutedEventArgs e)
    {
        if (!_reports.TryGetValue(_currentZone, out var rep)) return;
        foreach (var c in rep.Candidates) c.IsSelected = false;
        UpdateCleanButtonState();
    }

    private void BtnLocateSelected_Click(object sender, RoutedEventArgs e)
    {
        if (GridCandidates.SelectedItem is not CleanCandidate item)
        {
            MessageBox.Show(this, "请先在列表中单击选中一行，或直接双击该行打开所在目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OpenInExplorer(item.Path);
    }

    private async void BtnWhitelistSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_isCleaning) return;
        if (GridCandidates.SelectedItem is not CleanCandidate item)
        {
            MessageBox.Show(this, "请先在列表中选中要永久保留的项目，再点击「加入白名单」。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await AddCandidateToWhitelistAsync(item);
    }

    private void GridCandidates_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateInspector(GridCandidates.SelectedItem as CleanCandidate);
    }

    private void GridCandidates_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not DataGridRow)
            dep = VisualTreeHelper.GetParent(dep);
        if (dep is DataGridRow row && row.Item is CleanCandidate item)
            OpenInExplorer(item.Path);
    }

    private async void BtnSpaceMap_Click(object sender, RoutedEventArgs e)
    {
        if (_isCleaning) return;
        string targetPath;
        string title;
        if (GridCandidates.SelectedItem is CleanCandidate item && (Directory.Exists(item.Path) || File.Exists(item.Path)))
        {
            targetPath = item.Path;
            title = item.Title;
        }
        else
        {
            var def = ZoneCatalog.Get(_currentZone);
            targetPath = def.ResolveDefaultRadarPath(SettingsStore.Load());
            title = def.DisplayName;
        }

        if (!Directory.Exists(targetPath) && !File.Exists(targetPath))
        {
            MessageBox.Show(this, $"目标路径不存在：{targetPath}", "空间雷达", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (GlassDialogs.ShowSpaceMapDialog(this, targetPath, title))
        {
            await ScanSingleZoneAsync(_currentZone);
        }
    }

    private async void BtnQuickCleanAllSafe_Click(object sender, RoutedEventArgs e)
    {
        if (_isCleaning || _scanningZones.Count > 0) return;

        var allSafe = new List<(ZoneId Zone, CleanCandidate Item)>();
        foreach (var def in ZoneCatalog.All.Where(d => !d.IsReadOnlyZone))
        {
            if (!_reports.TryGetValue(def.Id, out var rep)) continue;
            foreach (var c in rep.Candidates.Where(x => x.Tier == CleanTier.SafeOnline && x.CanSelect && !x.IsReadOnlyDiagnostic))
                allSafe.Add((def.Id, c));
        }

        if (allSafe.Count == 0)
        {
            MessageBox.Show(this, "当前所有专区的绿区在线安全缓存均为 0，无需清理。", "一键清理全区绿区", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        long totalBytes = allSafe.Sum(x => x.Item.SizeBytes);
        var sb = new StringBuilder();
        sb.AppendLine($"即将一键清理全部可写专区的【绿区 · 在线安全】项目（共 {allSafe.Count} 项，预计释放 {CleanCandidate.FormatBytes(totalBytes)}）：");
        sb.AppendLine();
        foreach (var (z, it) in allSafe.Take(14))
            sb.AppendLine($"• [{GetZoneName(z)}] [{it.SizeText}] {it.Title}");
        if (allSafe.Count > 14) sb.AppendLine($"... 等共 {allSafe.Count} 项");
        sb.AppendLine();
        sb.AppendLine("安全承诺：仅清理绿区安全项（绝不碰只读诊断区、黄区工作区、蓝区离线缓存与红区）；绝不关闭运行中的 AI 桌面端，安装包残留与临时目录均移入 Windows 回收站。");

        if (MessageBox.Show(this, sb.ToString(), "确认一键清理全区绿区", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _isCleaning = true;
        UpdateBusyIndicators($"正在跨区执行绿区安全清理（共 {allSafe.Count} 项）...");
        try
        {
            long grandFreed = 0;
            int grandOk = 0;
            int grandBlocked = 0;
            var touchedZones = allSafe.Select(x => x.Zone).Distinct().ToList();

            await Task.Run(() =>
            {
                foreach (var z in touchedZones)
                {
                    var items = allSafe.Where(x => x.Zone == z).Select(x => x.Item).ToList();
                    var audit = new CleanRunAudit { Zone = z.ToString(), SelectedCount = items.Count };
                    foreach (var it in items)
                    {
                        var outcome = SafetyGuard.ExecuteCandidate(it);
                        audit.Outcomes.Add(outcome);
                        audit.TotalFreedBytes += outcome.FreedBytes;
                        grandFreed += outcome.FreedBytes;
                        if (outcome.Succeeded) grandOk++;
                        if (outcome.Blocked) grandBlocked++;
                        Dispatcher.Invoke(() =>
                        {
                            it.StatusText = outcome.Message;
                            it.IsSelected = false;
                        });
                    }
                    SafetyGuard.WriteAuditLog(audit);
                }
            });

            RefreshCumulativeFreed();
            RefreshDiskSpace();
            _ = RefreshRecycleBinAsync();
            _isCleaning = false;
            UpdateBusyIndicators($"全区绿区清理完成：成功 {grandOk} 项，拦截/跳过 {grandBlocked} 项，共释放 {CleanCandidate.FormatBytes(grandFreed)}。");

            await Task.WhenAll(touchedZones.Select(z => ScanSingleZoneAsync(z, silent: true)));
            ApplyCurrentReportToView();

            MessageBox.Show(this,
                $"全区绿区一键清理已完成！\n\n• 涉及专区：{touchedZones.Count} 个\n• 成功：{grandOk} 项\n• 拦截/跳过：{grandBlocked} 项\n• 共释放/移入回收站：{CleanCandidate.FormatBytes(grandFreed)}",
                "全区绿区清理完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _isCleaning = false;
            UpdateBusyIndicators($"跨区清理异常：{ex.Message}");
            MessageBox.Show(this, ex.Message, "清理出错", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MenuOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (GridCandidates.SelectedItem is CleanCandidate item) OpenInExplorer(item.Path);
    }

    private void MenuCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (GridCandidates.SelectedItem is CleanCandidate item)
        {
            try { Clipboard.SetText(item.Path); TxtStatusLine.Text = $"已复制路径：{item.Path}"; } catch { }
        }
    }

    private async void MenuAddWhitelist_Click(object sender, RoutedEventArgs e)
    {
        if (_isCleaning) return;
        if (GridCandidates.SelectedItem is CleanCandidate item)
            await AddCandidateToWhitelistAsync(item);
    }

    private async void MenuRemoveWhitelist_Click(object sender, RoutedEventArgs e)
    {
        if (_isCleaning) return;
        if (GridCandidates.SelectedItem is not CleanCandidate item) return;
        if (SettingsStore.RemoveWhitelist(item.Path))
        {
            TxtStatusLine.Text = $"已移出白名单：{item.Title}，正在刷新...";
            await ScanSingleZoneAsync(_currentZone);
        }
        else
        {
            TxtStatusLine.Text = "该项目不在用户白名单中（红区系统保护不可移出）。";
        }
    }

    private async Task AddCandidateToWhitelistAsync(CleanCandidate item)
    {
        if (item.ActionType == CleanActionType.RecycleBatchDirectories)
        {
            MessageBox.Show(this, "该项为多个临时缓存目录汇总，无需加入白名单。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (SettingsStore.AddWhitelist(item.Path))
        {
            TxtStatusLine.Text = $"已将「{item.Title}」加入永久保留白名单，正在刷新...";
            await ScanSingleZoneAsync(_currentZone);
        }
        else
        {
            TxtStatusLine.Text = $"「{item.Title}」已在白名单中。";
        }
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private async void BtnCleanSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_isCleaning || _scanningZones.Contains(_currentZone) || !_reports.TryGetValue(_currentZone, out var rep)) return;
        var selected = rep.Candidates.Where(c => c.IsSelected && c.CanSelect).ToList();
        if (selected.Count == 0) return;

        var visibleSet = (GridCandidates.ItemsSource as IEnumerable<CleanCandidate>)?.ToHashSet() ?? new HashSet<CleanCandidate>();
        int hiddenSelected = selected.Count(c => !visibleSet.Contains(c));

        long totalBytes = selected.Sum(c => c.SizeBytes);
        var sb = new StringBuilder();
        sb.AppendLine($"即将清理【{GetZoneName(_currentZone)}】已勾选的 {selected.Count} 项（预计释放 {CleanCandidate.FormatBytes(totalBytes)}）：");
        if (hiddenSelected > 0)
            sb.AppendLine($"注意：其中 {hiddenSelected} 项当前处于筛选/搜索隐藏状态，请确认下方完整清单。");
        sb.AppendLine();
        foreach (var s in selected.Take(12))
        {
            sb.AppendLine($"• [{s.SizeText}] {s.Title}");
            sb.AppendLine($"   方式：{s.ActionHint}");
        }
        if (selected.Count > 12) sb.AppendLine($"... 等共 {selected.Count} 项");
        sb.AppendLine();
        sb.AppendLine("安全承诺：绝不关闭运行中的 AI 桌面端，自动跳过被占用文件与全部目录联接；工作区/归档/安装包残留均移入 Windows 回收站可随时还原。");

        var confirm = MessageBox.Show(this, sb.ToString(), "确认执行安全清理", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _isCleaning = true;
        UpdateBusyIndicators($"正在执行清理（共 {selected.Count} 项）...");
        try
        {
            var audit = new CleanRunAudit
            {
                Zone = _currentZone.ToString(),
                SelectedCount = selected.Count
            };

            await Task.Run(() =>
            {
                foreach (var item in selected)
                {
                    var outcome = SafetyGuard.ExecuteCandidate(item);
                    audit.Outcomes.Add(outcome);
                    audit.TotalFreedBytes += outcome.FreedBytes;
                    Dispatcher.Invoke(() =>
                    {
                        item.StatusText = outcome.Message;
                        item.IsSelected = false;
                    });
                }
            });

            string logPath = SafetyGuard.WriteAuditLog(audit);
            RefreshCumulativeFreed();
            _ = RefreshRecycleBinAsync();
            TxtLastAuditHint.Text = $"最新日志：{Path.GetFileName(logPath)}";
            int okCnt = audit.Outcomes.Count(o => o.Succeeded);
            int blockedCnt = audit.Outcomes.Count(o => o.Blocked);

            _isCleaning = false;
            UpdateBusyIndicators($"清理完成：成功 {okCnt} 项，拦截/跳过 {blockedCnt} 项，释放/移入回收站 {CleanCandidate.FormatBytes(audit.TotalFreedBytes)}。");

            await ScanSingleZoneAsync(_currentZone);
            MessageBox.Show(this,
                $"本次清理已完成！\n\n• 成功：{okCnt} 项\n• 拦截/跳过：{blockedCnt} 项\n• 释放/移入回收站：{CleanCandidate.FormatBytes(audit.TotalFreedBytes)}\n• 审计日志：{logPath}",
                "清理完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _isCleaning = false;
            UpdateBusyIndicators($"执行异常：{ex.Message}");
            MessageBox.Show(this, ex.Message, "清理出错", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnExportReport_Click(object sender, RoutedEventArgs e)
    {
        if (!_reports.TryGetValue(_currentZone, out var rep))
        {
            MessageBox.Show(this, "请先等待当前专区扫描完成后再导出体检报告。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            Directory.CreateDirectory(SafetyGuard.ReportsDir);
            string fileName = $"audit-{_currentZone.ToString().ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmmss}.md";
            string fullPath = Path.Combine(SafetyGuard.ReportsDir, fileName);

            var md = new StringBuilder();
            md.AppendLine($"# {GetZoneName(_currentZone)} 清理体检报告");
            md.AppendLine($"- 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            md.AppendLine($"- 扫描状态：{rep.Status}（耗时 {rep.ElapsedMs} ms，权限受限 {rep.AccessDeniedCount} 处）");
            md.AppendLine($"- 应用状态：{(rep.ProcessInfo.IsRunning ? $"运行中 ({rep.ProcessInfo.RunningPids.Count} 进程)" : "已退出")}");
            md.AppendLine($"- 在线安全可清：{CleanCandidate.FormatBytes(rep.SafeOnlineBytes)} | 回收站候选：{CleanCandidate.FormatBytes(rep.RecycleCandidateBytes)} | 离线缓存：{CleanCandidate.FormatBytes(rep.OfflineCacheBytes)} | 只读雷达诊断：{CleanCandidate.FormatBytes(rep.DiagnosticBytes)}");
            md.AppendLine();
            md.AppendLine("| 分级 | 体积 | 文件数 | 最新修改 | 项目 | 路径 | 说明 |");
            md.AppendLine("|---|---:|---:|---|---|---|---|");
            foreach (var c in rep.Candidates)
                md.AppendLine($"| {c.TierBadge} | {c.SizeText} | {c.FileCount} | {c.LastWriteText} | {c.Title} | `{c.Path}` | {c.Reason} |");

            File.WriteAllText(fullPath, md.ToString(), new UTF8Encoding(false));
            TxtLastAuditHint.Text = $"报告：{fileName}";
            TxtStatusLine.Text = $"已导出体检报告：{fullPath}";
            OpenInExplorer(fullPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnToggleSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (_isCleaning) return;
        BtnToggleSchedule.IsEnabled = false;
        bool wantEnable = !_scheduleInstalled;
        var (ok, msg) = await Task.Run(() => TaskSchedulerHelper.SetEnabled(wantEnable));
        await RefreshScheduleStatusAsync();
        BtnToggleSchedule.IsEnabled = !_isCleaning;
        TxtStatusLine.Text = msg;
        if (!ok) MessageBox.Show(this, msg, "计划任务操作提示", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void BtnOverviewDashboard_Click(object sender, RoutedEventArgs e)
    {
        GlassDialogs.ShowCrossZoneOverviewDialog(this, _reports, targetZone => SwitchToZoneInstant(targetZone));
    }

    private void BtnLangToggle_Click(object sender, RoutedEventArgs e)
    {
        string nextLang = Loc.IsEnglish ? "zh-CN" : "en-US";
        Loc.SetLanguage(nextLang);
        var s = SettingsStore.Load();
        s.UiLanguage = nextLang;
        SettingsStore.Save(s);
        ApplyLocalization();
    }

    private void BtnShowHistory_Click(object sender, RoutedEventArgs e) =>
        GlassDialogs.ShowHistoryDialog(this);

    private async void BtnShowSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_isCleaning) return;
        if (GlassDialogs.ShowSettingsAndWhitelistDialog(this))
        {
            RefreshPolicyButtonLabel();
            RefreshSidebarDetectionGroups();
            _trayService?.SyncWithSettings(SettingsStore.Load(), suppressForQa: !string.IsNullOrWhiteSpace(_qaCaptureDir));
            var procs = await RefreshAllProcessBadgesAsync(syncOfflineLocks: false);
            await Task.WhenAll(ZoneCatalog.All.Select(def =>
                ScanSingleZoneAsync(def.Id, silent: true, preloadedProc: procs[def.Id])));
            ApplyCurrentReportToView();
        }
    }

    private void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SafetyGuard.LogsDir);
        OpenInExplorer(SafetyGuard.LogsDir);
    }

    private void BtnOpenRecycleBin_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true }); } catch { }
    }

    private void BtnShowRedlines_Click(object sender, RoutedEventArgs e)
    {
        string msg =
            "【七专区引擎级硬红线保护（双层实时校验 + 执行器授权闸门）】\n\n" +
            "1. Antigravity 专区\n" +
            "   • 永不触碰：conversations 主对话库、.gemini/config、.env、登录态 Network/LocalStorage/IndexedDB/Service Worker Database、最近 72 小时会话产物。\n\n" +
            "2. GPT / Codex 专区（ChatGPT 桌面端）\n" +
            "   • 永不触碰：%USERPROFILE%\\.codex 全目录与全部 SQLite 数据库（仅 O(1) 只读诊断）、MSIX 包目录、runtimes、.codex 全部联接目标及含联接目录。\n\n" +
            "3. DeepSeek Harness 专区\n" +
            "   • 永不触碰：.dsh/profiles（含插件联接）、dsh-runtimes、skills 联接、sessions、.credentials.yaml 与登录态。\n\n" +
            "4. Claude Code 专区（严格只读诊断模式）\n" +
            "   • 永不触碰：.claude 全目录（含 file-history 编辑回滚副本、shell-snapshots、projects 与凭据），删除候选恒为 0。\n\n" +
            "5. AI 工具链与模型雷达\n" +
            "   • 仅 uv / pip / npm 超期纯下载缓存开放绿区清理（跳过 NTFS 联接）；pnpm 硬链接库、Playwright/Puppeteer 与 Hugging Face/Ollama/LM Studio 均严格只读。\n\n" +
            "6. AI IDE 专区（Cursor / Windsurf / Trae）\n" +
            "   • 仅顶层离线 Electron 缓存按各 IDE 进程独立加锁开放清理；硬保护 User/globalStorage/state.vscdb、workspaceStorage、扩展目录与 CachedExtensionVSIXs。\n\n" +
            "7. 本地 AI 生态与虚拟盘雷达（严格只读）\n" +
            "   • WSL2/Docker ext4.vhdx、Cline/Roo Code/Continue、ComfyUI 与受控只读社区规则包均强制只读（0 删除候选），绝不自动删文件。\n\n" +
            "8. 通用铁律\n" +
            "   • 执行器入口强制二次核验 (Zone, Action, Path)；硬保护系统目录与 .ssh/.gnupg/.aws/.kube/Documents/Desktop；全祖先链联接校验；绝不强杀运行中桌面进程。";
        MessageBox.Show(this, msg, Loc.T("BtnRedlines"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string GetZoneName(ZoneId z) => ZoneCatalog.Get(z).LocalizedDisplayName;
}
