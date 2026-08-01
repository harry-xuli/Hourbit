# Task 2 Report: Typed reminder/todo quick-parse drafts

## Status

Implemented and verified. Quick parsing now returns `ItemDraft` payloads with concrete
`ReminderDraft` and `TodoDraft` results, and absolute numeric dates use the supplied
`CultureInfo`.

## TDD evidence

### RED

Added the locale/date/time/item matrix before the production API existed, then ran:

```text
dotnet test tests\Moment.Core.Tests\Moment.Core.Tests.csproj --configuration Release --filter "FullyQualifiedName~ChineseTimeParserTests"
```

The build failed for the expected missing contract:

- no four-argument `Parse(..., CultureInfo)` overload;
- no `TodoDraft` type.

After adding the contract and implementation, the first behavioral GREEN attempt exposed
five test fixtures whose absolute reminder dates were already in the past relative to the
fixed `Now`, plus one existing ambiguous `3点` expectation that correctly produces 03:00
and 15:00 choices. The fixtures were corrected to future dates and to the established
two-choice behavior; parser code was not weakened to accept past reminders.

### GREEN

Exact focused command requested by the brief:

```text
dotnet test tests/Moment.Core.Tests/Moment.Core.Tests.csproj --filter "FullyQualifiedName~ChineseTimeParserTests"
```

Result: 68 passed, 0 failed, 0 skipped.

Fresh full Release suite before commit:

```text
dotnet test Moment.slnx --configuration Release
```

Results:

- Moment.Core.Tests: 137 passed;
- Moment.Infrastructure.Tests: 48 passed;
- Moment.Windows.Tests: 88 passed;
- Moment.App.Tests: 132 passed;
- total failures: 0.

## Test matrix

| Area | Covered cases |
| --- | --- |
| Cultures | `zh-CN`, `en-US`, `en-GB` |
| Numeric order | leading four-digit year always YMD; year-last MDY/DMY derived from `ShortDatePattern`; ambiguous order differs between en-US and en-GB |
| Date syntax | `-`, `/`, `.`, Chinese `年/月/日`, relative `今天`/`明天`/`明早` |
| Calendar validity | valid 2028 leap day; invalid 2026 leap day, April 31, month 13, mixed separators |
| Clock syntax | `00:00`, `23:59`, `0点`, `14点30分`, `下午2点`, `晚上8点半`, `晚上12点` |
| Clock validity | invalid `24:00`, `23:60`, `24点`, `9点60分` |
| Typed outcomes | title+date+time reminder; time-only next-valid reminder; date-only todo; title-only todo |
| Recurrence | timed recurrence remains a reminder; no-clock recurrence text remains an exact ordinary todo title |
| Invalid/conflict | multiple dates, multiple clocks, duration+date/time, recurrence+absolute date+time, missing/overlong title, past explicit reminder |
| Compatibility | existing vague choices, relative durations, recurring reminders, and DST gap/overlap resolution |

## Parsing decisions

- A numeric token whose first field has four digits is parsed as YMD regardless of culture.
- With a four-digit year in the last field, month/day order is determined by the relative
  positions of `M` and `d` in `culture.DateTimeFormat.ShortDatePattern`.
- `DateOnly` construction validates calendar values without normalization.
- Scheduling text is removed from the title only after its date/time value is valid.
- A recurrence prefix is removed only when a definite clock creates a recurring reminder.
  Consequently `每天锻炼`, `每周一整理房间`, and `每周五晚上看书` are undated,
  non-recurring todos whose titles remain unchanged.
- A time without a date uses the existing next-valid occurrence behavior; an explicit dated
  reminder in the past remains invalid.

## Call-site compatibility

The new interface signature and `ItemDraft` payload required minimal compilation updates
outside the parser-owned files:

- `QuickAddViewModel` supplies `CultureInfo.CurrentCulture`.
- Existing Quick Add reminder preview/persistence executes only for `ReminderDraft`.
  A `TodoDraft` is temporarily surfaced as unavailable and is never persisted; Task 5 owns
  the real todo dispatch and preview.
- `QuickAddChoiceViewModel` retains its existing reminder-only choice behavior with an
  explicit type guard. Current parser ambiguity choices are all reminder drafts.
- `SmokeTestRunner` supplies deterministic `zh-CN` and requires a reminder result.
- Three App test parser stubs accept the new culture parameter without changing behavior.

## Files changed

- `src/Moment.Core/Parsing/ParseResult.cs`
- `src/Moment.Core/Parsing/ChineseTimeParser.cs`
- `tests/Moment.Core.Tests/Parsing/ChineseTimeParserTests.cs`
- `src/Moment.App/QuickAdd/QuickAddViewModel.cs`
- `src/Moment.App/Diagnostics/SmokeTestRunner.cs`
- `tests/Moment.App.Tests/Composition/QuickAddTimelineCompositionTests.cs`
- `tests/Moment.App.Tests/QuickAdd/QuickAddViewModelTests.cs`
- `tests/Moment.App.Tests/QuickAdd/QuickAddWindowTests.cs`

## Concerns / handoff

- Task 5 must replace the temporary Quick Add todo-unavailable branch with todo service
  dispatch and type-specific previews.
- The old test expectation that `每周五晚上看书` created ambiguous recurring reminder
  choices intentionally changed: the approved binding rule says every no-clock recurrence
  phrase is an ordinary todo title.
- This task performs no persistence from the parser.

## Fix Round 1

### Findings addressed

1. Clock regexes could restart inside signed or overlong values, allowing inputs such as
   `123点`, `-1点`, and `-1:00` to hide invalid prefixes and create reminder drafts.
2. Malformed date/time-like text was checked only when no valid token existed, so a valid
   relative date or clock could mask a malformed companion and leave it in the title.
3. Raw `IndexOf('M')` / `IndexOf('d')` calls treated quoted and escaped literals in
   `ShortDatePattern` as date fields.

### RED

The first regression matrix added signed, overlong, embedded, and adjacent malformed
Chinese/colon clocks; valid+malformed companion tokens; custom quoted/escaped short-date
patterns; and ordinary version text. Running:

```text
dotnet test tests/Moment.Core.Tests/Moment.Core.Tests.csproj --filter "FullyQualifiedName~ChineseTimeParserTests"
```

produced 14 expected failures out of 87 cases. The failures reproduced all three findings,
including successful drafts for malformed clocks, a mixed-separator date left in a todo
title, wrong DMY resolution, and `v1.2.3` rejected as a date.

After the first implementation pass, the focused suite was 87/87. A second boundary slice
added clocks embedded after title text, trailing signs, and the ordinary title
`发布 v1.2.2026`. That title failed because `1.2.2026` was stripped as a dated todo token,
confirming one remaining false positive.

### GREEN

The exact focused command after the final boundary fix:

```text
dotnet test tests/Moment.Core.Tests/Moment.Core.Tests.csproj --filter "FullyQualifiedName~ChineseTimeParserTests"
```

Result: 92 passed, 0 failed, 0 skipped.

The full Release command:

```text
dotnet test Moment.slnx --configuration Release
```

Results:

- Moment.Core.Tests: 161 passed;
- Moment.Infrastructure.Tests: 48 passed;
- Moment.Windows.Tests: 88 passed;
- Moment.App.Tests: 132 passed;
- total failures: 0.

### Implementation notes

- Valid clock regexes now have digit/sign boundaries on both sides. Separate lightweight
  colon and Chinese-clock markers identify every scheduling-like clock occurrence; every
  marker must be covered by a complete valid candidate before any draft can be returned.
  This rejects signed, overlong, embedded, chained, and valid+malformed clock combinations.
- Date-like candidates with a four-digit leading or trailing year are independently matched
  and must be covered by a valid same-separator numeric date. Mixed separators therefore
  remain actionable invalid input even alongside a valid relative/absolute date.
- Numeric dotted text without a four-digit endpoint remains ordinary title text. ASCII
  identifier adjacency also prevents `v1.2.2026` from being consumed as a date, while normal
  standalone dates and dates adjacent to Chinese title text remain supported.
- The short-date scanner skips single- and double-quoted literals, doubled quote escapes,
  and backslash-escaped characters before locating actual `M` and `d` field runs. Tests use
  `'M''/d' dd/MM/yyyy` for DMY and `\d/\M MM/dd/yyyy` for MDY.

### Remaining concerns

No new call-site changes were required in this round. The Task 5 Quick Add todo-dispatch
handoff from the original implementation remains unchanged.
