using System;
using System.Collections.Generic;
using System.Linq;
using MissionPlanner.OpsGCS.WebUI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MissionPlannerTests.OpsGCS
{
    [TestClass]
    public class WebUiTests
    {
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
