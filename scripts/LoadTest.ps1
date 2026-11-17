[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[ValidateNotNullOrEmpty()]
	[string] $Endpoint,

	[ValidateRange(1, [int]::MaxValue)]
	[int] $Count = 1000,

	[ValidateRange(1, 1024)]
	[int] $Concurrency = 20,

	[ValidateRange(1, [int]::MaxValue)]
	[int] $BatchSize = 50,

	[ValidateNotNullOrEmpty()]
	[string] $EventName = 'DeviceRegistered',

	[ValidateNotNullOrEmpty()]
	[string] $TenantId = 'tenant-load-test',

	[string] $BearerToken,

	[ValidateRange(0, [int]::MaxValue)]
	[int] $WarmupCount = 10,

	[ValidateRange(1, 3600)]
	[int] $TimeoutSeconds = 30,

	[ValidateNotNullOrEmpty()]
	[string] $PayloadJson = '{}'
)

$ErrorActionPreference = 'Stop'

$endpointUri = $null
if (-not [Uri]::TryCreate($Endpoint, [UriKind]::Absolute, [ref] $endpointUri) -or
	$endpointUri.Scheme -notin @('http', 'https')) {
	throw 'Endpoint must be an absolute HTTP or HTTPS URL.'
}

try {
	$payloadDocument = [System.Text.Json.JsonDocument]::Parse($PayloadJson)
	$payloadDocument.Dispose()
}
catch {
	throw "PayloadJson must contain valid JSON: $($_.Exception.Message)"
}

if (-not ('EventIngestion.LoadTest.HttpLoadClient' -as [type])) {
	Add-Type -TypeDefinition @'
#nullable enable
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EventIngestion.LoadTest
{
	public sealed class HttpRequestResult
	{
		public int RequestNumber { get; init; }
		public string MessageId { get; init; } = string.Empty;
		public int? StatusCode { get; init; }
		public bool HttpSuccess { get; init; }
		public bool ReceiptConfirmed { get; init; }
		public string? Acceptance { get; init; }
		public double LatencyMilliseconds { get; init; }
		public string? Failure { get; init; }
	}

	public sealed class IngestReceipt
	{
		public string? MessageId { get; init; }
		public string? CorrelationId { get; init; }
		public string? Acceptance { get; init; }
	}

	public static class HttpLoadClient
	{
		private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
		private static readonly JsonSerializerOptions ReceiptOptions = new() { PropertyNameCaseInsensitive = true };

		public static async Task<HttpRequestResult> SendAsync(
			HttpClient client,
			string endpoint,
			int requestNumber,
			string eventName,
			string tenantId,
			string payloadJson,
			string? bearerToken,
			int timeoutSeconds)
		{
			var messageId = Guid.NewGuid().ToString("N");
			var correlationId = Guid.NewGuid().ToString("N");
			var stopwatch = Stopwatch.StartNew();
			int? statusCode = null;
			var httpSuccess = false;
			var receiptConfirmed = false;
			string? acceptance = null;
			string? failure = null;

			try
			{
				using var payload = JsonDocument.Parse(payloadJson);
				var envelope = new
				{
					messageId,
					eventName,
					tenantId,
					correlationId,
					occurredAtUtc = DateTimeOffset.UtcNow,
					payload = payload.RootElement
				};
				var json = JsonSerializer.Serialize(envelope, SerializerOptions);
				using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
				{
					Content = new StringContent(json, Encoding.UTF8, "application/json")
				};

				if (!string.IsNullOrWhiteSpace(bearerToken))
				{
					request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
				}

				using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
				using var response = await client.SendAsync(
					request,
					HttpCompletionOption.ResponseHeadersRead,
					timeout.Token).ConfigureAwait(false);
				statusCode = (int)response.StatusCode;
				httpSuccess = response.IsSuccessStatusCode;

				if (response.StatusCode == HttpStatusCode.Accepted)
				{
					var responseBody = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
					var receipt = JsonSerializer.Deserialize<IngestReceipt>(responseBody, ReceiptOptions);
					receiptConfirmed = receipt is not null &&
						string.Equals(receipt.MessageId, messageId, StringComparison.Ordinal) &&
						string.Equals(receipt.CorrelationId, correlationId, StringComparison.Ordinal) &&
						(receipt.Acceptance == "published" || receipt.Acceptance == "durable");
					if (receiptConfirmed)
					{
						acceptance = receipt!.Acceptance;
					}
					else
					{
						failure = "HTTP 202 did not contain a matching receipt with a recognized acceptance mode.";
					}
				}
				else if (!httpSuccess)
				{
					var responseBody = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
					failure = string.IsNullOrWhiteSpace(responseBody)
						? $"HTTP {statusCode} {response.ReasonPhrase}"
						: $"HTTP {statusCode} {response.ReasonPhrase}: {Truncate(responseBody, 500)}";
				}
			}
			catch (OperationCanceledException)
			{
				failure = $"Request timed out after {timeoutSeconds} seconds.";
			}
			catch (Exception exception)
			{
				failure = $"{exception.GetType().Name}: {exception.Message}";
			}
			finally
			{
				stopwatch.Stop();
			}

			return new HttpRequestResult
			{
				RequestNumber = requestNumber,
				MessageId = messageId,
				StatusCode = statusCode,
				HttpSuccess = httpSuccess,
				ReceiptConfirmed = receiptConfirmed,
				Acceptance = acceptance,
				LatencyMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
				Failure = failure
			};
		}

		private static string Truncate(string value, int maximumLength) =>
			value.Length <= maximumLength ? value : value.Substring(0, maximumLength) + "...";
	}
}
'@
}

$httpClient = [System.Net.Http.HttpClient]::new()
$httpClient.Timeout = [System.Threading.Timeout]::InfiniteTimeSpan

try {
	$headers = [System.Net.Http.Headers.HttpRequestHeaders] $httpClient.DefaultRequestHeaders
	$headers.Accept.ParseAdd('application/json')

	function Invoke-RequestSet {
		param(
			[Parameter(Mandatory)] [int] $RequestCount,
			[Parameter(Mandatory)] [int] $RequestOffset
		)

		$results = [System.Collections.Generic.List[EventIngestion.LoadTest.HttpRequestResult]]::new()
		$remaining = $RequestCount
		$offset = $RequestOffset

		while ($remaining -gt 0) {
			$windowSize = [Math]::Min($Concurrency, $remaining)
			$tasks = [System.Collections.Generic.List[System.Threading.Tasks.Task[EventIngestion.LoadTest.HttpRequestResult]]]::new()

			for ($index = 0; $index -lt $windowSize; $index++) {
				$requestNumber = $offset + $index + 1
				$tasks.Add([EventIngestion.LoadTest.HttpLoadClient]::SendAsync(
					$httpClient,
					$Endpoint,
					$requestNumber,
					$EventName,
					$TenantId,
					$PayloadJson,
					$BearerToken,
					$TimeoutSeconds))
			}

			[void] [System.Threading.Tasks.Task]::WhenAll($tasks).GetAwaiter().GetResult()
			foreach ($task in $tasks) {
				$results.Add($task.Result)
			}

			$remaining -= $windowSize
			$offset += $windowSize
		}

		return $results
	}

	if ($WarmupCount -gt 0) {
		Write-Host "Running $WarmupCount warm-up request(s) with concurrency $Concurrency..."
		$warmupResults = Invoke-RequestSet -RequestCount $WarmupCount -RequestOffset 0
		$warmupHttpSuccesses = @($warmupResults | Where-Object HttpSuccess).Count
		$warmupConfirmed = @($warmupResults | Where-Object ReceiptConfirmed).Count
		Write-Host "Warm-up complete: HTTP successful $warmupHttpSuccesses/$WarmupCount; matching receipts $warmupConfirmed/$WarmupCount. Warm-up is excluded from measurements."
	}

	Write-Host "Running $Count measured request(s); concurrency $Concurrency, batch size $BatchSize..."
	$measuredResults = [System.Collections.Generic.List[EventIngestion.LoadTest.HttpRequestResult]]::new()
	$measuredStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
	$remainingMeasured = $Count
	$requestOffset = $WarmupCount

	while ($remainingMeasured -gt 0) {
		$currentBatchSize = [Math]::Min($BatchSize, $remainingMeasured)
		$batchResults = Invoke-RequestSet -RequestCount $currentBatchSize -RequestOffset $requestOffset
		foreach ($result in $batchResults) {
			$measuredResults.Add($result)
		}

		$remainingMeasured -= $currentBatchSize
		$requestOffset += $currentBatchSize
		Write-Progress -Activity 'HTTP event ingestion load test' `
			-Status "$($measuredResults.Count) of $Count requests completed" `
			-PercentComplete ([Math]::Min(100, 100 * $measuredResults.Count / $Count))
	}

	$measuredStopwatch.Stop()
	Write-Progress -Activity 'HTTP event ingestion load test' -Completed

	$latencies = @($measuredResults | ForEach-Object LatencyMilliseconds | Sort-Object)
	$httpSuccesses = @($measuredResults | Where-Object HttpSuccess).Count
	$receiptConfirmed = @($measuredResults | Where-Object ReceiptConfirmed).Count
	$published = @($measuredResults | Where-Object { $_.Acceptance -eq 'published' }).Count
	$durable = @($measuredResults | Where-Object { $_.Acceptance -eq 'durable' }).Count
	$httpFailures = @($measuredResults | Where-Object { -not $_.HttpSuccess }).Count
	$unconfirmed = @($measuredResults | Where-Object { -not $_.ReceiptConfirmed }).Count
	$elapsedSeconds = $measuredStopwatch.Elapsed.TotalSeconds
	$throughput = if ($elapsedSeconds -gt 0) { $Count / $elapsedSeconds } else { 0 }
	$averageLatency = if ($latencies.Count -gt 0) { ($latencies | Measure-Object -Average).Average } else { 0 }

	function Get-Percentile {
		param([double[]] $SortedValues, [double] $Percentile)
		if ($SortedValues.Count -eq 0) { return 0 }
		$rank = [Math]::Max(1, [Math]::Ceiling($Percentile * $SortedValues.Count))
		return $SortedValues[$rank - 1]
	}

	$statusCounts = $measuredResults |
		Group-Object { if ($null -eq $_.StatusCode) { 'No HTTP response' } else { [string] $_.StatusCode } } |
		Sort-Object Name

	Write-Host ''
	Write-Host 'Load test summary' -ForegroundColor Cyan
	Write-Host '-----------------'
	Write-Host ("Endpoint:                 {0}" -f $Endpoint)
	Write-Host ("Measured requests:        {0}" -f $Count)
	Write-Host ("Concurrency:              {0}" -f $Concurrency)
	Write-Host ("Batch size:               {0}" -f $BatchSize)
	Write-Host ("Warm-up requests:         {0} (excluded)" -f $WarmupCount)
	Write-Host ("Total measured time:      {0:N2} seconds" -f $elapsedSeconds)
	Write-Host ("Throughput:               {0:N2} requests/second" -f $throughput)
	Write-Host ("HTTP successful (2xx):    {0}/{1}" -f $httpSuccesses, $Count)
	Write-Host ("HTTP failed:              {0}/{1}" -f $httpFailures, $Count)
	Write-Host ("Matching receipts:        {0}/{1}" -f $receiptConfirmed, $Count)
	Write-Host ("Published receipts:       {0}/{1}" -f $published, $Count)
	Write-Host ("Durable receipts:         {0}/{1}" -f $durable, $Count)
	Write-Host ("Unconfirmed receipts:     {0}/{1}" -f $unconfirmed, $Count)
	Write-Host ("Average latency:          {0:N2} ms" -f $averageLatency)
	Write-Host ("P95 latency:              {0:N2} ms" -f (Get-Percentile -SortedValues $latencies -Percentile 0.95))
	Write-Host ("P99 latency:              {0:N2} ms" -f (Get-Percentile -SortedValues $latencies -Percentile 0.99))
	Write-Host ''
	Write-Host 'HTTP status codes:'
	foreach ($statusCount in $statusCounts) {
		Write-Host ("  {0}: {1}" -f $statusCount.Name, $statusCount.Count)
	}

	$failures = @($measuredResults | Where-Object { -not $_.HttpSuccess -or ($_.StatusCode -eq 202 -and -not $_.ReceiptConfirmed) })
	if ($failures.Count -gt 0) {
		Write-Host ''
		Write-Host "Failures (showing up to 20 of $($failures.Count)) :" -ForegroundColor Yellow
		foreach ($failure in ($failures | Select-Object -First 20)) {
			Write-Host ("  Request {0} [{1}]: {2}" -f $failure.RequestNumber, $failure.MessageId, $failure.Failure)
		}
	}

	if ($unconfirmed -gt 0) {
		Write-Host ''
		Write-Host 'A matching receipt requires HTTP 202, matching IDs, and a recognized acceptance mode.'
		Write-Host 'Durable acceptance does not confirm broker publication; other 2xx responses are not matching receipts.'
	}

	if ($httpFailures -gt 0 -or $receiptConfirmed -lt $Count) {
		exit 1
	}
}
finally {
	$httpClient.Dispose()
}
