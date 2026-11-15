using System.Text.Json;

namespace EventIngestion.Domain.Events;

public sealed record EventEnvelope
{
    public string? MessageId { get; init; }

    public string EventName { get; init; } = string.Empty;

    public string TenantId { get; init; } = string.Empty;

    public string? CorrelationId { get; init; }

    public DateTimeOffset? OccurredAtUtc { get; init; }

    public JsonElement Payload { get; init; }
}
