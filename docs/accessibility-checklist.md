# Accessibility verification checklist

Verified on 2026-07-30 for the Windows 11 x64 WPF build.

## Evidence boundaries

- The production app was launched normally and inspected with Windows UI Automation and Windows Graphics Capture. The timeline rendered in normal mode with a readable 42-node accessibility tree; the Quick Add window rendered with a visible focus indicator.
- Computer Use could not target the notification-area icon or the `ShowInTaskbar="False"` Quick Add HWND. Activating the main HWND hides Quick Add by design. Therefore Settings could not be opened through the tray, and the important-alert test action could not be triggered safely through Computer Use.
- Settings and Important Alert were executed on a real STA WPF dispatcher in automated UI tests. These are production controls and templates, but this is not claimed as an end-to-end tray-path manual test.
- Display scaling was simulated deterministically with WPF `LayoutTransform` values of 1.0, 1.25, 1.5, and 2.0. Windows display scaling was not changed.
- High contrast was simulated by injecting black/white/yellow `SystemColors` resources and applying the production high-contrast palette to the production windows. Assertions inspect the rendered foreground/background brushes, not only container backgrounds. The user's global Windows theme was not changed. A manual OS-toggle pass remains recommended before release.

## Keyboard and automation

| Surface | Result | Evidence |
| --- | --- | --- |
| Timeline | PASS | `NewReminderButton` moves to the selected reminder list; the non-actionable outer `ItemsControl` is not a tab stop. The Enter binding is deterministically verified as `EditCommand`. |
| Quick Add | PASS | First Tab expands details and focuses title; later traversal reaches detail fields or ambiguity choices. The production Enter path submits only from the sentence input, and the Escape binding hides without clearing text. |
| Important Alert | PASS | `MoveFocus` verifies all six named actions in visible order. The production key handler is tested for Enter on the focused action and Escape → Snooze 10; title-bar close also maps to Snooze 10. |
| Settings | PASS | `MoveFocus` verifies all 13 interactive controls in visible order, including the two behavior test actions, sound controls, data/backup folder actions, and Save. |

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

- PASS (simulated): Settings and Important Alert resolve window/control/highlight pairs to black/white, black/white, and yellow/black through the production palette.
- PASS (simulated): Quick Add footer text is white on the injected dark control surface and measures at least 4.5:1.
- PASS (simulated): Timeline header and footer text are white on the injected dark control surface; a selected row and every visible row text element resolve to the exact yellow/black system highlight pair. The pair remains exact while the selected row is also under the pointer.
- PASS: Required surfaces use matching dynamic system backgrounds, foregrounds, borders, highlight text, and focus brushes.
- PASS: Focus uses a visible two-pixel outline. Text fields also change border thickness on keyboard focus.
- PASS: No animations, fades, auto-scrolling, or motion-only feedback were added.

## Open manual release checks

- Toggle Windows High Contrast manually, restart the app, and inspect Timeline, Quick Add, Settings, and Important Alert.
- Inspect all four surfaces on physical monitors configured at 125%, 150%, and 200%, including a mixed-DPI multi-monitor placement check.
- Trigger a production important reminder, listen for the looped WAV, and confirm title-bar close snoozes ten minutes through the persisted reminder action.
