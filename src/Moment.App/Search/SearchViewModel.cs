using System.Collections.ObjectModel;
using Moment.App.Commands;
using Moment.Core.Search;

namespace Moment.App.Search;

public sealed class SearchViewModel : ObservableObject
{
    private readonly IItemSearchQuery _query;
    private readonly Func<DateOnly, Task> _navigate;
    private string _queryText = string.Empty;
    private bool _isOpen;
    private string? _errorMessage;

    public SearchViewModel(
        IItemSearchQuery query,
        Func<DateOnly, Task> navigate)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _navigate = navigate ?? throw new ArgumentNullException(nameof(navigate));
        SearchCommand = new AsyncCommand((_, ct) => SearchAsync(ct));
        CloseCommand = new AsyncCommand((_, _) =>
        {
            IsOpen = false;
            return Task.CompletedTask;
        });
        SelectResultCommand = new AsyncCommand(
            (parameter, ct) => SelectResultAsync(parameter as ItemSearchResult, ct));
    }

    public ObservableCollection<ItemSearchResult> Results { get; } = [];
    public IAsyncCommand SearchCommand { get; }
    public IAsyncCommand CloseCommand { get; }
    public IAsyncCommand SelectResultCommand { get; }

    public string QueryText
    {
        get => _queryText;
        set => SetProperty(ref _queryText, value ?? string.Empty);
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    private async Task SearchAsync(CancellationToken ct)
    {
        var filter = new ItemSearchFilter(QueryText);
        if (filter.Text.Length == 0)
        {
            Results.Clear();
            IsOpen = false;
            ErrorMessage = null;
            return;
        }

        try
        {
            var rows = await _query.SearchAsync(filter, ct);
            Results.Clear();
            foreach (var row in rows)
                Results.Add(row);
            IsOpen = true;
            ErrorMessage = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = exception.Message;
            IsOpen = true;
        }
    }

    private async Task SelectResultAsync(
        ItemSearchResult? result,
        CancellationToken ct)
    {
        if (result?.LocalDate is null)
            return;
        ct.ThrowIfCancellationRequested();
        await _navigate(result.LocalDate.Value);
        IsOpen = false;
    }
}
