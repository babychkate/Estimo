# Лабораторна робота №2 — TaskTracker

Використання брокерів повідомлень для організації міжкомпонентної взаємодії.
Варіант 1: програмне забезпечення для відстеження виконання завдань.

## Склад рішення

| Проєкт | Роль |
|---|---|
| `TaskTracker.Contracts` | Інтерфейс події, 6 класів подій, топологія обміну (імена обмінника, черг, ключів і масок) |
| `TaskTracker.TaskService` | **Producer** — ASP.NET Core Web API зі Swagger |
| `TaskTracker.NotificationService` | **Consumer 1** — консоль, усі події, лист на `waiting.opened` |
| `TaskTracker.AnalyticsService` | **Consumer 2** — консоль, фільтрація за маскою `*.completed` |

## Топологія

```
                     tasktracker.events  (topic, durable)
                              │
          ┌───────────────────┴───────────────────┐
   assignment.#                               *.completed
   waiting.#                                       │
          │                                        │
  notifications_queue                       analytics_queue
          │                                        │
  NotificationService                      AnalyticsService
```

## Запуск

### 1. Брокер

```bash
docker compose up -d
```

Або без compose, як у методичці:

```bash
docker run -it --rm --name rabbitmq -p 5672:5672 -p 15672:15672 rabbitmq:4-management
```

Веб-інтерфейс: http://localhost:15672 — логін `guest`, пароль `guest`.

### 2. Відновлення пакетів

```bash
dotnet restore
```

### 3. Запуск сервісів — у трьох окремих терміналах

**Важливо: спочатку консюмери, потім продюсер.** Topic-обмінник мовчки
відкидає повідомлення, для яких немає жодної прив'язаної черги, — якщо
запустити TaskService першим, перші події зникнуть безслідно.

```bash
# Термінал 1
dotnet run --project src/TaskTracker.NotificationService

# Термінал 2
dotnet run --project src/TaskTracker.AnalyticsService

# Термінал 3
dotnet run --project src/TaskTracker.TaskService
```

Swagger: http://localhost:5080

## Налаштування пошти

За замовчуванням `Smtp:Enabled = false` — лист не надсилається, а друкується
в консоль NotificationService. Щоб слати по-справжньому, відредагуй
`src/TaskTracker.NotificationService/appsettings.json`:

```json
"Smtp": {
  "Enabled": true,
  "Host": "smtp.gmail.com",
  "Port": "587",
  "User": "твоя.адреса@gmail.com",
  "Password": "пароль застосунку"
}
```

Для Gmail потрібен **пароль застосунку** (App Password), а не звичайний пароль
від акаунта: увімкни двофакторну автентифікацію в акаунті Google, далі в
налаштуваннях безпеки створи App Password і встав сюди 16 символів без пробілів.

Отримувач захардкоджений у `EmailSender.RecipientAddress` —
`kateryna.babych.pz.2023@lpnu.ua`.

Файл `appsettings.json` з паролем до git не потрапить: каталог `data/` і
локальні appsettings вже в `.gitignore`. Пароль з основного `appsettings.json`
перед комітом прибери вручну.

## Сценарій демонстрації

1. **Створити завдання** — `POST /api/assignments`

   ```json
   {
     "title": "Звіт з АПЗ",
     "category": "Звіт",
     "dueDate": "2026-10-20T00:00:00",
     "estimatedMinutes": 120
   }
   ```

   Скопіюй `assignmentId` з відповіді.

   - TaskService: `→ опубліковано assignment.created`
   - NotificationService: подія отримана, проекція створена
   - AnalyticsService: **тиша** — маска не пропустила

2. **Розпочати** — `POST /api/assignments/{id}/start`

3. **Відкрити очікування** — `POST /api/assignments/{id}/waitings`

   ```json
   {
     "teacherName": "Луцик І.І.",
     "requestType": "Надати методичку",
     "description": "Потрібна методичка до ЛР №3",
     "agreedDeadline": "2026-10-15T00:00:00"
   }
   ```

   NotificationService формує і надсилає лист. Завдання переходить у `Blocked`.

4. **Перевірити блокування** — повтори крок 2 або спробуй завершити завдання.
   Отримаєш `400` з текстом доменного правила. Це демонструє валідацію в агрегаті:
   жодної події не породжено, стан не змінився.

5. **Закрити очікування** — `POST /api/assignments/{id}/waitings/close`

   ```json
   { "outcome": "Done" }
   ```

6. **Завершити** — `POST /api/assignments/{id}/complete`

   ```json
   { "actualMinutes": 210 }
   ```

   AnalyticsService нарешті прокидається і друкує таблицю з коефіцієнтом 1.75.

7. **Показати реплей** — `GET /api/assignments/{id}`

   У відповіді поле `eventStream` з усіма подіями і поточний стан,
   обчислений щойно прокручуванням цієї історії.

8. **Management UI** — http://localhost:15672 → Exchanges → `tasktracker.events`
   → видно обидві черги з прив'язками. Вкладка Queues показує різні лічильники:
   `notifications_queue` отримала всі події, `analytics_queue` — тільки завершення.

9. **Відтворюваність** — зупини TaskService (Ctrl+C), запусти знову,
   виконай `GET /api/assignments/{id}`. Стан на місці: він відновлений реплеєм
   з `data/event-store.json`, бо жодної таблиці з поточним станом не існує.

## Де яка вимога реалізована

| Пункт завдання | Файл |
|---|---|
| Агрегат, команди, події | `Domain/AssignmentAggregate.cs`, `Contracts/Events/Events.cs` |
| Producer приймає команди | `Controllers/AssignmentsController.cs` |
| Валідація (поверхнева) | `Dto/Commands.cs` — DataAnnotations |
| Валідація (доменна) | `AssignmentAggregate` — `DomainException` |
| Consumer через чергу, прив'язану до обмінника | `NotificationService/Program.cs` |
| Стан агрегату з потоку подій (сторона запису) | конструктор `AssignmentAggregate(history)` |
| Read Model (сторона читання) | `NotificationService` — `UpdateProjection` |
| Другий Consumer + окрема черга | `AnalyticsService/Program.cs` |
| Маска в Routing Key | `Messaging.Bindings.AnyCompleted` = `*.completed` |

## Файли даних

Створюються автоматично в каталозі `data/` поряд із бінарниками кожного сервісу:

- `event-store.json` — сховище подій (TaskService)
- `assignments-view.json` — проекція дошки завдань (NotificationService)
- `analytics-view.json` — проекція коефіцієнтів (AnalyticsService)

Щоб почати демонстрацію з чистого аркуша — видали ці файли і черги в
Management UI (Queues → обрати чергу → Delete).
