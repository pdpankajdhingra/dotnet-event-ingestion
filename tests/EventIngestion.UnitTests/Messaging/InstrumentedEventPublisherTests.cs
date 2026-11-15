using System.Diagnostics;
using EventIngestion.Application.Abstractions;
using EventIngestion.Domain.Events;
using EventIngestion.Infrastructure.Messaging;

namespace EventIngestion.UnitTests.Messaging;

public sealed class InstrumentedEventPublisherTests
{
    [Fact]
    public async Task PublishAsync_EmitsProducerActivityAndForwardsTheMessage()
    {
        Activity? publishedActivity = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == EventIngestionTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => publishedActivity = activity
        };
        ActivitySource.AddActivityListener(listener);

        var message = new EventEnvelope { EventName = "DeviceRegistered", TenantId = "tenant-1" };
        var innerPublisher = new RecordingPublisher();
        var publisher = new InstrumentedEventPublisher(innerPublisher, new HealthyBrokerProbe());

        await publisher.PublishAsync(message, CancellationToken.None);

        Assert.Same(message, innerPublisher.PublishedMessage);
        Assert.NotNull(publishedActivity);
        Assert.Equal(ActivityKind.Producer, publishedActivity.Kind);
        Assert.Equal(ActivityStatusCode.Ok, publishedActivity.Status);
        Assert.Equal("RabbitMQ", publishedActivity.GetTagItem("messaging.system"));
        Assert.Equal("DeviceRegistered", publishedActivity.GetTagItem("event.name"));
    }

    [Fact]
    public async Task PublishAsync_RecordsErrorAndRethrowsPublisherFailure()
    {
        Activity? publishedActivity = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == EventIngestionTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => publishedActivity = activity
        };
        ActivitySource.AddActivityListener(listener);
        var expectedException = new InvalidOperationException("Publish failed.");
        var publisher = new InstrumentedEventPublisher(
            new RecordingPublisher(expectedException),
            new HealthyBrokerProbe());

        var actualException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => publisher.PublishAsync(new EventEnvelope { EventName = "DeviceRegistered" }, CancellationToken.None));

        Assert.Same(expectedException, actualException);
        Assert.NotNull(publishedActivity);
        Assert.Equal(ActivityStatusCode.Error, publishedActivity.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName, publishedActivity.GetTagItem("error.type"));
    }

    private sealed class RecordingPublisher(Exception? exceptionToThrow = null) : IEventPublisher
    {
        public EventEnvelope? PublishedMessage { get; private set; }

        public Task PublishAsync(EventEnvelope message, CancellationToken cancellationToken)
        {
            PublishedMessage = message;
            return exceptionToThrow is null ? Task.CompletedTask : Task.FromException(exceptionToThrow);
        }
    }

    private sealed class HealthyBrokerProbe : IBrokerHealthProbe
    {
        public string ProviderName => "RabbitMQ";

        public Task CheckHealthAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
