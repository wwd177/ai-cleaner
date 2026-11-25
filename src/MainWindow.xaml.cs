using System.Collections.Concurrent;
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
    private bool _isCleaning;
    private bool _scheduleInstalled;
    private bool _scheduleLoaded;
    private bool _suppressFilterEvents;
    private long _procRefreshSeq;
    private DateTime _lastActivatedRefreshUtc = DateTime.MinValue;
    private readonly string? _qaCaptureDir;

    public MainWindow(string? qaCaptureDir = null)
    {
        _qaCaptureDir = qaCaptureDir;
        InitializeComponent();
        GlassDialogs.ApplyIceBlueTitleBar(this);

        // Live auto-unlock: when user exits ChatGPT/Antigravity/DSH and switches back,
        // asynchronously re-check processes (7ms, 0ms UI lag) and auto-unlock Blue offline caches!
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

            // Single-pass process check first (~7ms), then pass the snapshot to all 3 parallel zone scans
            // (eliminates 3 redundant OS process scans at startup!).
            var procs = await RefreshAllProcessBadgesAsync(syncOfflineLocks: false);
            var schedTask = RefreshScheduleStatusAsync();
            var scanAg = ScanSingleZoneAsync(ZoneId.Antigravity, silent: true, preloadedProc: procs[ZoneId.Antigravity]);
            var scanDsh = ScanSingleZoneAsync(ZoneId.DeepSeekHarness, silent: true, preloadedProc: procs[ZoneId.DeepSeekHarness]);
            var scanGpt = ScanSingleZoneAsync(ZoneId.GptCodex, silent: true, preloadedProc: procs[ZoneId.GptCodex]);

            await Task.WhenAll(schedTask, scanAg, scanDsh, scanGpt);

            if (!string.IsNullOrWhiteSpace(_qaCaptureDir))
            {
                await RunQaVisualCaptureAndBenchAsync(_qaCaptureDir);
                Close();
            }
        };
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

        foreach (var z in new[] { ZoneId.Antigravity, ZoneId.GptCodex, ZoneId.DeepSeekHarness })
        {
            var swScan = Stopwatch.StartNew();
            _ = await Task.Run(() => ZoneScanner.ScanZone(z));
            swScan.Stop();
            bench.AppendLine($"Scan_{z}_ms={swScan.ElapsedMilliseconds}");
        }

        // Measure pure UI switching latency across all 3 zones!
        foreach (var z in new[] { ZoneId.GptCodex, ZoneId.DeepSeekHarness, ZoneId.Antigravity, ZoneId.GptCodex })
        {
            var swSwitch = Stopwatch.StartNew();
            SwitchToZoneInstant(z);
            UpdateLayout();
            swSwitch.Stop();
            bench.AppendLine($"UiSwitchTo_{z}_ms={swSwitch.ElapsedMilliseconds}");
        }

        foreach (var z in new[] { ZoneId.Antigravity, ZoneId.GptCodex, ZoneId.DeepSeekHarness })
        {
            SwitchToZoneInstant(z);
            if (GridCandidates.Items.Count > 0)
                GridCandidates.SelectedIndex = 0;
            UpdateLayout();
            await Task.Delay(120);
            SaveVisualPng(Path.Combine(outDir, $"glass-{z.ToString().ToLowerInvariant()}.png"));
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
            ? $"一键清三区绿区 ({CleanCandidate.FormatBytes(totalSafe)})"
            : "三区绿区已洁净";
    }

    private async Task RefreshRecycleBinAsync()
    {
        try
        {
            var (bytes, items) = await Task.Run(SafetyGuard.GetRecycleBinStats);
            if (bytes < 0)
            {
                TxtRecycleBinSize.Text = "不可用";
                return;
            }
            if (bytes > 0 || items > 0)
            {
                TxtRecycleBinSize.Text = $"{CleanCandidate.FormatBytes(bytes)} · {items} 项";
                TxtRecycleBinSize.Foreground = bytes >= 5L * 1024 * 1024 * 1024 ? BrAmberText : BrCyanText;
            }
            else
            {
                TxtRecycleBinSize.Text = "空 (0 B)";
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
            TxtTotalFreed.Text = $"累计释放 {CleanCandidate.FormatBytes(total)}";
        }
        catch { }
    }

    private void RefreshPolicyButtonLabel()
    {
        var s = SettingsStore.Load();
        BtnPolicyQuick.Content = $"阈值: {s.TempMinAgeDays}天/{s.WorkspaceMinSizeMB}M";
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
                TxtDriveC.Text = low ? $"偏紧 · 余 {freeGb:F1} GB" : $"余 {freeGb:F1} GB";
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
                TxtDrive2Label.Text = $"{letter} 盘 (数据/工作区)";
                double freeGb = dataDrive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                double totalGb = dataDrive.TotalSize / (1024.0 * 1024 * 1024);
                double usedPct = (1.0 - freeGb / totalGb) * 100.0;
                bool low = freeGb < 30.0 || usedPct >= 88.0;
                TxtDriveF.Text = low ? $"偏紧 · 余 {freeGb:F1} GB" : $"余 {freeGb:F1} GB";
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
        ApplySideProcBadge(all[ZoneId.Antigravity], DotAgProc, TxtAgSideProc);
        ApplySideProcBadge(all[ZoneId.GptCodex], DotGptProc, TxtGptSideProc);
        ApplySideProcBadge(all[ZoneId.DeepSeekHarness], DotDshProc, TxtDshSideProc);

        if (syncOfflineLocks)
        {
            string? unlockedNotice = null;
            bool currentChanged = false;
            foreach (var (zone, newProc) in all)
            {
                if (!_reports.TryGetValue(zone, out var rep)) continue;
                bool wasRunning = rep.ProcessInfo.IsRunning;
                rep.ProcessInfo = newProc;
                if (wasRunning != newProc.IsRunning)
                {
                    foreach (var c in rep.Candidates.Where(x => x.Tier == CleanTier.OfflineCache))
                        c.ApplyProcessLockState(newProc.IsRunning);

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

    private static void ApplySideProcBadge(ZoneProcessInfo p, Border dot, TextBlock label)
    {
        if (p.IsRunning)
        {
            dot.Background = BrEmerald;
            label.Text = $"运行中 ({p.RunningPids.Count} 进程) · 离线项已锁";
        }
        else
        {
            dot.Background = BrSlateDot;
            label.Text = "已退出 · 可清离线缓存";
        }
    }

    private void UpdateZoneChrome()
    {
        var normalBorder = (Brush)FindResource("BorderGlass");
        var normalBg = (Brush)FindResource("GlassCardBrush");
        var activeBg = (Brush)FindResource("ActiveZoneBrush");
        var normalLift = (Brush)FindResource("GlassLiftShadow");
        var activeLift = (Brush)FindResource("GlassLiftShadowActive");

        void StyleCard(Border card, Border shadowBorder, Border accentBar, bool active, string accentRes)
        {
            card.Background = active ? activeBg : normalBg;
            card.BorderBrush = active ? (Brush)FindResource(accentRes) : normalBorder;
            card.BorderThickness = active ? new Thickness(1.6) : new Thickness(1);
            shadowBorder.Background = active ? activeLift : normalLift;
            accentBar.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        }

        StyleCard(CardAntigravity, ShadowAntigravity, BarAgAccent, _currentZone == ZoneId.Antigravity, "AccentBlue");
        StyleCard(CardGpt, ShadowGpt, BarGptAccent, _currentZone == ZoneId.GptCodex, "AccentCyan");
        StyleCard(CardDsh, ShadowDsh, BarDshAccent, _currentZone == ZoneId.DeepSeekHarness, "AccentIndigo");

        BarGptSchedule.Visibility = _currentZone == ZoneId.GptCodex ? Visibility.Visible : Visibility.Collapsed;

        switch (_currentZone)
        {
            case ZoneId.Antigravity:
                TxtZoneTitle.Text = "Antigravity 专区";
                TxtZoneSubtitle.Text = "覆盖 Antigravity 更新器安装包残留、运行缓存、崩溃/客户端日志、历史会话产物与离线浏览器缓存；硬保护 conversations 主对话库与登录态。";
                break;
            case ZoneId.GptCodex:
                TxtZoneTitle.Text = "GPT / Codex 专区";
                TxtZoneSubtitle.Text = @"面向 ChatGPT 桌面端与 Codex 工作区：覆盖 temp、包缓存、_tmp/__pycache__、历史打包暂存（回收站）与离线浏览器缓存；硬保护 .codex 核心库、runtimes 及全部联接。";
                break;
            case ZoneId.DeepSeekHarness:
                TxtZoneTitle.Text = "DeepSeek Harness 专区";
                TxtZoneSubtitle.Text = "覆盖 DSH 更新器残留、.dsh/cache、历史配置备份、超 14 天旧附件（回收站）与离线桌面缓存；硬保护 .dsh/profiles、skills 联接、sessions 与账号凭据。";
                break;
        }

        ApplyCurrentReportToView();
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
            UpdateBusyIndicators($"{GetZoneName(target)} 已就绪（在线安全 {CleanCandidate.FormatBytes(ready.SafeOnlineBytes)}，回收站候选 {CleanCandidate.FormatBytes(ready.RecycleCandidateBytes)}，离线缓存 {CleanCandidate.FormatBytes(ready.OfflineCacheBytes)}）。");
        }
        else
        {
            UpdateBusyIndicators(null);
        }
    }

    private async Task ScanSingleZoneAsync(ZoneId zone, bool silent = false, ZoneProcessInfo? preloadedProc = null)
    {
        if (!_scanningZones.Add(zone)) return;
        string zoneLabel = GetZoneName(zone);
        if (!silent || _currentZone == zone)
            UpdateBusyIndicators($"正在扫描 {zoneLabel}（只读体检，不阻塞切换）...");
        else
            UpdateBusyIndicators();

        // Capture currently checked items so still-valid selections survive a rescan!
        HashSet<string>? prevSelectedKeys = null;
        if (_reports.TryGetValue(zone, out var oldRep))
        {
            var keys = oldRep.Candidates.Where(c => c.IsSelected && c.CanSelect).Select(c => c.StableKey).ToList();
            if (keys.Count > 0) prevSelectedKeys = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            long seqAtStart = Volatile.Read(ref _procRefreshSeq);
            var report = await Task.Run(() => ZoneScanner.ScanZone(zone, preloadedProc));

            // Reconcile commit order (Astra P1 race fix!): if a fresher process refresh completed
            // while this scan was running, NEVER let stale scan status overwrite live process locks!
            if (Volatile.Read(ref _procRefreshSeq) > seqAtStart && _procCache.TryGetValue(zone, out var latestProc))
            {
                report.ProcessInfo = latestProc;
                foreach (var c in report.Candidates.Where(x => x.Tier == CleanTier.OfflineCache))
                    c.ApplyProcessLockState(latestProc.IsRunning);
            }
            else
            {
                _procCache[zone] = report.ProcessInfo;
            }

            // Reconcile user selections that remain eligible & not newly whitelisted/locked
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

            _scanningZones.Remove(zone);
            string doneMsg = _scanningZones.Count == 0
                ? $"全部专区已就绪（{zoneLabel}：在线安全 {CleanCandidate.FormatBytes(report.SafeOnlineBytes)}，回收站候选 {CleanCandidate.FormatBytes(report.RecycleCandidateBytes)}）。"
                : $"{zoneLabel} 扫描完成，其余专区仍在后台扫描中...";
            UpdateBusyIndicators(doneMsg);
        }
        catch (Exception ex)
        {
            _scanningZones.Remove(zone);
            UpdateBusyIndicators($"{zoneLabel} 扫描出错：{ex.Message}");
        }
    }

    private void UpdateSingleSideProc(ZoneId zone, ZoneProcessInfo info)
    {
        switch (zone)
        {
            case ZoneId.Antigravity: ApplySideProcBadge(info, DotAgProc, TxtAgSideProc); break;
            case ZoneId.GptCodex: ApplySideProcBadge(info, DotGptProc, TxtGptSideProc); break;
            case ZoneId.DeepSeekHarness: ApplySideProcBadge(info, DotDshProc, TxtDshSideProc); break;
        }
    }

    private void UpdateSidebarZoneSummary(ZoneScanReport report)
    {
        long totalCleanable = report.SafeOnlineBytes + report.RecycleCandidateBytes + (report.ProcessInfo.IsRunning ? 0 : report.OfflineCacheBytes);
        string text = $"可清理 {CleanCandidate.FormatBytes(totalCleanable)}（含回收站项）";
        switch (report.Zone)
        {
            case ZoneId.Antigravity: TxtAgSideSize.Text = text; break;
            case ZoneId.GptCodex: TxtGptSideSize.Text = text; break;
            case ZoneId.DeepSeekHarness: TxtDshSideSize.Text = text; break;
        }
    }

    private void ApplyHeaderProcessBadge()
    {
        // Strictly O(1) memory lookup — NEVER runs Process.GetProcesses() on the UI thread!
        ZoneProcessInfo? proc = null;
        if (_reports.TryGetValue(_currentZone, out var rep))
            proc = rep.ProcessInfo;
        else if (_procCache.TryGetValue(_currentZone, out var cached))
            proc = cached;

        if (proc == null)
        {
            BadgeProcStatus.Background = BrDetectBg;
            BadgeProcStatus.BorderBrush = BrDetectBorder;
            TxtProcBadge.Foreground = BrDetectInk;
            TxtProcBadge.Text = "检测进程中...";
            return;
        }

        if (proc.IsRunning)
        {
            BadgeProcStatus.Background = BrRunBg;
            BadgeProcStatus.BorderBrush = BrRunBorder;
            TxtProcBadge.Foreground = BrRunInk;
            TxtProcBadge.Text = $"运行中 ({proc.RunningPids.Count} 进程) · 在线安全模式";
        }
        else
        {
            BadgeProcStatus.Background = BrOffBg;
            BadgeProcStatus.BorderBrush = BrOffBorder;
            TxtProcBadge.Foreground = BrOffInk;
            TxtProcBadge.Text = "已退出 · 在线/离线均可清理";
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

        int safeCnt = rep.Candidates.Count(c => c.Tier == CleanTier.SafeOnline && c.CanSelect);
        int recCnt = rep.Candidates.Count(c => c.Tier == CleanTier.RecycleCandidate && c.CanSelect);
        int offCnt = rep.Candidates.Count(c => c.Tier == CleanTier.OfflineCache);
        int recoCnt = rep.Candidates.Count(c => c.IsRecommended && c.CanSelect);

        TxtKpiSafe.Text = CleanCandidate.FormatBytes(rep.SafeOnlineBytes);
        TxtKpiSafeSub.Text = $"{safeCnt} 项 · 应用开着即可清";

        TxtKpiRecycle.Text = CleanCandidate.FormatBytes(rep.RecycleCandidateBytes);
        TxtKpiRecycleSub.Text = $"{recCnt} 项 · 移入回收站可恢复";

        TxtKpiOffline.Text = CleanCandidate.FormatBytes(rep.OfflineCacheBytes);
        TxtKpiOfflineSub.Text = rep.ProcessInfo.IsRunning
            ? $"{offCnt} 项 · 应用运行中已自动加锁"
            : $"{offCnt} 项 · 应用已退出，可勾选清理";

        BtnSelectRecommended.Content = recoCnt > 0
            ? $"勾选高置信推荐 ({recoCnt})"
            : "勾选高置信推荐";

        UpdateBreakdownBar(rep);
        UpdateGlobalSafeCleanButton();
        ApplyFilter();
    }

    private void UpdateBreakdownBar(ZoneScanReport? rep)
    {
        if (rep == null)
        {
            ColBarSafe.Width = new GridLength(1, GridUnitType.Star);
            ColBarRecycle.Width = new GridLength(1, GridUnitType.Star);
            ColBarOffline.Width = new GridLength(1, GridUnitType.Star);
            TxtBreakdownSummary.Text = "正在统计空间分布...";
            return;
        }

        long s = Math.Max(0, rep.SafeOnlineBytes);
        long r = Math.Max(0, rep.RecycleCandidateBytes);
        long o = Math.Max(0, rep.OfflineCacheBytes);
        long total = s + r + o;

        if (total <= 0)
        {
            ColBarSafe.Width = new GridLength(1, GridUnitType.Star);
            ColBarRecycle.Width = new GridLength(0, GridUnitType.Star);
            ColBarOffline.Width = new GridLength(0, GridUnitType.Star);
            TxtBreakdownSummary.Text = "当前专区已非常洁净（0 B 待清理）";
            return;
        }

        ColBarSafe.Width = new GridLength(s > 0 ? Math.Max(0.03, (double)s / total) : 0, GridUnitType.Star);
        ColBarRecycle.Width = new GridLength(r > 0 ? Math.Max(0.03, (double)r / total) : 0, GridUnitType.Star);
        ColBarOffline.Width = new GridLength(o > 0 ? Math.Max(0.03, (double)o / total) : 0, GridUnitType.Star);

        long nowCleanable = s + r + (rep.ProcessInfo.IsRunning ? 0 : o);
        TxtBreakdownSummary.Text = $"可立即释放 {CleanCandidate.FormatBytes(nowCleanable)} / 总发现 {CleanCandidate.FormatBytes(total)}（绿 {CleanCandidate.FormatBytes(s)} · 黄 {CleanCandidate.FormatBytes(r)} · 蓝 {CleanCandidate.FormatBytes(o)}）";
    }

    private void UpdateInspector(CleanCandidate? item)
    {
        if (item == null)
        {
            BadgeInspectorTier.Background = BrInspDefaultBg;
            BadgeInspectorTier.BorderBrush = BrInspDefaultBorder;
            TxtInspectorTier.Foreground = BrInspDefaultInk;
            TxtInspectorTier.Text = "项目透视";
            TxtInspectorPath.Text = "单击列表任意项目，在此查看完整绝对路径、安全判定与清理影响评估（双击打开目录）";
            TxtInspectorVerdict.Text = "双层红线校验：运行中仅清理超期未占用文件，大目录与安装包残留均移入 Windows 回收站可随时还原。";
            BtnInspectorCopy.IsEnabled = false;
            return;
        }

        BadgeInspectorTier.Background = GetFrozenHexBrush(item.TierColorHex, BrInspDefaultBg);
        BadgeInspectorTier.BorderBrush = GetFrozenHexBrush(item.TierBorderHex, BrInspDefaultBorder);
        TxtInspectorTier.Foreground = GetFrozenHexBrush(item.TierInkHex, BrInspDefaultInk);

        TxtInspectorTier.Text = item.TierBadge;
        TxtInspectorPath.Text = item.Path;
        string statsSuffix = item.JunctionCount > 0 || item.RecentSkippedCount > 0
            ? $"（自动保护跳过：近期 {item.RecentSkippedCount} / 联接 {item.JunctionCount}）"
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
            BtnCleanSelected.Content = "执行已勾选清理 (0 项)";
            return;
        }
        var selected = rep.Candidates.Where(c => c.IsSelected && c.CanSelect).ToList();
        long bytes = selected.Sum(c => c.SizeBytes);
        BtnCleanSelected.IsEnabled = selected.Count > 0;
        BtnCleanSelected.Content = selected.Count > 0
            ? $"执行已勾选清理 ({selected.Count} 项 · {CleanCandidate.FormatBytes(bytes)})"
            : "执行已勾选清理 (0 项)";
    }

    private async Task RefreshScheduleStatusAsync()
    {
        var (installed, summary) = await Task.Run(TaskSchedulerHelper.GetStatus);
        _scheduleInstalled = installed;
        _scheduleLoaded = true;
        TxtScheduleStatus.Text = summary;
        BtnToggleSchedule.Content = installed ? "停用定时任务" : "启用每周定时任务";
    }

    // === Instant Non-Blocking Zone Switching ===
    private void ZoneCard_Antigravity_Click(object sender, MouseButtonEventArgs e)
    {
        SwitchToZoneInstant(ZoneId.Antigravity);
        if (!_reports.ContainsKey(ZoneId.Antigravity) && !_scanningZones.Contains(ZoneId.Antigravity))
            _ = ScanSingleZoneAsync(ZoneId.Antigravity);
    }

    private void ZoneCard_Gpt_Click(object sender, MouseButtonEventArgs e)
    {
        SwitchToZoneInstant(ZoneId.GptCodex);
        if (!_scheduleLoaded)
            _ = RefreshScheduleStatusAsync();
        if (!_reports.ContainsKey(ZoneId.GptCodex) && !_scanningZones.Contains(ZoneId.GptCodex))
            _ = ScanSingleZoneAsync(ZoneId.GptCodex);
    }

    private void ZoneCard_Dsh_Click(object sender, MouseButtonEventArgs e)
    {
        SwitchToZoneInstant(ZoneId.DeepSeekHarness);
        if (!_reports.ContainsKey(ZoneId.DeepSeekHarness) && !_scanningZones.Contains(ZoneId.DeepSeekHarness))
            _ = ScanSingleZoneAsync(ZoneId.DeepSeekHarness);
    }

    private async void BtnRescanZone_Click(object sender, RoutedEventArgs e)
    {
        if (_currentZone == ZoneId.GptCodex)
            _ = RefreshScheduleStatusAsync();
        await ScanSingleZoneAsync(_currentZone);
    }

    private async void BtnScanAllZones_Click(object sender, RoutedEventArgs e)
    {
        if (_isCleaning || _scanningZones.Count > 0) return;
        var procs = await RefreshAllProcessBadgesAsync(syncOfflineLocks: false);
        await Task.WhenAll(
            ScanSingleZoneAsync(ZoneId.Antigravity, silent: true, preloadedProc: procs[ZoneId.Antigravity]),
            ScanSingleZoneAsync(ZoneId.DeepSeekHarness, silent: true, preloadedProc: procs[ZoneId.DeepSeekHarness]),
            ScanSingleZoneAsync(ZoneId.GptCodex, silent: true, preloadedProc: procs[ZoneId.GptCodex])
        );
        ApplyCurrentReportToView();
        UpdateBusyIndicators("三个专区并行扫描完成。可随时瞬间切换专区并勾选清理。");
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
    private void KpiOffline_Click(object sender, MouseButtonEventArgs e) => RbFilterOffline.IsChecked = true;

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
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var settings = SettingsStore.Load();
            string? firstWs = settings.ExtraWorkspaceRoots.FirstOrDefault(Directory.Exists);
            (targetPath, title) = _currentZone switch
            {
                ZoneId.Antigravity => (Path.Combine(home, @".gemini\antigravity"), "Antigravity 数据目录"),
                ZoneId.GptCodex => (!string.IsNullOrEmpty(firstWs) ? firstWs : Path.Combine(home, ".codex"), "GPT / Codex 数据目录"),
                ZoneId.DeepSeekHarness => (Path.Combine(home, ".dsh"), "DeepSeek Harness 目录"),
                _ => (home, "目录透视")
            };
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
        foreach (var z in new[] { ZoneId.Antigravity, ZoneId.GptCodex, ZoneId.DeepSeekHarness })
        {
            if (!_reports.TryGetValue(z, out var rep)) continue;
            foreach (var c in rep.Candidates.Where(x => x.Tier == CleanTier.SafeOnline && x.CanSelect))
                allSafe.Add((z, c));
        }

        if (allSafe.Count == 0)
        {
            MessageBox.Show(this, "当前三个专区的绿区在线安全缓存均为 0，无需清理。", "一键清理三区绿区", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        long totalBytes = allSafe.Sum(x => x.Item.SizeBytes);
        var sb = new StringBuilder();
        sb.AppendLine($"即将一键清理三个专区的全部【绿区 · 在线安全】项目（共 {allSafe.Count} 项，预计释放 {CleanCandidate.FormatBytes(totalBytes)}）：");
        sb.AppendLine();
        foreach (var (z, it) in allSafe.Take(14))
            sb.AppendLine($"• [{GetZoneName(z)}] [{it.SizeText}] {it.Title}");
        if (allSafe.Count > 14) sb.AppendLine($"... 等共 {allSafe.Count} 项");
        sb.AppendLine();
        sb.AppendLine("安全承诺：仅清理绿区安全项（绝不碰黄区工作区、蓝区离线缓存与红区）；绝不关闭运行中的 AI 桌面端，安装包残留与临时目录均移入 Windows 回收站。");

        if (MessageBox.Show(this, sb.ToString(), "确认一键清理三区绿区", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _isCleaning = true;
        UpdateBusyIndicators($"正在跨三区执行绿区安全清理（共 {allSafe.Count} 项）...");
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
            UpdateBusyIndicators($"三区绿区清理完成：成功 {grandOk} 项，拦截/跳过 {grandBlocked} 项，共释放 {CleanCandidate.FormatBytes(grandFreed)}。");

            await Task.WhenAll(touchedZones.Select(z => ScanSingleZoneAsync(z, silent: true)));
            ApplyCurrentReportToView();

            MessageBox.Show(this,
                $"三区绿区一键清理已完成！\n\n• 涉及专区：{touchedZones.Count} 个\n• 成功：{grandOk} 项\n• 拦截/跳过：{grandBlocked} 项\n• 共释放/移入回收站：{CleanCandidate.FormatBytes(grandFreed)}",
                "三区绿区清理完成", MessageBoxButton.OK, MessageBoxImage.Information);
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
            md.AppendLine($"- 应用状态：{(rep.ProcessInfo.IsRunning ? $"运行中 ({rep.ProcessInfo.RunningPids.Count} 进程)" : "已退出")}");
            md.AppendLine($"- 在线安全可清：{CleanCandidate.FormatBytes(rep.SafeOnlineBytes)} | 回收站候选：{CleanCandidate.FormatBytes(rep.RecycleCandidateBytes)} | 离线缓存：{CleanCandidate.FormatBytes(rep.OfflineCacheBytes)}");
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

    private void BtnShowHistory_Click(object sender, RoutedEventArgs e) =>
        GlassDialogs.ShowHistoryDialog(this);

    private async void BtnShowSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_isCleaning) return;
        if (GlassDialogs.ShowSettingsAndWhitelistDialog(this))
        {
            RefreshPolicyButtonLabel();
            await ScanSingleZoneAsync(_currentZone);
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
            "【三专区硬编码红线保护（双层实时校验）】\n\n" +
            "1. Antigravity 专区\n" +
            "   • 永不触碰：conversations 主对话库、.gemini/config、.env、登录态 Network/LocalStorage/IndexedDB/Service Worker Database、最近 72 小时会话产物。\n\n" +
            "2. GPT / Codex 专区（ChatGPT 桌面端）\n" +
            "   • 永不触碰：%USERPROFILE%\\.codex 全目录与全部 SQLite 数据库（仅做 O(1) 只读体积诊断）、MSIX 包目录、runtimes、.codex 联接目标目录、一切 NTFS 联接点及含联接目录。\n" +
            "   • 离线浏览器缓存仅在 ChatGPT/codex 进程为 0 时开放（退出切回本窗口自动解锁），且只删内部文件、完整保留目录骨架。\n\n" +
            "3. DeepSeek Harness 专区\n" +
            "   • 永不触碰：.dsh/profiles（含插件联接）、dsh-runtimes、skills 联接、sessions、.credentials.yaml、dsh-computer-use-win 与登录态。\n\n" +
            "4. 通用铁律与白名单\n" +
            "   • 执行清理前一刻再次校验实时进程、白名单与红线；绝不强杀任何 AI 桌面进程；所有工作区、安装包残留、备份与旧附件清理均移入 Windows 回收站，随时可还原；支持 CLI --dry-run 只读体检。";
        MessageBox.Show(this, msg, "红线保护说明", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string GetZoneName(ZoneId z) => z switch
    {
        ZoneId.Antigravity => "Antigravity 专区",
        ZoneId.GptCodex => "GPT / Codex 专区",
        ZoneId.DeepSeekHarness => "DeepSeek Harness 专区",
        _ => z.ToString()
    };
}
