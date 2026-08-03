using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GM.Idempotency.Sample.Tests;

// Spins up the real sample app in-memory (in-memory cache + lock) and drives the two integration
// points end to end — no external infra needed.
public class IdempotencySampleTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Http_SameIdempotencyKey_ReplaysTheOriginalResponse()
    {
        var client = factory.CreateClient();
        var key = Guid.NewGuid().ToString();

        async Task<(HttpResponseMessage Response, JsonElement Body)> PostOrder()
        {
            var msg = new HttpRequestMessage(HttpMethod.Post, "/orders")
            {
                Content = JsonContent.Create(new { item = "widget", quantity = 3 }),
            };
            msg.Headers.Add("Idempotency-Key", key);
            var response = await client.SendAsync(msg);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return (response, doc.RootElement.Clone());
        }

        var first = await PostOrder();
        var second = await PostOrder();

        Assert.Equal(HttpStatusCode.Created, first.Response.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.Response.StatusCode);

        // The replay is flagged and returns the identical server-minted token and order id.
        Assert.False(first.Response.Headers.Contains("Idempotency-Replayed"));
        Assert.True(second.Response.Headers.Contains("Idempotency-Replayed"));
        Assert.Equal(
            first.Body.GetProperty("serverToken").GetString(),
            second.Body.GetProperty("serverToken").GetString());
        Assert.Equal(
            first.Body.GetProperty("id").GetString(),
            second.Body.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Http_DifferentKeys_CreateDistinctOrders()
    {
        var client = factory.CreateClient();

        async Task<string?> Create(string key)
        {
            var msg = new HttpRequestMessage(HttpMethod.Post, "/orders")
            {
                Content = JsonContent.Create(new { item = "gadget", quantity = 1 }),
            };
            msg.Headers.Add("Idempotency-Key", key);
            var response = await client.SendAsync(msg);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("id").GetString();
        }

        var a = await Create(Guid.NewGuid().ToString());
        var b = await Create(Guid.NewGuid().ToString());

        Assert.NotNull(a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task Mediator_RedeliveredMessage_IsHandledOnce_AndReplayed()
    {
        var client = factory.CreateClient();
        var messageId = Guid.NewGuid().ToString();
        var payload = new { messageId, account = "acc-123", amount = 42.50m };

        async Task<JsonElement> Deliver()
        {
            var response = await client.PostAsJsonAsync("/payments/deliver", payload);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.Clone();
        }

        var first = await Deliver();
        var redelivery = await Deliver();

        // Same paymentId on the redelivery — the handler's result was replayed, not recomputed.
        Assert.Equal(
            first.GetProperty("result").GetProperty("paymentId").GetString(),
            redelivery.GetProperty("result").GetProperty("paymentId").GetString());

        // Same run number too: the handler executed once for this message id.
        Assert.Equal(
            first.GetProperty("result").GetProperty("handlerRunNumber").GetInt32(),
            redelivery.GetProperty("result").GetProperty("handlerRunNumber").GetInt32());
    }
}
