using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AICleaner;

/// <summary>
/// Optional lightweight system-tray low-disk-space alert service (disabled by default, zero network telemetry).
/// Runs disk checks asynchronously off the UI thread with single-flight protection and clean shutdown on Windows SessionEnding.
/// </summary>
public sealed class TrayAlertService : IDisposable
{
    private readonly Window _owner;
    private readonly Action _onExplicitExit;
    private System.Windows.Forms.NotifyIcon? _icon;
    private DispatcherTimer? _timer;
    private DateTime _lastAlertUtc = DateTime.MinValue;
    private int _checkInFlight;
    private volatile bool _disposed;

    public bool IsActive => _icon != null;

    public TrayAlertService(Window owner, Action onExplicitExit)
    {
        _owner = owner;
        _onExplicitExit = onExplicitExit;
    }

    public void SyncWithSettings(CleanerSettings settings, bool suppressForQa = false)
    {
        if (_disposed) return;
        if (suppressForQa || !settings.EnableTrayLowSpaceAlert)
        {
            Disable(restoreIfHidden: true);
            return;
        }
        Enable();
        RefreshMenuLocalization();
    }

    private void Enable()
    {
        if (_icon != null) return;

        System.Drawing.Icon? appIcon = null;
        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                appIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
        }
        catch { }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("TrayOpen"), null, (_, _) => RestoreOwner());
        menu.Items.Add(Loc.T("TrayCheckDisk"), null, (_, _) => _ = CheckDiskSpaceAsync(forceNotify: true));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("TrayExit"), null, (_, _) =>
        {
            Disable(restoreIfHidden: false);
            _onExplicitExit();
        });

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = appIcon ?? System.Drawing.SystemIcons.Information,
            Text = Loc.T("BrandName"),
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => RestoreOwner();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _timer.Tick += (_, _) => _ = CheckDiskSpaceAsync(forceNotify: false);
        _timer.Start();
    }

    public void RefreshMenuLocalization()
    {
        if (_icon == null) return;
        _icon.Text = Loc.T("BrandName");
        if (_icon.ContextMenuStrip is { Items.Count: >= 4 } menu)
        {
            menu.Items[0].Text = Loc.T("TrayOpen");
            menu.Items[1].Text = Loc.T("TrayCheckDisk");
            menu.Items[3].Text = Loc.T("TrayExit");
        }
    }

    /// <summary>Non-blocking background fixed-drive check with single-flight guard and per-drive fault isolation.</summary>
    public async Task CheckDiskSpaceAsync(bool forceNotify)
    {
        if (_disposed || _icon == null) return;
        if (Interlocked.Exchange(ref _checkInFlight, 1) != 0) return;

        try
        {
            var settings = SettingsStore.Load();
            if (!settings.EnableTrayLowSpaceAlert) return;
            if (!forceNotify && (DateTime.UtcNow - _lastAlertUtc).TotalHours < 6)
                return;

            int thresholdGb = settings.LowSpaceAlertThresholdGB;
            var lowDrive = await Task.Run(() => FindFirstLowDrive(thresholdGb));

            // Re-verify active state on UI thread after async I/O completes (never notify after Disable/Dispose!)
            await _owner.Dispatcher.InvokeAsync(() =>
            {
                if (_disposed || _icon == null || !SettingsStore.Load().EnableTrayLowSpaceAlert) return;
                if (lowDrive.HasValue)
                {
                    _lastAlertUtc = DateTime.UtcNow;
                    _icon.BalloonTipTitle = Loc.T("TrayLowDiskTitle");
                    _icon.BalloonTipText = Loc.F("TrayLowDiskBody", lowDrive.Value.Letter, lowDrive.Value.FreeGb, thresholdGb);
                    _icon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Warning;
                    _icon.ShowBalloonTip(5000);
                }
                else if (forceNotify)
                {
                    _icon.BalloonTipTitle = Loc.T("BrandName");
                    _icon.BalloonTipText = Loc.IsEnglish
                        ? $"All fixed drives are above your {thresholdGb} GB alert threshold."
                        : $"全部本地固定磁盘剩余空间均高于 {thresholdGb} GB 预警阈值。";
                    _icon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
                    _icon.ShowBalloonTip(3500);
                }
            });
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _checkInFlight, 0);
        }
    }

    private static (string Letter, double FreeGb)? FindFirstLowDrive(int thresholdGb)
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); } catch { return null; }

        foreach (var d in drives)
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                double freeGb = d.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                if (freeGb < thresholdGb)
                    return (d.Name.TrimEnd('\\', ':'), freeGb);
            }
            catch { }
        }
        return null;
    }

    private void RestoreOwner()
    {
        if (_disposed) return;
        _owner.Dispatcher.Invoke(() =>
        {
            if (!_owner.IsVisible) _owner.Show();
            if (_owner.WindowState == WindowState.Minimized)
                _owner.WindowState = WindowState.Normal;
            _owner.Activate();
            _owner.Topmost = true;
            _owner.Topmost = false;
            _owner.Focus();
        });
    }

    public void Disable(bool restoreIfHidden = false)
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer = null;
        }
        if (_icon != null)
        {
            try
            {
                _icon.Visible = false;
                _icon.ContextMenuStrip?.Dispose();
                _icon.Dispose();
            }
            catch { }
            _icon = null;
        }
        if (restoreIfHidden && !_disposed)
        {
            try
            {
                _owner.Dispatcher.Invoke(() =>
                {
                    if (!_owner.IsVisible) _owner.Show();
                });
            }
            catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disable(restoreIfHidden: false);
    }
}
