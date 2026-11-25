# CodexCleaner (PowerShell 零依赖安全维护套件)

本目录提供与 **AI 清理助手** 配套的零依赖 PowerShell 脚本，默认全部为只读预览（Dry-Run），支持 Windows 计划任务每周自动清理超期未占用临时缓存。

## 使用方法

1. （可选）复制 `config.example.json` 为 `config.json`，填入你本机的自定义缓存或工作区路径（`config.json` 已加入 `.gitignore`，不会被提交）。
2. 双击 `.cmd` 入口或通过 PowerShell 运行：
   - `1-日常安全清理.cmd`：清理超期且未被占用的临时与包缓存（ChatGPT 桌面端运行中可用）
   - `2-工作区瘦身体检.cmd`：扫描配置工作区中的超期大目录并生成 Markdown 体检报告，勾选后移入回收站
   - `3-离线清浏览器缓存.cmd`：确认 ChatGPT / codex 进程全部退出后，清理浏览器缓存内部文件（完整保留目录骨架与联接）
   - `4-管理每周自动清理.cmd`：一键安装 / 预览 / 卸载每周日 03:30 的 `CodexSafeTempCleanup` 计划任务

## 安全保障
- 自动硬保护 `%USERPROFILE%\.codex` 全目录及其全部 NTFS 联接目标、`OpenAI.Codex` MSIX 包目录与全部用户配置红区。
- 绝不强杀 ChatGPT / codex 进程；绝不穿越 NTFS 联接点；大目录全部走 Windows 回收站可随时撤销。
