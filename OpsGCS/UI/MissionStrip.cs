using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using MissionPlanner.OpsGCS.Core;

namespace MissionPlanner.OpsGCS.UI
{
    /// <summary>
    /// Bottom-of-map mission bar: WP n/N with per-item ticks, distance and ETA to the active WP,
    /// time in air and distance to home.
    /// </summary>
    public class MissionStrip : Control
    {
        private VehicleSnapshot _s = new VehicleSnapshot();
        private readonly Font _labelFont = OpsPalette.Label(7.5f);
        private readonly Font _valueFont = OpsPalette.Value(10f);

        public MissionStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 4 + _labelFont.Height + _valueFont.Height + 6;
            BackColor = OpsPalette.Surface;
        }

        public void SetState(VehicleSnapshot s)
        {
            _s = s ?? new VehicleSnapshot();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(OpsPalette.Surface);
            using (var p = new Pen(OpsPalette.Border))
                g.DrawLine(p, 0, 0, Width, 0);

            float k = g.DpiX / 96f;
            int pad = (int)(6 * k), x = pad;
            var inv = CultureInfo.InvariantCulture;
            var flyable = Math.Max(0, _s.MissionCount - 1);

            x = Field(g, x, "WP", flyable > 0 ? string.Format(inv, "{0}/{1}", Math.Max(0, _s.CurrentWp), flyable) : "--", (int)(70 * k));
            x = Field(g, x, "TO WP", _s.WpDistM > 0 ? FormatDist(_s.WpDistM) : "--", (int)(84 * k));
            x = Field(g, x, "ETA WP", HealthEvaluator.FormatDuration(HealthEvaluator.EtaToWpSec(_s.WpDistM, _s.GroundSpeedMs)), (int)(74 * k));
            x = Field(g, x, "GS", string.Format(inv, "{0:0.0} m/s", _s.GroundSpeedMs), (int)(84 * k));
            x = Field(g, x, "ALT", string.Format(inv, "{0:0} m", _s.AltM), (int)(70 * k));
            x = Field(g, x, "HOME", FormatDist(_s.DistToHomeM), (int)(84 * k));
            x = Field(g, x, "AIR", HealthEvaluator.FormatDuration(_s.TimeInAirSec), (int)(74 * k));

            // progress bar with one tick per mission item
            int bx = x + pad, bw = Width - bx - pad * 2, by = 3 + _labelFont.Height, bh = (int)(12 * k);
            if (bw < 60 * k)
                return;

            using (var b = new SolidBrush(OpsPalette.SurfaceRaised))
                g.FillRectangle(b, bx, by, bw, bh);

            var prog = HealthEvaluator.MissionProgress(_s.CurrentWp, _s.MissionCount);
            using (var b = new SolidBrush(OpsPalette.Accent))
                g.FillRectangle(b, bx, by, (int)(bw * prog), bh);

            if (flyable > 1 && flyable <= 200)
            {
                using (var p = new Pen(OpsPalette.Surface))
                    for (int i = 1; i < flyable; i++)
                    {
                        int tx = bx + (int)(bw * (i / (double)flyable));
                        g.DrawLine(p, tx, by, tx, by + bh);
                    }
            }

            using (var p = new Pen(OpsPalette.Border))
                g.DrawRectangle(p, bx, by, bw, bh);

            using (var b = new SolidBrush(OpsPalette.TextMuted))
                g.DrawString(string.Format(inv, "MISSION {0:0}%", prog * 100), _labelFont, b, bx, 1);
        }

        private int Field(Graphics g, int x, string label, string value, int w)
        {
            using (var b = new SolidBrush(OpsPalette.TextMuted))
                g.DrawString(label, _labelFont, b, x, 3);
            using (var b = new SolidBrush(OpsPalette.Text))
                g.DrawString(value, _valueFont, b, x, 3 + _labelFont.Height);
            return x + w;
        }

        private static string FormatDist(double m)
        {
            var inv = CultureInfo.InvariantCulture;
            return m >= 1000 ? string.Format(inv, "{0:0.00} km", m / 1000.0) : string.Format(inv, "{0:0} m", m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _labelFont.Dispose();
                _valueFont.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
