namespace KyoshinDashboard.Models
{
    public enum MachineStatus
    {
        Off = 0,    // All lights off - Power Off
        Green = 1,  // Running
        Yellow = 2, // Waiting/Warning
        Red = 3     // Stopped/Error
    }

    public enum ErrorType
    {
        None = 0,
        Out = 1,
        Trash = 2,
        Other = 3
    }

    public enum ShiftType
    {
        Shift1 = 1, // 06:00 - 14:00
        Shift2 = 2, // 14:00 - 22:00
        Shift3 = 3  // 22:00 - 06:00
    }
}
