using System;
using MissionPlanner.OpsGCS.Core;

namespace MissionPlanner.OpsGCS.UI
{
    /// <summary>
    /// Copies the fields the dashboard needs out of a live MAVState. Distances and speeds are
    /// converted back to SI, since CurrentState scales them by the user's display units.
    /// </summary>
    public static class SnapshotBuilder
    {
        public static VehicleSnapshot From(MAVLinkInterface port, MAVState mav)
        {
            var s = new VehicleSnapshot();
            if (port == null || mav == null || mav.cs == null)
                return s;

            var cs = mav.cs;

            bool open = false;
            try
            {
                open = port.BaseStream != null && port.BaseStream.IsOpen;
            }
            catch
            {
                // stream disposed mid-read
            }

            s.Connected = open || port.logreadmode;
            s.SecondsSinceLastPacket = mav.lastvalidpacket == DateTime.MinValue
                ? double.MaxValue
                : Math.Max(0, (DateTime.Now - mav.lastvalidpacket).TotalSeconds);
            if (port.logreadmode)
                s.SecondsSinceLastPacket = 0;

            s.LinkQualityPct = cs.linkqualitygcs;

            s.BatteryVoltage = cs.battery_voltage;
            s.BatteryRemainingPct = cs.battery_remaining;
            s.BatteryCurrentA = cs.current;

            s.GpsFixType = (int)cs.gpsstatus;
            s.Satellites = (int)cs.satcount;
            s.Hdop = cs.gpshdop;

            s.EkfVariance = cs.ekfstatus;

            s.VibeX = cs.vibex;
            s.VibeY = cs.vibey;
            s.VibeZ = cs.vibez;

            s.Armed = cs.armed;
            s.Failsafe = cs.failsafe;
            s.Mode = cs.mode;

            s.CurrentWp = (int)cs.wpno;
            s.MissionCount = mav.wps != null ? mav.wps.Count : 0;

            var md = CurrentState.multiplierdist > 0 ? CurrentState.multiplierdist : 1f;
            var ms = CurrentState.multiplierspeed > 0 ? CurrentState.multiplierspeed : 1f;
            s.WpDistM = cs.wp_dist / md;
            s.GroundSpeedMs = cs.groundspeed / ms;
            s.DistToHomeM = cs.DistToHome / md;
            s.AltM = cs.alt / (CurrentState.multiplieralt > 0 ? CurrentState.multiplieralt : 1f);
            s.TimeInAirSec = cs.timeInAir;

            s.Lat = cs.lat;
            s.Lon = cs.lng;
            s.AltAmsl = cs.altasl / (CurrentState.multiplieralt > 0 ? CurrentState.multiplieralt : 1f);
            s.HasPosition = Math.Abs(cs.lat) > 1e-6 || Math.Abs(cs.lng) > 1e-6;

            var home = cs.HomeLocation;
            if (home != null && (Math.Abs(home.Lat) > 1e-6 || Math.Abs(home.Lng) > 1e-6))
            {
                s.HomeLat = home.Lat;
                s.HomeLon = home.Lng;
                s.HomeAltAmsl = home.Alt;
                s.HasHome = true;
            }

            s.Roll = cs.roll;
            s.Pitch = cs.pitch;
            s.Heading = cs.yaw;
            s.ClimbMs = cs.verticalspeed / ms;
            s.AirspeedMs = cs.airspeed / ms;

            s.WindFromDeg = cs.wind_dir;
            s.WindMs = cs.wind_vel / ms;

            return s;
        }
    }
}
