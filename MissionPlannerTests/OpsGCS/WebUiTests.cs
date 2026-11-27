using System;
using System.Collections.Generic;
using System.Linq;
using MissionPlanner.OpsGCS.WebUI;
using MissionPlanner.Utilities;
using Newtonsoft.Json.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MissionPlannerTests.OpsGCS
{
    [TestClass]
    public class WebUiTests
    {
        [TestMethod]
        public void WebHostingDefaultsToVisual()
        {
            var cfg = new MissionPlanner.OpsGCS.Core.OpsConfig();
            Assert.IsTrue(cfg.WebVisualHosting);
            cfg.WebHosting = "Windowed";
            Assert.IsFalse(cfg.WebVisualHosting);
            cfg.WebHosting = "anything-else";
            Assert.IsTrue(cfg.WebVisualHosting);
        }

        [TestMethod]
        public void ParsesTileUrls()
        {
            int z, x, y;
            Assert.IsTrue(TileUrl.TryParse("https://tiles.opsgcs.local/12/2345/1567", "tiles.opsgcs.local", out z, out x, out y));
            Assert.AreEqual(12, z); Assert.AreEqual(2345, x); Assert.AreEqual(1567, y);
            Assert.IsFalse(TileUrl.TryParse("https://tiles.opsgcs.local/2/9/1", "tiles.opsgcs.local", out z, out x, out y)); // x out of range
            Assert.IsFalse(TileUrl.TryParse("https://evil.example/1/0/0", "tiles.opsgcs.local", out z, out x, out y));
            Assert.IsFalse(TileUrl.TryParse("https://tiles.opsgcs.local/../etc", "tiles.opsgcs.local", out z, out x, out y));
            Assert.AreEqual("image/png", TileUrl.SniffImageType(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0 }));
            Assert.AreEqual("image/jpeg", TileUrl.SniffImageType(new byte[] { 0xFF, 0xD8, 0xFF }));
        }

        private static List<MissionItem> Square(byte frame)
        {
            return new List<MissionItem>
            {
                new MissionItem { Seq = 0, Command = 16, Frame = 0, Lat = 38.0, Lon = 23.7, Z = 100 },   // home, AMSL
                new MissionItem { Seq = 1, Command = 22, Frame = frame, Lat = 38.0, Lon = 23.71, Z = 50 }, // takeoff
                new MissionItem { Seq = 2, Command = 178, Frame = 2, Lat = 0, Lon = 0, Z = 0 },           // DO_CHANGE_SPEED: not nav
                new MissionItem { Seq = 3, Command = 16, Frame = frame, Lat = 38.01, Lon = 23.71, Z = 120 },
                new MissionItem { Seq = 4, Command = 20, Frame = frame, Lat = 0, Lon = 0, Z = 0 }        // RTL: no position
            };
        }

        [TestMethod]
        public void ProfileUsesFramesAndSkipsNonNavItems()
        {
            var r = MissionProfile.Build(Square(3), 100, (la, lo) => 40.0);
            CollectionAssert.AreEqual(new[] { 0, 1, 3 }, r.NavItems.Select(i => i.Seq).ToArray());
            Assert.AreEqual(2, r.Legs.Count);
            Assert.AreEqual(3, r.Legs[1].To);
            Assert.AreEqual(r.Legs.Sum(l => l.Len), r.TotalM, 1e-6);
            Assert.AreEqual(220, r.Profile.Last().Amsl, 1e-6); // relative 120 + home 100
            Assert.AreEqual(40, r.Profile.Last().Terrain, 1e-6);
            Assert.AreEqual(r.TotalM, r.Profile.Last().D, 1e-6);

            var t = MissionProfile.Build(Square(10), 100, (la, lo) => 40.0);
            Assert.AreEqual(160, t.Profile.Last().Amsl, 1e-6); // terrain 40 + 120 AGL
        }

        [TestMethod]
        public void LegClearanceSkipsClimbOutAndFollowsTerrainFrame()
        {
            // a ridge of 200 m between the two cruise waypoints
            Func<double, double, double?> ridge = (la, lo) => Math.Abs(lo - 23.705) < 0.002 ? 200.0 : 40.0;
            var items = new List<MissionItem>
            {
                new MissionItem { Seq = 0, Command = 16, Frame = 0, Lat = 38.0, Lon = 23.60, Z = 100 },
                new MissionItem { Seq = 1, Command = 16, Frame = 3, Lat = 38.0, Lon = 23.65, Z = 120 },
                new MissionItem { Seq = 2, Command = 16, Frame = 3, Lat = 38.0, Lon = 23.76, Z = 120 },
                new MissionItem { Seq = 3, Command = 21, Frame = 3, Lat = 38.0, Lon = 23.80, Z = 0 }
            };
            var r = MissionProfile.Build(items, 100, ridge);
            Assert.IsFalse(r.Legs[0].Checked);  // climb-out from home
            Assert.IsTrue(r.Legs[1].Checked);
            Assert.IsFalse(r.Legs[2].Checked);  // approach into LAND
            Assert.AreEqual(20, r.Legs[1].MinClear, 1e-6); // 220 AMSL over the 200 m ridge
            Assert.AreEqual(0, r.TerrainMissing);

            // same mission held 120 m above terrain: clearance stays 120 over the ridge
            items[1].Frame = 10; items[2].Frame = 10;
            var t = MissionProfile.Build(items, 100, ridge);
            Assert.AreEqual(120, t.Legs[1].MinClear, 1e-6);

            var none = MissionProfile.Build(items, 100, (la, lo) => null);
            Assert.IsTrue(none.TerrainMissing > 0);
        }

        [TestMethod]
        public void PlanCodecBuildsUploadWithHomeFirstAndRejectsBadInput()
        {
            var home = JObject.Parse("{lat:38.0,lon:23.6,alt:112.5}");
            var items = JArray.Parse("[{cmd:22,frame:3,lat:0,lon:0,alt:40,p1:12}," +
                                     "{cmd:16,frame:10,lat:38.01,lon:23.61,alt:120}," +
                                     "{cmd:20,frame:3,lat:0,lon:0,alt:0}]");
            var list = PlanCodec.BuildUpload(home, items);
            Assert.AreEqual(4, list.Count);
            Assert.AreEqual(16, list[0].id);
            Assert.AreEqual(0, list[0].frame);
            Assert.AreEqual(112.5, list[0].alt, 1e-3);
            Assert.AreEqual(22, list[1].id);
            Assert.AreEqual(12, list[1].p1, 1e-6);
            Assert.AreEqual(10, list[2].frame);
            Assert.AreEqual(23.61, list[2].lng, 1e-9);

            JObject h;
            JArray back;
            PlanCodec.Split(list, out h, out back);
            Assert.AreEqual(38.0, h.Value<double>("lat"), 1e-9);
            Assert.AreEqual(3, back.Count);
            Assert.AreEqual(20, back[2].Value<int>("cmd"));

            Assert.ThrowsException<ArgumentException>(() => PlanCodec.BuildUpload(null, items));
            Assert.ThrowsException<ArgumentException>(() => PlanCodec.BuildUpload(home, new JArray()));
            Assert.ThrowsException<ArgumentException>(() => PlanCodec.BuildUpload(home, JArray.Parse("[{cmd:16,frame:7,lat:1,lon:1,alt:1}]")));
            Assert.ThrowsException<ArgumentException>(() => PlanCodec.BuildUpload(home, JArray.Parse("[{cmd:16,frame:3,lat:91,lon:1,alt:1}]")));
            Assert.ThrowsException<ArgumentException>(() => PlanCodec.BuildUpload(JObject.Parse("{lat:0,lon:0,alt:0}"), items));
        }

        [TestMethod]
        public void TileUrlCarriesBasemapId()
        {
            string src; int z, x, y;
            Assert.IsTrue(TileUrl.TryParse("https://tiles.opsgcs.local/topo/12/2345/1567", "tiles.opsgcs.local", out src, out z, out x, out y));
            Assert.AreEqual("topo", src);
            Assert.AreEqual(12, z);
            Assert.IsTrue(TileUrl.TryParse("https://tiles.opsgcs.local/12/2345/1567", "tiles.opsgcs.local", out src, out z, out x, out y));
            Assert.AreEqual("mp", src);
            Assert.IsFalse(TileUrl.TryParse("https://tiles.opsgcs.local/bad id/12/1/1", "tiles.opsgcs.local", out src, out z, out x, out y));
            Assert.IsTrue(Basemaps.IsKnown("osm"));
            Assert.IsFalse(Basemaps.IsKnown("nope"));
        }

        [TestMethod]
        public void ProfileToleratesMissingTerrainAndFingerprintChanges()
        {
            var r = MissionProfile.Build(Square(3), 100, (la, lo) => null);
            Assert.IsTrue(r.Profile.All(p => Math.Abs(p.Terrain - 100) < 1e-6)); // falls back to home
            var a = MissionProfile.Fingerprint(Square(3), 100);
            var items = Square(3);
            items[3].Z = 121;
            Assert.AreNotEqual(a, MissionProfile.Fingerprint(items, 100));
            Assert.AreEqual(a, MissionProfile.Fingerprint(Square(3), 100));
        }
    }
}
