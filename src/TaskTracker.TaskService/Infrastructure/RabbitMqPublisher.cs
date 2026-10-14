using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using TaskTracker.Contracts;

namespace TaskTracker.TaskService.Infrastructure;

/// <summary>
/// Публікація подій у topic-обмінник.
///
/// Сервіс зареєстрований як singleton: з'єднання з RabbitMQ дороге, відкривати
/// його на кожен HTTP-запит — класична помилка.
///
/// Продюсер оголошує ТІЛЬКИ обмінник. Про черги й консюмерів він нічого не знає —
/// у цьому й полягає слабка зв'язаність: щоб додати новий обробник, у цьому
/// коді не треба міняти жодного рядка.
/// </summary>
public sealed class RabbitMqPublisher : IDisposable
{
    private readonly IConnection _connection;
    private readonly IModel _channel;
    private readonly ILogger<RabbitMqPublisher> _logger;
    private readonly EventRoutingMap _routingMap;

    public RabbitMqPublisher(IConfiguration configuration, 
                            ILogger<RabbitMqPublisher> logger,
                            EventRoutingMap routingMap)
    {
        _logger = logger;
        _routingMap = routingMap;

        var factory = new ConnectionFactory
        {
            HostName = configuration["RabbitMq:HostName"] ?? "localhost",
            Port = int.TryParse(configuration["RabbitMq:Port"], out var port) ? port : 5672,
            UserName = configuration["RabbitMq:UserName"] ?? "guest",
            Password = configuration["RabbitMq:Password"] ?? "guest"
        };

        _connection = factory.CreateConnection("TaskService");
        _channel = _connection.CreateModel();

        _channel.ExchangeDeclare(
            exchange: Messaging.ExchangeName,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null);

        _logger.LogInformation("[TaskService] Підключено до RabbitMQ, обмінник '{Exchange}' (topic, durable) готовий",
            Messaging.ExchangeName);
    }

    public void Publish(IDomainEvent @event)
    {
        var routingKey = _routingMap.GetRoutingKey(@event);
        var json = JsonSerializer.Serialize(@event, @event.GetType(), Messaging.JsonOptions);
        var body = Encoding.UTF8.GetBytes(json);

        var properties = _channel.CreateBasicProperties();
        properties.Persistent = true;                       // повідомлення переживе перезапуск брокера
        properties.ContentType = "application/json";
        properties.MessageId = @event.EventId.ToString();   // знадобиться для ідемпотентності в ЛР №3
        properties.Type = @event.GetType().Name;
        properties.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        properties.Headers = new Dictionary<string, object>
        {
            ["EventType"] = @event.GetType().Name
        };

        _channel.BasicPublish(
            exchange: Messaging.ExchangeName,
            routingKey: routingKey,
            basicProperties: properties,
            body: body);

        _logger.LogInformation("[TaskService] → опубліковано {RoutingKey} | EventId: {EventId}",
            routingKey, @event.EventId);
    }

    public void Dispose()
    {
        if (_channel.IsOpen) _channel.Close();
        if (_connection.IsOpen) _connection.Close();

        _channel.Dispose();
        _connection.Dispose();
    }
}
