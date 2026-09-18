using System.Text.Json;

namespace PlannerEdge.Helper.Storage;

public interface ILocalJsonStore
{
    Task<T?> ReadAsync<T>(string name, CancellationToken cancellationToken);
    Task WriteAsync<T>(string name, T value, CancellationToken cancellationToken);
}

public sealed class LocalJsonStore(string rootDirectory) : ILocalJsonStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<T?> ReadAsync<T>(string name, CancellationToken cancellationToken)
    {
        var path = GetPath(name);
        if (!File.Exists(path)) return default;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    public async Task WriteAsync<T>(string name, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(rootDirectory);
        var path = GetPath(name);
        var tempPath = path + ".tmp";

        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private string GetPath(string name) => Path.Combine(rootDirectory, name + ".json");
}
