using System.Text.Json;
using EventIngestion.Domain.Events;
using EventIngestion.Infrastructure.Messaging.AzureServiceBus;
using Microsoft.Extensions.Options;

namespace EventIngestion.IntegrationTests.Messaging;

public sealed class AzureServiceBusEventPublisherIntegrationTests
{
    [AzureServiceBusFact]
    public async Task PublishAsync_CompletesSuccessfullyForConfiguredEntity()
    {
        var options = new AzureServiceBusOptions
        {
            ConnectionString = Environment.GetEnvironmentVariable("EVENTING_AZURE_SERVICEBUS_CONNECTION_STRING"),
            FullyQualifiedNamespace = Environment.GetEnvironmentVariable("EVENTING_AZURE_SERVICEBUS_NAMESPACE"),
            EntityName = Environment.GetEnvironmentVariable("EVENTING_AZURE_SERVICEBUS_ENTITY_NAME")!,
            EntityType = Environment.GetEnvironmentVariable("EVENTING_AZURE_SERVICEBUS_ENTITY_TYPE") ?? "Queue"
        };
        await using var publisher = new AzureServiceBusEventPublisher(Options.Create(options));
        using var payloadDocument = JsonDocument.Parse("{\"source\":\"optional-integration-test\"}");
        var message = new EventEnvelope
        {
            MessageId = Guid.NewGuid().ToString("N"),
            EventName = "ServiceBusPublisherIntegrationTest",
            TenantId = "integration-test",
            CorrelationId = Guid.NewGuid().ToString("N"),
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Payload = payloadDocument.RootElement.Clone()
        };

        await publisher.PublishAsync(message, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(message.MessageId));
    }
}
