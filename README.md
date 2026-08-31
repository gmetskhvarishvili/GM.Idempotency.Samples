# GM.Idempotency.Samples

A runnable ASP.NET Core app demonstrating both integration points of
[GM.Idempotency](https://github.com/gmetskhvarishvili/GM.Idempotency) with **no external infra** —
in-memory `GM.Caching` + `GM.DistributedLock` back the dedup.

## Run

```bash
dotnet run --project GM.Idempotency.Sample.API
```

## 1. HTTP — replay a POST on retry

The `POST /api/v1/orders` endpoint is guarded by the middleware. `serverToken` is minted once per
real execution, so a replay returns the **same** token:

```bash
# First call — creates the order
curl -i -X POST http://localhost:5000/api/v1/orders \
  -H "Content-Type: application/json" -H "Idempotency-Key: abc-123" \
  -d '{"item":"widget","quantity":3}'
# → 201 Created   {"id":"a1b2c3d4","item":"widget","quantity":3,"serverToken":"…"}

# Retry with the SAME key — replayed, not re-created
curl -i -X POST http://localhost:5000/api/v1/orders \
  -H "Content-Type: application/json" -H "Idempotency-Key: abc-123" \
  -d '{"item":"widget","quantity":3}'
# → 201 Created   (same id + serverToken)   + header  Idempotency-Replayed: true
```

A **different** `Idempotency-Key` creates a distinct order.

## 2. GM.Mediator — dedup a redelivered message

`POST /api/v1/payments/deliver` stands in for a RabbitMQ consumer. `ProcessPayment` opts in via its
`MessageId` (`IIdempotentRequest`), so `IdempotencyBehavior` dedups a redelivery before the handler
runs:

```bash
# Deliver a message
curl -s -X POST http://localhost:5000/api/v1/payments/deliver \
  -H "Content-Type: application/json" \
  -d '{"messageId":"msg-1","account":"acc-123","amount":42.50}'
# → { "result": { "paymentId":"pay_…", "amount":42.50, "handlerRunNumber":1 }, "totalHandlerExecutions":1 }

# Redeliver the SAME messageId — handler does NOT run again
curl -s -X POST http://localhost:5000/api/v1/payments/deliver \
  -H "Content-Type: application/json" \
  -d '{"messageId":"msg-1","account":"acc-123","amount":42.50}'
# → same paymentId + handlerRunNumber:1, totalHandlerExecutions STILL 1
```

`GET /` shows the live counters (`ordersActuallyCreated`, `paymentsActuallyHandled`).

## Endpoints

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/` | Live counters + a hint for which backend is active |
| `POST` | `/api/v1/orders` | Idempotency-guarded order creation (HTTP integration) |
| `POST` | `/api/v1/payments/deliver` | Dedup'd payment delivery (GM.Mediator integration) |
| `GET` | `/health/live` | Liveness probe — no downstream checks |
| `GET` | `/health/ready` | Readiness probe — runs registered health checks |

## Cross-instance idempotency (Redis)

In-memory dedup only works within one process. To make idempotency hold across replicas, point the
sample at Redis — **both** the cache and the lock switch together (idempotency is only correct when
both are shared):

```bash
# via env var (no code/config change needed)
Redis__ConnectionString=localhost:6379 dotnet run --project GM.Idempotency.Sample.API
```

or uncomment `Redis:ConnectionString` in
[`appsettings.json`](GM.Idempotency.Sample.API/appsettings.json). `GET /` reports the active
`backend` (`in-memory` vs `redis`). The wiring is the whole change — see
[`Program.cs`](GM.Idempotency.Sample.API/Program.cs):

```csharp
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    builder.Services.AddGMRedisCaching(o => { o.ConnectionString = redisConnection; o.KeyPrefix = "sample:"; });
    builder.Services.AddGMRedisDistributedLock(o => o.ConnectionString = redisConnection);
}
else // self-contained default
{
    builder.Services.AddGMCaching();
    builder.Services.AddGMDistributedLock();
}
```

## Tests

```bash
dotnet test
```

`tests/GM.Idempotency.Sample.Tests` drives both flows through the real pipeline with
`WebApplicationFactory` — proving one execution + replay on the HTTP path and once-per-message-id on
the Mediator path.
