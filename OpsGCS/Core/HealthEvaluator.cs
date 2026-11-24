using System;
using System.Collections.Generic;
using System.Globalization;

namespace MissionPlanner.OpsGCS.Core
{
    public enum HealthLevel
    {
        Unknown = 0,
        Ok = 1,
        Caution = 2,
        Warning = 3
    }

    public class HealthItem
    {
        public string Key { get; private set; }
        public HealthLevel Level { get; private set; }
        public string Value { get; private set; }
        public string Detail { get; private set; }

        public HealthItem(string key, HealthLevel level, string value, string detail)
        {
            Key = key;
            Level = level;
            Value = value;
            Detail = detail;
        }
    }

    /// <summary>
    /// Plain telemetry snapshot, decoupled from CurrentState so the evaluation logic can be unit-tested.
    /// </summary>
    public class VehicleSnapshot
    {
        public bool Connected;
        public double SecondsSinceLastPacket;
        public int LinkQualityPct;

        public double BatteryVoltage;
        public int BatteryRemainingPct = -1;
        public double BatteryCurrentA;

        public int GpsFixType;
        public int Satellites;
        public double Hdop;

        public double EkfVariance;

        public double VibeX, VibeY, VibeZ;

        public bool Armed;
        public bool Failsafe;
        public string Mode;

        public int CurrentWp;
        public int MissionCount;
        public double WpDistM;
        public double GroundSpeedMs;
        public double TimeInAirSec;
        public double DistToHomeM;
        public double AltM;
    }

    public static class HealthEvaluator
    {
        public static readonly string[] Order = { "LINK", "BATT", "GPS", "EKF", "VIBE" };

        public static List<HealthItem> Evaluate(VehicleSnapshot s, OpsThresholds t)
        {
            if (t == null)
                t = new OpsThresholds();

            var list = new List<HealthItem>
            {
                Link(s, t),
                Battery(s, t),
                Gps(s, t),
                Ekf(s, t),
                Vibe(s, t)
            };
            return list;
        }

        public static HealthLevel Worst(IEnumerable<HealthItem> items)
        {
            var worst = HealthLevel.Unknown;
            foreach (var i in items)
                if (i.Level > worst)
                    worst = i.Level;
            return worst;
        }

        public static HealthItem Link(VehicleSnapshot s, OpsThresholds t)
        {
            if (s == null || !s.Connected)
                return new HealthItem("LINK", HealthLevel.Unknown, "--", "Not connected");

            if (s.SecondsSinceLastPacket > t.LinkStaleSec)
                return new HealthItem("LINK", HealthLevel.Warning, "LOST",
                    string.Format(CultureInfo.InvariantCulture, "No packets for {0:0.0} s", s.SecondsSinceLastPacket));

            var lvl = s.LinkQualityPct < t.LinkWarningPct ? HealthLevel.Warning
                : s.LinkQualityPct < t.LinkCautionPct ? HealthLevel.Caution
                : HealthLevel.Ok;

            return new HealthItem("LINK", lvl, s.LinkQualityPct + "%", "GCS packet reception");
        }

        public static HealthItem Battery(VehicleSnapshot s, OpsThresholds t)
        {
            if (s == null || !s.Connected)
                return new HealthItem("BATT", HealthLevel.Unknown, "--", "Not connected");

            var lvl = HealthLevel.Ok;
            var hasPct = s.BatteryRemainingPct >= 0 && s.BatteryRemainingPct <= 100;

            if (hasPct)
            {
                if (s.BatteryRemainingPct < t.BattWarningPct)
                    lvl = HealthLevel.Warning;
                else if (s.BatteryRemainingPct < t.BattCautionPct)
                    lvl = HealthLevel.Caution;
            }

            string cellText = "";
            if (t.CellCount > 0 && s.BatteryVoltage > 0)
            {
                var cell = s.BatteryVoltage / t.CellCount;
                cellText = string.Format(CultureInfo.InvariantCulture, " ({0:0.00} V/cell)", cell);
                var cellLvl = cell < t.CellWarningV ? HealthLevel.Warning
                    : cell < t.CellCautionV ? HealthLevel.Caution
                    : HealthLevel.Ok;
                if (cellLvl > lvl)
                    lvl = cellLvl;
            }

            if (!hasPct && t.CellCount <= 0)
                lvl = s.BatteryVoltage > 0 ? HealthLevel.Ok : HealthLevel.Unknown;

            var value = hasPct
                ? s.BatteryRemainingPct + "%"
                : string.Format(CultureInfo.InvariantCulture, "{0:0.0}V", s.BatteryVoltage);

            var detail = string.Format(CultureInfo.InvariantCulture, "{0:0.0} V, {1:0.0} A{2}",
                s.BatteryVoltage, s.BatteryCurrentA, cellText);

            return new HealthItem("BATT", lvl, value, detail);
        }

        public static HealthItem Gps(VehicleSnapshot s, OpsThresholds t)
        {
            if (s == null || !s.Connected)
                return new HealthItem("GPS", HealthLevel.Unknown, "--", "Not connected");

            var lvl = HealthLevel.Ok;
            if (s.GpsFixType < t.GpsMinFixType)
                lvl = HealthLevel.Warning;
            else
            {
                if (s.Satellites < t.GpsWarningSats || s.Hdop > t.GpsWarningHdop)
                    lvl = HealthLevel.Warning;
                else if (s.Satellites < t.GpsCautionSats || s.Hdop > t.GpsCautionHdop)
                    lvl = HealthLevel.Caution;
            }

            return new HealthItem("GPS", lvl, FixName(s.GpsFixType),
                string.Format(CultureInfo.InvariantCulture, "{0} sats, HDOP {1:0.0}", s.Satellites, s.Hdop));
        }

        public static HealthItem Ekf(VehicleSnapshot s, OpsThresholds t)
        {
            if (s == null || !s.Connected)
                return new HealthItem("EKF", HealthLevel.Unknown, "--", "Not connected");

            var lvl = s.EkfVariance >= t.EkfWarning ? HealthLevel.Warning
                : s.EkfVariance >= t.EkfCaution ? HealthLevel.Caution
                : HealthLevel.Ok;

            return new HealthItem("EKF", lvl,
                string.Format(CultureInfo.InvariantCulture, "{0:0.00}", s.EkfVariance),
                "Max EKF variance ratio");
        }

        public static HealthItem Vibe(VehicleSnapshot s, OpsThresholds t)
        {
            if (s == null || !s.Connected)
                return new HealthItem("VIBE", HealthLevel.Unknown, "--", "Not connected");

            var max = Math.Max(s.VibeX, Math.Max(s.VibeY, s.VibeZ));
            var lvl = max >= t.VibeWarning ? HealthLevel.Warning
                : max >= t.VibeCaution ? HealthLevel.Caution
                : HealthLevel.Ok;

            return new HealthItem("VIBE", lvl,
                string.Format(CultureInfo.InvariantCulture, "{0:0}", max),
                string.Format(CultureInfo.InvariantCulture, "X {0:0} Y {1:0} Z {2:0} m/s²", s.VibeX, s.VibeY, s.VibeZ));
        }

        public static string FixName(int fix)
        {
            switch (fix)
            {
                case 0:
                case 1: return "NO FIX";
                case 2: return "2D";
                case 3: return "3D";
                case 4: return "DGPS";
                case 5: return "RTK FLT";
                case 6: return "RTK FIX";
                default: return fix.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Mission progress 0..1. ArduPilot item 0 is home, so the flyable items are 1..count-1.</summary>
        public static double MissionProgress(int currentWp, int missionCount)
        {
            if (missionCount <= 1 || currentWp <= 0)
                return 0;
            var flyable = missionCount - 1;
            var done = Math.Min(currentWp - 1, flyable);
            return Math.Max(0, Math.Min(1, done / (double)flyable));
        }

        /// <summary>Seconds to the active waypoint at current ground speed, or -1 when not meaningful.</summary>
        public static double EtaToWpSec(double wpDistM, double groundSpeedMs)
        {
            if (wpDistM <= 0 || groundSpeedMs < 0.5)
                return -1;
            return wpDistM / groundSpeedMs;
        }

        public static string FormatDuration(double seconds)
        {
            if (seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
                return "--:--";
            var ts = TimeSpan.FromSeconds(seconds);
            return ts.TotalHours >= 1
                ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)ts.TotalHours, ts.Minutes, ts.Seconds)
                : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", ts.Minutes, ts.Seconds);
        }
    }
}
