using TaskTracker.Contracts;
using TaskTracker.Contracts.Events;

namespace TaskTracker.TaskService.Infrastructure;

/// <summary>
/// Відповідність «тип події → ключ маршрутизації».
/// </summary>
public sealed class EventRoutingMap
{
    private static readonly IReadOnlyDictionary<Type, string> Map = new Dictionary<Type, string>
    {
        [typeof(AssignmentCreated)] = Messaging.RoutingKeys.AssignmentCreated,
        [typeof(AssignmentStarted)] = Messaging.RoutingKeys.AssignmentStarted,
        [typeof(WaitingOpened)] = Messaging.RoutingKeys.WaitingOpened,
        [typeof(WaitingClosed)] = Messaging.RoutingKeys.WaitingClosed,
        [typeof(AssignmentCompleted)] = Messaging.RoutingKeys.AssignmentCompleted,
        [typeof(AssignmentCancelled)] = Messaging.RoutingKeys.AssignmentCancelled
    };

    /// <summary>
    /// Кидає виняток, якщо для типу події ключа немає: краще впасти на старті
    /// розробки, ніж мовчки опублікувати подію з порожнім ключем — обмінник
    /// просто відкинув би її, і втрату було б майже неможливо помітити.
    /// </summary>
    public string GetRoutingKey(IDomainEvent @event) =>
        Map.TryGetValue(@event.GetType(), out var routingKey)
            ? routingKey
            : throw new InvalidOperationException(
                $"Для події {@event.GetType().Name} не задано ключ маршрутизації");
}