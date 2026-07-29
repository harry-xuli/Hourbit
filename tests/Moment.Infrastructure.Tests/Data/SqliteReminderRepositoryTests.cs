using Microsoft.Data.Sqlite;
using Moment.Core.Domain;
using Moment.Infrastructure.Data;
using Moment.TestSupport;

namespace Moment.Infrastructure.Tests.Data;

public sealed class SqliteReminderRepositoryTests
{
    [Fact]
    public async Task SaveItemWithOccurrence_is_atomic_and_round_trips()
    {
        using var temp = new TempDirectory();
        var repository = await SqliteReminderRepository.OpenAsync(
            Path.Combine(temp.Path, "moment.db"), CancellationToken.None);
        var created = new DateTimeOffset(2026, 7, 29, 9, 0, 0, TimeSpan.FromHours(8));
        var item = ReminderItem.Create("会议", ReminderKind.Plan,
            ReminderImportance.Important, created, created.AddHours(1));
        var occurrence = ReminderOccurrence.Schedule(item.Id, created.AddHours(1));

        await repository.SaveItemWithOccurrenceAsync(item, occurrence, CancellationToken.None);

        var scheduled = await repository.GetScheduledAsync(CancellationToken.None);
        Assert.Single(scheduled);
        Assert.Equal(item.Id, scheduled[0].Occurrence.ItemId);
    }

    [Fact]
    public async Task EditAsync_with_occurrence_only_splits_the_selected_occurrence_from_the_series()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var repository = await SqliteReminderRepository.OpenAsync(path, CancellationToken.None);
        var due = new DateTimeOffset(2026, 7, 30, 9, 0, 0, TimeSpan.FromHours(8));
        var item = new ReminderItem(Guid.NewGuid(), "原计划", ReminderKind.Plan, ReminderImportance.Normal,
            due.AddDays(-1), RecurrenceRule.Daily(TimeOnly.FromDateTime(due.DateTime)));
        var selected = ReminderOccurrence.Schedule(item.Id, due);
        var later = ReminderOccurrence.Schedule(item.Id, due.AddDays(1));
        await repository.SaveItemWithOccurrenceAsync(item, selected, CancellationToken.None);
        await repository.SaveOccurrenceAsync(later, CancellationToken.None);
        var editedItem = item with
        {
            Title = "编辑后的单次计划",
            Kind = ReminderKind.Alarm,
            Importance = ReminderImportance.Important
        };
        var editedOccurrence = selected with { DueAt = due.AddHours(2) };

        await repository.EditAsync(selected.Id, editedItem, editedOccurrence, SeriesScope.OccurrenceOnly, CancellationToken.None);

        var storedItem = await repository.GetItemAsync(item.Id, CancellationToken.None);
        var storedSelected = await repository.GetScheduledReminderAsync(selected.Id, CancellationToken.None);
        var storedLater = await repository.GetScheduledReminderAsync(later.Id, CancellationToken.None);
        Assert.Equal("原计划", storedItem!.Title);
        Assert.Equal(due.AddHours(2), storedSelected!.Occurrence.DueAt);
        Assert.Equal("编辑后的单次计划", storedSelected.Item.Title);
        Assert.Equal(ReminderKind.Alarm, storedSelected.Item.Kind);
        Assert.Equal(ReminderImportance.Important, storedSelected.Item.Importance);
        Assert.NotEqual(item.Id, storedSelected.Item.Id);
        Assert.Equal(due.AddDays(1), storedLater!.Occurrence.DueAt);
        Assert.Equal(item.Id, storedLater.Item.Id);
    }

    [Fact]
    public async Task DeleteAsync_with_this_and_future_keeps_completed_history()
    {
        using var temp = new TempDirectory();
        var repository = await SqliteReminderRepository.OpenAsync(Path.Combine(temp.Path, "moment.db"), CancellationToken.None);
        var selectedDue = new DateTimeOffset(2026, 7, 30, 9, 0, 0, TimeSpan.FromHours(8));
        var item = new ReminderItem(Guid.NewGuid(), "每日计划", ReminderKind.Plan, ReminderImportance.Normal,
            selectedDue.AddDays(-2), RecurrenceRule.Daily(TimeOnly.FromDateTime(selectedDue.DateTime)));
        var past = new ReminderOccurrence(Guid.NewGuid(), item.Id, selectedDue.AddDays(-1), OccurrenceState.Completed,
            selectedDue.AddDays(-1).AddMinutes(1), null);
        var selected = ReminderOccurrence.Schedule(item.Id, selectedDue);
        var later = ReminderOccurrence.Schedule(item.Id, selectedDue.AddDays(1));
        await repository.SaveItemWithOccurrenceAsync(item, past, CancellationToken.None);
        await repository.SaveOccurrenceAsync(selected, CancellationToken.None);
        await repository.SaveOccurrenceAsync(later, CancellationToken.None);

        await repository.DeleteAsync(selected.Id, SeriesScope.ThisAndFuture, CancellationToken.None);

        Assert.Empty(await repository.GetScheduledAsync(CancellationToken.None));
        var history = await repository.GetScheduledReminderAsync(past.Id, CancellationToken.None);
        var storedItem = await repository.GetItemAsync(item.Id, CancellationToken.None);
        Assert.Equal(OccurrenceState.Completed, history!.Occurrence.State);
        Assert.Null(storedItem!.Recurrence);
    }

    [Fact]
    public async Task EditAsync_with_this_and_future_splits_history_and_removes_stale_future_rows()
    {
        using var temp = new TempDirectory();
        var repository = await SqliteReminderRepository.OpenAsync(Path.Combine(temp.Path, "moment.db"), CancellationToken.None);
        var due = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.FromHours(8));
        var item = new ReminderItem(Guid.NewGuid(), "旧系列", ReminderKind.Plan, ReminderImportance.Normal,
            due.AddDays(-2), RecurrenceRule.Daily(TimeOnly.FromDateTime(due.DateTime)));
        var past = new ReminderOccurrence(Guid.NewGuid(), item.Id, due.AddDays(-1), OccurrenceState.Completed, due.AddDays(-1), null);
        var selected = ReminderOccurrence.Schedule(item.Id, due);
        var later = ReminderOccurrence.Schedule(item.Id, due.AddDays(1));
        await repository.SaveItemWithOccurrenceAsync(item, past, CancellationToken.None);
        await repository.SaveOccurrenceAsync(selected, CancellationToken.None);
        await repository.SaveOccurrenceAsync(later, CancellationToken.None);
        var replacementItem = item with { Title = "新系列", Recurrence = RecurrenceRule.Weekdays(new TimeOnly(10, 0)) };
        var replacementOccurrence = selected with { DueAt = due.AddHours(1) };

        await repository.EditAsync(selected.Id, replacementItem, replacementOccurrence, SeriesScope.ThisAndFuture, CancellationToken.None);

        var history = await repository.GetScheduledReminderAsync(past.Id, CancellationToken.None);
        var scheduled = await repository.GetScheduledAsync(CancellationToken.None);
        Assert.Equal("旧系列", history!.Item.Title);
        Assert.Equal(item.Id, history.Item.Id);
        var current = Assert.Single(scheduled);
        Assert.Equal("新系列", current.Item.Title);
        Assert.NotEqual(item.Id, current.Item.Id);
        Assert.Equal(due.AddHours(1), current.Occurrence.DueAt);
        Assert.Equal(RecurrenceKind.Weekdays, current.Item.Recurrence!.Kind);
        Assert.Null(await repository.GetScheduledReminderAsync(later.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ApplyActionAsync_is_idempotent_and_does_not_insert_a_second_next_occurrence()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var repository = await SqliteReminderRepository.OpenAsync(path, CancellationToken.None);
        var due = new DateTimeOffset(2026, 8, 2, 9, 0, 0, TimeSpan.FromHours(8));
        var item = ReminderItem.Create("行动", ReminderKind.Plan, ReminderImportance.Normal, due.AddHours(-1), due);
        var current = ReminderOccurrence.Schedule(item.Id, due);
        var firstNext = ReminderOccurrence.Schedule(item.Id, due.AddDays(1));
        var secondNext = ReminderOccurrence.Schedule(item.Id, due.AddDays(2));
        await repository.SaveItemWithOccurrenceAsync(item, current, CancellationToken.None);

        await repository.ApplyActionAsync(current.Id, OccurrenceState.Completed, due.AddMinutes(1), firstNext, CancellationToken.None);
        await repository.ApplyActionAsync(current.Id, OccurrenceState.Ignored, due.AddMinutes(2), secondNext, CancellationToken.None);

        var storedCurrent = await repository.GetScheduledReminderAsync(current.Id, CancellationToken.None);
        Assert.Equal(OccurrenceState.Completed, storedCurrent!.Occurrence.State);
        Assert.NotNull(await repository.GetScheduledReminderAsync(firstNext.Id, CancellationToken.None));
        Assert.Null(await repository.GetScheduledReminderAsync(secondNext.Id, CancellationToken.None));
        await using var connection = await DatabaseMigrator.OpenConnectionAsync(path, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM action_log WHERE occurrence_id = $id;";
        command.Parameters.AddWithValue("$id", current.Id.ToString("D"));
        Assert.Equal(1L, await command.ExecuteScalarAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ApplyActionAsync_rolls_back_the_state_change_when_next_occurrence_breaks_a_constraint()
    {
        using var temp = new TempDirectory();
        var repository = await SqliteReminderRepository.OpenAsync(Path.Combine(temp.Path, "moment.db"), CancellationToken.None);
        var due = new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.FromHours(8));
        var item = ReminderItem.Create("原子动作", ReminderKind.Plan, ReminderImportance.Normal, due.AddHours(-1), due);
        var current = ReminderOccurrence.Schedule(item.Id, due);
        var duplicateDue = ReminderOccurrence.Schedule(item.Id, due.AddDays(1));
        await repository.SaveItemWithOccurrenceAsync(item, current, CancellationToken.None);
        await repository.SaveOccurrenceAsync(duplicateDue, CancellationToken.None);

        await Assert.ThrowsAsync<SqliteException>(() => repository.ApplyActionAsync(current.Id, OccurrenceState.Completed,
            due.AddMinutes(1), ReminderOccurrence.Schedule(item.Id, duplicateDue.DueAt), CancellationToken.None));

        var storedCurrent = await repository.GetScheduledReminderAsync(current.Id, CancellationToken.None);
        Assert.Equal(OccurrenceState.Scheduled, storedCurrent!.Occurrence.State);
    }

    [Fact]
    public async Task SaveOccurrenceAsync_rejects_the_same_instant_with_a_different_offset_and_preserves_the_original_offset()
    {
        using var temp = new TempDirectory();
        var repository = await SqliteReminderRepository.OpenAsync(Path.Combine(temp.Path, "moment.db"), CancellationToken.None);
        var due = new DateTimeOffset(2026, 8, 4, 9, 0, 0, TimeSpan.FromHours(8));
        var item = ReminderItem.Create("时区", ReminderKind.Plan, ReminderImportance.Normal, due.AddHours(-1), due);
        var first = ReminderOccurrence.Schedule(item.Id, due);
        await repository.SaveItemWithOccurrenceAsync(item, first, CancellationToken.None);

        await Assert.ThrowsAsync<SqliteException>(() => repository.SaveOccurrenceAsync(
            ReminderOccurrence.Schedule(item.Id, due.ToOffset(TimeSpan.Zero)), CancellationToken.None));

        var stored = await repository.GetScheduledReminderAsync(first.Id, CancellationToken.None);
        Assert.Equal(TimeSpan.FromHours(8), stored!.Occurrence.DueAt.Offset);
    }

    [Fact]
    public async Task GetScheduledAsync_orders_occurrences_by_utc_instant_when_offsets_differ()
    {
        using var temp = new TempDirectory();
        var repository = await SqliteReminderRepository.OpenAsync(Path.Combine(temp.Path, "moment.db"), CancellationToken.None);
        var firstDue = new DateTimeOffset(2026, 8, 8, 9, 0, 0, TimeSpan.FromHours(8));
        var secondDue = new DateTimeOffset(2026, 8, 8, 2, 0, 0, TimeSpan.Zero);
        var item = ReminderItem.Create("排序", ReminderKind.Plan, ReminderImportance.Normal, firstDue.AddHours(-1), firstDue);
        var first = ReminderOccurrence.Schedule(item.Id, firstDue);
        var second = ReminderOccurrence.Schedule(item.Id, secondDue);
        await repository.SaveItemWithOccurrenceAsync(item, first, CancellationToken.None);
        await repository.SaveOccurrenceAsync(second, CancellationToken.None);

        var scheduled = await repository.GetScheduledAsync(CancellationToken.None);

        Assert.Collection(scheduled,
            reminder => Assert.Equal(first.Id, reminder.Occurrence.Id),
            reminder => Assert.Equal(second.Id, reminder.Occurrence.Id));
    }

    [Fact]
    public async Task OpenAsync_migrates_the_same_database_without_losing_saved_data()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moment.db");
        var first = await SqliteReminderRepository.OpenAsync(path, CancellationToken.None);
        var due = new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.FromHours(8));
        var item = ReminderItem.Create("迁移", ReminderKind.Plan, ReminderImportance.Normal, due.AddHours(-1), due);
        var occurrence = ReminderOccurrence.Schedule(item.Id, due);
        await first.SaveItemWithOccurrenceAsync(item, occurrence, CancellationToken.None);

        var reopened = await SqliteReminderRepository.OpenAsync(path, CancellationToken.None);

        Assert.NotNull(await reopened.GetScheduledReminderAsync(occurrence.Id, CancellationToken.None));
    }
}
