using System.Text.Json;
using TaskTracker.Contracts;

namespace TaskTracker.TaskService.Infrastructure;

/// <summary>
/// Сховище подій
/// </summary>
public interface IEventStore
{
    IReadOnlyList<IDomainEvent> GetEvents(Guid aggregateId);
    void Append(Guid aggregateId, IEnumerable<IDomainEvent> events);
    IReadOnlyList<Guid> GetAllAggregateIds();
}

/// <summary>
/// Сховище подій у JSON-файлі. Події дописуються і ніколи не змінюються —
/// це і є незмінна історія, з якої агрегат відновлює свій стан.
/// </summary>
public sealed class JsonEventStore : IEventStore
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, List<IDomainEvent>> _streams = new();

    /// <summary>Конверт для збереження: тип події + її тіло.</summary>
    private sealed class StoredEvent
    {
        public string EventType { get; set; } = string.Empty;
        public JsonElement Data { get; set; }
    }

    public JsonEventStore(string filePath)
    {
        _filePath = filePath;

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        Load();
    }

    public IReadOnlyList<IDomainEvent> GetEvents(Guid aggregateId)
    {
        lock (_lock)
        {
            return _streams.TryGetValue(aggregateId, out var stream)
                ? stream.ToList()
                : new List<IDomainEvent>();
        }
    }

    public void Append(Guid aggregateId, IEnumerable<IDomainEvent> events)
    {
        lock (_lock)
        {
            if (!_streams.TryGetValue(aggregateId, out var stream))
            {
                stream = new List<IDomainEvent>();
                _streams[aggregateId] = stream;
            }

            stream.AddRange(events);
            Save();
        }
    }

    public IReadOnlyList<Guid> GetAllAggregateIds()
    {
        lock (_lock)
            return _streams.Keys.ToList();
    }

    private void Load()
    {
        if (!File.Exists(_filePath))
            return;

        var json = File.ReadAllText(_filePath);
        if (string.IsNullOrWhiteSpace(json))
            return;

        var raw = JsonSerializer.Deserialize<Dictionary<Guid, List<StoredEvent>>>(json, Messaging.JsonOptions);
        if (raw is null)
            return;

        foreach (var (aggregateId, stored) in raw)
        {
            var stream = new List<IDomainEvent>();

            foreach (var item in stored)
            {
                if (!Messaging.EventTypes.TryGetValue(item.EventType, out var type))
                    continue; // невідомий тип події — пропускаємо, щоб не впасти на старих даних

                if (JsonSerializer.Deserialize(item.Data.GetRawText(), type, Messaging.JsonOptions) is IDomainEvent @event)
                    stream.Add(@event);
            }

            _streams[aggregateId] = stream;
        }
    }

    private void Save()
    {
        var raw = _streams.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Select(@event => new StoredEvent
            {
                EventType = @event.GetType().Name,
                Data = JsonSerializer.SerializeToElement(@event, @event.GetType(), Messaging.JsonOptions)
            }).ToList());

        File.WriteAllText(_filePath, JsonSerializer.Serialize(raw, Messaging.JsonOptions));
    }
}
