using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using log4net;
using MissionPlanner.OpsGCS.Core;

namespace MissionPlanner.OpsGCS.Services
{
    /// <summary>
    /// Polls Open-Meteo (model grid: wind, gusts, precipitation, cloud, visibility, QNH) and
    /// aviationweather.gov (nearest METAR: ceiling, visibility, observed wind). Each source fails
    /// on its own; the vehicle's EKF wind is shown regardless, from telemetry.
    /// </summary>
    public class WeatherService : IDisposable
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const string UserAgent = "OpsGCS/0.2 (ArduPilot Mission Planner fork)";

        private readonly WeatherConfig _cfg;
        private readonly Func<GeoPoint> _center;
        private readonly Thread _thread;
        private volatile bool _run = true;
        private volatile bool _enabled;
        private volatile bool _force;

        private DateTime _lastFetch = DateTime.MinValue;
        private DateTime _lastAttempt = DateTime.MinValue;
        private double _lastLat = double.NaN, _lastLon;

        public WeatherSnapshot Latest { get; private set; }

        /// <summary>Raised on the worker thread after each fetch.</summary>
        public event Action<WeatherSnapshot> Updated;

        /// <param name="center">Where to centre the grid: active vehicle, else map centre; null when unknown.</param>
        public WeatherService(WeatherConfig cfg, Func<GeoPoint> center)
        {
            _cfg = cfg ?? new WeatherConfig();
            _center = center;
            _thread = new Thread(Loop) { IsBackground = true, Name = "OpsGCS weather" };
            _thread.Start();
        }

        public bool Enabled
        {
            get { return _enabled; }
            set
            {
                _enabled = value;
                if (value)
                    _force = true;
            }
        }

        public void RefreshNow()
        {
            _force = true;
        }

        private void Loop()
        {
            while (_run)
            {
                try
                {
                    if (_enabled)
                        Tick();
                }
                catch (Exception ex)
                {
                    log.Error("OpsGCS weather", ex);
                }

                Thread.Sleep(1000);
            }
        }

        private void Tick()
        {
            var c = _center != null ? _center() : null;
            if (c == null)
                return;

            var now = DateTime.UtcNow;
            var moved = double.IsNaN(_lastLat) ? double.MaxValue : Viewshed.DistanceM(_lastLat, _lastLon, c.Lat, c.Lon) / 1000.0;
            var age = (now - _lastFetch).TotalMinutes;
            var sinceAttempt = (now - _lastAttempt).TotalSeconds;

            var due = (_force && sinceAttempt > 15) ||
                      age >= _cfg.RefreshMin ||
                      (moved > _cfg.GridSpanKm / 3 && sinceAttempt > 60);
            // after a failure, wait 2 min before the next automatic try
            if (!due || (!_force && Latest != null && (Latest.ModelError != null) && sinceAttempt < 120))
                return;

            _force = false;
            _lastAttempt = now;
            var snap = Fetch(c.Lat, c.Lon);
            if (snap.ModelError == null)
            {
                _lastFetch = now;
                _lastLat = c.Lat;
                _lastLon = c.Lon;
            }

            Latest = snap;
            var h = Updated;
            if (h != null)
                h(snap);
        }

        private WeatherSnapshot Fetch(double lat, double lon)
        {
            var snap = new WeatherSnapshot { FetchedUtc = DateTime.UtcNow };

            double dLat, dLon;
            var pts = WeatherGrid.Points(lat, lon, _cfg.GridSpanKm, _cfg.GridN, out dLat, out dLon);
            snap.GridCellDegLat = dLat;
            snap.GridCellDegLon = dLon;

            try
            {
                var json = Get(WeatherUrls.OpenMeteo(_cfg.OpenMeteoUrl, _cfg.OpenMeteoApiKey, pts));
                snap.Grid = WeatherParse.OpenMeteo(json);
                snap.AtVehicle = WeatherGrid.Closest(snap.Grid, lat, lon);
                if (snap.Grid.Count == 0)
                    snap.ModelError = "Open-Meteo: empty response";
            }
            catch (Exception ex)
            {
                snap.ModelError = "Open-Meteo: " + Short(ex);
                log.Warn("OpsGCS Open-Meteo", ex);
            }

            try
            {
                var url = string.IsNullOrWhiteSpace(_cfg.MetarStation)
                    ? WeatherUrls.MetarAround(_cfg.MetarUrl, lat, lon, _cfg.MetarSearchKm)
                    : WeatherUrls.MetarStation(_cfg.MetarUrl, _cfg.MetarStation);
                var obs = WeatherParse.Metar(Get(url));
                double km;
                snap.Metar = WeatherParse.Nearest(obs, lat, lon, out km);
                snap.MetarDistanceKm = km;
                if (snap.Metar == null)
                    snap.MetarError = string.IsNullOrWhiteSpace(_cfg.MetarStation)
                        ? "No METAR within " + _cfg.MetarSearchKm + " km"
                        : "No METAR for " + _cfg.MetarStation;
            }
            catch (Exception ex)
            {
                snap.MetarError = "METAR: " + Short(ex);
                log.Warn("OpsGCS METAR", ex);
            }

            return snap;
        }

        private static string Get(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UserAgent;
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            // gzip only: .NET Framework's "deflate" decoder expects raw deflate and fails on the
            // zlib-wrapped stream some servers send ("Found invalid data while decoding")
            req.AutomaticDecompression = DecompressionMethods.GZip;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var r = new StreamReader(s, Encoding.UTF8))
            {
                // 204 = no data (e.g. no METAR stations in the box)
                return resp.StatusCode == HttpStatusCode.NoContent ? "" : r.ReadToEnd();
            }
        }

        private static string Short(Exception ex)
        {
            var w = ex as WebException;
            if (w != null && w.Response is HttpWebResponse)
                return "HTTP " + (int)((HttpWebResponse)w.Response).StatusCode;
            return ex.GetType().Name.Replace("Exception", "") + (ex.Message.Length < 60 ? " - " + ex.Message : "");
        }

        public void Dispose()
        {
            _run = false;
        }
    }

    /// <summary>A latitude/longitude pair.</summary>
    public class GeoPoint
    {
        public double Lat, Lon;

        public GeoPoint(double lat, double lon)
        {
            Lat = lat;
            Lon = lon;
        }
    }
}
