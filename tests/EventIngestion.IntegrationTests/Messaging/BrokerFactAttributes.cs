using Xunit;

namespace EventIngestion.IntegrationTests.Messaging;

public sealed class RabbitMqFactAttribute : FactAttribute
{
    public RabbitMqFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EVENTING_RABBITMQ_HOST")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EVENTING_RABBITMQ_USERNAME")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EVENTING_RABBITMQ_PASSWORD")))
        {
            Skip = "Set EVENTING_RABBITMQ_HOST, EVENTING_RABBITMQ_USERNAME, and EVENTING_RABBITMQ_PASSWORD to run RabbitMQ integration tests.";
        }
    }
}

public sealed class AzureServiceBusFactAttribute : FactAttribute
{
    public AzureServiceBusFactAttribute()
    {
        var hasConnectionString = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("EVENTING_AZURE_SERVICEBUS_CONNECTION_STRING"));
        var hasNamespace = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("EVENTING_AZURE_SERVICEBUS_NAMESPACE"));
        var hasEntityName = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("EVENTING_AZURE_SERVICEBUS_ENTITY_NAME"));

        if ((!hasConnectionString && !hasNamespace) || !hasEntityName)
        {
            Skip = "Set EVENTING_AZURE_SERVICEBUS_CONNECTION_STRING or EVENTING_AZURE_SERVICEBUS_NAMESPACE, and EVENTING_AZURE_SERVICEBUS_ENTITY_NAME to run Azure Service Bus integration tests.";
        }
    }
}
