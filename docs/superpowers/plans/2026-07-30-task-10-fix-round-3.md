# Task 10 fix round 3 verification

Verified on 2026-07-30 from base
`a40758279bf32c6cf6381f4f9e514adbe3786fca`.

## Timeline selected and hover precedence

- RED: a rendered selected Timeline row resolved to the injected system
  highlight/highlight-text pair until the pointer moved over it. The later
  hover trigger replaced only the background with the dark control surface,
  leaving black selection text on dark slate gray.
- GREEN: a final selected-and-hover `MultiTrigger` restores the dynamic
  selection background, selection text, and focus border. Normal unselected
  hover and selected-not-hovered behavior are unchanged.
- The WPF regression moves the real system pointer to the rendered row,
  synchronizes WPF input, waits for dispatcher idle, verifies
  `IsMouseOver == true`, and then inspects the rendered template border and
  every visible text brush. Under the simulated palette the pair is exactly
  yellow/black.

## Verification

- Focused selected-plus-hover rendered test: 1/1 passed.
- Full no-build/no-restore suite with 45-second hang timeout:
  Core 78/78, Infrastructure 21/21, Windows 85/85, App 91/91;
  275/275 total.
- Fresh solution build: 0 warnings, 0 errors.
