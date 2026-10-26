using System.Net;
using System.Security.Cryptography;
using System.Text;
using Kyoshin.Plc;
using KyoshinDashboard.Services;
using Microsoft.AspNetCore.Mvc;

namespace KyoshinDashboard.Controllers
{
    /// <summary>
    /// POST /api/ingest — machine state events from KyoshinAgent at the factory.
    /// Requires header X-Api-Key equal to Ingest:ApiKey (env Ingest__ApiKey). Without a configured key the
    /// endpoint is disabled. Optional Ingest:AllowedIps (comma separated) limits which addresses may send.
    /// </summary>
    [Route("api/ingest")]
    [ApiController]
    public class IngestController : ControllerBase
    {
        private const int MAX_EVENTS_PER_REQUEST = 5000;

        /// <summary>Placeholder in deploy/truenas/docker-compose.yml — never accepted as a real key.</summary>
        public const string PLACEHOLDER_KEY = "DOI-THANH-KHOA-BI-MAT-CUA-BAN";

        private readonly MachineMonitorService _monitor;
        private readonly IConfiguration _config;
        private readonly ILogger<IngestController> _logger;

        public IngestController(MachineMonitorService monitor, IConfiguration config, ILogger<IngestController> logger)
        {
            _monitor = monitor;
            _config = config;
            _logger = logger;
        }

        [HttpPost]
        public async Task<IActionResult> Post([FromBody] IngestRequest request)
        {
            string expectedKey = _config["Ingest:ApiKey"] ?? "";
            if (expectedKey.Length == 0 || expectedKey == PLACEHOLDER_KEY)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Ingest disabled: set Ingest__ApiKey on the server");

            string givenKey = Request.Headers["X-Api-Key"].ToString();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(givenKey), Encoding.UTF8.GetBytes(expectedKey)))
            {
                _logger.LogWarning("Rejected ingest from {Ip}: wrong API key", HttpContext.Connection.RemoteIpAddress);
                return Unauthorized();
            }

            string allowedIps = _config["Ingest:AllowedIps"] ?? "";
            if (allowedIps.Length > 0 && !IsAllowed(HttpContext.Connection.RemoteIpAddress, allowedIps))
            {
                _logger.LogWarning("Rejected ingest from {Ip}: not in Ingest:AllowedIps", HttpContext.Connection.RemoteIpAddress);
                return StatusCode(StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(request.AgentId) || request.AgentId.Length > 64)
                return BadRequest("AgentId is required");
            if (request.Events.Count > MAX_EVENTS_PER_REQUEST)
                return BadRequest($"At most {MAX_EVENTS_PER_REQUEST} events per request");

            long lastSeq = await _monitor.ApplyAsync(request);
            return Ok(new IngestResponse { LastSeq = lastSeq });
        }

        private static bool IsAllowed(IPAddress? remote, string allowedIps)
        {
            if (remote == null) return false;
            if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            return allowedIps
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(ip => IPAddress.TryParse(ip, out var allowed) && allowed.Equals(remote));
        }
    }
}
