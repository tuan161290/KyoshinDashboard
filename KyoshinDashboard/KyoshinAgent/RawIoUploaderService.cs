using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace KyoshinAgent
{
    /// <summary>
    /// Sends the inputs X of the last poll, unfiltered, to the server for the RAW IO page
    /// (POST {Server:Url}/api/ingest/raw): as soon as they change, and at least every 5 s so the page
    /// knows the reading is current. Nothing is queued — while the server is unreachable the reads are
    /// dropped and sending is retried every 5 s. Server problems are logged by UploaderService.
    /// </summary>
    public sealed class RawIoUploaderService : BackgroundService
    {
        private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(5);

        private readonly ILogger<RawIoUploaderService> _logger;
        private readonly ServerOptions _server;
        private readonly PlcOptions _plc;
        private readonly AgentState _state;
        private readonly IHttpClientFactory _httpClientFactory;

        public RawIoUploaderService(ILogger<RawIoUploaderService> logger, IOptions<ServerOptions> server,
            IOptions<PlcOptions> plc, AgentState state, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _server = server.Value;
            _plc = plc.Value;
            _state = state;
            _httpClientFactory = httpClientFactory;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!Uri.TryCreate(_server.Url, UriKind.Absolute, out var baseUri))
                return;

            var rawUri = new Uri(baseUri, "api/ingest/raw");
            var interval = TimeSpan.FromMilliseconds(Math.Max(100, _plc.PollingIntervalMs));
            var client = _httpClientFactory.CreateClient(UploaderService.HttpClientName);

            string? lastSent = null;
            DateTime nextSend = DateTime.MinValue;

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(interval, stoppingToken);

                var raw = _state.RawIo;
                if (raw == null)
                    continue;
                string inputs = string.Join('|', raw.Stations.Select(s => s.Words == null ? "-" : string.Join(',', s.Words)));
                if (inputs == lastSent && DateTime.UtcNow < nextSend)
                    continue;

                bool sent = false;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, rawUri) { Content = JsonContent.Create(raw) };
                    request.Headers.Add("X-Api-Key", _server.ApiKey);
                    using var response = await client.SendAsync(request, stoppingToken);
                    sent = response.IsSuccessStatusCode;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogDebug("Raw inputs not sent: {Message}", ex.Message);
                }

                if (sent)
                {
                    lastSent = inputs;
                    nextSend = DateTime.UtcNow + Refresh;
                }
                else
                {
                    // Server down or refusing: don't retry on every poll
                    await Task.Delay(Refresh, stoppingToken);
                }
            }
        }
    }
}
