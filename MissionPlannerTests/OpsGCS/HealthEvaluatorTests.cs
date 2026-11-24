using System.IO;
using MissionPlanner.OpsGCS.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MissionPlannerTests.OpsGCS
{
    [TestClass]
    public class HealthEvaluatorTests
    {
        private static VehicleSnapshot Healthy()
        {
            return new VehicleSnapshot
            {
                Connected = true,
                SecondsSinceLastPacket = 0.2,
                LinkQualityPct = 100,
                BatteryVoltage = 24.8,
                BatteryRemainingPct = 90,
                BatteryCurrentA = 12,
                GpsFixType = 3,
                Satellites = 16,
                Hdop = 0.7,
                EkfVariance = 0.1,
                VibeX = 5, VibeY = 6, VibeZ = 9,
                Mode = "AUTO"
            };
        }

        [TestMethod]
        public void HealthyVehicleIsAllOk()
        {
            var items = HealthEvaluator.Evaluate(Healthy(), new OpsThresholds());
            Assert.AreEqual(5, items.Count);
            Assert.AreEqual(HealthLevel.Ok, HealthEvaluator.Worst(items));
        }

        [TestMethod]
        public void DisconnectedIsUnknown()
        {
            var items = HealthEvaluator.Evaluate(new VehicleSnapshot(), new OpsThresholds());
            Assert.AreEqual(HealthLevel.Unknown, HealthEvaluator.Worst(items));
        }

        [TestMethod]
        public void StaleLinkIsWarning()
        {
            var s = Healthy();
            s.SecondsSinceLastPacket = 5;
            var link = HealthEvaluator.Link(s, new OpsThresholds());
            Assert.AreEqual(HealthLevel.Warning, link.Level);
            Assert.AreEqual("LOST", link.Value);
        }

        [TestMethod]
        public void LinkQualityBands()
        {
            var t = new OpsThresholds();
            var s = Healthy();
            s.LinkQualityPct = 70;
            Assert.AreEqual(HealthLevel.Caution, HealthEvaluator.Link(s, t).Level);
            s.LinkQualityPct = 40;
            Assert.AreEqual(HealthLevel.Warning, HealthEvaluator.Link(s, t).Level);
        }

        [TestMethod]
        public void BatteryPercentBands()
        {
            var t = new OpsThresholds();
            var s = Healthy();
            s.BatteryRemainingPct = 30;
            Assert.AreEqual(HealthLevel.Caution, HealthEvaluator.Battery(s, t).Level);
            s.BatteryRemainingPct = 15;
            Assert.AreEqual(HealthLevel.Warning, HealthEvaluator.Battery(s, t).Level);
        }

        [TestMethod]
        public void CellVoltageOverridesHealthyPercent()
        {
            var t = new OpsThresholds { CellCount = 6 };
            var s = Healthy();
            s.BatteryRemainingPct = 80; // monitor not calibrated
            s.BatteryVoltage = 6 * 3.40;
            Assert.AreEqual(HealthLevel.Warning, HealthEvaluator.Battery(s, t).Level);
        }

        [TestMethod]
        public void GpsBands()
        {
            var t = new OpsThresholds();
            var s = Healthy();
            s.GpsFixType = 2;
            Assert.AreEqual(HealthLevel.Warning, HealthEvaluator.Gps(s, t).Level);
            s.GpsFixType = 3;
            s.Satellites = 8;
            Assert.AreEqual(HealthLevel.Caution, HealthEvaluator.Gps(s, t).Level);
            s.Satellites = 16;
            s.Hdop = 3.0;
            Assert.AreEqual(HealthLevel.Warning, HealthEvaluator.Gps(s, t).Level);
            s.Hdop = 0.6;
            s.GpsFixType = 6;
            Assert.AreEqual("RTK FIX", HealthEvaluator.Gps(s, t).Value);
        }

        [TestMethod]
        public void EkfAndVibeBands()
        {
            var t = new OpsThresholds();
            var s = Healthy();
            s.EkfVariance = 0.6;
            Assert.AreEqual(HealthLevel.Caution, HealthEvaluator.Ekf(s, t).Level);
            s.EkfVariance = 1.0;
            Assert.AreEqual(HealthLevel.Warning, HealthEvaluator.Ekf(s, t).Level);
            s.VibeZ = 45;
            Assert.AreEqual(HealthLevel.Caution, HealthEvaluator.Vibe(s, t).Level);
            s.VibeX = 70;
            Assert.AreEqual(HealthLevel.Warning, HealthEvaluator.Vibe(s, t).Level);
        }

        [TestMethod]
        public void MissionProgressSkipsHome()
        {
            Assert.AreEqual(0, HealthEvaluator.MissionProgress(0, 0));
            Assert.AreEqual(0, HealthEvaluator.MissionProgress(1, 11));
            Assert.AreEqual(0.5, HealthEvaluator.MissionProgress(6, 11), 1e-9);
            Assert.AreEqual(1.0, HealthEvaluator.MissionProgress(50, 11), 1e-9);
        }

        [TestMethod]
        public void EtaAndFormatting()
        {
            Assert.AreEqual(-1, HealthEvaluator.EtaToWpSec(100, 0.1));
            Assert.AreEqual(50, HealthEvaluator.EtaToWpSec(1000, 20), 1e-9);
            Assert.AreEqual("--:--", HealthEvaluator.FormatDuration(-1));
            Assert.AreEqual("01:05", HealthEvaluator.FormatDuration(65));
            Assert.AreEqual("1:01:01", HealthEvaluator.FormatDuration(3661));
        }

        [TestMethod]
        public void ConfigPinAndDefaults()
        {
            var dir = Path.Combine(Path.GetTempPath(), "opsgcs-test-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var missing = OpsConfig.Load(dir);
                Assert.IsFalse(missing.EngineerModeAvailable);
                Assert.IsFalse(missing.CheckPin(""));

                File.WriteAllText(Path.Combine(dir, OpsConfig.FileName),
                    "{\"ProductName\":\"TEST\",\"EngineerPinSha256\":\"" + OpsConfig.Sha256Hex("4321") + "\"}");
                var cfg = OpsConfig.Load(dir);
                Assert.AreEqual("TEST", cfg.ProductName);
                Assert.IsTrue(cfg.CheckPin("4321"));
                Assert.IsFalse(cfg.CheckPin("1234"));
                Assert.IsNotNull(cfg.Thresholds);

                File.WriteAllText(Path.Combine(dir, OpsConfig.FileName), "{ not json");
                Assert.IsFalse(OpsConfig.Load(dir).EngineerModeAvailable);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
