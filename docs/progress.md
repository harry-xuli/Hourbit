# Hourbit 开发进度

更新日期：2026-08-22

当前开发版本：v0.7.1

开发分支：`codex/hourbit-v0.6-followup`

## 当前状态

v0.7.1 将应用图标切换为已确认的 B 方案（日历、勾选与时钟），并针对任务栏、托盘和标题栏的小尺寸显示重新生成多分辨率 ICO。功能、数据库 schema 与用户数据格式保持不变。

## v0.7.1 功能范围

- B 方案 Logo 覆盖主图、RGBA、ICO、窗口、任务栏、托盘、Setup 与 Portable。
- ICO 包含 16、20、24、32、40、48、64、128 与 256 像素层。
- 仅做品牌资源与补丁版本更新，不迁移或重置用户数据。

## v0.7.0 已发布状态

v0.7.0 汇总已确认的 A 方案 Logo、提醒可靠性、待办交互、本地化与日期控件修复，并整合 GitHub v0.6.3 的 Windows App SDK 启动修复。完整 Release 测试、个人签名、packaged smoke 与 GitHub Release 均已完成。

## v0.7.0 功能范围

- A 方案 Logo 覆盖主图、RGBA、ICO、窗口、任务栏、托盘、Setup 与 Portable。
- 已错过提醒可再次完成、忽略或延迟；完成或忽略后的倒计时停止。
- 待办采用单层卡片、右键操作和可持久化拖动排序。
- 时间线、状态、重要性、重复、日期选择、报告、设置、快速创建和托盘支持中英文切换。
- 日期控件使用线程文化驱动原生 Calendar，保留 16px 字体和报告可读尺寸。
- Windows App SDK 改为显式启动，避免测试发现和应用启动死锁。

## 发布门禁

- 完整 Release 测试：787/787（App 288、Core 259、Infrastructure 143、Windows 97），0 failed / 0 skipped。
- 本机 `CN=Harry` 个人 Authenticode 签名：Setup 与 Portable 内 Hourbit.exe 均已验证；DigiCert 时间戳有效至 2036-09-04。
- packaged smoke：12 个事件通过；版本 0.7.0 与发布日期 2026-08-22 一致。
- GitHub Release：[`v0.7.0`](https://github.com/harry-xuli/Hourbit/releases/tag/v0.7.0) 已公开；Setup、Portable ZIP 和两个 SHA-256 sidecar 的名称、大小与 digest 已读回核对。

## v0.7.0 产物

- `Hourbit-Setup-x64.exe`：88,549,832 bytes；SHA-256 `B505AC24994FDA3C71C3A818972BC070A09985E71EF3E8984E589E1292B909FF`。
- `Hourbit-Portable-x64.zip`：125,470,091 bytes；SHA-256 `6659F01DCCF1D62A985EE50D7AF1960FD07FF0871A2756F10DEDD0C9D5845B6C`。

## 长期延期

- 商业 CA 代码签名证书；当前个人自签名证书在未安装证书的电脑上可能仍显示不受信任。
- 云同步、账户系统和在线帮助；继续保持本地优先。
