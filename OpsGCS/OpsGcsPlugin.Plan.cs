using System;
using System.Linq;
using System.Threading.Tasks;
using MissionPlanner.ArduPilot;
using MissionPlanner.Controls;
using MissionPlanner.OpsGCS.Core;
using MissionPlanner.OpsGCS.UI;
using MissionPlanner.OpsGCS.WebUI;
using MissionPlanner.Utilities;
using Newtonsoft.Json.Linq;

namespace MissionPlanner.OpsGCS
{
    /// <summary>
    /// PLAN screen of the web UI: read, check and write missions. The page edits a draft; this side
    /// supplies terrain profiles and does the MAVLink mission transfer through Mission Planner.
    /// </summary>
    public partial class OpsGcsPlugin
    {
        private volatile bool _planBusy;

        /// <summary>True when the message was a PLAN request.</summary>
        private bool HandlePlanMessage(string type, JObject m)
        {
            switch (type)
            {
                case "planGet":
                    PlanGet((string)m["key"], m.Value<bool?>("fromVehicle") ?? false);
                    return true;
                case "planProfile":
                    PlanProfile(m);
                    return true;
                case "planWrite":
                    PlanWrite(m);
                    return true;
                case "planNative":
                    if (_role == OpsRole.Engineer)
                    {
                        SetNativeTopBar(true);
                        MainV2.instance.MenuFlightPlanner.PerformClick();
                    }

                    return true;
            }

            return false;
        }

        private void PostPlan(JObject msg)
        {
            MainV2.instance.BeginInvokeIfRequired(() =>
            {
                if (_web != null)
                    _web.Post(msg);
            });
        }

        private void PlanResult(string op, bool ok, string text)
        {
            PostPlan(new JObject { ["type"] = "planResult", ["op"] = op, ["ok"] = ok, ["text"] = text });
        }

        private JObject PlanDefaults()
        {
            return new JObject
            {
                ["minClearance"] = _cfg.PlanMinClearanceM,
                ["defaultAlt"] = _cfg.PlanDefaultAltM,
                ["defaultFrame"] = _cfg.PlanDefaultFrame
            };
        }

        // ------------------------------------------------------------------ read

        private void PlanGet(string key, bool fromVehicle)
        {
            var row = FindRow(key);
            if (row == null)
            {
                PlanResult("read", false, "Vehicle " + key + " not found");
                return;
            }

            if (!fromVehicle)
            {
                // what Mission Planner already holds for this vehicle; no link traffic
                var cached = row.Mav.wps.ToArray().OrderBy(kv => kv.Key).Select(kv => (Locationwp)kv.Value).ToList();
                SendPlanData(row, cached, "cache");
                return;
            }

            if (_planBusy)
            {
                PlanResult("read", false, "A mission transfer is already running");
                return;
            }

            if (row.Snapshot == null || !row.Snapshot.Connected)
            {
                PlanResult("read", false, row.Name + " is not connected");
                return;
            }

            _planBusy = true;
            var port = row.Port;
            var sysid = row.Mav.sysid;
            var compid = row.Mav.compid;
            Task.Run(async () =>
            {
                try
                {
                    var list = await mav_mission.download(port, sysid, compid, MAVLink.MAV_MISSION_TYPE.MISSION,
                        (pct, text) => PostPlan(new JObject { ["type"] = "planProgress", ["op"] = "read", ["pct"] = pct, ["text"] = text }))
                        .ConfigureAwait(false);
                    SendPlanData(row, list, "vehicle");
                    PlanResult("read", true, "Read " + Math.Max(0, list.Count - 1) + " items from " + row.Name);
                }
                catch (Exception ex)
                {
                    log.Error("OpsGCS plan read", ex);
                    PlanResult("read", false, "Read failed: " + (ex.InnerException ?? ex).Message);
                }
                finally
                {
                    _planBusy = false;
                }
            });
        }

        private void SendPlanData(FleetRow row, System.Collections.Generic.IList<Locationwp> list, string source)
        {
            JObject home;
            JArray items;
            PlanCodec.Split(list, out home, out items);
            if (home == null && row.Snapshot != null && row.Snapshot.HasHome)
                home = new JObject { ["lat"] = row.Snapshot.HomeLat, ["lon"] = row.Snapshot.HomeLon, ["alt"] = row.Snapshot.HomeAltAmsl };

            var msg = new JObject
            {
                ["type"] = "planData",
                ["key"] = row.Mav.sysid + ":" + row.Mav.compid,
                ["vehicle"] = row.Name,
                ["source"] = source,
                ["home"] = home,
                ["items"] = items,
                ["defaults"] = PlanDefaults()
            };
            PostPlan(msg);
        }

        // ------------------------------------------------------------------ terrain profile

        private void PlanProfile(JObject m)
        {
            var id = m.Value<int?>("id") ?? 0;
            var home = m["home"] as JObject;
            var items = m["items"] as JArray;
            Task.Run(() =>
            {
                var reply = new JObject { ["type"] = "planProfile", ["id"] = id, ["minClearance"] = _cfg.PlanMinClearanceM };
                try
                {
                    if (home == null)
                    {
                        reply["error"] = "no home";
                        PostPlan(reply);
                        return;
                    }

                    var hl = home.Value<double>("lat");
                    var hn = home.Value<double>("lon");
                    Func<double, double, double?> terrain = (la, lo) =>
                    {
                        var a = srtm.getAltitude(la, lo);
                        if (a == null) return null;
                        if (a.currenttype == srtm.tiletype.valid) return a.alt;
                        if (a.currenttype == srtm.tiletype.ocean) return 0;
                        return null;
                    };

                    var homeAlt = home["alt"];
                    var homeAmsl = homeAlt != null && homeAlt.Type != JTokenType.Null
                        ? homeAlt.Value<double>()
                        : terrain(hl, hn) ?? 0;

                    var input = PlanCodec.ProfileItems(hl, hn, homeAmsl, items);
                    var prof = MissionProfile.Build(input, homeAmsl, terrain, 400);
                    reply["homeAmsl"] = Math.Round(homeAmsl, 1);
                    reply["terrainMissing"] = prof.TerrainMissing;
                    reply["totalM"] = Math.Round(prof.TotalM, 1);
                    reply["legs"] = new JArray(prof.Legs.Select(l => new JObject
                    {
                        ["to"] = l.To,
                        ["len"] = Math.Round(l.Len, 1),
                        ["minClear"] = l.MinClear == double.MaxValue ? (JToken)null : Math.Round(l.MinClear, 1),
                        ["minClearD"] = Math.Round(l.MinClearD, 1),
                        ["checked"] = l.Checked
                    }));
                    reply["profile"] = new JArray(prof.Profile.Select(p => new JObject
                    {
                        ["d"] = Math.Round(p.D, 1), ["amsl"] = Math.Round(p.Amsl, 1), ["terrain"] = Math.Round(p.Terrain, 1)
                    }));
                    reply["wpMarks"] = new JArray(prof.WpMarks.Select((p, i) => new JObject
                    {
                        ["seq"] = prof.NavItems[i].Seq, ["d"] = Math.Round(p.D, 1), ["amsl"] = Math.Round(p.Amsl, 1),
                        ["terrain"] = Math.Round(p.Terrain, 1)
                    }));
                }
                catch (Exception ex)
                {
                    log.Debug("OpsGCS plan profile", ex);
                    reply["error"] = (ex.InnerException ?? ex).Message;
                }

                PostPlan(reply);
            });
        }

        // ------------------------------------------------------------------ write

        private void PlanWrite(JObject m)
        {
            var key = (string)m["key"];
            var row = FindRow(key);
            if (row == null || row.Snapshot == null || !row.Snapshot.Connected)
            {
                PlanResult("write", false, "Not written: vehicle " + key + " is not connected");
                return;
            }

            if (_planBusy)
            {
                PlanResult("write", false, "Not written: a mission transfer is already running");
                return;
            }

            System.Collections.Generic.List<Locationwp> list;
            try
            {
                list = PlanCodec.BuildUpload(m["home"], m["items"] as JArray);
            }
            catch (ArgumentException ex)
            {
                PlanResult("write", false, "Not written: " + ex.Message);
                return;
            }

            if (m.Value<bool?>("override") == true)
                _alerts.Add(row.Name, HealthLevel.Caution, "Mission written with failed checks overridden (" + _role + ")");

            _planBusy = true;
            var port = row.Port;
            var sysid = row.Mav.sysid;
            var compid = row.Mav.compid;
            var name = row.Name;
            _alerts.Add(name, HealthLevel.Unknown, "Writing mission: " + (list.Count - 1) + " items");
            Task.Run(async () =>
            {
                try
                {
                    await mav_mission.upload(port, sysid, compid, MAVLink.MAV_MISSION_TYPE.MISSION, list,
                        (pct, text) => PostPlan(new JObject { ["type"] = "planProgress", ["op"] = "write", ["pct"] = pct, ["text"] = text }))
                        .ConfigureAwait(false);
                    MainV2.instance.BeginInvokeIfRequired(() =>
                        _alerts.Add(name, HealthLevel.Ok, "Mission written: " + (list.Count - 1) + " items"));
                    PlanResult("write", true, "Mission written to " + name + ": " + (list.Count - 1) + " items");
                }
                catch (Exception ex)
                {
                    log.Error("OpsGCS plan write", ex);
                    var why = (ex.InnerException ?? ex).Message;
                    MainV2.instance.BeginInvokeIfRequired(() => _alerts.Add(name, HealthLevel.Warning, "Mission write failed: " + why));
                    PlanResult("write", false, "Write failed: " + why);
                }
                finally
                {
                    _planBusy = false;
                }
            });
        }
    }
}
