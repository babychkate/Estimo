using System.ComponentModel.DataAnnotations;
using TaskTracker.Contracts.Events;

namespace TaskTracker.TaskService.Dto;

// Перший рівень валідації — поверхневий: формат, обов'язковість, діапазони.
// Відсіює очевидне сміття до того, як воно дійде до доменних правил агрегату.

public sealed class CreateAssignmentRequest
{
    [Required(ErrorMessage = "Назва обов'язкова")]
    [StringLength(200, MinimumLength = 3)]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "Категорія обов'язкова")]
    [StringLength(60)]
    public string Category { get; set; } = string.Empty;

    [Required]
    public DateTime DueDate { get; set; }

    [Range(1, 100000, ErrorMessage = "Планова тривалість має бути від 1 хвилини")]
    public int EstimatedMinutes { get; set; }
}

public sealed class OpenWaitingRequest
{
    [Required(ErrorMessage = "Вкажіть викладача")]
    [StringLength(120)]
    public string TeacherName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Вкажіть тип запиту")]
    [StringLength(80)]
    public string RequestType { get; set; } = string.Empty;

    [StringLength(500)]
    public string Description { get; set; } = string.Empty;

    [Required]
    public DateTime AgreedDeadline { get; set; }
}

public sealed class CloseWaitingRequest
{
    [Required]
    public WaitingOutcome Outcome { get; set; }
}

public sealed class CompleteAssignmentRequest
{
    /// <summary>
    /// Фактична тривалість у хвилинах. Якщо не вказати — рахується як різниця
    /// між стартом і завершенням (на демонстрації це будуть секунди).
    /// </summary>
    [Range(1, 100000)]
    public int? ActualMinutes { get; set; }
}

public sealed class CancelAssignmentRequest
{
    [StringLength(300)]
    public string Reason { get; set; } = string.Empty;
}
