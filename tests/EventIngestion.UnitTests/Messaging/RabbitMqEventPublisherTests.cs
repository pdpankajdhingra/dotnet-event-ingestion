using EventIngestion.Infrastructure.Messaging.RabbitMQ;
using Microsoft.Extensions.Options;

namespace EventIngestion.UnitTests.Messaging;

public sealed class RabbitMqEventPublisherTests
{
    [Fact]
    public void Constructor_RejectsMissingCredentials()
    {
        var options = Options.Create(new RabbitMqOptions
        {
            UserName = "",
            Password = ""
        });

        Assert.Throws<InvalidOperationException>(() => new RabbitMqEventPublisher(options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Constructor_RejectsInvalidPort(int port)
    {
        var rabbitOptions = ValidOptions();
        rabbitOptions.Port = port;
        var options = Options.Create(rabbitOptions);

        Assert.Throws<InvalidOperationException>(() => new RabbitMqEventPublisher(options));
    }

    [Fact]
    public void Constructor_RejectsUnsupportedExchangeType()
    {
        var rabbitOptions = ValidOptions();
        rabbitOptions.ExchangeType = "stream";
        var options = Options.Create(rabbitOptions);

        Assert.Throws<InvalidOperationException>(() => new RabbitMqEventPublisher(options));
    }

    [Fact]
    public async Task Constructor_AcceptsCompleteConfigurationWithoutConnectingImmediately()
    {
        await using var publisher = new RabbitMqEventPublisher(Options.Create(ValidOptions()));

        Assert.NotNull(publisher);
    }

    private static RabbitMqOptions ValidOptions() => new()
    {
        HostName = "localhost",
        Port = 5672,
        UserName = "guest",
        Password = "guest",
        VirtualHost = "/",
        ExchangeName = "events",
        ExchangeType = "topic",
        RoutingKey = "events.ingested",
        UseTls = false
    };
}
