namespace TaskTracker.Contracts.Events;

/// <summary>Результат закриття очікування.</summary>
public enum WaitingOutcome
{
    /// <summary>Викладач виконав дію.</summary>
    Done = 0,

    /// <summary>Викладач відхилив звернення.</summary>
    Rejected = 1,

    /// <summary>Відповіді не було, очікування закрите за вичерпанням терміну.</summary>
    NoResponse = 2
}

/// <summary>Завдання створено. Початок потоку подій агрегату.</summary>
public sealed class AssignmentCreated : DomainEvent
{
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public DateTime DueDate { get; set; }
    public int EstimatedMinutes { get; set; }
}

/// <summary>Роботу над завданням розпочато.</summary>
public sealed class AssignmentStarted : DomainEvent
{
    public string Title { get; set; } = string.Empty;
}

/// <summary>Відкрито очікування дії викладача; завдання переходить у стан «заблоковано».</summary>
public sealed class WaitingOpened : DomainEvent
{
    public Guid WaitingId { get; set; }
    public string AssignmentTitle { get; set; } = string.Empty;
    public string TeacherName { get; set; } = string.Empty;
    public string RequestType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime AgreedDeadline { get; set; }
}

/// <summary>Очікування закрите; блокування з завдання знято.</summary>
public sealed class WaitingClosed : DomainEvent
{
    public Guid WaitingId { get; set; }
    public string AssignmentTitle { get; set; } = string.Empty;
    public string TeacherName { get; set; } = string.Empty;
    public WaitingOutcome Outcome { get; set; }
    public DateTime OpenedAt { get; set; }

    /// <summary>Скільки годин тривало очікування — матеріал для статистики часу відповіді.</summary>
    public double WaitedHours { get; set; }
}

/// <summary>
/// Завдання виконано. Подія самодостатня: AnalyticsService отримує ТІЛЬКИ її
/// (прив'язка за маскою *.completed), тому всі дані для перерахунку коефіцієнта
/// точності мають бути всередині самої події.
/// </summary>
public sealed class AssignmentCompleted : DomainEvent
{
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int EstimatedMinutes { get; set; }
    public int ActualMinutes { get; set; }
}

/// <summary>Завдання скасовано; зі статистики точності виключається.</summary>
public sealed class AssignmentCancelled : DomainEvent
{
    public string Title { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
