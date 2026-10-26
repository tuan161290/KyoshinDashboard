namespace KyoshinAgent
{
    /// <summary>appsettings.json "Plc" section.</summary>
    public class PlcOptions
    {
        public string ComPort { get; set; } = "COM5";
        public int BaudRate { get; set; } = 38400;
        public int PollingIntervalMs { get; set; } = 500;

        /// <summary>true = simulated PLC data (testing without hardware). Never used as a fallback.</summary>
        public bool UseMock { get; set; }

        /// <summary>
        /// ON confirmation: an error signal turning on, or a machine changing to a lamp colour, must read the same
        /// for this long before it is confirmed and sent, stamped with the confirmation time
        /// (ON at 10:00:00, still ON at 10:00:03 → ON from 10:00:03). 0 = at once.
        /// </summary>
        public double? OnConfirmSeconds { get; set; }

        /// <summary>OFF confirmation: an error signal turning off, or a machine going dark (Off). 0 = at once.</summary>
        public double? OffConfirmSeconds { get; set; }

        /// <summary>Older single setting for both directions, used when On/OffConfirmSeconds are not set.</summary>
        public double StableSeconds { get; set; } = 3;

        public double OnSeconds => OnConfirmSeconds ?? StableSeconds;
        public double OffSeconds => OffConfirmSeconds ?? StableSeconds;

        /// <summary>
        /// Batch read command: "WR" (all FX series, default) or "QR" (FX3U/FX3UC only, used by FXDataCommnunication).
        /// </summary>
        public string Command { get; set; } = "WR";

        /// <summary>Must match the PLC's Computer Link "Sum Check" setting (PLC parameter / D8120).</summary>
        public bool SumCheck { get; set; }
    }

    /// <summary>appsettings.json "Server" section.</summary>
    public class ServerOptions
    {
        /// <summary>Base URL of the dashboard server, e.g. http://127.0.0.1:5000 (KyoshinServer on this machine)</summary>
        public string Url { get; set; } = "";

        /// <summary>Must equal Ingest__ApiKey configured on the server.</summary>
        public string ApiKey { get; set; } = "";

        /// <summary>Send an (empty) heartbeat at least this often so the server knows the factory is online.</summary>
        public int HeartbeatSeconds { get; set; } = 10;

        public int BatchSize { get; set; } = 500;
    }

    /// <summary>appsettings.json "Agent" section.</summary>
    public class AgentOptions
    {
        /// <summary>Local SQLite file holding the agent id and the queue of not-yet-sent events (relative to the exe folder).</summary>
        public string DataFile { get; set; } = "agent.db";
    }

    /// <summary>Live status shared between the PLC poller and the uploader (reported to the server).</summary>
    public class AgentState
    {
        private volatile string _mode = "starting";
        private volatile int _stationsOk;

        public string Mode { get => _mode; set => _mode = value; }
        public int StationsOk { get => _stationsOk; set => _stationsOk = value; }
    }
}
