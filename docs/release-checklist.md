# Hourbit 日程 v0.3.0 发布检查表

- 发布日期：2026-08-12
- 目标平台：Windows 11 x64，安装版与免安装版
- 数据库 schema：v5
- 版本唯一来源：仓库根目录 `Version.props`
- 当前阶段：本地验证和构建完成，等待 GitHub 上传

## 发布门禁

| 检查 | 当前状态 | 最终证据 |
|---|---|---|
| 完整 Release 测试 | 通过 | Core 250 + Infrastructure 132 + Windows 97 + App 242 = 721/721，0 failed，0 skipped |
| schema v1/v2 至 v5 升级 | 通过 | packaged smoke：`schema-v1-upgrade`、`schema-v2-upgrade` |
| 提醒投递、失败重试与恢复 | 通过 | packaged smoke：important/normal delivery、completed、snoozed、restart/missed recovery |
| todo 持久化与调度隔离 | 通过 | packaged smoke：`todos-created`、`todo-scheduler-exclusion` |
| 通知动作后自动刷新 | 已通过定向测试 | 普通/重要提醒完成、忽略、稍后提醒 |
| 紧凑待办、复制、倒计时、帮助 | 已通过定向测试 | 见 `docs/progress.md` |
| 安装包不含用户数据 | 通过 | ZIP 中 `.db`、`.moment-backup`、设置、fixture/testdata 命中数为 0 |
| Setup/ZIP 文件名与版本一致 | 通过 | smoke 验证 Hourbit 日程 0.3.0 / 2026-08-12 |
| SHA256 | 通过 | 见下方产物证据；两个 sidecar 均匹配 |
| Authenticode | 已检查 | Setup 为 `NotSigned`；ZIP 不适用（PowerShell 返回 `UnknownError`） |
| GitHub Release v0.3.0 | 通过 | https://github.com/harry-xuli/Hourbit/releases/tag/v0.3.0；四个附件均读回为 `uploaded` |

## 目标附件

- `Hourbit-Setup-x64.exe`
- `Hourbit-Portable-x64.zip`
- 两个对应的 `.sha256` 文件

## 本地产物证据

| 文件 | 字节数 | SHA256 |
|---|---:|---|
| `Hourbit-Setup-x64.exe` | 89,129,789 | `2BE98447571A747C66756D97B393CAE0923B2930F1F918183BCAA3F2F5C5B5F6` |
| `Hourbit-Portable-x64.zip` | 125,214,556 | `FE274AB5E58338923D783268CD83FE9A43EE2A0FB456D3833E602205DC5939A3` |

`build-release.ps1` 已成功完成 .NET win-x64 自包含发布和 Inno Setup 6.7.3 编译；`smoke-test.ps1` 的 12 个事件均通过。

## 安全与数据要求

- 不关闭或绕过 Smart App Control、Microsoft Defender 或其他 Windows 安全功能。
- 安装包和 ZIP 不得包含数据库、设置、备份、测试夹具或历史客户记录。
- 升级必须继续保留原 Inno AppId、兼容安装路径和 `%LocalAppData%\Moment\data\moment.db`。
- 安装升级人工测试只能使用隔离测试账户或虚拟机，不能使用客户正式数据。

## 最终命令

```powershell
dotnet test Moment.slnx --configuration Release --no-restore --maxcpucount:1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\smoke-test.ps1
Get-AuthenticodeSignature .\artifacts\Hourbit-Setup-x64.exe
```

GitHub Release 已创建并读回四个附件，v0.3.0 发布完成。
