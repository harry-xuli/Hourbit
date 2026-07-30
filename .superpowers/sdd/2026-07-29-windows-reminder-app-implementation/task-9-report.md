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
- The tray Settings command remains a shell placeholder. A rich settings surface should be
  treated as later product work, not silently inferred into this task.

## Fix round 1 — Reviewed presentation and interaction findings

**Base:** `be6a5ee53e43f4ca78e16ba29156a63e4f6edaff`

### Finding 1 — Target-framework interpretation

No architecture split was made. The controlling global constraint says:

> “Keep Moment.Core, Moment.Infrastructure, Moment.TestSupport, and their corresponding test
> projects on net10.0; target Moment.Windows and Moment.App with
> net10.0-windows10.0.22621.0.”

“Their corresponding test projects” refers to the three plain-.NET projects named immediately
before it; it does not say that the WPF App test project must be cross-platform. The fix round now
contains actual WPF construction, focus traversal, rendering, and virtualization tests. Moving
those tests to plain `net10.0` would remove the WindowsDesktop runtime they intentionally exercise,
while introducing a new presentation assembly would not test the WPF behavior under review.

Effective MSBuild property evidence:

- `dotnet msbuild tests\Moment.App.Tests\Moment.App.Tests.csproj -getProperty:TargetFramework`
  → `net10.0-windows10.0.22621.0`
- `dotnet msbuild tests\Moment.Core.Tests\Moment.Core.Tests.csproj -getProperty:TargetFramework`
  → `net10.0`
- `dotnet msbuild tests\Moment.Infrastructure.Tests\Moment.Infrastructure.Tests.csproj -getProperty:TargetFramework`
  → `net10.0`
- `dotnet msbuild tests\Moment.TestSupport\Moment.TestSupport.csproj -getProperty:TargetFramework`
  → `net10.0`

This keeps the tested WPF boundary Windows-targeted and preserves every project explicitly named
by the constraint on plain `net10.0`.

### Finding 2 — Non-recurring delete confirmation

`ITimelineDialogService` now has a distinct `ConfirmDeleteAsync` gate. A non-recurring row cannot
reach `IReminderService.DeleteAsync` until the user confirms; recurring rows retain the existing
scope chooser.

Covering test: `tests/Moment.App.Tests/Timeline/TimelineViewModelTests.cs`

- RED:
  `dotnet test tests\Moment.App.Tests\Moment.App.Tests.csproj --filter FullyQualifiedName~Non_recurring_delete --verbosity minimal`
  → FAIL 2/2. Both tests observed zero confirmation calls under the old direct-delete branch.
- GREEN: the identical command → PASS 2/2.

The cancellation test also asserts zero reminder-service calls; the confirmation test asserts
exactly one deletion.

### Finding 3 — Editable, validated reminder form

The former informational message box was replaced by `EditReminderWindow` and
`EditReminderViewModel`. The form edits:

- title;
- local date and time;
- reminder kind;
- importance;
- recurrence mode;
- weekly day selection when applicable.

It validates blank titles, exact `yyyy-MM-dd` and `HH:mm` values, invalid local DST times, and
weekly recurrence without a recognized day. Ambiguous local times follow the same earliest-UTC
policy used by the timeline query. Localized option labels are used in the actual WPF controls.

Covering tests:

- `tests/Moment.App.Tests/Timeline/EditReminderViewModelTests.cs`
- `tests/Moment.App.Tests/Timeline/TimelineViewModelTests.cs`

Evidence:

- RED:
  `dotnet test tests\Moment.App.Tests\Moment.App.Tests.csproj --filter "FullyQualifiedName~EditReminderViewModelTests|FullyQualifiedName~Edited_values_reach" --verbosity minimal`
  → expected `CS0246` because `EditReminderViewModel` did not exist.
- The first post-implementation attempt exposed only a test-fixture signature error
  (`TestData.Draft` was called with unsupported arguments); the fixture was corrected without
  changing product behavior.
- GREEN: the same focused filter → PASS 6/6.

The propagation assertion compares the complete modified `ReminderDraft` received by
`IReminderService.EditAsync`, preventing the original row from silently replacing edited values.

### Finding 4 — Real Quick Add details and keyboard traversal

Quick Add now exposes an editable validated field model rather than repeating the one-line preview.
The expanded surface contains title, date, time, kind, importance, recurrence, and weekly days.
Submitting while expanded persists those edited values.

The window-level Tab and Enter bindings were removed. Preview-key handling now:

- handles the first Tab only when the sentence input owns focus and valid details are collapsed;
- expands details and focuses the title field;
- returns false after expansion, allowing normal WPF focus traversal;
- lets ambiguity candidate buttons use standard keyboard behavior;
- handles Enter only from the sentence input, so a focused ambiguity button remains actionable.

One shared background STA dispatcher and one WPF `Application` instance host all actual-window
tests. The initial per-test host failed on the second window test with
`InvalidOperationException: 不能在同一 AppDomain 中创建多个 System.Windows.Application 实例`;
the stack traced directly to `WpfTestHost` constructing one App per test. The shared host fixes the
test infrastructure at that source.

Covering tests:

- `tests/Moment.App.Tests/QuickAdd/QuickAddViewModelTests.cs`
- `tests/Moment.App.Tests/QuickAdd/QuickAddWindowTests.cs`
- `tests/Moment.App.Tests/WpfTestHost.cs`

Evidence:

- RED:
  `dotnet test tests\Moment.App.Tests\Moment.App.Tests.csproj --filter "FullyQualifiedName~Expanded_fields|FullyQualifiedName~QuickAddWindowTests" --verbosity minimal`
  → expected compile failures for missing `QuickAddViewModel.Details` and
  `QuickAddWindow.TryExpandDetailsFromTab`.
- After the shared-host correction, one assertion showed actual WPF traversal first reaches the
  containing `ItemsControl`; the test was corrected to require the candidate button within a
  bounded normal traversal rather than assuming it was the immediately next focus element.
- GREEN: the same focused filter → PASS 3/3.
- Reduced logical viewport RED:
  `dotnet test tests\Moment.App.Tests\Moment.App.Tests.csproj -c Release --filter FullyQualifiedName~Expanded_details_remain_scrollable --verbosity minimal`
  → FAIL 1/1 because no details `ScrollViewer` existed.
- Reduced logical viewport GREEN: the same command → PASS 1/1. A 360-DIP-high viewport,
  representative of the logical space pressure at 200% scaling, retains a positive scroll range
  after expansion.

### Finding 5 — Awaited post-create timeline refresh

`QuickAddViewModel` now accepts an awaited `afterCreated` callback. Production composition wires
that callback to `TimelineViewModel.LoadAsync`; persistence completes first, refresh completes
second, and only then is `HideRequested` raised. A refresh error is copied into Quick Add's
observable error state and leaves the window open.

`CompositionRoot.ComposeQuickAdd` is the production wiring seam exercised by the focused test, so
the test cannot pass merely by recreating a similar callback in test code.

Covering test:
`tests/Moment.App.Tests/Composition/QuickAddTimelineCompositionTests.cs`

- RED:
  `dotnet test tests\Moment.App.Tests\Moment.App.Tests.csproj --filter FullyQualifiedName~QuickAddTimelineCompositionTests --verbosity minimal`
  → expected `CS0117` because `CompositionRoot.ComposeQuickAdd` did not exist.
- Two Debug GREEN attempts were blocked before discovery by Windows application control:
  `FileLoadException`, `0x800711C7`, on the newly emitted App test DLL. No assertion ran.
- Clean Release-output GREEN:
  `dotnet test tests\Moment.App.Tests\Moment.App.Tests.csproj -c Release --filter FullyQualifiedName~QuickAddTimelineCompositionTests --verbosity minimal`
  → PASS 2/2.

The success test holds the refresh query open and proves the submit task and hide event remain
pending. The failure test proves persistence occurred, both view models expose
`时间轴刷新失败`, and the window did not hide.

Self-review extended that failure test to retry after recovery. RED failed because the recording
service contained two identical drafts; GREEN passed 1/1 after Quick Add began remembering that
persistence had already succeeded and retrying only the refresh. This prevents a user from
creating a duplicate by pressing Enter after a transient refresh failure.

### Finding 6 — Fixed groups rendered by the view

`TimelineView` now binds its outer `GroupList` directly to the view model's three fixed groups.
Every group always renders its accessible header. Each group owns a bounded recycling
`VirtualizingStackPanel` row list, and selection is forwarded to the shared timeline view model.

Covering test: `tests/Moment.App.Tests/Timeline/TimelineViewTests.cs`

- After correcting a test namespace import, RED:
  `dotnet test tests\Moment.App.Tests\Moment.App.Tests.csproj -c Release --filter FullyQualifiedName~TimelineViewTests --verbosity minimal`
  → FAIL 2/2. Empty data had no `GroupList`; partial data exposed only the populated “已完成”
  group.
- GREEN: the identical command → PASS 2/2.

The actual WPF tests verify:

- empty and partial data both render `已错过 / 接下来 / 已完成` in that order;
- the partial row remains visible under “已完成” while the other headers remain;
- there are exactly three row lists;
- all three lists have virtualization enabled in recycling mode;
- partial item counts are exactly `[0, 0, 1]`.

### Actual interaction and layout evidence

Computer Use launched the final Release executable and inspected the visible WPF tree. The
accessibility hierarchy contains:

- `GroupList` / “今天提醒时间线”;
- three ordered group items with names `时间线分组：已错过`, `时间线分组：接下来`,
  and `时间线分组：已完成`;
- child lists `已错过提醒`, `接下来提醒`, and `已完成提醒`.

The actual 1267 × 794 capture is committed at:

`.superpowers/sdd/2026-07-29-windows-reminder-app-implementation/task-9-fix-round-1-main-actual.jpg`

The capture shows all three headers simultaneously in the real main window. Populated partial-row
rendering is exercised by the actual WPF view test, which constructs and lays out the real XAML at
the app's 900 × 600 minimum window size. Quick Add ambiguity reachability and subsequent focus
movement are likewise exercised on a shown real WPF window, not through a pure key-policy fake.
The reduced 360-DIP viewport test supplies deterministic high-scale layout pressure without
changing the user's global display settings.

The active desktop was not in Windows high-contrast mode, and this round did not mutate the user's
OS-wide accessibility setting. Consequently, native WPF focus/automation behavior, text-plus-color
status semantics, minimum-window layout, and reduced-logical-viewport scrolling are verified;
an OS-level high-contrast screenshot remains a manual environment-specific check.

### Fix-round final verification

Fresh final results after the last XAML change:

- `dotnet test tests\Moment.App.Tests\Moment.App.Tests.csproj -c Release --verbosity minimal`:
  PASS, 35/35
- `dotnet test tests\Moment.Core.Tests\Moment.Core.Tests.csproj -c Release --verbosity minimal`:
  PASS, 78/78
- `dotnet test tests\Moment.Infrastructure.Tests\Moment.Infrastructure.Tests.csproj -c Release --verbosity minimal`:
  PASS, 19/19
- `dotnet test tests\Moment.Windows.Tests\Moment.Windows.Tests.csproj -c Release --verbosity minimal`:
  PASS, 78/78
- `dotnet build Moment.slnx -c Release --verbosity minimal`:
  PASS, 0 warnings and 0 errors

The final Quick Add scroll-only XAML change was followed by a fresh App 35/35 run and full Release
build. Core, Infrastructure, and Windows had already passed in Release after all changes to their
consumed contracts; no Task 7/8 mechanics were modified.

## Fix round 2 — Coordinated timeline selection

**Base:** `cc740576924e1bf9a02102e67ce58649cb657df7`

### Root cause

The three fixed timeline groups are three independent WPF `ListBox` instances. Their shared
`SelectionChanged` handler forwarded a newly selected row from the view to
`TimelineViewModel.SelectedItem`, but there was no reverse projection from the view model to the
lists and no clearing of a selection retained by either of the other two lists.

This produced two related inconsistencies:

- the initial selection assigned by `LoadAsync` had no visible selected row;
- selecting a row in another group left the previous group highlighted, even though commands used
  only the newest view-model selection.

### Fix

`TimelineView` now coordinates the lists as one logical selection:

- while loaded, it observes `TimelineViewModel.SelectedItem`;
- it selects that item only in the list that contains it and clears the other group lists;
- it performs the same synchronization after a row-originated selection;
- a reentrancy guard prevents the programmatic clears from feeding back through
  `SelectionChanged`;
- data-context subscriptions are replaced on change and detached when the view unloads.

No command behavior, grouping, ordering, or timeline data contract changed.

### Deterministic WPF regression coverage

`tests/Moment.App.Tests/Timeline/TimelineViewTests.cs` now exercises the shown real WPF view for:

1. the initial view-model selection being visible in exactly one group;
2. switching from “已错过” to “接下来” clearing the previous group's visual selection;
3. a view-model-projected “接下来” selection remaining the single visible target and supplying
   the exact occurrence ID to `CompleteCommand`.

TDD evidence:

- Initial RED showed the missing initial and command-target projections:
  `dotnet test tests\Moment.App.Tests\Moment.App.Tests.csproj -c Release --filter FullyQualifiedName~TimelineViewTests --verbosity minimal`
  → FAIL 2, PASS 3.
- The cross-group fixture was then made independent of initial projection by explicitly selecting
  the first group's row before switching. Final RED with unchanged product code:
  the identical command → FAIL 3, PASS 2.
- GREEN after the coordinated-selection implementation:
  the identical command → PASS 5/5.

### Fix-round final verification

Fresh results after the final lifecycle self-review:

- focused `TimelineViewTests`: PASS, 5/5;
- `dotnet test tests\Moment.App.Tests\Moment.App.Tests.csproj -c Release --verbosity minimal`:
  PASS, 38/38;
- `dotnet test tests\Moment.Core.Tests\Moment.Core.Tests.csproj -c Release --verbosity minimal`:
  PASS, 78/78;
- `dotnet test tests\Moment.Infrastructure.Tests\Moment.Infrastructure.Tests.csproj -c Release --verbosity minimal`:
  PASS, 19/19;
- `dotnet test tests\Moment.Windows.Tests\Moment.Windows.Tests.csproj -c Release --verbosity minimal`:
  PASS, 78/78;
- `dotnet build Moment.slnx -c Release --verbosity minimal`:
  PASS, 0 warnings and 0 errors.

## Fix round 3 — Removal-only timeline deselection

**Base:** `8e48ebda13edaebc0aef14d0d024ececf71dc7d7`

### Root cause and fix

The coordinated selection handler covered events with an added row, but ignored a
removal-only `SelectionChanged` event. Clearing the selected row therefore removed its visual
highlight while leaving `TimelineViewModel.SelectedItem` and Edit/Delete/Complete enabled.

When a removal-only event contains the current logical selection, the view now clears
`SelectedItem`. The existing view-model setter disables all three commands, and the existing
round-2 projection keeps every group list visually unselected. Programmatic cross-group clearing
remains protected by the existing reentrancy guard.

### TDD and verification evidence

The added real-WPF test deselects the active row through its actual `ListBox`, then proves:

- the view model has no selected item;
- all three lists have no selected item;
- Edit, Delete, and Complete cannot execute;
- an attempted Complete records no occurrence ID.

Focused RED:

- `dotnet test tests\Moment.App.Tests\Moment.App.Tests.csproj -c Release --filter FullyQualifiedName~TimelineViewTests --verbosity minimal`
  → FAIL 1, PASS 5; `SelectedItem` retained the visually removed “已错过” row.

Focused GREEN:

- the identical command → PASS 6/6.

Broader final evidence:

- Core: PASS 78/78;
- Windows: PASS 78/78;
- Release solution build: PASS, 0 warnings and 0 errors.

Windows Application Control then blocked the freshly emitted `Moment.Infrastructure.dll` with
`FileLoadException` `0x800711C7` before the affected assertions could run. The App suite reached
37/39 with its two SQLite-query tests blocked; Infrastructure reached 6/19 with 13 tests blocked.
Separate `--no-build` retries reproduced the same policy block. This is the same environment-level
assembly-load condition already recorded in fix round 1; no Infrastructure source changed in this
round.
