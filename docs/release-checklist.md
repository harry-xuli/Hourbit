# Hourbit v0.4.0 发布检查表

- 发布日期：2026-08-13
- 目标平台：Windows 11 x64，安装版与免安装版
- 数据库 schema：v5
- 版本唯一来源：仓库根目录 `Version.props`
- 当前阶段：功能完成，等待最终构建、smoke、签名状态和 GitHub 发布

## 发布门禁

| 检查 | 当前状态 | 最终证据 |
|---|---|---|
| 完整 Release 测试 | 待最终运行 | 重命名后 740/740；UI 本地化 App 261/261 |
| schema v1/v2 至 v5 升级 | 待 packaged smoke | 不得读取或改写正式用户数据 |
| 提醒投递、失败重试与恢复 | 待 packaged smoke | important/normal、完成、稍后、重启、错过 |
| 搜索、未来日期、完成项隐藏 | 已通过定向测试 | SQLite 搜索、日/周/月、真实 WPF |
| 中英文 UI 与操作手册 | 已通过定向测试 | UI 语言偏好持久化；用户内容不翻译 |
| 旧版本数据与身份兼容 | 已通过定向测试 | 数据路径、备份、启动项、单实例 46/46 |
| 安装包不含用户数据 | 待构建扫描 | `.db`、`.moment-backup`、设置与测试夹具必须为 0 |
| Setup/ZIP 文件名与版本一致 | 待构建 | Hourbit / 0.4.0 / 2026-08-13 |
| SHA256 | 待构建 | 两个 sidecar 必须匹配 |
| Authenticode | 待检查 | 未签名则 Release Notes 明确披露，不绕过安全功能 |
| GitHub Release v0.4.0 | 未发布 | 需用户最终确认后执行并读回四个附件 |

## 目标附件

- `Hourbit-Setup-x64.exe`
- `Hourbit-Portable-x64.zip`
- 两个对应的 `.sha256` 文件

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
