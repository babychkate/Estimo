namespace TaskTracker.NotificationService;

/// <summary>
/// Проекція (Read Model). Денормалізований, готовий до показу знімок завдання.
/// Доменних правил не містить — тільки те, що зручно читати.
///
/// Будується ВИКЛЮЧНО з подій, що прийшли з брокера. Сервіс ніколи не звертається
/// ні до TaskService, ні до його сховища подій.
///
/// Проекція тут не формальність: щоб написати осмислений лист на waiting.opened,
/// сервісу потрібна назва завдання — а вона приходила раніше, в assignment.created.
/// Тобто консюмер змушений тримати власний локальний стан.
/// </summary>
public sealed class AssignmentView
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string State { get; set; } = "Planned";
    public DateTime DueDate { get; set; }
    public int EstimatedMinutes { get; set; }
    public int? ActualMinutes { get; set; }

    /// <summary>Стан, у який завдання повернеться після зняття блокування.</summary>
    public string StateBeforeBlock { get; set; } = "Planned";

    public bool IsBlocked { get; set; }
    public string? WaitingTeacher { get; set; }
    public DateTime? WaitingSince { get; set; }

    public int EventsApplied { get; set; }

    public override string ToString()
    {
        var blocked = IsBlocked ? $" | ЗАБЛОКОВАНО: {WaitingTeacher}" : string.Empty;
        var actual = ActualMinutes is null ? string.Empty : $" | факт {ActualMinutes} хв";
        return $"{Title} [{Category}] — {State} | план {EstimatedMinutes} хв{actual}{blocked}";
    }
}
