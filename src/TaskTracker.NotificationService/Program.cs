using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TaskTracker.Contracts;
using TaskTracker.Contracts.Events;
using TaskTracker.NotificationService;

Console.OutputEncoding = Encoding.UTF8;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var projection = new ProjectionStore<AssignmentView>(
    configuration["Projection:FilePath"] ?? "data/assignments-view.json");

var emailSender = new EmailSender(configuration);

// ---------- Підключення до брокера ----------

var factory = new ConnectionFactory
{
    HostName = configuration["RabbitMq:HostName"] ?? "localhost",
    Port = int.TryParse(configuration["RabbitMq:Port"], out var port) ? port : 5672,
    UserName = configuration["RabbitMq:UserName"] ?? "guest",
    Password = configuration["RabbitMq:Password"] ?? "guest"
};

using var connection = factory.CreateConnection("NotificationService");
using var channel = connection.CreateModel();

// Обмінник оголошуємо з тими самими параметрами, що й продюсер.
// Операція ідемпотентна: якщо обмінник уже є — нічого не станеться.
channel.ExchangeDeclare(
    exchange: Messaging.ExchangeName,
    type: ExchangeType.Topic,
    durable: true,
    autoDelete: false,
    arguments: null);

// Власна черга сервісу.
channel.QueueDeclare(
    queue: Messaging.NotificationsQueue,
    durable: true,
    exclusive: false,
    autoDelete: false,
    arguments: null);

// Дві прив'язки: цей сервіс слухає ВСІ події системи.
channel.QueueBind(Messaging.NotificationsQueue, Messaging.ExchangeName, Messaging.Bindings.AllAssignmentEvents);
channel.QueueBind(Messaging.NotificationsQueue, Messaging.ExchangeName, Messaging.Bindings.AllWaitingEvents);

// Не більше одного неопрацьованого повідомлення за раз.
channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);

PrintHeader();

// ---------- Обробка повідомлень ----------

var consumer = new EventingBasicConsumer(channel);

consumer.Received += (_, args) =>
{
    try
    {
        var eventTypeName = ReadEventTypeHeader(args);

        if (eventTypeName is null || !Messaging.EventTypes.TryGetValue(eventTypeName, out var eventType))
        {
            Console.WriteLine($"[NotificationService] невідомий тип події, пропускаю: {args.RoutingKey}");
            channel.BasicAck(args.DeliveryTag, multiple: false);
            return;
        }

        var json = Encoding.UTF8.GetString(args.Body.ToArray());
        var @event = (IDomainEvent)JsonSerializer.Deserialize(json, eventType, Messaging.JsonOptions)!;

        Console.WriteLine();
        Console.WriteLine($"[NotificationService] ← {args.RoutingKey}  |  EventId: {@event.EventId}");

        var view = UpdateProjection(projection, @event);

        Console.WriteLine($"     проекція: {view}");

        // Лист шлеться тільки на відкриття очікування.
        if (@event is WaitingOpened waitingOpened)
            emailSender.SendWaitingOpened(waitingOpened);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[NotificationService] ПОМИЛКА обробки: {ex.Message}");
    }
    finally
    {
        // Ручне підтвердження після обробки. З autoAck: true повідомлення
        // вважалося б опрацьованим одразу після доставки — і зникало б,
        // якби сервіс упав посеред обробки.
        channel.BasicAck(args.DeliveryTag, multiple: false);
    }
};

channel.BasicConsume(
    queue: Messaging.NotificationsQueue,
    autoAck: false,
    consumer: consumer);

Console.WriteLine("Очікування подій... (Ctrl+C для виходу)");
Thread.Sleep(Timeout.Infinite);

// ---------- Допоміжне ----------

static string? ReadEventTypeHeader(BasicDeliverEventArgs args)
{
    if (args.BasicProperties.Headers is null)
        return args.BasicProperties.Type;

    if (!args.BasicProperties.Headers.TryGetValue("EventType", out var raw))
        return args.BasicProperties.Type;

    // RabbitMQ повертає рядкові заголовки як byte[].
    return raw switch
    {
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        string text => text,
        _ => args.BasicProperties.Type
    };
}

/// <summary>
/// Проектор: знає, як КОЖНА подія змінює модель читання.
/// Саме тут потік подій перетворюється на поточний стан (п. 3 завдання).
/// </summary>
static AssignmentView UpdateProjection(ProjectionStore<AssignmentView> store, IDomainEvent @event)
{
    var key = @event.AssignmentId.ToString();

    var view = store.GetOrCreate(key);
    view.Id = @event.AssignmentId;

    switch (@event)
    {
        case AssignmentCreated e:
            view.Title = e.Title;
            view.Category = e.Category;
            view.DueDate = e.DueDate;
            view.EstimatedMinutes = e.EstimatedMinutes;
            view.State = "Planned";
            break;

        case AssignmentStarted:
            view.State = "InProgress";
            break;

        case WaitingOpened e:
            view.StateBeforeBlock = view.State;
            view.State = "Blocked";
            view.IsBlocked = true;
            view.WaitingTeacher = e.TeacherName;
            view.WaitingSince = e.OccurredAt;
            break;

        case WaitingClosed:
            view.State = view.StateBeforeBlock;
            view.IsBlocked = false;
            view.WaitingTeacher = null;
            view.WaitingSince = null;
            break;

        case AssignmentCompleted e:
            view.State = "Completed";
            view.ActualMinutes = e.ActualMinutes;
            break;

        case AssignmentCancelled:
            view.State = "Cancelled";
            view.IsBlocked = false;
            break;
    }

    view.EventsApplied++;
    store.Save();

    return view;
}

static void PrintHeader()
{
    Console.WriteLine();
    Console.WriteLine("========================================================");
    Console.WriteLine("  NotificationService (Consumer 1)");
    Console.WriteLine($"  Черга:     {Messaging.NotificationsQueue}");
    Console.WriteLine($"  Прив'язки: {Messaging.Bindings.AllAssignmentEvents}, {Messaging.Bindings.AllWaitingEvents}");
    Console.WriteLine($"  Лист шлеться на: {Messaging.RoutingKeys.WaitingOpened}");
    Console.WriteLine("========================================================");
}
