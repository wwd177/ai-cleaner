# AI 清理助手 (AI Cleaner for Windows)

面向 Windows 10 / 11 的原生 `.NET 10 + WPF` 白蓝玻璃材质 AI 桌面端安全清理工具，配套 `CodexCleaner` PowerShell 自动化维护脚本。专为重度使用 **Antigravity**、**GPT / Codex Desktop (ChatGPT)** 与 **DeepSeek Harness (DSH)** 的开发者设计：在**绝不关闭运行中桌面端、绝不误删会话/登录态/NTFS 目录联接**的前提下安全释放磁盘空间。

---

## 核心特性

### 三大独立清理专区
| 专区 | 覆盖目标 | 硬编码红线保护（永不触碰） |
| :--- | :--- | :--- |
| **Antigravity 专区** | 更新器残留（`antigravity-updater\pending`）、运行缓存、崩溃与客户端日志、超期会话产物、离线 Electron / 分区缓存 | `.gemini\antigravity\conversations` 主对话库、`.gemini\config`、`.env`、登录态（`Network` / `Local Storage` / `IndexedDB` / `Service Worker\Database`）、72 小时内活跃会话 |
| **GPT / Codex 专区** | 全局 `uv` / `pip` 缓存、可选自定义 Codex 缓存目录（`temp` / `pip` / `npm` / `uv` / 离线 `browser-cache` & `desktop-logs`）、自定义工作区 `_tmp`/`__pycache__`/`.pytest_cache`/`.ruff_cache` 与超期大目录、内置每周定时任务开关 | `%USERPROFILE%\.codex` 全目录及 SQLite 数据库（仅做 O(1) 只读体积诊断，不直接删库）、`OpenAI.Codex` MSIX 包目录、`.codex` 全部 NTFS 联接及其真实目标目录、`runtimes` |
| **DeepSeek Harness 专区** | 更新器残留（`@deepseek-aidsh-desktop-updater\pending`）、`.dsh\cache`、历史配置备份、超 14 天旧附件（回收站）、离线桌面与分区缓存 | `.dsh\profiles`（含插件联接）、`dsh-runtimes`、`skills` 联接、`sessions`、`.credentials.yaml`、登录态与凭据 |

### 工程级安全架构
- **四级安全隔离**：
  - 🟢 **绿区（在线安全）**：应用运行中可清理超期且未被占用的临时/缓存文件（或移入回收站）。
  - 🟡 **黄区（回收站瘦身）**：默认不自动勾选；确认后仅移入 Windows 回收站，可随时还原。
  - 🔵 **蓝区（离线缓存）**：检测到对应应用运行中自动加锁；退出应用切回本窗口自动感知并解锁，清理时完整保留目录骨架。
  - 🔴 **红区 / 白名单**：底层引擎执行前双层校验（Dual-Path Validation），拒绝删除红区、受保护容器及其**全部祖先目录**。
- **NTFS 联接与祖先链保护**：删除/回收前执行全祖先链 `ReparsePoint` 校验，永不穿越或破坏符号链接与目录联接。
- **失败封闭（Fail-Closed）**：路径规范化异常、盘符根或属性不可验证时一律拒绝操作。
- **绝不强杀进程**：永远只检测进程状态，不关闭正在运行的 ChatGPT / Antigravity / DSH。
- **全量 JSON 审计与 44 项内置自检**：每次清理自动写入审计日志，并内置 `--self-test` 自动化回归验证。

### 交互与效率体验
- **白蓝磨砂玻璃 + ClearType 高清渲染**：`PerMonitorV2` 高 DPI 适配、Windows 11 原生 DWM 圆角与冰蓝标题栏、零着色器景深卡片与 DataGrid 行虚拟化回收，三专区瞬时切换（20–35 ms）。
- **交互式「空间雷达」**：只读并行透视任意目录 Top 50 子目录/大文件占比，支持双击下钻、返回上级、一键加白名单与即时取消遍历。
- **项目透视条与三色空间分布条**：单击任意行立即查看完整绝对路径与白话影响说明；左侧实时显示 C 盘/数据盘低空间预警与 Windows 回收站当前占用。
- **快捷键支持**：`Ctrl+F` 搜索、`Esc` 清空搜索/关闭弹窗、`F5` 重扫当前专区。

---

## 目录结构

```text
ai-cleaner/
├── src/                        # .NET 10 WPF 桌面端源码
│   ├── AICleaner.csproj
│   ├── SafetyGuard.cs          # 红线保护引擎、祖先链联接校验、回收站 API、44 项自检
│   ├── ZoneScanner.cs          # 三专区并行只读扫描与 SQLite O(1) 诊断
│   ├── UserSettings.cs         # 不可变快照配置与原子落盘
│   ├── TaskSchedulerHelper.cs  # 无死锁超时可控计划任务助手
│   ├── MainWindow.xaml(.cs)    # 白蓝玻璃主界面、空间分布条、项目透视条与自动解锁
│   ├── GlassDialogs.cs         # 空间雷达、清理历史、阈值与白名单弹窗
│   └── Models.cs               # 候选模型与 INotifyPropertyChanged 联动
├── scripts/
│   └── CodexCleaner/           # 零依赖 PowerShell 清理脚本与每周定时任务工具
├── settings.example.json       # 自定义工作区/Codex 缓存目录配置示例
├── LICENSE                     # MIT
└── README.md
```

---

## 快速构建与运行

### 环境要求
- Windows 10 / 11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（或 .NET 10 Windows Desktop Runtime 运行发布产物）

### 1. 构建与单文件发布
```powershell
dotnet publish .\src\AICleaner.csproj -c Release -r win-x64 --no-self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
```

### 2. 运行内置安全自检与 CLI 只读体检
```powershell
# 运行全部内置安全与回归自检（隔离临时目录，不改动用户文件）
.\publish\AICleaner.exe --self-test .\selftest.txt

# 无界面 CLI 只读体检（支持定时巡检或终端快查）
.\publish\AICleaner.exe --dry-run .\dryrun-report.txt
```

### 3. 可选：配置自定义工作区与 Codex 缓存路径
首次启动后配置保存在 `%LOCALAPPDATA%\AICleaner\settings.json`。若你将 Codex 缓存或工作区迁移到了其他磁盘（如 `D:\`、`E:\`），可参考 [settings.example.json](./settings.example.json) 配置：
- `CustomCodexCacheRoot`：自定义 Codex 缓存根目录（包含 `temp`、`pip`、`npm`、`uv`、`browser-cache`、`desktop-logs`；程序会自动硬保护其下的 `runtimes`、`app-metadata`、`plugin-staging`、`command-staging`）
- `ExtraWorkspaceRoots`：需要纳入工作区体检与临时目录（`_tmp`/`__pycache__` 等）汇总的根目录列表
- `ExtraProtectedSubtrees`：额外需要硬保护的源码或工具目录列表
- `ExtraBackupDirs`：可选的自定义历史安装包/备份目录（仅展示于黄区回收站候选）

---

## 配套 PowerShell 工具 (`scripts/CodexCleaner`)

适合通过命令行或 Windows 计划任务每周自动维护 Codex 临时缓存，详见 [scripts/CodexCleaner/README.md](./scripts/CodexCleaner/README.md)。

```powershell
# 只读预览（Dry-Run）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\CodexCleaner\CodexCleaner.ps1 -Mode DailySafe

# 交互式管理每周安全清理定时任务（周日 03:30，仅清 >7 天未占用临时文件）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\CodexCleaner\Manage-ScheduledTask.ps1 -Action Interactive
```

---

## 许可协议

本项目采用 [MIT License](./LICENSE) 开源。
