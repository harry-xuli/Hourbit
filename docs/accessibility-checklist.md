# Accessibility verification checklist

Verified on 2026-07-30 for the Windows 11 x64 WPF build.

## Evidence boundaries

- The production app was launched normally and inspected with Windows UI Automation and Windows Graphics Capture. The timeline rendered in normal mode with a readable 42-node accessibility tree; the Quick Add window rendered with a visible focus indicator.
- Computer Use could not target the notification-area icon or the `ShowInTaskbar="False"` Quick Add HWND. Activating the main HWND hides Quick Add by design. Therefore Settings could not be opened through the tray, and the important-alert test action could not be triggered safely through Computer Use.
- Settings and Important Alert were executed on a real STA WPF dispatcher in automated UI tests. These are production controls and templates, but this is not claimed as an end-to-end tray-path manual test.
- Display scaling was simulated deterministically with WPF `LayoutTransform` values of 1.0, 1.25, 1.5, and 2.0. Windows display scaling was not changed.
- High contrast was simulated by injecting black/white/yellow `SystemColors` resources into the production windows. The user's global Windows theme was not changed. A manual OS-toggle pass remains recommended before release.

## Keyboard and automation

| Surface | Result | Evidence |
| --- | --- | --- |
| Timeline | PASS | Normal production launch exposed named timeline/group/list elements through UI Automation. Existing timeline UI tests verify stable groups, selection, and command targets. |
| Quick Add | PASS | `First_Tab_expands_details_then_subsequent_focus_traversal_moves_to_next_field`; ambiguity choices are reachable by normal focus traversal; Escape behavior is covered by view-model tests. |
| Important Alert | PASS | Six visible action buttons have non-empty automation names. Complete, Ignore, and Snooze 5/10/30/60 are reachable by Tab; Enter activates the focused button; Escape and title-bar close map to Snooze 10. |
| Settings | PASS | Focus traversal is `HotkeyBox` → `SaveHotkeyButton` → `StartupCheckBox`, followed by the two reminder-level test actions and remaining controls. All interactive controls have visible text and automation names. |

There are no icon-only application controls in the new Settings or Important Alert windows. Every symbol is paired with a visible Chinese text label. Status and reminder importance use symbol plus text; color is supplementary.

## Scaling matrix

| Scale | Settings | Important Alert | Result |
| --- | --- | --- | --- |
| 100% (1.0) | Save action brought into the 820×620 viewport | Complete action brought into the 720×600 viewport | PASS |
| 125% (1.25) | Save action remains scroll-reachable | Complete action remains scroll-reachable | PASS |
| 150% (1.5) | Save action remains scroll-reachable | Complete action remains scroll-reachable | PASS |
| 200% (2.0) | Save action remains scroll-reachable | Complete action remains scroll-reachable | PASS |

Quick Add separately passes its 200%-equivalent compact-viewport scrolling test. The production timeline was visually inspected in normal mode; a manual per-monitor 125/150/200% timeline pass is still recommended.

## High contrast and reduced motion

- PASS (simulated): Settings background/text resolve to black/white, primary actions to yellow/black, and secondary actions to black/white.
- PASS (simulated): Important Alert uses the same system-color contrast pairs for its window and all actions.
- PASS: Shared TextBlock, TextBox, primary Button, and secondary Button styles use dynamic Windows system brushes, so Settings, Quick Add, Important Alert, and shared timeline text respond to system palette resources.
- PASS: Focus uses a visible two-pixel outline. Text fields also change border thickness on keyboard focus.
- PASS: No animations, fades, auto-scrolling, or motion-only feedback were added.

## Open manual release checks

- Toggle Windows High Contrast manually, restart the app, and inspect Timeline, Quick Add, Settings, and Important Alert.
- Inspect all four surfaces on physical monitors configured at 125%, 150%, and 200%, including a mixed-DPI multi-monitor placement check.
- Trigger a production important reminder, listen for the looped WAV, and confirm title-bar close snoozes ten minutes through the persisted reminder action.
