namespace TaskTracker.Contracts;

/// <summary>
/// Подія — факт, що вже відбувся в системі. Незмінна, іменується в минулому часі,
/// не може бути відхилена. Кожна подія несе власний ідентифікатор і мітку часу,
/// тому її можна розпізнати повторно (знадобиться для ідемпотентності).
/// </summary>
public interface IDomainEvent
{
    Guid EventId { get; }

    /// <summary>Ідентифікатор потоку подій, тобто агрегату «Завдання».</summary>
    Guid AssignmentId { get; }

    DateTime OccurredAt { get; }

}
