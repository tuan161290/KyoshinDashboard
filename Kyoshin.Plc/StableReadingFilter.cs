using System;
using System.Collections.Generic;

namespace Kyoshin.Plc
{
    /// <summary>
    /// Confirms what is read from the PLC inputs only after it has stayed the same for a minimum time:
    /// shorter pulses, flicker and contact bounce are ignored. The time is set separately for
    /// ON (Plc:OnConfirmSeconds — an error signal turning on, or the machine changing to a lamp colour) and
    /// OFF (Plc:OffConfirmSeconds — an error signal turning off, or the machine going dark = Off).
    /// A change is confirmed at the time the minimum is reached: ON read at 10:00:00 and still ON at 10:00:03
    /// = ON from 10:00:03 (the caller stamps the change with that time).
    /// A machine's lamp status (the colour decoded from RED/YEL/GRN) and each of its 3 error signals are confirmed
    /// separately, so a colour change GRN → RED is one change (no brief "Off" in between) and a flickering error
    /// signal doesn't hold back the lamp status.
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
        /// (none right after the agent starts, until each has been read unchanged for the required time).
        /// Machines missing from <paramref name="readings"/> (their station did not answer) lose any pending change:
        /// a change has to be read continuously for the whole required time.
        /// </summary>
        public List<MachineReading> Update(IEnumerable<MachineReading> readings, DateTimeOffset now)
        {
            var seen = new HashSet<int>();
            foreach (var r in readings)
            {
                seen.Add(r.MachineId);
                if (!_machines.TryGetValue(r.MachineId, out var machine))
                    _machines[r.MachineId] = machine = new MachineFilter();
                machine.Status.Update(r.Status, now, RequiredTime(r.Status));
                machine.OutError.Update(r.OutError, now, RequiredTime(r.OutError));
                machine.TrashError.Update(r.TrashError, now, RequiredTime(r.TrashError));
                machine.OtherError.Update(r.OtherError, now, RequiredTime(r.OtherError));
            }

            var confirmed = new List<MachineReading>();
            foreach (var pair in _machines)
            {
                var m = pair.Value;
                if (!seen.Contains(pair.Key))
                    m.Interrupt();
                if (m.Status.Confirmed is LampStatus status && m.OutError.Confirmed is bool outError
                    && m.TrashError.Confirmed is bool trashError && m.OtherError.Confirmed is bool otherError)
                    confirmed.Add(new MachineReading(pair.Key, status, outError, trashError, otherError));
            }
            return confirmed;
        }

        private sealed class MachineFilter
        {
            public readonly StableValue<LampStatus> Status = new StableValue<LampStatus>();
            public readonly StableValue<bool> OutError = new StableValue<bool>();
            public readonly StableValue<bool> TrashError = new StableValue<bool>();
            public readonly StableValue<bool> OtherError = new StableValue<bool>();

            public void Interrupt()
            {
                Status.Interrupt();
                OutError.Interrupt();
                TrashError.Interrupt();
                OtherError.Interrupt();
            }
        }

        private sealed class StableValue<T> where T : struct
        {
            private T _pending;
            private DateTimeOffset _pendingSince;
            private bool _hasPending;

            /// <summary>Last confirmed value (null until the first value has been read for the required time).</summary>
            public T? Confirmed { get; private set; }

            public void Update(T value, DateTimeOffset now, TimeSpan requiredTime)
            {
                var comparer = EqualityComparer<T>.Default;
                if (Confirmed.HasValue && comparer.Equals(Confirmed.Value, value))
                {
                    _hasPending = false; // back to the confirmed value before the required time: the pulse is ignored
                    return;
                }
                if (!_hasPending || !comparer.Equals(_pending, value))
                {
                    _pending = value;      // a new value: start timing it
                    _pendingSince = now;
                    _hasPending = true;
                }
                if (now - _pendingSince >= requiredTime)
                {
                    Confirmed = value;     // read unchanged for the required time: confirmed now
                    _hasPending = false;
                }
            }

            /// <summary>A poll was missed: a pending value has to be read again for the whole required time.</summary>
            public void Interrupt() => _hasPending = false;
        }
    }
}
