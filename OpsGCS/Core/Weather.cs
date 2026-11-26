using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace MissionPlanner.OpsGCS.Core
{
    /// <summary>Model weather at one grid point (Open-Meteo "current" block).</summary>
    public class WeatherPoint
    {
        public double Lat, Lon;
        public DateTime? TimeUtc;
        public double? TempC, PrecipMm, CloudPct, PressureHpa, VisibilityM;
        public double? WindKt, WindDirDeg, GustKt;
    }

    /// <summary>Decoded METAR from aviationweather.gov.</summary>
    public class MetarObs
    {
        public string Station, Name, Raw, Wx;
        public double Lat, Lon;
        public DateTime ObsUtc;
        public int? WindDirDeg; // null = variable
        public int? WindKt, GustKt;
        public double? VisibilityKm;
        public bool VisibilityOrMore;
        public int? CeilingFt;
        public string CeilingCover; // BKN, OVC, OVX, VV, or null for no ceiling
        public double? AltimHpa, TempC;
    }

    /// <summary>What the weather card shows, merged from the sources that answered.</summary>
    public class WeatherSnapshot
    {
        public DateTime FetchedUtc;
        public List<WeatherPoint> Grid = new List<WeatherPoint>();
        public WeatherPoint AtVehicle;
        public MetarObs Metar;
        public double MetarDistanceKm;
        public string ModelError, MetarError;
        public double GridCellDegLat, GridCellDegLon;
    }

    public static class WeatherParse
    {
        /// <summary>Parses an Open-Meteo forecast response: one object, or an array for several coordinates.</summary>
        public static List<WeatherPoint> OpenMeteo(string json)
        {
            var list = new List<WeatherPoint>();
            if (string.IsNullOrWhiteSpace(json))
                return list;

            var root = JToken.Parse(json);
            var items = root is JArray ? (IEnumerable<JToken>)root.Children() : new[] { root };
            foreach (var item in items)
            {
                var cur = item["current"];
                if (cur == null)
                    continue;

                var p = new WeatherPoint
                {
                    Lat = item.Value<double?>("latitude") ?? 0,
                    Lon = item.Value<double?>("longitude") ?? 0,
                    TempC = Num(cur["temperature_2m"]),
                    PrecipMm = Num(cur["precipitation"]),
                    CloudPct = Num(cur["cloud_cover"]),
                    PressureHpa = Num(cur["pressure_msl"]),
                    VisibilityM = Num(cur["visibility"]),
                    WindKt = Num(cur["wind_speed_10m"]),
                    WindDirDeg = Num(cur["wind_direction_10m"]),
                    GustKt = Num(cur["wind_gusts_10m"])
                };

                DateTime t;
                var ts = (string)cur["time"];
                if (ts != null && DateTime.TryParse(ts, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t))
                    p.TimeUtc = t;

                list.Add(p);
            }

            return list;
        }

        /// <summary>Parses an aviationweather.gov /api/data/metar?format=json response.</summary>
        public static List<MetarObs> Metar(string json)
        {
            var list = new List<MetarObs>();
            if (string.IsNullOrWhiteSpace(json))
                return list;

            var root = JToken.Parse(json);
            if (!(root is JArray))
                return list;

            foreach (var m in root.Children())
            {
                var o = new MetarObs
                {
                    Station = (string)m["icaoId"],
                    Name = (string)m["name"],
                    Raw = (string)m["rawOb"],
                    Wx = (string)m["wxString"],
                    Lat = Num(m["lat"]) ?? 0,
                    Lon = Num(m["lon"]) ?? 0,
                    WindKt = Int(m["wspd"]),
                    GustKt = Int(m["wgst"]),
                    AltimHpa = Num(m["altim"]),
                    TempC = Num(m["temp"])
                };

                var obs = Num(m["obsTime"]);
                if (obs.HasValue)
                    o.ObsUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(obs.Value);

                var wdir = m["wdir"];
                if (wdir != null && wdir.Type != JTokenType.String && wdir.Type != JTokenType.Null)
                    o.WindDirDeg = Int(wdir);

                // visibility in statute miles, possibly "10+"
                var vis = m["visib"];
                if (vis != null && vis.Type != JTokenType.Null)
                {
                    var vs = vis.ToString().Trim();
                    o.VisibilityOrMore = vs.EndsWith("+");
                    double sm;
                    if (double.TryParse(vs.TrimEnd('+'), NumberStyles.Float, CultureInfo.InvariantCulture, out sm))
                        o.VisibilityKm = Math.Round(sm * 1.609344, 1);
                }

                // ceiling: lowest broken / overcast layer, or vertical visibility
                var clouds = m["clouds"] as JArray;
                if (clouds != null)
                {
                    foreach (var layer in clouds)
                    {
                        var cover = ((string)layer["cover"] ?? "").ToUpperInvariant();
                        var bas = Int(layer["base"]);
                        if ((cover == "BKN" || cover == "OVC" || cover == "OVX") && bas.HasValue &&
                            (!o.CeilingFt.HasValue || bas.Value < o.CeilingFt.Value))
                        {
                            o.CeilingFt = bas.Value;
                            o.CeilingCover = cover;
                        }
                    }
                }

                var vv = Int(m["vertVis"]);
                if (vv.HasValue && (!o.CeilingFt.HasValue || vv.Value < o.CeilingFt.Value))
                {
                    o.CeilingFt = vv.Value;
                    o.CeilingCover = "VV";
                }

                list.Add(o);
            }

            return list;
        }

        public static MetarObs Nearest(IEnumerable<MetarObs> obs, double lat, double lon, out double distanceKm)
        {
            distanceKm = double.NaN;
            MetarObs best = null;
            foreach (var o in obs ?? Enumerable.Empty<MetarObs>())
            {
                var d = Viewshed.DistanceM(lat, lon, o.Lat, o.Lon) / 1000.0;
                if (best == null || d < distanceKm)
                {
                    best = o;
                    distanceKm = d;
                }
            }

            return best;
        }

        private static double? Num(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null)
                return null;
            if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer)
                return t.Value<double>();
            double d;
            return double.TryParse(t.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : (double?)null;
        }

        private static int? Int(JToken t)
        {
            var d = Num(t);
            return d.HasValue ? (int?)Math.Round(d.Value) : null;
        }
    }

    public static class WeatherGrid
    {
        /// <summary>n x n points spanning spanKm, centred on (lat, lon); row-major from the north-west.</summary>
        public static List<KeyValuePair<double, double>> Points(double lat, double lon, double spanKm, int n,
            out double cellDegLat, out double cellDegLon)
        {
            n = Math.Max(1, Math.Min(20, n));
            var mLat = 111320.0;
            var mLon = Math.Max(1, 111320.0 * Math.Cos(lat * Math.PI / 180));
            var cellM = spanKm * 1000.0 / n;
            cellDegLat = cellM / mLat;
            cellDegLon = cellM / mLon;

            var pts = new List<KeyValuePair<double, double>>();
            var off = (n - 1) / 2.0;
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                    pts.Add(new KeyValuePair<double, double>(
                        Math.Round(lat + (off - r) * cellDegLat, 4),
                        Math.Round(lon + (c - off) * cellDegLon, 4)));
            return pts;
        }

        public static WeatherPoint Wettest(IEnumerable<WeatherPoint> grid)
        {
            WeatherPoint best = null;
            foreach (var p in grid ?? Enumerable.Empty<WeatherPoint>())
                if (p.PrecipMm.HasValue && (best == null || p.PrecipMm.Value > best.PrecipMm.Value))
                    best = p;
            return best;
        }

        public static WeatherPoint Closest(IEnumerable<WeatherPoint> grid, double lat, double lon)
        {
            WeatherPoint best = null;
            var bestD = double.MaxValue;
            foreach (var p in grid ?? Enumerable.Empty<WeatherPoint>())
            {
                var d = Viewshed.DistanceM(lat, lon, p.Lat, p.Lon);
                if (d < bestD)
                {
                    bestD = d;
                    best = p;
                }
            }

            return best;
        }
    }

    public static class WeatherUrls
    {
        public const string OpenMeteoDefault = "https://api.open-meteo.com/v1/forecast";
        public const string MetarDefault = "https://aviationweather.gov/api/data/metar";

        public static string OpenMeteo(string baseUrl, string apiKey, IEnumerable<KeyValuePair<double, double>> points)
        {
            var inv = CultureInfo.InvariantCulture;
            var pts = points.ToList();
            var lats = string.Join(",", pts.Select(p => p.Key.ToString("0.####", inv)));
            var lons = string.Join(",", pts.Select(p => p.Value.ToString("0.####", inv)));
            var url = (string.IsNullOrWhiteSpace(baseUrl) ? OpenMeteoDefault : baseUrl.Trim()) +
                      "?latitude=" + lats + "&longitude=" + lons +
                      "&current=temperature_2m,precipitation,cloud_cover,pressure_msl,visibility," +
                      "wind_speed_10m,wind_direction_10m,wind_gusts_10m&wind_speed_unit=kn&timezone=GMT";
            if (!string.IsNullOrWhiteSpace(apiKey))
                url += "&apikey=" + Uri.EscapeDataString(apiKey.Trim());
            return url;
        }

        /// <summary>METARs inside a box of +-radiusKm around a point (bbox order: minLat,minLon,maxLat,maxLon).</summary>
        public static string MetarAround(string baseUrl, double lat, double lon, double radiusKm)
        {
            var inv = CultureInfo.InvariantCulture;
            var dLat = radiusKm / 111.32;
            var dLon = radiusKm / Math.Max(0.01, 111.32 * Math.Cos(lat * Math.PI / 180));
            return (string.IsNullOrWhiteSpace(baseUrl) ? MetarDefault : baseUrl.Trim()) +
                   string.Format(inv, "?bbox={0:0.###},{1:0.###},{2:0.###},{3:0.###}&format=json",
                       lat - dLat, lon - dLon, lat + dLat, lon + dLon);
        }

        public static string MetarStation(string baseUrl, string icao)
        {
            return (string.IsNullOrWhiteSpace(baseUrl) ? MetarDefault : baseUrl.Trim()) +
                   "?ids=" + Uri.EscapeDataString(icao.Trim().ToUpperInvariant()) + "&format=json";
        }
    }

    public static class WindBarb
    {
        /// <summary>Standard barb: speed rounded to 5 kt, then pennants of 50, full barbs of 10, half of 5.</summary>
        public static void Glyph(double knots, out int pennants, out int full, out int half)
        {
            var k = (int)(Math.Round(Math.Max(0, knots) / 5.0) * 5);
            pennants = k / 50;
            k -= pennants * 50;
            full = k / 10;
            k -= full * 10;
            half = k >= 5 ? 1 : 0;
        }
    }
}
