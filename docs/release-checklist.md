# Hourbit 日程 v0.3.0 发布检查表

- 发布日期：2026-08-12
- 目标平台：Windows 11 x64，安装版与免安装版
- 数据库 schema：v5
- 版本唯一来源：仓库根目录 `Version.props`
- 当前阶段：功能完成，等待最终全量验证、构建和上传

## 发布门禁

| 检查 | 当前状态 | 最终证据 |
|---|---|---|
| 完整 Release 测试 | 待 Task 7 | 运行后填写各项目与总数 |
| schema v1/v2 至 v5 升级 | 待 Task 7 smoke | 运行后填写事件结果 |
| 提醒投递、失败重试与恢复 | 已有自动测试，待 packaged smoke | 运行后填写 |
| todo 持久化与调度隔离 | 已有自动测试，待 packaged smoke | 运行后填写 |
| 通知动作后自动刷新 | 已通过定向测试 | 普通/重要提醒完成、忽略、稍后提醒 |
| 紧凑待办、复制、倒计时、帮助 | 已通过定向测试 | 见 `docs/progress.md` |
| 安装包不含用户数据 | 待产物检查 | 运行后填写 |
| Setup/ZIP 文件名与版本一致 | 待构建 | 运行后填写 |
| SHA256 | 待构建 | 运行后填写 |
| Authenticode | 待检查 | 若为 `NotSigned` 必须公开披露 |
| GitHub Release v0.3.0 | 未创建 | 上传并读回附件后填写链接 |

## 目标附件

- `Hourbit-Setup-x64.exe`
- `Hourbit-Portable-x64.zip`
- 两个对应的 `.sha256` 文件

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

执行完毕后必须用真实测试数、事件、字节数、SHA256、签名状态和 GitHub Release 链接替换所有“待”状态，才可宣布 v0.3.0 发布完成。
