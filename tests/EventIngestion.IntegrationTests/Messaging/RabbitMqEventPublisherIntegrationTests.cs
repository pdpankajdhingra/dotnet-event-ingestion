using System.Text;
using System.Text.Json;
using EventIngestion.Domain.Events;
using EventIngestion.Infrastructure.Messaging.RabbitMQ;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace EventIngestion.IntegrationTests.Messaging;

public sealed class RabbitMqEventPublisherIntegrationTests
{
    [RabbitMqFact]
    public async Task PublishAsync_PublishesPersistentEnvelopeAndMetadataToBoundQueue()
    {
        var settings = ReadOptions();
        var exchangeName = $"event-ingestion-it-{Guid.NewGuid():N}";
        var routingKey = $"events.ingested.{Guid.NewGuid():N}";
        var queueName = $"event-ingestion-it-{Guid.NewGuid():N}";
        settings.ExchangeName = exchangeName;
        settings.RoutingKey = routingKey;

        var factory = new ConnectionFactory
        {
            HostName = settings.HostName,
            Port = settings.Port,
            UserName = settings.UserName,
            Password = settings.Password,
            VirtualHost = settings.VirtualHost,
            Ssl =
            {
                Enabled = settings.UseTls,
                ServerName = settings.HostName
            }
        };

        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(exchangeName, "topic", durable: true, autoDelete: false);
        try
        {
            await channel.QueueDeclareAsync(queueName, durable: false, exclusive: true, autoDelete: true);
            await channel.QueueBindAsync(queueName, exchangeName, routingKey);

            await using var publisher = new RabbitMqEventPublisher(Options.Create(settings));
            using var payloadDocument = JsonDocument.Parse("{\"deviceId\":\"rabbit-it-device\"}");
            var message = new EventEnvelope
            {
                MessageId = Guid.NewGuid().ToString("N"),
                EventName = "DeviceRegistered",
                TenantId = "rabbit-it-tenant",
                CorrelationId = Guid.NewGuid().ToString("N"),
                OccurredAtUtc = DateTimeOffset.UtcNow,
                Payload = payloadDocument.RootElement.Clone()
            };

            await publisher.PublishAsync(message, CancellationToken.None);

            var received = await channel.BasicGetAsync(queueName, autoAck: true);
            Assert.NotNull(received);
            Assert.Equal(message.MessageId, received.BasicProperties.MessageId);
            Assert.Equal(message.CorrelationId, received.BasicProperties.CorrelationId);
            Assert.Equal(message.EventName, received.BasicProperties.Type);
            Assert.Equal("application/json", received.BasicProperties.ContentType);
            Assert.True(received.BasicProperties.Persistent);
            Assert.NotNull(received.BasicProperties.Headers);
            Assert.Equal(message.TenantId, Encoding.UTF8.GetString(Assert.IsType<byte[]>(received.BasicProperties.Headers["tenantId"])));
            Assert.Equal(message.OccurredAtUtc.Value.ToUniversalTime().ToString("O"),
                Encoding.UTF8.GetString(Assert.IsType<byte[]>(received.BasicProperties.Headers["occurredAtUtc"])));

            var publishedEnvelope = JsonSerializer.Deserialize<EventEnvelope>(received.Body.Span, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(publishedEnvelope);
            Assert.Equal(message.MessageId, publishedEnvelope.MessageId);
            Assert.Equal(message.EventName, publishedEnvelope.EventName);
            Assert.Equal(message.TenantId, publishedEnvelope.TenantId);
            Assert.Equal("rabbit-it-device", publishedEnvelope.Payload.GetProperty("deviceId").GetString());
        }
        finally
        {
            await channel.ExchangeDeleteAsync(exchangeName);
        }
    }

    private static RabbitMqOptions ReadOptions()
    {
        var port = Environment.GetEnvironmentVariable("EVENTING_RABBITMQ_PORT");
        return new RabbitMqOptions
        {
            HostName = Environment.GetEnvironmentVariable("EVENTING_RABBITMQ_HOST")!,
            Port = string.IsNullOrWhiteSpace(port) ? 5672 : int.Parse(port),
            UserName = Environment.GetEnvironmentVariable("EVENTING_RABBITMQ_USERNAME")!,
            Password = Environment.GetEnvironmentVariable("EVENTING_RABBITMQ_PASSWORD")!,
            VirtualHost = Environment.GetEnvironmentVariable("EVENTING_RABBITMQ_VIRTUAL_HOST") ?? "/",
            UseTls = bool.TryParse(Environment.GetEnvironmentVariable("EVENTING_RABBITMQ_USE_TLS"), out var useTls) && useTls
        };
    }
}
