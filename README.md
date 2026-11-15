# Event Ingestion Service

A .NET 10 ASP.NET Core API that accepts JSON event envelopes and publishes them through a broker-independent application boundary. The current Infrastructure adapters are Azure Service Bus and RabbitMQ; provider selection is configuration-driven.

## Architecture

```text
HTTP client
	|
	v
EventIngestion.Api  --->  EventIngestion.Application  --->  IEventPublisher
								  |                              ^
								  v                              |
						   EventIngestion.Domain        EventIngestion.Infrastructure
															  |             |
															  v             v
													  Azure Service Bus   RabbitMQ
```

Dependency rules:

- **Domain** has no project dependencies. It owns the `EventEnvelope` contract.
- **Application** depends on Domain and defines `IEventPublisher` and `IngestEventHandler`. It has no broker SDK references.
- **Infrastructure** depends on Application and Domain and contains provider-specific adapters and registration.
- **API** is the composition root and depends on Application and Infrastructure.

```text
src/
  EventIngestion.Api/             ASP.NET Core API and composition root
  EventIngestion.Application/     Ingestion handler and publisher port
  EventIngestion.Domain/          Event contract
  EventIngestion.Infrastructure/  Azure Service Bus and RabbitMQ adapters
 tests/
  EventIngestion.UnitTests/       Handler, adapter configuration, DI, and telemetry tests
  EventIngestion.IntegrationTests/ API tests and opt-in broker tests
scripts/
  LoadTest.ps1                    Bounded HTTP load test
```

### Ingestion flow

```mermaid
sequenceDiagram
	participant Client
	participant API as ASP.NET Core API
	participant Handler as IngestEventHandler
	participant Publisher as IEventPublisher adapter
	participant Broker as Selected broker
	Client->>API: POST /api/events (JSON envelope)
	API->>API: Validate event and authenticated tenant
	API->>Handler: HandleAsync(event, cancellation token)
	Handler->>Publisher: PublishAsync(event, cancellation token)
	Publisher->>Broker: Send with provider metadata
	Broker-->>Publisher: Provider acknowledgement
	Publisher-->>Handler: Completed
	Handler-->>API: Completed
	API-->>Client: 202 Accepted (messageId, correlationId)
```

## Requirements

- .NET 10 SDK
- Docker Desktop / Docker Engine with Docker Compose (for local RabbitMQ)
- Azure namespace and data-plane sender permissions only when using Azure Service Bus

## Build and test

From the repository root:

```powershell
dotnet restore EventIngestion.sln
dotnet build EventIngestion.sln
dotnet test EventIngestion.sln
```

NuGet package versions are centrally managed in `Directory.Packages.props`. Add or update a `PackageVersion` there, and add versionless `PackageReference` entries to the projects that use the package.

The default test run does not require either broker. Broker-adapter integration tests are opt-in through environment variables; skipped tests are reported by xUnit when configuration is absent.

## Run the API and RabbitMQ in containers

With Docker Desktop or Docker Engine running, from the repository root:

```powershell
docker compose up --build -d
docker compose ps
Invoke-RestMethod 'http://localhost:8080/health/live'
Invoke-RestMethod 'http://localhost:8080/health/ready'
```

Compose builds `event-ingestion-api:local` using the root Dockerfile and starts it after RabbitMQ reports healthy. The API connects to the `rabbitmq` service over the Compose network; port 8080, AMQP, and the RabbitMQ management UI are exposed on loopback only. The multi-stage image uses .NET 10 SDK and ASP.NET Core runtime images and runs the API as a non-root user. HTTP and the default credentials are for local development only; production deployments need TLS and securely supplied credentials.

The readiness check verifies broker connectivity; it does not publish an event. To exercise publishing against the local broker, run the opt-in RabbitMQ adapter integration test described below. An unauthenticated `POST http://localhost:8080/api/events` should return `401`: production authentication is not yet configured, so the containerized API cannot accept authenticated ingestion requests until a trusted authentication scheme is added. Do not use a test-only authentication bypass in production.

To check the unauthenticated response in PowerShell 7:

```powershell
$body = @{ eventName = 'DeviceRegistered'; tenantId = 'tenant-123'; payload = @{ deviceId = 'test' } } | ConvertTo-Json
$response = Invoke-WebRequest 'http://localhost:8080/api/events' -Method Post -ContentType 'application/json' -Body $body -SkipHttpErrorCheck
$response.StatusCode # 401
```

View logs and stop the containers with:

```powershell
docker compose logs api rabbitmq
docker compose down
```

The named RabbitMQ volume survives `down`. RabbitMQ initializes its default user only on a fresh data volume; changing `RABBITMQ_DEFAULT_USER` or `RABBITMQ_DEFAULT_PASS` later does not update an existing user. Manage users in RabbitMQ or, if the data can be discarded, recreate the volume deliberately with `docker compose down -v` and start again.

## Local RabbitMQ without the API container

Start RabbitMQ with the management plugin:

```powershell
docker compose up -d rabbitmq
docker compose ps
```

The Compose service binds to loopback only:

- AMQP: `localhost:5672`
- Management UI: <http://localhost:15672>
- Local Compose default login: `eventing` / `local-development-only`

These defaults are for local development only. Override them with `RABBITMQ_DEFAULT_USER` and `RABBITMQ_DEFAULT_PASS` before starting Compose if needed. The named volume preserves broker data. Stop the service with `docker compose down`; add `-v` only when intentionally deleting local broker data.

In the same PowerShell session used to launch the API, configure matching credentials:

```powershell
$env:Messaging__Provider = 'RabbitMQ'
$env:Messaging__RabbitMQ__HostName = 'localhost'
$env:Messaging__RabbitMQ__Port = '5672'
$env:Messaging__RabbitMQ__UserName = 'eventing'
$env:Messaging__RabbitMQ__Password = 'local-development-only'
$env:Messaging__RabbitMQ__VirtualHost = '/'
$env:Messaging__RabbitMQ__ExchangeName = 'events'
$env:Messaging__RabbitMQ__ExchangeType = 'topic'
$env:Messaging__RabbitMQ__RoutingKey = 'events.ingested'
$env:Messaging__RabbitMQ__UseTls = 'false'
dotnet run --project src\EventIngestion.Api\EventIngestion.Api.csproj
```

The .NET environment-variable provider maps `__` to `:`. The base appsettings enables TLS; `appsettings.Development.json` disables it for the local Compose broker. Local credentials must not be reused in production.

## Azure Service Bus

Create an Azure Service Bus namespace and a topic or queue (default entity type is `Topic`). Grant the workload identity the **Azure Service Bus Data Sender** role on the narrowest appropriate scope. On Azure-hosted workloads, prefer managed identity; locally, configure an appropriate `DefaultAzureCredential` developer login. Avoid putting shared access keys in source-controlled settings. If a connection string is unavoidable for development, supply it as a secret environment variable or through a secret store.

Managed identity / `DefaultAzureCredential` configuration:

```powershell
$env:Messaging__Provider = 'AzureServiceBus'
$env:Messaging__AzureServiceBus__FullyQualifiedNamespace = 'your-namespace.servicebus.windows.net'
$env:Messaging__AzureServiceBus__EntityName = 'events'
$env:Messaging__AzureServiceBus__EntityType = 'Topic' # or Queue
dotnet run --project src\EventIngestion.Api\EventIngestion.Api.csproj
```

Optional secret-based local connection string configuration (do not commit it):

```powershell
$env:Messaging__Provider = 'AzureServiceBus'
$env:Messaging__AzureServiceBus__ConnectionString = $env:EVENTING_AZURE_SERVICEBUS_CONNECTION_STRING
$env:Messaging__AzureServiceBus__EntityName = 'events'
$env:Messaging__AzureServiceBus__EntityType = 'Topic'
```

Configure exactly one of `ConnectionString` or `FullyQualifiedNamespace`. Startup validates only the selected provider, so the inactive broker does not need credentials or settings.

## API

The endpoint is `POST /api/events`. Example envelope:

```json
{
  "eventName": "DeviceRegistered",
  "tenantId": "tenant-123",
  "correlationId": "request-456",
  "payload": {
	"deviceId": "device-42",
	"registered": true
  }
}
```

`messageId` and `occurredAtUtc` are generated when omitted. Tenant identity is taken from the authenticated `tenant_id` claim; an absent or conflicting request tenant is rejected. Do not put bearer tokens or other credentials in event payloads.

On completion of the selected adapter's send operation, the API returns `202 Accepted` with a receipt:

```json
{
  "messageId": "generated-or-supplied-id",
  "correlationId": "request-456"
}
```

**Authentication integration is not yet configured in this sample.** The API intentionally fails closed if no authentication scheme produces an authenticated principal and trusted `tenant_id` claim. Before using the endpoint, register the deployment's validated JWT/OIDC or other trusted authentication scheme; do not treat a caller-controlled header as a tenant claim. The integration tests use a test-only authentication handler and do not provide production authentication.

## Health, tracing, and metrics

- `GET /health/live` reports API process liveness and does not probe the broker.
- `GET /health/ready` includes the selected broker health check, with a five-second timeout.
- OpenTelemetry captures ASP.NET Core traces and runtime metrics. The messaging instrumentation emits a producer span, `eventing.publish.successes`, `eventing.publish.failures`, and `eventing.publish.duration`.
- Set `OTEL_EXPORTER_OTLP_ENDPOINT` to enable OTLP export. Without it, the application runs without an OTLP exporter.

The readiness probe creates/checks provider resources without sending an event. Health endpoints should be restricted or handled according to the hosting environment's network and information-disclosure policy.

## Broker adapter integration tests (opt-in)

### RabbitMQ

Use the Compose service, or provide another disposable RabbitMQ instance. The test creates a uniquely named exchange, routing key, and exclusive queue, publishes a message, then verifies the received envelope, metadata, persistence flag, and routing. Set these variables in the same shell used for `dotnet test`:

```powershell
$env:EVENTING_RABBITMQ_HOST = 'localhost'
$env:EVENTING_RABBITMQ_PORT = '5672'
$env:EVENTING_RABBITMQ_USERNAME = 'eventing'
$env:EVENTING_RABBITMQ_PASSWORD = 'local-development-only'
$env:EVENTING_RABBITMQ_VIRTUAL_HOST = '/'
$env:EVENTING_RABBITMQ_USE_TLS = 'false'
dotnet test tests\EventIngestion.IntegrationTests\EventIngestion.IntegrationTests.csproj --filter FullyQualifiedName~RabbitMqEventPublisherIntegrationTests
```

### Azure Service Bus

Set either a connection string or namespace. For the namespace option, `DefaultAzureCredential` must be able to authenticate and have sender access. The named entity must already exist. The test verifies the provider send completes successfully; it does not consume from the entity.

Use one of these authentication options, not both:

```powershell
# Option A: connection string supplied by your secret manager
$env:EVENTING_AZURE_SERVICEBUS_CONNECTION_STRING = $env:YOUR_SECRET_VARIABLE
Remove-Item Env:EVENTING_AZURE_SERVICEBUS_NAMESPACE -ErrorAction SilentlyContinue
```

```powershell
# Option B: managed identity / DefaultAzureCredential
$env:EVENTING_AZURE_SERVICEBUS_NAMESPACE = 'your-namespace.servicebus.windows.net'
Remove-Item Env:EVENTING_AZURE_SERVICEBUS_CONNECTION_STRING -ErrorAction SilentlyContinue
```

Then set the entity and run the test:

```powershell
$env:EVENTING_AZURE_SERVICEBUS_ENTITY_NAME = 'events'
$env:EVENTING_AZURE_SERVICEBUS_ENTITY_TYPE = 'Topic' # or Queue
dotnet test tests\EventIngestion.IntegrationTests\EventIngestion.IntegrationTests.csproj --filter FullyQualifiedName~AzureServiceBusEventPublisherIntegrationTests
```

Do not run integration tests against production entities. The RabbitMQ test publishes a uniquely identified event; the Azure test sends a uniquely identified event into the configured entity.

## HTTP load test

The script exercises the HTTP API, not the broker. For example:

```powershell
.\scripts\LoadTest.ps1 `
	-Endpoint 'https://localhost:7001/api/events' `
	-Count 1000 `
	-Concurrency 20 `
	-BatchSize 50 `
	-EventName 'DeviceRegistered' `
	-TenantId 'tenant-123' `
	-BearerToken '<token>' `
	-WarmupCount 20 `
	-TimeoutSeconds 30 `
	-PayloadJson '{"deviceId":"load-test"}'
```

Use a valid token accepted by the authentication scheme configured in your deployment. For local HTTPS development, trust the ASP.NET Core development certificate or use the API's HTTP URL.

`Concurrency` bounds simultaneous requests. `BatchSize` bounds each request group before progress is reported. Warm-up requests run separately and are excluded from measured statistics. The script reports total measured wall-clock time, successful HTTP status responses, status-code counts, throughput (`measured request count / measured elapsed seconds`), and average/P95/P99 request latency. A publish-confirmed result requires the API's `202 Accepted` response and a receipt whose `messageId` and `correlationId` match that request. Other 2xx responses count as HTTP successes but do not confirm publishing. Any HTTP failure or unconfirmed publish makes the script exit nonzero.

## Reliability and delivery considerations

- **Azure Service Bus:** the SDK retries transient send failures with exponential backoff; the send task completes when the service accepts the send. A timeout after broker acceptance can leave the caller uncertain, and retrying an event can create duplicates. Configure broker-side duplicate detection if appropriate, but this sample does not implement application idempotency.
- **RabbitMQ:** the adapter declares a durable exchange, publishes persistent messages, enables publisher-confirm tracking, and uses mandatory publishing. A successful publish task represents broker publisher confirmation/routing according to RabbitMQ.Client's confirm behavior; it does not prove a consumer processed the message. Durability also depends on queue type, broker persistence, and cluster configuration. A connection loss around confirmation can make the outcome ambiguous and lead to duplicates on retry.
- Neither provider nor the API promises exactly-once delivery. Consumers should be idempotent and use `MessageId` where useful. Application-level idempotency and an outbox are future enhancements.
- Provider acknowledgement is not equivalent to downstream business processing. `202` means the selected publish operation completed according to the adapter's broker acknowledgement semantics.

## Current limitations and future enhancements

- Production authentication / JWT validation is not wired; the API currently requires a trusted authenticated principal with a `tenant_id` claim and fails closed otherwise.
- No application idempotency store, outbox, consumer, or dead-letter processing is included.
- Kafka and Azure Event Grid are future extension points and have no placeholder implementations.
- Broker integration tests require isolated broker resources and are skipped unless their environment configuration is present.
- Future work may include production identity integration, richer broker readiness checks, idempotency/outbox support, CI workflows, and optional sustained-load profiles.
