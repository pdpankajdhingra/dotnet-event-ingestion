using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventIngestion.Api.Controllers;
using EventIngestion.Domain.Events;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EventIngestion.IntegrationTests.Api;

public sealed class EventsEndpointTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public EventsEndpointTests()
    {
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task GetHealthLive_ReturnsHealthyWithoutCheckingBroker()
    {
        _factory.BrokerHealthProbe.Failure = new InvalidOperationException("Broker unavailable.");

        using var response = await _client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetHealthReady_ReturnsHealthyWhenBrokerIsHealthy()
    {
        using var response = await _client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetHealthReady_ReturnsServiceUnavailableWhenBrokerIsUnhealthy()
    {
        _factory.BrokerHealthProbe.Failure = new InvalidOperationException("Broker unavailable.");

        using var response = await _client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task PostEvents_UsesTrustedTenantAndCompletesPublishBeforeAcceptedResponse()
    {
        using var payloadDocument = JsonDocument.Parse("{\"deviceId\":\"device-42\"}");
        var message = new EventEnvelope
        {
            EventName = "DeviceRegistered",
            Payload = payloadDocument.RootElement.Clone()
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/events")
        {
            Content = JsonContent.Create(message)
        };
        request.Headers.Add("X-Test-Tenant", "trusted-tenant");
        request.Headers.Add("X-Correlation-ID", "correlation-42");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var responseBody = await response.Content.ReadFromJsonAsync<IngestEventResponse>();
        Assert.NotNull(responseBody);
        Assert.False(string.IsNullOrWhiteSpace(responseBody.MessageId));
        Assert.Equal("correlation-42", responseBody.CorrelationId);
        Assert.Equal("published", responseBody.Acceptance);

        var published = Assert.Single(_factory.Publisher.PublishedMessages);
        Assert.Equal(responseBody.MessageId, published.MessageId);
        Assert.Equal("DeviceRegistered", published.EventName);
        Assert.Equal("trusted-tenant", published.TenantId);
        Assert.Equal("correlation-42", published.CorrelationId);
        Assert.NotNull(published.OccurredAtUtc);
        Assert.Equal("device-42", published.Payload.GetProperty("deviceId").GetString());
    }

    [Fact]
    public async Task PostEvents_RejectsMissingAuthentication()
    {
        using var response = await _client.PostAsJsonAsync("/api/events", new EventEnvelope
        {
            EventName = "DeviceRegistered",
            TenantId = "untrusted-tenant",
            Payload = JsonDocument.Parse("{}").RootElement.Clone()
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_factory.Publisher.PublishedMessages);
    }

    [Fact]
    public async Task PostEvents_RejectsTenantMismatch()
    {
        using var request = CreateRequest(new EventEnvelope
        {
            EventName = "DeviceRegistered",
            TenantId = "different-tenant",
            Payload = JsonDocument.Parse("{}").RootElement.Clone()
        });
        request.Headers.Add("X-Test-Tenant", "trusted-tenant");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.Publisher.PublishedMessages);
    }

    [Fact]
    public async Task PostEvents_RejectsMissingRequiredFields()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/events")
        {
            Content = JsonContent.Create(new { TenantId = "trusted-tenant" })
        };
        request.Headers.Add("X-Test-Tenant", "trusted-tenant");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(_factory.Publisher.PublishedMessages);
    }

    [Fact]
    public async Task PostEvents_ReturnsServerErrorWhenPublisherFails()
    {
        using var request = CreateRequest(new EventEnvelope
        {
            EventName = "FailToPublish",
            Payload = JsonDocument.Parse("{}").RootElement.Clone()
        });
        request.Headers.Add("X-Test-Tenant", "trusted-tenant");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Single(_factory.Publisher.PublishedMessages);
    }

    private static HttpRequestMessage CreateRequest(EventEnvelope message) => new(HttpMethod.Post, "/api/events")
    {
        Content = JsonContent.Create(message)
    };

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
