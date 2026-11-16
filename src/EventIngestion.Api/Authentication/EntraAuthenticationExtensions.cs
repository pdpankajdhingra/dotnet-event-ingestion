using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace EventIngestion.Api.Authentication;

public static class EntraAuthenticationExtensions
{
    public static IServiceCollection AddEntraAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var tenantId = configuration["Entra:TenantId"];
        var clientId = configuration["Entra:ClientId"];
        if (string.IsNullOrWhiteSpace(tenantId) && string.IsNullOrWhiteSpace(clientId))
        {
            services.AddAuthentication();
            return services;
        }

        if (!Guid.TryParse(tenantId, out var tenantGuid) || !Guid.TryParse(clientId, out var clientGuid))
        {
            throw new InvalidOperationException("Entra:TenantId and Entra:ClientId must both be valid GUIDs.");
        }

        var expectedTenant = tenantGuid.ToString();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = $"https://login.microsoftonline.com/{expectedTenant}/v2.0";
                options.Audience = clientGuid.ToString();
                options.MapInboundClaims = false;
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var identity = context.Principal?.Identity as ClaimsIdentity;
                        var tid = context.Principal?.FindFirst("tid")?.Value;
                        var scope = context.Principal?.FindFirst("scp")?.Value;
                        var hasDelegatedScope = scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .Contains("Event.Ingest", StringComparer.Ordinal) == true;
                        var hasApplicationRole = context.Principal?.HasClaim("idtyp", "app") == true &&
                            context.Principal.HasClaim("roles", "Event.Ingest");
                        if (identity is null ||
                            !Guid.TryParse(tid, out var tokenTenant) || tokenTenant != tenantGuid ||
                            (!hasDelegatedScope && !hasApplicationRole))
                        {
                            context.Fail("The token is not authorized to ingest events for this tenant.");
                            return Task.CompletedTask;
                        }

                        foreach (var claim in identity.FindAll("tenant_id").ToArray())
                        {
                            identity.RemoveClaim(claim);
                        }
                        identity.AddClaim(new Claim("tenant_id", expectedTenant));
                        return Task.CompletedTask;
                    }
                };
            });
        return services;
    }
}
