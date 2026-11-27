using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace MissionPlanner.OpsGCS.Core
{
    /// <summary>
    /// Health thresholds for the operational dashboard. All values are configurable in opsgcs.json.
    /// </summary>
    public class OpsThresholds
    {
        // battery (percent remaining, as reported by BATTERY_STATUS / SYS_STATUS)
        public int BattCautionPct { get; set; } = 35;
        public int BattWarningPct { get; set; } = 20;

        // per-cell voltage, used when the cell count is configured (CellCount > 0)
        public int CellCount { get; set; } = 0;
        public double CellCautionV { get; set; } = 3.60;
        public double CellWarningV { get; set; } = 3.45;

        // GPS
        public int GpsMinFixType { get; set; } = 3; // 3 = 3D fix
        public int GpsCautionSats { get; set; } = 10;
        public int GpsWarningSats { get; set; } = 6;
        public double GpsCautionHdop { get; set; } = 1.5;
        public double GpsWarningHdop { get; set; } = 2.5;

        // EKF variance ratio (max of vel/pos/compass/terrain), same scale as the stock EKF indicator
        public double EkfCaution { get; set; } = 0.5;
        public double EkfWarning { get; set; } = 0.8;

        // link quality (percent of packets received at GCS) and staleness
        public int LinkCautionPct { get; set; } = 80;
        public int LinkWarningPct { get; set; } = 50;
        public double LinkStaleSec { get; set; } = 3.0;

        // vibration m/s/s
        public double VibeCaution { get; set; } = 30;
        public double VibeWarning { get; set; } = 60;
    }

    public class ViewshedConfig
    {
        /// <summary>Radius around the vehicle for "what can the sensor see", metres.</summary>
        public double SensorRangeM { get; set; } = 5000;

        /// <summary>Radius around the GCS for "where does the radio reach at flight altitude", metres.</summary>
        public double RadioRangeM { get; set; } = 15000;

        /// <summary>GCS antenna height above the ground at home, metres.</summary>
        public double GcsMastHeightM { get; set; } = 6;

        /// <summary>Raster cell size, metres. SRTM is about 30 m, so finer adds cost without detail.</summary>
        public double CellM { get; set; } = 50;

        /// <summary>Recompute when the observer moves this far or changes altitude by this much, metres.</summary>
        public double RecomputeMoveM { get; set; } = 100;

        public double MinIntervalSec { get; set; } = 2;
        public double SensorRefractionK { get; set; } = 0.07;
        public double RadioRefractionK { get; set; } = 0.13;
    }

    public class WeatherConfig
    {
        public double RefreshMin { get; set; } = 10;
        public double StaleMin { get; set; } = 30;

        /// <summary>Wind/precipitation grid around the vehicle (or map centre): span and points per side.</summary>
        public double GridSpanKm { get; set; } = 30;
        public int GridN { get; set; } = 6;

        /// <summary>Fixed METAR station (ICAO). Empty: nearest station within MetarSearchKm.</summary>
        public string MetarStation { get; set; } = "";
        public double MetarSearchKm { get; set; } = 60;

        /// <summary>Open-Meteo endpoint; use https://customer-api.open-meteo.com/v1/forecast with an API key for licensed use.</summary>
        public string OpenMeteoUrl { get; set; } = "";
        public string OpenMeteoApiKey { get; set; } = "";
        public string MetarUrl { get; set; } = "";

        /// <summary>Precipitation (mm, latest model interval) that raises the caution line.</summary>
        public double PrecipCautionMm { get; set; } = 0.5;
    }

    /// <summary>
    /// Deployment configuration, read from opsgcs.json beside the executable.
    /// </summary>
    public class OpsConfig
    {
        public const string FileName = "opsgcs.json";

        public string ProductName { get; set; } = "OPS GCS";
        public string Unit { get; set; } = "";

        /// <summary>SHA-256 (hex) of the engineer PIN. Empty disables engineer mode entirely.</summary>
        public string EngineerPinSha256 { get; set; } = "";

        /// <summary>Start in engineer mode (development installs only).</summary>
        public bool StartAsEngineer { get; set; } = false;

        /// <summary>Show the multi-vehicle fleet table.</summary>
        public bool ShowFleet { get; set; } = true;

        /// <summary>"v2": fleet rail, centre map, HUD/commands/alerts column. "stock": upstream Flight Data layout.</summary>
        public string Layout { get; set; } = "v2";

        /// <summary>"web": operator FLY screen rendered in WebView2 (falls back to Layout v2 if unavailable). "native": WinForms only.</summary>
        public string Ui { get; set; } = "web";

        public bool WebUi
        {
            get { return !string.Equals(Ui, "native", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>
        /// "visual" (default): WebView2 draws into Mission Planner's own window (Window to Visual hosting).
        /// Mission Planner is DPI-unaware, so above 100 % display scaling a separately windowed WebView2
        /// showed a second mouse pointer. "windowed": WebView2's own child window (the previous behaviour).
        /// </summary>
        public string WebHosting { get; set; } = "visual";

        /// <summary>PLAN: minimum terrain clearance (m) on every cruise leg; lower blocks Write. FLY shades below it.</summary>
        public double PlanMinClearanceM { get; set; } = 60;

        /// <summary>PLAN: default altitude (m) and frame ("REL" above home, "AGL" terrain, "AMSL") for new items.</summary>
        public double PlanDefaultAltM { get; set; } = 120;

        public string PlanDefaultFrame { get; set; } = "REL";

        public bool WebVisualHosting
        {
            get { return !string.Equals(WebHosting, "windowed", StringComparison.OrdinalIgnoreCase); }
        }

        public bool LayoutV2
        {
            get { return !string.Equals(Layout, "stock", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>Map right-click items an operator keeps (control Name values in FlightData).</summary>
        public List<string> OperatorMapMenuAllowlist { get; set; } = new List<string>
        {
            "goHereToolStripMenuItem",
            "flyToHereAltToolStripMenuItem",
            "flyToCoordsToolStripMenuItem",
            "pointCameraHereToolStripMenuItem",
            "triggerCameraToolStripMenuItem",
            "takeOffToolStripMenuItem"
        };

        public OpsThresholds Thresholds { get; set; } = new OpsThresholds();
        public ViewshedConfig Viewshed { get; set; } = new ViewshedConfig();
        public WeatherConfig Weather { get; set; } = new WeatherConfig();

        public static OpsConfig Load(string directory)
        {
            try
            {
                var path = Path.Combine(directory ?? "", FileName);
                if (File.Exists(path))
                {
                    var cfg = JsonConvert.DeserializeObject<OpsConfig>(File.ReadAllText(path));
                    if (cfg != null)
                    {
                        if (cfg.Thresholds == null)
                            cfg.Thresholds = new OpsThresholds();
                        if (cfg.Viewshed == null)
                            cfg.Viewshed = new ViewshedConfig();
                        if (cfg.Weather == null)
                            cfg.Weather = new WeatherConfig();
                        if (cfg.OperatorMapMenuAllowlist == null)
                            cfg.OperatorMapMenuAllowlist = new List<string>();
                        return cfg;
                    }
                }
            }
            catch
            {
                // malformed file: fall back to defaults (operator mode, no engineer unlock)
            }

            return new OpsConfig();
        }

        public bool EngineerModeAvailable
        {
            get { return !string.IsNullOrWhiteSpace(EngineerPinSha256); }
        }

        public bool CheckPin(string pin)
        {
            if (!EngineerModeAvailable || pin == null)
                return false;

            return string.Equals(Sha256Hex(pin), EngineerPinSha256.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public static string Sha256Hex(string text)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
