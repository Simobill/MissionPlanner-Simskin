using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using MissionPlanner.OpsGCS.Core;
using MissionPlanner.OpsGCS.Services;

namespace MissionPlanner.OpsGCS.UI
{
    /// <summary>
    /// LAYERS tab: viewshed toggle and source, weather toggle and the weather card.
    /// </summary>
    public class LayersPanel : UserControl
    {
        private readonly CheckBox _viewshed = new CheckBox();
        private readonly RadioButton _sensor = new RadioButton();
        private readonly RadioButton _radio = new RadioButton();
        private readonly Label _vsStatus = new Label();
        private readonly CheckBox _weather = new CheckBox();
        private readonly Button _refresh = new Button();
        public readonly WeatherCard Card = new WeatherCard();

        public event Action<bool> ViewshedToggled;
        public event Action<ViewshedSource> SourceChanged;
        public event Action<bool> WeatherToggled;
        public event Action RefreshRequested;

        private bool _suppress;

        public LayersPanel()
        {
            BackColor = OpsPalette.Surface;
            ForeColor = OpsPalette.Text;
            Dock = DockStyle.Fill;
            AutoScroll = true;

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(8, 6, 8, 4),
                BackColor = OpsPalette.Surface
            };

            flow.Controls.Add(Heading("MAP LAYERS"));

            Style(_viewshed, "Live viewshed");
            _viewshed.CheckedChanged += (s, e) =>
            {
                _sensor.Enabled = _radio.Enabled = _viewshed.Checked;
                if (!_suppress && ViewshedToggled != null) ViewshedToggled(_viewshed.Checked);
            };
            flow.Controls.Add(_viewshed);

            var src = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(20, 0, 0, 0), BackColor = OpsPalette.Surface };
            Style(_sensor, "From vehicle sensor");
            Style(_radio, "GCS radio coverage");
            _sensor.Checked = true;
            _sensor.Enabled = _radio.Enabled = false;
            _sensor.CheckedChanged += (s, e) => { if (_sensor.Checked && !_suppress && SourceChanged != null) SourceChanged(ViewshedSource.Sensor); };
            _radio.CheckedChanged += (s, e) => { if (_radio.Checked && !_suppress && SourceChanged != null) SourceChanged(ViewshedSource.Radio); };
            src.Controls.Add(_sensor);
            src.Controls.Add(_radio);
            flow.Controls.Add(src);

            _vsStatus.AutoSize = true;
            _vsStatus.MaximumSize = new Size(420, 0);
            _vsStatus.ForeColor = OpsPalette.TextMuted;
            _vsStatus.Font = new Font("Segoe UI", 8.5f);
            _vsStatus.Margin = new Padding(22, 2, 0, 8);
            _vsStatus.Text = "Off";
            flow.Controls.Add(_vsStatus);

            var wxRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0), BackColor = OpsPalette.Surface };
            Style(_weather, "Weather (Open-Meteo + METAR)");
            _weather.CheckedChanged += (s, e) =>
            {
                _refresh.Enabled = _weather.Checked;
                if (!_suppress && WeatherToggled != null) WeatherToggled(_weather.Checked);
            };
            _refresh.Text = "Refresh";
            _refresh.AutoSize = true;
            _refresh.Enabled = false;
            _refresh.FlatStyle = FlatStyle.Flat;
            _refresh.FlatAppearance.BorderColor = OpsPalette.Border;
            _refresh.BackColor = OpsPalette.SurfaceRaised;
            _refresh.ForeColor = OpsPalette.Text;
            _refresh.Margin = new Padding(12, 2, 0, 0);
            _refresh.Click += (s, e) => { if (RefreshRequested != null) RefreshRequested(); };
            wxRow.Controls.Add(_weather);
            wxRow.Controls.Add(_refresh);
            flow.Controls.Add(wxRow);

            Card.Dock = DockStyle.Fill;
            Controls.Add(Card);
            Controls.Add(flow);
        }

        public void SetState(bool viewshed, ViewshedSource source, bool weather)
        {
            _suppress = true;
            _viewshed.Checked = viewshed;
            _sensor.Checked = source == ViewshedSource.Sensor;
            _radio.Checked = source == ViewshedSource.Radio;
            _weather.Checked = weather;
            _sensor.Enabled = _radio.Enabled = viewshed;
            _refresh.Enabled = weather;
            _suppress = false;
        }

        public void SetViewshedStatus(string text)
        {
            _vsStatus.Text = string.IsNullOrEmpty(text) ? "Off" : text;
        }

        private static Label Heading(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = OpsPalette.TextMuted,
                Font = OpsPalette.Label(8.5f),
                Margin = new Padding(0, 2, 0, 6)
            };
        }

        private static void Style(ButtonBase b, string text)
        {
            b.Text = text;
            b.AutoSize = true;
            b.ForeColor = OpsPalette.Text;
            b.BackColor = OpsPalette.Surface;
            b.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            b.Margin = new Padding(0, 2, 12, 2);
            b.MinimumSize = new Size(0, 28);
        }
    }

    /// <summary>Painted weather summary; shows which source each value came from and how old it is.</summary>
    public class WeatherCard : Control
    {
        private WeatherSnapshot _snap;
        private VehicleSnapshot _veh;
        private WeatherConfig _cfg = new WeatherConfig();
        private bool _enabled;
        private readonly Font _head = OpsPalette.Label(8.5f);
        private readonly Font _key = new Font("Segoe UI", 9f);
        private readonly Font _val = OpsPalette.Value(9.5f);
        private readonly Font _small = new Font("Consolas", 8f);

        public WeatherCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = OpsPalette.Surface;
        }

        public void SetConfig(WeatherConfig cfg)
        {
            _cfg = cfg ?? new WeatherConfig();
        }

        public void SetData(bool enabled, WeatherSnapshot snap, VehicleSnapshot veh)
        {
            _enabled = enabled;
            _snap = snap;
            _veh = veh;
            Invalidate();
        }

        public static List<KeyValuePair<string, string>> Lines(WeatherSnapshot s, VehicleSnapshot v, WeatherConfig cfg, out string alert)
        {
            var inv = CultureInfo.InvariantCulture;
            var lines = new List<KeyValuePair<string, string>>();
            alert = null;
            var m = s != null ? s.AtVehicle : null;
            var metar = s != null ? s.Metar : null;

            if (m != null && m.WindKt.HasValue)
                lines.Add(new KeyValuePair<string, string>("Wind sfc (model)", string.Format(inv, "{0:000}° / {1:0} kt{2}",
                    m.WindDirDeg ?? 0, m.WindKt.Value, m.GustKt.HasValue && m.GustKt.Value > m.WindKt.Value + 3 ? string.Format(inv, " G{0:0}", m.GustKt.Value) : "")));

            if (metar != null && metar.WindKt.HasValue)
                lines.Add(new KeyValuePair<string, string>("Wind " + metar.Station, string.Format(inv, "{0} / {1} kt{2}",
                    metar.WindDirDeg.HasValue ? metar.WindDirDeg.Value.ToString("000", inv) + "°" : "VRB",
                    metar.WindKt.Value, metar.GustKt.HasValue ? " G" + metar.GustKt.Value : "")));

            if (v != null && v.Connected && v.WindMs > 0.1)
                lines.Add(new KeyValuePair<string, string>("Wind @ vehicle", string.Format(inv, "{0:000}° / {1:0} kt (EKF)", v.WindFromDeg, v.WindMs * 1.943844)));

            if (metar != null)
                lines.Add(new KeyValuePair<string, string>("Ceiling", metar.CeilingFt.HasValue
                    ? string.Format(inv, "{0} {1} ft ({2})", metar.CeilingCover, metar.CeilingFt.Value, metar.Station)
                    : "None reported (" + metar.Station + ")"));
            else if (m != null && m.CloudPct.HasValue)
                lines.Add(new KeyValuePair<string, string>("Cloud (model)", string.Format(inv, "{0:0}% cover", m.CloudPct.Value)));

            if (metar != null && metar.VisibilityKm.HasValue)
                lines.Add(new KeyValuePair<string, string>("Visibility", string.Format(inv, "{0:0.#}{1} km ({2}){3}", metar.VisibilityKm.Value,
                    metar.VisibilityOrMore ? "+" : "", metar.Station, string.IsNullOrEmpty(metar.Wx) ? "" : " " + metar.Wx)));
            else if (m != null && m.VisibilityM.HasValue)
                lines.Add(new KeyValuePair<string, string>("Visibility (model)", string.Format(inv, "{0:0.#} km", m.VisibilityM.Value / 1000.0)));

            var temp = metar != null && metar.TempC.HasValue ? metar.TempC : (m != null ? m.TempC : null);
            var qnh = metar != null && metar.AltimHpa.HasValue ? metar.AltimHpa : (m != null ? m.PressureHpa : null);
            if (temp.HasValue || qnh.HasValue)
                lines.Add(new KeyValuePair<string, string>("Temp / QNH", string.Format(inv, "{0} / {1}",
                    temp.HasValue ? temp.Value.ToString("0", inv) + " °C" : "--", qnh.HasValue ? qnh.Value.ToString("0", inv) + " hPa" : "--")));

            var wet = s != null ? WeatherGrid.Wettest(s.Grid) : null;
            if (wet != null && wet.PrecipMm.HasValue && wet.PrecipMm.Value >= cfg.PrecipCautionMm)
            {
                if (v != null && v.HasPosition)
                {
                    var km = Viewshed.DistanceM(v.Lat, v.Lon, wet.Lat, wet.Lon) / 1000.0;
                    var brg = Viewshed.BearingDeg(v.Lat, v.Lon, wet.Lat, wet.Lon);
                    alert = km < 1
                        ? string.Format(inv, "Precipitation {0:0.0} mm at the vehicle", wet.PrecipMm.Value)
                        : string.Format(inv, "Precipitation {0:0.0} mm, {1:0} km {2}", wet.PrecipMm.Value, km, Viewshed.CompassPoint(brg));
                }
                else
                {
                    alert = string.Format(inv, "Precipitation up to {0:0.0} mm in the area", wet.PrecipMm.Value);
                }
            }

            return lines;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(OpsPalette.Surface);
            var k = g.DpiX / 96f;
            float x = 10 * k, y = 6 * k, col2 = 150 * k;
            var inv = CultureInfo.InvariantCulture;

            using (var p = new Pen(OpsPalette.Border))
                g.DrawLine(p, 0, 0, Width, 0);

            if (!_enabled)
            {
                using (var b = new SolidBrush(OpsPalette.TextMuted))
                    g.DrawString("Weather off. Needs internet: api.open-meteo.com, aviationweather.gov.", _key, b, x, y + 4 * k);
                return;
            }

            // heading with age; stale data turns amber
            string age = "fetching…";
            var stale = false;
            if (_snap != null)
            {
                var mins = (DateTime.UtcNow - _snap.FetchedUtc).TotalMinutes;
                stale = mins > _cfg.StaleMin;
                age = mins < 1 ? "updated just now" : string.Format(inv, "updated {0:0} min ago", mins);
            }

            using (var b = new SolidBrush(OpsPalette.TextMuted))
                g.DrawString("WEATHER", _head, b, x, y);
            using (var b = new SolidBrush(stale ? OpsPalette.Caution : OpsPalette.TextMuted))
                g.DrawString(age, _key, b, col2, y);
            y += _head.Height + 6 * k;

            string alert;
            var lines = Lines(_snap, _veh, _cfg, out alert);
            foreach (var l in lines)
            {
                using (var b = new SolidBrush(OpsPalette.TextMuted))
                    g.DrawString(l.Key, _key, b, x, y);
                using (var b = new SolidBrush(OpsPalette.Text))
                    g.DrawString(l.Value, _val, b, col2, y);
                y += Math.Max(_key.Height, _val.Height) + 4 * k;
            }

            if (alert != null)
            {
                var h = _key.Height + 8 * k;
                using (var b = new SolidBrush(Color.FromArgb(0x2A, 0x23, 0x12)))
                    g.FillRectangle(b, x, y + 2, Width - 2 * x, h);
                using (var b = new SolidBrush(OpsPalette.Caution))
                {
                    g.FillRectangle(b, x, y + 2, 4 * k, h);
                    g.DrawString(alert, _key, b, x + 10 * k, y + 2 + 4 * k);
                }
                y += h + 8 * k;
            }

            if (_snap != null && _snap.Metar != null && !string.IsNullOrEmpty(_snap.Metar.Raw))
            {
                using (var b = new SolidBrush(OpsPalette.TextMuted))
                    g.DrawString(_snap.Metar.Raw, _small, b, new RectangleF(x, y, Width - 2 * x, _small.Height * 3));
                y += _small.Height * 2 + 6 * k;
            }

            var err = _snap == null ? null : (_snap.ModelError ?? "") + (_snap.MetarError != null && _snap.ModelError != null ? " · " : "") + (_snap.MetarError ?? "");
            if (!string.IsNullOrEmpty(err))
                using (var b = new SolidBrush(OpsPalette.Warning))
                    g.DrawString(err, _small, b, new RectangleF(x, y, Width - 2 * x, _small.Height * 3));

            using (var b = new SolidBrush(OpsPalette.TextMuted))
                g.DrawString("Model: Open-Meteo.com (CC BY 4.0) · METAR: aviationweather.gov", _small, b, x, Height - _small.Height - 4 * k);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _head.Dispose();
                _key.Dispose();
                _val.Dispose();
                _small.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
