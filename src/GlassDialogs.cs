using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace AICleaner;

public static class GlassDialogs
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void ApplyIceBlueTitleBar(Window win)
    {
        TextOptions.SetTextFormattingMode(win, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(win, TextRenderingMode.ClearType);
        TextOptions.SetTextHintingMode(win, TextHintingMode.Fixed);
        RenderOptions.SetClearTypeHint(win, ClearTypeHint.Enabled);
        win.UseLayoutRounding = true;
        win.SnapsToDevicePixels = true;

        win.SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(win).Handle;
                int dark = 0;
                DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
                int captionColor = 0x00FFF6EE; // #EEF6FF in COLORREF (0x00BBGGRR)
                DwmSetWindowAttribute(hwnd, 35, ref captionColor, sizeof(int));
                int textColor = 0x00361C0A;    // #0A1C36
                DwmSetWindowAttribute(hwnd, 36, ref textColor, sizeof(int));
                int borderColor = 0x00F8DFC8;  // #C8DFF8
                DwmSetWindowAttribute(hwnd, 34, ref borderColor, sizeof(int));
                int cornerPref = 2;            // DWMWCP_ROUND (Windows 11 native rounded corners)
                DwmSetWindowAttribute(hwnd, 33, ref cornerPref, sizeof(int));
            }
            catch { }
        };
    }

    public static void ShowHistoryDialog(Window owner)
    {
        var audits = SafetyGuard.LoadHistoryAudits();
        long totalFreed = audits.Sum(a => a.TotalFreedBytes);

        var win = new Window
        {
            Title = "清理历史与累计释放统计",
            Owner = owner,
            Width = 760,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new LinearGradientBrush(Color.FromRgb(246, 250, 255), Color.FromRgb(230, 241, 255), 45),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Foreground = new SolidColorBrush(Color.FromRgb(15, 33, 61)),
            ResizeMode = ResizeMode.CanResizeWithGrip
        };
        ApplyIceBlueTitleBar(win);

        var root = new DockPanel { Margin = new Thickness(18) };

        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(207, 226, 248)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 0, 0, 12)
        };
        var hGrid = new Grid();
        hGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hLeft = new StackPanel();
        hLeft.Children.Add(new TextBlock
        {
            Text = $"累计安全释放：{CleanCandidate.FormatBytes(totalFreed)}（共 {audits.Count} 次记录）",
            FontSize = 15,
            FontWeight = FontWeights.Bold
        });
        hLeft.Children.Add(new TextBlock
        {
            Text = $"所有操作均保存在 {SafetyGuard.LogsDir}，回收站项可随时还原。",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(71, 98, 130)),
            Margin = new Thickness(0, 3, 0, 0)
        });
        hGrid.Children.Add(hLeft);

        var btnLogs = new Button
        {
            Content = "打开日志文件夹",
            Style = (Style)Application.Current.FindResource("GlassBtn"),
            VerticalAlignment = VerticalAlignment.Center
        };
        btnLogs.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(SafetyGuard.LogsDir);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SafetyGuard.LogsDir}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(win, $"无法打开日志文件夹：\n{SafetyGuard.LogsDir}\n\n原因：{ex.Message}", "打开日志失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        Grid.SetColumn(btnLogs, 1);
        hGrid.Children.Add(btnLogs);
        header.Child = hGrid;
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var btnClose = new Button
        {
            Content = "关闭",
            Style = (Style)Application.Current.FindResource("PrimaryGlassBtn"),
            MinWidth = 88,
            IsCancel = true
        };
        btnClose.Click += (_, _) => win.Close();
        footer.Children.Add(btnClose);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var listBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(207, 226, 248)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8)
        };
        if (audits.Count == 0)
        {
            listBorder.Child = new TextBlock
            {
                Text = "暂无清理执行记录。\n你目前只执行过只读扫描，勾选项目并确认清理后会在这里展示明细。",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(117, 143, 173)),
                FontSize = 13
            };
        }
        else
        {
            var lb = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ItemsSource = audits
            };
            lb.ItemTemplate = CreateHistoryTemplate();
            listBorder.Child = lb;
        }
        root.Children.Add(listBorder);
        win.Content = root;
        win.ShowDialog();
    }

    private static DataTemplate CreateHistoryTemplate()
    {
        var dt = new DataTemplate(typeof(CleanRunAudit));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(190, 244, 249, 255)));
        bd.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(215, 232, 250)));
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        bd.SetValue(Border.PaddingProperty, new Thickness(12, 8, 12, 8));
        bd.SetValue(Border.MarginProperty, new Thickness(2, 3, 2, 3));

        var sp = new FrameworkElementFactory(typeof(StackPanel));

        var row1 = new FrameworkElementFactory(typeof(TextBlock));
        row1.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(CleanRunAudit.DisplayTime))
        {
            StringFormat = "{0}"
        });
        row1.SetValue(TextBlock.FontSizeProperty, 11.5);
        row1.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(117, 143, 173)));
        sp.AppendChild(row1);

        var row2 = new FrameworkElementFactory(typeof(TextBlock));
        row2.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(CleanRunAudit.ZoneLabel)));
        row2.SetValue(TextBlock.FontSizeProperty, 13.5);
        row2.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        row2.SetValue(TextBlock.MarginProperty, new Thickness(0, 2, 0, 2));
        sp.AppendChild(row2);

        var row3 = new FrameworkElementFactory(typeof(TextBlock));
        row3.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(CleanRunAudit.SummaryLine)));
        row3.SetValue(TextBlock.FontSizeProperty, 12.0);
        row3.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(11, 135, 91)));
        sp.AppendChild(row3);

        bd.AppendChild(sp);
        dt.VisualTree = bd;
        return dt;
    }

    public static bool ShowSettingsAndWhitelistDialog(Window owner)
    {
        var current = SettingsStore.Load();
        bool changed = false;

        var win = new Window
        {
            Title = "扫描阈值设置与白名单管理",
            Owner = owner,
            Width = 680,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new LinearGradientBrush(Color.FromRgb(246, 250, 255), Color.FromRgb(230, 241, 255), 45),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Foreground = new SolidColorBrush(Color.FromRgb(15, 33, 61)),
            ResizeMode = ResizeMode.NoResize
        };
        ApplyIceBlueTitleBar(win);

        var root = new DockPanel { Margin = new Thickness(18) };

        // Top policy card
        var policyCard = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(207, 226, 248)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 12)
        };
        var pStack = new StackPanel();
        pStack.Children.Add(new TextBlock
        {
            Text = "扫描策略阈值（修改后立即持久化并重新扫描）",
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var pGrid = new Grid();
        pGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        pGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        pGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var cmbTempDays = MakeChoiceBox(new[] { 1, 3, 7, 14 }, current.TempMinAgeDays, " 天");
        var cmbWsMB = MakeChoiceBox(new[] { 50, 100, 200, 500 }, current.WorkspaceMinSizeMB, " MB");
        var cmbWsDays = MakeChoiceBox(new[] { 3, 7, 14, 30 }, current.WorkspaceMinAgeDays, " 天");

        pGrid.Children.Add(MakeLabeledField("临时缓存保留天数", cmbTempDays, 0));
        pGrid.Children.Add(MakeLabeledField("工作区大目录体积门槛", cmbWsMB, 2));
        pGrid.Children.Add(MakeLabeledField("工作区最小静置宽限期", cmbWsDays, 4));
        pStack.Children.Add(pGrid);
        policyCard.Child = pStack;
        DockPanel.SetDock(policyCard, Dock.Top);
        root.Children.Add(policyCard);

        // Footer buttons
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var btnCancel = new Button
        {
            Content = "取消",
            Style = (Style)Application.Current.FindResource("GlassBtn"),
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 80,
            IsCancel = true
        };
        btnCancel.Click += (_, _) => win.Close();

        var btnSave = new Button
        {
            Content = "保存并重新扫描",
            Style = (Style)Application.Current.FindResource("PrimaryGlassBtn")
        };
        footer.Children.Add(btnCancel);
        footer.Children.Add(btnSave);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // Whitelist card
        var wlCard = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(207, 226, 248)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12)
        };
        var wlDock = new DockPanel();
        var wlTop = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        wlTop.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        wlTop.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        wlTop.Children.Add(new TextBlock
        {
            Text = "用户永久保留白名单（在主列表右键或点「加入白名单」即可添加）",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var btnRemoveWl = new Button
        {
            Content = "移出选中白名单",
            Style = (Style)Application.Current.FindResource("GlassBtn"),
            Padding = new Thickness(10, 4, 10, 4)
        };
        Grid.SetColumn(btnRemoveWl, 1);
        wlTop.Children.Add(btnRemoveWl);
        DockPanel.SetDock(wlTop, Dock.Top);
        wlDock.Children.Add(wlTop);

        var wlList = new ListBox
        {
            Background = new SolidColorBrush(Color.FromArgb(180, 244, 249, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(215, 232, 250)),
            BorderThickness = new Thickness(1),
            FontSize = 12
        };
        foreach (var p in current.WhitelistedPaths) wlList.Items.Add(p);
        wlDock.Children.Add(wlList);
        wlCard.Child = wlDock;
        root.Children.Add(wlCard);

        btnRemoveWl.Click += (_, _) =>
        {
            if (wlList.SelectedItem is string s)
            {
                SettingsStore.RemoveWhitelist(s);
                wlList.Items.Remove(s);
                changed = true;
            }
        };

        btnSave.Click += (_, _) =>
        {
            var fresh = SettingsStore.Load();
            fresh.TempMinAgeDays = (int)((ComboBoxItem)cmbTempDays.SelectedItem).Tag;
            fresh.WorkspaceMinSizeMB = (int)((ComboBoxItem)cmbWsMB.SelectedItem).Tag;
            fresh.WorkspaceMinAgeDays = (int)((ComboBoxItem)cmbWsDays.SelectedItem).Tag;
            SettingsStore.Save(fresh);
            changed = true;
            win.Close();
        };

        win.Content = root;
        win.ShowDialog();
        return changed;
    }

    /// <summary>Roomy / Mole inspired interactive Read-Only Space Map & Top-N Large File Radar.</summary>
    public static bool ShowSpaceMapDialog(Window owner, string initialPath, string title)
    {
        bool whitelistChanged = false;
        string currentPath = File.Exists(initialPath) ? (Path.GetDirectoryName(initialPath) ?? initialPath) : initialPath;
        var navStack = new Stack<string>();

        var win = new Window
        {
            Title = $"空间雷达透视 (只读) · {title}",
            Owner = owner,
            Width = 860,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new LinearGradientBrush(Color.FromRgb(246, 250, 255), Color.FromRgb(230, 241, 255), 45),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Foreground = new SolidColorBrush(Color.FromRgb(15, 33, 61)),
            ResizeMode = ResizeMode.CanResizeWithGrip
        };
        ApplyIceBlueTitleBar(win);

        var root = new DockPanel { Margin = new Thickness(18) };

        // Top glass header with path + Back + summary
        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(228, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(198, 222, 248)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 0, 0, 12)
        };
        var hGrid = new Grid();
        hGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hLeft = new StackPanel();
        var txtPath = new TextBlock
        {
            Text = currentPath,
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var txtSummary = new TextBlock
        {
            Text = "正在并行透视空间分布（100% 只读，不穿越 NTFS 目录联接）...",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(71, 98, 130)),
            Margin = new Thickness(0, 3, 0, 0)
        };
        hLeft.Children.Add(txtPath);
        hLeft.Children.Add(txtSummary);
        hGrid.Children.Add(hLeft);

        var btnBack = new Button
        {
            Content = "← 返回上级",
            Style = (Style)Application.Current.FindResource("GlassBtn"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            IsEnabled = false
        };
        Grid.SetColumn(btnBack, 1);
        hGrid.Children.Add(btnBack);
        header.Child = hGrid;
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // Bottom actions
        var bottomBar = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var leftBtns = new StackPanel { Orientation = Orientation.Horizontal };
        var btnOpen = new Button
        {
            Content = "定位文件夹",
            Style = (Style)Application.Current.FindResource("GlassBtn"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        var btnWl = new Button
        {
            Content = "加入保留白名单",
            Style = (Style)Application.Current.FindResource("GlassBtn"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        leftBtns.Children.Add(btnOpen);
        leftBtns.Children.Add(btnWl);
        DockPanel.SetDock(leftBtns, Dock.Left);
        bottomBar.Children.Add(leftBtns);

        var btnClose = new Button
        {
            Content = "关闭",
            Style = (Style)Application.Current.FindResource("PrimaryGlassBtn"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Width = 90,
            IsCancel = true
        };
        btnClose.Click += (_, _) => win.Close();
        bottomBar.Children.Add(btnClose);
        DockPanel.SetDock(bottomBar, Dock.Bottom);
        root.Children.Add(bottomBar);

        // Main Space Map DataGrid with proportion bar template
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(222, 236, 252)),
            Background = new SolidColorBrush(Color.FromArgb(232, 255, 255, 255)),
            RowBackground = Brushes.Transparent,
            AlternatingRowBackground = new SolidColorBrush(Color.FromArgb(72, 232, 243, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(198, 222, 248)),
            BorderThickness = new Thickness(1),
            RowHeight = 36,
            FontSize = 12.5
        };

        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "类型",
            Binding = new System.Windows.Data.Binding("TypeBadge"),
            Width = 56
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "名称（双击子目录可下钻）",
            Binding = new System.Windows.Data.Binding("Name"),
            Width = new DataGridLength(1.6, DataGridLengthUnitType.Star)
        });

        // Proportion visual bar column
        var barCol = new DataGridTemplateColumn { Header = "体积占比", Width = 178 };
        var barTpl = new DataTemplate();
        var spFactory = new FrameworkElementFactory(typeof(StackPanel));
        spFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        spFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var trackFactory = new FrameworkElementFactory(typeof(Border));
        trackFactory.SetValue(FrameworkElement.WidthProperty, 112.0);
        trackFactory.SetValue(FrameworkElement.HeightProperty, 8.0);
        trackFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        trackFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(219, 234, 254)));
        trackFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        trackFactory.SetValue(UIElement.ClipToBoundsProperty, true);

        var fillFactory = new FrameworkElementFactory(typeof(Border));
        fillFactory.SetBinding(FrameworkElement.WidthProperty, new System.Windows.Data.Binding("BarWidth"));
        fillFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        fillFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        fillFactory.SetValue(Border.BackgroundProperty, new LinearGradientBrush(Color.FromRgb(56, 189, 248), Color.FromRgb(37, 99, 235), 0));
        trackFactory.AppendChild(fillFactory);
        spFactory.AppendChild(trackFactory);

        var pctFactory = new FrameworkElementFactory(typeof(TextBlock));
        pctFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("ShareText"));
        pctFactory.SetValue(TextBlock.FontSizeProperty, 11.5);
        pctFactory.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(71, 98, 130)));
        spFactory.AppendChild(pctFactory);

        barTpl.VisualTree = spFactory;
        barCol.CellTemplate = barTpl;
        grid.Columns.Add(barCol);

        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "大小",
            Binding = new System.Windows.Data.Binding("SizeText"),
            Width = 92
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "文件数",
            Binding = new System.Windows.Data.Binding("FileCount"),
            Width = 72
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "保护状态",
            Binding = new System.Windows.Data.Binding("ProtectionNote"),
            Width = new DataGridLength(1.0, DataGridLengthUnitType.Star)
        });

        root.Children.Add(grid);

        long navSeq = 0;
        CancellationTokenSource? navCts = null;
        bool isClosed = false;

        win.Closed += (_, _) =>
        {
            isClosed = true;
            try { navCts?.Cancel(); navCts?.Dispose(); } catch { }
            navCts = null;
        };

        async Task LoadDirectoryAsync(string path)
        {
            if (isClosed) return;
            long mySeq = ++navSeq;
            try { navCts?.Cancel(); navCts?.Dispose(); } catch { }
            navCts = new CancellationTokenSource();
            var ct = navCts.Token;

            currentPath = path;
            txtPath.Text = path;
            txtSummary.Text = "正在并行计算子项体积（跳过 NTFS 联接）...";
            btnBack.IsEnabled = navStack.Count > 0;
            grid.ItemsSource = null;

            try
            {
                var sw = Stopwatch.StartNew();
                var (totalBytes, entries) = await Task.Run(() => SafetyGuard.InspectTopEntries(path, 50, ct), ct);
                sw.Stop();

                // Guard against stale navigation response or closed dialog
                if (isClosed || mySeq != navSeq) return;

                grid.ItemsSource = entries;
                txtSummary.Text = $"当前目录可见总计 {CleanCandidate.FormatBytes(totalBytes)} · 展示 Top {entries.Count} 项 · 耗时 {sw.ElapsedMilliseconds} ms（100% 只读）";
            }
            catch (OperationCanceledException)
            {
                // Intentionally cancelled by rapid navigation or dialog close
            }
            catch (Exception ex)
            {
                if (!isClosed && mySeq == navSeq)
                    txtSummary.Text = $"目录读取受限：{ex.Message}";
            }
        }

        grid.MouseDoubleClick += async (_, e) =>
        {
            try
            {
                // Verify user actually double-clicked a DataGridRow (not column header or blank space!)
                DependencyObject? dep = e.OriginalSource as DependencyObject;
                while (dep != null && dep is not DataGridRow)
                    dep = VisualTreeHelper.GetParent(dep);
                if (dep is DataGridRow row && row.Item is SpaceMapEntry entry && entry.IsDirectory && !entry.IsReparsePoint)
                {
                    navStack.Push(currentPath);
                    await LoadDirectoryAsync(entry.FullPath);
                }
            }
            catch { }
        };

        btnBack.Click += async (_, _) =>
        {
            try
            {
                if (navStack.Count > 0)
                {
                    string prev = navStack.Pop();
                    await LoadDirectoryAsync(prev);
                }
            }
            catch { }
        };

        btnOpen.Click += (_, _) =>
        {
            string target = (grid.SelectedItem as SpaceMapEntry)?.FullPath ?? currentPath;
            try
            {
                if (Directory.Exists(target))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
                else if (File.Exists(target))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
            }
            catch { }
        };

        btnWl.Click += (_, _) =>
        {
            if (grid.SelectedItem is not SpaceMapEntry entry)
            {
                MessageBox.Show(win, "请先在列表中选中要加入白名单的目录或文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (SettingsStore.AddWhitelist(entry.FullPath))
            {
                whitelistChanged = true;
                entry.IsProtected = true;
                grid.Items.Refresh();
                txtSummary.Text = $"已将「{entry.Name}」加入永久保留白名单！";
            }
            else
            {
                txtSummary.Text = $"「{entry.Name}」已在白名单中。";
            }
        };

        win.Content = root;
        win.Loaded += async (_, _) => await LoadDirectoryAsync(currentPath);
        win.ShowDialog();
        return whitelistChanged;
    }

    private static ComboBox MakeChoiceBox(int[] options, int selectedValue, string suffix)
    {
        var cmb = new ComboBox { Height = 30, FontSize = 12.5, VerticalContentAlignment = VerticalAlignment.Center };
        var merged = options.ToList();
        if (!merged.Contains(selectedValue))
        {
            merged.Add(selectedValue);
            merged.Sort();
        }
        int selIdx = 0;
        for (int i = 0; i < merged.Count; i++)
        {
            bool isCustom = !options.Contains(merged[i]);
            cmb.Items.Add(new ComboBoxItem
            {
                Content = isCustom ? $"{merged[i]}{suffix}（自定义）" : merged[i] + suffix,
                Tag = merged[i]
            });
            if (merged[i] == selectedValue) selIdx = i;
        }
        cmb.SelectedIndex = selIdx;
        return cmb;
    }

    private static StackPanel MakeLabeledField(string label, UIElement ctrl, int col)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(71, 98, 130)),
            Margin = new Thickness(0, 0, 0, 5)
        });
        sp.Children.Add(ctrl);
        Grid.SetColumn(sp, col);
        return sp;
    }
}
