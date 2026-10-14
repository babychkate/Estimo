using TaskTracker.Contracts;
using TaskTracker.Contracts.Events;

namespace TaskTracker.TaskService.Domain;

/// <summary>Стани життєвого циклу завдання (ФВ-1.4 зі звіту ЛР №1).</summary>
public enum AssignmentState
{
    Planned,
    InProgress,
    Blocked,
    Completed,
    Cancelled
}

/// <summary>Порушення доменного правила. Контролер перетворює його на HTTP 400.</summary>
public sealed class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
}

/// <summary>
/// Агрегат «Завдання» — корінь і єдиний гарант цілісності даних.
/// Очікування (Waiting) живе ВСЕРЕДИНІ агрегату, а не окремо, бо правила
/// «не більше одного активного очікування на завдання» (ФВ-2.4) та
/// «очікування блокує завдання» (ФВ-2.3) одночасно торкаються обох сутностей
/// і мають перевірятися в одній межі узгодженості.
///
/// Жоден зовнішній код не може змінити стан завдання в обхід методів цього класу:
/// усі властивості мають private set.
/// </summary>
public sealed class AssignmentAggregate
{
    // ---------- Поточний стан. Ніде не зберігається — завжди обчислюється реплеєм. ----------

    public Guid Id { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Category { get; private set; } = string.Empty;
    public DateTime DueDate { get; private set; }
    public int EstimatedMinutes { get; private set; }
    public AssignmentState State { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public int? ActualMinutes { get; private set; }

    public Guid? ActiveWaitingId { get; private set; }
    public string? ActiveWaitingTeacher { get; private set; }
    public DateTime? ActiveWaitingOpenedAt { get; private set; }

    /// <summary>Стан, у який завдання повернеться після закриття очікування.</summary>
    private AssignmentState _stateBeforeBlock;

    private bool _created;

    /// <summary>Події, породжені поточною командою і ще не збережені у сховище.</summary>
    public List<IDomainEvent> UncommittedEvents { get; } = new();

    /// <summary>
    /// Конструктор відновлення стану (Replay). Серце патерну «джерело подій»:
    /// агрегат не читає рядок з таблиці, а послідовно застосовує всю свою історію.
    /// Для нового завдання історія порожня.
    /// </summary>
    public AssignmentAggregate(IEnumerable<IDomainEvent> history)
    {
        foreach (var @event in history)
            Apply(@event);
    }

    public bool Exists => _created;

    // ---------- Команди. Кожна: перевірити правила → породити подію. ----------

    public void Create(Guid id, string title, string category, DateTime dueDate, int estimatedMinutes)
    {
        if (_created)
            throw new DomainException("Завдання вже створено.");
        if (string.IsNullOrWhiteSpace(title))
            throw new DomainException("Назва завдання не може бути порожньою.");
        if (string.IsNullOrWhiteSpace(category))
            throw new DomainException("Категорія завдання обов'язкова — за нею рахується коефіцієнт точності.");
        if (estimatedMinutes <= 0)
            throw new DomainException("Планова тривалість має бути більшою за нуль.");

        Raise(new AssignmentCreated
        {
            AssignmentId = id,
            Title = title.Trim(),
            Category = category.Trim(),
            DueDate = dueDate,
            EstimatedMinutes = estimatedMinutes
        });
    }

    public void Start()
    {
        EnsureCreated();

        if (State == AssignmentState.Blocked)
            throw new DomainException("Завдання заблоковане очікуванням — спершу закрийте очікування.");
        if (State != AssignmentState.Planned)
            throw new DomainException($"Розпочати можна лише заплановане завдання. Поточний стан: {State}.");

        Raise(new AssignmentStarted
        {
            AssignmentId = Id,
            Title = Title
        });
    }

    public void OpenWaiting(string teacherName, string requestType, string description, DateTime agreedDeadline)
    {
        EnsureCreated();

        if (State is AssignmentState.Completed or AssignmentState.Cancelled)
            throw new DomainException($"Не можна відкрити очікування для завдання у стані {State}.");

        // ФВ-2.4 — саме це правило й тримає очікування всередині агрегату.
        if (ActiveWaitingId is not null)
            throw new DomainException("На це завдання вже відкрите активне очікування.");

        if (string.IsNullOrWhiteSpace(teacherName))
            throw new DomainException("Потрібно вказати викладача.");

        Raise(new WaitingOpened
        {
            AssignmentId = Id,
            WaitingId = Guid.NewGuid(),
            AssignmentTitle = Title,
            TeacherName = teacherName.Trim(),
            RequestType = requestType.Trim(),
            Description = description?.Trim() ?? string.Empty,
            AgreedDeadline = agreedDeadline
        });
    }

    public void CloseWaiting(WaitingOutcome outcome)
    {
        EnsureCreated();

        if (ActiveWaitingId is null)
            throw new DomainException("На цьому завданні немає активного очікування.");

        var openedAt = ActiveWaitingOpenedAt!.Value;

        Raise(new WaitingClosed
        {
            AssignmentId = Id,
            WaitingId = ActiveWaitingId.Value,
            AssignmentTitle = Title,
            TeacherName = ActiveWaitingTeacher ?? string.Empty,
            Outcome = outcome,
            OpenedAt = openedAt,
            WaitedHours = Math.Round((DateTime.UtcNow - openedAt).TotalHours, 2)
        });
    }

    /// <summary>
    /// Завершення завдання. actualMinutesOverride дозволяє вказати фактичний час
    /// вручну — без нього демонстрація давала б нуль хвилин, бо між стартом і
    /// завершенням на захисті проходять секунди.
    /// </summary>
    public void Complete(int? actualMinutesOverride = null)
    {
        EnsureCreated();

        if (State == AssignmentState.Blocked)
            throw new DomainException("Завдання заблоковане очікуванням — завершити його не можна.");
        if (State != AssignmentState.InProgress)
            throw new DomainException($"Завершити можна лише завдання в роботі. Поточний стан: {State}.");

        var actual = actualMinutesOverride
                     ?? (int)Math.Max(1, Math.Round((DateTime.UtcNow - StartedAt!.Value).TotalMinutes));

        if (actual <= 0)
            throw new DomainException("Фактична тривалість має бути більшою за нуль.");

        Raise(new AssignmentCompleted
        {
            AssignmentId = Id,
            Title = Title,
            Category = Category,
            EstimatedMinutes = EstimatedMinutes,
            ActualMinutes = actual
        });
    }

    public void Cancel(string reason)
    {
        EnsureCreated();

        if (State is AssignmentState.Completed or AssignmentState.Cancelled)
            throw new DomainException($"Завдання у стані {State} скасувати не можна.");

        // ФВ-1.8 / UC-09: скасування закриває активне очікування.
        if (ActiveWaitingId is not null)
            CloseWaiting(WaitingOutcome.NoResponse);

        Raise(new AssignmentCancelled
        {
            AssignmentId = Id,
            Title = Title,
            Reason = string.IsNullOrWhiteSpace(reason) ? "не вказано" : reason.Trim()
        });
    }

    // ---------- Внутрішня механіка ----------

    private void EnsureCreated()
    {
        if (!_created)
            throw new DomainException("Завдання не знайдено.");
    }

    /// <summary>Застосувати подію до власного стану і запам'ятати її як нову.</summary>
    private void Raise(IDomainEvent @event)
    {
        Apply(@event);
        UncommittedEvents.Add(@event);
    }

    /// <summary>
    /// Єдине місце, де змінюється стан. Викликається і під час реплею історії,
    /// і під час виконання нової команди — тому обидва шляхи дають однаковий результат.
    /// </summary>
    private void Apply(IDomainEvent @event)
    {
        switch (@event)
        {
            case AssignmentCreated e:
                Id = e.AssignmentId;
                Title = e.Title;
                Category = e.Category;
                DueDate = e.DueDate;
                EstimatedMinutes = e.EstimatedMinutes;
                State = AssignmentState.Planned;
                _created = true;
                break;

            case AssignmentStarted e:
                State = AssignmentState.InProgress;
                StartedAt = e.OccurredAt;
                break;

            case WaitingOpened e:
                _stateBeforeBlock = State;
                State = AssignmentState.Blocked;
                ActiveWaitingId = e.WaitingId;
                ActiveWaitingTeacher = e.TeacherName;
                ActiveWaitingOpenedAt = e.OccurredAt;
                break;

            case WaitingClosed:
                State = _stateBeforeBlock;
                ActiveWaitingId = null;
                ActiveWaitingTeacher = null;
                ActiveWaitingOpenedAt = null;
                break;

            case AssignmentCompleted e:
                State = AssignmentState.Completed;
                ActualMinutes = e.ActualMinutes;
                break;

            case AssignmentCancelled:
                State = AssignmentState.Cancelled;
                break;
        }
    }
}
