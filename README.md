# AI 清理助手 (AI Cleaner for Windows) v3.2

简体中文 | [English](./README.en.md)

[![CI & Release](https://github.com/wwd177/ai-cleaner/actions/workflows/ci-release.yml/badge.svg)](https://github.com/wwd177/ai-cleaner/actions/workflows/ci-release.yml)
[![Latest Release](https://img.shields.io/github/v/release/wwd177/ai-cleaner)](https://github.com/wwd177/ai-cleaner/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011%20x64-0284C7.svg)](#快速安装与构建)
[![.NET 10](https://img.shields.io/badge/.NET-10%20WPF-2563EB.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-0A7A4E.svg)](./LICENSE)

面向 Windows 10 / 11 的原生 `.NET 10 + WPF` 白蓝玻璃材质 **跨平台 AI 生态安全清理与空间诊断工具**，覆盖 **7 大 AI 生态专区**，集成 **跨区总览与 Top 10 空间大户排行**、**中英文实时无重启切换**、**受控只读社区诊断包体系**、**可选轻量系统托盘低空间预警**、版本化 **CLI JSON 接口**、官方 **只读 Agent Skill** 以及 **Scoop / WinGet 分发清单**。

专为重度使用 **Antigravity**、**GPT / Codex Desktop (ChatGPT)**、**DeepSeek Harness (DSH)**、**Claude Code**、**Cursor / Windsurf / Trae**、**WSL2 / Docker VHDX / ComfyUI / Cline** 以及 **uv / pip / npm / pnpm / Playwright / Hugging Face / Ollama** 的开发者设计：在**绝不关闭运行中桌面端、绝不误删会话/回滚副本/扩展/登录态/NTFS 目录联接**的前提下，精准洞察并安全释放磁盘空间。

---

## v3.2 完整路线图大满贯特性

- **7 大 AI 生态专区 (`ZoneCatalog`)**：在 v3.0 五专区基础上新增 **AI IDE 专区（Cursor / Windsurf / Trae，支持单 IDE 独立进程锁与 `state.vscdb` O(1) 诊断）** 以及 **本地 AI 生态与虚拟盘雷达（严格只读：WSL2 / Docker `ext4.vhdx` 膨胀巡检、Cline / Roo Code / Continue、ComfyUI 与受控只读社区规则包）**。
- **跨区总览 Dashboard 与 Top 10 空间大户**：一键汇总全部 7 个专区的绿/黄/蓝/只读诊断总额，展示可点击直达的专区卡片与跨区 Top 10 空间占用排行，支持双击定位文件夹或「空间雷达」下钻。
- **中英文界面实时切换（i18n）**：顶部右上角 `EN / 中文` 按钮零重启即时切换全界面语言，自动持久化至 `settings.json`。
- **受控只读社区诊断包 (`packs/`)**：允许开发者将遵循 `mode: "read-only"` 规范的社区 JSON 诊断包放入 `%LOCALAPPDATA%\AICleaner\packs\`（仓库附带 [community-ai-radar.example.json](./packs/community-ai-radar.example.json)，覆盖 Zed AI、Aider、Jan、GPT4All、OpenHands），引擎严格阻断 `..` 穿越、敏感目录与任何写权限。
- **可选轻量系统托盘低空间预警**：默认完全关闭（零后台常驻、零网络遥测）；在「规则 / 白名单」弹窗中主动开启后，仅在本机定时巡检固定盘剩余空间并在低于设定阈值（默认 30 GB）时温和提醒。
- **GUI 工作区管理器 + CLI JSON (`schemaVersion: 1.0`) + Agent Skill + Scoop / WinGet 清单**：完整覆盖桌面交互、终端自动化、AI Agent 集成与主流包管理器分发（见 [packaging/](./packaging/)）。

---

## 七大生态专区与红线矩阵

| 专区 (`slug`) | 可安全清理目标 | 只读雷达 / 引擎硬红线保护（永不触碰） |
| :--- | :--- | :--- |
| **Antigravity 专区**<br>`antigravity` | 更新器残留、运行缓存、崩溃与客户端日志、超期会话产物、离线 Electron / 分区缓存 | `.gemini\antigravity\conversations` 主对话库、`.gemini\config`、`.env`、登录态（`Network` / `Local Storage` / `IndexedDB` / `Service Worker\Database`）、72 小时内活跃会话 |
| **GPT / Codex 专区**<br>`gpt-codex` | 自定义 Codex 缓存目录（`temp` / `pip` / `npm` / `uv` / 离线 `browser-cache` & `desktop-logs`）、自定义工作区 `_tmp`/`__pycache__` 与超期大目录（回收站）、每周定时任务开关 | `%USERPROFILE%\.codex` 全目录及 SQLite（仅 O(1) 体积诊断）、`OpenAI.Codex` MSIX 包、`.codex` 全部 NTFS 联接与真实目标、`runtimes` |
| **DeepSeek Harness 专区**<br>`deepseek-harness` | 更新器残留、`.dsh\cache`、历史配置备份、超 14 天旧附件（回收站）、离线桌面与分区缓存 | `.dsh\profiles`（含插件联接）、`dsh-runtimes`、`skills` 联接、`sessions`、`.credentials.yaml` 与登录态 |
| **Claude Code 专区**<br>`claude-code` | **无（严格只读诊断专区，0 删除候选）** | `%USERPROFILE%\.claude` 全目录与 `.claude.json`（含 `file-history` 编辑回滚副本、`shell-snapshots`、`projects`、`settings.json` 及凭据） |
| **AI 工具链与模型雷达**<br>`ai-toolchains` | 仅 `uv` / `pip` / `npm` 超期纯下载缓存（绿区，跳过 NTFS 联接） | **只读雷达**：`pnpm\store`（硬链接）、`ms-playwright` / `puppeteer`、`huggingface` / `modelscope` / `ollama` / `lm-studio` |
| **AI IDE 专区**<br>`ai-ides` | 仅 Cursor / Windsurf / Trae **顶层**离线 Electron 缓存（按各自 IDE 进程独立加锁，退出后可清） | **硬保护 & 只读诊断**：`User` 设置、`globalStorage\state.vscdb`（O(1) 诊断）、`workspaceStorage`、扩展目录、`CachedExtensionVSIXs`、`.cursor`/`.codeium`/`.trae` 与登录态 |
| **本地生态 / VHDX 雷达**<br>`local-ai-ecosystem` | **无（严格只读诊断专区，0 删除候选）** | WSL2 / Docker `ext4.vhdx` 虚拟盘（附 `wsl --shutdown` + `Optimize-VHD` 官方压缩提示）、Cline / Roo Code / Continue、ComfyUI `models`/`output`、受控只读社区规则包 `packs/` |

### 引擎级全局安全铁律
1. **执行器统一授权闸门**：`SafetyGuard.ExecuteCandidate` 在物理操作前独立复核 `(Zone, ActionType, Path)` 及经过 `ValidateCustomRootPath` 校验的自定义目录，拒绝一切跨区或未授权路径；永久硬保护系统目录、`.ssh`、`.gnupg`、`.aws`、`.kube`、`Documents`、`Desktop`。
2. **四级隔离 + 只读诊断标识**：绿区在线安全 · 黄区回收站可撤销 · 蓝区离线按单产品进程加锁（清理中途重启自动中止） · 只读诊断雷达 / 红区双层拦截。
3. **全祖先链实时 ReparsePoint 校验 & 失败封闭**：扫描、删除及回收前均沿完整祖先链复核 `ReparsePoint` 并跳过符号链接/目录联接；路径不可验证时拒绝执行。
4. **70+ 项内置回归自检与 JSON 审计日志**：覆盖执行器越权反例、AI IDE 顶层缓存隔离、WSL2 VHDX 样本、恶意社区规则包拦截、双语键对齐与 CLI 混合状态契约。

---

## 快速安装与构建

### 方式一：下载预编译版（推荐）
前往 [GitHub Releases](https://github.com/wwd177/ai-cleaner/releases/latest) 下载 `AICleaner-win-x64.zip` 与 `SHA256SUMS.txt`，解压后双击 `AICleaner.exe` 运行（需 Windows 10/11 x64 与 [.NET 10 Windows Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)）。

### 方式二：Scoop / WinGet 清单
仓库 [packaging/](./packaging/) 提供完整的 Scoop Manifest (`packaging/scoop/ai-cleaner.json`)、WinGet 1.6 多文件清单 (`packaging/winget/`) 以及版本校验和同步脚本 `packaging/Update-PackageManifests.ps1`。

### 方式三：从源码构建单文件程序
```powershell
dotnet publish .\src\AICleaner.csproj -c Release -r win-x64 --no-self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
```

---

## CLI 只读体检 (`schemaVersion: 1.0`) 与 Agent Skill

```powershell
# 查看全部 7 个专区与安装检测状态（JSON）
.\publish\AICleaner.exe --list-zones --json

# 扫描全部 7 个专区并输出结构化 JSON 体检报告
.\publish\AICleaner.exe --dry-run --json

# 仅扫描指定专区（antigravity / gpt-codex / deepseek-harness / claude-code / ai-toolchains / ai-ides / local-ai-ecosystem）
.\publish\AICleaner.exe --dry-run --json --zone local-ai-ecosystem

# 运行全部隔离安全回归自检（不触碰真实用户文件）
.\publish\AICleaner.exe --self-test .\selftest.txt
```

**退出码与状态优先级**：`0` 正常（`ok`） · `1` 运行异常 · `2` 参数无效或未知专区 · `3` 降级/中断（优先级：`error > cancelled > partial > ok`）。
官方只读 Agent 技能定义见 [skills/ai-cleaner/SKILL.md](./skills/ai-cleaner/SKILL.md)；社区只读规则包说明见 [packs/README.md](./packs/README.md)；PowerShell 定时脚本见 [scripts/CodexCleaner/README.md](./scripts/CodexCleaner/README.md)。

---

## 参与贡献与许可协议

欢迎贡献只读社区诊断包、新专区提案与缺陷反馈，请先阅读 [CONTRIBUTING.md](./CONTRIBUTING.md)。
本项目采用 [MIT License](./LICENSE) 开源。
