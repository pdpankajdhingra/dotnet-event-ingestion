using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventIngestion.Api.Controllers;
using EventIngestion.Application.Abstractions;
using EventIngestion.Domain.Events;
using EventIngestion.Infrastructure.Outbox;
using EventIngestion.IntegrationTests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace EventIngestion.IntegrationTests.Outbox;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EVENTING_POSTGRES_TEST_CONNECTION")))
        {
            Skip = "Set EVENTING_POSTGRES_TEST_CONNECTION to a dedicated test database initialized with 001-outbox.sql.";
        }
    }
}

[CollectionDefinition("PostgresIntegration", DisableParallelization = true)]
public sealed class PostgresCollection;

[Collection("PostgresIntegration")]
public sealed class PostgresOutboxTests : IAsyncLifetime
{
    private readonly string _tenant = Guid.NewGuid().ToString("N");
    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("EVENTING_POSTGRES_TEST_CONNECTION")!);

    public Task InitializeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task ConcurrentRetriesReturnOriginalReceiptAndConflictingContentIsRejected()
    {
        var outbox = new PostgresOutbox(_dataSource, NullLogger<PostgresOutbox>.Instance);
        var first = Event("id", "{\"a\":1,\"b\":2}", "original");
        var receipts = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => outbox.AcceptAsync(first, null, CancellationToken.None)));
        Assert.All(receipts, receipt =>
        {
            Assert.NotNull(receipt);
            Assert.Equal("original", receipt.CorrelationId);
            Assert.Equal("durable", receipt.Acceptance);
        });
        var retry = Event("id", "{\"b\":2,\"a\":1}", "new-correlation");
        var same = await outbox.AcceptAsync(retry, null, CancellationToken.None);
        Assert.Equal("original", same!.CorrelationId);
        var conflict = await outbox.AcceptAsync(Event("id", "{\"a\":3}", "original"), null, CancellationToken.None);
        Assert.Null(conflict);
        var timestampConflict = await outbox.AcceptAsync(first, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Null(timestampConflict);
        var otherTenant = await outbox.AcceptAsync(first with { TenantId = _tenant + "-other", EventName = "other" }, null, CancellationToken.None);
        Assert.NotNull(otherTenant);
        Assert.Equal(1L, await CountAsync());
    }

    [PostgresFact]
    public async Task DatabaseErrorsAndCancellationAreNotReportedAsAcceptance()
    {
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("EVENTING_POSTGRES_TEST_CONNECTION"))
        {
            Host = "127.0.0.1", Port = 1, Timeout = 1
        };
        await using var unavailable = NpgsqlDataSource.Create(builder.ConnectionString);
        var outbox = new PostgresOutbox(unavailable, NullLogger<PostgresOutbox>.Instance);
        var message = Event("unavailable", "{}", "correlation");
        await Assert.ThrowsAsync<NpgsqlException>(() => outbox.AcceptAsync(message, null, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var actual = new PostgresOutbox(_dataSource, NullLogger<PostgresOutbox>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => actual.AcceptAsync(message, null, cancelled.Token));
        Assert.Equal(0L, await CountAsync());
    }

    [PostgresFact]
    public async Task FailedPublishIsRetriedAndConcurrentWorkersDoNotPublishTheSameRow()
    {
        var outbox = new PostgresOutbox(_dataSource, NullLogger<PostgresOutbox>.Instance);
        var message = Event("retry", "{}", "correlation");
        await outbox.AcceptAsync(message, null, CancellationToken.None);
        var failing = new TestPublisher(true);
        Assert.True(await outbox.PublishNextAsync(failing, CancellationToken.None));
        Assert.Equal(1, failing.Count);
        await using (var command = _dataSource.CreateCommand("UPDATE event_ingestion_outbox SET next_attempt_at = now() WHERE tenant_id = @tenant"))
        {
            command.Parameters.AddWithValue("tenant", _tenant);
            await command.ExecuteNonQueryAsync();
        }
        var successful = new TestPublisher(false);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => outbox.PublishNextAsync(successful, CancellationToken.None)));
        Assert.Equal(1, successful.Count);
        await using var verify = _dataSource.CreateCommand("SELECT attempts, published_at IS NOT NULL FROM event_ingestion_outbox WHERE tenant_id = @tenant");
        verify.Parameters.AddWithValue("tenant", _tenant);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.True(reader.GetBoolean(1));
    }

    [PostgresFact]
    public async Task ApiAcceptsDurablyWhileBrokerIsUnhealthyAndRejectsConflictingReuse()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVENTING_POSTGRES_TEST_CONNECTION")!;
        using var factory = new ApiFactory(connectionString);
        factory.BrokerHealthProbe.Failure = new InvalidOperationException("Broker unavailable");
        using var client = CreateDurableClient(factory, connectionString);
        client.DefaultRequestHeaders.Add("X-Test-Tenant", _tenant);
        var message = Event("api-id", "{}", "first-correlation") with { EventName = "FailToPublish" };
        using var accepted = await client.PostAsJsonAsync("/api/events", message);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var receipt = await accepted.Content.ReadFromJsonAsync<IngestEventResponse>();
        Assert.Equal("durable", receipt!.Acceptance);
        Assert.Equal("first-correlation", receipt.CorrelationId);
        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var duplicate = await client.PostAsJsonAsync("/api/events", message with { CorrelationId = "second-correlation" });
        Assert.Equal("first-correlation", (await duplicate.Content.ReadFromJsonAsync<IngestEventResponse>())!.CorrelationId);
        using var conflict = await client.PostAsJsonAsync("/api/events", message with { EventName = "different" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var missingId = await client.PostAsJsonAsync("/api/events", message with { MessageId = null });
        Assert.Equal(HttpStatusCode.BadRequest, missingId.StatusCode);
        Assert.Equal(1L, await CountAsync());
    }

    [PostgresFact]
    public async Task ApiReturnsErrorsWhenDatabaseIsUnavailable()
    {
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("EVENTING_POSTGRES_TEST_CONNECTION"))
        {
            Host = "127.0.0.1", Port = 1, Timeout = 1
        };
        using var factory = new ApiFactory(builder.ConnectionString);
        using var client = CreateDurableClient(factory, builder.ConnectionString);
        client.DefaultRequestHeaders.Add("X-Test-Tenant", _tenant);
        using var response = await client.PostAsJsonAsync("/api/events", Event("unavailable", "{}", "correlation"));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }

    private static HttpClient CreateDurableClient(ApiFactory factory, string connectionString)
    {
        var previousEnabled = Environment.GetEnvironmentVariable("Outbox__Enabled");
        var previousConnection = Environment.GetEnvironmentVariable("ConnectionStrings__Outbox");
        try
        {
            Environment.SetEnvironmentVariable("Outbox__Enabled", "true");
            Environment.SetEnvironmentVariable("ConnectionStrings__Outbox", connectionString);
            return factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        }
        finally
        {
            Environment.SetEnvironmentVariable("Outbox__Enabled", previousEnabled);
            Environment.SetEnvironmentVariable("ConnectionStrings__Outbox", previousConnection);
        }
    }

    private EventEnvelope Event(string id, string payload, string correlation) => new()
    {
        TenantId = _tenant,
        MessageId = id,
        EventName = "test",
        CorrelationId = correlation,
        OccurredAtUtc = DateTimeOffset.UtcNow,
        Payload = JsonDocument.Parse(payload).RootElement.Clone()
    };

    private async Task<long> CountAsync()
    {
        await using var command = _dataSource.CreateCommand("SELECT count(*) FROM event_ingestion_outbox WHERE tenant_id = @tenant");
        command.Parameters.AddWithValue("tenant", _tenant);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async Task DisposeAsync()
    {
        await using var command = _dataSource.CreateCommand("DELETE FROM event_ingestion_outbox WHERE tenant_id = @tenant OR tenant_id = @other");
        command.Parameters.AddWithValue("tenant", _tenant);
        command.Parameters.AddWithValue("other", _tenant + "-other");
        await command.ExecuteNonQueryAsync();
        await _dataSource.DisposeAsync();
    }

    private sealed class TestPublisher(bool fail) : IEventPublisher
    {
        private int _count;
        public int Count => _count;
        public Task PublishAsync(EventEnvelope message, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return fail ? Task.FromException(new InvalidOperationException("Broker unavailable")) : Task.CompletedTask;
        }
    }
}
