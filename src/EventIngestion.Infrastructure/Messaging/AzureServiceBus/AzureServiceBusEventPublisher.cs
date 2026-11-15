using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using EventIngestion.Application.Abstractions;
using EventIngestion.Domain.Events;
using EventIngestion.Infrastructure.Messaging;
using Microsoft.Extensions.Options;

namespace EventIngestion.Infrastructure.Messaging.AzureServiceBus;

public sealed class AzureServiceBusEventPublisher : IEventPublisher, IBrokerHealthProbe, IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ServiceBusClient _client;
    private readonly ServiceBusSender _sender;

    public AzureServiceBusEventPublisher(IOptions<AzureServiceBusOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value;
        Validate(settings);

        var clientOptions = new ServiceBusClientOptions
        {
            RetryOptions = new ServiceBusRetryOptions
            {
                Mode = ServiceBusRetryMode.Exponential,
                MaxRetries = 3,
                Delay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(30)
            }
        };

        _client = string.IsNullOrWhiteSpace(settings.ConnectionString)
            ? new ServiceBusClient(
                settings.FullyQualifiedNamespace!,
                new DefaultAzureCredential(),
                clientOptions)
            : new ServiceBusClient(settings.ConnectionString, clientOptions);
        _sender = _client.CreateSender(settings.EntityName);
    }

    public string ProviderName => "Azure Service Bus";

    public async Task CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (_sender.IsClosed)
        {
            throw new InvalidOperationException("The Azure Service Bus sender is closed.");
        }

        using var batch = await _sender.CreateMessageBatchAsync(cancellationToken);
    }

    public async Task PublishAsync(EventEnvelope message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var body = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
        var serviceBusMessage = new ServiceBusMessage(BinaryData.FromBytes(body))
        {
            ContentType = "application/json",
            MessageId = string.IsNullOrWhiteSpace(message.MessageId)
                ? Guid.NewGuid().ToString("N")
                : message.MessageId,
            Subject = message.EventName
        };

        if (!string.IsNullOrWhiteSpace(message.CorrelationId))
        {
            serviceBusMessage.CorrelationId = message.CorrelationId;
        }

        if (!string.IsNullOrWhiteSpace(message.TenantId))
        {
            serviceBusMessage.ApplicationProperties["tenantId"] = message.TenantId;
        }

        if (message.OccurredAtUtc is { } occurredAtUtc)
        {
            serviceBusMessage.ApplicationProperties["occurredAtUtc"] = occurredAtUtc.ToUniversalTime().ToString("O");
        }

        await _sender.SendMessageAsync(serviceBusMessage, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _sender.DisposeAsync();
        }
        finally
        {
            await _client.DisposeAsync();
        }
    }

    private static void Validate(AzureServiceBusOptions settings)
    {
        var hasConnectionString = !string.IsNullOrWhiteSpace(settings.ConnectionString);
        var hasNamespace = !string.IsNullOrWhiteSpace(settings.FullyQualifiedNamespace);

        if (hasConnectionString == hasNamespace)
        {
            throw new InvalidOperationException(
                $"Configure exactly one of {nameof(settings.ConnectionString)} or {nameof(settings.FullyQualifiedNamespace)}.");
        }

        if (string.IsNullOrWhiteSpace(settings.EntityName))
        {
            throw new InvalidOperationException($"{nameof(settings.EntityName)} must be configured.");
        }

        if (!string.Equals(settings.EntityType, "Topic", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(settings.EntityType, "Queue", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Azure Service Bus EntityType must be either 'Topic' or 'Queue'.");
        }
    }
}
