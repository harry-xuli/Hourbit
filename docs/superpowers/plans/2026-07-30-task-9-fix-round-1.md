# Task 9 Fix Round 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve the six scoped Task 9 review findings without changing approved Task 7/8 mechanics or adding a gratuitous cross-platform presentation project.

**Architecture:** Keep the WPF application and its UI test assembly Windows-targeted. Add narrow dialog and composition seams around the existing view models, use actual WPF STA tests for focus/rendering contracts, and keep persistence in the existing reminder services. The timeline view renders the three existing group view models directly so empty groups remain visible.

**Tech Stack:** .NET 10, WPF, xUnit, SQLite, existing Moment Core/Infrastructure/Windows services.

## Global Constraints

- “Keep Moment.Core, Moment.Infrastructure, Moment.TestSupport, and their corresponding test projects on net10.0; target Moment.Windows and Moment.App with net10.0-windows10.0.22621.0.”
- Keep `Moment.App` and its WPF UI tests on `net10.0-windows10.0.22621.0`.
- Do not alter approved Task 7 notification or Task 8 hotkey/single-instance/resume mechanics.
- Use one RED then minimal GREEN cycle for every behavior change.
- Append exact evidence to the existing Task 9 report and commit the fix round separately from `be6a5ee`.

---

### Task 1: Resolve the target-framework review interpretation

**Files:**
- Inspect: `tests/Moment.App.Tests/Moment.App.Tests.csproj`
- Modify: `.superpowers/sdd/2026-07-29-windows-reminder-app-implementation/task-9-report.md`

**Interfaces:**
- Consumes: the exact global constraint above.
- Produces: explicit evidence that only Core/Infrastructure/TestSupport and their corresponding tests are required to remain plain `net10.0`; the WPF App tests remain Windows-targeted.

- [ ] **Step 1: Query effective target frameworks**

Run `dotnet msbuild` property queries for App.Tests and Core/Infrastructure/TestSupport test projects.

- [ ] **Step 2: Record the evidence**

Record the exact values and explain why a new cross-platform presentation project would not satisfy the required WPF focus/render tests.

### Task 2: Confirm non-recurring deletion

**Files:**
- Modify: `tests/Moment.App.Tests/Timeline/TimelineViewModelTests.cs`
- Modify: `src/Moment.App/Timeline/TimelineViewModel.cs`
- Modify: `src/Moment.App/Timeline/TimelineDialogService.cs`

**Interfaces:**
- Add: `Task<bool> ConfirmDeleteAsync(TimelineItemViewModel item, CancellationToken ct)`.
- Preserve: recurring delete returns `SeriesScope?` from `SelectDeleteScopeAsync`.

- [ ] **Step 1: Write a failing cancellation test**

Create a non-recurring selected row, make confirmation return false, execute `DeleteCommand`, and assert no service deletion.

- [ ] **Step 2: Verify RED**

Run the focused test; expect the current immediate delete call to produce one deletion.

- [ ] **Step 3: Add the confirmation gate**

Use `ConfirmDeleteAsync` only for non-recurring rows and return before the reminder service when false.

- [ ] **Step 4: Verify GREEN**

Run cancellation and confirmed-delete tests; expect both to pass.

### Task 3: Provide a real validated edit form

**Files:**
- Create: `src/Moment.App/Timeline/EditReminderViewModel.cs`
- Create: `src/Moment.App/Timeline/EditReminderWindow.xaml`
- Create: `src/Moment.App/Timeline/EditReminderWindow.xaml.cs`
- Modify: `src/Moment.App/Timeline/TimelineDialogService.cs`
- Modify: `src/Moment.App/CompositionRoot.cs`
- Modify: `tests/Moment.App.Tests/Timeline/TimelineViewModelTests.cs`
- Create: `tests/Moment.App.Tests/Timeline/EditReminderViewModelTests.cs`

**Interfaces:**
- `EditReminderViewModel.TryBuildDraft(out ReminderDraft? draft)` validates title, `yyyy-MM-dd`, `HH:mm`, kind, importance, recurrence, and weekly day selection.
- `TimelineDialogService.EditAsync` opens `EditReminderWindow` and returns the edited draft only when validation succeeds and the user confirms.

- [ ] **Step 1: Write failing edit-model and propagation tests**

Assert modified title/date/time/kind/importance/daily recurrence produce literal expected values; invalid title/time/weekly days produce an observable validation error. Assert the exact modified draft reaches `IReminderService.EditAsync`.

- [ ] **Step 2: Verify RED**

Run the focused edit tests; expect missing edit model contracts and the recording service's missing draft capture.

- [ ] **Step 3: Implement the minimal edit model and WPF form**

Use localized options for all enums, resolve the entered local date/time with the injected time zone, and expose validation beside the form.

- [ ] **Step 4: Verify GREEN**

Run all edit-model and timeline edit propagation tests.

### Task 4: Make Quick Add details and Tab traversal real

**Files:**
- Modify: `src/Moment.App/QuickAdd/QuickAddViewModel.cs`
- Modify: `src/Moment.App/QuickAdd/QuickAddWindow.xaml`
- Modify: `src/Moment.App/QuickAdd/QuickAddWindow.xaml.cs`
- Modify: `tests/Moment.App.Tests/QuickAdd/QuickAddViewModelTests.cs`
- Create: `tests/Moment.App.Tests/QuickAdd/QuickAddWindowTests.cs`
- Create: `tests/Moment.App.Tests/WpfTestHost.cs`

**Interfaces:**
- Expose localized detail values for title, absolute due time, kind, importance, and recurrence.
- First Tab from the sentence input expands details and focuses the first detail control; once expanded, Tab is not handled and WPF performs normal focus traversal.

- [ ] **Step 1: Write failing detail and focus tests**

Assert literal detail values after success/choice. In an STA WPF test, assert first Tab policy expands and focuses details, a subsequent focus traversal moves to the next field, and an ambiguity candidate is reachable from the input.

- [ ] **Step 2: Verify RED**

Run the focused Quick Add tests; expect missing detail properties and current window-level Tab binding to prevent traversal.

- [ ] **Step 3: Implement detail fields and one-time Tab handling**

Remove the window-level Tab `KeyBinding`; handle preview Tab only while details are collapsed and the input owns focus. Render labeled, read-only detail controls with meaningful automation names.

- [ ] **Step 4: Verify GREEN**

Run the focused view-model and WPF focus tests.

### Task 5: Await timeline refresh after Quick Add

**Files:**
- Modify: `src/Moment.App/QuickAdd/QuickAddViewModel.cs`
- Modify: `src/Moment.App/CompositionRoot.cs`
- Create: `tests/Moment.App.Tests/Composition/QuickAddTimelineCompositionTests.cs`

**Interfaces:**
- Add: `Func<CancellationToken, Task> afterCreated` to `QuickAddViewModel`.
- Add a production composition helper that wires `afterCreated` to `TimelineViewModel.LoadAsync`.

- [ ] **Step 1: Write a failing composed refresh test**

Use a query whose rows change after service persistence, submit through the composed Quick Add view model, and assert the timeline item/header has refreshed before `HideRequested`.

- [ ] **Step 2: Verify RED**

Run the focused composition test; expect the timeline to retain its old item/header.

- [ ] **Step 3: Wire and await refresh**

Await persistence, then the refresh callback, and hide only after both succeed. Route callback failures to Quick Add's observable error state and leave the window visible.

- [ ] **Step 4: Verify GREEN**

Run refresh and refresh-failure tests.

### Task 6: Render all fixed groups

**Files:**
- Modify: `src/Moment.App/Timeline/TimelineView.xaml`
- Create: `tests/Moment.App.Tests/Timeline/TimelineViewTests.cs`

**Interfaces:**
- Bind the outer timeline to `TimelineViewModel.Groups` in its fixed order.
- Each group always renders its header and owns a virtualized recycling `ListBox` for its rows.

- [ ] **Step 1: Write failing WPF render tests**

Render the actual view in an STA host for empty and partial data; assert all three literal headers are visible in order and only the populated group contains a row.

- [ ] **Step 2: Verify RED**

Run the focused view tests; expect empty group headers to be absent.

- [ ] **Step 3: Bind to fixed groups**

Replace grouped `Items` rendering with direct `Groups` rendering while retaining row selection and recycling virtualization.

- [ ] **Step 4: Verify GREEN**

Run empty/partial render tests and the timeline view-model suite.

### Task 7: Actual interaction, verification, report, and commit

**Files:**
- Modify: `.superpowers/sdd/2026-07-29-windows-reminder-app-implementation/task-9-report.md`

**Interfaces:**
- Produce exact test counts, Release build result, actual populated timeline/focus evidence, and remaining safe limitations.

- [ ] **Step 1: Exercise the actual WPF application**

Verify populated fixed groups, ambiguity keyboard access, minimum window size, and normal/high-contrast-aware resources where safely observable without changing the user's OS settings.

- [ ] **Step 2: Run complete verification**

Run all amended focused tests, full App/Core/Infrastructure/Windows tests, and `dotnet build Moment.slnx -c Release --verbosity minimal`.

- [ ] **Step 3: Append the fix-round report**

Record each RED/GREEN command and exact result, covering test files, actual interaction evidence, TFM resolution, and any remaining concerns.

- [ ] **Step 4: Self-review and commit**

Check the scoped diff, `git diff --check`, staged files, and worktree status; commit once with a fix-scoped message.
