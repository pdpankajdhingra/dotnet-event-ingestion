using EventIngestion.Domain.Events;

namespace EventIngestion.Application.Abstractions;

public interface IEventPublisher
{
    Task PublishAsync(
        EventEnvelope message,
        CancellationToken cancellationToken);
}
