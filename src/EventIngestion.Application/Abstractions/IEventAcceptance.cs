using EventIngestion.Domain.Events;

namespace EventIngestion.Application.Abstractions;

public interface IEventAcceptance
{
    bool RequiresMessageId { get; }
    Task<AcceptanceReceipt?> AcceptAsync(EventEnvelope message, DateTimeOffset? requestedOccurredAtUtc, CancellationToken cancellationToken);
}

public sealed record AcceptanceReceipt(string MessageId, string CorrelationId, string Acceptance);
