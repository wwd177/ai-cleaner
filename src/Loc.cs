namespace AICleaner;

/// <summary>
/// Zero-dependency runtime localization dictionary (zh-CN / en-US).
/// Switchable instantly without restarting the WPF window.
/// </summary>
public static class Loc
{
    private static volatile string _lang = "zh-CN";

    public static string CurrentLanguage => _lang;
    public static bool IsEnglish => _lang.Equals("en-US", StringComparison.OrdinalIgnoreCase);

    public static string NormalizeLanguage(string? lang) =>
        string.Equals(lang?.Trim(), "en-US", StringComparison.OrdinalIgnoreCase) ? "en-US" : "zh-CN";

    public static void SetLanguage(string? lang)
    {
        _lang = NormalizeLanguage(lang);
    }

    private static readonly Dictionary<string, string> Zh = new(StringComparer.Ordinal)
    {
        ["AppTitle"] = "AI 清理助手 v3.2 · 跨平台 AI 生态安全维护",
        ["BrandName"] = "AI 清理助手",
        ["BrandSub"] = "AI 生态 7 专区 · 零干扰安全维护",
        ["ChooseZone"] = "选择清理 / 诊断专区",
        ["BtnOverview"] = "跨区总览 · Top 10 大户",
        ["BtnScanAll"] = "一键扫描全部专区",
        ["BtnQuickSafeClean"] = "一键清全区绿区安全缓存",
        ["BtnQuickSafeCleanDone"] = "全区绿区已洁净",
        ["DiskTitle"] = "磁盘可用空间",
        ["DriveC"] = "C 盘 (系统)",
        ["RecycleBin"] = "回收站占用",
        ["BtnHistory"] = "清理历史",
        ["BtnSettings"] = "规则/白名单",
        ["BtnRecycleBin"] = "打开回收站",
        ["BtnRedlines"] = "红线说明",
        ["BtnRescan"] = "重新扫描本区",
        ["BtnExport"] = "导出体检报告",
        ["BtnLangToggle"] = "EN",
        ["KpiSafeTitle"] = "绿区 · 在线安全可清（运行中可用）",
        ["KpiRecycleTitle"] = "黄区 · 移入回收站瘦身（可撤销）",
        ["KpiOfflineTitle"] = "蓝区 · 离线浏览器缓存（退出后可清）",
        ["KpiDiagTitle"] = "只读雷达 · 空间诊断（绝不自动删）",
        ["BreakdownLabel"] = "空间分布",
        ["FilterAll"] = "全部",
        ["FilterSafe"] = "在线安全",
        ["FilterRecycle"] = "回收站瘦身",
        ["FilterOffline"] = "离线缓存",
        ["FilterProtected"] = "已保护/诊断",
        ["SearchPlaceholder"] = "搜索名称、路径或说明...",
        ["SortDefault"] = "按分级/体积",
        ["SortSize"] = "按体积降序",
        ["SortTime"] = "按修改时间",
        ["SortName"] = "按名称排序",
        ["BtnSelectSafe"] = "勾选在线安全项",
        ["BtnSelectReco"] = "勾选高置信推荐",
        ["BtnClearSel"] = "取消全选",
        ["BtnSpaceRadar"] = "空间雷达",
        ["BtnLocate"] = "定位文件夹",
        ["BtnWhitelist"] = "加入白名单",
        ["BtnCleanZero"] = "执行已勾选清理 (0 项)",
        ["ColSelect"] = "选",
        ["ColTier"] = "安全分级",
        ["ColItemPath"] = "项目与路径（双击打开 · 右键白名单）",
        ["ColSize"] = "体积",
        ["ColFiles"] = "文件数",
        ["ColModified"] = "最新修改",
        ["ColReason"] = "说明 / 清理反馈",
        ["InspectorDefaultTitle"] = "项目透视",
        ["InspectorDefaultPath"] = "单击列表任意项目，在此查看完整绝对路径、安全判定与清理影响评估（双击打开目录）",
        ["InspectorDefaultVerdict"] = "双层红线校验：运行中仅清理超期未占用文件，大目录与安装包残留均移入 Windows 回收站可随时还原。",
        ["BtnInspectRadar"] = "透视",
        ["BtnCopyPath"] = "复制路径",
        ["UninstalledGroup"] = "未检测到 / 其他专区",
        ["DashboardTitle"] = "跨区总览与 Top 10 空间大户",
        ["TrayOpen"] = "打开 AI 清理助手",
        ["TrayCheckDisk"] = "立即检查磁盘空间",
        ["TrayExit"] = "退出程序",
        ["TrayLowDiskTitle"] = "AI 清理助手 · 磁盘低空间预警",
        ["TrayLowDiskBody"] = "检测到 {0} 盘剩余空间仅 {1:F1} GB（低于 {2} GB 阈值），可打开 AI 清理助手查看可安全释放的 AI 缓存。"
    };

    private static readonly Dictionary<string, string> En = new(StringComparer.Ordinal)
    {
        ["AppTitle"] = "AI Cleaner v3.2 · Safe Windows AI Ecosystem Maintenance",
        ["BrandName"] = "AI Cleaner",
        ["BrandSub"] = "7-Zone AI Ecosystem · Zero-Disruption Safety",
        ["ChooseZone"] = "Select Cleanup / Diagnostic Zone",
        ["BtnOverview"] = "Cross-Zone Overview · Top 10",
        ["BtnScanAll"] = "Scan All Zones",
        ["BtnQuickSafeClean"] = "Clean All Green Safe Caches",
        ["BtnQuickSafeCleanDone"] = "All Green Caches Clean",
        ["DiskTitle"] = "Disk Free Space",
        ["DriveC"] = "Drive C (System)",
        ["RecycleBin"] = "Recycle Bin",
        ["BtnHistory"] = "History",
        ["BtnSettings"] = "Rules / Paths",
        ["BtnRecycleBin"] = "Recycle Bin",
        ["BtnRedlines"] = "Redlines",
        ["BtnRescan"] = "Rescan Zone",
        ["BtnExport"] = "Export Report",
        ["BtnLangToggle"] = "中文",
        ["KpiSafeTitle"] = "Green · Safe Online Clean (Live Apps OK)",
        ["KpiRecycleTitle"] = "Yellow · Recycle Bin Slimming (Reversible)",
        ["KpiOfflineTitle"] = "Blue · Offline Caches (Clean After Exit)",
        ["KpiDiagTitle"] = "Read-Only Radar · Diagnostics (Never Auto-Deleted)",
        ["BreakdownLabel"] = "Space Split",
        ["FilterAll"] = "All",
        ["FilterSafe"] = "Safe Online",
        ["FilterRecycle"] = "Recycle Bin",
        ["FilterOffline"] = "Offline Cache",
        ["FilterProtected"] = "Protected/Radar",
        ["SearchPlaceholder"] = "Search name, path, or reason...",
        ["SortDefault"] = "Tier / Size",
        ["SortSize"] = "Largest First",
        ["SortTime"] = "Oldest Modified",
        ["SortName"] = "Name A-Z",
        ["BtnSelectSafe"] = "Select Safe Online",
        ["BtnSelectReco"] = "Select Recommended",
        ["BtnClearSel"] = "Clear",
        ["BtnSpaceRadar"] = "Space Radar",
        ["BtnLocate"] = "Open Folder",
        ["BtnWhitelist"] = "Whitelist",
        ["BtnCleanZero"] = "Clean Selected (0)",
        ["ColSelect"] = "Sel",
        ["ColTier"] = "Safety Tier",
        ["ColItemPath"] = "Item & Path (Double-click open · Right-click whitelist)",
        ["ColSize"] = "Size",
        ["ColFiles"] = "Files",
        ["ColModified"] = "Modified",
        ["ColReason"] = "Reason / Outcome",
        ["InspectorDefaultTitle"] = "Inspector",
        ["InspectorDefaultPath"] = "Click any row to inspect full absolute path, safety verdict, and cleanup impact (double-click opens Explorer).",
        ["InspectorDefaultVerdict"] = "Dual-layer redline engine: online sweeps skip locked/recent files; large folders move safely to Windows Recycle Bin.",
        ["BtnInspectRadar"] = "Radar",
        ["BtnCopyPath"] = "Copy Path",
        ["UninstalledGroup"] = "Undetected / Other Zones",
        ["DashboardTitle"] = "Cross-Zone Overview & Top 10 Heavyweights",
        ["TrayOpen"] = "Open AI Cleaner",
        ["TrayCheckDisk"] = "Check Disk Space Now",
        ["TrayExit"] = "Exit",
        ["TrayLowDiskTitle"] = "AI Cleaner · Low Disk Space Alert",
        ["TrayLowDiskBody"] = "Drive {0} has only {1:F1} GB free (below {2} GB threshold). Open AI Cleaner to inspect safe reclaimable AI caches."
    };

    public static string T(string key)
    {
        var dict = IsEnglish ? En : Zh;
        if (dict.TryGetValue(key, out var val)) return val;
        if (Zh.TryGetValue(key, out var fb)) return fb;
        return key;
    }

    public static string F(string key, params object[] args) => string.Format(T(key), args);

    /// <summary>Verifies 1:1 key parity between zh-CN and en-US dictionaries (tested by --self-test).</summary>
    public static bool VerifyParity() =>
        Zh.Count > 0 && Zh.Count == En.Count && Zh.Keys.All(k => En.ContainsKey(k));
}
