using System;
using MissionPlanner.OpsGCS.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MissionPlannerTests.OpsGCS
{
    [TestClass]
    public class ViewshedTests
    {
        private const double Lat = 38.0, Lon = 23.7;
        private const double MPerDegLat = 111320.0;

        private static double MPerDegLon
        {
            get { return MPerDegLat * Math.Cos(Lat * Math.PI / 180); }
        }

        private static double EastM(double lon)
        {
            return (lon - Lon) * MPerDegLon;
        }

        private static ViewshedCell CellAt(ViewshedResult r, double eastM, double northM, double cellM)
        {
            var c = (r.Size - 1) / 2;
            var col = c + (int)Math.Round(eastM / cellM);
            var row = c - (int)Math.Round(northM / cellM);
            return r.At(row, col);
        }

        [TestMethod]
        public void FlatGroundIsAllVisibleInsideRadius()
        {
            var req = new ViewshedRequest { ObserverLat = Lat, ObserverLon = Lon, ObserverAmsl = 100, RadiusM = 2000, CellM = 50 };
            var r = Viewshed.Compute(req, (la, lo) => 0.0);
            Assert.AreEqual(0, r.Masked);
            Assert.AreEqual(0, r.Unknown);
            Assert.IsTrue(r.Visible > 3000);
            Assert.AreEqual(ViewshedCell.Outside, r.At(0, 0)); // corner is outside the circle
        }

        [TestMethod]
        public void RidgeMasksGroundBehindIt()
        {
            // 300 m wall from 1.0 to 1.1 km east of the observer, everything else at 0
            Func<double, double, double?> terrain = (la, lo) =>
            {
                var e = EastM(lo);
                return e > 1000 && e < 1100 ? 300.0 : 0.0;
            };
            var req = new ViewshedRequest { ObserverLat = Lat, ObserverLon = Lon, ObserverAmsl = 50, RadiusM = 3000, CellM = 50 };
            var r = Viewshed.Compute(req, terrain);

            Assert.AreEqual(ViewshedCell.Visible, CellAt(r, -2000, 0, 50)); // west, open ground
            Assert.AreEqual(ViewshedCell.Visible, CellAt(r, 1000, 0, 50));  // the ridge's near face
            Assert.AreEqual(ViewshedCell.Masked, CellAt(r, 1050, 0, 50));   // its flat top, seen from below
            Assert.AreEqual(ViewshedCell.Masked, CellAt(r, 2000, 0, 50));   // behind the ridge
        }

        [TestMethod]
        public void TargetAboveRidgeIsVisibleForRadioCoverage()
        {
            Func<double, double, double?> terrain = (la, lo) =>
            {
                var e = EastM(lo);
                return e > 1000 && e < 1100 ? 300.0 : 0.0;
            };
            var req = new ViewshedRequest
            {
                ObserverLat = Lat, ObserverLon = Lon, ObserverAmsl = 10, RadiusM = 3000, CellM = 50,
                TargetAmsl = 1000 // aircraft at 1000 m AMSL
            };
            var r = Viewshed.Compute(req, terrain);
            Assert.AreEqual(ViewshedCell.Visible, CellAt(r, 2500, 0, 50));

            req.TargetAmsl = 200; // below the ridge line from a 10 m mast
            r = Viewshed.Compute(req, terrain);
            Assert.AreEqual(ViewshedCell.Masked, CellAt(r, 2500, 0, 50));
        }

        [TestMethod]
        public void EarthCurvatureHidesGroundPastHorizon()
        {
            // 2 m observer over flat sea: radio horizon sqrt(2 * R/(1-k) * h) is about 5.4 km
            var req = new ViewshedRequest { ObserverLat = Lat, ObserverLon = Lon, ObserverAmsl = 2, RadiusM = 9000, CellM = 100 };
            var r = Viewshed.Compute(req, (la, lo) => 0.0);
            Assert.AreEqual(ViewshedCell.Visible, CellAt(r, 0, 3000, 100));
            Assert.AreEqual(ViewshedCell.Masked, CellAt(r, 0, 8000, 100));
        }

        [TestMethod]
        public void MissingTerrainMakesTheRestOfTheRayUnknown()
        {
            // no data in a band 500..600 m north
            Func<double, double, double?> terrain = (la, lo) =>
            {
                var n = (la - Lat) * MPerDegLat;
                return n > 500 && n < 600 ? (double?)null : 0.0;
            };
            var req = new ViewshedRequest { ObserverLat = Lat, ObserverLon = Lon, ObserverAmsl = 100, RadiusM = 1500, CellM = 50 };
            var r = Viewshed.Compute(req, terrain);
            Assert.AreEqual(ViewshedCell.Visible, CellAt(r, 0, 300, 50));
            Assert.AreEqual(ViewshedCell.Unknown, CellAt(r, 0, 1000, 50));
            Assert.IsTrue(r.Unknown > 0);
        }

        [TestMethod]
        public void BoundsAreCentredOnObserver()
        {
            var req = new ViewshedRequest { ObserverLat = Lat, ObserverLon = Lon, ObserverAmsl = 100, RadiusM = 1000, CellM = 50 };
            var r = Viewshed.Compute(req, (la, lo) => 0.0);
            Assert.AreEqual(Lat, (r.North + r.South) / 2, 1e-9);
            Assert.AreEqual(Lon, (r.East + r.West) / 2, 1e-9);
            Assert.AreEqual(41, r.Size);
        }

        [TestMethod]
        public void GeoHelpers()
        {
            Assert.AreEqual(111195, Viewshed.DistanceM(0, 0, 1, 0), 50);
            Assert.AreEqual(90, Viewshed.BearingDeg(0, 0, 0, 1), 0.01);
            Assert.AreEqual("NW", Viewshed.CompassPoint(312));
            Assert.AreEqual("N", Viewshed.CompassPoint(359));
        }
    }
}
