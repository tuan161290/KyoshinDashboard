namespace KyoshinAgent
{
    /// <summary>
    /// Last resort when the PLC polling loop stops (e.g. a call into the serial driver that never returns, which
    /// PlcPollingService can't give up on, such as opening the port): with no poll for 60 s the agent ends itself
    /// with an error and Windows restarts the service (install-service.cmd sets "restart on failure").
    /// Counts polls rather than comparing clocks, so a clock change or the PC waking from sleep doesn't trigger it.
    /// </summary>
    public sealed class PollingWatchdogService : BackgroundService
    {
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
        private const int MAX_CHECKS_WITHOUT_POLL = 12;  // 60 s

        private readonly ILogger<PollingWatchdogService> _logger;
        private readonly AgentState _state;

        public PollingWatchdogService(ILogger<PollingWatchdogService> logger, AgentState state)
        {
            _logger = logger;
            _state = state;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            long lastCount = _state.PollCount;
            int checksWithoutPoll = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(CheckInterval, stoppingToken);

                long count = _state.PollCount;
                checksWithoutPoll = count == lastCount ? checksWithoutPoll + 1 : 0;
                lastCount = count;
                if (checksWithoutPoll < MAX_CHECKS_WITHOUT_POLL)
                    continue;

                string message = $"PLC polling is stuck: no poll for {checksWithoutPoll * CheckInterval.TotalSeconds:0} s — ending the agent so that Windows restarts it";
                _logger.LogCritical("{Message}", message);
                Environment.FailFast(message);
            }
        }
    }
}
