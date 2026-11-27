using System.Collections.Generic;
using MissionPlanner.OpsGCS.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MissionPlannerTests.OpsGCS
{
    [TestClass]
    public class AlertLogTests
    {
        private static List<HealthItem> H(HealthLevel batt)
        {
            return new List<HealthItem>
            {
                new HealthItem("LINK", HealthLevel.Ok, "96%", "x"),
                new HealthItem("BATT", batt, "30%", "y")
            };
        }

        [TestMethod]
        public void AlertsOnWorseningAndRecoveryOnly()
        {
            var log = new AlertLog();
            log.Observe("FW-1", H(HealthLevel.Ok), "AUTO");
            Assert.AreEqual(0, log.Snapshot().Count);

            log.Observe("FW-1", H(HealthLevel.Caution), "AUTO");
            log.Observe("FW-1", H(HealthLevel.Caution), "AUTO"); // no repeat
            Assert.AreEqual(1, log.Snapshot().Count);
            Assert.AreEqual(HealthLevel.Caution, log.Snapshot()[0].Level);

            log.Observe("FW-1", H(HealthLevel.Warning), "AUTO");
            log.Observe("FW-1", H(HealthLevel.Ok), "AUTO");
            var s = log.Snapshot();
            Assert.AreEqual(3, s.Count);
            Assert.AreEqual(HealthLevel.Ok, s[0].Level); // newest first: recovery
            StringAssert.Contains(s[0].Text, "recovered");
        }

        [TestMethod]
        public void ReportsDegradedOnFirstSightAndModeChanges()
        {
            var log = new AlertLog();
            log.Observe("MR-2", H(HealthLevel.Warning), "LOITER");
            log.Observe("MR-2", H(HealthLevel.Warning), "RTL");
            var s = log.Snapshot();
            Assert.AreEqual(2, s.Count);
            StringAssert.Contains(s[0].Text, "LOITER -> RTL");
        }

        [TestMethod]
        public void MessagesDeduplicateAndCapIsHonoured()
        {
            var log = new AlertLog(5);
            log.ObserveMessage("FW-1", "PreArm: Compass not calibrated", false);
            log.ObserveMessage("FW-1", "PreArm: Compass not calibrated", false);
            Assert.AreEqual(1, log.Snapshot().Count);
            for (int i = 0; i < 10; i++)
                log.Add("FW-1", HealthLevel.Unknown, "m" + i);
            Assert.AreEqual(5, log.Snapshot().Count);
            Assert.AreEqual("m9", log.Snapshot()[0].Text);
        }
    }
}
