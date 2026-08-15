# Hourbit 开发进度

更新日期：2026-08-15
目标版本：v0.6.0（合并为单一版本发布）
开发分支：`codex/hourbit-v0.6.0`

## 当前状态

v0.3.0 已发布。v0.4.0 功能与语言/日历 follow-up 收尾已完成并推送；项目已决定合并后续所有工作为单一 **v0.6.0** 版本一次性发布，不再分多个版本。签名、CI 已接入；重复待办（schema v6）、分析报告 PDF/配对导出/端到端、数据重置 UI 尚待实现。

## 已完成

### 数据重置（已提交并推送）

- `DataResetRequest` 请求文件（原子写入、JSON、30 分钟有效期）；
- `DataResetApplier`：校验路径/备份/时效，隔离旧数据库与 sidecar，重建空库，失败回滚；
- `DataResetCoordinator`：备份导出 → 写入请求 → 请求重启；
- 启动时在打开任何仓库前应用待处理的重置请求（`CompositionRoot.OpenAsync`）；
- 设置页新增「重置本地数据」分区：警告 + 备份导出 + 确认短语「重置 Hourbit」+ 确认后启用按钮；
- 覆盖测试：过期/畸形请求、错误路径、缺失备份、隔离重建、导出失败安全、确认短语门控。

### 重复待办（schema v6，已提交并推送）

- 自然语言 `每天 / 每个工作日 / 每周N` 前缀创建重复待办（无时间、不入提醒调度器）；
- 完成重复待办后自动生成下一次待办（每日 +1 天、工作日跳过周末、每周按所选星期）；
- todos 表新增 `recurrence_kind` / `recurrence_days_of_week` 列（schema v5→v6 迁移 + 备份校验）；
- 编辑/复制重复待办保留其重复规则；README 已同步。

### v0.4.0 功能与语言/日历 follow-up（已提交并推送）

- 暖色主题、`中 / EN` UI 切换、ISO 周（周一至周日）、未来日期导航、全局搜索、仅待办面板、托盘双击恢复、统一快捷键、公开项目 Moment→Hourbit 重命名（保留旧数据目录/AppId/启动项/单实例兼容）；
- 语言/日历 follow-up 全部完成：
  - `ILocalizationService` 暴露共享 `CurrentCulture` / `LanguageTag`；
  - 主时间线指标、空状态、工具提示、页脚快捷键本地化；
  - 选择日期对话框本地化并绑定日历语言；分析报告日期筛选尺寸与预设日期同步、日历绑定 UI 语言；
  - 托盘菜单随语言切换即时重建、退出确认本地化；
  - 重要提醒弹窗与帮助标题本地化；
  - 新增硬编码字符串覆盖测试与对话框/日历文化测试。

### 工程与发布基础

- `scripts/build-release.ps1` 接入 Authenticode 签名（证书库自签名 `CN=Harry`，时间戳服务；`-SkipSign` 用于无证书环境），SHA256 在签名后重算；
- 新增 `.github/workflows/ci.yml`（PR/推送跑 Release 测试；tag `v*` 打包并上传产物）；
- `main` 已快进到 v0.4.0 基线，`codex/hourbit-v0.6.0` 分支已创建并推送 GitHub。

## 待完成（合并进 v0.6.0）

1. 分析报告：PDF 导出、配对导出 UI（完整/匿名隐私模式）、报告端到端（Task 8）；
2. 数据生命周期 smoke（Task 5：`reset-backup-restorable` 等事件）；
3. CompositionRoot 重构、产物瘦身、高 DPI 目检清单；
4. 版本 0.6.0 签名发布：全量测试 → build-release（含签名）→ packaged smoke → tag `v0.6.0` → GitHub Release。

### 已知环境阻塞

- Inno Setup 6.7.3 安装包（约 10 MB，GitHub Release 资产）在本沙箱下载超时，暂无法构建 `Hourbit-Setup-x64.exe`；
  免安装 ZIP 不受影响。发布前需在可访问 GitHub 资产的环境重试下载，或用便携解压方式安装 Inno Setup。

## 验证基线

- 语言/日历 follow-up 后全解决方案 Release 测试通过（0 failed / 0 skipped）。

## 长期延期

- 云同步、账户系统和在线帮助（本地优先的产品定位，暂不实现）。

## GitHub 与发布

- `codex/hourbit-v0.6.0` 已推送：https://github.com/harry-xuli/Hourbit；
- v0.3.0 Release 仍为最新正式版本；v0.6.0 将在全部功能完成后签名发布。
