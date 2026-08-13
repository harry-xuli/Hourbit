# Hourbit User Guide

Hourbit is a local-first reminder and to-do app for Windows 11. It does not require an account. Reminders, settings, and backups stay on this computer.

## Install or use the portable package

- Setup: verify `Hourbit-Setup-x64.exe.sha256`, then run `Hourbit-Setup-x64.exe`. New installs use `%LOCALAPPDATA%\Programs\Hourbit`.
- Portable: verify and fully extract `Hourbit-Portable-x64.zip`, then run `Hourbit.exe`. Portable data stays in its `Data` folder.
- Upgrades preserve existing local data. The legacy `%LOCALAPPDATA%\Moment\data` path remains intentionally compatible with older Hourbit releases.

Never disable Windows security features to install Hourbit. An unsigned build may show a SmartScreen warning; verify the GitHub release source and SHA-256 first.

## Timeline and future dates

- Choose Day, Week, or Month, then use the arrow buttons to move into the past or future.
- Weeks always run Monday through Sunday.
- Choose the date heading to jump to a specific date.
- Completed to-dos remain in statistics and reports but no longer occupy the pending list.
- Choose Reports to open charts and history.

## Create reminders and to-dos

Choose New or press `Ctrl+N`. Text with a date and time creates a reminder. Text without a time creates a to-do; a date-only to-do never rings. The preview shows what Hourbit will create before you press Enter.

Examples:

```text
Call Alex 2026-10-03 17:00
Renew license 2026-10-03
Organize bookshelf
20 minutes countdown
```

Chinese date and time expressions remain supported when the UI is English. Switching UI language never translates or changes user-entered content.

## Repeat, copy, search, and countdowns

- Reminders support daily, weekdays (Monday to Friday), and selected weekly days.
- Select an item and press `Ctrl+D` to create an editable copy. The original stays unchanged.
- Press `Ctrl+F` to search reminders and to-dos, then activate a result to jump to its date.
- Countdown reminders show live remaining time and change to due when they reach zero.

## Notifications and tray

Normal reminders use Windows Notification Center; Focus mode may delay them. Important reminders show an always-on-top Hourbit window and loop sound until handled. Completing, ignoring, or snoozing refreshes the main timeline.

Closing the main window hides Hourbit. Double-click the tray icon to restore the main window. Use the tray Exit command to stop Hourbit completely.

## Shortcuts

- `Ctrl+N`: New; `Ctrl+F`: Search; `F5`: Refresh
- `Enter`: Edit; `Delete`: Delete; `Ctrl+D`: Copy
- `Ctrl+Shift+Space`: Complete; `Esc`: Hide quick create

## Backups and upgrades

Settings can create, export, and restore `.moment-backup` files. The extension and old data identity remain for upgrade compatibility. Exit Hourbit before moving a portable folder, and keep the `portable.flag` and `Data` folder when replacing program files.

The `中 / EN` control changes UI labels only. User reminder and to-do content remains exactly as entered.
