using System;
using System.Collections.Generic;
using System.Linq;
using MissionPlanner.OpsGCS.Core;

namespace MissionPlanner.OpsGCS.WebUI
{
    /// <summary>One mission item, reduced to what the map and profile need.</summary>
    public class MissionItem
    {
        public int Seq;
        public ushort Command;
        public byte Frame;
        public double Lat, Lon;
        public double Z;
    }

    public class MissionLeg
    {
        public int To;
        public double Len;

        /// <summary>Lowest planned height above terrain on the leg (m), and where (distance from start).</summary>
        public double MinClear = double.MaxValue;
        public double MinClearD;

        /// <summary>False for climb-out from home and approach legs into a landing, where low clearance is expected.</summary>
        public bool Checked = true;
    }

    public class ProfilePoint
    {
        public double D, Amsl, Terrain;
    }

    public class MissionProfileResult
    {
        public List<MissionItem> NavItems = new List<MissionItem>();
        public List<MissionLeg> Legs = new List<MissionLeg>();
        public List<ProfilePoint> Profile = new List<ProfilePoint>();
        public List<ProfilePoint> WpMarks = new List<ProfilePoint>();
        public double TotalM;

        /// <summary>Samples where no terrain data existed (the last known height was carried over).</summary>
        public int TerrainMissing;
    }

    /// <summary>
    /// Planned altitude (AMSL) and terrain along the route, for the timeline. Pure: terrain comes
    /// from a callback so it can be unit-tested; Mission Planner supplies SRTM.
    /// </summary>
    public static class MissionProfile
    {
        // MAV_CMD navigation items that carry a position and are flown through
        private static readonly HashSet<ushort> NavWithPosition = new HashSet<ushort>
        {
            16, // WAYPOINT
            17, // LOITER_UNLIM
            18, // LOITER_TURNS
            19, // LOITER_TIME
            21, // LAND
            22, // TAKEOFF
            31, // LOITER_TO_ALT
            82, // SPLINE_WAYPOINT
            84, // VTOL_TAKEOFF
            85  // VTOL_LAND
        };

        public static bool IsNav(MissionItem m)
        {
            return m.Seq == 0 || (NavWithPosition.Contains(m.Command) && (Math.Abs(m.Lat) > 1e-7 || Math.Abs(m.Lon) > 1e-7));
        }

        /// <summary>Planned altitude AMSL from the item's frame.</summary>
        public static double PlannedAmsl(MissionItem m, double homeAmsl, double? terrain)
        {
            switch (m.Frame)
            {
                case 0: // GLOBAL
                case 5: // GLOBAL_INT
                    return m.Z;
                case 10: // GLOBAL_TERRAIN_ALT
                case 11: // GLOBAL_TERRAIN_ALT_INT
                    return (terrain ?? homeAmsl) + m.Z;
                default: // GLOBAL_RELATIVE_ALT (3) / _INT (6) and anything else: relative to home
                    return homeAmsl + m.Z;
            }
        }

        private static bool IsTerrainFrame(byte frame)
        {
            return frame == 10 || frame == 11;
        }

        private static bool IsLanding(ushort cmd)
        {
            return cmd == 21 || cmd == 85; // LAND, VTOL_LAND
        }

        public static MissionProfileResult Build(IList<MissionItem> items, double homeAmsl, Func<double, double, double?> terrain, int maxSamples = 300)
        {
            var r = new MissionProfileResult();
            if (items == null || items.Count == 0)
                return r;

            var nav = items.OrderBy(i => i.Seq).Where(IsNav).ToList();
            // TAKEOFF items often carry 0,0 in some planners; IsNav already drops positionless ones
            r.NavItems = nav;
            if (nav.Count < 2)
                return r;

            var total = 0.0;
            for (int i = 1; i < nav.Count; i++)
            {
                var len = Viewshed.DistanceM(nav[i - 1].Lat, nav[i - 1].Lon, nav[i].Lat, nav[i].Lon);
                r.Legs.Add(new MissionLeg
                {
                    To = nav[i].Seq,
                    Len = len,
                    // climb-out from home and the approach into a landing are expected to be low
                    Checked = nav[i - 1].Seq != 0 && !IsLanding(nav[i].Command)
                });
                total += len;
            }

            r.TotalM = total;
            var step = Math.Max(30.0, total / Math.Max(10, maxSamples));

            double lastTerrain = homeAmsl;
            Func<double, double, double> ground = (la, lo) =>
            {
                var t = terrain != null ? terrain(la, lo) : null;
                if (t.HasValue && !double.IsNaN(t.Value))
                    lastTerrain = t.Value;
                else
                    r.TerrainMissing++;
                return lastTerrain;
            };

            var d0 = 0.0;
            for (int i = 0; i < nav.Count; i++)
            {
                var a = nav[i];
                var aGround = ground(a.Lat, a.Lon);
                var aAlt = a.Seq == 0 ? homeAmsl : PlannedAmsl(a, homeAmsl, aGround);
                r.WpMarks.Add(new ProfilePoint { D = d0, Amsl = aAlt, Terrain = aGround });
                if (i == nav.Count - 1)
                    break;

                var b = nav[i + 1];
                var leg = r.Legs[i];
                var len = leg.Len;
                var bGround = ground(b.Lat, b.Lon);
                var bAlt = PlannedAmsl(b, homeAmsl, bGround);
                // ArduPilot holds height above terrain between two terrain-frame items; otherwise AMSL is interpolated
                var follow = a.Seq != 0 && IsTerrainFrame(a.Frame) && IsTerrainFrame(b.Frame);
                var n = Math.Max(1, (int)Math.Ceiling(len / step));
                for (int k = 0; k <= n; k++)
                {
                    var f = (double)k / n;
                    var la = a.Lat + (b.Lat - a.Lat) * f;
                    var lo = a.Lon + (b.Lon - a.Lon) * f;
                    var g = k == 0 ? aGround : k == n ? bGround : ground(la, lo);
                    var amsl = follow ? g + a.Z + (b.Z - a.Z) * f : aAlt + (bAlt - aAlt) * f;
                    if (k < n)
                        r.Profile.Add(new ProfilePoint { D = d0 + len * f, Amsl = amsl, Terrain = g });
                    var clear = amsl - g;
                    if (clear < leg.MinClear)
                    {
                        leg.MinClear = clear;
                        leg.MinClearD = d0 + len * f;
                    }
                }

                d0 += len;
            }

            var last = nav[nav.Count - 1];
            var lastGround = ground(last.Lat, last.Lon);
            r.Profile.Add(new ProfilePoint
            {
                D = total,
                Amsl = PlannedAmsl(last, homeAmsl, lastGround),
                Terrain = lastGround
            });
            return r;
        }

        /// <summary>Cheap fingerprint so the profile is rebuilt only when the mission changes.</summary>
        public static long Fingerprint(IEnumerable<MissionItem> items, double homeAmsl)
        {
            unchecked
            {
                long h = 17;
                foreach (var i in items.OrderBy(x => x.Seq))
                {
                    h = h * 31 + i.Seq;
                    h = h * 31 + i.Command;
                    h = h * 31 + i.Frame;
                    h = h * 31 + (long)(i.Lat * 1e7);
                    h = h * 31 + (long)(i.Lon * 1e7);
                    h = h * 31 + (long)(i.Z * 100);
                }

                return h * 31 + (long)(homeAmsl * 10);
            }
        }
    }
}
