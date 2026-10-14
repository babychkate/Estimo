using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TaskTracker.AnalyticsService;
using TaskTracker.Contracts;
using TaskTracker.Contracts.Events;

Console.OutputEncoding = Encoding.UTF8;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var projection = new ProjectionStore<CategoryAccuracyView>(
    configuration["Projection:FilePath"] ?? "data/analytics-view.json");

// ---------- Підключення до брокера ----------

var factory = new ConnectionFactory
{
    HostName = configuration["RabbitMq:HostName"] ?? "localhost",
    Port = int.TryParse(configuration["RabbitMq:Port"], out var port) ? port : 5672,
    UserName = configuration["RabbitMq:UserName"] ?? "guest",
    Password = configuration["RabbitMq:Password"] ?? "guest"
};

using var connection = factory.CreateConnection("AnalyticsService");
using var channel = connection.CreateModel();

channel.ExchangeDeclare(
    exchange: Messaging.ExchangeName,
    type: ExchangeType.Topic,
    durable: true,
    autoDelete: false,
    arguments: null);

// Власна, окрема від NotificationService черга.
channel.QueueDeclare(
    queue: Messaging.AnalyticsQueue,
    durable: true,
    exclusive: false,
    autoDelete: false,
    arguments: null);

// ОДНА прив'язка з маскою — реалізація п. 4 завдання.
// "*.completed" означає: рівно один сегмент, потім ".completed".
// Тому сюди доходить assignment.completed і НЕ доходять
// assignment.created, assignment.started, waiting.opened, waiting.closed.
channel.QueueBind(Messaging.AnalyticsQueue, Messaging.ExchangeName, Messaging.Bindings.AnyCompleted);

channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);

PrintHeader();
PrintTable(projection);

// ---------- Обробка повідомлень ----------

var consumer = new EventingBasicConsumer(channel);

consumer.Received += (_, args) =>
{
    try
    {
        var json = Encoding.UTF8.GetString(args.Body.ToArray());
        var @event = JsonSerializer.Deserialize<AssignmentCompleted>(json, Messaging.JsonOptions);

        if (@event is null)
        {
            channel.BasicAck(args.DeliveryTag, multiple: false);
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"[AnalyticsService] ← {args.RoutingKey}  |  EventId: {@event.EventId}");
        Console.WriteLine($"     «{@event.Title}» [{@event.Category}]: " +
                          $"план {@event.EstimatedMinutes} хв, факт {@event.ActualMinutes} хв");

        // Оновлення проекції: подія змінює агреговані показники категорії.
        var view = projection.GetOrCreate(@event.Category);
        view.Category = @event.Category;
        view.CompletedCount++;
        view.TotalEstimatedMinutes += @event.EstimatedMinutes;
        view.TotalActualMinutes += @event.ActualMinutes;
        projection.Save();

        PrintTable(projection);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[AnalyticsService] ПОМИЛКА обробки: {ex.Message}");
    }
    finally
    {
        channel.BasicAck(args.DeliveryTag, multiple: false);
    }
};

channel.BasicConsume(
    queue: Messaging.AnalyticsQueue,
    autoAck: false,
    consumer: consumer);

Console.WriteLine("Очікування подій... (Ctrl+C для виходу)");
Thread.Sleep(Timeout.Infinite);

// ---------- Допоміжне ----------

static void PrintTable(ProjectionStore<CategoryAccuracyView> store)
{
    var rows = store.All.OrderBy(r => r.Category).ToList();

    Console.WriteLine();
    Console.WriteLine("     ТОЧНІСТЬ ОЦІНЮВАННЯ ЗА КАТЕГОРІЯМИ");
    Console.WriteLine("     ┌──────────────────────┬───────┬──────────┬──────────┬────────┐");
    Console.WriteLine("     │ Категорія            │ К-сть │   План   │   Факт   │  Коеф. │");
    Console.WriteLine("     ├──────────────────────┼───────┼──────────┼──────────┼────────┤");

    if (rows.Count == 0)
    {
        Console.WriteLine("     │ (ще немає завершених завдань)                                │");
    }
    else
    {
        foreach (var row in rows)
        {
            var category = row.Category.Length > 20 ? row.Category[..20] : row.Category;
            Console.WriteLine($"     │ {category,-20} │ {row.CompletedCount,5} │ " +
                              $"{row.AverageEstimated,6:0.0} хв │ {row.AverageActual,6:0.0} хв │ " +
                              $"{row.AccuracyCoefficient,6:0.00} │");
        }
    }

    Console.WriteLine("     └──────────────────────┴───────┴──────────┴──────────┴────────┘");
    Console.WriteLine("     Коеф. > 1 — недооцінка тривалості, < 1 — переоцінка");
}

static void PrintHeader()
{
    Console.WriteLine();
    Console.WriteLine("========================================================");
    Console.WriteLine("  AnalyticsService (Consumer 2)");
    Console.WriteLine($"  Черга:     {Messaging.AnalyticsQueue}");
    Console.WriteLine($"  Прив'язка: {Messaging.Bindings.AnyCompleted}   ← ФІЛЬТРАЦІЯ ЗА МАСКОЮ");
    Console.WriteLine("  Інші події (created, started, waiting.*) сюди не доходять");
    Console.WriteLine("========================================================");
}
