using EventIngestion.Application.Abstractions;
using EventIngestion.Domain.Events;

namespace EventIngestion.Application.Events;

public sealed class IngestEventHandler(IEventPublisher publisher)
{
    private readonly IEventPublisher _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));

    public Task HandleAsync(EventEnvelope message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _publisher.PublishAsync(message, cancellationToken);
    }
}
