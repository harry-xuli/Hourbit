using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Moment.App.QuickAdd;
using Moment.App.Styles;
using Moment.Core.Abstractions;
using Moment.Core.Domain;
using Moment.Core.Parsing;
using Moment.Core.Services;
using Moment.TestSupport;
using System.Globalization;

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
    public Task First_Tab_on_a_todo_opens_the_todo_fields_and_focuses_its_title() =>
        WpfTestHost.RunAsync(() =>
        {
            var vm = Create(new ParseResult.Success(
                new TodoDraft("提交报告", new DateOnly(2026, 8, 5), ReminderImportance.Normal)));
            vm.Text = "8月5日提交报告";
            var window = new QuickAddWindow { DataContext = vm };
            window.Show();
            window.Activate();
            window.UpdateLayout();
            var input = Assert.IsType<TextBox>(window.FindName("InputBox"));
            var todoTitle = Assert.IsType<TextBox>(window.FindName("TodoDetailTitleBox"));
            Assert.True(input.Focus());

            Assert.True(window.TryExpandDetailsFromTab());

            Assert.True(vm.IsTodoDetailsVisible);
            Assert.False(vm.IsReminderDetailsVisible);
            Assert.Same(todoTitle, Keyboard.FocusedElement);
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

    [Fact]
    public Task Simulated_dark_system_palette_reaches_quick_add_surfaces() =>
        WpfTestHost.RunAsync(() =>
        {
            var window = new QuickAddWindow
            {
                DataContext = CreateWithoutTestSupport()
            };
            ApplyDarkPalette(window);
            HighContrastPalette.Apply(window.Resources, true, window.FindResource);
            window.Show();
            window.UpdateLayout();

            AssertBrush(Colors.Black, window.Background);
            AssertBrush(Colors.White, window.Foreground);
            var footer = Assert.IsType<Border>(
                window.FindName("QuickAddFooter"));
            AssertBrush(Colors.DarkSlateGray, footer.Background);
            var footerText = Assert.IsType<TextBlock>(
                window.FindName("QuickAddFooterText"));
            AssertBrush(Colors.White, footerText.Foreground);
            Assert.True(ContrastRatio(
                ((SolidColorBrush)footerText.Foreground).Color,
                ((SolidColorBrush)footer.Background).Color) >= 4.5);
            window.Close();
        });

    [Fact]
    public async Task Enter_submits_from_input_and_Escape_binding_hides_without_clearing()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var vm = CreateWithoutTestSupport();
            vm.Text = "明早九点看书";
            var window = new QuickAddWindow { DataContext = vm };
            window.Show();
            window.Activate();
            window.UpdateLayout();
            var input = Assert.IsType<TextBox>(
                window.FindName("InputBox"));
            Assert.True(input.Focus());

            Assert.True(await window.TrySubmitFromEnterAsync());
            Assert.False(window.IsVisible);
            Assert.Equal("明早九点看书", vm.Text);

            vm.Text = "不要清空";
            window.Show();
            window.Activate();
            var escape = Assert.Single(
                window.InputBindings.OfType<KeyBinding>(),
                binding => binding.Key == Key.Escape);
            escape.Command.Execute(escape.CommandParameter);

            Assert.False(window.IsVisible);
            Assert.Equal("不要清空", vm.Text);
            window.Close();
        });
    }

    private static QuickAddViewModel CreateWithoutTestSupport()
    {
        var due = new DateTimeOffset(
            2026, 7, 30, 9, 0, 0, TimeSpan.FromHours(8));
        var draft = new ReminderDraft(
            "看书", due, ReminderKind.Plan, ReminderImportance.Normal, null);
        return new QuickAddViewModel(
            new StubParser(new ParseResult.Success(draft)),
            new ReminderServiceStub(),
            new TodoServiceStub(),
            new LocalClock(due.AddDays(-1)),
            TimeZoneInfo.CreateCustomTimeZone(
                "UTC+08-hc", TimeSpan.FromHours(8), "UTC+08", "UTC+08"),
            CultureInfo.GetCultureInfo("zh-CN"));
    }

    private static QuickAddViewModel Create(ParseResult result) =>
        new(new StubParser(result), new ReminderServiceStub(), new TodoServiceStub(),
            new FakeClock("2026-07-29T09:00:00+08:00"),
            TimeZoneInfo.CreateCustomTimeZone(
                "UTC+08-window", TimeSpan.FromHours(8), "UTC+08", "UTC+08"),
            CultureInfo.GetCultureInfo("zh-CN"));

    private sealed class StubParser(ParseResult result) : IChineseTimeParser
    {
        public ParseResult Parse(
            string text,
            DateTimeOffset now,
            TimeZoneInfo zone,
            System.Globalization.CultureInfo culture) => result;
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

    private sealed class TodoServiceStub : ITodoService
    {
        public Task<TodoItem> CreateAsync(TodoDraft draft, CancellationToken ct) =>
            Task.FromResult(new TodoItem(
                Guid.NewGuid(), draft.Title, DateTimeOffset.UtcNow,
                draft.DueDate, draft.Importance, false, null));
        public Task EditAsync(Guid todoId, TodoDraft draft, CancellationToken ct) =>
            Task.CompletedTask;
        public Task CompleteAsync(Guid todoId, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(Guid todoId, CancellationToken ct) => Task.CompletedTask;
        public Task ConvertToReminderAsync(
            Guid todoId, ReminderDraft draft, CancellationToken ct) => Task.CompletedTask;
        public Task ConvertToTodoAsync(
            Guid occurrenceId, TodoDraft draft, CancellationToken ct) => Task.CompletedTask;
        public Task ConvertToTodoAsync(
            Guid occurrenceId, TodoDraft draft, SeriesScope scope, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class LocalClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now => now;
        public Task DelayUntilAsync(
            DateTimeOffset dueAt,
            CancellationToken ct) => Task.CompletedTask;
    }

    private static void ApplyDarkPalette(FrameworkElement element)
    {
        element.Resources[SystemColors.WindowBrushKey] =
            new SolidColorBrush(Colors.Black);
        element.Resources[SystemColors.WindowTextBrushKey] =
            new SolidColorBrush(Colors.White);
        element.Resources[SystemColors.ControlBrushKey] =
            new SolidColorBrush(Colors.DarkSlateGray);
        element.Resources[SystemColors.ControlTextBrushKey] =
            new SolidColorBrush(Colors.White);
        element.Resources[SystemColors.ActiveBorderBrushKey] =
            new SolidColorBrush(Colors.Yellow);
        element.Resources[SystemColors.HighlightBrushKey] =
            new SolidColorBrush(Colors.Yellow);
        element.Resources[SystemColors.HighlightTextBrushKey] =
            new SolidColorBrush(Colors.Black);
    }

    private static void AssertBrush(Color expected, Brush actual) =>
        Assert.Equal(expected, Assert.IsType<SolidColorBrush>(actual).Color);

    private static double ContrastRatio(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Channel(byte value)
            {
                var normalized = value / 255d;
                return normalized <= 0.04045
                    ? normalized / 12.92
                    : Math.Pow((normalized + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Channel(color.R)) +
                   (0.7152 * Channel(color.G)) +
                   (0.0722 * Channel(color.B));
        }

        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}
