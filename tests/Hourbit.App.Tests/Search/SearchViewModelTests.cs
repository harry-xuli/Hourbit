using Hourbit.App.Search;
using Hourbit.Core.Domain;
using Hourbit.Core.Search;

namespace Hourbit.App.Tests.Search;

public sealed class SearchViewModelTests
{
    [Fact]
    public async Task Search_publishes_results_and_selecting_a_dated_result_navigates_to_its_date()
    {
        var result = new ItemSearchResult(
            Guid.NewGuid(), SearchItemType.Reminder, "未来会议",
            new DateOnly(2027, 3, 18), ReminderImportance.Normal, false);
        var query = new Query([result]);
        DateOnly? opened = null;
        var vm = new SearchViewModel(query, date =>
        {
            opened = date;
            return Task.CompletedTask;
        });
        vm.QueryText = "未来";

        await vm.SearchCommand.ExecuteAsync(null);
        await vm.SelectResultCommand.ExecuteAsync(result);

        Assert.Equal("未来会议", Assert.Single(vm.Results).Title);
        Assert.Equal(new DateOnly(2027, 3, 18), opened);
        Assert.False(vm.IsOpen);
    }

    [Fact]
    public async Task Empty_search_closes_results_without_querying_database()
    {
        var query = new Query([]);
        var vm = new SearchViewModel(query, _ => Task.CompletedTask)
        {
            QueryText = "   "
        };

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(0, query.Calls);
        Assert.False(vm.IsOpen);
    }

    private sealed class Query(IReadOnlyList<ItemSearchResult> rows) : IItemSearchQuery
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<ItemSearchResult>> SearchAsync(
            ItemSearchFilter filter, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(rows);
        }
    }
}
