using System;
using System.Linq;
using MissionPlanner.OpsGCS.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MissionPlannerTests.OpsGCS
{
    [TestClass]
    public class WeatherTests
    {
        private const string OpenMeteoMulti =
            "[{\"latitude\":37.9,\"longitude\":23.7,\"current\":{\"time\":\"2026-11-26T19:00\",\"temperature_2m\":14.2," +
            "\"precipitation\":0.0,\"cloud_cover\":75,\"pressure_msl\":1012.3,\"visibility\":24140.0,\"wind_speed_10m\":14.1," +
            "\"wind_direction_10m\":290,\"wind_gusts_10m\":22.0}}," +
            "{\"latitude\":38.0,\"longitude\":23.8,\"current\":{\"time\":\"2026-11-26T19:00\",\"temperature_2m\":13.1," +
            "\"precipitation\":1.6,\"cloud_cover\":100,\"pressure_msl\":1011.8,\"visibility\":null,\"wind_speed_10m\":18," +
            "\"wind_direction_10m\":300,\"wind_gusts_10m\":29}}]";

        private const string MetarJson =
            "[{\"icaoId\":\"LGAV\",\"name\":\"Athens Intl\",\"obsTime\":1795719600,\"temp\":14,\"wdir\":290,\"wspd\":14,\"wgst\":22," +
            "\"visib\":\"6+\",\"altim\":1012,\"wxString\":\"-RA\",\"rawOb\":\"LGAV 261850Z 29014G22KT 9999 -RA FEW015 BKN024 OVC080 14/09 Q1012\"," +
            "\"lat\":37.936,\"lon\":23.944,\"clouds\":[{\"cover\":\"FEW\",\"base\":1500},{\"cover\":\"BKN\",\"base\":2400},{\"cover\":\"OVC\",\"base\":8000}]}," +
            "{\"icaoId\":\"LGEL\",\"obsTime\":1795719600,\"wdir\":\"VRB\",\"wspd\":3,\"wgst\":null,\"visib\":4,\"altim\":1013," +
            "\"lat\":38.065,\"lon\":23.556,\"clouds\":[{\"cover\":\"CAVOK\"}],\"vertVis\":null}]";

        [TestMethod]
        public void ParsesOpenMeteoArray()
        {
            var pts = WeatherParse.OpenMeteo(OpenMeteoMulti);
            Assert.AreEqual(2, pts.Count);
            Assert.AreEqual(290, pts[0].WindDirDeg.Value, 1e-9);
            Assert.AreEqual(22, pts[0].GustKt.Value, 1e-9);
            Assert.AreEqual(new DateTime(2026, 11, 26, 19, 0, 0, DateTimeKind.Utc), pts[0].TimeUtc.Value);
            Assert.IsFalse(pts[1].VisibilityM.HasValue);
            Assert.AreEqual(1.6, WeatherGrid.Wettest(pts).PrecipMm.Value, 1e-9);
        }

        [TestMethod]
        public void ParsesOpenMeteoSingleObject()
        {
            var single = OpenMeteoMulti.Substring(1, OpenMeteoMulti.IndexOf("},{"));
            var pts = WeatherParse.OpenMeteo(single);
            Assert.AreEqual(1, pts.Count);
            Assert.AreEqual(14.2, pts[0].TempC.Value, 1e-9);
        }

        [TestMethod]
        public void ParsesMetarCeilingVisibilityAndVariableWind()
        {
            var obs = WeatherParse.Metar(MetarJson);
            Assert.AreEqual(2, obs.Count);
            var a = obs[0];
            Assert.AreEqual(2400, a.CeilingFt.Value);
            Assert.AreEqual("BKN", a.CeilingCover);
            Assert.AreEqual(9.7, a.VisibilityKm.Value, 0.05);
            Assert.IsTrue(a.VisibilityOrMore);
            Assert.AreEqual(290, a.WindDirDeg.Value);
            Assert.AreEqual(2026, a.ObsUtc.Year);

            var b = obs[1];
            Assert.IsFalse(b.WindDirDeg.HasValue); // VRB
            Assert.IsFalse(b.CeilingFt.HasValue);  // CAVOK
            Assert.IsFalse(b.GustKt.HasValue);
        }

        [TestMethod]
        public void PicksNearestMetar()
        {
            double km;
            var n = WeatherParse.Nearest(WeatherParse.Metar(MetarJson), 38.07, 23.55, out km);
            Assert.AreEqual("LGEL", n.Station);
            Assert.IsTrue(km < 2);
        }

        [TestMethod]
        public void GridIsCentredAndSized()
        {
            double dLat, dLon;
            var pts = WeatherGrid.Points(38.0, 23.7, 20, 5, out dLat, out dLon);
            Assert.AreEqual(25, pts.Count);
            Assert.AreEqual(38.0, pts[12].Key, 1e-4);
            Assert.AreEqual(23.7, pts[12].Value, 1e-4);
            Assert.IsTrue(pts[0].Key > pts[24].Key);   // first row is north
            Assert.IsTrue(pts[0].Value < pts[24].Value); // first column is west
            Assert.AreEqual(4000 / 111320.0, dLat, 1e-6);
        }

        [TestMethod]
        public void BuildsUrls()
        {
            double dLat, dLon;
            var url = WeatherUrls.OpenMeteo(null, "", WeatherGrid.Points(38, 23.7, 10, 2, out dLat, out dLon));
            StringAssert.StartsWith(url, "https://api.open-meteo.com/v1/forecast?latitude=");
            StringAssert.Contains(url, "wind_speed_unit=kn");
            Assert.IsFalse(url.Contains("apikey"));
            StringAssert.Contains(WeatherUrls.OpenMeteo("https://customer-api.open-meteo.com/v1/forecast", "k y", Enumerable.Empty<System.Collections.Generic.KeyValuePair<double, double>>()), "apikey=k%20y");
            StringAssert.Contains(WeatherUrls.MetarAround(null, 38, 23.7, 50), "bbox=37.551,23.13,38.449,24.27&format=json");
            StringAssert.Contains(WeatherUrls.MetarStation(null, "lgav"), "ids=LGAV");
        }

        [TestMethod]
        public void WindBarbGlyphs()
        {
            int p, f, h;
            WindBarb.Glyph(14, out p, out f, out h); // rounds to 15
            Assert.AreEqual(0, p); Assert.AreEqual(1, f); Assert.AreEqual(1, h);
            WindBarb.Glyph(65, out p, out f, out h);
            Assert.AreEqual(1, p); Assert.AreEqual(1, f); Assert.AreEqual(1, h);
            WindBarb.Glyph(2, out p, out f, out h);
            Assert.AreEqual(0, p + f + h);
        }
    }
}
