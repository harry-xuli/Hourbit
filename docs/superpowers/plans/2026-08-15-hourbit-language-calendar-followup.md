# Hourbit Language and Calendar Follow-up Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every visible Hourbit UI surface switch consistently between Chinese and English, make WPF calendars follow the selected UI language, and correct the report date-filter sizing and displayed range.

**Architecture:** Keep one `ILocalizationService` instance as the source of truth. UI view models expose localized text and the active UI culture; WPF windows bind labels and `Language` to those properties. Tray items are rebuilt on `LanguageChanged`, while user-entered reminder and to-do content remains untouched.

**Tech Stack:** .NET 10, WPF, Windows Forms `NotifyIcon`, xUnit, existing `LocalizationService` / `LocalizationCatalog`.

## Global Constraints

- Supported UI languages are exactly `zh-CN` and `en-US`.
- Language switching changes application UI only; reminder titles, to-do titles, stored dates, and database records must not be translated or rewritten.
- Date calculations, ISO-week boundaries, reminder scheduling, and analytics query ranges must not change.
- Use one shared `ILocalizationService`; do not add independent language flags to individual windows.
- WPF `DatePicker` controls must receive the selected UI culture through `FrameworkElement.Language`.
- Do not rebuild installers or publish GitHub assets until focused tests, full Release tests, and manual Chinese/English visual checks pass.
- Preserve all current uncommitted v0.4.0 warm-focus UI work in the worktree.

---

## File Map

- `src/Hourbit.App/Localization/ILocalizationService.cs`: expose the current UI culture/language tag.
- `src/Hourbit.App/Localization/LocalizationService.cs`: derive `zh-CN` or `en-US` culture from `CurrentLanguage`.
- `src/Hourbit.App/Localization/LocalizationCatalog.cs`: add missing main-window, date-picker, analytics, tray, and confirmation strings.
- `src/Hourbit.App/Timeline/TimelineViewModel.cs`: expose localized metric-card and accessible text.
- `src/Hourbit.App/Timeline/TimelineView.xaml`: remove remaining hard-coded metric and UIA strings.
- `src/Hourbit.App/Timeline/DatePickerWindow.xaml`: bind all dialog copy and calendar language.
- `src/Hourbit.App/Timeline/DatePickerWindow.xaml.cs`: accept the shared localization service and expose a localized dialog model.
- `src/Hourbit.App/Timeline/IDatePicker.cs`: retain the existing picker contract; only the concrete WPF implementation changes.
- `src/Hourbit.App/Analytics/AnalyticsViewModel.cs`: expose UI language, synchronize preset dates, and localize report text.
- `src/Hourbit.App/Analytics/AnalyticsWindow.xaml`: bind both calendars to UI language and adjust responsive filter sizing.
- `src/Hourbit.App/Shell/TrayIconController.cs`: rebuild the context menu after `LanguageChanged` and unsubscribe on dispose.
- `src/Hourbit.App/CompositionRoot.cs`: pass the shared localization instance to date picker, analytics, and tray.
- `tests/Hourbit.App.Tests/Timeline/TimelineViewModelTests.cs`: main metrics and language-switch behavior.
- `tests/Hourbit.App.Tests/Timeline/TimelineViewTests.cs`: real WPF main-window text and UIA checks.
- `tests/Hourbit.App.Tests/Timeline/DatePickerWindowTests.cs`: localized dialog and calendar-culture checks.
- `tests/Hourbit.App.Tests/Analytics/AnalyticsViewModelTests.cs`: preset range/date synchronization and language changes.
- `tests/Hourbit.App.Tests/Analytics/AnalyticsWindowTests.cs`: date control width, layout, and calendar language.
- `tests/Hourbit.App.Tests/Shell/TrayIconControllerTests.cs`: live tray-menu language rebuilding.

---

### Task 1: Establish One UI Culture Contract

**Files:**
- Modify: `src/Hourbit.App/Localization/ILocalizationService.cs`
- Modify: `src/Hourbit.App/Localization/LocalizationService.cs`
- Modify: `src/Hourbit.App/Localization/LocalizationCatalog.cs`
- Test: `tests/Hourbit.App.Tests/Localization/LocalizationServiceTests.cs`

**Interfaces:**
- Produces: `CultureInfo CurrentCulture` and `string LanguageTag` on `ILocalizationService`.
- Consumes: existing `UiLanguage.ZhCn`, `UiLanguage.EnUs`, and `LanguageChanged`.

- [ ] **Step 1: Write the failing culture-mapping test**

```csharp
[Fact]
public void Language_switch_updates_the_shared_culture_and_language_tag()
{
    var service = new LocalizationService(
        CultureInfo.GetCultureInfo("zh-CN"), null);

    Assert.Equal("zh-CN", service.CurrentCulture.Name);
    Assert.Equal("zh-CN", service.LanguageTag);

    service.SetLanguage(UiLanguage.EnUs);

    Assert.Equal("en-US", service.CurrentCulture.Name);
    Assert.Equal("en-US", service.LanguageTag);
}
```

- [ ] **Step 2: Run the test and verify RED**

Run:

```powershell
dotnet test tests/Hourbit.App.Tests/Hourbit.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Language_switch_updates_the_shared_culture"
```

Expected: compile failure because `CurrentCulture` and `LanguageTag` do not exist.

- [ ] **Step 3: Implement the minimal shared culture mapping**

```csharp
public CultureInfo CurrentCulture => CultureInfo.GetCultureInfo(
    CurrentLanguage == UiLanguage.EnUs ? "en-US" : "zh-CN");

public string LanguageTag => CurrentCulture.Name;
```

- [ ] **Step 4: Add all required localization keys symmetrically**

Add matching Chinese and English values for:

```text
Timeline.PastSevenDaysCompleted
Timeline.NextFourteenDaysPlanned
Timeline.OpenAnalyticsAccessible
DatePicker.Title
DatePicker.Heading
DatePicker.Description
DatePicker.Cancel
DatePicker.View
Analytics.CustomDatesHint
Exit.Title
Exit.Warning
```

- [ ] **Step 5: Run localization parity tests**

Run:

```powershell
dotnet test tests/Hourbit.App.Tests/Hourbit.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Localization"
```

Expected: PASS; Chinese and English key sets remain identical.

- [ ] **Step 6: Commit Task 1**

```powershell
git add src/Hourbit.App/Localization tests/Hourbit.App.Tests/Localization
git commit -m "fix: expose shared Hourbit UI culture"
```

---

### Task 2: Localize Main Metrics and Accessibility Text

**Files:**
- Modify: `src/Hourbit.App/Timeline/TimelineViewModel.cs`
- Modify: `src/Hourbit.App/Timeline/TimelineView.xaml`
- Test: `tests/Hourbit.App.Tests/Timeline/TimelineViewModelTests.cs`
- Test: `tests/Hourbit.App.Tests/Timeline/TimelineViewTests.cs`

**Interfaces:**
- Consumes: `ILocalizationService.Translate` and `LanguageChanged`.
- Produces: `PastSevenDaysText`, `NextFourteenDaysText`, `PastSevenDaysAccessibleName`, and `NextFourteenDaysAccessibleName`.

- [ ] **Step 1: Write failing bilingual metric tests**

```csharp
await vm.SelectEnglishLanguageCommand.ExecuteAsync(null);
Assert.Equal("Completed in the last 7 days", vm.PastSevenDaysText);
Assert.Equal("Plans in the next 14 days", vm.NextFourteenDaysText);
Assert.Equal("Completed in the last 7 days: 3. Open analytics.",
    vm.PastSevenDaysAccessibleName);
```

- [ ] **Step 2: Verify RED**

Run the exact test with `dotnet test --filter`. Expected: missing properties or Chinese text remains after switching to English.

- [ ] **Step 3: Add localized properties and change notifications**

Use `LocalizationCatalog` keys for both visible labels and UIA names. Raise all four properties when the language changes and when metric counts change.

- [ ] **Step 4: Replace hard-coded XAML strings**

Replace these current literals in `TimelineView.xaml`:

```text
过去 7 天已完成
未来 14 天计划
过去 7 天已完成：{0}，打开分析
未来 14 天计划：{0}，打开分析
选择日期
```

Bind to view-model properties instead.

- [ ] **Step 5: Verify real WPF output**

Assert visible `TextBlock.Text` and `AutomationProperties.Name` in both languages without rebuilding the view model.

- [ ] **Step 6: Commit Task 2**

```powershell
git add src/Hourbit.App/Timeline/TimelineViewModel.cs src/Hourbit.App/Timeline/TimelineView.xaml tests/Hourbit.App.Tests/Timeline
git commit -m "fix: localize timeline metrics"
```

---

### Task 3: Localize the Choose-Date Dialog and Calendar

**Files:**
- Modify: `src/Hourbit.App/Timeline/DatePickerWindow.xaml`
- Modify: `src/Hourbit.App/Timeline/DatePickerWindow.xaml.cs`
- Modify: `src/Hourbit.App/CompositionRoot.cs`
- Create: `tests/Hourbit.App.Tests/Timeline/DatePickerWindowTests.cs`

**Interfaces:**
- Consumes: shared `ILocalizationService.CurrentCulture`, `LanguageTag`, and translation keys.
- Preserves: `IDatePicker.PickDateAsync(DateOnly current, CancellationToken ct)`.

- [ ] **Step 1: Write a real WPF RED test for English**

```csharp
var localization = new LocalizationService(
    CultureInfo.GetCultureInfo("zh-CN"), "en-US");
var window = new DatePickerWindow(new DateOnly(2026, 8, 15), localization);
window.Show();
window.UpdateLayout();

Assert.Equal("Choose date", window.Title);
Assert.Equal("Which day do you want to view?",
    Assert.IsType<TextBlock>(window.FindName("DatePickerHeading")).Text);
Assert.Equal("en-US",
    Assert.IsType<DatePicker>(window.FindName("DateInput")).Language.IetfLanguageTag);
```

- [ ] **Step 2: Verify RED**

Expected: the constructor does not accept localization and the XAML remains Chinese.

- [ ] **Step 3: Bind all dialog copy and WPF language**

The dialog must receive the shared service when it is opened. Set the root window and `DateInput.Language` from:

```csharp
XmlLanguage.GetLanguage(localization.LanguageTag)
```

Do not mutate `Thread.CurrentCulture`; only this UI surface changes.

- [ ] **Step 4: Verify both languages**

Test title, heading, description, Cancel/View buttons, formatted selected date, keyboard focus, and `zh-CN` / `en-US` calendar language.

- [ ] **Step 5: Commit Task 3**

```powershell
git add src/Hourbit.App/Timeline/DatePickerWindow.xaml src/Hourbit.App/Timeline/DatePickerWindow.xaml.cs src/Hourbit.App/CompositionRoot.cs tests/Hourbit.App.Tests/Timeline/DatePickerWindowTests.cs
git commit -m "fix: localize the date picker dialog"
```

---

### Task 4: Correct Report Date Sizing, Values, and Calendar Language

**Files:**
- Modify: `src/Hourbit.App/Analytics/AnalyticsViewModel.cs`
- Modify: `src/Hourbit.App/Analytics/AnalyticsWindow.xaml`
- Test: `tests/Hourbit.App.Tests/Analytics/AnalyticsViewModelTests.cs`
- Test: `tests/Hourbit.App.Tests/Analytics/AnalyticsWindowTests.cs`

**Interfaces:**
- Consumes: shared `ILocalizationService.LanguageTag`.
- Produces: `string UiLanguageTag`; preset selection synchronizes `CustomStart` and `CustomEnd` with the loaded range.

- [ ] **Step 1: Write failing preset synchronization tests**

```csharp
await vm.SelectRangeAsync(AnalyticsRangeKind.CurrentMonth);

Assert.Equal(new DateOnly(2026, 8, 1), vm.CustomStart);
Assert.Equal(new DateOnly(2026, 8, 31), vm.CustomEnd);
```

This prevents the screenshot mismatch where “This month” displays unrelated dates such as `2026/8/5` and `2026/8/4`.

- [ ] **Step 2: Write failing WPF language and size tests**

At a `1040`-pixel report width, assert:

```csharp
Assert.Equal("en-US", start.Language.IetfLanguageTag);
Assert.Equal("en-US", end.Language.IetfLanguageTag);
Assert.True(start.ActualWidth >= 190d);
Assert.True(end.ActualWidth >= 190d);
Assert.True(start.ActualHeight >= 38d);
Assert.True(end.ActualHeight >= 38d);
```

- [ ] **Step 3: Verify RED**

Expected: calendars retain the Windows language, preset dates remain stale, and current widths are below the new acceptance threshold.

- [ ] **Step 4: Implement preset/date synchronization**

When `CreateRange(kind)` succeeds, assign its `Start` and `End` to `CustomStart` and `CustomEnd` before publishing the snapshot. Keep custom dates editable only for `AnalyticsRangeKind.Custom`; for presets, display the actual loaded range and disable direct editing.

- [ ] **Step 5: Bind report calendars to UI culture and adjust layout**

Bind both `DatePicker.Language` values to the shared language tag through an `XmlLanguage` property. Allocate equal star-width columns to start and end dates, `MinWidth="190"`, `MinHeight="38"`, and keep Apply fully visible at `MinWidth="82"`.

- [ ] **Step 6: Verify Chinese and English report windows**

Test `zh-CN` and `en-US`, `Recent7Days`, `CurrentMonth`, and `Custom`. Confirm the range label and displayed date controls describe the same inclusive range.

- [ ] **Step 7: Commit Task 4**

```powershell
git add src/Hourbit.App/Analytics tests/Hourbit.App.Tests/Analytics
git commit -m "fix: align localized report date filters"
```

---

### Task 5: Rebuild the Tray Menu When Language Changes

**Files:**
- Modify: `src/Hourbit.App/Shell/TrayIconController.cs`
- Modify: `src/Hourbit.App/CompositionRoot.cs`
- Test: `tests/Hourbit.App.Tests/Shell/TrayIconControllerTests.cs`

**Interfaces:**
- Consumes: the shared `ILocalizationService` directly; do not snapshot `LocalizationHub.Translate(...)` only once in the constructor.
- Produces: `RebuildMenu()` that calls `ITrayMenuHost.SetItems(...)` with the current language.

- [ ] **Step 1: Write a failing live-update test**

```csharp
using var controller = CreateController(host, localization);
Assert.Equal("打开时间轴", host.Items[0].Text);

localization.SetLanguage(UiLanguage.EnUs);

Assert.Equal("Open timeline", host.Items[0].Text);
Assert.Equal("Common countdowns", host.Items[2].Text);
Assert.Equal("Exit", host.Items[^1].Text);
Assert.Equal(2, host.SetItemsCalls);
```

- [ ] **Step 2: Verify RED**

Expected: `SetItemsCalls` remains `1` and menu text remains Chinese.

- [ ] **Step 3: Inject localization and rebuild safely**

Subscribe in the constructor, call a private `RebuildMenu()` once initially and on `LanguageChanged`, marshal to the tray UI thread if required, then unsubscribe before disposing the host.

- [ ] **Step 4: Localize the exit confirmation**

Pass the same localization service to `MessageBoxExitConfirmationService`; replace its hard-coded Chinese title and warning with `Exit.Title` and `Exit.Warning`.

- [ ] **Step 5: Verify disposal and action preservation**

After `Dispose()`, changing language must not call `SetItems` again. Rebuilt items must still invoke Open, Quick create, countdowns, Reports, Help, Settings, and Exit exactly once.

- [ ] **Step 6: Commit Task 5**

```powershell
git add src/Hourbit.App/Shell/TrayIconController.cs src/Hourbit.App/CompositionRoot.cs tests/Hourbit.App.Tests/Shell/TrayIconControllerTests.cs
git commit -m "fix: refresh tray language dynamically"
```

---

### Task 6: Audit Remaining UI Surfaces and Verify the Release Candidate

**Files:**
- Modify as findings require: `src/Hourbit.App/**/*.xaml`
- Modify as findings require: `src/Hourbit.App/**/*.cs`
- Modify: `docs/release-checklist.md`
- Test: corresponding `tests/Hourbit.App.Tests/**/*Tests.cs`

**Interfaces:**
- Consumes: the shared localization contract from Task 1.
- Produces: a clean focused/full verification record and manual visual checklist.

- [ ] **Step 1: Scan for visible Chinese or English literals**

Run:

```powershell
rg -n 'Text="[^"]+"|Content="[^"]+"|Title="[^"]+"|AutomationProperties.Name="[^"]+"' src/Hourbit.App -g '*.xaml'
rg -n 'MessageBox.Show|new TrayMenuItem|\.Text\s*=|\.Title\s*=' src/Hourbit.App -g '*.cs'
```

Classify every result as product name, language-neutral symbol, user content, or a string that must move to `LocalizationCatalog`. Do not translate stored user content.

- [ ] **Step 2: Add a static localization coverage test**

The test must fail if the main timeline, choose-date dialog, analytics window, settings, help, or tray menu introduces a new visible hard-coded bilingual string outside the approved allowlist.

- [ ] **Step 3: Run focused tests**

```powershell
dotnet test tests/Hourbit.App.Tests/Hourbit.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Localization|FullyQualifiedName~TimelineView|FullyQualifiedName~DatePickerWindow|FullyQualifiedName~Analytics|FullyQualifiedName~TrayIconController" --maxcpucount:1
```

- [ ] **Step 4: Run the full Release suite**

```powershell
dotnet test Hourbit.slnx -c Release --no-restore --maxcpucount:1
```

Expected: zero failures and zero skipped tests.

- [ ] **Step 5: Perform manual visual verification**

In an isolated portable preview, verify both `中` and `EN` for:

1. Main navigation, search, two metrics, panels, empty states, and footer.
2. Choose-date dialog copy and calendar month/day names.
3. Analytics title, presets, year/from/to/apply, calendar month/day names, KPI cards, chart labels, empty state, and summaries.
4. Tray right-click menu, countdown submenu, Reports, Help, Settings, Exit, and exit confirmation.
5. Switching language while the analytics window is already open and while the tray icon already exists.

- [ ] **Step 6: Update the release checklist with exact evidence**

Record focused/full test totals, visual verification results, and any remaining manual Windows locale caveats. Do not mark installers or GitHub assets complete until they have been rebuilt and read back.

- [ ] **Step 7: Commit Task 6**

```powershell
git add src/Hourbit.App tests/Hourbit.App.Tests docs/release-checklist.md
git commit -m "test: verify complete Hourbit UI localization"
```

---

## Acceptance Checklist

- [ ] English mode contains no Chinese UI labels in the main metrics, choose-date dialog, report window, WPF calendars, tray menu, or exit confirmation.
- [ ] Chinese mode contains no English UI labels except the brand name `Hourbit`, standard shortcuts, and user-entered content.
- [ ] Main selected-period label and all calendars use the selected UI culture.
- [ ] Report preset and visible start/end dates always describe the same range.
- [ ] Report start/end date controls are equal width, at least 190 px wide and 38 px high at the default window size.
- [ ] Tray menu updates immediately without restarting Hourbit.
- [ ] Language switching does not query analytics again, alter reminder scheduling, or modify customer data.
- [ ] All focused and full Release tests pass before packaging.

## Self-Review

- Spec coverage: all four reported surfaces are covered—main metrics, choose-date dialog/calendar, report dates/calendar, and tray menu. A final hard-coded string audit covers adjacent windows.
- Placeholder scan: no placeholder markers remain.
- Type consistency: every task consumes the same `ILocalizationService`; `LanguageTag`, localized view-model properties, and tray `RebuildMenu()` are defined before use.
