using System.Text.Json;
using EventIngestion.Application.Abstractions;
using EventIngestion.Domain.Events;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace EventIngestion.Infrastructure.Outbox;

public sealed class PostgresOutbox(NpgsqlDataSource dataSource, ILogger<PostgresOutbox> logger) : IEventAcceptance
{
    public bool RequiresMessageId => true;

    public async Task<AcceptanceReceipt?> AcceptAsync(EventEnvelope message, DateTimeOffset? requestedOccurredAtUtc, CancellationToken cancellationToken)
    {
        var content = JsonSerializer.Serialize(new { message.EventName, message.Payload, OccurredAtUtc = requestedOccurredAtUtc?.ToUniversalTime() });
        var envelope = JsonSerializer.Serialize(message);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO event_ingestion_outbox (tenant_id, message_id, content, envelope)
            VALUES (@tenant, @id, @content, @envelope)
            ON CONFLICT (tenant_id, message_id) DO NOTHING
            RETURNING envelope
            """, connection);
        insert.Parameters.AddWithValue("tenant", message.TenantId);
        insert.Parameters.AddWithValue("id", message.MessageId!);
        insert.Parameters.AddWithValue("content", NpgsqlDbType.Jsonb, content);
        insert.Parameters.AddWithValue("envelope", NpgsqlDbType.Jsonb, envelope);
        var inserted = await insert.ExecuteScalarAsync(cancellationToken);
        if (inserted is string insertedEnvelope)
        {
            return Receipt(insertedEnvelope);
        }

        await using var select = new NpgsqlCommand("""
            SELECT envelope FROM event_ingestion_outbox
            WHERE tenant_id = @tenant AND message_id = @id AND content = @content
            """, connection);
        select.Parameters.AddWithValue("tenant", message.TenantId);
        select.Parameters.AddWithValue("id", message.MessageId!);
        select.Parameters.AddWithValue("content", NpgsqlDbType.Jsonb, content);
        var existing = await select.ExecuteScalarAsync(cancellationToken);
        return existing is string existingEnvelope ? Receipt(existingEnvelope) : null;
    }

    public async Task CheckHealthAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT tenant_id, message_id, content, envelope, published_at, next_attempt_at, attempts FROM event_ingestion_outbox LIMIT 0");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    }

    public async Task<bool> PublishNextAsync(IEventPublisher publisher, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var select = new NpgsqlCommand("""
            SELECT tenant_id, message_id, envelope FROM event_ingestion_outbox
            WHERE published_at IS NULL AND next_attempt_at <= now()
            ORDER BY next_attempt_at, created_at
            FOR UPDATE SKIP LOCKED LIMIT 1
            """, connection, transaction);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return false;
        }
        var tenant = reader.GetString(0);
        var id = reader.GetString(1);
        var message = JsonSerializer.Deserialize<EventEnvelope>(reader.GetString(2))
            ?? throw new InvalidOperationException("Outbox event could not be deserialized.");
        await reader.DisposeAsync();

        try
        {
            await publisher.PublishAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to publish outbox event {MessageId} for tenant {TenantId}; retrying.", id, tenant);
            await using var failed = new NpgsqlCommand("""
                UPDATE event_ingestion_outbox SET attempts = attempts + 1, next_attempt_at = now() + interval '30 seconds'
                WHERE tenant_id = @tenant AND message_id = @id
                """, connection, transaction);
            failed.Parameters.AddWithValue("tenant", tenant);
            failed.Parameters.AddWithValue("id", id);
            await failed.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        await using var update = new NpgsqlCommand("""
            UPDATE event_ingestion_outbox SET published_at = now(), attempts = attempts + 1
            WHERE tenant_id = @tenant AND message_id = @id
            """, connection, transaction);
        update.Parameters.AddWithValue("tenant", tenant);
        update.Parameters.AddWithValue("id", id);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static AcceptanceReceipt Receipt(string envelope)
    {
        var message = JsonSerializer.Deserialize<EventEnvelope>(envelope)
            ?? throw new InvalidOperationException("Outbox event could not be deserialized.");
        return new AcceptanceReceipt(message.MessageId!, message.CorrelationId!, "durable");
    }
}
