# Hourbit 日程 0.2.0 发布检查表

- 发布日期：2026-08-01
- 本次验证日期：2026-08-08
- 目标平台：Windows 11 x64，安装版与便携版
- 签名状态：`Hourbit.exe` 与 `Hourbit-Setup-x64.exe` 均为 `NotSigned`
- 版本唯一来源：仓库根目录 `Version.props`

未签名测试构建可能被 Smart App Control、Microsoft Defender 或其他 Windows 安全
策略拦截。不得关闭或绕过这些安全功能来完成发布检查；正式对外分发前应配置可信
代码签名。

## 本次自动验证证据

| 检查 | 结果 | 证据 |
|---|---|---|
| 完整 Release 测试 | 通过 | Core 202、Infrastructure 99、Windows 88、App 183，共 572/572，0 失败 |
| schema v1 升级 | 通过 | 最终便携包自检用隔离 v1 数据库升级到 v3，并由真实提醒仓储重新读取原提醒 |
| schema v2 升级 | 通过 | 最终便携包自检用隔离 v2 数据库升级到 v3，并保留原提醒 |
| todo 持久化 | 通过 | 在升级后的 v2 数据库创建有日期和无日期 todo，重开仓储后精确读取两项 |
| todo 调度隔离 | 通过 | 有已有提醒作为对照时，`GetScheduledAsync` 与 `GetDueAsync` 都只返回该提醒 |
| todo 备份恢复 | 通过 | 导出后从当前库删除 todo，再恢复备份，完整 `TodoItem` 回归 |
| 手动备份默认名 | 通过 | `hourbit-export-20260808T111213Z.moment-backup` 精确测试通过 |
| 版本与身份一致 | 通过 | 最终 EXE 自检元数据、设置页脚、Inno 安装器版本资源、ZIP/Setup 文件名均与 MSBuild 求值一致 |
| 旧安装兼容静态门 | 通过 | AppId 保持 `{8E5D37F4-A701-4B84-A71E-B7C0A8E46D51}`；只清理旧 EXE/快捷方式，不删除数据 |
| 旧启动项兼容门 | 通过 | 只迁移精确旧路径与 `--background`；`StartupApproved` 缺失或规范启用才迁移，读取错误失败关闭 |
| 发布构建 | 通过 | `build-release.ps1` 退出 0；Inno Setup 6.7.3 编译完成 |
| 最终便携包 smoke | 通过 | 12 个 JSONL 事件各一次；进程退出 0；CPU 时间 1031.25 ms |

最终 smoke 事件：

```text
schema-v1-upgrade
schema-v2-upgrade
todos-created
todo-scheduler-exclusion
normal-delivery
important-delivery
completed
snoozed
restart-recovered
missed-recovery
single-instance-protocol
release-metadata
```

所有数据库升级与便携包自检都在任务专用目录或系统临时目录执行；没有读取、安装、
替换或删除用户正式数据，也没有写入正式开机启动注册表项。

## 发行文件

| 文件 | 字节数 | SHA-256 |
|---|---:|---|
| `Hourbit-Portable-x64.zip` | 125114063 | `81f6919fb3cc35228c55bef5401653cad532a8a80389ec0568ae7247652a4ca3` |
| `Hourbit-Setup-x64.exe` | 89050178 | `6f13f8f4feaba3ab63a153e2ba2917b8c837437ae865284b89568a5639e23723` |

相邻 `.sha256` 文件已由发布脚本生成，内容与重新计算的哈希一致。

## 发布命令

在仓库根目录运行：

```powershell
dotnet test Moment.slnx --configuration Release --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\smoke-test.ps1
```

## 仍需物理 Windows 11 人工验收

下列项目不能由隔离自动测试冒充为人工通过，本轮也没有改变用户的全局区域、安全、
通知或安装状态：

| 场景 | 状态 | 自动证据与人工方法 |
|---|---|---|
| `zh-CN`、`en-US`、`en-GB` 区域格式 | 待人工验证 | parser culture matrix 已自动通过；人工需逐一切换 Windows 短日期顺序、启动 Release 并按预览确认，最后恢复原区域 |
| todo 不产生 Windows 通知 | 待人工观察 | 仓储与 packaged smoke 已证明 todo 不进入 scheduler；人工需跨过截止日观察通知中心 |
| 旧安装原地升级且数据保留 | 待人工安装 | AppId、目录、清理和启动项 seam 已通过；必须在可回滚测试账户/虚拟机以旧版数据实际安装验证 |
| 主窗口、托盘、设置页 Hourbit 身份 | 待人工观察 | 程序集和 WPF 自动化测试已通过；需从最终 Release 产物截图确认 |
| 锁屏、睡眠、专注模式、通知权限 | 待人工验证 | 会改变系统状态，需在专用测试机执行并恢复设置 |

安装升级人工测试不得使用用户正式数据。先复制旧版测试数据库到隔离测试账户或虚拟
机，核对提醒、设置、AppId 与启动项后再卸载测试实例。

## 未来版本规则

未来发布只修改 `Version.props` 中的产品名、可执行文件名、语义版本或发布日期。
不要在脚本、安装器、设置页或文档中维护第二份可执行版本号。修改后必须运行本检查
表的三个发布命令，并确认：

1. `Hourbit.exe` 的程序集元数据；
2. 设置页脚 `版本 <version> · 发布于 <yyyy-MM-dd>`；
3. 安装器 `ProductVersion`；
4. 便携包与安装器的 Hourbit 发行文件名；
5. 新生成的 SHA-256。
