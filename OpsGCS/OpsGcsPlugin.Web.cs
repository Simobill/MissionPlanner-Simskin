using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using MissionPlanner.GCSViews;
using MissionPlanner.OpsGCS.Core;
using MissionPlanner.OpsGCS.Services;
using MissionPlanner.OpsGCS.UI;
using MissionPlanner.OpsGCS.WebUI;
using MissionPlanner.Utilities;
using Newtonsoft.Json.Linq;

namespace MissionPlanner.OpsGCS
{
    /// <summary>Operator web UI: state out to the page, requests in from it.</summary>
    public partial class OpsGcsPlugin
    {
        private OpsWebHost _web;
        private string _vsStatus = "Off";
        private JObject _vsJson;          // last viewshed overlay (sent once per version, again on hello)
        private bool _vsDirty;
        private int _vsVer;
        private JObject _missionJson;
        private long _missionPrint = long.MinValue;
        private int _missionVer;
        private int _wxVer;
        private WeatherSnapshot _wxSent;
        private string _webScreen;

        private void SetupWeb(FlightData fd)
        {
            if (!_cfg.WebUi)
                return;

            _web = new OpsWebHost(fd, () => fd.gMapControl1.MapProvider, _cfg.WebVisualHosting);
            _web.MessageReceived += OnWebMessage;
            _web.Initialized += why =>
            {
                if (why != null)
                {
                    log.Warn("OpsGCS web UI unavailable: " + why);
                    _alerts.Add("GCS", HealthLevel.Caution, "Web UI unavailable (" + why + "); using Layout v2");
                }

                ApplyWebMode();
            };
            _web.Start();
        }

        /// <summary>Web UI for operators on the Flight Data screen; Mission Planner's own UI otherwise.</summary>
        private void ApplyWebMode()
        {
            if (_web == null || !_web.Ready)
                return;

            var main = MainV2.instance;
            var onFly = OpsSkin.CurrentScreen() == "FlightData";
            var webOn = _role == OpsRole.Operator && onFly;

            _web.Show(webOn);
            _healthStrip.Visible = !webOn;
            _missionStrip.Visible = !webOn;
            // the page has its own top bar on FLY; PLAN and engineer screens keep the native one
            SetNativeTopBar(!webOn);
            _webScreen = OpsSkin.CurrentScreen();
        }

        private bool _nativeBarHidden;
        private bool _panelWasVisible;
        private bool _menuBtnWasVisible;

        /// <summary>
        /// Shows or hides Mission Planner's top bar. The menu strip sits inside MainV2.panel1 (and, with
        /// auto-hide, behind the "menu" button), so hiding only the strip left the panel as dead space.
        /// The panel's and button's previous visibility is restored, keeping upstream auto-hide working.
        /// </summary>
        private void SetNativeTopBar(bool show)
        {
            var main = MainV2.instance;
            if (main == null)
                return;
            var panel = main.panel1;
            var menuBtn = main.Controls.Find("menu", false).FirstOrDefault();
            main.MainMenu.Visible = true;

            if (!show)
            {
                if (_nativeBarHidden)
                    return;
                _panelWasVisible = panel != null && panel.Visible;
                _menuBtnWasVisible = menuBtn != null && menuBtn.Visible;
                if (panel != null)
                    panel.Visible = false;
                if (menuBtn != null)
                    menuBtn.Visible = false;
                _nativeBarHidden = true;
            }
            else if (_nativeBarHidden)
            {
                if (panel != null)
                    panel.Visible = _panelWasVisible;
                if (menuBtn != null)
                    menuBtn.Visible = _menuBtnWasVisible;
                _nativeBarHidden = false;
            }

            main.PerformLayout();
        }

        // ------------------------------------------------------------------ state out (built on the plugin thread)

        private JObject BuildWebState(List<FleetRow> rows, FleetRow activeRow)
        {
            var inv = CultureInfo.InvariantCulture;
            var st = new JObject
            {
                ["type"] = "state",
                ["product"] = string.IsNullOrWhiteSpace(_cfg.Unit) ? _cfg.ProductName : _cfg.ProductName + " · " + _cfg.Unit,
                ["role"] = _role == OpsRole.Engineer ? "ENGINEER" : "OPERATOR",
                ["planDefaults"] = PlanDefaults()
            };

            var vehicles = new JArray();
            foreach (var r in rows)
                vehicles.Add(VehicleJson(r));
            st["vehicles"] = vehicles;

            var port = activeRow != null ? activeRow.Port : MainV2.comPort;
            st["commands"] = new JArray(CommandsFor(port).Select(c => new JObject
            {
                ["label"] = c.Label,
                ["mode"] = c.Mode,
                ["caution"] = c.Caution
            }));

            var modes = new JArray();
            if (activeRow != null)
            {
                try
                {
                    foreach (var m in MissionPlanner.ArduPilot.Common.getModesList(activeRow.Mav.cs.firmware))
                        modes.Add(m.Value);
                }
                catch
                {
                    // unknown firmware: no mode list
                }
            }

            st["modes"] = modes;

            st["alerts"] = new JArray(_alerts.Snapshot().Take(40).Select(a => new JObject
            {
                ["t"] = a.TimeUtc.ToString("HH:mm:ss", inv) + "Z",
                ["veh"] = a.Vehicle,
                ["level"] = (int)a.Level,
                ["text"] = a.Text
            }));

            st["layers"] = new JObject
            {
                ["viewshed"] = _viewshed.Enabled,
                ["source"] = _viewshed.Source.ToString(),
                ["weather"] = _weather.Enabled,
                ["vsStatus"] = _vsStatus
            };

            var wx = WeatherJson(activeRow != null ? activeRow.Snapshot : null);
            if (wx != null)
                st["weather"] = wx;

            var mission = MissionJson(activeRow);
            if (mission != null)
                st["mission"] = mission;

            return st;
        }

        private static JObject VehicleJson(FleetRow r)
        {
            var s = r.Snapshot ?? new VehicleSnapshot();
            var health = new JArray();
            foreach (var h in r.Health ?? new List<HealthItem>())
            {
                var o = new JObject { ["key"] = h.Key, ["level"] = (int)h.Level, ["value"] = h.Value, ["detail"] = h.Detail };
                if (h.Key == "BATT" && s.Connected && s.BatteryVoltage > 0)
                    o["extra"] = s.BatteryVoltage.ToString("0.0", CultureInfo.InvariantCulture) + " V";
                if (h.Key == "GPS" && s.Connected)
                    o["extra"] = s.Satellites + " sats";
                health.Add(o);
            }

            var v = new JObject
            {
                ["key"] = r.Mav.sysid + ":" + r.Mav.compid,
                ["name"] = r.Name,
                ["sysid"] = r.Mav.sysid,
                ["firmware"] = r.Mav.cs.firmware.ToString(),
                ["connected"] = s.Connected,
                ["active"] = r.Active,
                ["mode"] = s.Mode ?? "",
                ["armed"] = s.Armed,
                ["failsafe"] = s.Failsafe,
                ["hasPos"] = s.HasPosition,
                ["lat"] = s.Lat,
                ["lon"] = s.Lon,
                ["alt"] = Math.Round(s.AltM, 1),
                ["altAmsl"] = Math.Round(s.AltAmsl, 1),
                ["gs"] = Math.Round(s.GroundSpeedMs, 2),
                ["as"] = Math.Round(s.AirspeedMs, 2),
                ["vs"] = Math.Round(s.ClimbMs, 2),
                ["hdg"] = Math.Round(s.Heading, 1),
                ["roll"] = Math.Round(s.Roll, 1),
                ["pitch"] = Math.Round(s.Pitch, 1),
                ["battPct"] = s.BatteryRemainingPct,
                ["battV"] = Math.Round(s.BatteryVoltage, 2),
                ["linkPct"] = s.LinkQualityPct,
                ["wp"] = s.CurrentWp,
                ["wpCount"] = s.MissionCount,
                ["wpDist"] = Math.Round(s.WpDistM),
                ["distHome"] = Math.Round(s.DistToHomeM),
                ["timeInAir"] = Math.Round(s.TimeInAirSec),
                ["windFrom"] = Math.Round(s.WindFromDeg),
                ["windMs"] = Math.Round(s.WindMs, 1),
                ["health"] = health,
                ["worst"] = (int)HealthEvaluator.Worst(r.Health ?? new List<HealthItem>())
            };
            v["home"] = s.HasHome ? new JObject { ["lat"] = s.HomeLat, ["lon"] = s.HomeLon, ["alt"] = Math.Round(s.HomeAltAmsl, 1) } : null;
            return v;
        }

        private JObject WeatherJson(VehicleSnapshot veh)
        {
            if (!_weather.Enabled)
                return null;
            var snap = _weather.Latest;
            if (snap == null)
                return null;

            if (!ReferenceEquals(snap, _wxSent))
            {
                _wxSent = snap;
                _wxVer++;
            }

            string alert;
            var lines = WeatherCard.Lines(snap, veh, _cfg.Weather, out alert);
            var err = string.Join(" · ", new[] { snap.ModelError, snap.MetarError }.Where(e => !string.IsNullOrEmpty(e)));
            return new JObject
            {
                ["ver"] = _wxVer,
                ["fetched"] = snap.FetchedUtc.ToString("o", CultureInfo.InvariantCulture),
                ["staleMin"] = _cfg.Weather.StaleMin,
                ["cellLat"] = snap.GridCellDegLat,
                ["cellLon"] = snap.GridCellDegLon,
                ["grid"] = new JArray((snap.Grid ?? new List<WeatherPoint>()).Select(p => new JObject
                {
                    ["lat"] = p.Lat,
                    ["lon"] = p.Lon,
                    ["windDir"] = p.WindDirDeg,
                    ["windKt"] = p.WindKt,
                    ["precip"] = p.PrecipMm ?? 0
                })),
                ["lines"] = new JArray(lines.Select(l => new JArray(l.Key, l.Value))),
                ["alert"] = alert,
                ["errors"] = err.Length > 0 ? err : null
            };
        }

        /// <summary>Route, legs and terrain profile of the active vehicle's mission; rebuilt only when it changes.</summary>
        private JObject MissionJson(FleetRow active)
        {
            if (active == null || active.Mav == null || active.Mav.wps == null)
                return null;

            var items = new List<MissionItem>();
            foreach (var kv in active.Mav.wps.ToArray())
            {
                var w = kv.Value;
                items.Add(new MissionItem { Seq = w.seq, Command = w.command, Frame = w.frame, Lat = w.x / 1e7, Lon = w.y / 1e7, Z = w.z });
            }

            if (items.Count == 0)
                return null;

            var home = items.FirstOrDefault(i => i.Seq == 0);
            var homeAmsl = home != null && Math.Abs(home.Z) > 0.01 ? home.Z : active.Snapshot.HomeAltAmsl;
            var print = MissionProfile.Fingerprint(items, homeAmsl) ^ ((long)active.Mav.sysid << 48);
            if (print == _missionPrint && _missionJson != null)
                return _missionJson;

            var prof = MissionProfile.Build(items, homeAmsl, (la, lo) =>
            {
                var a = srtm.getAltitude(la, lo);
                if (a == null) return null;
                if (a.currenttype == srtm.tiletype.valid) return a.alt;
                if (a.currenttype == srtm.tiletype.ocean) return 0;
                return null;
            });

            _missionPrint = print;
            _missionVer++;
            _missionJson = new JObject
            {
                ["ver"] = _missionVer,
                ["minClearance"] = _cfg.PlanMinClearanceM,
                ["items"] = new JArray(prof.NavItems.Select(i => new JObject
                {
                    ["seq"] = i.Seq, ["lat"] = i.Lat, ["lon"] = i.Lon, ["alt"] = i.Z, ["nav"] = true
                })),
                ["legs"] = new JArray(prof.Legs.Select(l => new JObject
                {
                    ["to"] = l.To, ["len"] = Math.Round(l.Len, 1), ["checked"] = l.Checked,
                    ["minClear"] = l.MinClear == double.MaxValue ? (JToken)null : Math.Round(l.MinClear, 1)
                })),
                ["profile"] = new JArray(prof.Profile.Select(p => new JObject
                {
                    ["d"] = Math.Round(p.D, 1), ["amsl"] = Math.Round(p.Amsl, 1), ["terrain"] = Math.Round(p.Terrain, 1)
                })),
                ["wpMarks"] = new JArray(prof.WpMarks.Skip(1).Select(p => new JObject
                {
                    ["d"] = Math.Round(p.D, 1), ["amsl"] = Math.Round(p.Amsl, 1)
                }))
            };
            return _missionJson;
        }

        // ------------------------------------------------------------------ viewshed overlay (UI thread)

        private void WebViewshedFrame(ViewshedFrame frame)
        {
            if (_web == null || frame.Image == null || frame.Result == null)
                return;
            try
            {
                string b64;
                using (var ms = new MemoryStream())
                {
                    frame.Image.Save(ms, ImageFormat.Png);
                    b64 = Convert.ToBase64String(ms.ToArray());
                }

                var r = frame.Result;
                _vsVer++;
                _vsJson = new JObject
                {
                    ["ver"] = _vsVer,
                    ["png"] = b64,
                    ["bounds"] = new JObject { ["n"] = r.North, ["s"] = r.South, ["e"] = r.East, ["w"] = r.West }
                };
                _vsDirty = true;
            }
            catch (Exception ex)
            {
                log.Debug("OpsGCS viewshed encode", ex);
            }
        }

        private void WebViewshedCleared()
        {
            if (_vsJson == null || _vsJson["png"] == null)
                return;
            _vsVer++;
            _vsJson = new JObject { ["ver"] = _vsVer };
            _vsDirty = true;
        }

        // ------------------------------------------------------------------ post (UI thread)

        private void PostWebState(JObject st)
        {
            if (_web == null || !_web.Visible || st == null)
                return;

            st["link"] = LinkJson();
            if (_vsDirty && _vsJson != null)
            {
                st["viewshed"] = _vsJson;
                _vsDirty = false;
            }

            _web.Post(st);
        }

        private static JObject LinkJson()
        {
            var cc = MainV2._connectionControl;
            var ports = new JArray();
            var bauds = new JArray();
            string port = "", baud = "";
            if (cc != null)
            {
                foreach (var p in cc.CMB_serialport.Items)
                    ports.Add(p.ToString());
                foreach (var n in System.IO.Ports.SerialPort.GetPortNames())
                    if (!ports.Any(t => (string)t == n))
                        ports.Add(n);
                foreach (var b in cc.CMB_baudrate.Items)
                    bauds.Add(b.ToString());
                port = cc.CMB_serialport.Text;
                baud = cc.CMB_baudrate.Text;
            }

            bool connected = false;
            try
            {
                connected = MainV2.comPort.BaseStream != null && MainV2.comPort.BaseStream.IsOpen;
            }
            catch
            {
                // stream being replaced
            }

            return new JObject { ["connected"] = connected, ["port"] = port, ["baud"] = baud, ["ports"] = ports, ["bauds"] = bauds };
        }

        // ------------------------------------------------------------------ requests in (UI thread)

        private void OnWebMessage(JObject m)
        {
            var type = (string)m["type"];
            switch (type)
            {
                case "hello":
                    // page (re)loaded: resend the overlay it no longer has
                    if (_vsJson != null)
                        _vsDirty = true;
                    break;

                case "select":
                    SelectByKey((string)m["key"]);
                    break;

                case "setMode":
                    WebSetMode((string)m["key"], (string)m["mode"]);
                    break;

                case "connect":
                    WebConnect((string)m["port"], (string)m["baud"]);
                    break;

                case "nav":
                    if ((string)m["screen"] == "PLAN")
                    {
                        SetNativeTopBar(true);
                        MainV2.instance.MenuFlightPlanner.PerformClick();
                    }

                    break;

                case "layers":
                    WebLayers(m);
                    break;

                case "weatherRefresh":
                    _weather.RefreshNow();
                    break;

                case "engineer":
                    ToggleRole();
                    break;

                default:
                    HandlePlanMessage(type, m);
                    break;
            }
        }

        private FleetRow FindRow(string key)
        {
            if (string.IsNullOrEmpty(key))
                return null;
            return BuildRows().FirstOrDefault(r => r.Mav.sysid + ":" + r.Mav.compid == key);
        }

        private void SelectByKey(string key)
        {
            var row = FindRow(key);
            if (row != null)
                SelectVehicle(row);
        }

        private void WebSetMode(string key, string mode)
        {
            if (string.IsNullOrWhiteSpace(mode))
                return;
            var row = FindRow(key);
            if (row == null || row.Snapshot == null || !row.Snapshot.Connected)
            {
                _alerts.Add("GCS", HealthLevel.Caution, "Command not sent: vehicle " + key + " not connected");
                return;
            }

            // the page confirmed with a hold; the command goes only to the vehicle it named
            SendMode(row.Port, row.Mav, row.Name, mode);
        }

        private void WebConnect(string port, string baud)
        {
            try
            {
                var cc = MainV2._connectionControl;
                var open = MainV2.comPort.BaseStream != null && MainV2.comPort.BaseStream.IsOpen;
                if (!open && cc != null)
                {
                    if (!string.IsNullOrWhiteSpace(port))
                        cc.CMB_serialport.Text = port;
                    if (!string.IsNullOrWhiteSpace(baud))
                        cc.CMB_baudrate.Text = baud;
                }

                // same path as the CONNECT button: handles connect and disconnect, prompts included
                var click = typeof(MainV2).GetMethod("MenuConnect_Click", BindingFlags.NonPublic | BindingFlags.Instance);
                if (click != null)
                    click.Invoke(MainV2.instance, new object[] { null, EventArgs.Empty });
            }
            catch (Exception ex)
            {
                log.Error("OpsGCS web connect", ex);
                _alerts.Add("GCS", HealthLevel.Warning, "Connect failed: " + (ex.InnerException ?? ex).Message);
            }
        }

        private void WebLayers(JObject m)
        {
            if (m["viewshed"] != null)
            {
                var on = (bool)m["viewshed"];
                Settings.Instance[SetViewshed] = on.ToString();
                _viewshed.Enabled = on;
                if (!on)
                {
                    _mapLayers.ClearViewshed();
                    _vsStatus = "Off";
                    WebViewshedCleared();
                }
                else
                {
                    _vsStatus = "Computing…";
                }
            }

            if (m["source"] != null)
            {
                var src = (string)m["source"] == "Radio" ? ViewshedSource.Radio : ViewshedSource.Sensor;
                Settings.Instance[SetViewshedSource] = src.ToString();
                _mapLayers.ClearViewshed();
                WebViewshedCleared();
                _vsStatus = "Computing…";
                _viewshed.Source = src;
            }

            if (m["weather"] != null)
            {
                var on = (bool)m["weather"];
                Settings.Instance[SetWeather] = on.ToString();
                _weather.Enabled = on;
                if (!on)
                    _mapLayers.ClearWeather();
            }

            _layersPanel.SetState(_viewshed.Enabled, _viewshed.Source, _weather.Enabled);
        }
    }
}
