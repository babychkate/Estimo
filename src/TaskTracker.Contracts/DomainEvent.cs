namespace TaskTracker.Contracts;

/// <summary>
/// Спільна частина всіх подій. Інтерфейс лишається (на нього спираються
/// публікатор, сховище і агрегат), а базовий клас прибирає дублювання
/// трьох однакових властивостей у кожній події.
/// </summary>
public abstract class DomainEvent : IDomainEvent
{
    public Guid EventId { get; set; } = Guid.NewGuid();
    public Guid AssignmentId { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}