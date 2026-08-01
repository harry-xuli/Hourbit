using Moment.Core.Domain;

namespace Moment.Core.Tests.Domain;

public sealed class TodoItemTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 1, 9, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Constructor_normalizes_title_and_preserves_an_optional_due_date()
    {
        var dated = new TodoItem(
            Guid.NewGuid(), "  提交报告  ", CreatedAt,
            new DateOnly(2026, 8, 5), ReminderImportance.Important,
            false, null);
        var undated = new TodoItem(
            Guid.NewGuid(), "整理桌面", CreatedAt, null,
            ReminderImportance.Normal, false, null);

        Assert.Equal("提交报告", dated.Title);
        Assert.Equal(new DateOnly(2026, 8, 5), dated.DueDate);
        Assert.Null(undated.DueDate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_rejects_an_empty_normalized_title(string title)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TodoItem(
            Guid.NewGuid(), title, CreatedAt, null,
            ReminderImportance.Normal, false, null));
    }

    [Fact]
    public void Constructor_rejects_a_title_longer_than_two_hundred_characters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TodoItem(
            Guid.NewGuid(), new string('a', 201), CreatedAt, null,
            ReminderImportance.Normal, false, null));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Constructor_requires_completion_state_and_timestamp_to_agree(
        bool isCompleted,
        bool hasCompletedAt)
    {
        DateTimeOffset? completedAt =
            hasCompletedAt ? CreatedAt.AddMinutes(5) : null;

        Assert.Throws<ArgumentException>(() => new TodoItem(
            Guid.NewGuid(), "一致性", CreatedAt, null,
            ReminderImportance.Normal, isCompleted, completedAt));
    }

    [Fact]
    public void Constructor_rejects_completion_before_creation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TodoItem(
            Guid.NewGuid(), "时间顺序", CreatedAt, null,
            ReminderImportance.Normal, true, CreatedAt.AddTicks(-1)));
    }
}
