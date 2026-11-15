namespace EventIngestion.Infrastructure.Messaging.RabbitMQ;

public sealed class RabbitMqOptions
{
    public const string SectionName = "Messaging:RabbitMQ";

    public string HostName { get; set; } = "localhost";

    public int Port { get; set; } = 5672;

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string VirtualHost { get; set; } = "/";

    public string ExchangeName { get; set; } = "events";

    public string ExchangeType { get; set; } = "topic";

    public string RoutingKey { get; set; } = "events.ingested";

    public bool UseTls { get; set; } = true;
}
