# Chinese Date Day Suffix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Hourbit natively parse both `日` and `号` as Chinese calendar-day suffixes without rewriting user input.

**Architecture:** Extend the existing `ChineseDatePattern` and `ChineseDateMarkerPattern` together so valid-token recognition and malformed-token rejection retain identical boundaries. Reuse all current annual rollover, date validation, time-zone conversion, title removal, and Quick Add dispatch logic.

**Tech Stack:** C# 14, .NET 10, `System.Text.RegularExpressions`, xUnit, WPF.

## Global Constraints

- Accept both `日` and `号`; do not normalize or replace the input text.
- Preserve existing numeric-date culture handling, 24-hour time handling, recurrence behavior, todo behavior, and database schema.
- A malformed `号` date must return `ParseResult.Invalid`, not fall back to a clock-only reminder.
- Do not stage or modify the paused PDF reporting files or `tests/Moment.App.Tests/WpfTestHost.cs` in this change.

---

### Task 1: Parse `日` and `号` with identical validation

**Files:**
- Modify: `tests/Moment.Core.Tests/Parsing/ChineseTimeParserTests.cs`
- Modify: `src/Moment.Core/Parsing/ChineseTimeParser.cs`

**Interfaces:**
- Consumes: `ChineseTimeParser.Parse(string, ParseContext)` and existing `ParseResult.Success` draft types.
- Produces: unchanged parser interface; expanded accepted syntax only.

- [ ] **Step 1: Write failing production-behavior tests**

Add cases that call the real parser:

```csharp
[Theory]
[InlineData("10月3日 早上6点 闺女办事")]
[InlineData("10月3号 早上6点 闺女办事")]
public void Parses_Chinese_day_suffix_variants(string text)
{
    var draft = Reminder(text, now: DateTimeOffset.Parse("2026-08-09T10:25:00+08:00"));
    Assert.Equal("闺女办事", draft.Title);
    Assert.Equal(DateTimeOffset.Parse("2026-10-03T06:00:00+08:00"), draft.DueAt);
}
```

Also cover explicit-year `号`, date-only `号`, impossible `13月3号`, overlong/signed `号`, and ordinary title text containing `号` that is not a date token.

- [ ] **Step 2: Run the new focused tests and verify RED**

Run:

```powershell
dotnet test tests\Moment.Core.Tests\Moment.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Chinese_day_suffix|FullyQualifiedName~malformed_Chinese_date_markers"
```

Expected: `日` cases remain green; valid `号` cases fail because the parser returns a clock-only reminder or undated todo.

- [ ] **Step 3: Implement the minimal parser change**

Change both regexes from the literal suffix `日` to the grouped suffix `(?:日|号)` while preserving their current lookbehind/lookahead boundaries:

```csharp
private static readonly Regex ChineseDatePattern = new(
    "(?<![\\d年])(?:(?<year>\\d{4})年)?(?<month>\\d{1,2})月(?<day>\\d{1,2})(?:日|号)(?!\\d)",
    RegexOptions.Compiled | RegexOptions.CultureInvariant);

private static readonly Regex ChineseDateMarkerPattern = new(
    "(?<![\\d年])(?:(?:[+-]?\\d+)年)?[+-]?\\d+月[+-]?\\d+(?:日|号)(?!\\d)",
    RegexOptions.Compiled | RegexOptions.CultureInvariant);
```

Do not add input normalization.

- [ ] **Step 4: Verify GREEN and parser regression coverage**

Run:

```powershell
dotnet test tests\Moment.Core.Tests\Moment.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ChineseTimeParserTests"
dotnet test tests\Moment.Core.Tests\Moment.Core.Tests.csproj -c Release --no-restore --maxcpucount:1
```

Expected: all selected tests pass with zero failures.

- [ ] **Step 5: Commit only parser files**

```powershell
git add -- src/Moment.Core/Parsing/ChineseTimeParser.cs tests/Moment.Core.Tests/Parsing/ChineseTimeParserTests.cs
git diff --cached --check
git commit -m "fix: parse Chinese day number suffix"
```

---

### Task 2: Verify Quick Add and the running Windows application

**Files:**
- No source changes expected.

**Interfaces:**
- Consumes: the parser behavior from Task 1 through the existing Quick Add composition.
- Produces: a newly built and running Debug Hourbit executable.

- [ ] **Step 1: Run Quick Add integration tests**

```powershell
dotnet test tests\Moment.App.Tests\Moment.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~QuickAddViewModelTests|FullyQualifiedName~CompositionRootTests"
```

Expected: all selected tests pass with zero failures.

- [ ] **Step 2: Stop only the exact running Debug Hourbit process**

Resolve the process path and stop it only if it equals:

```text
D:\Coding\window alert tool\.worktrees\hourbit-0.2-implementation\src\Moment.App\bin\Debug\net10.0-windows10.0.22621.0\Hourbit.exe
```

- [ ] **Step 3: Build and restart Hourbit**

```powershell
dotnet build src\Moment.App\Moment.App.csproj -c Debug --no-restore
```

Start the exact executable above, then verify the resulting `Hourbit` process reports `Responding = True`.

- [ ] **Step 4: Request an independent scoped review**

Review only the parser implementation commit against this specification. Require zero Critical and zero Important findings before reporting completion.
