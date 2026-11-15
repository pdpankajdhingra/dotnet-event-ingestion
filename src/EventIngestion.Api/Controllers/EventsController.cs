using System.Diagnostics;
using System.Security.Claims;
using EventIngestion.Application.Events;
using EventIngestion.Domain.Events;
using Microsoft.AspNetCore.Mvc;

namespace EventIngestion.Api.Controllers;

[ApiController]
[Route("api/events")]
public sealed class EventsController(
    IngestEventHandler ingestEventHandler,
    ILogger<EventsController> logger) : ControllerBase
{
    private const string TenantClaimType = "tenant_id";
    private const string CorrelationHeaderName = "X-Correlation-ID";

    [HttpPost]
    [ProducesResponseType<IngestEventResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> IngestAsync(
        [FromBody] EventEnvelope message,
        CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return Unauthorized();
        }

        var tenantId = User.FindFirstValue(TenantClaimType);
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return Forbid();
        }

        if (!string.IsNullOrWhiteSpace(message.TenantId) &&
            !string.Equals(message.TenantId, tenantId, StringComparison.Ordinal))
        {
            return Forbid();
        }

        var validationErrors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(message.EventName))
        {
            validationErrors[nameof(message.EventName)] = ["EventName is required."];
        }

        if (message.Payload.ValueKind == System.Text.Json.JsonValueKind.Undefined)
        {
            validationErrors[nameof(message.Payload)] = ["Payload is required."];
        }

        if (validationErrors.Count > 0)
        {
            foreach (var (field, errors) in validationErrors)
            {
                foreach (var error in errors)
                {
                    ModelState.AddModelError(field, error);
                }
            }

            return ValidationProblem(ModelState);
        }

        var correlationId = GetCorrelationId();
        var eventToPublish = message with
        {
            MessageId = string.IsNullOrWhiteSpace(message.MessageId) ? Guid.NewGuid().ToString("N") : message.MessageId,
            TenantId = tenantId,
            CorrelationId = string.IsNullOrWhiteSpace(message.CorrelationId) ? correlationId : message.CorrelationId,
            OccurredAtUtc = message.OccurredAtUtc?.ToUniversalTime() ?? DateTimeOffset.UtcNow
        };

        try
        {
            await ingestEventHandler.HandleAsync(eventToPublish, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to publish event {MessageId} for tenant {TenantId}",
                eventToPublish.MessageId,
                eventToPublish.TenantId);
            throw;
        }

        logger.LogInformation(
            "Published event {MessageId} ({EventName}) for tenant {TenantId} with correlation {CorrelationId}",
            eventToPublish.MessageId,
            eventToPublish.EventName,
            eventToPublish.TenantId,
            eventToPublish.CorrelationId);

        return Accepted(new IngestEventResponse(eventToPublish.MessageId!, eventToPublish.CorrelationId!));
    }

    private string GetCorrelationId()
    {
        var correlationId = Request.Headers[CorrelationHeaderName].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            return correlationId;
        }

        return Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
    }
}

public sealed record IngestEventResponse(string MessageId, string CorrelationId);
