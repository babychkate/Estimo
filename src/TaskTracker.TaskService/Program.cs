using System.Text.Json.Serialization;
using Microsoft.OpenApi.Models;
using TaskTracker.TaskService.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        // Щоб WaitingOutcome у Swagger і в тілі запиту був рядком ("Done"), а не числом.
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "TaskTracker — TaskService (Producer)",
        Version = "v1",
        Description = "Лабораторна робота №2. Приймає команди, валідує їх в агрегаті, " +
                      "зберігає події у сховище подій і публікує їх у RabbitMQ."
    });
});

// Сховище подій — JSON-файл. Інтерфейс дозволяє підмінити його на БД у ЛР №3.
var eventStorePath = builder.Configuration["EventStore:FilePath"] ?? "data/event-store.json";
builder.Services.AddSingleton<IEventStore>(_ => new JsonEventStore(eventStorePath));

// Карта маршрутизації — знання інфраструктури, а не домену.
builder.Services.AddSingleton<EventRoutingMap>();

// Publisher — singleton: одне з'єднання з брокером на весь час життя застосунку.
builder.Services.AddSingleton<RabbitMqPublisher>();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "TaskService v1");
    options.RoutePrefix = string.Empty; // Swagger відкривається на корені
});

app.MapControllers();

// Створюємо publisher одразу на старті, щоб помилку підключення до RabbitMQ
// побачити зараз, а не під час першого запиту.
app.Services.GetRequiredService<RabbitMqPublisher>();

Console.WriteLine();
Console.WriteLine("========================================================");
Console.WriteLine("  TaskService (Producer) запущено");
Console.WriteLine("  Swagger:  http://localhost:5080");
Console.WriteLine("  RabbitMQ: http://localhost:15672  (guest / guest)");
Console.WriteLine("========================================================");
Console.WriteLine();

app.Run();
