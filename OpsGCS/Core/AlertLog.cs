using System;
using System.Collections.Generic;

namespace MissionPlanner.OpsGCS.Core
{
    public class OpsAlert
    {
        public DateTime TimeUtc;
        public string Vehicle;
        public HealthLevel Level;
        public string Text;
    }

    /// <summary>
    /// Turns health snapshots and vehicle messages into an alert history. Raises an alert when an
    /// item gets worse, a line when it recovers, and one per high-severity status text.
    /// Pure, so the edge-triggering can be unit-tested.
    /// </summary>
    public class AlertLog
    {
        private readonly int _max;
        private readonly LinkedList<OpsAlert> _items = new LinkedList<OpsAlert>();
        private readonly Dictionary<string, HealthLevel> _last = new Dictionary<string, HealthLevel>();
        private readonly Dictionary<string, string> _lastMode = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _lastMsg = new Dictionary<string, string>();
        private readonly object _lock = new object();

        public AlertLog(int max = 60)
        {
            _max = Math.Max(5, max);
        }

        public int Version { get; private set; }

        public void Add(string vehicle, HealthLevel level, string text, DateTime? utc = null)
        {
            lock (_lock)
            {
                _items.AddFirst(new OpsAlert { TimeUtc = utc ?? DateTime.UtcNow, Vehicle = vehicle, Level = level, Text = text });
                while (_items.Count > _max)
                    _items.RemoveLast();
                Version++;
            }
        }

        /// <summary>Feed the latest health for a vehicle; alerts on worsening and recovery only.</summary>
        public void Observe(string vehicle, IEnumerable<HealthItem> health, string mode, DateTime? utc = null)
        {
            if (health != null)
            {
                foreach (var h in health)
                {
                    var key = vehicle + "|" + h.Key;
                    HealthLevel prev;
                    var known = _last.TryGetValue(key, out prev);
                    _last[key] = h.Level;

                    if (h.Level == HealthLevel.Unknown)
                        continue;

                    if (!known || prev == HealthLevel.Unknown)
                    {
                        // first sight: only report if already degraded
                        if (h.Level >= HealthLevel.Caution)
                            Add(vehicle, h.Level, h.Key + " " + h.Value + " - " + h.Detail, utc);
                        continue;
                    }

                    if (h.Level > prev)
                        Add(vehicle, h.Level, h.Key + " " + h.Value + " - " + h.Detail, utc);
                    else if (h.Level < prev && h.Level == HealthLevel.Ok)
                        Add(vehicle, HealthLevel.Ok, h.Key + " recovered (" + h.Value + ")", utc);
                }
            }

            if (!string.IsNullOrEmpty(mode))
            {
                string prevMode;
                if (_lastMode.TryGetValue(vehicle, out prevMode) && prevMode != mode)
                    Add(vehicle, HealthLevel.Unknown, "Mode " + prevMode + " -> " + mode, utc);
                _lastMode[vehicle] = mode;
            }
        }

        /// <summary>Feed the vehicle's latest high-severity status text; repeats are ignored.</summary>
        public void ObserveMessage(string vehicle, string text, bool critical, DateTime? utc = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            string prev;
            if (_lastMsg.TryGetValue(vehicle, out prev) && prev == text)
                return;
            _lastMsg[vehicle] = text;
            Add(vehicle, critical ? HealthLevel.Warning : HealthLevel.Caution, text.Trim(), utc);
        }

        public List<OpsAlert> Snapshot()
        {
            lock (_lock)
                return new List<OpsAlert>(_items);
        }
    }
}
