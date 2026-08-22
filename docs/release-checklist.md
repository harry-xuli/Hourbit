# Hourbit v0.7.0 发布检查表

- 发布日期：2026-08-22
- 目标平台：Windows 11 x64，安装版与免安装版
- 数据库 schema：v6
- 版本唯一来源：仓库根目录 `Version.props`
- 目标签名：个人证书 `CN=Harry`，指纹 `9CE426CC31B420A308F33BD233587D9A7071FED8`

## 发布门禁

| 检查 | 当前状态 | 最终证据 |
|---|---|---|
| 完整 Release 测试 | 通过 | App 288 + Core 259 + Infrastructure 143 + Windows 97 = 787/787 |
| schema v1/v2 至 v6 升级 | 通过 | packaged smoke 的 schema-v1-upgrade / schema-v2-upgrade |
| 提醒投递、失败重试与恢复 | 通过 | packaged smoke 12/12 事件 |
| 搜索、日期、待办排序和本地化 | 已有回归测试 | 完整 Release suite |
| 安装包不含用户数据 | 通过 | build-release publish/ZIP 敏感数据扫描为 0 |
| Setup/ZIP 文件名与版本一致 | 通过 | Hourbit / 0.7.0 / 2026-08-22 |
| SHA-256 sidecar | 通过 | 与两个实际文件逐字节核对一致 |
| Authenticode | 通过 | Setup 与 Portable 内 Hourbit.exe 均为 `CN=Harry`，指纹一致并含 DigiCert 时间戳 |
| GitHub Release v0.7.0 | 通过 | [公开发布页](https://github.com/harry-xuli/Hourbit/releases/tag/v0.7.0)；四个附件名、大小和 digest 已读回核对 |

## 安全与数据要求

- 不关闭或绕过 Smart App Control、Microsoft Defender 或其他 Windows 安全功能。
- 安装包和 ZIP 不得包含数据库、设置、备份、测试夹具或历史客户记录。
- 升级必须继续保留原 Inno AppId、现有数据路径、旧启动项迁移键和单实例身份。
- 个人自签名证书不等同于商业 CA 信任；发布说明必须如实披露。

## 目标附件

- `Hourbit-Setup-x64.exe`
- `Hourbit-Setup-x64.exe.sha256`
- `Hourbit-Portable-x64.zip`
- `Hourbit-Portable-x64.zip.sha256`
