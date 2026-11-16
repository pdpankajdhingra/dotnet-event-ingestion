using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using EventIngestion.Application.Abstractions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace EventIngestion.IntegrationTests.Api;

public sealed class EntraAuthenticationTests : IDisposable
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Client = "22222222-2222-2222-2222-222222222222";
    private const string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("a test signing key that is at least 256 bits long"));
    private readonly EntraApiFactory _factory = new();
    private readonly HttpClient _client;

    public EntraAuthenticationTests() => _client = _factory.CreateClient();

    [Theory]
    [InlineData("scp", "Event.Ingest")]
    [InlineData("roles", "Event.Ingest")]
    public async Task ValidToken_PublishesUsingValidatedTenant(string claimType, string permission)
    {
        var claims = new List<Claim> { new(claimType, permission), new("tenant_id", "spoofed") };
        if (claimType == "roles")
        {
            claims.Add(new Claim("idtyp", "app"));
        }
        using var request = CreateRequest(CreateToken(claims.ToArray()));
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(Tenant, Assert.Single(_factory.Publisher.PublishedMessages).TenantId);
    }

    [Theory]
    [InlineData("wrong-tenant")]
    [InlineData("wrong-audience")]
    [InlineData("wrong-issuer")]
    [InlineData("no-permission")]
    [InlineData("role-without-app-token")]
    [InlineData("expired")]
    [InlineData("wrong-signature")]
    public async Task InvalidToken_IsRejectedWithoutPublishing(string scenario)
    {
        var claims = scenario switch
        {
            "no-permission" => Array.Empty<Claim>(),
            "role-without-app-token" => [new Claim("roles", "Event.Ingest")],
            _ => [new Claim("scp", "Event.Ingest")]
        };
        using var request = CreateRequest(CreateToken(claims, scenario));
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_factory.Publisher.PublishedMessages);
    }

    private static HttpRequestMessage CreateRequest(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/events")
        {
            Content = JsonContent.Create(new { eventName = "Example", payload = new { value = 1 } })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static string CreateToken(Claim[] additionalClaims, string scenario = "valid")
    {
        var claims = new List<Claim>
        {
            new("tid", scenario == "wrong-tenant" ? "33333333-3333-3333-3333-333333333333" : Tenant)
        };
        claims.AddRange(additionalClaims);
        var key = scenario == "wrong-signature"
            ? new SymmetricSecurityKey(Encoding.UTF8.GetBytes("a different test signing key longer than 256 bits"))
            : SigningKey;
        var token = new JwtSecurityToken(
            issuer: scenario == "wrong-issuer" ? "https://invalid.example" : Issuer,
            audience: scenario == "wrong-audience" ? "other-api" : Client,
            claims: claims,
            expires: scenario == "expired" ? DateTime.UtcNow.AddMinutes(-10) : DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private sealed class EntraApiFactory : WebApplicationFactory<Program>
    {
        public RecordingEventPublisher Publisher { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Entra:TenantId"] = Tenant,
                    ["Entra:ClientId"] = Client,
                    ["Messaging:Provider"] = "RabbitMQ",
                    ["Messaging:RabbitMQ:UserName"] = "test-user",
                    ["Messaging:RabbitMQ:Password"] = "test-password"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEventPublisher>();
                services.AddSingleton<IEventPublisher>(Publisher);
                EventIngestion.Api.Authentication.EntraAuthenticationExtensions.AddEntraAuthentication(
                    services,
                    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Entra:TenantId"] = Tenant,
                        ["Entra:ClientId"] = Client
                    }).Build());
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    var config = new OpenIdConnectConfiguration { Issuer = Issuer };
                    config.SigningKeys.Add(SigningKey);
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
                });
            });
        }
    }
}
