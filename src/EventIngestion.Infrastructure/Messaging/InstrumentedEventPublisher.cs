using System.Diagnostics;
using EventIngestion.Application.Abstractions;
using EventIngestion.Domain.Events;

namespace EventIngestion.Infrastructure.Messaging;

public sealed class InstrumentedEventPublisher(
    IEventPublisher innerPublisher,
    IBrokerHealthProbe brokerHealthProbe) : IEventPublisher, IBrokerHealthProbe
{
    public string ProviderName => brokerHealthProbe.ProviderName;

    public Task CheckHealthAsync(CancellationToken cancellationToken) =>
        brokerHealthProbe.CheckHealthAsync(cancellationToken);

    public async Task PublishAsync(EventEnvelope message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var startedAt = Stopwatch.GetTimestamp();
        using var activity = EventIngestionTelemetry.ActivitySource.StartActivity(
            "event.publish",
            ActivityKind.Producer);
        activity?.SetTag("messaging.system", ProviderName);
        activity?.SetTag("messaging.operation.name", "publish");
        activity?.SetTag("event.name", message.EventName);

        try
        {
            await innerPublisher.PublishAsync(message, cancellationToken);
            EventIngestionTelemetry.PublishedEvents.Add(1, new KeyValuePair<string, object?>("messaging.system", ProviderName));
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception exception)
        {
            EventIngestionTelemetry.FailedEvents.Add(1, new KeyValuePair<string, object?>("messaging.system", ProviderName));
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            activity?.SetTag("error.type", exception.GetType().FullName);
            throw;
        }
        finally
        {
            EventIngestionTelemetry.PublishDuration.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                new KeyValuePair<string, object?>("messaging.system", ProviderName));
        }
    }
}
