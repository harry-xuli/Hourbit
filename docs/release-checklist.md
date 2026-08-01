# 时刻 0.1.0 发布检查表

- 检查日期：2026-08-01
- 目标：Windows 11 x64，安装版与便携版
- 签名状态：未签名测试发行版
- 总体状态：**自动发布门通过；物理 Windows 11 矩阵仍待人工验证**

最终 `build-release.ps1` 与 `smoke-test.ps1` 均在 Windows PowerShell 5.1 下退出 0。
安装包、便携 ZIP 及相邻 SHA-256 文件已生成；未执行的物理 Windows 场景仍明确
保留为待验证，不能由自动证据替代。

## 已完成的自动证据

| 检查 | 状态 | 证据 |
|---|---|---|
| Task 12 focused RED | 通过 | 首次 focused test 退出 1；CS0234 指出 `Moment.App.Diagnostics` 尚不存在 |
| Task 12 focused GREEN | 通过 | `SmokeTestRunnerTests` 3/3，通过 0 失败 |
| 直接 Release WinExe 自测 | 通过 | 进程等待后 1.3 秒退出 0；六个 JSONL 事件各一次 |
| 相对输出路径拒绝 | 通过 | focused test 验证退出 2，且未创建相对目录 |
| 隔离数据路径 | 通过 | focused test 验证数据库位于传入目录的 `data/moment-self-test.db` |
| 清理安全正向验证 | 通过 | 只接受 `artifacts/publish` 与 `artifacts/portable` |
| 清理安全反向验证 | 通过 | 以 `artifacts` 根为 probe 时退出 1、拒绝清理 |
| 完整 Release 测试门 | 通过 | Core 81/81、Infrastructure 44/44、Windows 85/85、App 107/107；合计 317/317 |
| 发布、Inno 编译、SHA-256 | 通过 | `build-release.ps1` 退出 0；Inno Setup 6.7.3 编译成功；四个发行文件存在 |
| 便携 ZIP 烟雾测试 | 通过 | `smoke-test.ps1` 退出 0；最终 ZIP 内 WinExe 退出 0，六个事件各一次 |
| Release 截图 | 待验证 | 最终发布包已生成；尚未启动实际 Release UI 捕获三张截图 |

### 已观察的 JSONL

```jsonl
{"event":"normal-delivery","timestampUtc":"2026-07-31T01:22:36.1107807+00:00"}
{"event":"important-delivery","timestampUtc":"2026-07-31T01:22:36.1373625+00:00"}
{"event":"completed","timestampUtc":"2026-07-31T01:22:36.1508568+00:00"}
{"event":"snoozed","timestampUtc":"2026-07-31T01:22:36.1611304+00:00"}
{"event":"restart-recovered","timestampUtc":"2026-07-31T01:22:36.1711506+00:00"}
{"event":"single-instance-protocol","timestampUtc":"2026-07-31T01:22:36.1892303+00:00"}
```

最终便携 ZIP 的 2026-08-01 冒烟运行再次产生相同六类事件且各一次；时间戳因每次
运行而变化。

## 已完成的自动门禁

在
`D:\Coding\window alert tool\.worktrees\moment-development` 中执行：

```powershell
dotnet test Moment.slnx -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-release.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/smoke-test.ps1
Get-FileHash artifacts\Moment-Portable-x64.zip -Algorithm SHA256
Get-FileHash artifacts\Moment-Setup-x64.exe -Algorithm SHA256
```

实际结果为 317/317 测试通过，构建和烟雾脚本退出 0，四个发行文件存在。新算哈希
与相邻 `.sha256` 内容一致：

- `Moment-Portable-x64.zip`：123,211,792 字节，
  `BEA5FFB11F7E9026C7B4E8257B37A1E504B3184E6BCFFA1E54B0DF663DC70198`
- `Moment-Setup-x64.exe`：87,147,889 字节，
  `FBB4840B9691E2706E9B70A611048B1E3A68E36E84D9AE799D7E88AE00B5F8BC`

## 手动 Windows 11 矩阵

以下项目没有被自动测试替代，也没有捏造为通过。

| 场景 | 状态 | 待验证原因 / 方法 |
|---|---|---|
| 主窗口关闭、托盘调度触发 | 待验证 | 需要实际 Release 应用驻留并等待到期 |
| 锁屏提醒 | 待验证 | 需要锁定实际 Windows 会话 |
| 睡眠超过 5 分钟后恢复 | 待验证 | 需要物理睡眠/恢复，自动门不唤醒电脑 |
| 手动日期、时间、时区变化 | 待验证 | 会改变全局系统设置；本任务未获得新许可 |
| 三个提醒同时到期 | 待验证 | 需观察 Release UI、通知合并和重要队列 |
| 专注模式下普通通知 | 待验证 | 会改变 Windows 专注设置；未获得新许可 |
| 重要提醒队列、循环声音、全部稍后值 | 待验证 | 需听觉和窗口交互验证 |
| 禁用通知权限 | 待验证 | 会改变 Windows 全局通知设置；未获得新许可 |
| 默认快捷键被占用 | 待验证 | 需另一个实际程序占用组合键 |
| 自定义声音缺失 | 待验证 | 需 Release 设置页和实际音频回退 |
| 安装/升级/卸载且数据保留 | 待验证 | 安装程序已生成；仍需实际安装、升级、卸载并核对数据目录 |
| 移动便携目录、报告启动路径过期 | 待验证 | 便携 ZIP 已生成；仍需移动目录并核对启动项诊断 |
| 100%、125%、150%、200% 缩放 | 待验证 | 会改变全局显示设置；未获得新许可 |
| 高对比度、纯键盘操作 | 待验证 | 高对比度是全局设置；未获得新许可 |
| 24 小时驻留稳定性 | 待验证 | 本轮没有经过连续 24 小时；必须记录调度器数、重复投递和内存趋势 |

## 安装生命周期数据保护检查

`installer/Moment.iss` 的文件清单只从发布目录安装到
`%LOCALAPPDATA%\Programs\Moment`。脚本不包含 `UninstallDelete` 或针对
`%LOCALAPPDATA%\Moment\data` 的删除操作；应用设置仍独占开机启动注册。
这项静态检查不能替代实际安装/升级/卸载测试。

## Release 截图待办

最终发布门通过后，从实际 Release 应用（不是设计图或 Debug 构建）捕获：

1. 主时间轴（同时显示状态文字和图标）。
2. 快速创建的明确预览。
3. 设置页的通知、快捷键、启动和备份区域。

不得为截图修改 Smart App Control、安全、通知、高对比度或显示缩放设置，除非用户
另行明确许可。
