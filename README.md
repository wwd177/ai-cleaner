# AI 清理助手 (AI Cleaner for Windows) v3.0

简体中文 | [English](./README.en.md)

[![CI & Release](https://github.com/wwd177/ai-cleaner/actions/workflows/ci-release.yml/badge.svg)](https://github.com/wwd177/ai-cleaner/actions/workflows/ci-release.yml)
[![Latest Release](https://img.shields.io/github/v/release/wwd177/ai-cleaner)](https://github.com/wwd177/ai-cleaner/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011%20x64-0284C7.svg)](#快速安装与构建)
[![.NET 10](https://img.shields.io/badge/.NET-10%20WPF-2563EB.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-0A7A4E.svg)](./LICENSE)

面向 Windows 10 / 11 的原生 `.NET 10 + WPF` 白蓝玻璃材质 **跨平台 AI 生态安全清理与空间诊断工具**，内置版本化 **CLI JSON 接口**、**官方只读 Agent Skill** 以及配套 `CodexCleaner` PowerShell 自动化脚本。

专为同时使用 **Antigravity**、**GPT / Codex Desktop (ChatGPT)**、**DeepSeek Harness (DSH)**、**Claude Code** 以及 **Python / Node / Playwright / Hugging Face / Ollama** 等 AI 工具链的开发者设计：在**绝不关闭运行中桌面端、绝不误删会话/回滚副本/登录态/NTFS 目录联接**的前提下，精准洞察并安全释放磁盘空间。

---

## v3.0 核心升级亮点

- **可扩展五专区数据驱动引擎 (`ZoneCatalog`)**：左侧导航动态渲染并自动检测已安装的 AI 生态工具；未检测到的专区自动归入底部可折叠区。
- **新增两大生态专区**：
  - **Claude Code 专区（严格只读诊断）**：安全盘点 `~/.claude` 下 `debug`、`file-history`、`shell-snapshots`、`projects` 等目录体积并支持「空间雷达」下钻；删除候选数恒为 `0`，绝不破坏历史编辑回滚副本与凭据。
  - **AI 工具链与模型雷达**：安全清理 `uv` / `pip` / `npm` 超期纯下载缓存（自动跳过 NTFS 联接）；对 `pnpm/store`（硬链接库）、`Playwright` / `Puppeteer` 浏览器、以及 `Hugging Face` / `ModelScope` / `Ollama` / `LM Studio` 模型缓存仅作**只读雷达诊断**，严禁自动删除，也不虚报可释放量。
- **GUI 可视化工作区与红区管理器**：无需手改 `settings.json`！在「规则 / 白名单」弹窗的新增分页中，使用 Windows 原生文件夹选择器可视化配置自定义 Codex 缓存目录、额外工作区根目录与额外硬保护目录。
- **版本化 CLI JSON (`schemaVersion: "1.0"`)**：支持 `--list-zones --json`、`--dry-run --json [--zone <slug>] [--output <path>]`、明确退出码（`0 / 1 / 2 / 3`）与扫描状态 telemetry（`ok / partial / cancelled / error`、`accessDeniedCount`、`elapsedMs`）。
- **官方只读 Agent Skill (`skills/ai-cleaner/SKILL.md`)**：可直接集成到 Antigravity、Claude Code 或 Codex，让 AI 助手只读盘点本机 AI 磁盘占用，而清理决策始终保留在人工 GUI 确认中。
- **GitHub Actions 自动 CI + Release**：每次推送自动执行隐私路径检查、PowerShell 语法校验、`.NET 10` 单文件打包，并对**解压后的正式 Release ZIP** 现场运行 62 项自检与非空 CLI JSON 契约验证，打 Tag 后自动发布 `AICleaner-win-x64.zip` 与 `SHA256SUMS.txt`。

---

## 五大生态专区与红线矩阵

| 专区 (`slug`) | 可安全清理目标 | 只读雷达 / 引擎硬红线保护（永不触碰） |
| :--- | :--- | :--- |
| **Antigravity 专区**<br>`antigravity` | 更新器残留（`antigravity-updater\pending`）、运行缓存、崩溃与客户端日志、超期会话产物、离线 Electron / 分区缓存 | `.gemini\antigravity\conversations` 主对话库、`.gemini\config`、`.env`、登录态（`Network` / `Local Storage` / `IndexedDB` / `Service Worker\Database`）、72 小时内活跃会话 |
| **GPT / Codex 专区**<br>`gpt-codex` | 可选自定义 Codex 缓存目录（`temp` / `pip` / `npm` / `uv` / 离线 `browser-cache` & `desktop-logs`）、自定义工作区 `_tmp`/`__pycache__`/`.pytest_cache`/`.ruff_cache` 与超期大目录（回收站）、内置每周定时任务开关 | `%USERPROFILE%\.codex` 全目录及 SQLite 数据库（仅 O(1) 只读体积诊断）、`OpenAI.Codex` MSIX 包目录、`.codex` 全部 NTFS 联接及其真实目标目录、`runtimes` |
| **DeepSeek Harness 专区**<br>`deepseek-harness` | 更新器残留（`@deepseek-aidsh-desktop-updater\pending`）、`.dsh\cache`、历史配置备份、超 14 天旧附件（回收站）、离线桌面与分区缓存 | `.dsh\profiles`（含插件联接）、`dsh-runtimes`、`skills` 联接、`sessions`、`.credentials.yaml`、登录态与凭据 |
| **Claude Code 专区**<br>`claude-code` | **无（严格只读诊断专区，0 删除候选）** | `%USERPROFILE%\.claude` 全目录与 `.claude.json`（含 `file-history` 编辑回滚副本、`shell-snapshots`、`projects`、`settings.json` 及凭据） |
| **AI 工具链与模型雷达**<br>`ai-toolchains` | 仅 `uv` / `pip` / `npm` 超期纯下载缓存（绿区，自动跳过 NTFS 联接与运行中文件） | **只读诊断雷达**：`pnpm\store`（硬链接）、`ms-playwright` / `puppeteer`、`huggingface` / `modelscope` / `ollama` / `lm-studio` 模型权重与断点文件 |

### 引擎级全局安全铁律
1. **引擎授权执行闸门（零外部高危配置）**：执行器入口按 `(专区, 动作类型, 目标路径)` 与经过校验的自定义根目录进行独立二次白名单复核，拒绝任何跨区或未授权路径；同时永久硬保护系统目录、`.ssh`、`.gnupg`、`.aws`、`.kube`、`Documents`、`Desktop` 及一切关键路径。
2. **四级隔离 + 只读诊断标识**：
   - 🟢 **绿区（在线安全）**：应用运行中即可安全清理超期未占用缓存。
   - 🟡 **黄区（回收站瘦身）**：默认不自动勾选；确认后仅移入 Windows 回收站，随时可还原。
   - 🔵 **蓝区（离线缓存）**：应用运行中自动加锁；退出切回本窗口自动解锁，离线清理过程中持续复查进程状态（应用重启即立即中止），并完整保留目录骨架。
   - 🩵 **只读 · 诊断雷达** / 🔴 **红区硬保护**：在模型层与执行层双层拦截，无法勾选、无法执行删除。
3. **全祖先链实时 ReparsePoint 校验 & 失败封闭（Fail-Closed）**：扫描、删除及回收前均沿完整祖先链复核 `ReparsePoint` 属性并跳过符号链接/目录联接；路径不可验证时一律拒绝。
4. **绝不强杀进程**：只做只读进程探测，绝不关闭正在运行的 ChatGPT / Antigravity / DSH / Claude / Ollama。
5. **62 项内置回归自检与 JSON 审计日志**：覆盖执行器越权拦截、红区、联接、只读区阻断、清理中止回调、扫描取消与 CLI JSON 混合状态契约，每次清理自动落盘 JSON 审计日志。

---

## 快速安装与构建

### 方式一：直接下载预编译版（推荐）
前往 [GitHub Releases](https://github.com/wwd177/ai-cleaner/releases/latest) 下载 `AICleaner-win-x64.zip`（附 `SHA256SUMS.txt` 校验文件），解压后双击 `AICleaner.exe` 即可运行。
> 系统需安装 Windows 10 / 11 x64 及 [.NET 10 Windows Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)。

### 方式二：从源码构建单文件可执行程序
```powershell
dotnet publish .\src\AICleaner.csproj -c Release -r win-x64 --no-self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
```

---

## CLI 只读体检与 JSON 接口 (`schemaVersion: 1.0`)

`AICleaner.exe` 同时支持无界面 CLI 只读调用（UTF-8 标准输出与 `--output` 文件写入），适合终端快查、自动化巡检与 AI Agent 集成：

```powershell
# 查看全部专区与安装检测状态（JSON）
.\publish\AICleaner.exe --list-zones --json

# 扫描全部 5 个专区并输出结构化 JSON 体检报告
.\publish\AICleaner.exe --dry-run --json

# 仅扫描指定专区（antigravity / gpt-codex / deepseek-harness / claude-code / ai-toolchains）
.\publish\AICleaner.exe --dry-run --json --zone ai-toolchains

# 直接将 JSON 报告写入文件
.\publish\AICleaner.exe --dry-run --json --output .\audit.json

# 运行全部 62 项隔离安全自检（不触碰真实用户文件）
.\publish\AICleaner.exe --self-test .\selftest.txt
```

**退出码与状态优先级规范**：`0` 正常完成（`ok`） · `1` 运行异常 · `2` 参数无效或未知专区 · `3` 降级/中断结果（顶层状态优先级：`error > cancelled > partial > ok`）。

<details>
<summary>展开查看 <code>--dry-run --json</code> 返回结构示例</summary>

```json
{
  "schemaVersion": "1.0",
  "appVersion": "3.0.0",
  "generatedAt": "2026-11-26T20:00:00.0000000+08:00",
  "readOnly": true,
  "status": "ok",
  "recycleBin": { "available": true, "bytes": 0, "items": 0, "formatted": "0 B" },
  "summary": {
    "safeOnlineBytes": 0,
    "recycleCandidateBytes": 0,
    "offlineCacheBytes": 0,
    "diagnosticBytes": 2472890649
  },
  "zones": [
    {
      "slug": "ai-toolchains",
      "id": "AiToolchains",
      "displayName": "AI 工具链与模型雷达",
      "isReadOnlyZone": false,
      "detected": true,
      "status": "ok",
      "accessDeniedCount": 0,
      "elapsedMs": 1420,
      "errorMessage": null,
      "running": false,
      "runningProcessCount": 0,
      "safeOnlineBytes": 0,
      "recycleCandidateBytes": 0,
      "offlineCacheBytes": 0,
      "diagnosticBytes": 2472890649,
      "candidates": [
        {
          "title": "pnpm 内容寻址库 (AppData\\Local\\pnpm\\store)",
          "category": "硬链接只读雷达",
          "tier": "ProtectedSkipped",
          "tierBadge": "只读 · 诊断雷达",
          "actionType": "None",
          "canSelect": false,
          "isReadOnlyDiagnostic": true,
          "sizeBytes": 2246000000,
          "sizeFormatted": "2.09 GB"
        }
      ]
    }
  ]
}
```

</details>

---

## Agent Skill 集成（Antigravity / Claude Code / Codex）

仓库内置开箱即用的只读 Agent 技能定义：[skills/ai-cleaner/SKILL.md](./skills/ai-cleaner/SKILL.md)。
将 `skills/ai-cleaner` 复制到你的 Agent Skills 目录后，即可直接让 AI 助手帮你执行：
> “用 ai-cleaner 帮我看看电脑上 Antigravity、Codex、Claude Code 和 Hugging Face 各自占了多少空间，哪些能安全清理？”

该 Skill 遵循严格只读原则：Agent 仅能调用 `--list-zones --json` 与 `--dry-run --json` 生成结构化报告，绝不会自动删除任何文件。

---

## 自定义工作区与配套 PowerShell 脚本

- **GUI 可视化配置**：在主界面点击左侧「规则 / 白名单」→ 切换到「自定义工作区与红区目录」分页即可通过原生目录选择器增删路径，自动原子写入 `%LOCALAPPDATA%\AICleaner\settings.json`（亦可参考 [settings.example.json](./settings.example.json)）。
- **PowerShell 自动化脚本 (`scripts/CodexCleaner`)**：零依赖实现 Codex 临时缓存每周定时维护，详见 [scripts/CodexCleaner/README.md](./scripts/CodexCleaner/README.md)。

---

## 目录结构

```text
ai-cleaner/
├── src/                        # .NET 10 WPF 桌面端与 CLI 源码
│   ├── ZoneCatalog.cs          # 5 专区引擎注册表、安装探测与空间雷达默认入口
│   ├── SafetyGuard.cs          # 执行器授权闸门、红线引擎、全祖先链联接校验、62 项自检
│   ├── ZoneScanner.cs          # 五专区并发只读扫描（支持取消令牌与权限受限统计）
│   ├── CliRunner.cs            # 版本化 CLI JSON (1.0) 输出与标准退出码
│   ├── MainWindow.xaml(.cs)    # 白蓝玻璃主界面、动态侧边栏、三色分布条、项目透视条
│   ├── GlassDialogs.cs         # 空间雷达、清理历史、双分页规则/工作区/红区管理器
│   ├── UserSettings.cs         # 不可变快照配置与原子落盘
│   ├── TaskSchedulerHelper.cs  # 无死锁超时可控计划任务助手
│   └── Models.cs               # 候选模型、只读雷达标识与 ScanStatus
├── skills/ai-cleaner/SKILL.md  # 官方只读 Agent Skill
├── scripts/CodexCleaner/       # 零依赖 PowerShell 清理脚本与每周定时任务工具
├── .github/workflows/          # 自动 CI、隐私门禁、Release ZIP 现场自检与发版
├── settings.example.json       # 自定义配置示例
├── CONTRIBUTING.md             # 贡献指南与新专区接入红线规范
├── README.en.md                # 英文文档
└── LICENSE                     # MIT
```

---

## 参与贡献与许可协议

欢迎提交新 AI 工具专区提案（默认从只读诊断起步）与缺陷反馈，请先阅读 [CONTRIBUTING.md](./CONTRIBUTING.md)。
本项目采用 [MIT License](./LICENSE) 开源。
