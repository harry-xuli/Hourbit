# Hourbit 日程 v0.6.1 交付说明

发布日期：2026-08-21

版本来源：`Version.props`

数据库：schema v6，保留现有提醒、待办、操作历史和用户设置。

## 主要变化

- 使用 A 方案新 Logo，增大任务栏和托盘中的实际可见面积。
- 已错过提醒可再次完成或延迟；完成后的倒计时停止。
- 待办改为单层卡片、右键操作并支持持久化拖动排序。
- 补齐时间线、快速创建、设置页、日期选择和报告日期控件的中英文/无障碍文本。
- GitHub tag 与 `Version.props` 自动校验，避免错误发布到旧版本号。

## 验证

- Release tests：786/786。
- Inno Setup 6.7.3：成功。
- packaged smoke：通过，元数据为 Hourbit 日程 0.6.1 / 2026-08-21。

## 产物

| 文件 | 大小 | SHA256 |
|---|---:|---|
| `Hourbit-Setup-x64.exe` | 88,543,086 bytes | `DCDD19EA5B938CE826E074954ED4C8494C97F81413BAEA568DE8252584B6D142` |
| `Hourbit-Portable-x64.zip` | 125,463,605 bytes | `B93BCDBD9041DB683380BA85D1E759926AD70AC0354707A71CCFBE4B973968E2` |

安装包与程序当前均为 `NotSigned`。未绕过 Windows Security；正式公开发布前应使用受信任的 Authenticode 代码签名证书签名。
