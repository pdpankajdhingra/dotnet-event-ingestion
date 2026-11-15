using System.Text.Json;
using EventIngestion.Application.Abstractions;
using EventIngestion.Application.Events;
using EventIngestion.Domain.Events;

namespace EventIngestion.UnitTests.Events;

public sealed class IngestEventHandlerTests
{
    [Fact]
    public async Task HandleAsync_PublishesTheSameMessageAndCancellationToken()
    {
        using var payloadDocument = JsonDocument.Parse("{\"device\":{\"id\":\"device-42\"},\"active\":true}");
        var message = new EventEnvelope
        {
            MessageId = "message-1",
            EventName = "DeviceRegistered",
            TenantId = "tenant-1",
            CorrelationId = "correlation-1",
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Payload = payloadDocument.RootElement.Clone()
        };
        using var cancellationSource = new CancellationTokenSource();
        var publisher = new RecordingEventPublisher();
        var handler = new IngestEventHandler(publisher);

        await handler.HandleAsync(message, cancellationSource.Token);

        Assert.Same(message, publisher.PublishedMessage);
        Assert.Equal(cancellationSource.Token, publisher.PublishedCancellationToken);
        Assert.Equal("device-42", publisher.PublishedMessage!.Payload.GetProperty("device").GetProperty("id").GetString());
    }

    [Fact]
    public async Task HandleAsync_PropagatesPublisherFailure()
    {
        var expectedException = new InvalidOperationException("Publish failed.");
        var handler = new IngestEventHandler(new RecordingEventPublisher(expectedException));
        var message = new EventEnvelope { EventName = "DeviceRegistered", TenantId = "tenant-1" };

        var actualException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(message, CancellationToken.None));

        Assert.Same(expectedException, actualException);
    }

    [Fact]
    public async Task HandleAsync_PropagatesCancellationFromPublisher()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var publisher = new RecordingEventPublisher();
        var handler = new IngestEventHandler(publisher);
        var message = new EventEnvelope { EventName = "DeviceRegistered", TenantId = "tenant-1" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.HandleAsync(message, cancellationSource.Token));
        Assert.Equal(cancellationSource.Token, publisher.PublishedCancellationToken);
    }

    private sealed class RecordingEventPublisher(Exception? exceptionToThrow = null) : IEventPublisher
    {
        public EventEnvelope? PublishedMessage { get; private set; }

        public CancellationToken PublishedCancellationToken { get; private set; }

        public Task PublishAsync(EventEnvelope message, CancellationToken cancellationToken)
        {
            PublishedMessage = message;
            PublishedCancellationToken = cancellationToken;

            if (exceptionToThrow is not null)
            {
                return Task.FromException(exceptionToThrow);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            return Task.CompletedTask;
        }
    }
}
