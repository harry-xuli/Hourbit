# Hourbit Analytics and Reporting Design

**Date:** 2026-08-01  
**Target release:** 0.2.0  
**Depends on:** `2026-08-01-hourbit-core-items-design.md`

## Goal

Add an offline analytics window, day/week/month filtering on the main timeline,
and local PDF plus CSV report export. Todos and timed reminders share one
analytics read model while retaining separate operational domains.

## Navigation and Time Ranges

The main window provides `日 / 周 / 月` period selectors, previous/next period
navigation, and a visible period label. Day is the default. Week boundaries use
the active Windows culture's first day of week. The annual view is deliberately
excluded from the main timeline.

Two clickable summary cards remain visible above the sections:

- `过去 7 天已完成` counts todos and reminders by actual completion timestamp;
- `未来 14 天计划` counts pending dated todos and scheduled reminders whose due
  value falls after now and within the next 14 local calendar days.

Selecting a summary card opens the corresponding filtered analytics view. The
main list applies the selected day, week, or month to both the todo and reminder
sections. Pending overdue todos remain pinned at the start of the todo section
even when their due date precedes the selected period, so filtering cannot hide
unfinished overdue work.

An `分析报告` command opens the dedicated analytics window.

## Analytics Read Model

`AnalyticsQueryService` creates an immutable `AnalyticsSnapshot` for one local
date range and time zone. It reads reminder occurrence history, action history,
and todos, and then produces:

- totals for completed, future planned, overdue, and deleted records;
- status, item-type, and importance distributions;
- completion trend buckets;
- optional detail rows for export.

The same snapshot is the sole input to WPF charts, PDF rendering, and CSV
serialization. Neither the UI nor an exporter re-queries or recalculates data.

### Counting rules

- Completed metrics use the actual completion timestamp.
- Future plans include only non-completed, non-deleted records with a due date or
  time in the requested forward range.
- An overdue todo is pending with a due date before the current local date.
- An overdue reminder is a persisted `Missed` occurrence.
- Undated todos count in overall todo and completion totals but cannot appear in
  date-based trend buckets.
- Recurring reminder occurrences are counted separately because each occurrence
  represents a distinct planned action.
- Primary KPI cards and the default status chart exclude deleted records. A
  separate deleted count and report appendix preserve audit visibility without
  distorting completion rate.

Date range endpoints are inclusive local dates and are converted to UTC only at
the repository query boundary.

## Soft Delete and Schema Version 4

Schema version 4 adds nullable `deleted_at` timestamps to reminder occurrences
and todos. Operational reminder, scheduler, todo, and main timeline queries
exclude rows whose `deleted_at` is set. Analytics history queries may include
them.

Deleting a single occurrence marks that occurrence deleted. Deleting this and
future occurrences marks matching scheduled occurrences deleted and prevents
future recurrence generation while preserving already handled history. Deleting
a todo marks the todo deleted. The operation is transactional and idempotent.

Backup and restore continue to cover the complete SQLite database, including
soft-delete history.

## Analytics Window

Available ranges are:

- recent 7 days;
- recent 30 days;
- current month;
- a selected calendar year;
- an inclusive custom date range.

The approved balanced overview contains:

1. KPI cards for completed, future planned, and overdue;
2. a default donut chart showing completed / incomplete / overdue;
3. a completion bar chart using daily buckets for short ranges and weekly or
   monthly buckets for longer ranges;
4. a donut-dimension selector for status, todo/reminder type, or importance.

Every chart exposes a textual summary and keyboard focus information. Colors use
dynamic application resources and remain distinguishable in Windows high
contrast mode. Empty ranges show a useful zero-data state rather than an empty
plot or exception.

## Export

The export command always asks the user to choose one privacy mode:

- `完整报告`: titles and stable record identifiers may appear;
- `匿名统计报告`: titles and record identifiers are omitted.

It then opens a Windows save dialog for an export base path and produces a PDF
summary plus a CSV detail file from the same snapshot.

### PDF

The PDF contains Hourbit product/version metadata, generation timestamp, local
date range and time zone, KPI values, the donut and trend charts, metric
definitions, and a short summary. A complete report may include a titled record
table and a deleted-record appendix. An anonymous report contains aggregate data
only.

### CSV

CSV uses UTF-8 with a byte-order mark for direct Excel compatibility. Complete
rows include item type, title, importance, created date, due date/time,
completion time, deletion time, and status. Anonymous rows omit title and stable
identifiers while retaining non-identifying dimensions and dates.

Export never uploads, opens a network connection, or silently launches another
application. On success Hourbit displays both paths and an optional
`打开所在文件夹` action. A cancelled save dialog creates no files. Partial failures
remove files created by that export attempt and leave existing files untouched.

## Rendering Boundaries

Chart geometry is calculated in a renderer-neutral model. A WPF renderer draws
the live controls, while the PDF renderer consumes the same sectors, labels,
colors, and trend points. This avoids maintaining independent statistical chart
logic. CSV serialization consumes detail rows directly and follows RFC-style
quoting for commas, quotes, and newlines.

## Error Handling

- Invalid or reversed custom ranges are rejected before querying.
- Analytics cancellation discards the stale snapshot and keeps the last valid
  view visible.
- Query errors appear inline without affecting reminder scheduling.
- Export validates the destination, privacy mode, and snapshot before writing.
- File access or disk-space failures produce an actionable local error and clean
  up only the incomplete files created by the current attempt.

## Testing and Acceptance

Automated coverage includes:

- local day boundaries, regional week starts, leap dates, DST changes, and
  inclusive range endpoints;
- completion, future-plan, overdue, undated, recurring-occurrence, and deleted
  counting rules;
- schema v4 migration, idempotence, and operational exclusion of soft-deleted
  rows;
- day/week/month main filters and the two summary-card ranges;
- chart geometry, zero-data states, dimension switching, accessibility text,
  and high-contrast resources;
- deterministic PDF metadata and chart data;
- CSV BOM, escaping, full/anonymous column sets, cancellation, and partial-file
  cleanup;
- proof that PDF, CSV, and live charts consume the same snapshot identifier and
  totals.

Manual Windows 11 acceptance verifies navigation, readable charts at supported
scales, complete and anonymous exports, Chinese text in PDF/CSV, and opening the
export folder without transmitting data.

## Out of Scope

- cloud dashboards, telemetry, accounts, sharing, email, or scheduled reports;
- Excel workbook export;
- user-authored custom chart builders;
- year view on the main timeline.

