# New Reminder Alignment and App Logo Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Align the “新建提醒” vector plus with its label and ship the approved “时刻之环” brand mark as the Windows application and installer icon.

**Architecture:** Generate one high-resolution GPT-Image-2 master mark, remove its flat chroma-key background, and deterministically derive a multi-size Windows ICO. Keep the button change local to `TimelineView.xaml`; verify alignment through rendered WPF geometry instead of source-text assertions, then validate the Release application through Computer Use.

**Tech Stack:** .NET 10, WPF XAML, xUnit, GPT-Image-2, Python Pillow, Inno Setup 6.7.3, Windows Computer Use.

## Global Constraints

- Preserve `NewReminderButton`, `OpenQuickAddCommand`, automation name `新建提醒`, minimum width `164`, and minimum height `52`.
- Use a fixed `16 × 16` vector plus with approximately `2 px` stroke and `10 px` spacing before the label.
- Remove the full-width plus glyph and its negative top margin.
- Continue using the system highlight-text brush in normal and High Contrast modes.
- The logo contains no text, letters, numbers, alarm-clock legs, watermark, device mockup, or background scene.
- The logo uses a rounded blue square, a white clock ring and hands, and one orange-red reminder dot in the upper-right quadrant.
- Preserve clear recognition at 16, 24, 32, 48, 64, and 256 pixels.
- Do not change Windows security, notification, privacy, display, accessibility, time, or time-zone settings during UI verification.

---

### Task 1: Generate and Integrate the “时刻之环” Logo

**Files:**
- Create: `src/Moment.App/Assets/moment-logo-master.png`
- Create: `src/Moment.App/Assets/moment.ico`
- Create: `scripts/build-app-icon.py`
- Modify: `src/Moment.App/Moment.App.csproj`
- Modify: `installer/Moment.iss`

**Interfaces:**
- Consumes: approved visual specification in `docs/superpowers/specs/2026-08-01-new-reminder-logo-alignment-design.md`
- Produces: `scripts/build-app-icon.py <rgba-png> <ico-path>` and a multi-size `moment.ico` consumed by MSBuild and Inno Setup

- [ ] **Step 1: Generate the logo master with GPT-Image-2**

Use the image-generation tool with this exact production prompt:

```text
Use case: logo-brand
Asset type: Windows 11 reminder app icon master
Primary request: Create “时刻之环”, a single centered app mark made from a white simplified clock ring with two clean clock hands and one separate orange-red reminder dot in the upper-right quadrant.
Scene/backdrop: perfectly flat solid #00ff00 chroma-key background outside the icon, with no shadows, gradient, texture, floor, reflection, or lighting variation in the background.
Style/medium: clean flat vector-friendly logo, polished Windows 11 visual language.
Composition/framing: centered rounded-square blue tile, generous uniform safe area, strong silhouette, readable at 16 pixels.
Color palette: restrained deep-blue to bright-blue tile gradient, white clock symbol, one orange-red reminder dot; do not use #00ff00 in the icon.
Text: none.
Constraints: one icon only; crisp edges; symmetric optical balance; reminder dot clearly separated from the clock ring; no cast shadow outside the tile.
Avoid: letters, Chinese characters, numbers, alarm-clock legs, bells, check marks, extra dots, watermark, device mockup, background scene, photorealism, glass, fine detail.
```

Inspect the result at full size and as a 16-pixel thumbnail. Regenerate once with a single targeted correction if it violates the constraints. Copy the selected source into `src/Moment.App/Assets/moment-logo-master.png`.

- [ ] **Step 2: Remove the chroma key and verify alpha**

Run the installed image-generation helper against the selected master:

```powershell
python "$env:CODEX_HOME\skills\.system\imagegen\scripts\remove_chroma_key.py" `
  --input src\Moment.App\Assets\moment-logo-master.png `
  --out src\Moment.App\Assets\moment-logo-rgba.png `
  --auto-key border --soft-matte --transparent-threshold 12 `
  --opaque-threshold 220 --despill
```

Verify with Pillow that the output mode is `RGBA`, all four corners have alpha `0`, and the non-transparent bounding box leaves at least 8% padding on every side.

- [ ] **Step 3: Add the deterministic ICO builder**

Create `scripts/build-app-icon.py` with this implementation:

```python
from pathlib import Path
import sys
from PIL import Image

SIZES = [(16, 16), (20, 20), (24, 24), (32, 32),
         (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]

if len(sys.argv) != 3:
    raise SystemExit("usage: build-app-icon.py <rgba-png> <ico-path>")

source = Path(sys.argv[1]).resolve()
destination = Path(sys.argv[2]).resolve()
with Image.open(source) as image:
    rgba = image.convert("RGBA")
    if rgba.width != rgba.height or rgba.width < 1024:
        raise SystemExit("source logo must be square and at least 1024 px")
    destination.parent.mkdir(parents=True, exist_ok=True)
    rgba.save(destination, format="ICO", sizes=SIZES)
```

- [ ] **Step 4: Build and validate the ICO**

Run:

```powershell
python scripts\build-app-icon.py `
  src\Moment.App\Assets\moment-logo-rgba.png `
  src\Moment.App\Assets\moment.ico
```

Open the ICO with Pillow, enumerate its frames, and require the planned sizes. Visually inspect the 16-, 32-, and 256-pixel renderings for a recognizable ring, hands, and reminder dot.

- [ ] **Step 5: Integrate the icon into application and installer builds**

Add to the main property group of `src/Moment.App/Moment.App.csproj`:

```xml
<ApplicationIcon>Assets\moment.ico</ApplicationIcon>
```

Add to `[Setup]` in `installer/Moment.iss`:

```ini
SetupIconFile=..\src\Moment.App\Assets\moment.ico
UninstallDisplayIcon={app}\Moment.App.exe
```

- [ ] **Step 6: Verify and commit Task 1**

Run:

```powershell
dotnet build src\Moment.App\Moment.App.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-release.ps1
```

Require exit code `0`, then inspect the built EXE and setup EXE icons at small and large Windows icon sizes.

```powershell
git add src\Moment.App\Assets\moment-logo-master.png `
  src\Moment.App\Assets\moment-logo-rgba.png `
  src\Moment.App\Assets\moment.ico scripts\build-app-icon.py `
  src\Moment.App\Moment.App.csproj installer\Moment.iss
git commit -m "feat: add Moment application logo"
```

### Task 2: Align the New Reminder Vector Icon

**Files:**
- Modify: `src/Moment.App/Timeline/TimelineView.xaml:57-69`
- Modify: `tests/Moment.App.Tests/Timeline/TimelineViewTests.cs`

**Interfaces:**
- Consumes: existing `NewReminderButton` and `OpenQuickAddCommand`
- Produces: a rendered `16 × 16` vector icon whose vertical center differs from the label center by no more than `0.5` device-independent pixels

- [ ] **Step 1: Write the failing rendered-alignment test**

Add this behavior to `TimelineViewTests` using its existing `Create`, `Show`, and WPF descendant helpers:

```csharp
[Fact]
public Task New_reminder_vector_icon_and_label_are_vertically_centered() =>
    WpfTestHost.RunAsync(() =>
    {
        var viewModel = Create(TwoGroupQuery());
        viewModel.LoadAsync().GetAwaiter().GetResult();
        var view = Show(viewModel);
        var button = Assert.IsType<Button>(view.FindName("NewReminderButton"));
        var content = Assert.IsType<StackPanel>(button.Content);
        var icon = Assert.IsType<Viewbox>(content.Children[0]);
        var label = Assert.IsType<TextBlock>(content.Children[1]);

        Assert.Equal(16d, icon.ActualWidth);
        Assert.Equal(16d, icon.ActualHeight);
        Assert.Equal("新建提醒", label.Text);
        Assert.Equal(10d, icon.Margin.Right);

        var iconCenter = icon.TranslatePoint(
            new Point(icon.ActualWidth / 2d, icon.ActualHeight / 2d), button).Y;
        var labelCenter = label.TranslatePoint(
            new Point(label.ActualWidth / 2d, label.ActualHeight / 2d), button).Y;
        Assert.InRange(Math.Abs(iconCenter - labelCenter), 0d, 0.5d);
    });
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests\Moment.App.Tests\Moment.App.Tests.csproj -c Release `
  --filter "FullyQualifiedName~New_reminder_vector_icon_and_label_are_vertically_centered"
```

Expected: fail because the first child is the current full-width plus `TextBlock`, not a fixed-size `Viewbox`.

- [ ] **Step 3: Replace the text glyph with the vector plus**

Replace the button content with:

```xml
<StackPanel Orientation="Horizontal" VerticalAlignment="Center">
    <Viewbox Width="16" Height="16" Margin="0,0,10,0"
             VerticalAlignment="Center" Stretch="Uniform">
        <Path Data="M 8,1 L 8,15 M 1,8 L 15,8"
              Stroke="{DynamicResource {x:Static SystemColors.HighlightTextBrushKey}}"
              StrokeThickness="2"
              StrokeStartLineCap="Round"
              StrokeEndLineCap="Round" />
    </Viewbox>
    <TextBlock Text="新建提醒"
               Foreground="{DynamicResource {x:Static SystemColors.HighlightTextBrushKey}}"
               FontSize="17"
               VerticalAlignment="Center" />
</StackPanel>
```

- [ ] **Step 4: Run focused and regression tests**

Run:

```powershell
dotnet test tests\Moment.App.Tests\Moment.App.Tests.csproj -c Release `
  --filter "FullyQualifiedName~TimelineViewTests"
dotnet test Moment.slnx -c Release
```

Require zero failures and confirm the existing automation name, command, keyboard navigation, and High Contrast tests remain green.

- [ ] **Step 5: Commit Task 2**

```powershell
git add src\Moment.App\Timeline\TimelineView.xaml `
  tests\Moment.App.Tests\Timeline\TimelineViewTests.cs
git commit -m "fix: align new reminder icon and label"
```

### Task 3: Release UI Verification

**Files:**
- Create: `.superpowers/sdd/2026-08-01-new-reminder-logo-alignment/ui-main.png`
- Create: `.superpowers/sdd/2026-08-01-new-reminder-logo-alignment/ui-quick-add.png`
- Create: `.superpowers/sdd/2026-08-01-new-reminder-logo-alignment/ui-settings.png`
- Modify: `.superpowers/sdd/2026-08-01-new-reminder-logo-alignment/report.md`

**Interfaces:**
- Consumes: `artifacts/portable/Moment.App.exe` produced by the release build
- Produces: real Release evidence for button alignment, application identity, Quick Add, and Settings; files remain review evidence and are not shipped assets

- [ ] **Step 1: Run final automated verification**

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-release.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\smoke-test.ps1
git diff --check
```

Require all tests, packaging, hashes, and six packaged self-test events to pass.

- [ ] **Step 2: Launch the isolated Release portable application**

Use Computer Use to launch the existing executable path
`artifacts/portable/Moment.App.exe`. Select exactly one returned window titled
`时刻`, activate it, and refresh window state before every input action. Do not
reuse coordinates or element indexes after state changes.

- [ ] **Step 3: Verify and capture the main timeline**

Inspect the real rendered button and require the plus and label to share one
visual center line with even spacing. Confirm the window/taskbar identity uses
the new logo. Save an app-window-only screenshot as `ui-main.png`.

- [ ] **Step 4: Verify Quick Add and Settings without protected setting changes**

Open Quick Add through the app, verify the preview and new logo-bearing app
identity, and save `ui-quick-add.png`. Close it, open Settings, inspect the page
without toggling notification, startup, security, accessibility, display, time,
or time-zone settings, and save `ui-settings.png`.

- [ ] **Step 5: Close the app and record evidence**

Close the Release application through its own UI. Record the exact build/smoke
commands, test counts, artifact paths, UI observations, and screenshot paths in
the report. If Computer Use reports a user stop, stop immediately and record the
remaining UI checks as blocked.

- [ ] **Step 6: Request code review**

Generate a review package covering both implementation commits. Require spec
compliance and code-quality approval before branch completion.
