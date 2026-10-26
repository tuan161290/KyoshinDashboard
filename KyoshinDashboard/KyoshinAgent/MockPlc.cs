using Kyoshin.Plc;

namespace KyoshinAgent
{
    /// <summary>
    /// Simulated Mitsubishi FX PLCs for testing without hardware.
    /// Keeps the same raw words a real station returns for its status inputs (X000~) and
    /// error inputs (X120~), written and read through Kyoshin.Plc.FxIoMap, so mock data takes
    /// exactly the same bit mapping and decoder (FxIoMap.DecodeStation) as a real PLC.
    /// A machine's status turns on at most one of RED/YEL/GRN (all OFF = TẮT);
    /// error signals are independent of status (separate input group).
    /// </summary>
    public class MockPlc
    {
        private readonly Random _random = new();
        private readonly ushort[][] _statusWords = new ushort[FxIoMap.Stations.Length][];
        private readonly ushort[][] _errorWords = new ushort[FxIoMap.Stations.Length][];

        public MockPlc()
        {
            for (int station = 0; station < FxIoMap.Stations.Length; station++)
            {
                _statusWords[station] = new ushort[FxIoMap.WORDS_PER_BLOCK];
                _errorWords[station] = new ushort[FxIoMap.WORDS_PER_BLOCK];
            }
        }

        /// <summary>
        /// Write one machine's signals into the simulated input bits.
        /// </summary>
        public void SetMachine(int machineId, LampStatus status, bool outErr, bool trashErr, bool otherErr)
        {
            int station = FxIoMap.GetStationIndex(machineId);

            var statusWords = _statusWords[station];
            FxIoMap.SetBit(statusWords, FxIoMap.GetBitIndex(machineId, 0), status == LampStatus.Red);
            FxIoMap.SetBit(statusWords, FxIoMap.GetBitIndex(machineId, 1), status == LampStatus.Yellow);
            FxIoMap.SetBit(statusWords, FxIoMap.GetBitIndex(machineId, 2), status == LampStatus.Green);

            var errorWords = _errorWords[station];
            FxIoMap.SetBit(errorWords, FxIoMap.GetBitIndex(machineId, 0), outErr);
            FxIoMap.SetBit(errorWords, FxIoMap.GetBitIndex(machineId, 1), trashErr);
            FxIoMap.SetBit(errorWords, FxIoMap.GetBitIndex(machineId, 2), otherErr);
        }

        /// <summary>
        /// Each machine has a 1% chance per poll to switch to a random state (~1 change/s at 500 ms polling).
        /// </summary>
        public void RandomStep()
        {
            for (int machineId = 1; machineId <= FxIoMap.TOTAL_MACHINES; machineId++)
            {
                if (_random.NextDouble() >= 0.01) continue;

                double roll = _random.NextDouble();
                LampStatus status;
                if (roll < 0.50)       status = LampStatus.Green;   // 50% → GRN ON (CHẠY)
                else if (roll < 0.65)  status = LampStatus.Yellow;  // 15% → YEL ON (CHỜ)
                else if (roll < 0.80)  status = LampStatus.Red;     // 15% → RED ON (DỪNG)
                else                   status = LampStatus.Off;     // 20% → all OFF (TẮT)

                SetMachine(machineId, status,
                    outErr: _random.NextDouble() < 0.1,
                    trashErr: _random.NextDouble() < 0.08,
                    otherErr: _random.NextDouble() < 0.05);
            }
        }

        /// <summary>
        /// Same result as a read of one block on a real station: a copy of its 5 words.
        /// </summary>
        public ushort[] ReadWords(int station, FxIoBlock block)
        {
            var words = block == FxIoBlock.Status ? _statusWords : _errorWords;
            return (ushort[])words[station].Clone();
        }
    }
}
