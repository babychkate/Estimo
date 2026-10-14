using Microsoft.AspNetCore.Mvc;
using TaskTracker.Contracts;
using TaskTracker.TaskService.Domain;
using TaskTracker.TaskService.Dto;
using TaskTracker.TaskService.Infrastructure;

namespace TaskTracker.TaskService.Controllers;

/// <summary>
/// Producer. Приймає команди користувача, проганяє їх через агрегат
/// і публікує породжені події в брокер.
///
/// Конвеєр однаковий для кожної команди:
///   1. дістати історію подій агрегату зі сховища
///   2. відновити агрегат реплеєм цієї історії
///   3. виконати команду — агрегат перевірить інваріанти й породить подію
///   4. дописати нові події у сховище
///   5. опублікувати кожну нову подію в обмінник
/// </summary>
[ApiController]
[Route("api/assignments")]
public sealed class AssignmentsController : ControllerBase
{
    private readonly IEventStore _eventStore;
    private readonly RabbitMqPublisher _publisher;
    private readonly EventRoutingMap _routingMap;
    private readonly ILogger<AssignmentsController> _logger;

    public AssignmentsController(
        IEventStore eventStore,
        RabbitMqPublisher publisher,
        EventRoutingMap routingMap,
        ILogger<AssignmentsController> logger)
    {
        _eventStore = eventStore;
        _publisher = publisher;
        _routingMap = routingMap;
        _logger = logger;
    }

    /// <summary>Команда CreateAssignment — створити завдання.</summary>
    [HttpPost]
    public IActionResult Create([FromBody] CreateAssignmentRequest request)
    {
        var assignmentId = Guid.NewGuid();

        // Історія порожня — агрегат чистий.
        var aggregate = new AssignmentAggregate(Array.Empty<IDomainEvent>());

        try
        {
            aggregate.Create(assignmentId, request.Title, request.Category, request.DueDate, request.EstimatedMinutes);
        }
        catch (DomainException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        Persist(assignmentId, aggregate);

        return Ok(new
        {
            assignmentId,
            message = "Завдання створено",
            state = aggregate.State.ToString()
        });
    }

    /// <summary>Команда StartAssignment — розпочати виконання.</summary>
    [HttpPost("{id:guid}/start")]
    public IActionResult Start(Guid id) =>
        Execute(id, aggregate => aggregate.Start(), "Завдання розпочато");

    /// <summary>Команда OpenWaiting — відкрити очікування дії викладача.</summary>
    [HttpPost("{id:guid}/waitings")]
    public IActionResult OpenWaiting(Guid id, [FromBody] OpenWaitingRequest request) =>
        Execute(id,
            aggregate => aggregate.OpenWaiting(
                request.TeacherName, request.RequestType, request.Description, request.AgreedDeadline),
            "Очікування відкрито, завдання заблоковано");

    /// <summary>Команда CloseWaiting — закрити очікування.</summary>
    [HttpPost("{id:guid}/waitings/close")]
    public IActionResult CloseWaiting(Guid id, [FromBody] CloseWaitingRequest request) =>
        Execute(id, aggregate => aggregate.CloseWaiting(request.Outcome), "Очікування закрито, блокування знято");

    /// <summary>Команда CompleteAssignment — завершити завдання.</summary>
    [HttpPost("{id:guid}/complete")]
    public IActionResult Complete(Guid id, [FromBody] CompleteAssignmentRequest request) =>
        Execute(id, aggregate => aggregate.Complete(request.ActualMinutes), "Завдання виконано");

    /// <summary>Команда CancelAssignment — скасувати завдання.</summary>
    [HttpPost("{id:guid}/cancel")]
    public IActionResult Cancel(Guid id, [FromBody] CancelAssignmentRequest request) =>
        Execute(id, aggregate => aggregate.Cancel(request.Reason), "Завдання скасовано");

    /// <summary>
    /// Запит (Query) — повертає стан агрегату, ВІДНОВЛЕНИЙ З ПОТОКУ ПОДІЙ.
    /// Жодної таблиці з поточним станом не існує: усе, що тут видно,
    /// обчислене щойно, прокручуванням історії.
    /// </summary>
    [HttpGet("{id:guid}")]
    public IActionResult GetById(Guid id)
    {
        var history = _eventStore.GetEvents(id);
        if (history.Count == 0)
            return NotFound(new { error = "Завдання не знайдено" });

        var aggregate = new AssignmentAggregate(history);

        return Ok(new
        {
            aggregate.Id,
            aggregate.Title,
            aggregate.Category,
            State = aggregate.State.ToString(),
            aggregate.DueDate,
            aggregate.EstimatedMinutes,
            aggregate.ActualMinutes,
            aggregate.StartedAt,
            ActiveWaiting = aggregate.ActiveWaitingId is null
                ? null
                : new
                {
                    Id = aggregate.ActiveWaitingId,
                    Teacher = aggregate.ActiveWaitingTeacher,
                    OpenedAt = aggregate.ActiveWaitingOpenedAt
                },
            EventCount = history.Count,
            EventStream = history.Select(e => new
            {
                Type = e.GetType().Name,
                e.EventId,
                e.OccurredAt,
                RoutingKey = _routingMap.GetRoutingKey(e)
            })
        });
    }

    /// <summary>Перелік усіх завдань, кожне відновлене реплеєм власної історії.</summary>
    [HttpGet]
    public IActionResult GetAll()
    {
        var result = _eventStore.GetAllAggregateIds()
            .Select(id => new AssignmentAggregate(_eventStore.GetEvents(id)))
            .Select(aggregate => new
            {
                aggregate.Id,
                aggregate.Title,
                aggregate.Category,
                State = aggregate.State.ToString(),
                aggregate.EstimatedMinutes,
                aggregate.ActualMinutes
            });

        return Ok(result);
    }

    // ---------- Спільна частина конвеєра ----------

    private IActionResult Execute(Guid id, Action<AssignmentAggregate> command, string successMessage)
    {
        var history = _eventStore.GetEvents(id);
        if (history.Count == 0)
            return NotFound(new { error = "Завдання не знайдено" });

        var aggregate = new AssignmentAggregate(history);

        try
        {
            command(aggregate);
        }
        catch (DomainException ex)
        {
            // Доменне правило порушене — жодної події не породжено, стан не змінився.
            _logger.LogWarning("[TaskService] Команду відхилено: {Message}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }

        Persist(id, aggregate);

        return Ok(new
        {
            assignmentId = id,
            message = successMessage,
            state = aggregate.State.ToString()
        });
    }

    /// <summary>Зберегти нові події у сховище і опублікувати їх у брокер.</summary>
    private void Persist(Guid id, AssignmentAggregate aggregate)
    {
        if (aggregate.UncommittedEvents.Count == 0)
            return;

        _eventStore.Append(id, aggregate.UncommittedEvents);

        foreach (var @event in aggregate.UncommittedEvents)
            _publisher.Publish(@event);

        aggregate.UncommittedEvents.Clear();
    }
}
