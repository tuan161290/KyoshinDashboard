namespace KyoshinDashboard.Models
{
    /// <summary>
    /// Represents the real-time state of a machine (in-memory, not persisted)
    /// </summary>
    public class MachineState
    {
        public int MachineId { get; set; }          // 1-48
        public string Name => $"M{MachineId}";
        public MachineStatus Status { get; set; } = MachineStatus.Off;
        // Errors as counted and logged (only while running or waiting, see Errors:OnlyWhenGreenOrYellow)
        public bool HasOutError { get; set; }
        public bool HasTrashError { get; set; }
        public bool HasOtherError { get; set; }

        // Raw error inputs from the PLC (I/O monitor), whatever the lamp
        public bool OutSignal { get; set; }
        public bool TrashSignal { get; set; }
        public bool OtherSignal { get; set; }
        public DateTime LastStatusChange { get; set; } = DateTime.Now;

        public string StatusCssClass => Status switch
        {
            MachineStatus.Green => "status-green",
            MachineStatus.Yellow => "status-yellow",
            MachineStatus.Red => "status-red",
            _ => "status-off"
        };

        public string StatusText => Status switch
        {
            MachineStatus.Green => "CHẠY",
            MachineStatus.Yellow => "CHỜ",
            MachineStatus.Red => "DỪNG",
            _ => "TẮT"
        };

        public string ErrorText
        {
            get
            {
                var errors = new List<string>();
                if (HasOutError) errors.Add(ErrorNames.Short(ErrorType.Out));
                if (HasTrashError) errors.Add(ErrorNames.Short(ErrorType.Trash));
                if (HasOtherError) errors.Add(ErrorNames.Short(ErrorType.Other));
                return errors.Count > 0 ? string.Join(", ", errors) : "";
            }
        }
    }
}
