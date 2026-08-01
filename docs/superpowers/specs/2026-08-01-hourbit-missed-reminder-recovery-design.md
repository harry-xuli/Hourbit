# Hourbit Missed Reminder Recovery Design

**Date:** 2026-08-01  
**Target release:** 0.2.0

## Problem

A normal reminder due at 19:00 remained under `接下来` with status `等待中` when
the user returned at 20:04. The system time had advanced, but the visible
timeline retained its pre-due snapshot.

The current resume callback calls `scheduler.Refresh()` and immediately starts
`timeline.LoadAsync()`. Refresh is only a signal; it does not await the scheduler
state transition. The timeline can therefore reload before the occurrence is
updated. There is also no subsequent scheduler-to-timeline change notification.

The existing five-minute `RecoveryClassifier` is tested but is not connected to
startup or resume processing. Consequently old normal reminders are delivered
through the ordinary fired path rather than being atomically persisted as
missed and summarized.

## Required Behavior

- A normal reminder no more than five minutes late is delivered immediately and
  becomes `Fired` / `等待处理`.
- A normal reminder more than five minutes late becomes `Missed` / `已错过` and
  is included in one recovery summary notification.
- An important reminder is delivered through the important-alert path no matter
  how late it is and remains actionable until the user handles it.
- A normal reminder delivered during ordinary operation becomes `Missed` when it
  remains unhandled for more than five minutes.
- Startup, power resume, unlock, system-time change, and time-zone change finish
  recovery before reloading the timeline.
- Every successful scheduler state transition eventually refreshes the visible
  timeline without requiring the user to restart the app.

The five-minute boundary is inclusive for immediate delivery: exactly five
minutes late is immediate; five minutes plus any positive duration is missed.

## Architecture

`ReminderRecoveryService` owns recovery orchestration. It depends on the
repository, `RecoveryClassifier`, reminder sink, clock, scheduler lifecycle,
and a refresh callback. It is the only startup/resume path that classifies
overdue scheduled reminders.

Recovery performs these ordered steps:

1. serialize against another recovery and pause the normal scheduler loop;
2. query due `Scheduled` occurrences and actionable `Fired` occurrences;
3. classify by importance and lateness;
4. use compare-and-set repository operations to claim each state transition;
5. deliver immediate/important alerts and one summary for newly missed normal
   reminders;
6. restart and signal the scheduler;
7. await a UI-dispatcher timeline reload.

If recovery is triggered repeatedly by clustered resume/unlock events, only the
first successful compare-and-set can deliver or summarize each occurrence.

## Ordinary Runtime Grace Period

The scheduler continues to deliver a due reminder exactly once. After a normal
occurrence becomes `Fired`, the scheduler also tracks its five-minute grace
deadline. If it is still actionable after that deadline, a compare-and-set
transition changes it to `Missed`. Completion, ignore, snooze, deletion, or
conversion before the deadline prevents that transition.

Important reminders do not automatically become missed. Their existing
persistent important-alert behavior remains unchanged.

## Repository Operations

Repository APIs add atomic transitions for:

- `Scheduled -> Fired`;
- `Scheduled -> Missed`;
- `Fired -> Missed` for normal reminders after the grace boundary.

Each operation includes the expected source state and returns whether this
caller won the transition. `handled_at` records the transition time. Queries for
recovery distinguish scheduled-due and fired-unhandled rows and exclude deleted
records once analytics schema version 4 is present.

## UI Refresh Contract

The scheduler exposes a state-changed signal after a committed transition. The
application marshals it to the WPF Dispatcher and coalesces concurrent refresh
requests. Recovery itself awaits its final refresh instead of signaling and
racing it.

`TimelineItemViewModel` continues to derive visible groups from persisted state;
it does not repair database state in the view layer. A stale `Scheduled` row may
be displayed as overdue defensively, but recovery must persist `Missed` so the
state survives restart and is available to analytics.

## Failure Handling

- One delivery failure is reported without stopping recovery of other reminders.
- A failed summary notification does not roll back already persisted missed
  states; it is reported through the runtime error channel.
- Scheduler restart occurs in a `finally` path unless application shutdown has
  cancelled the lifetime token.
- Timeline refresh failure is surfaced but does not undo reminder states.
- Disposal waits for an admitted recovery and prevents new recoveries.

## Testing and Acceptance

Automated tests cover:

- the reported 19:00 to 20:04 normal-reminder scenario;
- 4:59, exactly 5:00, and 5:00 plus one tick lateness boundaries;
- important reminders that are hours late;
- ordinary `Fired -> Missed` transition after five unhandled minutes;
- completion, ignore, snooze, and deletion before the grace deadline;
- concurrent resume/unlock/time-change events and duplicate-delivery prevention;
- recovery ordering that proves timeline reload occurs after persistence;
- scheduler state-change refresh coalescing and dispatcher affinity;
- startup recovery, sleep/resume, and restart smoke paths;
- notification failure, cancellation, and disposal races.

Manual Windows 11 acceptance creates a normal reminder, leaves or suspends the
machine past the grace period, returns, and verifies that the reminder appears
under `已错过` without a restart. A second recovery signal must not duplicate the
summary notification.

## Out of Scope

- changing the five-minute threshold through settings;
- treating important reminders as missed automatically;
- retroactively reconstructing reminders that users permanently deleted before
  soft-delete support existed.
