using Kyoshin.Plc;
using KyoshinDashboard.Hubs;
using KyoshinDashboard.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace KyoshinDashboard.Services
{
    /// <summary>
    /// Keeps the live state of all machines, writes status/error logs and pushes updates via SignalR.
    /// Data comes from KyoshinAgent at the factory (POST /api/ingest → ApplyAsync), each event carrying the
    /// time the change happened at the factory. Events already applied (Seq ≤ last Seq of that agent) are
    /// skipped, so re-sent batches never create duplicate logs.
    /// The background loop only watches the agent heartbeat and flags the factory offline when it stops.
    /// </summary>
    public class MachineMonitorService : BackgroundService
    {
        private readonly ILogger<MachineMonitorService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IHubContext<MachineStatusHub> _hubContext;
        private readonly TimeSpan _agentOfflineAfter;
        private readonly bool _errorsOnlyWhenGreenOrYellow;

        public const int TOTAL_MACHINES = FxIoMap.TOTAL_MACHINES;

        // In-memory current state of all 48 machines
        public static readonly Dictionary<int, MachineState> CurrentStates = new();
        private static readonly object _stateLock = new();

        // Track the active log entry for each machine (to close it when status changes)
        private readonly Dictionary<int, long> _activeLogIds = new();
        // Track active error log entries: key = (machineId, errorType)
        private readonly Dictionary<(int, ErrorType), long> _activeErrorLogIds = new();

        // One ingest batch at a time, and only after the state was restored from the database
        private readonly SemaphoreSlim _applyLock = new(1, 1);
        private readonly TaskCompletionSource _restored = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Factory link (latest agent heartbeat)
        private static readonly object _agentLock = new();
        private static AgentStatus _agent = new(false, null, "", 0);

        public MachineMonitorService(
            ILogger<MachineMonitorService> logger,
            IServiceProvider serviceProvider,
            IHubContext<MachineStatusHub> hubContext,
            IConfiguration config)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _hubContext = hubContext;
            _agentOfflineAfter = TimeSpan.FromSeconds(config.GetValue("Ingest:AgentOfflineSeconds", 30));
            _errorsOnlyWhenGreenOrYellow = config.GetValue("Errors:OnlyWhenGreenOrYellow", true);

            // Initialize 48 machines
            lock (_stateLock)
            {
                for (int i = 1; i <= TOTAL_MACHINES; i++)
                    CurrentStates[i] = new MachineState { MachineId = i };
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await RestoreFromDatabaseAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore machine states from the database");
            }
            finally
            {
                _restored.TrySetResult();
            }

            _logger.LogInformation("Waiting for data from the factory agent (offline after {Seconds} s without heartbeat)",
                _agentOfflineAfter.TotalSeconds);

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(5000, stoppingToken);

                AgentStatus? wentOffline = null;
                lock (_agentLock)
                {
                    if (_agent.Online && DateTime.Now - _agent.LastSeen > _agentOfflineAfter)
                        wentOffline = _agent = _agent with { Online = false };
                }
                if (wentOffline != null)
                {
                    _logger.LogWarning("Factory agent offline — no data since {LastSeen:HH:mm:ss}", wentOffline.LastSeen);
                    await BroadcastAgentStatusAsync();
                }
            }
        }

        /// <summary>
        /// Apply one batch from the agent (in Seq order) and return the last applied Seq.
        /// An empty batch is a heartbeat.
        /// </summary>
        public async Task<long> ApplyAsync(IngestRequest request)
        {
            await _restored.Task;
            await _applyLock.WaitAsync();
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<KyoshinDbContext>();

                var cursor = await db.AgentCursors.FindAsync(request.AgentId);
                if (cursor == null)
                {
                    cursor = new AgentCursor { AgentId = request.AgentId };
                    db.AgentCursors.Add(cursor);
                    _logger.LogInformation("New factory agent {AgentId}", request.AgentId);
                }

                var changedMachines = new Dictionary<int, MachineState>();
                foreach (var e in request.Events.OrderBy(e => e.Seq))
                {
                    if (e.Seq <= cursor.LastSeq)
                        continue; // already applied (the agent re-sent a batch)
                    cursor.LastSeq = e.Seq;

                    if (e.MachineId < 1 || e.MachineId > TOTAL_MACHINES
                        || !Enum.TryParse<MachineStatus>(e.Status, out var status) || !Enum.IsDefined(status))
                    {
                        _logger.LogWarning("Ignoring invalid event {Seq}: machine {MachineId}, status '{Status}'", e.Seq, e.MachineId, e.Status);
                        continue;
                    }

                    // Time the change happened at the factory, in server local time
                    var time = e.Time.ToLocalTime().DateTime;
                    if (await ApplyChangeAsync(db, e.MachineId, status, e.OutError, e.TrashError, e.OtherError, time))
                        changedMachines[e.MachineId] = CurrentStates[e.MachineId];
                }

                cursor.LastSeen = DateTime.Now;
                await db.SaveChangesAsync();

                await UpdateAgentStatusAsync(request, cursor.LastSeen);

                if (changedMachines.Count > 0)
                {
                    var updates = changedMachines.Values.Select(ToClientPayload).ToList();
                    await _hubContext.Clients.All.SendAsync("UpdateMachineStates", updates);
                }

                return cursor.LastSeq;
            }
            finally
            {
                _applyLock.Release();
            }
        }

        /// <summary>
        /// Status and error tracking for one machine at <paramref name="time"/> (factory time).
        /// Returns true if anything changed.
        /// </summary>
        private async Task<bool> ApplyChangeAsync(KyoshinDbContext db, int machineId, MachineStatus newStatus,
            bool outErr, bool trashErr, bool otherErr, DateTime time)
        {
            MachineState current;
            lock (_stateLock) { current = CurrentStates[machineId]; }
            bool anyChange = false;

            // Keep the raw inputs for the I/O monitor
            if (current.OutSignal != outErr || current.TrashSignal != trashErr || current.OtherSignal != otherErr)
            {
                lock (_stateLock)
                {
                    current.OutSignal = outErr;
                    current.TrashSignal = trashErr;
                    current.OtherSignal = otherErr;
                }
                anyChange = true;
            }

            // Customer spec: errors (thoát/rác/khác) count only while the machine runs or waits (green/yellow lamp).
            // An error still on when the lamp turns red or off is closed then, and opened again when it runs again.
            if (_errorsOnlyWhenGreenOrYellow && newStatus is not (MachineStatus.Green or MachineStatus.Yellow))
                outErr = trashErr = otherErr = false;

            // --- Status tracking (StatusLog table) ---
            if (current.Status != newStatus)
            {
                // Close previous status log
                if (_activeLogIds.TryGetValue(machineId, out long prevLogId))
                {
                    var prevLog = await db.StatusLogs.FindAsync(prevLogId);
                    if (prevLog != null)
                    {
                        var end = time < prevLog.StartTime ? prevLog.StartTime : time; // guard against clock changes
                        prevLog.EndTime = end;
                        prevLog.DurationSeconds = (end - prevLog.StartTime).TotalSeconds;
                    }
                }

                // Create new status log
                var newLog = new MachineStatusLog
                {
                    MachineId = machineId,
                    Status = newStatus,
                    StartTime = time,
                    Shift = GetCurrentShift(time)
                };
                db.StatusLogs.Add(newLog);
                await db.SaveChangesAsync();
                _activeLogIds[machineId] = newLog.Id;

                lock (_stateLock)
                {
                    current.Status = newStatus;
                    current.LastStatusChange = time;
                }
                anyChange = true;
            }

            // --- Error tracking (ErrorLog table) — completely independent ---
            var errorChecks = new[] {
                (ErrorType.Out, outErr, current.HasOutError),
                (ErrorType.Trash, trashErr, current.HasTrashError),
                (ErrorType.Other, otherErr, current.HasOtherError)
            };

            foreach (var (errType, newVal, oldVal) in errorChecks)
            {
                var errKey = (machineId, errType);
                if (newVal && !oldVal)
                {
                    // Error turned ON → create new ErrorLog record
                    var errLog = new MachineErrorLog
                    {
                        MachineId = machineId,
                        ErrorType = errType,
                        StartTime = time,
                        Shift = GetCurrentShift(time)
                    };
                    db.ErrorLogs.Add(errLog);
                    await db.SaveChangesAsync();
                    _activeErrorLogIds[errKey] = errLog.Id;
                    anyChange = true;
                }
                else if (!newVal && oldVal)
                {
                    // Error turned OFF → close ErrorLog record
                    if (_activeErrorLogIds.TryGetValue(errKey, out long errLogId))
                    {
                        var errLog = await db.ErrorLogs.FindAsync(errLogId);
                        if (errLog != null)
                        {
                            var end = time < errLog.StartTime ? errLog.StartTime : time;
                            errLog.EndTime = end;
                            errLog.DurationSeconds = (end - errLog.StartTime).TotalSeconds;
                            await db.SaveChangesAsync();
                        }
                        _activeErrorLogIds.Remove(errKey);
                    }
                    anyChange = true;
                }
            }

            // Update in-memory error state
            if (current.HasOutError != outErr || current.HasTrashError != trashErr || current.HasOtherError != otherErr)
            {
                lock (_stateLock)
                {
                    current.HasOutError = outErr;
                    current.HasTrashError = trashErr;
                    current.HasOtherError = otherErr;
                }
                anyChange = true;
            }

            return anyChange;
        }

        /// <summary>
        /// Restore active log IDs and in-memory state from DB (handles server restart).
        /// </summary>
        private async Task RestoreFromDatabaseAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KyoshinDbContext>();

            // Restore status logs
            var openLogs = await db.StatusLogs
                .Where(l => l.EndTime == null)
                .OrderByDescending(l => l.StartTime)
                .ToListAsync();

            int closedCount = 0;
            foreach (var log in openLogs)
            {
                if (!CurrentStates.ContainsKey(log.MachineId))
                {
                    // Machine no longer configured (e.g. M49/M50 after moving to 48 machines)
                    log.EndTime = DateTime.Now;
                    log.DurationSeconds = (log.EndTime.Value - log.StartTime).TotalSeconds;
                    closedCount++;
                }
                else if (!_activeLogIds.ContainsKey(log.MachineId))
                {
                    _activeLogIds[log.MachineId] = log.Id;
                    lock (_stateLock)
                    {
                        if (CurrentStates.TryGetValue(log.MachineId, out var state))
                        {
                            state.Status = log.Status;
                            state.LastStatusChange = log.StartTime;
                        }
                    }
                }
                else
                {
                    log.EndTime = log.StartTime;
                    log.DurationSeconds = 0;
                    closedCount++;
                }
            }

            // Restore error logs
            var openErrors = await db.ErrorLogs
                .Where(e => e.EndTime == null)
                .OrderByDescending(e => e.StartTime)
                .ToListAsync();

            foreach (var errLog in openErrors)
            {
                var key = (errLog.MachineId, errLog.ErrorType);
                if (!CurrentStates.ContainsKey(errLog.MachineId))
                {
                    errLog.EndTime = DateTime.Now;
                    errLog.DurationSeconds = (errLog.EndTime.Value - errLog.StartTime).TotalSeconds;
                    closedCount++;
                }
                else if (!_activeErrorLogIds.ContainsKey(key))
                {
                    _activeErrorLogIds[key] = errLog.Id;
                    lock (_stateLock)
                    {
                        if (CurrentStates.TryGetValue(errLog.MachineId, out var state))
                        {
                            switch (errLog.ErrorType)
                            {
                                case ErrorType.Out: state.HasOutError = state.OutSignal = true; break;
                                case ErrorType.Trash: state.HasTrashError = state.TrashSignal = true; break;
                                case ErrorType.Other: state.HasOtherError = state.OtherSignal = true; break;
                            }
                        }
                    }
                }
                else
                {
                    errLog.EndTime = errLog.StartTime;
                    errLog.DurationSeconds = 0;
                    closedCount++;
                }
            }

            if (closedCount > 0)
            {
                await db.SaveChangesAsync();
                _logger.LogInformation("Closed {Count} stale open log entries", closedCount);
            }
            _logger.LogInformation("Restored {Count} active log entries from database", _activeLogIds.Count);

            // Last time the factory was heard from (shown as "offline since ..." until it reconnects)
            var lastAgent = await db.AgentCursors.OrderByDescending(a => a.LastSeen).FirstOrDefaultAsync();
            if (lastAgent != null)
            {
                lock (_agentLock) { _agent = new AgentStatus(false, lastAgent.LastSeen, "", 0); }
            }
        }

        private async Task UpdateAgentStatusAsync(IngestRequest request, DateTime lastSeen)
        {
            bool changed;
            bool cameOnline;
            lock (_agentLock)
            {
                var updated = new AgentStatus(true, lastSeen, request.Mode, request.StationsOk);
                cameOnline = !_agent.Online;
                changed = cameOnline || updated.Mode != _agent.Mode || updated.StationsOk != _agent.StationsOk;
                _agent = updated;
            }

            if (cameOnline)
                _logger.LogInformation("Factory agent online (mode {Mode}, {Stations} station(s) answering)", request.Mode, request.StationsOk);
            if (changed)
                await BroadcastAgentStatusAsync();
        }

        private Task BroadcastAgentStatusAsync() =>
            _hubContext.Clients.All.SendAsync("AgentStatus", GetAgentStatusPayload());

        /// <summary>Factory link status as sent to browsers (SignalR "AgentStatus") and /api/machineapi/agent.</summary>
        public static object GetAgentStatusPayload()
        {
            AgentStatus agent;
            lock (_agentLock) { agent = _agent; }
            return new
            {
                agent.Online,
                LastSeen = agent.LastSeen?.ToString("HH:mm:ss dd/MM"),
                agent.Mode,
                agent.StationsOk,
                StationsTotal = FxIoMap.Stations.Length
            };
        }

        /// <summary>Machine state as sent to browsers (SignalR "InitializeStates"/"UpdateMachineStates").</summary>
        public static object ToClientPayload(MachineState m) => new
        {
            m.MachineId,
            m.Name,
            Status = m.Status.ToString(),
            m.StatusCssClass,
            m.StatusText,
            m.HasOutError,
            m.HasTrashError,
            m.HasOtherError,
            m.OutSignal,
            m.TrashSignal,
            m.OtherSignal,
            m.ErrorText,
            LastChange = m.LastStatusChange.ToString("HH:mm:ss")
        };

        public static ShiftType GetCurrentShift(DateTime time)
        {
            var t = time.TimeOfDay;
            if (t >= TimeSpan.FromHours(6) && t < TimeSpan.FromHours(14))
                return ShiftType.Shift1;
            if (t >= TimeSpan.FromHours(14) && t < TimeSpan.FromHours(22))
                return ShiftType.Shift2;
            return ShiftType.Shift3;
        }

        /// <summary>Start and end of a shift of production day <paramref name="date"/> (shift 3 ends 06:00 next day).</summary>
        public static (DateTime start, DateTime end) GetShiftBoundaries(DateTime date, ShiftType shift) => shift switch
        {
            ShiftType.Shift1 => (date.Date.AddHours(6), date.Date.AddHours(14)),
            ShiftType.Shift2 => (date.Date.AddHours(14), date.Date.AddHours(22)),
            ShiftType.Shift3 => (date.Date.AddHours(22), date.Date.AddDays(1).AddHours(6)),
            _ => (date.Date, date.Date.AddDays(1))
        };

        /// <summary>Production day a time belongs to: 06:00 → 06:00 next day, so shift 3 after midnight counts for the day before.</summary>
        public static DateTime GetShiftDay(DateTime time) => time.AddHours(-6).Date;

        public static List<MachineState> GetAllStates()
        {
            lock (_stateLock)
            {
                return CurrentStates.Values.OrderBy(m => m.MachineId).ToList();
            }
        }

        private record AgentStatus(bool Online, DateTime? LastSeen, string Mode, int StationsOk);
    }
}
