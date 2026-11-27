using System.IO;

namespace AICleaner;

public sealed class ZoneDefinition
{
    public required ZoneId Id { get; init; }
    public required string Slug { get; init; }
    public required string DisplayName { get; init; }
    public required string DisplayNameEn { get; init; }
    public required string ShortName { get; init; }
    public required string ShortNameEn { get; init; }
    public required string Subtitle { get; init; }
    public required string SubtitleEn { get; init; }
    public required string AccentHex { get; init; }
    public required string IconBgHex { get; init; }
    public required string IconPathData { get; init; }
    public required bool IconFillMode { get; init; }
    public required string[] ProcessNames { get; init; }
    public bool IsReadOnlyZone { get; init; }
    public bool HasScheduledTaskStrip { get; init; }
    public required Func<bool> DetectInstalled { get; init; }
    public required Func<CleanerSettings, string> ResolveDefaultRadarPath { get; init; }

    public string LocalizedDisplayName => Loc.IsEnglish ? DisplayNameEn : DisplayName;
    public string LocalizedShortName => Loc.IsEnglish ? ShortNameEn : ShortName;
    public string LocalizedSubtitle => Loc.IsEnglish ? SubtitleEn : Subtitle;
}

public static class ZoneCatalog
{
    private static readonly string UserHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string LocalApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static readonly IReadOnlyList<ZoneDefinition> All = new List<ZoneDefinition>
    {
        new()
        {
            Id = ZoneId.Antigravity,
            Slug = "antigravity",
            DisplayName = "Antigravity 专区",
            DisplayNameEn = "Antigravity Zone",
            ShortName = "Antigravity 专区",
            ShortNameEn = "Antigravity",
            Subtitle = "覆盖 Antigravity 更新器安装包残留、运行缓存、崩溃/客户端日志、历史会话产物与离线浏览器缓存；硬保护 conversations 主对话库与登录态。",
            SubtitleEn = "Covers updater leftovers, runtime caches, crash/client logs, stale session artifacts, and offline browser caches; hard-protects conversations DB and auth state.",
            AccentHex = "#2563EB",
            IconBgHex = "#E4F0FF",
            IconPathData = "M7,1.5 L8.6,5.4 L12.5,7 L8.6,8.6 L7,12.5 L5.4,8.6 L1.5,7 L5.4,5.4 Z",
            IconFillMode = true,
            ProcessNames = new[] { "Antigravity", "Antigravity IDE", "language_server" },
            DetectInstalled = () =>
                Directory.Exists(Path.Combine(UserHome, @".gemini\antigravity")) ||
                Directory.Exists(Path.Combine(Roaming, "Antigravity")) ||
                Directory.Exists(Path.Combine(LocalApp, @"antigravity-updater")),
            ResolveDefaultRadarPath = _ => Path.Combine(UserHome, @".gemini\antigravity")
        },
        new()
        {
            Id = ZoneId.GptCodex,
            Slug = "gpt-codex",
            DisplayName = "GPT / Codex 专区",
            DisplayNameEn = "GPT / Codex Zone",
            ShortName = "GPT / Codex 专区",
            ShortNameEn = "GPT / Codex",
            Subtitle = "面向 ChatGPT 桌面端与 Codex 工作区：覆盖 temp、自定义工作区 _tmp/__pycache__ 与超期大目录（回收站）及离线浏览器缓存；硬保护 .codex 核心库、SQLite、runtimes 及全部联接。",
            SubtitleEn = "For ChatGPT Desktop & Codex workspaces: covers custom cache roots, workspace scratch dirs, stale folders (Recycle Bin), and offline caches; protects .codex SQLite & junctions.",
            AccentHex = "#0284C7",
            IconBgHex = "#E0F4FF",
            IconPathData = "M2.5,4 L6,7 L2.5,10 M7.5,10 H12",
            IconFillMode = false,
            ProcessNames = new[] { "ChatGPT", "codex", "codex-code-mode-host", "codex-computer-use-swift" },
            HasScheduledTaskStrip = true,
            DetectInstalled = () =>
                Directory.Exists(Path.Combine(UserHome, ".codex")) ||
                !string.IsNullOrWhiteSpace(SettingsStore.Load().CustomCodexCacheRoot) ||
                SettingsStore.Load().ExtraWorkspaceRoots.Any(Directory.Exists),
            ResolveDefaultRadarPath = s =>
                s.ExtraWorkspaceRoots.FirstOrDefault(Directory.Exists)
                ?? (!string.IsNullOrWhiteSpace(s.CustomCodexCacheRoot) && Directory.Exists(s.CustomCodexCacheRoot) ? s.CustomCodexCacheRoot : Path.Combine(UserHome, ".codex"))
        },
        new()
        {
            Id = ZoneId.DeepSeekHarness,
            Slug = "deepseek-harness",
            DisplayName = "DeepSeek Harness 专区",
            DisplayNameEn = "DeepSeek Harness Zone",
            ShortName = "DeepSeek Harness",
            ShortNameEn = "DeepSeek Harness",
            Subtitle = "覆盖 DSH 更新器残留、.dsh/cache、历史配置备份、超 14 天旧附件（回收站）与离线桌面及分区缓存；硬保护 .dsh/profiles、skills 联接、sessions 与账号凭据。",
            SubtitleEn = "Covers DSH updater leftovers, .dsh/cache, legacy profile backups, >14d attachments (Recycle Bin), and offline caches; hard-protects profiles, skills junctions, and credentials.",
            AccentHex = "#4F46E5",
            IconBgHex = "#ECEBFF",
            IconPathData = "M7,1.5 L12.5,4.5 L7,7.5 L1.5,4.5 Z M1.5,8.2 L7,11.2 L12.5,8.2",
            IconFillMode = false,
            ProcessNames = new[] { "DeepSeek Harness", "dsh", "dsh-desktop" },
            DetectInstalled = () =>
                Directory.Exists(Path.Combine(UserHome, ".dsh")) ||
                Directory.Exists(Path.Combine(Roaming, @"@deepseek-ai\dsh-desktop")),
            ResolveDefaultRadarPath = _ => Path.Combine(UserHome, ".dsh")
        },
        new()
        {
            Id = ZoneId.ClaudeCode,
            Slug = "claude-code",
            DisplayName = "Claude Code 专区（只读诊断）",
            DisplayNameEn = "Claude Code Zone (Read-Only)",
            ShortName = "Claude Code",
            ShortNameEn = "Claude Code",
            Subtitle = "严格只读安全诊断模式：盘点 ~/.claude 下 debug、file-history、shell-snapshots 与 projects 空间占用并支持「空间雷达」下钻；硬保护历史编辑回滚副本与凭据，绝不自动删除。",
            SubtitleEn = "Strict read-only diagnostics: audits ~/.claude (debug, file-history, shell-snapshots, projects) with Space Radar drill-down. Protects edit rollback history and credentials (0 deletions).",
            AccentHex = "#D97706",
            IconBgHex = "#FEF3C7",
            IconPathData = "M7,2 A5,5 0 1 1 2,7 M7,4.5 V7.2 M7,9.6 V9.8",
            IconFillMode = false,
            ProcessNames = new[] { "claude", "Claude" },
            IsReadOnlyZone = true,
            DetectInstalled = () =>
                Directory.Exists(Path.Combine(UserHome, ".claude")) ||
                File.Exists(Path.Combine(UserHome, ".claude.json")),
            ResolveDefaultRadarPath = _ => Path.Combine(UserHome, ".claude")
        },
        new()
        {
            Id = ZoneId.AiToolchains,
            Slug = "ai-toolchains",
            DisplayName = "AI 工具链与模型雷达",
            DisplayNameEn = "AI Toolchains & Model Radar",
            ShortName = "AI 工具链 / 模型",
            ShortNameEn = "AI Toolchains",
            Subtitle = "统一管理 AI Agent 公共依赖缓存（uv / pip / npm 超期下载缓存可安全清理，自动跳过联接）；pnpm 硬链接库、Playwright/Puppeteer 浏览器与 Hugging Face/Ollama 模型仅做只读雷达诊断，绝不误删。",
            SubtitleEn = "Manages shared AI dev caches (safe expired uv/pip/npm sweeps skipping junctions); pnpm store, Playwright/Puppeteer, and Hugging Face/Ollama/LM Studio remain read-only radar.",
            AccentHex = "#0D9488",
            IconBgHex = "#CCFBF1",
            IconPathData = "M2.5,4.5 H11.5 M2.5,7.5 H11.5 M2.5,10.5 H8.5",
            IconFillMode = false,
            ProcessNames = new[] { "uv", "pip", "npm", "pnpm", "ollama", "ollama app", "LM Studio" },
            DetectInstalled = () =>
                Directory.Exists(Path.Combine(LocalApp, @"uv\cache")) ||
                Directory.Exists(Path.Combine(LocalApp, @"pip\Cache")) ||
                Directory.Exists(Path.Combine(LocalApp, "npm-cache")) ||
                Directory.Exists(Path.Combine(Roaming, "npm-cache")) ||
                Directory.Exists(Path.Combine(LocalApp, @"pnpm\store")) ||
                Directory.Exists(Path.Combine(LocalApp, "ms-playwright")) ||
                Directory.Exists(Path.Combine(UserHome, @".cache\huggingface")) ||
                Directory.Exists(Path.Combine(UserHome, ".ollama")) ||
                Directory.Exists(Path.Combine(UserHome, @".cache\lm-studio")),
            ResolveDefaultRadarPath = _ =>
            {
                foreach (var p in new[]
                {
                    Path.Combine(UserHome, @".cache\huggingface"),
                    Path.Combine(LocalApp, @"uv\cache"),
                    Path.Combine(LocalApp, "ms-playwright"),
                    Path.Combine(LocalApp, @"pnpm\store")
                })
                {
                    if (Directory.Exists(p)) return p;
                }
                return LocalApp;
            }
        },
        new()
        {
            Id = ZoneId.AiIdes,
            Slug = "ai-ides",
            DisplayName = "AI IDE 专区（Cursor / Windsurf / Trae）",
            DisplayNameEn = "AI IDEs Zone (Cursor / Windsurf / Trae)",
            ShortName = "AI IDE 专区",
            ShortNameEn = "AI IDEs",
            Subtitle = "覆盖 Cursor、Windsurf 与 Trae 的顶层离线 Electron 浏览器缓存（按对应 IDE 进程独立加锁，退出后可清）与 state.vscdb / workspaceStorage 只读诊断；硬保护 User 设置、登录态、扩展与 CachedExtensionVSIXs。",
            SubtitleEn = "Covers verified top-level offline Electron caches for Cursor, Windsurf, and Trae (per-IDE process locking) plus read-only state.vscdb & workspaceStorage telemetry; hard-protects User, auth & extensions.",
            AccentHex = "#7C3AED",
            IconBgHex = "#F3E8FF",
            IconPathData = "M4.5,4 L2,7 L4.5,10 M9.5,4 L12,7 L9.5,10 M8,3 L6,11",
            IconFillMode = false,
            ProcessNames = new[] { "Cursor", "Windsurf", "Trae" },
            DetectInstalled = () =>
                Directory.Exists(Path.Combine(Roaming, "Cursor")) ||
                Directory.Exists(Path.Combine(Roaming, "Windsurf")) ||
                Directory.Exists(Path.Combine(Roaming, "Trae")),
            ResolveDefaultRadarPath = _ =>
            {
                foreach (var p in new[]
                {
                    Path.Combine(Roaming, "Cursor"),
                    Path.Combine(Roaming, "Windsurf"),
                    Path.Combine(Roaming, "Trae")
                })
                {
                    if (Directory.Exists(p)) return p;
                }
                return Roaming;
            }
        },
        new()
        {
            Id = ZoneId.LocalAiEcosystem,
            Slug = "local-ai-ecosystem",
            DisplayName = "本地 AI 生态与虚拟盘雷达（只读）",
            DisplayNameEn = "Local AI & VHDX Radar (Read-Only)",
            ShortName = "本地生态 / VHDX",
            ShortNameEn = "Local AI / VHDX",
            Subtitle = "严格只读诊断：巡检 WSL2 / Docker Desktop ext4.vhdx 虚拟盘膨胀、Cline / Roo Code / Continue 插件数据、ComfyUI 本地资产及受控只读社区规则包（0 删除候选，提供官方安全瘦身命令提示）。",
            SubtitleEn = "Strict read-only radar: audits WSL2 / Docker ext4.vhdx virtual disks, Cline / Roo Code / Continue storage, ComfyUI assets, and read-only community diagnostic packs (0 deletions).",
            AccentHex = "#0369A1",
            IconBgHex = "#E0F2FE",
            IconPathData = "M2.5,5 C2.5,3.4 4.5,2.5 7,2.5 C9.5,2.5 11.5,3.4 11.5,5 V9.5 C11.5,11.1 9.5,12 7,12 C4.5,12 2.5,11.1 2.5,9.5 Z M2.5,5 C2.5,6.5 4.5,7.4 7,7.4 C9.5,7.4 11.5,6.5 11.5,5",
            IconFillMode = false,
            ProcessNames = new[] { "Docker Desktop", "com.docker.backend", "vmmem", "vmmemWSL" },
            IsReadOnlyZone = true,
            DetectInstalled = () =>
                Directory.Exists(Path.Combine(UserHome, ".continue")) ||
                Directory.Exists(Path.Combine(LocalApp, @"Docker\wsl")) ||
                Directory.Exists(Path.Combine(LocalApp, "wsl")) ||
                Directory.Exists(Path.Combine(UserHome, "ComfyUI")) ||
                DiagnosticPackLoader.HasAnyInstalledPacks(),
            ResolveDefaultRadarPath = _ =>
            {
                foreach (var p in new[]
                {
                    Path.Combine(LocalApp, @"Docker\wsl"),
                    Path.Combine(UserHome, "ComfyUI"),
                    Path.Combine(UserHome, ".continue")
                })
                {
                    if (Directory.Exists(p)) return p;
                }
                return LocalApp;
            }
        }
    };

    private static readonly Dictionary<ZoneId, ZoneDefinition> ById = All.ToDictionary(z => z.Id);
    private static readonly Dictionary<string, ZoneDefinition> BySlug = All.ToDictionary(z => z.Slug, StringComparer.OrdinalIgnoreCase);

    public static ZoneDefinition Get(ZoneId id) => ById[id];

    public static ZoneDefinition? GetByNameOrSlug(string nameOrSlug)
    {
        if (string.IsNullOrWhiteSpace(nameOrSlug)) return null;
        if (BySlug.TryGetValue(nameOrSlug.Trim(), out var bySlug)) return bySlug;
        if (Enum.TryParse<ZoneId>(nameOrSlug.Trim(), true, out var id) && ById.TryGetValue(id, out var byId))
            return byId;
        return null;
    }

    public static bool TryParseSlug(string slug, out ZoneId zone)
    {
        var def = GetByNameOrSlug(slug);
        if (def != null)
        {
            zone = def.Id;
            return true;
        }
        zone = default;
        return false;
    }
}
