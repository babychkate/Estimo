using System.Text.Json;

namespace TaskTracker.Contracts;

/// <summary>
/// Сховище проекції у JSON-файлі. Спільне для обох консюмерів.
///
/// Проекція — це модель ЧИТАННЯ: вона живе окремо від сховища подій,
/// оновлюється асинхронно і може відставати. Саме це розділення
/// сторони запису і сторони читання й називається CQRS.
/// </summary>
public sealed class ProjectionStore<TView> where TView : class, new()
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private Dictionary<string, TView> _items = new();

    public ProjectionStore(string filePath)
    {
        _filePath = filePath;

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (!File.Exists(filePath))
            return;

        var json = File.ReadAllText(filePath);
        if (string.IsNullOrWhiteSpace(json))
            return;

        _items = JsonSerializer.Deserialize<Dictionary<string, TView>>(json, Messaging.JsonOptions)
                 ?? new Dictionary<string, TView>();
    }

    public IReadOnlyCollection<TView> All
    {
        get { lock (_lock) return _items.Values.ToList(); }
    }

    public TView GetOrCreate(string key)
    {
        lock (_lock)
        {
            if (!_items.TryGetValue(key, out var value))
            {
                value = new TView();
                _items[key] = value;
            }

            return value;
        }
    }

    public void Save()
    {
        lock (_lock)
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_items, Messaging.JsonOptions));
    }
}
