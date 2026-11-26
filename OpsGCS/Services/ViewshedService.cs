using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Threading;
using log4net;
using MissionPlanner.OpsGCS.Core;
using MissionPlanner.Utilities;

namespace MissionPlanner.OpsGCS.Services
{
    public enum ViewshedSource
    {
        /// <summary>From the vehicle: which ground its sensor can see.</summary>
        Sensor,

        /// <summary>From the GCS mast at home: where a vehicle at its current altitude keeps radio line of sight.</summary>
        Radio
    }

    public class ViewshedFrame
    {
        public ViewshedResult Result;
        public Bitmap Image;
        public ViewshedSource Source;
        public string Status;
        public TimeSpan ComputeTime;
    }

    /// <summary>
    /// Background viewshed: recomputes when the observer moves or climbs enough, at most every
    /// MinIntervalSec, using SRTM through Mission Planner's srtm cache. Missing tiles come back
    /// as Unknown cells and trigger a retry while srtm downloads them.
    /// </summary>
    public class ViewshedService : IDisposable
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly ViewshedConfig _cfg;
        private readonly Func<VehicleSnapshot> _vehicle;
        private readonly Thread _thread;
        private volatile bool _run = true;
        private volatile bool _enabled;
        private volatile ViewshedSource _source = ViewshedSource.Sensor;
        private volatile bool _force;

        private double _lastLat = double.NaN, _lastLon, _lastObsAlt, _lastTgtAlt;
        private ViewshedSource _lastSource;
        private DateTime _lastRun = DateTime.MinValue;
        private bool _lastHadUnknown;

        /// <summary>Raised on the worker thread. Null frame = layer should be cleared.</summary>
        public event Action<ViewshedFrame> Updated;

        public ViewshedService(ViewshedConfig cfg, Func<VehicleSnapshot> vehicle)
        {
            _cfg = cfg ?? new ViewshedConfig();
            _vehicle = vehicle;
            _thread = new Thread(Loop) { IsBackground = true, Name = "OpsGCS viewshed", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (_enabled == value)
                    return;
                _enabled = value;
                _force = true;
                if (!value)
                    Raise(null);
            }
        }

        public ViewshedSource Source
        {
            get { return _source; }
            set
            {
                _source = value;
                _force = true;
            }
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
                    log.Error("OpsGCS viewshed", ex);
                }

                Thread.Sleep(500);
            }
        }

        private void Tick()
        {
            var v = _vehicle != null ? _vehicle() : null;
            var source = _source;

            ViewshedRequest req;
            string why;
            if (!BuildRequest(v, source, out req, out why))
            {
                Raise(new ViewshedFrame { Source = source, Status = why });
                return;
            }

            var since = (DateTime.UtcNow - _lastRun).TotalSeconds;
            if (since < _cfg.MinIntervalSec)
                return;

            var tgtAlt = req.TargetAmsl ?? 0;
            var moved = double.IsNaN(_lastLat)
                ? double.MaxValue
                : Viewshed.DistanceM(_lastLat, _lastLon, req.ObserverLat, req.ObserverLon);
            var climbed = Math.Max(Math.Abs(req.ObserverAmsl - _lastObsAlt), Math.Abs(tgtAlt - _lastTgtAlt));

            var due = _force || source != _lastSource || moved >= _cfg.RecomputeMoveM ||
                      climbed >= Math.Max(10, _cfg.RecomputeMoveM / 5) ||
                      (_lastHadUnknown && since >= 5);
            if (!due)
                return;

            _force = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = Viewshed.Compute(req, Terrain);
            sw.Stop();

            _lastLat = req.ObserverLat;
            _lastLon = req.ObserverLon;
            _lastObsAlt = req.ObserverAmsl;
            _lastTgtAlt = tgtAlt;
            _lastSource = source;
            _lastRun = DateTime.UtcNow;
            _lastHadUnknown = res.Unknown > res.Size; // more than a sliver missing: srtm still loading

            if (!_enabled)
                return;

            var total = Math.Max(1, res.Visible + res.Masked + res.Unknown);
            var status = string.Format("{0}: {1:0}% visible, {2:0}% masked{3} · {4:0} km · {5} ms",
                source == ViewshedSource.Sensor ? "Sensor" : "Radio at " + Math.Round(tgtAlt) + " m AMSL",
                100.0 * res.Visible / total, 100.0 * res.Masked / total,
                res.Unknown > 0 ? string.Format(", {0:0}% no terrain", 100.0 * res.Unknown / total) : "",
                req.RadiusM / 1000.0, sw.ElapsedMilliseconds);

            Raise(new ViewshedFrame { Result = res, Image = Render(res), Source = source, Status = status, ComputeTime = sw.Elapsed });
        }

        private bool BuildRequest(VehicleSnapshot v, ViewshedSource source, out ViewshedRequest req, out string why)
        {
            req = null;
            why = null;
            if (v == null || !v.Connected || !v.HasPosition)
            {
                why = "Waiting for vehicle position";
                return false;
            }

            if (source == ViewshedSource.Sensor)
            {
                req = new ViewshedRequest
                {
                    ObserverLat = v.Lat,
                    ObserverLon = v.Lon,
                    ObserverAmsl = v.AltAmsl,
                    RadiusM = _cfg.SensorRangeM,
                    CellM = _cfg.CellM,
                    TargetAgl = 0,
                    RefractionK = _cfg.SensorRefractionK
                };
                return true;
            }

            if (!v.HasHome)
            {
                why = "Radio coverage needs a home position";
                return false;
            }

            // GCS assumed at home; use terrain there when home altitude is not set
            var ground = v.HomeAltAmsl;
            if (Math.Abs(ground) < 0.01)
                ground = Terrain(v.HomeLat, v.HomeLon) ?? 0;

            req = new ViewshedRequest
            {
                ObserverLat = v.HomeLat,
                ObserverLon = v.HomeLon,
                ObserverAmsl = ground + _cfg.GcsMastHeightM,
                RadiusM = _cfg.RadioRangeM,
                CellM = Math.Max(_cfg.CellM, _cfg.RadioRangeM / 300.0), // keep the radio raster bounded
                TargetAmsl = v.AltAmsl,
                RefractionK = _cfg.RadioRefractionK
            };
            return true;
        }

        private static double? Terrain(double lat, double lon)
        {
            var a = srtm.getAltitude(lat, lon);
            if (a == null)
                return null;
            if (a.currenttype == srtm.tiletype.valid)
                return a.alt;
            if (a.currenttype == srtm.tiletype.ocean)
                return 0;
            return null;
        }

        /// <summary>Visible = translucent blue, masked = dark hatch, unknown = faint grey, outside = clear.</summary>
        public static Bitmap Render(ViewshedResult r)
        {
            var n = r.Size;
            var bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, n, n), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var stride = data.Stride / 4;
                var px = new int[stride * n];
                var visible = Color.FromArgb(80, 0x5B, 0xA3, 0xD9).ToArgb();
                var hatchDark = Color.FromArgb(130, 5, 8, 12).ToArgb();
                var hatchLight = Color.FromArgb(55, 5, 8, 12).ToArgb();
                var unknown = Color.FromArgb(45, 0x8A, 0x96, 0xA6).ToArgb();

                for (int row = 0; row < n; row++)
                {
                    for (int col = 0; col < n; col++)
                    {
                        int argb;
                        switch ((ViewshedCell)r.Cells[row * n + col])
                        {
                            case ViewshedCell.Visible: argb = visible; break;
                            case ViewshedCell.Masked: argb = (row + col) % 6 < 2 ? hatchDark : hatchLight; break;
                            case ViewshedCell.Unknown: argb = unknown; break;
                            default: argb = 0; break;
                        }

                        px[row * stride + col] = argb;
                    }
                }

                System.Runtime.InteropServices.Marshal.Copy(px, 0, data.Scan0, px.Length);
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            return bmp;
        }

        private void Raise(ViewshedFrame f)
        {
            var h = Updated;
            if (h != null)
                h(f);
        }

        public void Dispose()
        {
            _run = false;
        }
    }
}
