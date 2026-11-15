using System.Text.Json;
using EventIngestion.Application.Abstractions;
using EventIngestion.Domain.Events;
using EventIngestion.Infrastructure.Messaging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace EventIngestion.Infrastructure.Messaging.RabbitMQ;

public sealed class RabbitMqEventPublisher : IEventPublisher, IBrokerHealthProbe, IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly RabbitMqOptions _options;
    private readonly SemaphoreSlim _channelLock = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;
    private bool _disposed;

    public RabbitMqEventPublisher(IOptions<RabbitMqOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        Validate(_options);
    }

    public string ProviderName => "RabbitMQ";

    public async Task CheckHealthAsync(CancellationToken cancellationToken)
    {
        await _channelLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await EnsureChannelAsync(cancellationToken);
            if (_connection is not { IsOpen: true } || _channel is not { IsOpen: true })
            {
                throw new InvalidOperationException("RabbitMQ connection or channel is not open.");
            }
        }
        finally
        {
            _channelLock.Release();
        }
    }

    public async Task PublishAsync(EventEnvelope message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var body = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
        await _channelLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await EnsureChannelAsync(cancellationToken);

            var properties = new BasicProperties
            {
                Persistent = true,
                ContentType = "application/json",
                MessageId = string.IsNullOrWhiteSpace(message.MessageId)
                    ? Guid.NewGuid().ToString("N")
                    : message.MessageId,
                Type = message.EventName,
                CorrelationId = message.CorrelationId,
                Headers = new Dictionary<string, object?>
                {
                    ["tenantId"] = message.TenantId,
                    ["occurredAtUtc"] = message.OccurredAtUtc?.ToUniversalTime().ToString("O") ?? string.Empty
                }
            };

            await _channel!.BasicPublishAsync(
                exchange: _options.ExchangeName,
                routingKey: _options.RoutingKey,
                mandatory: true,
                basicProperties: properties,
                body: body,
                cancellationToken: cancellationToken);
        }
        finally
        {
            _channelLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _channelLock.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                if (_channel is not null)
                {
                    await _channel.DisposeAsync();
                    _channel = null;
                }
            }
            finally
            {
                if (_connection is not null)
                {
                    await _connection.DisposeAsync();
                    _connection = null;
                }
            }
        }
        finally
        {
            _channelLock.Release();
        }
    }

    private async Task EnsureChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return;
        }

        if (_channel is not null)
        {
            await _channel.DisposeAsync();
            _channel = null;
        }

        if (_connection is not { IsOpen: true })
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }

            var factory = new ConnectionFactory
            {
                HostName = _options.HostName,
                Port = _options.Port,
                UserName = _options.UserName,
                Password = _options.Password,
                VirtualHost = _options.VirtualHost,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                Ssl =
                {
                    Enabled = _options.UseTls,
                    ServerName = _options.HostName
                }
            };
            _connection = await factory.CreateConnectionAsync(cancellationToken);
        }

        try
        {
            _channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                cancellationToken);
            await _channel.ExchangeDeclareAsync(
                exchange: _options.ExchangeName,
                type: _options.ExchangeType,
                durable: true,
                autoDelete: false,
                arguments: null,
                cancellationToken: cancellationToken);
        }
        catch
        {
            if (_channel is not null)
            {
                await _channel.DisposeAsync();
                _channel = null;
            }

            throw;
        }
    }

    private static void Validate(RabbitMqOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.HostName))
        {
            throw new InvalidOperationException($"{nameof(options.HostName)} must be configured.");
        }

        if (options.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"{nameof(options.Port)} must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(options.UserName) || string.IsNullOrWhiteSpace(options.Password))
        {
            throw new InvalidOperationException("RabbitMQ username and password must be configured.");
        }

        if (string.IsNullOrWhiteSpace(options.VirtualHost) ||
            string.IsNullOrWhiteSpace(options.ExchangeName) ||
            string.IsNullOrWhiteSpace(options.RoutingKey))
        {
            throw new InvalidOperationException("RabbitMQ virtual host, exchange name, and routing key must be configured.");
        }

        if (!string.Equals(options.ExchangeType, "direct", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.ExchangeType, "fanout", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.ExchangeType, "topic", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.ExchangeType, "headers", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("RabbitMQ exchange type must be direct, fanout, topic, or headers.");
        }
    }
}
