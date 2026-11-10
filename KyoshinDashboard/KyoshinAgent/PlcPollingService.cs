using Kyoshin.Plc;
using Microsoft.Extensions.Options;

namespace KyoshinAgent
{
    /// <summary>
    /// Polls the inputs X of both FX stations (or the mock PLC) and queues every machine whose state changed.
    /// A change is queued only once it has been read unchanged for Plc:OnConfirmSeconds / OffConfirmSeconds, stamped with the time it was
    /// confirmed (StableReadingFilter). After the agent starts, each machine's first confirmed state is queued
    /// (a snapshot), so the server is always in sync. Machines of a station that does not answer are
    /// left out — the server keeps their last known state.
    /// </summary>
    public sealed class PlcPollingService : BackgroundService
    {
        private static readonly TimeSpan ReopenInterval = TimeSpan.FromSeconds(10);
        private const int MAX_FAILED_POLLS_BEFORE_REOPEN = 20; // ~10 s at 500 ms: e.g. USB-RS485 adapter replugged

        // A block read ends within ~1 s even when the PLC doesn't answer (3 tries × 300 ms). One still running after
        // this long is stuck in the serial driver and would stop all polling while the heartbeat goes on.
        private static readonly TimeSpan ReadHangTimeout = TimeSpan.FromSeconds(5);

        private readonly ILogger<PlcPollingService> _logger;
        private readonly PlcOptions _options;
        private readonly OutboxStore _outbox;
        private readonly AgentState _state;

        private readonly Dictionary<int, MachineReading> _lastQueued = new();
        private readonly bool?[] _stationOk = new bool?[FxIoMap.Stations.Length];
        private readonly StableReadingFilter _filter;
        private readonly FxReadCommand _command;
        private FxComputerLinkHelper? _plc;
        private MockPlc? _mock;
        private DateTime _nextOpenAttempt = DateTime.MinValue;
        private bool _openFailedLogged;
        private int _failedPolls;

        public PlcPollingService(ILogger<PlcPollingService> logger, IOptions<PlcOptions> options,
            OutboxStore outbox, AgentState state)
        {
            _logger = logger;
            _options = options.Value;
            _outbox = outbox;
            _state = state;
            _filter = new StableReadingFilter(TimeSpan.FromSeconds(_options.OnSeconds), TimeSpan.FromSeconds(_options.OffSeconds));
            _command = string.Equals(_options.Command, "QR", StringComparison.OrdinalIgnoreCase) ? FxReadCommand.QR : FxReadCommand.WR;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_options.UseMock)
            {
                _mock = new MockPlc();
                _state.Mode = "mock";
                _logger.LogWarning("Using MOCK PLC data (Plc:UseMock = true) — simulated data is sent to the server");
            }
            else
            {
                _logger.LogInformation("Reading PLC inputs with {Command}{SumCheck}: status {StatusDevice} + errors {ErrorDevice}, {Words} words each (Plc:Command, Plc:SumCheck)",
                    _command, _options.SumCheck ? " + sum check" : " (no sum check)",
                    FxIoMap.GetBlockDevice(FxIoBlock.Status, _command), FxIoMap.GetBlockDevice(FxIoBlock.Error, _command),
                    FxIoMap.WORDS_PER_BLOCK);
            }

            _logger.LogInformation("PLC polling started every {Interval} ms, a change is sent once stable for {On} s ON / {Off} s OFF (agent {AgentId}, queue file {File})",
                _options.PollingIntervalMs, _options.OnSeconds, _options.OffSeconds, _outbox.AgentId, _outbox.FilePath);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var readings = _mock != null ? ReadMock() : await ReadPlcAsync();

                    // A change is stamped with the time it is confirmed (read unchanged for the ON/OFF time)
                    var now = DateTimeOffset.Now;
                    var changed = _filter.Update(readings, now)
                        .Where(r => !_lastQueued.TryGetValue(r.MachineId, out var previous) || previous != r)
                        .Select(r => (Reading: r, Time: now))
                        .ToList();
                    if (changed.Count > 0)
                    {
                        _outbox.Enqueue(changed);
                        foreach (var c in changed)
                            _lastQueued[c.Reading.MachineId] = c.Reading;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error polling PLC");
                }
                _state.PollDone();

                await Task.Delay(_options.PollingIntervalMs, stoppingToken);
            }

            _plc?.Dispose();
        }

        private List<MachineReading> ReadMock()
        {
            _mock!.RandomStep();
            var readings = new List<MachineReading>();
            var raw = new ushort[]?[FxIoMap.Stations.Length];
            for (int station = 0; station < FxIoMap.Stations.Length; station++)
            {
                var statusWords = _mock.ReadWords(station, FxIoBlock.Status);
                var errorWords = _mock.ReadWords(station, FxIoBlock.Error);
                readings.AddRange(FxIoMap.DecodeStation(station, statusWords, errorWords));
                raw[station] = [.. statusWords, .. errorWords];
            }
            _state.StationsOk = FxIoMap.Stations.Length;
            _state.LastReadAt = DateTimeOffset.Now;
            PublishRaw(raw);
            return readings;
        }

        private async Task<List<MachineReading>> ReadPlcAsync()
        {
            var readings = new List<MachineReading>();
            var raw = new ushort[]?[FxIoMap.Stations.Length];
            if (!EnsurePlcOpen())
            {
                _state.Mode = "plc-offline";
                _state.StationsOk = 0;
                PublishRaw(raw);
                return readings;
            }
            _state.Mode = "plc";

            int ok = 0;
            for (int station = 0; station < FxIoMap.Stations.Length; station++)
            {
                string stationNo = FxIoMap.Stations[station];
                string statusDevice = FxIoMap.GetBlockDevice(FxIoBlock.Status, _command);
                string errorDevice = FxIoMap.GetBlockDevice(FxIoBlock.Error, _command);
                var (statusWords, statusProblem) = await ReadBlockAsync(stationNo, statusDevice);
                var (errorWords, errorProblem) = await ReadBlockAsync(stationNo, errorDevice);

                bool answered = statusWords != null && errorWords != null;
                if (_stationOk[station] != answered)
                {
                    // Log only when a station goes online/offline, not on every poll — with the PLC's actual answer
                    if (answered)
                        _logger.LogInformation("FX PLC station {Station} is responding ({Status} + {Error})", stationNo, statusDevice, errorDevice);
                    else
                        _logger.LogWarning("FX PLC station {Station} read failed — {Reason}. Its machines keep their last state",
                            stationNo, statusWords == null ? $"{statusDevice}: {statusProblem}" : $"{errorDevice}: {errorProblem}");
                    _stationOk[station] = answered;
                }

                if (answered)
                {
                    readings.AddRange(FxIoMap.DecodeStation(station, statusWords!, errorWords!));
                    raw[station] = [.. statusWords!, .. errorWords!];
                    ok++;
                }
            }
            _state.StationsOk = ok;
            if (ok > 0)
                _state.LastReadAt = DateTimeOffset.Now;
            PublishRaw(raw);

            // Nothing answers for a while: close and reopen the port (adapter unplugged/replugged)
            _failedPolls = ok == 0 ? _failedPolls + 1 : 0;
            if (_failedPolls >= MAX_FAILED_POLLS_BEFORE_REOPEN && _plc != null)
            {
                _logger.LogWarning("No station answered for {Count} polls — reopening {ComPort}", _failedPolls, _options.ComPort);
                _plc.Dispose();
                _plc = null;
                _failedPolls = 0;
            }
            return readings;
        }

        /// <summary>
        /// Read one 5-word block; (null, reason) if it fails. A read stuck in the serial driver is given up after
        /// ReadHangTimeout: the port is dropped (closed in the background — that can block too) and opened again on the next poll.
        /// </summary>
        private async Task<(ushort[]? Words, string? Problem)> ReadBlockAsync(string station, string device)
        {
            var plc = _plc;
            if (plc == null)
                return (null, "COM port closed after a stuck read");
            try
            {
                var words = await plc.ReadWords(station, _command, device, FxIoMap.WORDS_PER_BLOCK).WaitAsync(ReadHangTimeout);
                return (words, plc.LastError);
            }
            catch (TimeoutException)
            {
                _logger.LogError("Reading station {Station} {Device} did not end within {Seconds} s — the COM port is stuck, closing {ComPort} to open it again",
                    station, device, ReadHangTimeout.TotalSeconds, _options.ComPort);
                _plc = null;
                _ = Task.Run(plc.Dispose);
                return (null, "read stuck in the serial driver");
            }
        }

        /// <summary>The inputs just read, before the filter, for the RAW IO page (null = station did not answer).</summary>
        private void PublishRaw(ushort[]?[] stationWords)
        {
            _state.RawIo = new RawIoRequest
            {
                AgentId = _outbox.AgentId,
                Mode = _state.Mode,
                Time = DateTimeOffset.Now,
                Stations = stationWords.Select((words, i) => new RawStationInputs { Station = FxIoMap.Stations[i], Words = words }).ToList()
            };
        }

        /// <summary>Open the COM port, retrying every 10 s. Never falls back to mock data.</summary>
        private bool EnsurePlcOpen()
        {
            if (_plc != null && _plc.IsOpen())
                return true;
            if (DateTime.Now < _nextOpenAttempt)
                return false;
            _nextOpenAttempt = DateTime.Now + ReopenInterval;

            _plc?.Dispose();
            _plc = new FxComputerLinkHelper(_options.ComPort, _options.BaudRate, _options.SumCheck);
            if (_plc.Open())
            {
                _logger.LogInformation("Connected to FX PLC line on {ComPort} at {BaudRate} baud", _options.ComPort, _options.BaudRate);
                _openFailedLogged = false;
                return true;
            }

            if (!_openFailedLogged)
            {
                _logger.LogError("Failed to open FX PLC line on {ComPort} ({Reason}) — retrying every {Seconds} s (no data is sent meanwhile)",
                    _options.ComPort, _plc.LastError, ReopenInterval.TotalSeconds);
                _openFailedLogged = true;
            }
            _plc.Dispose();
            _plc = null;
            return false;
        }
    }
}
