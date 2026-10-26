using System.ComponentModel.DataAnnotations;

namespace KyoshinDashboard.Models
{
    /// <summary>
    /// Per factory agent: the last event Seq applied (re-sent events with Seq ≤ LastSeq are skipped)
    /// and when the agent was last heard from.
    /// </summary>
    public class AgentCursor
    {
        [Key]
        public string AgentId { get; set; } = "";
        public long LastSeq { get; set; }
        public DateTime LastSeen { get; set; }
    }
}
