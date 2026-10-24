# 🧩 TaskTracker

**TaskTracker** is an event-sourced task tracking service built around a RabbitMQ topic exchange.
Every change of state is an immutable event; the current state is never stored — it is recomputed by replaying the stream.

Most task trackers assume work stalls because you stopped working on it. In practice it stalls because you are **waiting on someone else** — a review, an approval, a missing document. TaskTracker makes that wait a first-class concept: an open request blocks the task, notifies the other party by email, and the time spent blocked is measured. A second consumer compares planned against actual duration per task category, which surfaces where estimates are systematically wrong.

[![.NET](https://img.shields.io/badge/.NET_8-512BD4?style=flat&logo=dotnet&logoColor=white)](https://learn.microsoft.com/en-us/aspnet/core/)
[![C#](https://img.shields.io/badge/C%23-239120?style=flat&logo=csharp&logoColor=white)](https://learn.microsoft.com/en-us/dotnet/csharp/)
[![RabbitMQ](https://img.shields.io/badge/RabbitMQ-FF6600?style=flat&logo=rabbitmq&logoColor=white)](https://www.rabbitmq.com/)
[![Docker](https://img.shields.io/badge/Docker-2496ED?style=flat&logo=docker&logoColor=white)](https://www.docker.com/)
[![Swagger](https://img.shields.io/badge/OpenAPI-85EA2D?style=flat&logo=swagger&logoColor=black)](https://swagger.io/)

---

## 🎯 What this project demonstrates

This is a **reference implementation**, not a product. It was built to work through four patterns end to end, in code that actually runs:

| Pattern | Where to look |
|---|---|
| **Event Sourcing** | `AssignmentAggregate` — state exists only as a replay of its event stream |
| **CQRS** | write side = aggregate + event store · read side = projections built by consumers |
| **Publish–Subscribe** | producer publishes to an exchange and knows nothing about queues or consumers |
| **Topic routing with wildcards** | two consumers, two queues, different binding masks — the broker does the filtering |

---

## 🏗️ Architecture

Three independent processes communicating only through the broker. The producer never calls a consumer; consumers never call the producer.

```
                    TaskService  (Producer, Web API)
                              │
                              ▼
                  tasktracker.events   (topic, durable)
                              │
          ┌───────────────────┴───────────────────┐
   assignment.#                               *.completed
   waiting.#                                       │
          │                                        │
  notifications_queue                       analytics_queue
          │                                        │
  NotificationService                      AnalyticsService
  (all events, email)                      (final events only)
```

Each command follows the same five-step pipeline:

```
1. load the aggregate's event history from the store
2. rebuild the aggregate by replaying that history
3. execute the command — invariants are checked, an event is raised
4. append new events to the store
5. publish each new event to the exchange
```

If step 3 rejects the command, no event is produced and nothing is written or published.

---

## ⚙️ Technologies

- **Runtime:** .NET 8 — one ASP.NET Core Web API (producer) and two console workers (consumers)
- **Broker:** RabbitMQ 4 — topic exchange, durable queues, persistent messages, manual acknowledgement, `prefetchCount = 1`
- **Client:** `RabbitMQ.Client` 6.8 — connection reused for the process lifetime, exchange declared idempotently by every service
- **API docs:** Swashbuckle / OpenAPI — the producer is driven entirely from Swagger
- **Email:** MailKit over an SMTP relay — deliberately the slowest operation in the system, and the one that justifies doing it asynchronously
- **Serialization:** `System.Text.Json` with a shared options instance — camelCase, string enums, unescaped Unicode
- **Storage:** append-only JSON files behind an `IEventStore` abstraction (see *Design decisions*)
- **Infrastructure:** Docker Compose — broker with its management UI and a named volume

---

## 🔁 Domain model

One aggregate root — **Assignment** — with six commands and six events.

| Command | Event | Routing key |
|---|---|---|
| `CreateAssignment` | `AssignmentCreated` | `assignment.created` |
| `StartAssignment` | `AssignmentStarted` | `assignment.started` |
| `OpenWaiting` | `WaitingOpened` | `waiting.opened` |
| `CloseWaiting` | `WaitingClosed` | `waiting.closed` |
| `CompleteAssignment` | `AssignmentCompleted` | `assignment.completed` |
| `CancelAssignment` | `AssignmentCancelled` | `assignment.cancelled` |

State machine:

```
Planned ──Start──► InProgress ──Complete──► Completed
    │                   │
    └─────OpenWaiting──► Blocked ◄──OpenWaiting─────┘
                            │
                       CloseWaiting
                            │
              ▼ returns to the previous state ▼
         Planned                        InProgress

Any unfinished state ──Cancel──► Cancelled
```

Invariants enforced inside the aggregate, not in the controller:

1. A task cannot be created twice.
2. Only a planned task can be started.
3. Only a task in progress can be completed.
4. A blocked task can neither be started nor completed.
5. At most one open waiting request per task.
6. A waiting request cannot be opened on a finished or cancelled task.
7. Cancelling a task automatically closes its open waiting request.

---

## 💡 Design decisions

**Waiting lives inside the Assignment aggregate.** Rules 4 and 5 touch both entities at once — "one open request per task" and "an open request blocks the task". Splitting them into separate aggregates would make those checks non-atomic. One consistency boundary, one guarantee.

**Routing keys were moved out of the events.** The first version exposed a `RoutingKey` property on `IDomainEvent`. That leaked transport into the domain: a business fact does not know it travels over AMQP. Keys now live in `EventRoutingMap` in the infrastructure layer, and the publisher asks the map. The domain event is pure data again.

**The event type is stored next to the body.** An interface cannot be deserialized, and every event shares the same three base fields, so the payload alone is ambiguous. Each record is wrapped with its type name — in storage as `{ eventType, data }`, in transit as an AMQP header. One problem, one solution, applied in both places.

**Replay and command execution share one code path.** `Apply` is the only method that mutates state. `Raise` calls `Apply` and records the event as uncommitted. Because both paths run the same switch, rebuilt state and live state cannot diverge — a bug class that appears the moment a command mutates fields directly.

**Uncommitted events are a list, not a field.** The aggregate is reconstructed per request and ends up holding both past and freshly raised events. The list marks what this request produced, so only those are appended and published. It also handles one command raising several events: `CancelAssignment` emits `WaitingClosed` then `AssignmentCancelled`.

**The analytics event is self-sufficient.** `AssignmentCompleted` carries category, planned and actual minutes. Its consumer is bound to `*.completed` only and will never see `assignment.created`, so it cannot look anything up. The event must be complete on its own.

**Manual acknowledgement, not auto-ack.** With `autoAck: true` a message is considered handled the moment it is delivered, so a crash mid-processing loses it silently. Acknowledgement is sent after the work is done. Email failures are caught separately — an unreachable SMTP relay must not block the queue.

**JSON files instead of a database.** A deliberate scope decision: the point was the messaging and event-sourcing mechanics, and file storage keeps the stream readable and the setup to one `docker compose up`. Access sits behind `IEventStore`, so the swap is local — [the follow-up project](#-limitations-and-next-steps) replaces it with PostgreSQL and changes nothing in the controller or the aggregate.

---

## ▶️ Sample session

Six commands against one task. Consumer output shows the projection being rebuilt event by event.

**1. Create, start, open a waiting request**

```http
POST /api/assignments
{
  "title": "API integration spec",
  "category": "Documentation",
  "dueDate": "2026-11-05T00:00:00",
  "estimatedMinutes": 120
}

POST /api/assignments/{id}/start

POST /api/assignments/{id}/waitings
{
  "teacherName": "M. Hartmann",
  "requestType": "Review required",
  "description": "Need sign-off on the endpoint contract",
  "agreedDeadline": "2026-11-01T00:00:00"
}
```

NotificationService:

```
[NotificationService] ← assignment.created  | EventId: 5b3f2bdb…
     projection: API integration spec [Documentation] — Planned | est. 120 min | events: 1

[NotificationService] ← assignment.started  | EventId: ae86befd…
     projection: API integration spec [Documentation] — InProgress | est. 120 min | events: 2

[NotificationService] ← waiting.opened      | EventId: c4e06999…
     projection: API integration spec [Documentation] — Blocked | est. 120 min | BLOCKED BY: M. Hartmann | events: 3
     [email] sent
```

AnalyticsService stays silent — its binding mask does not match any of these keys.

**2. The invariant holds**

```http
POST /api/assignments/{id}/complete   →  400 Bad Request
{ "error": "Task is blocked by an open request — it cannot be completed." }
```

No event was raised, nothing was written, nothing was published.

**3. Close the request and complete the task**

```http
POST /api/assignments/{id}/waitings/close   { "outcome": "Done" }
POST /api/assignments/{id}/complete         { "actualMinutes": 210 }
```

AnalyticsService wakes up for the first time:

```
[AnalyticsService] ← assignment.completed | «API integration spec» [Documentation]: est. 120 min, actual 210 min

     ESTIMATE ACCURACY BY CATEGORY
     ┌──────────────────────┬───────┬──────────┬──────────┬────────┐
     │ Category             │ Count │   Est.   │  Actual  │  Ratio │
     ├──────────────────────┼───────┼──────────┼──────────┼────────┤
     │ Documentation        │     1 │  120.0 m │  210.0 m │   1.75 │
     └──────────────────────┴───────┴──────────┴──────────┴────────┘
     Ratio > 1 — duration is underestimated, < 1 — overestimated
```

**4. Proof of replay**

```http
GET /api/assignments/{id}
```

```json
{
  "state": "Completed",
  "estimatedMinutes": 120,
  "actualMinutes": 210,
  "eventCount": 5,
  "eventStream": [
    { "type": "AssignmentCreated",   "routingKey": "assignment.created" },
    { "type": "AssignmentStarted",   "routingKey": "assignment.started" },
    { "type": "WaitingOpened",       "routingKey": "waiting.opened" },
    { "type": "WaitingClosed",       "routingKey": "waiting.closed" },
    { "type": "AssignmentCompleted", "routingKey": "assignment.completed" }
  ]
}
```

`state` is not stored anywhere. Restart the service and issue the same request — the response is identical, because it is computed from the stream every time.

**5. Proof of routing**

The management UI at `localhost:15672` shows both queues bound to the same exchange with different counters: `notifications_queue` received all five messages, `analytics_queue` received one. Nothing in the consumer code filters by event type — the broker does it.

---

## 📁 Project structure

```
TaskTracker/
├── docker-compose.yml
└── src/
    ├── TaskTracker.Contracts/              # shared language of the system
    │   ├── IDomainEvent.cs                 # event contract
    │   ├── DomainEvent.cs                  # base class with the three common fields
    │   ├── Events/Events.cs                # six concrete events
    │   └── Messaging.cs                    # exchange, queues, routing keys, binding masks
    │
    ├── TaskTracker.TaskService/            # Producer — ASP.NET Core Web API
    │   ├── Controllers/                    # command pipeline + replay query
    │   ├── Domain/AssignmentAggregate.cs   # invariants, Raise / Apply, replay
    │   ├── Dto/Commands.cs                 # surface validation
    │   └── Infrastructure/
    │       ├── IEventStore.cs              # storage abstraction
    │       ├── JsonEventStore.cs           # append-only JSON implementation
    │       ├── EventRoutingMap.cs          # event type → routing key
    │       └── RabbitMqPublisher.cs        # publishes to the exchange only
    │
    ├── TaskTracker.NotificationService/    # Consumer 1 — all events, email
    └── TaskTracker.AnalyticsService/       # Consumer 2 — *.completed only
```

---

## 🚀 Getting started

**Prerequisites**

```bash
dotnet --version    # 8.x
docker --version
```

**1. Start the broker**

```bash
docker compose up -d
```

Management UI: http://localhost:15672 — `guest` / `guest`

**2. Restore and build**

```bash
dotnet restore
dotnet build
```

**3. Run the services — consumers first**

A topic exchange silently drops messages that match no bound queue. Queues are declared by the consumers, so starting the producer first loses the first events.

```bash
# terminal 1
dotnet run --project src/TaskTracker.NotificationService

# terminal 2
dotnet run --project src/TaskTracker.AnalyticsService

# terminal 3
dotnet run --project src/TaskTracker.TaskService
```

Swagger: http://localhost:5080

**4. Email (optional)**

Leave `Smtp:Enabled = false` and the message is rendered to the console instead of being sent — enough to see the full flow. To send for real, put credentials in `appsettings.Development.json` (git-ignored) rather than in `appsettings.json`:

```json
{
  "Smtp": {
    "Enabled": true,
    "Host": "smtp-relay.example.com",
    "Port": "587",
    "User": "relay-login",
    "Password": "relay-key",
    "FromAddress": "noreply@example.com",
    "FromName": "TaskTracker"
  }
}
```

The recipient is a constant in `EmailSender` — in a real system it would come from the approver's profile.

**Starting from a clean slate:** delete the generated `data/*.json` files and purge both queues in the management UI.

---

## 🔭 Limitations and next steps

Known and deliberate, in rough order of importance:

- **No idempotency.** `MessageId` carries the event id, but consumers do not deduplicate, so a redelivery would be applied twice.
- **Failed messages are lost.** A message that throws is acknowledged anyway so it cannot block the queue. The correct answer is a dead-letter queue.
- **Non-atomic writes.** Event store and projections are rewritten in full on every change; a crash mid-write truncates the file.
- **No snapshots.** Replay walks the entire stream on every request, which does not scale past a few hundred events per aggregate.
- **Single user.** No authentication, no authorization, no multi-tenancy.

A follow-up project continues from here: PostgreSQL behind the same `IEventStore` interface, the consumers promoted to Web APIs, distributed tracing with OpenTelemetry and Jaeger — including propagating trace context across the queue — a YARP gateway, and a React front end.

---

Creator: **Kateryna Babych** — [LinkedIn](https://www.linkedin.com/in/babych-kate) | [Email](mailto:babychkatia14@gmail.com)
