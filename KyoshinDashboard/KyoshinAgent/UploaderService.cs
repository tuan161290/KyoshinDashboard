using System.Net;
using System.Net.Http.Json;
using Kyoshin.Plc;
using Microsoft.Extensions.Options;

namespace KyoshinAgent
{
    /// <summary>
    /// Sends queued events to the server (POST {Server:Url}/api/ingest), oldest first, and deletes them
    /// only after the server confirms (IngestResponse.LastSeq). With nothing queued it still sends an
    /// empty heartbeat every Server:HeartbeatSeconds. While the server is unreachable, events stay in
    /// the local queue and are sent later in the original order with their original timestamps.
    /// </summary>
    public sealed class UploaderService : BackgroundService
    {
        public const string HttpClientName = "server";
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

        private readonly ILogger<UploaderService> _logger;
        private readonly ServerOptions _options;
        private readonly OutboxStore _outbox;
        private readonly AgentState _state;
        private readonly IHttpClientFactory _httpClientFactory;

        public UploaderService(ILogger<UploaderService> logger, IOptions<ServerOptions> options,
            OutboxStore outbox, AgentState state, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _options = options.Value;
            _outbox = outbox;
            _state = state;
            _httpClientFactory = httpClientFactory;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!Uri.TryCreate(_options.Url, UriKind.Absolute, out var baseUri))
            {
                _logger.LogError("Server:Url is not set or invalid ('{Url}') — events are only queued locally. " +
                    "Set it in appsettings.json and restart the agent.", _options.Url);
                return;
            }
            if (string.IsNullOrWhiteSpace(_options.ApiKey))
                _logger.LogError("Server:ApiKey is empty — the server will reject the data");

            var ingestUri = new Uri(baseUri, "api/ingest");
            var heartbeat = TimeSpan.FromSeconds(Math.Max(1, _options.HeartbeatSeconds));
            var client = _httpClientFactory.CreateClient(HttpClientName);

            DateTime lastSuccess = DateTime.MinValue;
            bool connected = false;
            string? lastProblem = null;  // log each problem once, not on every retry
            int failures = 0;

            _logger.LogInformation("Uploading to {Url} ({Count} events waiting in the queue)", ingestUri, _outbox.Count());

            while (!stoppingToken.IsCancellationRequested)
            {
                var batch = _outbox.Peek(_options.BatchSize);
                if (batch.Count == 0 && DateTime.UtcNow - lastSuccess < heartbeat)
                {
                    await Task.Delay(500, stoppingToken);
                    continue;
                }

                string? problem;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, ingestUri)
                    {
                        Content = JsonContent.Create(new IngestRequest
                        {
                            AgentId = _outbox.AgentId,
                            Mode = _state.Mode,
                            StationsOk = _state.StationsOk,
                            Events = batch
                        })
                    };
                    request.Headers.Add("X-Api-Key", _options.ApiKey);

                    using var response = await client.SendAsync(request, stoppingToken);
                    if (response.IsSuccessStatusCode)
                    {
                        var result = await response.Content.ReadFromJsonAsync<IngestResponse>(stoppingToken);
                        if (result != null)
                            _outbox.RemoveUpTo(result.LastSeq);

                        lastSuccess = DateTime.UtcNow;
                        failures = 0;
                        if (!connected)
                            _logger.LogInformation("Connected to server — {Count} events still queued", _outbox.Count());
                        connected = true;
                        lastProblem = null;
                        continue; // more queued? send the next batch right away
                    }

                    problem = response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized => "Server rejected the API key (401) — Server:ApiKey must equal the server's Ingest__ApiKey",
                        HttpStatusCode.Forbidden => "Server refused this IP (403) — check Ingest__AllowedIps on the server",
                        HttpStatusCode.ServiceUnavailable => "Server has no Ingest__ApiKey configured (503)",
                        _ => $"Server answered {(int)response.StatusCode} {response.ReasonPhrase}"
                    };
                }
                catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !stoppingToken.IsCancellationRequested)
                {
                    problem = $"Cannot reach server {baseUri} ({ex.Message})";
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // e.g. Server:Url points at another web site that answers with HTML — keep running, keep the queue
                    problem = $"Unexpected answer from {baseUri} ({ex.GetType().Name}: {ex.Message})";
                }

                connected = false;
                if (problem != lastProblem)
                {
                    _logger.LogWarning("{Problem} — events stay queued ({Count} waiting)", problem, _outbox.Count());
                    lastProblem = problem;
                }

                failures++;
                var backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, failures)));
                await Task.Delay(backoff, stoppingToken);
            }
        }
    }
}
