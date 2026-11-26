using System;

namespace MissionPlanner.OpsGCS.Core
{
    public enum ViewshedCell : byte
    {
        Outside = 0,
        Visible = 1,
        Masked = 2,
        Unknown = 3
    }

    public class ViewshedRequest
    {
        public double ObserverLat;
        public double ObserverLon;

        /// <summary>Observer height above mean sea level, metres.</summary>
        public double ObserverAmsl;

        public double RadiusM = 5000;
        public double CellM = 50;

        /// <summary>Target height above the local ground (0 = "can the observer see the ground here").</summary>
        public double TargetAgl = 0;

        /// <summary>Fixed target altitude AMSL; overrides TargetAgl (radio coverage at a flight altitude).</summary>
        public double? TargetAmsl;

        /// <summary>Atmospheric refraction coefficient: 0.13 for VHF/UHF radio, about 0.07 for optical.</summary>
        public double RefractionK = 0.13;
    }

    /// <summary>
    /// Square raster centred on the observer, row 0 = north edge, column 0 = west edge.
    /// </summary>
    public class ViewshedResult
    {
        public int Size;
        public byte[] Cells;
        public double North, South, West, East;
        public int Visible, Masked, Unknown;
        public int TerrainSamples;

        public ViewshedCell At(int row, int col)
        {
            return (ViewshedCell)Cells[row * Size + col];
        }
    }

    /// <summary>
    /// Radial line-of-sight viewshed over a terrain callback. Pure (no Mission Planner types) so it
    /// can be unit-tested with synthetic terrain; the caller supplies SRTM via <c>terrain</c>.
    /// Earth curvature and refraction use the effective-radius model R/(1-k).
    /// </summary>
    public static class Viewshed
    {
        public const double EarthRadiusM = 6371000.0;
        private const double MetresPerDegLat = 111320.0;

        /// <param name="terrain">Ground height AMSL in metres at (lat, lon), or null when unknown.</param>
        public static ViewshedResult Compute(ViewshedRequest r, Func<double, double, double?> terrain)
        {
            if (r == null) throw new ArgumentNullException("r");
            if (terrain == null) throw new ArgumentNullException("terrain");

            var cell = Math.Max(5.0, r.CellM);
            var half = (int)Math.Ceiling(r.RadiusM / cell);
            var size = half * 2 + 1;
            var c = half;

            var mPerDegLon = MetresPerDegLat * Math.Cos(r.ObserverLat * Math.PI / 180.0);
            if (mPerDegLon < 1) mPerDegLon = 1;

            var result = new ViewshedResult
            {
                Size = size,
                Cells = new byte[size * size],
                North = r.ObserverLat + (half + 0.5) * cell / MetresPerDegLat,
                South = r.ObserverLat - (half + 0.5) * cell / MetresPerDegLat,
                West = r.ObserverLon - (half + 0.5) * cell / mPerDegLon,
                East = r.ObserverLon + (half + 0.5) * cell / mPerDegLon
            };

            // terrain is sampled once per cell, lazily, at the cell centre
            var height = new double[size * size];
            var fetched = new bool[size * size];

            Func<int, double?> ground = idx =>
            {
                if (!fetched[idx])
                {
                    fetched[idx] = true;
                    var row = idx / size;
                    var col = idx % size;
                    var lat = r.ObserverLat + (c - row) * cell / MetresPerDegLat;
                    var lon = r.ObserverLon + (col - c) * cell / mPerDegLon;
                    var h = terrain(lat, lon);
                    height[idx] = h.HasValue && !double.IsNaN(h.Value) ? h.Value : double.NaN;
                    result.TerrainSamples++;
                }

                return double.IsNaN(height[idx]) ? (double?)null : height[idx];
            };

            var reff = EarthRadiusM / Math.Max(0.01, 1.0 - r.RefractionK);
            var radius2 = r.RadiusM * r.RadiusM;

            // enough rays that neighbouring rays are at most ~half a cell apart at the rim
            var rays = Math.Max(360, (int)Math.Ceiling(2 * Math.PI * r.RadiusM / (cell * 0.5)));
            var step = cell * 0.5;

            // observer cell is visible by definition
            result.Cells[c * size + c] = (byte)ViewshedCell.Visible;

            for (int k = 0; k < rays; k++)
            {
                var az = 2 * Math.PI * k / rays;
                var sinA = Math.Sin(az);
                var cosA = Math.Cos(az);
                var maxAngle = double.NegativeInfinity;
                var uncertain = false;
                var lastIdx = -1;

                for (var s = step; s <= r.RadiusM; s += step)
                {
                    var dx = s * sinA; // east
                    var dy = s * cosA; // north
                    var col = c + (int)Math.Round(dx / cell);
                    var row = c - (int)Math.Round(dy / cell);
                    if (row < 0 || row >= size || col < 0 || col >= size)
                        break;

                    var idx = row * size + col;
                    if (idx == lastIdx)
                        continue;
                    lastIdx = idx;

                    var drop = s * s / (2 * reff);
                    var g = ground(idx);

                    if (!g.HasValue)
                    {
                        // without ground height the ray beyond this point cannot be trusted
                        uncertain = true;
                        if (result.Cells[idx] == (byte)ViewshedCell.Outside)
                            result.Cells[idx] = (byte)ViewshedCell.Unknown;
                        continue;
                    }

                    var terrainAngle = (g.Value - drop - r.ObserverAmsl) / s;
                    var target = r.TargetAmsl.HasValue ? r.TargetAmsl.Value : g.Value + r.TargetAgl;
                    var targetAngle = (target - drop - r.ObserverAmsl) / s;

                    ViewshedCell verdict;
                    if (targetAngle >= maxAngle - 1e-9)
                        verdict = uncertain ? ViewshedCell.Unknown : ViewshedCell.Visible;
                    else
                        verdict = uncertain ? ViewshedCell.Unknown : ViewshedCell.Masked;

                    // a cell any ray sees is visible; masked only overrides outside/unknown
                    var cur = (ViewshedCell)result.Cells[idx];
                    if (verdict == ViewshedCell.Visible ||
                        (verdict == ViewshedCell.Masked && cur != ViewshedCell.Visible) ||
                        (verdict == ViewshedCell.Unknown && cur == ViewshedCell.Outside))
                        result.Cells[idx] = (byte)verdict;

                    if (terrainAngle > maxAngle)
                        maxAngle = terrainAngle;
                }
            }

            // clip to the circle and count
            for (int row = 0; row < size; row++)
            {
                for (int col = 0; col < size; col++)
                {
                    var idx = row * size + col;
                    var ex = (col - c) * cell;
                    var ny = (c - row) * cell;
                    if (ex * ex + ny * ny > radius2)
                    {
                        result.Cells[idx] = (byte)ViewshedCell.Outside;
                        continue;
                    }

                    switch ((ViewshedCell)result.Cells[idx])
                    {
                        case ViewshedCell.Visible: result.Visible++; break;
                        case ViewshedCell.Masked: result.Masked++; break;
                        case ViewshedCell.Unknown: result.Unknown++; break;
                    }
                }
            }

            return result;
        }

        /// <summary>Great-circle distance in metres (haversine).</summary>
        public static double DistanceM(double lat1, double lon1, double lat2, double lon2)
        {
            var p1 = lat1 * Math.PI / 180;
            var p2 = lat2 * Math.PI / 180;
            var dp = p2 - p1;
            var dl = (lon2 - lon1) * Math.PI / 180;
            var a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return 2 * EarthRadiusM * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }

        /// <summary>Initial bearing from point 1 to point 2, degrees 0..360.</summary>
        public static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
        {
            var p1 = lat1 * Math.PI / 180;
            var p2 = lat2 * Math.PI / 180;
            var dl = (lon2 - lon1) * Math.PI / 180;
            var y = Math.Sin(dl) * Math.Cos(p2);
            var x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
            var b = Math.Atan2(y, x) * 180 / Math.PI;
            return (b + 360) % 360;
        }

        public static string CompassPoint(double bearingDeg)
        {
            var pts = new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            var i = (int)Math.Round(((bearingDeg % 360) + 360) % 360 / 45.0) % 8;
            return pts[i];
        }
    }
}
