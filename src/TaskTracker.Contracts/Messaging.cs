using System.Text.Json;
using System.Text.Json.Serialization;
using TaskTracker.Contracts.Events;

namespace TaskTracker.Contracts;

/// <summary>
/// Єдине місце, де описана топологія обміну. І продюсер, і обидва консюмери
/// беруть імена звідси, тому розсинхронізуватися неможливо.
/// </summary>
public static class Messaging
{
    /// <summary>Topic-обмінник. Продюсер публікує тільки в нього і про черги нічого не знає.</summary>
    public const string ExchangeName = "tasktracker.events";

    public const string NotificationsQueue = "notifications_queue";
    public const string AnalyticsQueue = "analytics_queue";

    /// <summary>Ключі маршрутизації — по одному на тип події.</summary>
    public static class RoutingKeys
    {
        public const string AssignmentCreated = "assignment.created";
        public const string AssignmentStarted = "assignment.started";
        public const string WaitingOpened = "waiting.opened";
        public const string WaitingClosed = "waiting.closed";
        public const string AssignmentCompleted = "assignment.completed";
        public const string AssignmentCancelled = "assignment.cancelled";
    }

    /// <summary>Маски прив'язок черг до обмінника.</summary>
    public static class Bindings
    {
        /// <summary>Усі події завдання — для NotificationService.</summary>
        public const string AllAssignmentEvents = "assignment.#";

        /// <summary>Усі події очікування — для NotificationService.</summary>
        public const string AllWaitingEvents = "waiting.#";

        /// <summary>
        /// Тільки фінальні події — для AnalyticsService.
        /// Саме ця маска є реалізацією п. 4 завдання: AnalyticsService фізично
        /// не отримує assignment.created чи waiting.opened, бо обмінник не
        /// зіставляє їх ключі з цією прив'язкою.
        /// </summary>
        public const string AnyCompleted = "*.completed";
    }

    /// <summary>
    /// Відповідність між іменем типу в заголовку EventType і класом події.
    /// Консюмер читає заголовок і одразу знає, у який тип десеріалізувати тіло.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Type> EventTypes = new Dictionary<string, Type>
    {
        [nameof(AssignmentCreated)] = typeof(AssignmentCreated),
        [nameof(AssignmentStarted)] = typeof(AssignmentStarted),
        [nameof(WaitingOpened)] = typeof(WaitingOpened),
        [nameof(WaitingClosed)] = typeof(WaitingClosed),
        [nameof(AssignmentCompleted)] = typeof(AssignmentCompleted),
        [nameof(AssignmentCancelled)] = typeof(AssignmentCancelled)
    };

    /// <summary>Спільні налаштування серіалізації для всіх трьох сервісів.</summary>
    public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
