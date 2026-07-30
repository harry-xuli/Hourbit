# Task 9 — WPF Shell, Timeline, and Quick Add

## Scope delivered

- Added the unpackaged `Moment.App` WPF `WinExe` targeting
  `net10.0-windows10.0.22621.0`, with WPF and Windows Forms enabled and
  `WindowsPackageType=None`.
- Added the open, keyboard-operable timeline shell with date rail, five data columns, fixed
  “已错过 / 接下来 / 已完成” presentation order, text-and-symbol status, recurrence and importance
  text, selection, virtualized rows, and commands for edit, delete, complete, and Quick Add.
- Added `ITimelineQuery` and a SQLite implementation that converts an inclusive local-date start
  and exclusive next-local-date boundary to UTC. The query preserves the stored occurrence
  offset and handles invalid and ambiguous local instants deterministically.
- Added recurring edit/delete scope gates. No reminder service call is made until a scope is
  selected; cancelling the scope dialog is a no-op.
- Added Quick Add success, ambiguity, and invalid states; ambiguity selection converts to a
  preview before creation. The window shows an absolute Chinese preview, supports
  Enter/Tab/Escape, restores focus and selection, is placed on the current monitor, and remains
  out of the taskbar.
- Added a Quick Add window controller that recreates a WPF window after it has been closed while
  preserving the view-model input. This prevents WPF's `Show()`-after-`Close()` lifecycle error.
- Added the exact tray commands:
  `打开今天时间轴`, `快速创建`, `常用倒计时`, `设置`, and `退出`.
  Closing the main window hides it to the tray; exit is gated by confirmation when scheduled
  occurrences remain.
- Added one composition graph containing one repository, scheduler, notification sink, action
  service, parser, and associated view models. Task 7 notification/action hooks and Task 8
  single-instance, hotkey, and resume hooks are connected at the application boundary.
- Added an explicit STA bootstrap that repairs a missing process `windir` from the machine
  environment before WPF initializes.

## TDD evidence

The initial timeline/Quick Add test project was written against missing contracts and UI
presentation types before product implementation. Subsequent focused RED/GREEN cycles covered:

- Timeline ordering and status text, cancellation, observable errors, async-command reentrancy,
  fixed groups, and recurring-scope cancellation.
- Inclusive/exclusive UTC query boundaries in UTC+08 and a 23-hour daylight-saving local day.
- Quick Add success, ambiguous, invalid, ambiguity choice, absolute preview, reentrancy, Escape,
  and Tab behavior.
- Tray labels, callbacks, and guarded exit.
- Startup environment repair.
- Quick Add closed-window recreation.
- Quick Add window-open failure. RED failed with the expected uncaught
  `InvalidOperationException("窗口不可用")`; GREEN passed after the command routed the failure into
  the view model's observable error state.

Two integration defects were found through actual WPF execution and then reproduced in focused
tests before their fixes:

1. **Startup crash with exit code `-532462766`.** WPF threw a `XamlParseException` while loading
   the resource dictionary because the process-level `windir` variable was absent. The WPF font
   cache constructs the Fonts URI from that process variable. `ApplicationBootstrap` now restores
   the process variable from the machine environment before `App.InitializeComponent()`.
2. **Quick Add could not reopen after Close.** WPF windows cannot be shown after being closed.
   `QuickAddWindowController` now creates a fresh window when the previous one reports closed and
   marshals opening to the UI dispatcher.

## Design fidelity ledger

| Baseline requirement | Implementation evidence |
| --- | --- |
| True-white workspace and near-black primary text | Central color tokens use a white window surface and dark text; blue is reserved for focus and primary action. |
| Open timeline, not a card-heavy dashboard | The main surface is a single date rail plus full-width five-column timeline with quiet separators. |
| Prominent but restrained creation action | The top-right “＋ 新建提醒” action is a centered 52 px button; no marketing copy or decorative hero area was added. |
| Status must not rely on color alone | Every row combines a symbol, Chinese status text, and status color. Fired occurrences read “等待处理”. |
| Strong visible keyboard focus | Shared controls use a two-pixel blue focus treatment; primary actions have accessible automation names. |
| Dense lists must remain usable | Timeline rows use WPF virtualization and keyboard selection/commands; no list animation is introduced. |
| Quick Add should feel like a command bar | The 680 px current-monitor window is compact, taskbar-free, auto-focused, and keeps the sentence input as its visual center. |
| Ambiguity must be explicit | Candidate choices appear in an in-window panel; creation remains blocked until a choice produces a preview. |
| Motion restraint | No custom animation or animated transition was added. |
| Concept language | Chinese labels, date formatting, recurrence/status vocabulary, spacing, and open-column hierarchy follow the supplied concepts and design-system document. |

The supplied visual baselines are committed at:

- `docs/superpowers/specs/assets/moment-main-timeline-concept.png`
- `docs/superpowers/specs/assets/moment-quick-add-concept.png`

The actual rendered evidence is:

- `.superpowers/sdd/2026-07-29-windows-reminder-app-implementation/task-9-main-window-actual.png`
  — 1267 × 794, captured from the visible WPF main window.
- `.superpowers/sdd/2026-07-29-windows-reminder-app-implementation/task-9-quick-add-actual.png`
  — 667 × 257 client capture, showing `明早10点开会`, selected input, and the absolute
  `2026年7月31日 10:00 · 单次 · 普通提醒` preview.

Both images were captured from the actual desktop session through Computer Use using the Windows
graphics capture path, not generated mockups. Accessibility inspection also exposed the expected
date, columns, shortcuts, and “新建提醒” automation name. After the final toolbar alignment change,
the Release executable was launched again: PID 1600 became responsive with title `时刻` and
non-zero main-window handle `6949102`.

## Verification

- `dotnet test tests\Moment.App.Tests\Moment.App.Tests.csproj`:
  PASS, 19/19
- `dotnet test tests\Moment.Core.Tests\Moment.Core.Tests.csproj`:
  PASS, 78/78
- `dotnet test tests\Moment.Infrastructure.Tests\Moment.Infrastructure.Tests.csproj`:
  PASS, 19/19
- `dotnet test tests\Moment.Windows.Tests\Moment.Windows.Tests.csproj`:
  PASS, 78/78
- `dotnet build Moment.slnx -c Release --verbosity minimal`:
  PASS, 0 warnings and 0 errors
- Actual Release WPF smoke launch:
  PASS, responsive titled top-level window with a non-zero handle
- Actual Quick Add lifecycle:
  PASS, open, close, `Ctrl+N`, and second open with input/focus preserved

## Intentional limitations and follow-up concerns

- Native Windows title-bar chrome is retained rather than drawing custom chrome; this preserves
  standard window movement, resizing, accessibility, and platform behavior.
- The captured main timeline used an empty local database, so the screenshot proves the real shell,
  date rail, columns, commands, and visual hierarchy rather than populated-row rendering. Row
  ordering, grouping, status, recurrence, importance, and interaction behavior are covered by
  deterministic view-model tests.
- The captured Quick Add image records the success/preview state rather than the ambiguity panel;
  the ambiguity gate and choice transition are covered by deterministic tests.
- Row symbols use native text glyphs instead of custom raster artwork.
- The Task 9 edit dialog is deliberately minimal: it confirms the current draft and scope rather
  than providing a complete field editor. The tray Settings command is also a shell placeholder.
  Rich edit/settings surfaces should be treated as later product work, not silently inferred into
  this task.
