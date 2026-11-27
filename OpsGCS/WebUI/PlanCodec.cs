using System;
using System.Collections.Generic;
using System.Linq;
using MissionPlanner.Utilities;
using Newtonsoft.Json.Linq;

namespace MissionPlanner.OpsGCS.WebUI
{
    /// <summary>
    /// Converts between the PLAN page's items and MAVLink mission items, and checks what the page sends
    /// before anything reaches a vehicle. Pure for testing.
    /// Page item: { cmd, frame, lat, lon, alt, p1, p2, p3, p4 } with lat/lon in degrees, alt in metres.
    /// Item 0 on the vehicle is home; the page keeps home apart from its item list.
    /// </summary>
    public static class PlanCodec
    {
        public const int MaxItems = 718; // ArduPilot's largest mission storage; the vehicle still has the last word

        private static readonly HashSet<byte> Frames = new HashSet<byte> { 0, 3, 10 };

        public static JObject ToJson(Locationwp w)
        {
            return new JObject
            {
                ["cmd"] = w.id,
                ["frame"] = w.frame,
                ["lat"] = Math.Round(w.lat, 7),
                ["lon"] = Math.Round(w.lng, 7),
                ["alt"] = Math.Round(w.alt, 2),
                ["p1"] = w.p1, ["p2"] = w.p2, ["p3"] = w.p3, ["p4"] = w.p4
            };
        }

        /// <summary>Splits a downloaded mission into home (item 0) and the rest.</summary>
        public static void Split(IList<Locationwp> mission, out JObject home, out JArray items)
        {
            home = null;
            items = new JArray();
            if (mission == null)
                return;
            for (int i = 0; i < mission.Count; i++)
            {
                if (i == 0)
                {
                    var h = mission[0];
                    if (Math.Abs(h.lat) > 1e-7 || Math.Abs(h.lng) > 1e-7)
                        home = new JObject { ["lat"] = Math.Round(h.lat, 7), ["lon"] = Math.Round(h.lng, 7), ["alt"] = Math.Round(h.alt, 2) };
                    continue;
                }

                items.Add(ToJson(mission[i]));
            }
        }

        private static double Num(JToken t, string name, double fallback = 0)
        {
            var v = t[name];
            if (v == null || v.Type == JTokenType.Null)
                return fallback;
            var d = v.Value<double>();
            if (double.IsNaN(d) || double.IsInfinity(d))
                throw new ArgumentException(name + " is not a number");
            return d;
        }

        public static Locationwp FromJson(JToken t, int index)
        {
            var cmd = (int)Num(t, "cmd");
            if (cmd < 1 || cmd > ushort.MaxValue)
                throw new ArgumentException("item " + index + ": bad command " + cmd);
            var frame = (int)Num(t, "frame", 3);
            if (frame < 0 || frame > 255 || !Frames.Contains((byte)frame))
                throw new ArgumentException("item " + index + ": unsupported altitude frame " + frame);
            var lat = Num(t, "lat");
            var lon = Num(t, "lon");
            if (Locationwp.isLocationCommand((ushort)cmd) && (Math.Abs(lat) > 90 || Math.Abs(lon) > 180))
                throw new ArgumentException("item " + index + ": position out of range");
            return new Locationwp
            {
                id = (ushort)cmd,
                frame = (byte)frame,
                lat = lat,
                lng = lon,
                alt = (float)Num(t, "alt"),
                p1 = (float)Num(t, "p1"),
                p2 = (float)Num(t, "p2"),
                p3 = (float)Num(t, "p3"),
                p4 = (float)Num(t, "p4")
            };
        }

        /// <summary>The list to upload: home as item 0 (absolute altitude), then the page's items in order.</summary>
        public static List<Locationwp> BuildUpload(JToken home, JArray items)
        {
            if (home == null || home.Type != JTokenType.Object)
                throw new ArgumentException("home position not set");
            if (items == null || items.Count == 0)
                throw new ArgumentException("the mission has no items");
            if (items.Count > MaxItems)
                throw new ArgumentException("too many items (" + items.Count + ")");

            var hl = Num(home, "lat");
            var hn = Num(home, "lon");
            if (Math.Abs(hl) > 90 || Math.Abs(hn) > 180 || (Math.Abs(hl) < 1e-7 && Math.Abs(hn) < 1e-7))
                throw new ArgumentException("home position out of range");

            var list = new List<Locationwp>
            {
                new Locationwp { id = 16, frame = 0, lat = hl, lng = hn, alt = (float)Num(home, "alt") }
            };
            list.AddRange(items.Select((t, i) => FromJson(t, i + 1)));
            return list;
        }

        /// <summary>Profile input for MissionProfile: home as seq 0, then items as seq 1..n.</summary>
        public static List<MissionItem> ProfileItems(double homeLat, double homeLon, double homeAmsl, JArray items)
        {
            var list = new List<MissionItem>
            {
                new MissionItem { Seq = 0, Command = 16, Frame = 0, Lat = homeLat, Lon = homeLon, Z = homeAmsl }
            };
            if (items == null)
                return list;
            for (int i = 0; i < items.Count; i++)
            {
                var w = FromJson(items[i], i + 1);
                list.Add(new MissionItem { Seq = i + 1, Command = w.id, Frame = w.frame, Lat = w.lat, Lon = w.lng, Z = w.alt });
            }

            return list;
        }
    }
}
