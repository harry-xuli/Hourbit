# Hourbit Split Timeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore the colored day/week/month icons and present reminders on the left and todos on the right while retaining one synchronized timeline snapshot and the existing keyboard behavior.

**Architecture:** Keep `TimelineViewModel`, `TimelineSnapshot`, query services, and command routing unchanged. Make the change entirely in `TimelineView.xaml`: supply typed `PathGeometry` values to the existing segmented-button template, then replace the vertical content stack with a `3*`/`2*` nested grid. Add static XAML contract tests because the managed runner has a documented pre-existing `WpfTestHost` Dispatcher timeout; verify the compiled application by launching the real Debug executable.

**Tech Stack:** .NET 10, C#, WPF XAML, xUnit, PowerShell, Git.

## Global Constraints

- Platform remains Windows 11 WPF.
- Timeline filters remain Day, Week, and Month only; no Year selector.
- Both columns consume the existing single `TimelineSnapshot`; do not add queries or ViewModel state.
- Left column is `3*` and contains reminders; right column is `2*` and contains todos.
- Completed todos remain collapsed by default.
- Preserve existing row-focus command routing, virtualized lists, automation names, and high-contrast resources.
- Do not modify `WpfTestHost`, PDF Task 6, Task 7, notification scheduling, or persistence.

---

### Task 1: Render typed period icons

**Files:**
- Modify: `tests/Moment.App.Tests/Timeline/TimelinePeriodSelectorXamlTests.cs`
- Modify: `src/Moment.App/Timeline/TimelineView.xaml`

**Interfaces:**
- Consumes: the existing `PeriodSegmentRadioButtonStyle` with `Path Data="{TemplateBinding Tag}"`.
- Produces: three `RadioButton.Tag` values whose runtime type is `PathGeometry`.

- [ ] **Step 1: Write the failing XAML contract test**

Extend `Period_selector_uses_equal_segmented_radio_buttons_and_category_colors` with:

```csharp
Assert.Equal(3, CountOccurrences(timelineXaml, "<PathGeometry Figures=\""));
Assert.DoesNotContain("Tag=\"M", timelineXaml);
```

The production change that makes this pass is replacing all three string-valued `Tag` attributes with typed `PathGeometry` property elements.

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests\Moment.App.Tests\Moment.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TimelinePeriodSelectorXamlTests"
```

Expected: FAIL because the current XAML contains zero `<PathGeometry Figures="...">` elements and three `Tag="M..."` strings.

- [ ] **Step 3: Replace the three string tags with typed geometries**

For each period button, remove the `Tag="M..."` attribute and add a property element before the command property. Example:

```xml
<RadioButton.Tag>
    <PathGeometry Figures="M3,4 L17,4 17,18 3,18 Z M3,8 L17,8 M7,2 L7,6 M13,2 L13,6 M7,12 L10,12 10,15 7,15 Z" />
</RadioButton.Tag>
```

Use the existing Day, Week, and Month path data unchanged. Do not alter the template, color resources, automation names, or button dimensions.

- [ ] **Step 4: Run the focused test and verify GREEN**

Run the Step 2 command again. Expected: 1 passed, 0 failed.

- [ ] **Step 5: Commit the icon fix**

```powershell
git add src/Moment.App/Timeline/TimelineView.xaml tests/Moment.App.Tests/Timeline/TimelinePeriodSelectorXamlTests.cs
git diff --cached --check
git commit -m "fix: render period selector icons"
```

---

### Task 2: Split reminders and todos into synchronized columns

**Files:**
- Modify: `tests/Moment.App.Tests/Timeline/TimelinePeriodSelectorXamlTests.cs`
- Modify: `src/Moment.App/Timeline/TimelineView.xaml`

**Interfaces:**
- Consumes: existing bindings `Groups`, `PendingTodos`, `CompletedTodos`, and existing `OnTimelineSelectionChanged` handlers.
- Produces: named `TimelineColumns`, `ReminderColumn`, and `TodoColumn` visual containers without changing ViewModel or service interfaces.

- [ ] **Step 1: Write the failing split-layout contract test**

Add a second test:

```csharp
[Fact]
public void Timeline_places_reminders_left_and_todos_right_in_three_to_two_columns()
{
    var xaml = ReadRepositoryFile("src", "Moment.App", "Timeline", "TimelineView.xaml");

    Assert.Contains("x:Name=\"TimelineColumns\"", xaml);
    Assert.Contains("<ColumnDefinition Width=\"3*\"", xaml);
    Assert.Contains("<ColumnDefinition Width=\"2*\"", xaml);
    Assert.Contains("x:Name=\"ReminderColumn\" Grid.Column=\"0\"", xaml);
    Assert.Contains("x:Name=\"TodoColumn\" Grid.Column=\"2\"", xaml);
    Assert.Contains("x:Name=\"ReminderSectionHeader\"", xaml);
    Assert.Contains("x:Name=\"TodoSectionHeader\"", xaml);
    Assert.Contains("x:Name=\"CompletedTodosExpander\"", xaml);
    Assert.Contains("IsExpanded=\"False\"", xaml);
}
```

The production change that makes this pass is the nested two-column grid, not a duplicate view or a second query.

- [ ] **Step 2: Run the focused test and verify RED**

Run the same focused command from Task 1. Expected: the new test fails because the named split-grid containers and `3*`/`2*` columns do not exist.

- [ ] **Step 3: Reshape the main content grid**

In `TimelineView.xaml`:

1. Keep the period navigation in outer row 0 and the two summary cards in outer row 1.
2. Change the remaining outer rows to one star-sized row.
3. Add this shell in outer row 2:

```xml
<Grid x:Name="TimelineColumns" Grid.Row="2" Margin="26,8,26,20">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="3*" MinWidth="620" />
        <ColumnDefinition Width="16" />
        <ColumnDefinition Width="2*" MinWidth="360" />
    </Grid.ColumnDefinitions>
    <Grid x:Name="ReminderColumn" Grid.Column="0">
        <!-- existing reminder heading, table header, error overlay, and grouped reminder list -->
    </Grid>
    <Border Grid.Column="1"
            Width="1"
            HorizontalAlignment="Center"
            Background="{DynamicResource BorderBrush}" />
    <Grid x:Name="TodoColumn" Grid.Column="2">
        <!-- existing todo heading, pending list, and completed expander -->
    </Grid>
</Grid>
```

Use `Grid.Column="2"` for `TodoColumn` because column 1 is the separator. Preserve all existing named controls, bindings, handlers, automation properties, virtualization flags, and item templates. Remove only the old outer-row placement attributes and margins that conflict with the nested columns.

Inside `ReminderColumn`, use rows `Auto`, `44`, and `*` for the heading, table header, and reminder content. Inside `TodoColumn`, use rows `Auto`, `*`, and `Auto` for the heading, pending list, and completed expander. Set the pending and completed todo lists to stretch within their column and retain vertical scrolling.

- [ ] **Step 4: Verify the XAML contract and compile**

Run:

```powershell
dotnet test tests\Moment.App.Tests\Moment.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TimelinePeriodSelectorXamlTests"
dotnet build src\Moment.App\Moment.App.csproj -c Debug --no-restore
```

Expected: all focused static tests pass; build exits 0 with no XAML compiler errors.

- [ ] **Step 5: Check unchanged behavioral contracts**

Run the non-WPF ViewModel tests:

```powershell
dotnet test tests\Moment.App.Tests\Moment.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TimelineViewModelTests"
```

Expected: all tests pass. Do not run or modify the known-hanging `WpfTestHost` suite as part of this change.

- [ ] **Step 6: Launch and visually verify the real application**

Stop only the running Debug `Hourbit.exe` whose executable path is inside this worktree, then launch:

```powershell
src\Moment.App\bin\Debug\net10.0-windows10.0.22621.0\Hourbit.exe
```

Verify in the real window:

- Day, Week, and Month show their colored icons and aligned labels.
- “定时提醒” is on the left and “待办事项” is on the right.
- Summary cards remain above both columns.
- Completed todos are collapsed initially.
- Selecting and editing rows still targets the focused column.

- [ ] **Step 7: Commit the split layout**

```powershell
git add src/Moment.App/Timeline/TimelineView.xaml tests/Moment.App.Tests/Timeline/TimelinePeriodSelectorXamlTests.cs
git diff --cached --check
git commit -m "feat: split reminders and todos into columns"
```

---

### Task 3: Prepare the release handoff

**Files:**
- No production source changes expected.

**Interfaces:**
- Consumes: committed Task 1 and Task 2 UI changes and existing release scripts.
- Produces: a clean worktree ready for installer and portable packaging in the next release step.

- [ ] **Step 1: Verify repository state**

```powershell
git status --short
git log -3 --oneline
```

Expected: no uncommitted files; the icon and split-layout commits are at HEAD after the design and plan commits.

- [ ] **Step 2: Report the visual verification gate**

Ask the user to confirm the running application layout. Only after confirmation proceed to the already-requested installer and portable build; keep PDF Task 6 and Task 7 paused.
