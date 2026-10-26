using System;
using System.Collections.Generic;

namespace Kyoshin.Plc
{
    public enum FxIoBlock { Status, Error }

    /// <summary>
    /// Computer Link batch word-read command: WR (all FX series, 5-character device, e.g. "X0120")
    /// or QR (FX3U/FX3UC only, 7-character device, e.g. "X000120").
    /// </summary>
    public enum FxReadCommand { WR, QR }

    /// <summary>Tower lamp state of a machine (names match KyoshinDashboard.Models.MachineStatus).</summary>
    public enum LampStatus { Off, Green, Yellow, Red }

    /// <summary>One machine's lamp status and error signals as read from the PLC.</summary>
    public readonly record struct MachineReading(int MachineId, LampStatus Status, bool OutError, bool TrashError, bool OtherError);

    /// <summary>
    /// IO mapping for the Mitsubishi FX setup, identical to FXDataCommnunication GPIOHelper.
    /// Two PLC stations share one RS-485 line: Station 00 → M1~M24, Station 01 → M25~M48.
    /// The agent reads the inputs X directly (no PLC program needed; filtering is done by StableReadingFilter).
    /// Each station is read as two 5-word blocks:
    ///   Status block X000~X117: RED/YEL/GRN × 24 machines = 72 bits, bits 72~79 spare (X110~X117)
    ///   Error  block X120~X237: OUT/TRASH/OTHER × 24 machines = 72 bits, bits 72~79 spare (X230~X237)
    /// Machine k (0~23) of a station, signal s (0~2) → bit k*3+s of the block (word = bit/16, LSB first).
    /// </summary>
    public static class FxIoMap
    {
        public static readonly string[] Stations = { "00", "01" };
        public const int MACHINES_PER_STATION = 24;
        public const int TOTAL_MACHINES = 48;
        public const int SIGNALS_PER_MACHINE = 3;
        public const int WORDS_PER_BLOCK = 5;

        private const int ERROR_INPUT_OFFSET = 80;     // Error inputs start at X120 (octal) = input #80

        public static readonly string[] StatusSignals = { "RED", "YEL", "GRN" };
        public static readonly string[] ErrorSignals = { "OUT_ERROR", "TRASH_ERROR", "OTHER_ERROR" };

        public static int GetStationIndex(int machineId) => (machineId - 1) / MACHINES_PER_STATION;

        /// <summary>Bit index (0~71) of a machine's signal within its station's 80-bit block.</summary>
        public static int GetBitIndex(int machineId, int signal) =>
            (machineId - 1) % MACHINES_PER_STATION * SIGNALS_PER_MACHINE + signal;

        public static bool IsBitSet(ushort[] words, int bitIndex) =>
            (words[bitIndex / 16] & (1 << (bitIndex % 16))) != 0;

        public static void SetBit(ushort[] words, int bitIndex, bool on)
        {
            ushort mask = (ushort)(1 << (bitIndex % 16));
            if (on) words[bitIndex / 16] |= mask;
            else words[bitIndex / 16] &= (ushort)~mask;
        }

        /// <summary>
        /// Decode one station's status + error blocks into the readings of its 24 machines.
        /// Lamp priority when several are on: GRN > YEL > RED; none on = Off.
        /// </summary>
        public static List<MachineReading> DecodeStation(int station, ushort[] statusWords, ushort[] errorWords)
        {
            var readings = new List<MachineReading>(MACHINES_PER_STATION);
            int firstMachine = station * MACHINES_PER_STATION + 1; // M1, M25
            for (int machineId = firstMachine; machineId < firstMachine + MACHINES_PER_STATION; machineId++)
            {
                // Status block: RED=signal0, YEL=signal1, GRN=signal2
                bool red = IsBitSet(statusWords, GetBitIndex(machineId, 0));
                bool yel = IsBitSet(statusWords, GetBitIndex(machineId, 1));
                bool grn = IsBitSet(statusWords, GetBitIndex(machineId, 2));

                LampStatus status;
                if (grn) status = LampStatus.Green;
                else if (yel) status = LampStatus.Yellow;
                else if (red) status = LampStatus.Red;
                else status = LampStatus.Off;

                // Error block: OUT=signal0, TRASH=signal1, OTHER=signal2
                readings.Add(new MachineReading(machineId, status,
                    IsBitSet(errorWords, GetBitIndex(machineId, 0)),
                    IsBitSet(errorWords, GetBitIndex(machineId, 1)),
                    IsBitSet(errorWords, GetBitIndex(machineId, 2))));
            }
            return readings;
        }

        /// <summary>
        /// Device name of a block's first input, as the read command expects it (X numbers are octal):
        /// WR (5 chars) "X0000"/"X0120"; QR (7 chars) "X000000"/"X000120". Each block is 5 words = 80 inputs.
        /// </summary>
        public static string GetBlockDevice(FxIoBlock block, FxReadCommand command)
        {
            int firstInput = block == FxIoBlock.Error ? ERROR_INPUT_OFFSET : 0;
            return "X" + Convert.ToString(firstInput, 8).PadLeft(command == FxReadCommand.WR ? 4 : 6, '0');
        }

        /// <summary>Physical input in GX Works octal notation, e.g. "X000", "X117", "X120".</summary>
        public static string GetInputLabel(int machineId, int signal, FxIoBlock block)
        {
            int input = GetBitIndex(machineId, signal) + (block == FxIoBlock.Error ? ERROR_INPUT_OFFSET : 0);
            return "X" + Convert.ToString(input, 8).PadLeft(3, '0');
        }

        /// <summary>Signal name as in FXDataCommnunication, e.g. "M1RED", "M1_OUT_ERROR".</summary>
        public static string GetDescription(int machineId, int signal, FxIoBlock block) =>
            block == FxIoBlock.Status
                ? $"M{machineId}{StatusSignals[signal]}"
                : $"M{machineId}_{ErrorSignals[signal]}";
    }
}
