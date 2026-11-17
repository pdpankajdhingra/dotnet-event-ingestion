using EventIngestion.Application.Abstractions;
using EventIngestion.Infrastructure.Messaging.AzureServiceBus;
using EventIngestion.Infrastructure.Messaging.RabbitMQ;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EventIngestion.Infrastructure.Messaging;

public static class MessagingServiceCollectionExtensions
{
    public const string ProviderConfigurationKey = "Messaging:Provider";

    public static IServiceCollection AddMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var provider = configuration[ProviderConfigurationKey];
        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new InvalidOperationException($"Configuration value '{ProviderConfigurationKey}' is required.");
        }
        var readinessTags = configuration.GetValue<bool>("Outbox:Enabled") ? Array.Empty<string>() : ["ready"];

        switch (provider.Trim().ToUpperInvariant())
        {
            case "AZURESERVICEBUS":
                services.AddOptions<AzureServiceBusOptions>()
                    .Bind(configuration.GetSection(AzureServiceBusOptions.SectionName))
                    .Validate(IsAzureServiceBusConfigurationValid, AzureServiceBusValidationMessage)
                    .ValidateOnStart();
                services.AddSingleton<AzureServiceBusEventPublisher>();
                services.AddSingleton<InstrumentedEventPublisher>(serviceProvider =>
                {
                    var publisher = serviceProvider.GetRequiredService<AzureServiceBusEventPublisher>();
                    return new InstrumentedEventPublisher(publisher, publisher);
                });
                services.AddSingleton<IEventPublisher>(serviceProvider =>
                    serviceProvider.GetRequiredService<InstrumentedEventPublisher>());
                services.AddSingleton<IBrokerHealthProbe>(serviceProvider =>
                    serviceProvider.GetRequiredService<InstrumentedEventPublisher>());
                services.AddHealthChecks().AddCheck<MessagingHealthCheck>(
                    "messaging",
                    tags: readinessTags,
                    timeout: TimeSpan.FromSeconds(5));
                break;

            case "RABBITMQ":
                services.AddOptions<RabbitMqOptions>()
                    .Bind(configuration.GetSection(RabbitMqOptions.SectionName))
                    .Validate(IsRabbitMqConfigurationValid, RabbitMqValidationMessage)
                    .ValidateOnStart();
                services.AddSingleton<RabbitMqEventPublisher>();
                services.AddSingleton<InstrumentedEventPublisher>(serviceProvider =>
                {
                    var publisher = serviceProvider.GetRequiredService<RabbitMqEventPublisher>();
                    return new InstrumentedEventPublisher(publisher, publisher);
                });
                services.AddSingleton<IEventPublisher>(serviceProvider =>
                    serviceProvider.GetRequiredService<InstrumentedEventPublisher>());
                services.AddSingleton<IBrokerHealthProbe>(serviceProvider =>
                    serviceProvider.GetRequiredService<InstrumentedEventPublisher>());
                services.AddHealthChecks().AddCheck<MessagingHealthCheck>(
                    "messaging",
                    tags: readinessTags,
                    timeout: TimeSpan.FromSeconds(5));
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported messaging provider '{provider}'. Supported providers are AzureServiceBus and RabbitMQ.");
        }

        return services;
    }

    private static bool IsAzureServiceBusConfigurationValid(AzureServiceBusOptions options)
    {
        var hasConnectionString = !string.IsNullOrWhiteSpace(options.ConnectionString);
        var hasNamespace = !string.IsNullOrWhiteSpace(options.FullyQualifiedNamespace);
        return hasConnectionString != hasNamespace &&
               !string.IsNullOrWhiteSpace(options.EntityName) &&
               (string.Equals(options.EntityType, "Topic", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(options.EntityType, "Queue", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRabbitMqConfigurationValid(RabbitMqOptions options) =>
        !string.IsNullOrWhiteSpace(options.HostName) &&
        options.Port is >= 1 and <= 65535 &&
        !string.IsNullOrWhiteSpace(options.UserName) &&
        !string.IsNullOrWhiteSpace(options.Password) &&
        !string.IsNullOrWhiteSpace(options.VirtualHost) &&
        !string.IsNullOrWhiteSpace(options.ExchangeName) &&
        !string.IsNullOrWhiteSpace(options.RoutingKey) &&
        (string.Equals(options.ExchangeType, "direct", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(options.ExchangeType, "fanout", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(options.ExchangeType, "topic", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(options.ExchangeType, "headers", StringComparison.OrdinalIgnoreCase));

    private const string AzureServiceBusValidationMessage =
        "Azure Service Bus requires exactly one connection string or fully qualified namespace, an entity name, and a valid entity type (Topic or Queue).";

    private const string RabbitMqValidationMessage =
        "RabbitMQ requires a host, valid port, username, password, virtual host, exchange name, routing key, and a supported exchange type.";
}
