using EventIngestion.Infrastructure.Messaging.AzureServiceBus;
using Microsoft.Extensions.Options;

namespace EventIngestion.UnitTests.Messaging;

public sealed class AzureServiceBusEventPublisherTests
{
    [Theory]
    [InlineData(null, null, "events", "Topic")]
    [InlineData("Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=name;SharedAccessKey=key", "example.servicebus.windows.net", "events", "Topic")]
    [InlineData("Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=name;SharedAccessKey=key", null, "", "Queue")]
    [InlineData("Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=name;SharedAccessKey=key", null, "events", "Subscription")]
    public void Constructor_RejectsInvalidConfiguration(
        string? connectionString,
        string? fullyQualifiedNamespace,
        string entityName,
        string entityType)
    {
        var options = Options.Create(new AzureServiceBusOptions
        {
            ConnectionString = connectionString,
            FullyQualifiedNamespace = fullyQualifiedNamespace,
            EntityName = entityName,
            EntityType = entityType
        });

        Assert.Throws<InvalidOperationException>(() => new AzureServiceBusEventPublisher(options));
    }
}
