using System;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kyoshin.Plc
{
    /// <summary>
    /// Mitsubishi FX Computer Link — dedicated protocol, control procedure format 1, over RS-485 (38400-7-O-1).
    /// Batch word read (FX manual 7.2 "WR"; "QR" is the FX3U/FX3UC variant with 7-character device names):
    ///   Computer: ENQ + Station(2) + PC No "FF" + "WR" + wait "0" + head device + points(2 hex) [+ sum check(2)]
    ///   PLC:      STX + Station(2) + "FF" + 4 hex chars per word + ETX [+ sum check(2)]
    ///             or NAK + Station(2) + "FF" + error code(2)
    ///   Computer: ACK + Station(2) + "FF"   (confirms the received data)
    /// Sum check (only when enabled in the PLC) = low byte of the sum of the ASCII codes from Station up to the
    /// character before it — request: through the point count; response: through ETX — as 2 hex characters.
    /// For bit devices (M, X) one point = 16 bits, the lowest-numbered device in bit 0.
    /// </summary>
    public class FxComputerLinkHelper : IDisposable
    {
        private const byte ENQ = 0x05;
        private const byte STX = 0x02;
        private const byte ETX = 0x03;
        private const byte ACK = 0x06;
        private const byte NAK = 0x15;
        private const string PC_NO = "FF";
        private const int MAX_RETRY = 3;
        private const int RESPONSE_TIMEOUT_MS = 300;

        private SerialPort _serialPort;
        private readonly bool _sumCheck;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private byte[] _readBuffer = new byte[256];

        /// <param name="sumCheck">Must match the PLC's "Sum Check" setting (PLC parameter / D8120).</param>
        public FxComputerLinkHelper(string comPort, int baudRate = 38400, bool sumCheck = false)
        {
            _sumCheck = sumCheck;
            _serialPort = new SerialPort(comPort, baudRate, Parity.Odd, 7, StopBits.One);
            _serialPort.Handshake = Handshake.None;
            _serialPort.ReadTimeout = RESPONSE_TIMEOUT_MS;
            _serialPort.WriteTimeout = RESPONSE_TIMEOUT_MS;
        }

        /// <summary>
        /// Open the serial port connection.
        /// Returns true if successfully opened.
        /// </summary>
        public bool Open()
        {
            try
            {
                if (!_serialPort.IsOpen)
                {
                    _serialPort.Open();
                    Thread.Sleep(50);
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Why the last ReadWords call failed (e.g. "PLC answered NAK, error code 06h ..." or "no answer"), or null after a success.
        /// </summary>
        public string LastError { get; private set; }

        /// <summary>
        /// Batch read <paramref name="wordCount"/> words (16 points each for bit devices) starting at <paramref name="device"/>,
        /// formatted for <paramref name="command"/> (WR: "X0120", QR: "X000120"). Returns null on failure (see <see cref="LastError"/>).
        /// </summary>
        public async Task<ushort[]> ReadWords(string station, FxReadCommand command, string device, int wordCount)
        {
            try
            {
                await _lock.WaitAsync();
                int retry = MAX_RETRY;
                byte[] request = BuildReadRequest(station, command, device, wordCount, _sumCheck);
                int expectedLen = ResponseLength(wordCount, _sumCheck);

                while (retry > 0)
                {
                    try
                    {
                        _serialPort.DiscardInBuffer();
                        await _serialPort.BaseStream.WriteAsync(request, 0, request.Length);
                        _serialPort.BaseStream.Flush();

                        int totalRead = await ReadResponseAsync(expectedLen);

                        string error;
                        ushort[] words = ParseReadResponse(_readBuffer, totalRead, station, wordCount, _sumCheck, out error);
                        LastError = error;
                        if (words != null)
                        {
                            await SendAckAsync(station);
                            return words;
                        }
                    }
                    catch (OperationCanceledException) { LastError = "no answer (timeout)"; }
                    catch (TimeoutException) { LastError = "no answer (timeout)"; }
                    catch (System.IO.IOException ex) { LastError = "COM port error: " + ex.Message; }

                    await Task.Delay(50);
                    retry--;
                }

                return null;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Request frame, e.g. WR: ENQ "05" "FF" "WR" "0" "X0040" "02" ["48"] (manual 7.2 example) —
        /// QR: ENQ "00" "FF" "QR" "0" "X000120" "05". The "0" after the command is the message wait time.
        /// </summary>
        public static byte[] BuildReadRequest(string station, FxReadCommand command, string device, int wordCount, bool sumCheck = false)
        {
            string body = $"{station}{PC_NO}{command}0{device}{wordCount:X2}";
            if (sumCheck)
                body += SumCheck(Encoding.ASCII.GetBytes(body), 0, body.Length);

            byte[] request = new byte[1 + body.Length];
            request[0] = ENQ;
            Encoding.ASCII.GetBytes(body, 0, body.Length, request, 1);
            return request;
        }

        /// <summary>STX + Station(2) + PC No(2) + 4 chars per word + ETX [+ sum check(2)].</summary>
        public static int ResponseLength(int wordCount, bool sumCheck) => 1 + 2 + 2 + wordCount * 4 + 1 + (sumCheck ? 2 : 0);

        /// <summary>
        /// Decode a read answer. Good: STX + Station(2) + "FF" + wordCount*4 hex chars + ETX [+ sum check].
        /// Error: NAK + Station(2) + "FF" + error code(2). Returns the words, or null with a readable reason.
        /// </summary>
        public static ushort[] ParseReadResponse(byte[] buffer, int length, string station, int wordCount, bool sumCheck, out string error)
        {
            int etxIndex = 5 + wordCount * 4;
            int expectedLen = ResponseLength(wordCount, sumCheck);
            if (length <= 0)
            {
                error = "no answer (timeout) — check station number, baud rate/parity, wiring";
                return null;
            }
            if (buffer[0] == NAK)
            {
                string code = length >= 7 ? Encoding.ASCII.GetString(buffer, 5, 2) : "??";
                error = $"PLC answered NAK, error code {code}h{DescribeError(code)}";
                return null;
            }
            if (buffer[0] != STX || length < expectedLen || buffer[etxIndex] != ETX)
            {
                error = $"unexpected answer ({length} bytes): {Preview(buffer, length)}";
                return null;
            }
            string answeredStation = Encoding.ASCII.GetString(buffer, 1, 2);
            if (answeredStation != station)
            {
                error = $"answer came from station {answeredStation}, expected {station}";
                return null;
            }
            if (sumCheck)
            {
                // Station .. ETX inclusive = buffer[1 .. etxIndex]
                string expected = SumCheck(buffer, 1, etxIndex);
                string received = Encoding.ASCII.GetString(buffer, etxIndex + 1, 2);
                if (!string.Equals(expected, received, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"sum check mismatch (received {received}, calculated {expected}) — noise on the line?";
                    return null;
                }
            }

            ushort[] words = new ushort[wordCount];
            for (int i = 0; i < wordCount; i++)
            {
                // Each word is 4 hex chars, high nibble first ("0A1F" = 0x0A1F)
                string hex = Encoding.ASCII.GetString(buffer, 5 + i * 4, 4);
                if (!ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out words[i]))
                {
                    error = $"invalid data '{hex}' in word {i} (noise or parity error?)";
                    return null;
                }
            }
            error = null;
            return words;
        }

        /// <summary>Low byte of the sum of the ASCII codes data[offset .. offset+count-1], as 2 uppercase hex characters.</summary>
        public static string SumCheck(byte[] data, int offset, int count)
        {
            int sum = 0;
            for (int i = offset; i < offset + count; i++)
                sum += data[i];
            return (sum & 0xFF).ToString("X2");
        }

        /// <summary>Confirm the received data: ACK + Station(2) + PC No(2).</summary>
        private async Task SendAckAsync(string station)
        {
            byte[] ack = new byte[1 + 2 + 2];
            ack[0] = ACK;
            Encoding.ASCII.GetBytes(station + PC_NO, 0, 4, ack, 1);
            try
            {
                await _serialPort.BaseStream.WriteAsync(ack, 0, ack.Length);
                _serialPort.BaseStream.Flush();
            }
            catch (Exception)
            {
                // The data is already valid; a lost ACK only matters to the PLC
            }
        }

        private static string DescribeError(string code)
        {
            switch (code)
            {
                case "02": return " (sum check error — set Plc:SumCheck the same as the PLC's Sum Check setting)";
                case "03": return " (protocol error — frame not accepted: check Plc:Command WR/QR and Plc:SumCheck)";
                case "06": return " (character area error — device, start address or count not allowed)";
                case "07": return " (character error — invalid character in the request)";
                default: return "";
            }
        }

        private static string Preview(byte[] buffer, int length)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Math.Min(length, 24); i++)
            {
                byte b = buffer[i];
                sb.Append(b >= 0x20 && b < 0x7F ? ((char)b).ToString() : $"<{b:X2}>");
            }
            if (length > 24) sb.Append("...");
            return sb.ToString();
        }

        /// <summary>
        /// Read expected number of bytes from serial port with timeout.
        /// Returns actual number of bytes read into _readBuffer.
        /// </summary>
        private async Task<int> ReadResponseAsync(int expectedLength)
        {
            int totalRead = 0;
            using (var cts = new CancellationTokenSource(RESPONSE_TIMEOUT_MS))
            {
                try
                {
                    while (totalRead < expectedLength)
                    {
                        int bytesRead = await _serialPort.BaseStream.ReadAsync(
                            _readBuffer, totalRead, expectedLength - totalRead, cts.Token);
                        if (bytesRead == 0) break;
                        totalRead += bytesRead;
                    }
                }
                catch (OperationCanceledException) { }
            }
            return totalRead;
        }

        public bool IsOpen()
        {
            return _serialPort?.IsOpen ?? false;
        }

        public void Close()
        {
            if (_serialPort != null && _serialPort.IsOpen)
            {
                _serialPort.Close();
            }
        }

        public void Dispose()
        {
            Close();
            _serialPort?.Dispose();
            _lock?.Dispose();
        }
    }
}
