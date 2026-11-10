using KyoshinDashboard.Services;
using Microsoft.AspNetCore.SignalR;

namespace KyoshinDashboard.Hubs
{
    /// <summary>
    /// SignalR Hub for real-time machine status updates.
    /// Clients connect to receive live status changes and the factory link status.
    /// </summary>
    public class MachineStatusHub : Hub
    {
        private readonly RawIoService _rawIo;

        public MachineStatusHub(RawIoService rawIo) => _rawIo = rawIo;

        /// <summary>RAW IO page: receive the unfiltered inputs ("RawIo"), starting with the latest ones.</summary>
        public async Task WatchRawIo()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, RawIoService.Group);
            var current = _rawIo.GetCurrentPayload();
            if (current != null)
                await Clients.Caller.SendAsync("RawIo", current);
        }

        public override async Task OnConnectedAsync()
        {
            // Send current states and factory link status to newly connected client
            var data = MachineMonitorService.GetAllStates().Select(MachineMonitorService.ToClientPayload).ToList();
            await Clients.Caller.SendAsync("InitializeStates", data);
            await Clients.Caller.SendAsync("AgentStatus", MachineMonitorService.GetAgentStatusPayload());
            await base.OnConnectedAsync();
        }
    }
}
