using System.Reflection;
using GM.Caching;
using GM.Caching.Redis;
using GM.DistributedLock;
using GM.DistributedLock.Redis;
using GM.Idempotency;
using GM.Idempotency.Http;
using GM.Idempotency.Mediator;
using GM.Idempotency.Sample.API;
using GM.Mediator;
using GM.Mediator.Contracts;

var builder = WebApplication.CreateBuilder(args);

// Storage + concurrency for the dedup. Idempotency is only correct across instances when BOTH the
// cache and the lock are shared, so the two always move together:
//   • no Redis configured → in-memory (self-contained; single process only)
//   • Redis:ConnectionString set → Redis-backed (dedup works across every replica)
// Set it via appsettings, or an env var:  Redis__ConnectionString=localhost:6379
var redisConnection = builder.Configuration.GetSection("Redis")["ConnectionString"];
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    builder.Services.AddGMRedisCaching(o =>
    {
        o.ConnectionString = redisConnection;
        o.KeyPrefix = "sample:"; // isolate this app's keys in a shared Redis
    });
    builder.Services.AddGMRedisDistributedLock(o => o.ConnectionString = redisConnection);
}
else
{
    builder.Services.AddGMCaching();
    builder.Services.AddGMDistributedLock();
}

// The core idempotency service (cache-backed store, distributed-lock-guarded check-and-set).
builder.Services.AddGMIdempotency(o => o.DefaultTtl = TimeSpan.FromHours(1));

// HTTP integration: reads the Idempotency-Key header and replays stored responses.
builder.Services.AddGMIdempotencyHttp();

// Mediator + the dedup pipeline behavior (registered first so it wraps other behaviors).
builder.Services.AddGMMediator(Assembly.GetExecutingAssembly());
builder.Services.AddGMIdempotencyBehavior();

// Demo state so we can show real executions vs. replays.
builder.Services.AddSingleton<OrderStore>();
builder.Services.AddSingleton<PaymentLog>();

var app = builder.Build();

// Routing must run before the middleware so per-endpoint [Idempotent] metadata is visible.
app.UseRouting();
app.UseGMIdempotency();

app.MapGet("/", (OrderStore orders, PaymentLog payments) => Results.Ok(new
{
    message = "GM.Idempotency sample",
    backend = string.IsNullOrWhiteSpace(redisConnection) ? "in-memory (single process)" : "redis (cross-instance)",
    try_it = new[]
    {
        "POST /orders           with header 'Idempotency-Key: <key>'  (send twice — the 2nd is replayed)",
        "POST /payments/deliver with body { messageId, account, amount } (deliver same id twice — handled once)",
    },
    counters = new { ordersActuallyCreated = orders.CreatedCount, paymentsActuallyHandled = payments.TotalExecutions },
}));

// ---- HTTP integration -------------------------------------------------------------------------
// Guarded automatically: POST + an Idempotency-Key header. A retry with the same key replays the
// original 201 (same ServerToken, and an 'Idempotency-Replayed: true' header) without re-creating.
app.MapPost("/orders", (CreateOrderRequest request, OrderStore store) =>
{
    var order = store.Create(request.Item, request.Quantity);
    return Results.Created($"/orders/{order.Id}", order);
});

// ---- GM.Mediator integration ------------------------------------------------------------------
// Stands in for a RabbitMQ consumer. Delivering the same messageId again (a redelivery) is deduped
// by IdempotencyBehavior before ProcessPaymentHandler runs — so the payment is processed once and
// the redelivery gets the same PaymentResult back.
app.MapPost("/payments/deliver", async (DeliverPaymentRequest request, IMediator mediator, PaymentLog log) =>
{
    var result = await mediator.Send(new ProcessPayment(request.MessageId, request.Account, request.Amount));
    return Results.Ok(new { result, totalHandlerExecutions = log.TotalExecutions });
});

app.Run();

// Exposed so the test project can spin the app up with WebApplicationFactory.
public partial class Program;
