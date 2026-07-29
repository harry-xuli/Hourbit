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
