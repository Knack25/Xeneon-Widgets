using System.Text.Json;
using System.Collections.Concurrent;

namespace PlannerEdge.Helper.Storage;

public interface ILocalJsonStore
{
    Task<T?> ReadAsync<T>(string name, CancellationToken cancellationToken);
    Task WriteAsync<T>(string name, T value, CancellationToken cancellationToken);
}

public sealed class LocalJsonStore(string rootDirectory) : ILocalJsonStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<T?> ReadAsync<T>(string name, CancellationToken cancellationToken)
    {
        var path = GetPath(name);
        if (!File.Exists(path)) return default;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    public async Task WriteAsync<T>(string name, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(rootDirectory);
        var path = GetPath(name);
        var writeGate = WriteGates.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
        await writeGate.WaitAsync(cancellationToken);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
            }

            if (File.Exists(path)) File.Replace(tempPath, path, null);
            else File.Move(tempPath, path);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            writeGate.Release();
        }
    }

    private string GetPath(string name) => Path.Combine(rootDirectory, name + ".json");
}
