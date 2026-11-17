using System.Security.Claims;
using System.Text.Encodings.Web;
using EventIngestion.Application.Abstractions;
using EventIngestion.Domain.Events;
using EventIngestion.Infrastructure.Messaging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventIngestion.IntegrationTests.Api;

public sealed class ApiFactory(string? outboxConnectionString = null) : WebApplicationFactory<Program>
{
    public RecordingEventPublisher Publisher { get; } = new();

    public RecordingBrokerHealthProbe BrokerHealthProbe { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Messaging:Provider"] = "RabbitMQ",
                ["Messaging:RabbitMQ:UserName"] = "test-user",
                ["Messaging:RabbitMQ:Password"] = "test-password",
                ["Outbox:Enabled"] = (outboxConnectionString is not null).ToString(),
                ["ConnectionStrings:Outbox"] = outboxConnectionString
            }));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEventPublisher>();
            services.AddSingleton<IEventPublisher>(Publisher);
            services.RemoveAll<IBrokerHealthProbe>();
            services.AddSingleton<IBrokerHealthProbe>(BrokerHealthProbe);
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.TestScheme;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.TestScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.TestScheme,
                    _ => { });
        });
    }
}

public sealed class RecordingBrokerHealthProbe : IBrokerHealthProbe
{
    public string ProviderName => "Test broker";

    public Exception? Failure { get; set; }

    public Task CheckHealthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }
}

public sealed class RecordingEventPublisher : IEventPublisher
{
    private readonly List<EventEnvelope> _publishedMessages = [];

    public IReadOnlyList<EventEnvelope> PublishedMessages => _publishedMessages;

    public Task PublishAsync(EventEnvelope message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _publishedMessages.Add(message);

        if (message.EventName == "FailToPublish")
        {
            throw new InvalidOperationException("Simulated broker failure.");
        }

        return Task.CompletedTask;
    }
}

public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string TestScheme = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var tenantId = Request.Headers["X-Test-Tenant"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new[] { new Claim("tenant_id", tenantId) };
        var identity = new ClaimsIdentity(claims, TestScheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, TestScheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
