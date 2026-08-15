using System.Text.Json;

namespace Hourbit.Infrastructure.Data;

public sealed class DataResetRequestStore : IDataResetRequestStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _requestPath;

    public DataResetRequestStore(string databasePath)
    {
        ArgumentNullException.ThrowIfNull(databasePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath))
            ?? throw new ArgumentException("Database path must have a directory.", nameof(databasePath));
        _requestPath = Path.Combine(directory, "data-reset-request.json");
    }

    public async Task WriteAsync(DataResetRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var json = JsonSerializer.Serialize(request, SerializerOptions);
        var temporary = _requestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, json, ct);
            File.Move(temporary, _requestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public async Task<DataResetRequest?> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_requestPath))
            return null;

        string json;
        try
        {
            json = await File.ReadAllTextAsync(_requestPath, ct);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("数据重置请求无法读取。", exception);
        }

        try
        {
            return JsonSerializer.Deserialize<DataResetRequest>(json, SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("数据重置请求格式无效。", exception);
        }
    }

    public Task DeleteAsync(CancellationToken ct)
    {
        if (File.Exists(_requestPath))
            File.Delete(_requestPath);
        return Task.CompletedTask;
    }
}
