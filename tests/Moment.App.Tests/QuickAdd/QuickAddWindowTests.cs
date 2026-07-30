using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Moment.App.QuickAdd;
using Moment.Core.Abstractions;
using Moment.Core.Domain;
using Moment.Core.Parsing;
using Moment.Core.Services;
using Moment.TestSupport;

namespace Moment.App.Tests.QuickAdd;

public sealed class QuickAddWindowTests
{
    [Fact]
    public Task First_Tab_expands_details_then_subsequent_focus_traversal_moves_to_next_field() =>
        WpfTestHost.RunAsync(() =>
        {
            var vm = Create(new ParseResult.Success(
                TestData.Draft("看书", "2026-07-30T09:00:00+08:00")));
            vm.Text = "明早9点看书";
            var window = new QuickAddWindow { DataContext = vm };
            window.Show();
            window.Activate();
            window.UpdateLayout();
            var input = Assert.IsType<TextBox>(window.FindName("InputBox"));
            var title = Assert.IsType<TextBox>(window.FindName("DetailTitleBox"));
            var date = Assert.IsType<TextBox>(window.FindName("DetailDateBox"));
            Assert.True(input.Focus());

            Assert.True(window.TryExpandDetailsFromTab());

            Assert.True(vm.AreDetailsVisible);
            Assert.Same(title, Keyboard.FocusedElement);
            Assert.False(window.TryExpandDetailsFromTab());
            Assert.True(title.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
            Assert.Same(date, Keyboard.FocusedElement);
            window.Close();
        });

    [Fact]
    public Task Ambiguity_choice_is_reachable_from_sentence_input_by_normal_focus_traversal() =>
        WpfTestHost.RunAsync(() =>
        {
            var vm = Create(new ParseResult.Ambiguous(
                "晚上提醒我看书",
                [new("今天 20:00", TestData.Draft("看书", "2026-07-30T20:00:00+08:00"))]));
            vm.Text = "晚上提醒我看书";
            var window = new QuickAddWindow { DataContext = vm };
            window.Show();
            window.Activate();
            window.UpdateLayout();
            var input = Assert.IsType<TextBox>(window.FindName("InputBox"));
            Assert.True(input.Focus());

            Assert.False(window.TryExpandDetailsFromTab());
            Assert.True(input.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));

            for (var remainingMoves = 3;
                 Keyboard.FocusedElement is not Button && remainingMoves > 0;
                 remainingMoves--)
            {
                Assert.True(((UIElement)Keyboard.FocusedElement)
                    .MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
            }
            var choice = Assert.IsType<Button>(Keyboard.FocusedElement);
            Assert.Equal("今天 20:00", choice.Content);
            window.Close();
        });

    [Fact]
    public Task Expanded_details_remain_scrollable_in_a_200_percent_equivalent_logical_viewport() =>
        WpfTestHost.RunAsync(() =>
        {
            var vm = Create(new ParseResult.Success(
                TestData.Draft("看书", "2026-07-30T09:00:00+08:00")));
            vm.Text = "明早9点看书";
            var window = new QuickAddWindow
            {
                DataContext = vm,
                SizeToContent = SizeToContent.Manual,
                Height = 360
            };
            window.Show();
            window.Activate();
            window.UpdateLayout();
            var input = Assert.IsType<TextBox>(window.FindName("InputBox"));
            Assert.True(input.Focus());
            Assert.True(window.TryExpandDetailsFromTab());
            window.UpdateLayout();

            var scroller = Assert.IsType<ScrollViewer>(
                window.FindName("QuickAddScrollViewer"));
            Assert.True(scroller.ScrollableHeight > 0);
            window.Close();
        });

    private static QuickAddViewModel Create(ParseResult result) =>
        new(new StubParser(result), new ReminderServiceStub(),
            new FakeClock("2026-07-29T09:00:00+08:00"),
            TimeZoneInfo.CreateCustomTimeZone(
                "UTC+08-window", TimeSpan.FromHours(8), "UTC+08", "UTC+08"));

    private sealed class StubParser(ParseResult result) : IChineseTimeParser
    {
        public ParseResult Parse(string text, DateTimeOffset now, TimeZoneInfo zone) => result;
    }

    private sealed class ReminderServiceStub : IReminderService
    {
        public Task<ReminderOccurrence> CreateAsync(ReminderDraft draft, CancellationToken ct) =>
            Task.FromResult(ReminderOccurrence.Schedule(Guid.NewGuid(), draft.DueAt));
        public Task EditAsync(Guid occurrenceId, ReminderDraft draft, SeriesScope scope, CancellationToken ct) =>
            Task.CompletedTask;
        public Task DeleteAsync(Guid occurrenceId, SeriesScope scope, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
