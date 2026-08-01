# Hourbit Analytics and Reporting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to execute this plan task-by-task. Use `superpowers:test-driven-development` for every behavior change and `superpowers:verification-before-completion` before completion.

**Goal:** Add day/week/month timeline filtering, offline analytics with accessible native WPF charts, and matching full/anonymous PDF plus CSV exports.

**Architecture:** Build one immutable analytics snapshot from todos and reminder history. Main summaries, live charts, and exporters consume that same read model. Schema v4 soft-deletes operational records while retaining their history for analytics.

**Tech Stack:** .NET 10, C# 14, WPF drawing primitives, Microsoft.Data.Sqlite, PDFsharp-WPF 6.2.4, xUnit.

## Global Constraints

- Execute after both recovery and core-items plans.
- No network access, telemetry, cloud storage, or automatic file launching.
- Local-date ranges are inclusive and convert to UTC only at repository boundaries.
- Default chart/KPI calculations exclude deleted records; deleted history remains separately queryable.
- PDF, CSV, and live charts must carry the same `SnapshotId` and totals.
- Complete each task with focused tests and a commit.

---

## Task 1: Add schema v4 soft delete and operational exclusions

**Files:**
- Modify: `src/Moment.Infrastructure/Data/DatabaseMigrator.cs`
- Modify: `src/Moment.Core/Abstractions/IReminderRepository.cs`
- Modify: `src/Moment.Core/Abstractions/ITodoRepository.cs`
- Modify: `src/Moment.Infrastructure/Data/SqliteReminderRepository.cs`
- Modify: `src/Moment.Infrastructure/Data/SqliteTodoRepository.cs`
- Modify: `src/Moment.Infrastructure/Data/SqliteTimelineQuery.cs`
- Modify: `tests/Moment.Infrastructure.Tests/Data/SqliteReminderRepositoryTests.cs`
- Modify: `tests/Moment.Infrastructure.Tests/Data/SqliteTodoRepositoryTests.cs`
- Modify: `tests/Moment.App.Tests/Data/SqliteTimelineQueryTests.cs`

- [ ] Add failing tests for schema-v3-to-v4 migration, repeat migration, single occurrence deletion, todo deletion, this-and-future recurrence deletion, idempotence, retained handled history, and exclusion from scheduler/timeline reads.
- [ ] Run the focused infrastructure/query tests and confirm failure.
- [ ] Add nullable `deleted_at` to `occurrences` and `todos` in one transactional schema-v4 migration; add indexes that preserve scheduler and analytics query performance.
- [ ] Replace hard deletes with parameterized updates setting `deleted_at` once. For this-and-future, mark matching generated occurrences and prevent future generation through the existing recurrence edit/delete semantics.
- [ ] Add `deleted_at IS NULL` to operational scheduler, action, todo, conversion-source, and timeline queries. Leave explicit analytics-history APIs able to include deleted rows.
- [ ] Run focused tests plus recovery scheduler tests to prove deleted rows cannot fire or recover.
- [ ] Commit with `git add src/Moment.Core/Abstractions src/Moment.Infrastructure/Data tests && git commit -m "feat: retain deleted item history"`.

## Task 2: Build the immutable analytics read model

**Files:**
- Create: `src/Moment.Core/Analytics/AnalyticsModels.cs`
- Create: `src/Moment.Core/Analytics/IAnalyticsQuery.cs`
- Create: `src/Moment.Core/Analytics/AnalyticsQueryService.cs`
- Create: `src/Moment.Infrastructure/Data/SqliteAnalyticsQuery.cs`
- Create: `tests/Moment.Core.Tests/Analytics/AnalyticsQueryServiceTests.cs`
- Create: `tests/Moment.Infrastructure.Tests/Data/SqliteAnalyticsQueryTests.cs`

- [ ] Add failing tests for inclusive endpoints, time zones, DST, leap dates, Windows first-day-of-week, completed-at counting, future 14-day plans, dated-todo overdue, persisted reminder `Missed`, undated totals, recurring occurrence counts, importance/type/status distributions, deleted appendix, and adaptive daily/weekly/monthly buckets.
- [ ] Define `LocalDateRange(DateOnly Start, DateOnly End)`, `AnalyticsSnapshot(Guid SnapshotId, DateTimeOffset GeneratedAt, LocalDateRange Range, string TimeZoneId, AnalyticsTotals Totals, IReadOnlyList<DistributionSlice> Status, IReadOnlyList<DistributionSlice> ItemTypes, IReadOnlyList<DistributionSlice> Importance, IReadOnlyList<TrendBucket> Trend, IReadOnlyList<AnalyticsDetailRow> Details)`.
- [ ] Define `IAnalyticsQuery.ReadAsync(DateTimeOffset utcStartInclusive, DateTimeOffset utcEndExclusive, bool includeDeleted, CancellationToken ct)` returning raw typed history rows; keep calculations out of SQL except range filtering.
- [ ] Implement `AnalyticsQueryService.CreateSnapshotAsync(LocalDateRange range, TimeZoneInfo zone, CancellationToken ct)`. Validate `Start <= End`, convert local midnight boundaries once, assign one snapshot id, and apply the approved counting rules.
- [ ] Run both focused suites and confirm deterministic totals and bucket labels.
- [ ] Commit with `git add src/Moment.Core/Analytics src/Moment.Infrastructure/Data/SqliteAnalyticsQuery.cs tests/Moment.Core.Tests/Analytics tests/Moment.Infrastructure.Tests/Data/SqliteAnalyticsQueryTests.cs && git commit -m "feat: add unified analytics snapshots"`.

## Task 3: Add day/week/month timeline filters and summary cards

**Files:**
- Create: `src/Moment.Core/Services/TimelinePeriod.cs`
- Modify: `src/Moment.Core/Services/ITimelineQuery.cs`
- Modify: `src/Moment.Infrastructure/Data/SqliteTimelineQuery.cs`
- Modify: `src/Moment.App/Timeline/TimelineViewModel.cs`
- Modify: `src/Moment.App/Timeline/TimelineView.xaml`
- Modify: `tests/Moment.App.Tests/Data/SqliteTimelineQueryTests.cs`
- Modify: `tests/Moment.App.Tests/Timeline/TimelineViewModelTests.cs`
- Modify: `tests/Moment.App.Tests/Timeline/TimelineViewTests.cs`

- [ ] Add failing tests for default day, previous/next day/week/month, culture-specific week start, month edges, visible period labels, both item types in filters, overdue todos pinned regardless of period, past-seven completed, future-fourteen planned, and card navigation into analytics.
- [ ] Define `TimelinePeriodKind { Day, Week, Month }` and `TimelinePeriod(LocalDateRange Range, string Label)`; calculate it from selected date and active `CultureInfo.DateTimeFormat.FirstDayOfWeek`.
- [ ] Change the timeline query to accept `LocalDateRange`; include overdue pending todos in addition to in-range items and never add a year option.
- [ ] Add `SelectedPeriodKind`, `PreviousPeriodCommand`, `NextPeriodCommand`, `PastSevenDaysCompleted`, `NextFourteenDaysPlanned`, and analytics navigation commands to the view model.
- [ ] Bind keyboard-accessible `日 / 周 / 月` selectors and summary cards above the split sections. Preserve the existing next-reminder indicator separately.
- [ ] Run focused tests and confirm locale/week boundary behavior.
- [ ] Commit with `git add src/Moment.Core/Services src/Moment.Infrastructure/Data/SqliteTimelineQuery.cs src/Moment.App/Timeline tests/Moment.App.Tests && git commit -m "feat: filter timeline and show planning summaries"`.

## Task 4: Implement renderer-neutral charts and analytics window

**Files:**
- Create: `src/Moment.Core/Analytics/ChartGeometry.cs`
- Create: `src/Moment.Core/Analytics/ChartGeometryBuilder.cs`
- Create: `src/Moment.App/Analytics/AnalyticsViewModel.cs`
- Create: `src/Moment.App/Analytics/AnalyticsWindow.xaml`
- Create: `src/Moment.App/Analytics/AnalyticsWindow.xaml.cs`
- Create: `src/Moment.App/Analytics/DonutChartControl.cs`
- Create: `src/Moment.App/Analytics/TrendChartControl.cs`
- Modify: `src/Moment.App/Styles/Colors.xaml`
- Modify: `src/Moment.App/Styles/HighContrastPalette.cs`
- Modify: `src/Moment.App/CompositionRoot.cs`
- Create: `tests/Moment.Core.Tests/Analytics/ChartGeometryBuilderTests.cs`
- Create: `tests/Moment.App.Tests/Analytics/AnalyticsViewModelTests.cs`
- Create: `tests/Moment.App.Tests/Analytics/AnalyticsWindowTests.cs`

- [ ] Add failing tests for sector angles summing to 360°, zero-data geometry, stable trend coordinates, status/type/importance switching, 7/30/current-month/year/custom ranges, invalid custom ranges, stale-load cancellation, accessible summaries, and high-contrast resources.
- [ ] Implement immutable `DonutGeometry` and `TrendGeometry` using normalized coordinates and semantic color keys, not WPF brushes. Handle all-zero values explicitly.
- [ ] Implement `AnalyticsViewModel` so loading creates one snapshot then derives cards and geometry; generation changes cancel and discard stale results while leaving the last valid snapshot visible.
- [ ] Build the approved layout: three KPI cards, donut with dimension selector, completion bars, textual chart summaries, keyboard focus, empty state, and range picker. Use WPF `DrawingContext` only for rendering.
- [ ] Add one reusable `分析报告` navigation entry and summary-card deep links in composition.
- [ ] Run focused Core/App analytics tests.
- [ ] Commit with `git add src/Moment.Core/Analytics src/Moment.App/Analytics src/Moment.App/Styles src/Moment.App/CompositionRoot.cs tests/Moment.Core.Tests/Analytics tests/Moment.App.Tests/Analytics && git commit -m "feat: add accessible analytics dashboard"`.

## Task 5: Export UTF-8 CSV in full and anonymous modes

**Files:**
- Create: `src/Moment.Core/Reporting/ReportPrivacyMode.cs`
- Create: `src/Moment.Core/Reporting/CsvReportExporter.cs`
- Create: `tests/Moment.Core.Tests/Reporting/CsvReportExporterTests.cs`

- [ ] Add failing byte-level tests for UTF-8 BOM, comma/quote/newline escaping, invariant date/time fields, full columns including title and stable id, anonymous omission of both fields, deleted rows, empty data, cancellation, and an unchanged pre-existing destination.
- [ ] Define `ReportPrivacyMode { Full, Anonymous }` and `CsvReportExporter.WriteAsync(AnalyticsSnapshot snapshot, ReportPrivacyMode privacy, Stream destination, CancellationToken ct)`.
- [ ] Serialize directly from `snapshot.Details`; use RFC-style double-quote escaping and `\r\n`. Write BOM once. Never query data or calculate metrics inside the exporter.
- [ ] Run `dotnet test tests/Moment.Core.Tests/Moment.Core.Tests.csproj --filter "FullyQualifiedName~CsvReportExporterTests"`.
- [ ] Commit with `git add src/Moment.Core/Reporting tests/Moment.Core.Tests/Reporting && git commit -m "feat: export analytics details as CSV"`.

## Task 6: Export matching native PDF reports

**Files:**
- Modify: `src/Moment.App/Moment.App.csproj`
- Create: `src/Moment.App/Reporting/PdfReportExporter.cs`
- Create: `src/Moment.App/Reporting/ChineseFontResolver.cs`
- Create: `tests/Moment.App.Tests/Reporting/PdfReportExporterTests.cs`

- [ ] Add failing tests that inspect produced PDFs for product/version, generation timestamp, range/time zone, KPI values, chart labels, metric definitions, snapshot id metadata, Chinese text, full detail/deleted appendix, anonymous omission, and empty snapshot behavior.
- [ ] Add an exact `PDFsharp-WPF` package reference at version `6.2.4`; do not add a second chart/statistics dependency.
- [ ] Implement a deterministic Chinese font resolver using installed Windows CJK fonts with an explicit fallback/error. Consume `DonutGeometry` and `TrendGeometry` from Task 4 to draw charts.
- [ ] Implement `PdfReportExporter.WriteAsync(AnalyticsSnapshot snapshot, ReportPrivacyMode privacy, Stream destination, ProductMetadata metadata, CancellationToken ct)`. Keep query logic out of the renderer.
- [ ] Run `dotnet test tests/Moment.App.Tests/Moment.App.Tests.csproj --filter "FullyQualifiedName~PdfReportExporterTests"` and inspect one rendered Chinese fixture manually.
- [ ] Commit with `git add src/Moment.App/Moment.App.csproj src/Moment.App/Reporting tests/Moment.App.Tests/Reporting && git commit -m "feat: export analytics summary as PDF"`.

## Task 7: Orchestrate safe paired export from the UI

**Files:**
- Create: `src/Moment.App/Reporting/IReportSaveDialog.cs`
- Create: `src/Moment.App/Reporting/ReportExportService.cs`
- Create: `src/Moment.App/Reporting/ReportExportDialog.xaml`
- Create: `src/Moment.App/Reporting/ReportExportDialog.xaml.cs`
- Modify: `src/Moment.App/Analytics/AnalyticsViewModel.cs`
- Modify: `src/Moment.App/CompositionRoot.cs`
- Create: `tests/Moment.App.Tests/Reporting/ReportExportServiceTests.cs`
- Modify: `tests/Moment.App.Tests/Analytics/AnalyticsViewModelTests.cs`

- [ ] Add failing tests for mandatory privacy choice, cancelled dialog creating nothing, one shared snapshot for both formats, safe `.pdf`/`.csv` base naming, refusal to overwrite without dialog confirmation, partial failure cleanup limited to files created by the attempt, success paths, and open-folder action without auto-launch.
- [ ] Implement `ReportExportRequest(AnalyticsSnapshot Snapshot, ReportPrivacyMode Privacy, string BasePath)` and write both outputs to unique sibling temporary files before atomically moving them to final paths.
- [ ] Track whether each final file existed before the attempt. On failure remove only new temporary/final files owned by that attempt; never delete a pre-existing file.
- [ ] Add the privacy dialog then Windows save dialog; show both resulting paths and an optional user-invoked `打开所在文件夹`. Do not open files or network connections automatically.
- [ ] Run focused reporting and analytics view-model tests.
- [ ] Commit with `git add src/Moment.App/Reporting src/Moment.App/Analytics/AnalyticsViewModel.cs src/Moment.App/CompositionRoot.cs tests/Moment.App.Tests && git commit -m "feat: export paired private analytics reports"`.

## Task 8: Verify analytics and packaged reports end to end

**Files:**
- Modify: `README.md`
- Modify: `docs/release-checklist.md`
- Modify: `src/Moment.App/Diagnostics/SmokeTestRunner.cs`
- Modify: `tests/Moment.App.Tests/Diagnostics/SmokeTestRunnerTests.cs`
- Modify: `scripts/smoke-test.ps1`

- [ ] Extend smoke fixtures with completed, future, missed, undated, recurring, and deleted records across local date boundaries. Assert the UI, PDF, and CSV expose one snapshot id and identical totals.
- [ ] Add packaged checks for schema-v4 upgrade, chart zero state, Chinese PDF text, CSV BOM, full/anonymous field sets, and no network dependency.
- [ ] Document range/count definitions, privacy modes, output files, and annual-view availability only in analytics/reporting.
- [ ] Run `dotnet test Moment.slnx --configuration Release --no-restore`.
- [ ] Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1` and `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\smoke-test.ps1`.
- [ ] Manually verify Windows 11 scaling, keyboard focus, high contrast, current-culture week start, all ranges, both privacy modes, Chinese PDF/CSV, and explicit open-folder behavior.
- [ ] Commit with `git add README.md docs src/Moment.App/Diagnostics tests/Moment.App.Tests/Diagnostics scripts/smoke-test.ps1 && git commit -m "test: verify analytics and reporting release"`.
