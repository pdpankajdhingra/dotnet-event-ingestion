using System.Diagnostics;
using System.Security.Claims;
using EventIngestion.Application.Abstractions;
using EventIngestion.Domain.Events;
using Microsoft.AspNetCore.Mvc;

namespace EventIngestion.Api.Controllers;

[ApiController]
[Route("api/events")]
public sealed class EventsController(
    IEventAcceptance eventAcceptance,
    ILogger<EventsController> logger) : ControllerBase
{
    private const string TenantClaimType = "tenant_id";
    private const string CorrelationHeaderName = "X-Correlation-ID";

    [HttpPost]
    [ProducesResponseType<IngestEventResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
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

        if (eventAcceptance.RequiresMessageId && string.IsNullOrWhiteSpace(message.MessageId))
        {
            validationErrors[nameof(message.MessageId)] = ["MessageId is required for durable acceptance."];
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

        AcceptanceReceipt? receipt;
        try
        {
            receipt = await eventAcceptance.AcceptAsync(eventToPublish, message.OccurredAtUtc, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to accept event {MessageId} for tenant {TenantId}",
                eventToPublish.MessageId,
                eventToPublish.TenantId);
            throw;
        }

        if (receipt is null)
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "MessageId is already associated with a different event."
            });
        }

        logger.LogInformation(
            "Accepted event {MessageId} ({EventName}) for tenant {TenantId} with correlation {CorrelationId}; acceptance {Acceptance}",
            receipt.MessageId,
            eventToPublish.EventName,
            eventToPublish.TenantId,
            receipt.CorrelationId,
            receipt.Acceptance);

        return Accepted(new IngestEventResponse(receipt.MessageId, receipt.CorrelationId, receipt.Acceptance));
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

public sealed record IngestEventResponse(string MessageId, string CorrelationId, string Acceptance);
