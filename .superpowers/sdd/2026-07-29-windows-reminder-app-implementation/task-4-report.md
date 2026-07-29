# Task 4 — Deterministic Chinese Time Parser

## Delivered files

- `src/Moment.Core/Parsing/ParseResult.cs`
- `src/Moment.Core/Parsing/ChineseTimeParser.cs`
- `tests/Moment.Core.Tests/Parsing/ChineseTimeParserTests.cs`
- `tests/Moment.TestSupport/TestData.cs`

## TDD evidence

1. RED: `dotnet test tests\\Moment.Core.Tests\\Moment.Core.Tests.csproj --filter "Parses_supported_phrases|Returns_choices_for_ambiguous_phrases"` failed as expected because `Moment.Core.Parsing` and `ReminderDraft` did not yet exist (CS0234 and CS0246).
2. GREEN: the same targeted command passed 6/6 after the minimal parser and result types were introduced.
3. RED: after adding assertions that ambiguous choices clean time phrases out of titles, `Returns_choices_for_ambiguous_phrases` failed 3/3: titles incorrectly retained `晚上`, `待会`, and `下周`.
4. GREEN: the focused ambiguous-title command passed 3/3 after explicit token removal.
5. RED: after adding recurrence-time assertions, `Parses_recurrence_rules` failed 2/2: expected 18:00/16:00 but the stored recurrence rule was 00:00.
6. GREEN: the focused recurrence command passed 2/2 after carrying the parsed `TimeOnly` into the recurrence rule.

## Final verification

- `dotnet test tests\\Moment.Core.Tests\\Moment.Core.Tests.csproj --filter Parsing` — passed 12/12.
- `dotnet test tests\\Moment.Core.Tests\\Moment.Core.Tests.csproj` — passed 21/21.
- `dotnet test Moment.slnx` — passed: Moment.Core.Tests 21/21; Moment.Infrastructure.Tests 19/19.
- `dotnet build Moment.slnx --no-restore` — succeeded with 0 warnings and 0 errors.

Smart App Control did not block any command; no retry and no `0x800711C7` external failure occurred.

## Design decisions and concerns

- Parsing is ordered and uses compiled, named-group regular expressions: recurrence prefix, relative duration, date token, and clock time. Chinese phrases are not delegated to `DateTime.Parse` or external services; numeric values use invariant integer parsing only.
- The parser derives calendar dates from `now` converted to the supplied `TimeZoneInfo`. For DST, it advances invalid local times to the first valid minute and chooses the earlier UTC instant for ambiguous local times, matching the existing recurrence policy.
- `晚上`, `待会`, and `下周` return explicit `ParseChoice` candidates rather than silently choosing a due time. UI callers must require a choice before scheduling.
- Explicit past due times, blank input, missing title, invalid clock values, and titles longer than 200 characters return `ParseResult.Invalid`.
- The supported grammar is intentionally narrow and deterministic. Chinese numerals, natural-language variants outside the stated grammar, and unsupported composite expressions are not guessed.

## Fix round 1 — reviewed parser findings

### TDD evidence

1. RED: added adversarial parser tests, then ran the focused filter. It failed as expected: `下周下午4点写周报` was a `Success`; `待会3点提醒我喝水` was also a `Success` when evaluated at 01:00; recurring `每周五晚上看书` choices had no recurrence; ambiguous blank/overlong titles returned choices; evening choices could be past; and duration/date or duration/clock combinations were accepted.
2. GREEN: after passing ambiguity context through the parser, validating titles before choice construction, and making one-off alternatives advance to the next future local occurrence, the focused adversarial command passed 17/17.

### Changes and policy

- A vague date token (`下周`) is detected before a success result even when a clock is present. Its explicit alternatives retain the parsed clock (for example, 16:00) and remove the token from the title.
- A vague relative token plus a clock (`待会3点`) returns explicit clock alternatives, rather than silently selecting the current-day 3:00. This is deliberately an ambiguity policy, not a natural-language inference.
- Ambiguous recurring clocks retain their recurrence rule. Each choice gets a rule with the candidate clock and the next valid recurrence occurrence as `DueAt`.
- Ambiguous paths validate title length and emptiness before creating a choice. Every choice is strictly later than `now`; passed evening times move to the following day.
- Parser-level invalid clocks (`0点`, `24点`, `9点60分`), duration numeric overflow, recurrence-with-duration, duration-with-date, and duration-with-clock combinations are rejected as `Invalid`.
- Parser DST tests cover invalid local times (advance to the first valid minute) and ambiguous local times (earlier UTC instant).

### Verification

- Focused adversarial parser filter: passed 17/17.
- `dotnet build Moment.slnx --no-restore`: succeeded with 0 warnings and 0 errors.
- The first full Parsing run and its required single retry were blocked by Windows Smart App Control loading `Moment.Core.dll` (`0x800711C7`), after compilation completed. The broader Core and solution test attempts failed for the same external assembly-policy reason, including pre-existing Core and Infrastructure tests; they did not expose a parser assertion failure.

## Fix round 2 — distinct explicit-period choices

### TDD evidence

- Added focused real-parser tests before implementation for `待会下午3点提醒我喝水`, `待会中午12点提醒我喝水`, and unqualified `待会12点提醒我喝水`. They require two distinct, future-only choices with a clean title and no `下午0点` label.
- The first RED attempt was blocked before assertions by the external Smart App Control assembly policy (`0x800711C7`). After the minimal change, the focused test passed 3/3.

### Changes and final verification

- The parser now passes the matched period into vague-relative clock choice generation. An explicit period (and unqualified 12:00) creates next-occurrence and next-day choices for the same clock, rather than inventing an AM/PM alternative. Labels retain the supplied period, so noon is `中午12点`, never `下午0点`.
- `dotnet test tests\\Moment.Core.Tests\\Moment.Core.Tests.csproj --filter "Returns_distinct_future_choices_when_a_vague_relative_phrase_has_a_disambiguated_clock"` — passed 3/3.
- `dotnet test tests\\Moment.Core.Tests\\Moment.Core.Tests.csproj --filter Parsing` — passed 32/32.
- `dotnet test tests\\Moment.Core.Tests\\Moment.Core.Tests.csproj` — passed 41/41.
- `dotnet test Moment.slnx` — passed: Moment.Core.Tests 41/41; Moment.Infrastructure.Tests 19/19.
- `dotnet build Moment.slnx --no-restore` — succeeded with 0 warnings and 0 errors.
