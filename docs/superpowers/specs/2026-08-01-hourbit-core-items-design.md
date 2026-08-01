# Hourbit 0.2 Core Items Design

**Date:** 2026-08-01  
**Target release:** 0.2.0  
**Product name:** Hourbit 日程

## Goal

Extend the existing Windows 11 reminder application with first-class todos,
locale-aware absolute-date input, consistent 24-hour time presentation, a
complete public-facing rename to Hourbit 日程, and one authoritative release
version source. Existing reminders, user data, upgrade identity, and reminder
delivery behavior must remain compatible.

The analytics dashboard and report export requested alongside this work are a
separate subproject and are intentionally excluded from this specification.

## Product Rules

Quick entry produces exactly one of two item types:

- Text containing a definite clock time creates a timed reminder.
- Text containing a date but no clock time creates an all-day todo with that
  local calendar date as its due date.
- Text containing neither a date nor a clock time creates an undated todo.

An all-day todo never rings and never sends a Windows notification. When its due
date has passed, it remains pending and is marked `已逾期` until the user
completes or deletes it.

Phrases such as `每天锻炼` or `每周一整理房间` that contain a recurrence phrase
but no clock time are stored verbatim as ordinary, non-recurring todo titles.
Recurring todos are not part of release 0.2.0.

## Architecture

Todos are a separate domain from scheduled reminders. This keeps nullable or
sentinel times out of the reminder scheduler and prevents accidental delivery.

### Todo domain

`TodoItem` contains:

- stable identifier;
- normalized title;
- creation timestamp;
- optional local `DateOnly` due date;
- importance;
- completion state;
- optional completion timestamp.

`ITodoRepository` owns todo persistence. `TodoService` owns creation, editing,
completion, deletion, and conversion orchestration. Todo records never implement
or enter `ScheduledReminder` and never appear in scheduler due queries.

### Parsing boundary

Quick entry returns a discriminated result whose successful payload is either a
`ReminderDraft` or a `TodoDraft`. Existing ambiguous and invalid result states
remain available. The Quick Add view model dispatches the successful draft to
the corresponding service and renders a type-specific preview before Enter can
persist it.

### Timeline boundary

The application query layer obtains todo rows and reminder rows independently.
The view model exposes separate collections for the two visual sections rather
than making the scheduler understand todos.

## Date and Time Parsing

### Accepted absolute dates

The parser accepts:

- `2026-08-05`, `2026/08/05`, and `2026.08.05`;
- `2026年8月5日`;
- year-last numeric dates using the active Windows regional short-date order.

Separators `/`, `-`, and `.` are accepted. A four-digit leading year is always
interpreted as year-month-day. When the year is last, the parser derives
day/month order from the Windows culture's `ShortDatePattern`. Thus an ambiguous
`03/04/2026` follows the machine's regional order instead of showing two choices.
Impossible dates are invalid and are never normalized into another date.

Existing relative expressions such as `今天` and `明天` continue to work.

### Accepted clock times

The parser accepts 24-hour forms including `14:30`, `14点30分`, and `23点`.
For compatibility it also accepts existing Chinese period expressions such as
`下午2点` and `晚上8点半`. All previews and saved-item displays use `HH:mm`.
Values outside `00:00` through `23:59` are invalid.

### Parsing outcomes

- title + date + time: timed reminder;
- title + time: existing today/next-valid-time reminder behavior;
- title + date: all-day dated todo;
- title only: undated todo;
- invalid or contradictory tokens: an actionable error, with no persistence.

Examples of successful previews are `待办 · 无日期`,
`待办 · 截止 2026-08-05`, and `提醒 · 2026-08-05 14:30`.

## Persistence and Migration

Database schema version 3 adds a `todos` table without rewriting the existing
`items`, `occurrences`, `recurrence_rules`, or action history tables. The table
stores the todo fields above, with the due date encoded as an invariant ISO
calendar date and completion timestamps encoded consistently with existing
timestamps.

Migration must be transactional and idempotent. Opening a version 1 or version 2
database upgrades it to version 3 while preserving all existing reminder rows.
Backup and restore continue to operate on the whole database file and therefore
include todos without a separate backup format.

## Main Window and Interaction

The main content uses the approved same-page split layout:

1. `待办事项` appears above `定时提醒`.
2. Pending todos sort as overdue, due today, future due, then undated. Stable
   identifier order breaks equal-date ties.
3. Completed todos appear in a collapsible `已完成` area and retain their
   completion time for future reporting.
4. Timed reminders retain the existing `已错过 / 接下来 / 已完成` behavior.
5. `下一个提醒` counts only scheduled reminders.
6. `今日完成` is the combined number of todos and reminders completed today;
   its tooltip separates the two counts.

Todo rows support complete, edit, and delete. Existing keyboard and automation
patterns remain available, with distinct accessible names for todo and reminder
sections.

## Todo and Reminder Conversion

Editing supports conversion in both directions:

- adding a clock time to a todo converts it to a reminder;
- removing the clock time from a reminder converts it to a dated or undated
  todo, depending on whether a date remains.

Conversion is one transactional service operation. It creates the destination
record before removing the source and preserves title, importance, and completion
state. A converted completed item remains completed and does not schedule a
notification. A failed conversion leaves the original item unchanged.

## Product Rename and Compatibility

All user-facing product identity becomes `Hourbit 日程`, including window
titles, tray tooltip and menu wording, notifications, settings, shortcuts,
installer and uninstaller entries, documentation, and release artifacts. The
published executable is `Hourbit.exe`.

The existing installer AppId, database file location, data-directory name, and
`Moment.*` code namespaces remain unchanged. These are compatibility identities,
not visible branding. Keeping them allows an Hourbit installer to upgrade the
existing application and reuse its reminders without migration by product name.

## Version Source of Truth

The repository root contains `Version.props`, imported by the build. It defines:

- product name `Hourbit 日程`;
- assembly name `Hourbit`;
- semantic version `0.2.0`;
- release date `2026-08-01`.

MSBuild uses these values for assembly and executable metadata. The release
script queries the same properties for Inno Setup defines, artifact naming, and
validation. The settings footer shows
`版本 0.2.0 · 发布于 2026-08-01` from assembly metadata.

Release validation fails when the semantic version or ISO release date is
missing or malformed. The installer and scripts must not retain a second
hard-coded product version. Future releases update `Version.props` as the single
required version-change location; the release checklist explicitly verifies the
EXE, installer, settings footer, and artifact names agree.

## Error Handling

- Parser failures retain the input and display a specific validation message.
- Todo persistence and conversion errors surface in the existing inline error
  area and do not partially mutate source records.
- Database migration rolls back on failure.
- Missing or invalid version properties stop Release packaging before publish.
- Existing reminder runtime error handling remains unchanged.

## Testing and Acceptance

Automated coverage includes:

- parser matrices for `zh-CN`, `en-US`, and `en-GB` short-date orders;
- ISO, Chinese, separator variants, leap dates, invalid dates, 24-hour bounds,
  and Chinese period compatibility;
- title-only and date-only todo outcomes and time-bearing reminder outcomes;
- schema v3 creation, idempotence, and upgrades from schema versions 1 and 2;
- todo CRUD, ordering, completion timestamps, and scheduler exclusion;
- atomic todo/reminder conversions, including failure rollback;
- split-section UI layout, accessible names, overdue styling, counts, and
  type-specific Quick Add previews;
- single-source product/version metadata across EXE, Inno Setup, settings, and
  release filenames;
- a clean Release build, installer compile, packaged self-test, and upgrade-safe
  database smoke test.

Manual Windows 11 acceptance verifies locale-sensitive entry using the current
regional settings, 24-hour display, the Hourbit identity in window/tray/installer,
and that dated todos become visually overdue without producing any notification.

## Out of Scope

- recurring todos;
- cloud sync, accounts, collaboration, or mobile clients;
- renaming internal namespaces or relocating existing user data;
- analytics charts and report export, which have their own design subproject.
