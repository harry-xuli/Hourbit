using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hourbit.App.QuickAdd;
using Hourbit.App.Styles;
using Hourbit.Core.Abstractions;
using Hourbit.Core.Domain;
using Hourbit.Core.Parsing;
using Hourbit.Core.Services;
using Hourbit.TestSupport;
using System.Globalization;

namespace Hourbit.App.Tests.QuickAdd;

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
            Assert.Equal(
                "快速创建待办或提醒输入",
                System.Windows.Automation.AutomationProperties.GetName(input));
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

    [Fact]
    public Task Refresh_only_exposes_focusable_retry_and_window_Enter_retries_without_recreating() =>
        WpfTestHost.RunAsync(async () =>
        {
            var due = DateTimeOffset.Parse("2026-08-05T14:30:00+08:00");
            var refreshAttempts = 0;
            var reminders = new ReminderServiceStub();
            var vm = new QuickAddViewModel(
                new StubParser(new ParseResult.Success(new ReminderDraft(
                    "开会", due, ReminderKind.Plan, ReminderImportance.Normal, null))),
                reminders,
                new TodoServiceStub(),
                new LocalClock(due.AddDays(-1)),
                TimeZoneInfo.CreateCustomTimeZone(
                    "UTC+08-refresh-only", TimeSpan.FromHours(8), "UTC+08", "UTC+08"),
                CultureInfo.GetCultureInfo("zh-CN"),
                _ => ++refreshAttempts == 1
                    ? throw new InvalidOperationException("时间轴刷新失败")
                    : Task.CompletedTask);
            vm.Text = "8月5日14:30开会";
            var window = new QuickAddWindow { DataContext = vm };
            window.Show();
            window.Activate();
            window.UpdateLayout();
            try
            {
                await vm.SubmitAsync();
                window.UpdateLayout();

                var input = Assert.IsType<TextBox>(window.FindName("InputBox"));
                var retry = Assert.IsType<Button>(window.FindName("RefreshRetryButton"));
                Assert.False(input.IsEnabled);
                Assert.True(retry.IsVisible);
                Assert.True(retry.IsEnabled);
                Assert.True(retry.Focusable);
                Assert.True(retry.Focus());
                Assert.True(window.IsVisible);
                Assert.Contains("重试刷新", vm.ErrorMessage);

                Assert.True(await window.TrySubmitFromEnterAsync());

                Assert.Equal(1, reminders.CreateCalls);
                Assert.Equal(2, refreshAttempts);
                Assert.False(window.IsVisible);
                Assert.False(vm.IsRefreshOnly);
            }
            finally
            {
                if (!window.IsClosed)
                {
                    if (vm.IsRefreshOnly)
                        await vm.SubmitAsync();
                    window.Close();
                }
            }
        });

    [Fact]
    public Task Refresh_only_blocks_user_close_until_refresh_retry_succeeds() =>
        WpfTestHost.RunAsync(async () =>
        {
            var due = DateTimeOffset.Parse("2026-08-05T14:30:00+08:00");
            var refreshAttempts = 0;
            var reminders = new ReminderServiceStub();
            var vm = new QuickAddViewModel(
                new StubParser(new ParseResult.Success(new ReminderDraft(
                    "开会", due, ReminderKind.Plan, ReminderImportance.Normal, null))),
                reminders,
                new TodoServiceStub(),
                new LocalClock(due.AddDays(-1)),
                TimeZoneInfo.CreateCustomTimeZone(
                    "UTC+08-close-guard", TimeSpan.FromHours(8), "UTC+08", "UTC+08"),
                CultureInfo.GetCultureInfo("zh-CN"),
                _ => ++refreshAttempts == 1
                    ? throw new InvalidOperationException("时间轴刷新失败")
                    : Task.CompletedTask);
            vm.Text = "8月5日14:30开会";
            var window = new QuickAddWindow { DataContext = vm };
            window.Show();

            await vm.SubmitAsync();
            window.Close();

            Assert.True(window.IsVisible);
            Assert.False(window.IsClosed);
            Assert.True(vm.IsRefreshOnly);

            await vm.SubmitCommand.ExecuteAsync(null);

            Assert.Equal(1, reminders.CreateCalls);
            Assert.False(window.IsVisible);
            window.Close();
            Assert.True(window.IsClosed);
        });

    [Fact]
    public Task Refresh_retry_Enter_is_handled_before_awaiting_async_refresh() =>
        WpfTestHost.RunAsync(async () =>
        {
            var due = DateTimeOffset.Parse("2026-08-05T14:30:00+08:00");
            var refreshAttempts = 0;
            var refreshEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRefresh = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var hidden = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var reminders = new ReminderServiceStub();
            var vm = new QuickAddViewModel(
                new StubParser(new ParseResult.Success(new ReminderDraft(
                    "开会", due, ReminderKind.Plan, ReminderImportance.Normal, null))),
                reminders,
                new TodoServiceStub(),
                new LocalClock(due.AddDays(-1)),
                TimeZoneInfo.CreateCustomTimeZone(
                    "UTC+08-enter-route", TimeSpan.FromHours(8), "UTC+08", "UTC+08"),
                CultureInfo.GetCultureInfo("zh-CN"),
                _ => ++refreshAttempts == 1
                    ? throw new InvalidOperationException("时间轴刷新失败")
                    : BlockRefreshAsync(refreshEntered, releaseRefresh));
            vm.HideRequested += (_, _) => hidden.TrySetResult();
            vm.Text = "8月5日14:30开会";
            var window = new QuickAddWindow { DataContext = vm };
            window.Show();
            window.Activate();
            await vm.SubmitAsync();
            window.UpdateLayout();
            var retry = Assert.IsType<Button>(window.FindName("RefreshRetryButton"));
            Assert.True(retry.Focus());
            var source = Assert.IsAssignableFrom<PresentationSource>(
                PresentationSource.FromVisual(window));
            var keyEvent = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                source,
                Environment.TickCount,
                Key.Enter)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };

            try
            {
                retry.RaiseEvent(keyEvent);

                Assert.True(keyEvent.Handled);
                await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(1, reminders.CreateCalls);

                releaseRefresh.TrySetResult();
                await hidden.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(window.IsVisible);
            }
            finally
            {
                releaseRefresh.TrySetResult();
                if (vm.IsRefreshOnly)
                    await hidden.Task.WaitAsync(TimeSpan.FromSeconds(2));
                if (!window.IsClosed)
                {
                    window.Close();
                }
            }
        });

    private static async Task BlockRefreshAsync(
        TaskCompletionSource entered,
        TaskCompletionSource release)
    {
        entered.TrySetResult();
        await release.Task;
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
        public int CreateCalls { get; private set; }

        public Task<ReminderOccurrence> CreateAsync(ReminderDraft draft, CancellationToken ct) =>
            Task.FromResult(CreateOccurrence(draft));

        private ReminderOccurrence CreateOccurrence(ReminderDraft draft)
        {
            CreateCalls++;
            return ReminderOccurrence.Schedule(Guid.NewGuid(), draft.DueAt);
        }
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
