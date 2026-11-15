namespace EventIngestion.Infrastructure.Messaging.AzureServiceBus;

public sealed class AzureServiceBusOptions
{
    public const string SectionName = "Messaging:AzureServiceBus";

    public string? ConnectionString { get; set; }

    public string? FullyQualifiedNamespace { get; set; }

    public string EntityName { get; set; } = string.Empty;

    public string EntityType { get; set; } = "Topic";
}
