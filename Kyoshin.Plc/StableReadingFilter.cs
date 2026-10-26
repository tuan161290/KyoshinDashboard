using System;
using System.Collections.Generic;

namespace Kyoshin.Plc
{
    /// <summary>
    /// Confirms what is read from the PLC inputs only after it has stayed the same for a minimum time:
    /// shorter pulses, flicker and contact bounce are ignored. The time is set separately for
    /// ON (Plc:OnConfirmSeconds — an error signal turning on, or the machine changing to a lamp colour) and
    /// OFF (Plc:OffConfirmSeconds — an error signal turning off, or the machine going dark = Off).
    /// A machine's lamp status (the colour decoded from RED/YEL/GRN) and each of its 3 error signals are confirmed
    /// separately, so a colour change GRN → RED is one change (no brief "Off" in between) and a flickering error
    /// signal doesn't hold back the lamp status.
    /// A confirmed change keeps the time it was first read, so history shows when it really happened.
    /// </summary>
    public sealed class StableReadingFilter
    {
        private readonly TimeSpan _onTime;
        private readonly TimeSpan _offTime;
        private readonly Dictionary<int, MachineFilter> _machines = new Dictionary<int, MachineFilter>();

        /// <summary>Same confirmation time for ON and OFF.</summary>
        public StableReadingFilter(TimeSpan stableTime) : this(stableTime, stableTime) { }

        public StableReadingFilter(TimeSpan onTime, TimeSpan offTime)
        {
            _onTime = onTime < TimeSpan.Zero ? TimeSpan.Zero : onTime;
            _offTime = offTime < TimeSpan.Zero ? TimeSpan.Zero : offTime;
        }

        // How long a new value must be read unchanged before it is confirmed
        private TimeSpan RequiredTime(LampStatus status) => status == LampStatus.Off ? _offTime : _onTime;
        private TimeSpan RequiredTime(bool signal) => signal ? _onTime : _offTime;

        /// <summary>
        /// Feed one poll. Returns the confirmed reading of every machine whose signals are all confirmed
        /// (none right after the agent starts, until each has been read unchanged for the stable time),
        /// with the time its latest confirmed change was first read.
        /// Machines missing from <paramref name="readings"/> (their station did not answer) lose any pending change:
        /// a change has to be read continuously for the whole stable time.
        /// </summary>
        public List<(MachineReading Reading, DateTimeOffset ChangedAt)> Update(IEnumerable<MachineReading> readings, DateTimeOffset now)
        {
            var seen = new HashSet<int>();
            foreach (var r in readings)
            {
                seen.Add(r.MachineId);
                if (!_machines.TryGetValue(r.MachineId, out var machine))
                    _machines[r.MachineId] = machine = new MachineFilter();
                machine.Update(r, now, this);
            }

            var confirmed = new List<(MachineReading, DateTimeOffset)>();
            foreach (var pair in _machines)
            {
                if (!seen.Contains(pair.Key))
                    pair.Value.Interrupt();
                if (pair.Value.TryGetConfirmed(pair.Key, out var reading))
                    confirmed.Add((reading, pair.Value.ChangedAt));
            }
            return confirmed;
        }

        private sealed class MachineFilter
        {
            private readonly StableValue<LampStatus> _status = new StableValue<LampStatus>();
            private readonly StableValue<bool> _outError = new StableValue<bool>();
            private readonly StableValue<bool> _trashError = new StableValue<bool>();
            private readonly StableValue<bool> _otherError = new StableValue<bool>();

            /// <summary>When the latest confirmed change of any of the machine's signals was first read.</summary>
            public DateTimeOffset ChangedAt { get; private set; }

            public void Update(MachineReading r, DateTimeOffset now, StableReadingFilter filter)
            {
                Confirmed(_status.Update(r.Status, now, filter.RequiredTime(r.Status)));
                Confirmed(_outError.Update(r.OutError, now, filter.RequiredTime(r.OutError)));
                Confirmed(_trashError.Update(r.TrashError, now, filter.RequiredTime(r.TrashError)));
                Confirmed(_otherError.Update(r.OtherError, now, filter.RequiredTime(r.OtherError)));
            }

            public void Interrupt()
            {
                _status.Interrupt();
                _outError.Interrupt();
                _trashError.Interrupt();
                _otherError.Interrupt();
            }

            public bool TryGetConfirmed(int machineId, out MachineReading reading)
            {
                reading = default;
                if (_status.Confirmed is not LampStatus status || _outError.Confirmed is not bool outError
                    || _trashError.Confirmed is not bool trashError || _otherError.Confirmed is not bool otherError)
                    return false; // agent just started: not every signal confirmed yet
                reading = new MachineReading(machineId, status, outError, trashError, otherError);
                return true;
            }

            // Several signals confirmed in the same poll were first read less than one poll apart: keep the latest
            private void Confirmed(DateTimeOffset? firstRead)
            {
                if (firstRead > ChangedAt)
                    ChangedAt = firstRead.Value;
            }
        }

        private sealed class StableValue<T> where T : struct
        {
            private T _pending;
            private DateTimeOffset _pendingSince;
            private bool _hasPending;

            public T? Confirmed { get; private set; }

            /// <summary>
            /// <paramref name="stableTime"/> is the time required for <paramref name="value"/> (ON or OFF).
            /// Returns the time the value was first read when it has just been confirmed, otherwise null.
            /// </summary>
            public DateTimeOffset? Update(T value, DateTimeOffset now, TimeSpan stableTime)
            {
                var comparer = EqualityComparer<T>.Default;
                if (Confirmed.HasValue && comparer.Equals(Confirmed.Value, value))
                {
                    _hasPending = false; // back to the confirmed value before the stable time: the pulse is ignored
                    return null;
                }
                if (!_hasPending || !comparer.Equals(_pending, value))
                {
                    _pending = value;
                    _pendingSince = now;
                    _hasPending = true;
                }
                if (now - _pendingSince < stableTime)
                    return null;

                Confirmed = value;
                _hasPending = false;
                return _pendingSince;
            }

            /// <summary>A poll was missed: a pending value has to be read again for the whole stable time.</summary>
            public void Interrupt() => _hasPending = false;
        }
    }
}
