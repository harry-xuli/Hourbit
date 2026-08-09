# Period Selector Segmented Control Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the misaligned default day/week/month radio circles with accessible, color-coded segmented radio controls, then build Hourbit 0.2.1 installer and portable artifacts.

**Architecture:** Keep the existing three `RadioButton` controls, bindings, commands, group name, automation names, and keyboard semantics. Add a local WPF `ControlTemplate` that renders a vector icon plus label in an equal-size segment and switches the selected surface through template triggers; use per-control brush resources for blue/day, purple/week, and orange/month.

**Tech Stack:** .NET 10, WPF XAML, C# 14, xUnit, Inno Setup release scripts.

## Global Constraints

- Day is blue, week is purple, and month is orange.
- Icon and text share one vertical centerline; all three segments have equal height and click area.
- Selection is communicated by background, border, and radio selection state, not color alone.
- Keep `RadioButton`, `GroupName="TimelinePeriod"`, existing commands, bindings, automation names, and keyboard behavior.
- Do not add a year option.
- Do not restore Task 6 PDF or Task 7 export work.
- High contrast must use visible system brushes for foreground, selection, border, and focus.

---

### Task 1: Implement and verify the segmented period selector

**Files:**
- Modify: `tests/Moment.App.Tests/Timeline/TimelineViewTests.cs`
- Modify: `src/Moment.App/Timeline/TimelineView.xaml`

**Interfaces:**
- Consumes: `SelectDayPeriodCommand`, `SelectWeekPeriodCommand`, `SelectMonthPeriodCommand`, and `Is*PeriodSelected` bindings.
- Produces: unchanged `DayPeriodButton`, `WeekPeriodButton`, and `MonthPeriodButton` names and radio automation semantics.

- [ ] **Step 1: Add failing real-WPF tests**

Extend `Period_selectors_and_summary_cards_are_keyboard_accessible_above_the_sections` and add a focused visual-template test that shows the real view and asserts:

```csharp
Assert.All(new[] { day, week, month }, button =>
{
    Assert.Equal(42d, button.ActualHeight);
    Assert.Equal(78d, button.ActualWidth);
    Assert.IsType<RadioButtonAutomationPeer>(Peer(button));
});

Assert.Equal(day.TranslatePoint(new Point(0, day.ActualHeight / 2), view).Y,
             week.TranslatePoint(new Point(0, week.ActualHeight / 2), view).Y,
             precision: 1);
Assert.Equal(week.TranslatePoint(new Point(0, week.ActualHeight / 2), view).Y,
             month.TranslatePoint(new Point(0, month.ActualHeight / 2), view).Y,
             precision: 1);
```

Expose stable resource keys on each control and assert the day/week/month icon brushes are distinct. Inspect template descendants to assert each segment contains one `Path` and one `TextBlock`, their vertical centers differ by no more than one device-independent pixel, and label text remains 日/周/月.

Apply `HighContrastPalette.Apply(...)` and assert selected foreground/background/border use the injected system highlight brushes rather than an unreadable category brush.

- [ ] **Step 2: Run the focused WPF test and verify RED**

```powershell
dotnet test tests\Moment.App.Tests\Moment.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Period_selector|FullyQualifiedName~Period_selectors_and_summary"
```

Expected: fail because the current controls use the default radio-circle template, unequal content geometry, and no category icon brushes.

- [ ] **Step 3: Add the local segmented RadioButton template**

In `TimelineView.Resources`, add a style keyed `PeriodSegmentRadioButtonStyle` with:

```xml
<Setter Property="Width" Value="78" />
<Setter Property="Height" Value="42" />
<Setter Property="HorizontalContentAlignment" Value="Center" />
<Setter Property="VerticalContentAlignment" Value="Center" />
<Setter Property="Template">
  <Setter.Value>
    <ControlTemplate TargetType="RadioButton">
      <Border x:Name="SegmentBorder"
              Background="{DynamicResource PeriodSegmentBackgroundBrush}"
              BorderBrush="{DynamicResource PeriodSegmentBorderBrush}"
              BorderThickness="1"
              CornerRadius="6">
        <Grid HorizontalAlignment="Center" VerticalAlignment="Center">
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width="18" />
            <ColumnDefinition Width="8" />
            <ColumnDefinition Width="Auto" />
          </Grid.ColumnDefinitions>
          <Viewbox Width="17" Height="17" VerticalAlignment="Center">
            <Path Data="{TemplateBinding Tag}"
                  Stroke="{DynamicResource PeriodSegmentIconBrush}"
                  StrokeThickness="1.8" />
          </Viewbox>
          <TextBlock Grid.Column="2"
                     Text="{TemplateBinding Content}"
                     Foreground="{TemplateBinding Foreground}"
                     VerticalAlignment="Center" />
        </Grid>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property="IsChecked" Value="True">
          <Setter TargetName="SegmentBorder" Property="Background"
                  Value="{DynamicResource PeriodSegmentSelectedBackgroundBrush}" />
          <Setter TargetName="SegmentBorder" Property="BorderBrush"
                  Value="{DynamicResource PeriodSegmentIconBrush}" />
        </Trigger>
        <Trigger Property="IsKeyboardFocused" Value="True">
          <Setter TargetName="SegmentBorder" Property="BorderThickness" Value="2" />
        </Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate>
  </Setter.Value>
</Setter>
```

Place the controls in a horizontal neutral segmented surface with consistent spacing. Give each control local dynamic resources:

```xml
<RadioButton.Resources>
  <SolidColorBrush x:Key="PeriodSegmentIconBrush" Color="#0969C8" />
  <SolidColorBrush x:Key="PeriodSegmentSelectedBackgroundBrush" Color="#DCEEFF" />
</RadioButton.Resources>
```

Use equivalent purple resources for week and orange resources for month. Use three distinct calendar vector geometries through `Tag`. Wire high-contrast overrides through existing `HighContrastPalette` resource keys or system-brush template triggers without changing ViewModel bindings.

- [ ] **Step 4: Run focused and App regression tests**

```powershell
dotnet test tests\Moment.App.Tests\Moment.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TimelineViewTests"
dotnet test tests\Moment.App.Tests\Moment.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TimelineViewModelTests"
```

Expected: all selected tests pass with zero failures.

- [ ] **Step 5: Commit only selector files**

```powershell
git add -- src/Moment.App/Timeline/TimelineView.xaml tests/Moment.App.Tests/Timeline/TimelineViewTests.cs
git diff --cached --check
git commit -m "fix: align and color period selector"
```

---

### Task 2: Build, run, and package Hourbit 0.2.1

**Files:**
- Modify after fresh evidence: `docs/release-checklist.md`
- Output: `artifacts/Hourbit-Portable-x64.zip`
- Output: `artifacts/Hourbit-Setup-x64.exe`

**Interfaces:**
- Consumes: committed Hourbit 0.2.1 source and `scripts/build-release.ps1`.
- Produces: portable and installer artifacts plus SHA256 sidecars.

- [ ] **Step 1: Build and restart the Debug app**

Stop only the running Debug Hourbit whose path exactly matches this worktree, then run:

```powershell
dotnet build src\Moment.App\Moment.App.csproj -c Debug --no-restore
```

Start `Hourbit.exe` and verify the process reports `Responding = True`.

- [ ] **Step 2: Run the complete release build once**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

Expected: full Release tests, self-contained win-x64 publish, Inno Setup compile, ZIP/Setup creation, and SHA256 sidecars all succeed.

- [ ] **Step 3: Run packaged smoke once**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\smoke-test.ps1
```

Expected: all required self-test events occur once and release metadata reports 0.2.1 / 2026-08-09.

- [ ] **Step 4: Record actual artifacts and commit the release checklist**

Update `docs/release-checklist.md` with version 0.2.1, date 2026-08-09, fresh test counts, artifact byte sizes, SHA256 values, smoke results, and the unchanged unsigned-build warning. Then:

```powershell
git add -- docs/release-checklist.md
git diff --cached --check
git commit -m "docs: record Hourbit 0.2.1 release"
```
