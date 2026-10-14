using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;
using TaskTracker.Contracts.Events;

namespace TaskTracker.NotificationService;

/// <summary>
/// Надсилання листів через зовнішню службу доставки пошти.
///
/// Лист формується і надсилається ТІЛЬКИ на подію waiting.opened —
/// це сценарій UC-10 зі звіту ЛР №1: студент відкрив очікування,
/// викладач отримав повідомлення.
/// </summary>
public sealed class EmailSender
{
    /// <summary>Отримувач захардкоджений — у реальній системі адреса бралася б з профілю викладача.</summary>
    public const string RecipientAddress = "kateryna.babych.pz.2023@lpnu.ua";
    public const string RecipientName = "Катерина Бабич";

    private readonly string _host;
    private readonly int _port;
    private readonly string _user;
    private readonly string _password;
    private readonly string _fromName;
    private readonly string _fromAddress;
    private readonly bool _enabled;

    public EmailSender(IConfiguration configuration)
    {
        _host = configuration["Smtp:Host"] ?? "smtp.gmail.com";
        _port = int.TryParse(configuration["Smtp:Port"], out var port) ? port : 587;
        _user = configuration["Smtp:User"] ?? string.Empty;
        _password = configuration["Smtp:Password"] ?? string.Empty;
        _fromName = configuration["Smtp:FromName"] ?? "TaskTracker";
        _fromAddress = configuration["Smtp:FromAddress"] ?? _user;
        _enabled = bool.TryParse(configuration["Smtp:Enabled"], out var enabled) && enabled;
    }

    public void SendWaitingOpened(WaitingOpened @event)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(_user))
        {
            Console.WriteLine("     [email] надсилання вимкнене (Smtp:Enabled = false) — лист лише сформовано");
            PrintPreview(@event);
            return;
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_fromName, _fromAddress));
            message.To.Add(new MailboxAddress(RecipientName, RecipientAddress));
            message.Subject = $"TaskTracker: новий запит — {@event.RequestType}";

            message.Body = new TextPart("plain")
            {
                Text = BuildBody(@event)
            };

            using var client = new SmtpClient();
            client.Connect(_host, _port, SecureSocketOptions.StartTls);
            client.Authenticate(_user, _password);
            client.Send(message);
            client.Disconnect(true);

            Console.WriteLine($"     [email] лист надіслано на {RecipientAddress}");
        }
        catch (Exception ex)
        {
            // Важливо: помилка пошти НЕ має валити обробку повідомлення.
            // Інакше без ack подія вічно поверталася б у чергу й перевідправлялася.
            Console.WriteLine($"     [email] ПОМИЛКА надсилання: {ex.Message}");
        }
    }

    private void PrintPreview(WaitingOpened @event)
    {
        Console.WriteLine("     ┌─ попередній перегляд листа ─────────────");
        foreach (var line in BuildBody(@event).Split('\n'))
            Console.WriteLine($"     │ {line.TrimEnd()}");
        Console.WriteLine("     └──────────────────────────────────────────");
    }

    private static string BuildBody(WaitingOpened @event) =>
        $"""
         Доброго дня, {@event.TeacherName}!

         Студентка очікує на вашу дію щодо завдання «{@event.AssignmentTitle}».

         Тип запиту:        {@event.RequestType}
         Уточнення:         {(string.IsNullOrWhiteSpace(@event.Description) ? "—" : @event.Description)}
         Домовлений термін: {@event.AgreedDeadline:dd.MM.yyyy}

         Доки запит не закрито, завдання перебуває у стані «заблоковано».

         --
         Надіслано автоматично системою Estimo
         Подія: {@event.RoutingKey}, EventId: {@event.EventId}
         """;
}
