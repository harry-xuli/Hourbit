# Hourbit v0.4.0 发布检查表

- 发布日期：2026-08-13
- 目标平台：Windows 11 x64，安装版与免安装版
- 数据库 schema：v5
- 版本唯一来源：仓库根目录 `Version.props`
- 当前阶段：本地构建与 smoke 完成，等待 GitHub 发布

## 发布门禁

| 检查 | 当前状态 | 最终证据 |
|---|---|---|
| 完整 Release 测试 | 通过 | Core 250 + Infrastructure 134 + Windows 97 + App 261 = 742/742 |
| schema v1/v2 至 v5 升级 | 通过 | packaged smoke：`schema-v1-upgrade`、`schema-v2-upgrade` |
| 提醒投递、失败重试与恢复 | 通过 | packaged smoke：important/normal、完成、稍后、重启、错过 |
| 搜索、未来日期、完成项隐藏 | 已通过定向测试 | SQLite 搜索、日/周/月、真实 WPF |
| 中英文 UI 与操作手册 | 已通过定向测试 | UI 语言偏好持久化；用户内容不翻译 |
| 旧版本数据与身份兼容 | 已通过定向测试 | 数据路径、备份、启动项、单实例 46/46 |
| 安装包不含用户数据 | 通过 | ZIP 与 publish 敏感数据扫描均为 0 |
| Setup/ZIP 文件名与版本一致 | 通过 | Hourbit / 0.4.0 / 2026-08-13；12 个 smoke 事件通过 |
| SHA256 | 通过 | 两个 sidecar 与实际文件一致，见下方 |
| Authenticode | 已检查 | Setup 与 Hourbit.exe 均为 `NotSigned`；Release Notes 必须披露 |
| GitHub Release v0.4.0 | 未发布 | 需用户最终确认后执行并读回四个附件 |

## 目标附件

- `Hourbit-Setup-x64.exe`
- `Hourbit-Portable-x64.zip`
- 两个对应的 `.sha256` 文件

## 本地产物证据

| 文件 | 字节数 | SHA256 |
|---|---:|---|
| `Hourbit-Setup-x64.exe` | 89,114,836 | `1C7317072A48109E762EBAF62B2701EB92688D38037016E3E4464930DEAF53FE` |
| `Hourbit-Portable-x64.zip` | 125,233,226 | `E298095286D394477FC601344931F3C4C8CEE9CC4856F6D84D23D4D63A852FEE` |

`build-release.ps1` 使用串行测试门禁后成功完成，自包含 win-x64 publish 与 Inno Setup 6.7.3 编译均为 0 警告、0 错误。`smoke-test.ps1` 的 12 个事件全部通过。

## 安全与数据要求

- 不关闭或绕过 Smart App Control、Microsoft Defender 或其他 Windows 安全功能。
- 安装包和 ZIP 不得包含数据库、设置、备份、测试夹具或历史客户记录。
- 升级必须继续保留原 Inno AppId、`%LocalAppData%\Moment\data\moment.db`、旧启动项迁移键和单实例身份。
- 新安装默认目录是 `%LocalAppData%\Programs\Hourbit`；已有安装使用 `UsePreviousAppDir=yes` 原地升级。
- 安装升级人工测试只能使用隔离测试账户或虚拟机，不能使用正式客户数据。

## 最终命令

```powershell
dotnet test Hourbit.slnx --configuration Release --no-restore --maxcpucount:1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\smoke-test.ps1
Get-AuthenticodeSignature .\artifacts\Hourbit-Setup-x64.exe
```

产物大小、SHA256、smoke 事件和 GitHub Release 地址必须在实际成功后再填写，不预先宣称通过。
