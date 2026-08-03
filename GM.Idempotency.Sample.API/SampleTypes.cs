using System.Collections.Concurrent;
using GM.Idempotency.Mediator;
using GM.Mediator.Contracts;

namespace GM.Idempotency.Sample.API;

// ---------------------------------------------------------------------------------------------
// HTTP demo — request body + in-memory store.
// ---------------------------------------------------------------------------------------------

/// <summary>Body for <c>POST /orders</c>.</summary>
public sealed record CreateOrderRequest(string Item, int Quantity);

/// <summary>A created order. <see cref="ServerToken"/> is minted once per real execution, so a
/// replayed response carries the same token as the original — visible proof the body was replayed.</summary>
public sealed record Order(string Id, string Item, int Quantity, string ServerToken);

/// <summary>Trivial in-memory order store that also counts how often it actually created an order.</summary>
public sealed class OrderStore
{
    private readonly ConcurrentDictionary<string, Order> _orders = new();
    private int _created;

    /// <summary>Number of orders actually created (i.e. real handler executions, not replays).</summary>
    public int CreatedCount => _created;

    public Order Create(string item, int quantity)
    {
        Interlocked.Increment(ref _created);
        var order = new Order(Guid.NewGuid().ToString("N")[..8], item, quantity, Guid.NewGuid().ToString("N"));
        _orders[order.Id] = order;
        return order;
    }
}

// ---------------------------------------------------------------------------------------------
// Mediator demo — a payment command deduped by message id (the RabbitMQ redelivery scenario).
// ---------------------------------------------------------------------------------------------

/// <summary>Body for <c>POST /payments/deliver</c> — stands in for a broker delivering a message.</summary>
public sealed record DeliverPaymentRequest(string MessageId, string Account, decimal Amount);

/// <summary>
/// A command to process a payment. It opts into idempotency with its <see cref="MessageId"/> as the
/// key (<see cref="IIdempotentRequest"/>), so a redelivery of the same message is deduped before the
/// handler runs.
/// </summary>
public sealed record ProcessPayment(string MessageId, string Account, decimal Amount)
    : IRequest<PaymentResult>, IIdempotentRequest
{
    public string IdempotencyKey => MessageId;
}

/// <summary>Result of processing a payment. <see cref="HandlerRunNumber"/> is the ordinal of the real
/// handler execution — a replayed result keeps the original number.</summary>
public sealed record PaymentResult(string PaymentId, decimal Amount, int HandlerRunNumber);

/// <summary>Counts how many times the payment handler actually executed.</summary>
public sealed class PaymentLog
{
    private int _executions;

    /// <summary>Total real handler executions (redeliveries that were deduped are not counted).</summary>
    public int TotalExecutions => _executions;

    public int RecordExecution() => Interlocked.Increment(ref _executions);
}

/// <summary>Handler for <see cref="ProcessPayment"/>. Discovered by <c>AddGMMediator</c>'s scan.</summary>
public sealed class ProcessPaymentHandler : IRequestHandler<ProcessPayment, PaymentResult>
{
    private readonly PaymentLog _log;

    public ProcessPaymentHandler(PaymentLog log) => _log = log;

    public Task<PaymentResult> Handle(ProcessPayment request, CancellationToken cancellationToken)
    {
        // Imagine calling a real payment provider here — the whole point is that this runs at most
        // once per MessageId even if the broker delivers the message several times.
        var run = _log.RecordExecution();
        var result = new PaymentResult($"pay_{Guid.NewGuid():N}"[..12], request.Amount, run);
        return Task.FromResult(result);
    }
}
