using EventIngestion.Application.Abstractions;
using EventIngestion.Infrastructure.Messaging;
using EventIngestion.Infrastructure.Messaging.AzureServiceBus;
using EventIngestion.Infrastructure.Messaging.RabbitMQ;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EventIngestion.UnitTests.Messaging;

public sealed class MessagingServiceCollectionExtensionsTests
{
    [Theory]
    [InlineData("RabbitMQ")]
    [InlineData("rabbitmq")]
    public async Task AddMessaging_RegistersRabbitMqPublisherAndIgnoresInactiveAzureSettings(string provider)
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Messaging:Provider"] = provider,
            ["Messaging:RabbitMQ:HostName"] = "localhost",
            ["Messaging:RabbitMQ:Port"] = "5672",
            ["Messaging:RabbitMQ:UserName"] = "test-user",
            ["Messaging:RabbitMQ:Password"] = "test-password",
            ["Messaging:RabbitMQ:ExchangeName"] = "events",
            ["Messaging:RabbitMQ:RoutingKey"] = "events.ingested"
        });
        var services = new ServiceCollection();
        services.AddMessaging(configuration);
        await using var serviceProvider = services.BuildServiceProvider();

        var publisher = serviceProvider.GetRequiredService<IEventPublisher>();

        Assert.IsType<InstrumentedEventPublisher>(publisher);
        Assert.Same(publisher, serviceProvider.GetRequiredService<IBrokerHealthProbe>());
        Assert.Equal("RabbitMQ", serviceProvider.GetRequiredService<IBrokerHealthProbe>().ProviderName);
    }

    [Fact]
    public async Task AddMessaging_RegistersAzurePublisherAndIgnoresInactiveRabbitSettings()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Messaging:Provider"] = "AzureServiceBus",
            ["Messaging:AzureServiceBus:FullyQualifiedNamespace"] = "example.servicebus.windows.net",
            ["Messaging:AzureServiceBus:EntityName"] = "events",
            ["Messaging:AzureServiceBus:EntityType"] = "Queue"
        });
        var services = new ServiceCollection();
        services.AddMessaging(configuration);
        await using var serviceProvider = services.BuildServiceProvider();

        var publisher = serviceProvider.GetRequiredService<IEventPublisher>();

        Assert.IsType<InstrumentedEventPublisher>(publisher);
        Assert.Same(publisher, serviceProvider.GetRequiredService<IBrokerHealthProbe>());
        Assert.Equal("Azure Service Bus", serviceProvider.GetRequiredService<IBrokerHealthProbe>().ProviderName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Kafka")]
    public void AddMessaging_RejectsMissingOrUnsupportedProvider(string? provider)
    {
        var settings = new Dictionary<string, string?>();
        if (provider is not null)
        {
            settings["Messaging:Provider"] = provider;
        }

        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddMessaging(CreateConfiguration(settings)));

        Assert.Contains(
            string.IsNullOrWhiteSpace(provider) ? "Messaging:Provider" : "Unsupported messaging provider",
            exception.Message);
    }

    [Theory]
    [InlineData("RabbitMQ")]
    [InlineData("AzureServiceBus")]
    public void AddMessaging_FailsWhenSelectedProviderConfigurationIsInvalid(string provider)
    {
        var services = new ServiceCollection();
        services.AddMessaging(CreateConfiguration(new Dictionary<string, string?>
        {
            ["Messaging:Provider"] = provider
        }));
        using var serviceProvider = services.BuildServiceProvider();

        var exception = provider == "RabbitMQ"
            ? Assert.Throws<OptionsValidationException>(
                () => serviceProvider.GetRequiredService<IOptions<RabbitMqOptions>>().Value)
            : Assert.Throws<OptionsValidationException>(
                () => serviceProvider.GetRequiredService<IOptions<AzureServiceBusOptions>>().Value);

        Assert.Contains(provider == "RabbitMQ" ? "RabbitMQ requires" : "Azure Service Bus requires", exception.Message);
    }

    private static IConfiguration CreateConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
