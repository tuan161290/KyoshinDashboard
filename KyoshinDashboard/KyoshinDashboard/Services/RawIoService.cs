using Kyoshin.Plc;
using KyoshinDashboard.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace KyoshinDashboard.Services
{
    /// <summary>
    /// Latest unfiltered inputs X from the agent (POST /api/ingest/raw), for the RAW IO page.
    /// Kept in memory only (nothing goes to the database) and pushed to the browsers that have the page
    /// open (SignalR group "rawio", event "RawIo").
    /// </summary>
    public class RawIoService
    {
        public const string Group = "rawio";

        private readonly IHubContext<MachineStatusHub> _hub;
        private readonly object _lock = new();
        private RawIoRequest? _latest;
        private DateTime _receivedAt;

        public RawIoService(IHubContext<MachineStatusHub> hub) => _hub = hub;

        public Task UpdateAsync(RawIoRequest request)
        {
            lock (_lock)
            {
                _latest = request;
                _receivedAt = DateTime.UtcNow;
            }
            return _hub.Clients.Group(Group).SendAsync("RawIo", ToPayload(request, _receivedAt));
        }

        /// <summary>Latest inputs for a browser that just opened the page; null before the agent has sent any.</summary>
        public object? GetCurrentPayload()
        {
            lock (_lock)
                return _latest == null ? null : ToPayload(_latest, _receivedAt);
        }

        // ageSeconds: how old the reading already is, so the page can tell a stale one — agent stopped, or its
        // polling stuck while it still re-sends the last reading (the agent runs on this PC: same clock)
        private static object ToPayload(RawIoRequest r, DateTime receivedAt) => new
        {
            mode = r.Mode,
            time = r.Time.ToString("HH:mm:ss.f"),
            ageSeconds = Math.Max(0, Math.Max((DateTime.UtcNow - receivedAt).TotalSeconds, (DateTimeOffset.Now - r.Time).TotalSeconds)),
            stations = r.Stations.Select(s => new { station = s.Station, words = s.Words })
        };
    }
}
