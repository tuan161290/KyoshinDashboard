using System;
using System.Collections.Generic;

namespace Kyoshin.Plc
{
    /// <summary>
    /// Wire contract between KyoshinAgent (factory) and KyoshinDashboard (server):
    ///   POST /api/ingest   header X-Api-Key: &lt;key&gt;   body: IngestRequest   → IngestResponse
    /// The agent sends a machine's full state whenever it changes (plus a snapshot of all machines at start).
    /// An empty Events list is a heartbeat. Seq is strictly increasing per AgentId, so the server can drop
    /// re-sent events (Seq &lt;= last applied) and the agent can delete everything up to the returned LastSeq.
    /// </summary>
    public class IngestRequest
    {
        public string AgentId { get; set; } = "";

        /// <summary>"plc" = reading the real PLC, "mock" = simulated data, "plc-offline" = COM port cannot be opened.</summary>
        public string Mode { get; set; } = "";

        /// <summary>Number of PLC stations that answered on the last poll (0~2).</summary>
        public int StationsOk { get; set; }

        /// <summary>
        /// Last poll in which a station answered (null = none yet). StationsOk alone can't show a polling loop that
        /// stopped: it keeps the result of the last poll while the heartbeat goes on.
        /// </summary>
        public DateTimeOffset? LastReadAt { get; set; }

        public List<IngestEvent> Events { get; set; } = new();
    }

    public class IngestEvent
    {
        public long Seq { get; set; }
        public int MachineId { get; set; }

        /// <summary>LampStatus name: Off, Green, Yellow, Red.</summary>
        public string Status { get; set; } = "";

        public bool OutError { get; set; }
        public bool TrashError { get; set; }
        public bool OtherError { get; set; }

        /// <summary>When the change was seen at the factory (factory clock, with UTC offset).</summary>
        public DateTimeOffset Time { get; set; }
    }

    public class IngestResponse
    {
        /// <summary>Highest Seq the server has applied for this agent; the agent may delete everything up to it.</summary>
        public long LastSeq { get; set; }
    }

    /// <summary>
    /// POST /api/ingest/raw (same API key) — the inputs X as just read, before any filtering, for the RAW IO page.
    /// Sent when they change and at least every few seconds. Not queued and not stored: a lost one is replaced by the next.
    /// </summary>
    public class RawIoRequest
    {
        public string AgentId { get; set; } = "";

        /// <summary>Same as IngestRequest.Mode.</summary>
        public string Mode { get; set; } = "";

        /// <summary>When the inputs were read (factory clock).</summary>
        public DateTimeOffset Time { get; set; }

        /// <summary>One per station, in FxIoMap.Stations order.</summary>
        public List<RawStationInputs> Stations { get; set; } = new();
    }

    public class RawStationInputs
    {
        public string Station { get; set; } = "";

        /// <summary>X000~X237 as 10 words (status block, then error block; bit 0 = lowest X). null = the station did not answer.</summary>
        public ushort[] Words { get; set; }
    }
}
