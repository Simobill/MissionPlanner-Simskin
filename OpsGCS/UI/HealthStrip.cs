using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using MissionPlanner.OpsGCS.Core;

namespace MissionPlanner.OpsGCS.UI
{
    /// <summary>
    /// Top-of-map status bar: vehicle id, mode, arm state, then one tile per health item.
    /// Custom painted so it stays legible regardless of the WinForms theme.
    /// </summary>
    public class HealthStrip : Control
    {
        private List<HealthItem> _items = new List<HealthItem>();
        private string _vehicle = "NO VEHICLE";
        private string _mode = "--";
        private bool _armed;
        private bool _failsafe;
        private bool _connected;

        private readonly Font _labelFont = OpsPalette.Label(7.5f);
        private readonly Font _valueFont = OpsPalette.Value(11f);
        private readonly Font _modeFont = OpsPalette.Value(13f);
        private readonly ToolTip _tip = new ToolTip();
        private readonly List<KeyValuePair<Rectangle, string>> _hit = new List<KeyValuePair<Rectangle, string>>();

        public HealthStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 44;
            BackColor = OpsPalette.Surface;
            ForeColor = OpsPalette.Text;
        }

        public void SetState(string vehicle, VehicleSnapshot snap, List<HealthItem> items)
        {
            _vehicle = vehicle ?? "NO VEHICLE";
            _items = items ?? new List<HealthItem>();
            _connected = snap != null && snap.Connected;
            _mode = snap != null && !string.IsNullOrEmpty(snap.Mode) ? snap.Mode.ToUpperInvariant() : "--";
            _armed = snap != null && snap.Armed;
            _failsafe = snap != null && snap.Failsafe;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            foreach (var kv in _hit)
            {
                if (kv.Key.Contains(e.Location))
                {
                    if (_tip.GetToolTip(this) != kv.Value)
                        _tip.SetToolTip(this, kv.Value);
                    return;
                }
            }

            _tip.SetToolTip(this, "");
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(OpsPalette.Surface);
            _hit.Clear();

            int pad = 6, x = pad, h = Height - pad * 2;

            // vehicle block
            x = DrawBlock(g, x, pad, 120, h, "VEHICLE", _vehicle, _connected ? OpsPalette.Accent : OpsPalette.Unknown, null);

            // mode
            x = DrawBlock(g, x, pad, 130, h, "MODE", _mode, OpsPalette.Text, _modeFont);

            // arm / failsafe
            var armText = !_connected ? "--" : _failsafe ? "FAILSAFE" : _armed ? "ARMED" : "DISARMED";
            var armColor = !_connected ? OpsPalette.Unknown
                : _failsafe ? OpsPalette.Warning
                : _armed ? OpsPalette.Caution
                : OpsPalette.Ok;
            x = DrawBlock(g, x, pad, 104, h, "STATE", armText, armColor, null);

            x += pad;

            // health tiles share the remaining width
            if (_items.Count > 0)
            {
                int remaining = Width - x - pad;
                int w = Math.Max(78, remaining / _items.Count - pad);
                foreach (var item in _items)
                {
                    var rect = new Rectangle(x, pad, w, h);
                    DrawTile(g, rect, item);
                    _hit.Add(new KeyValuePair<Rectangle, string>(rect, item.Key + ": " + item.Detail));
                    x += w + pad;
                }
            }

            using (var p = new Pen(OpsPalette.Border))
                g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }

        private int DrawBlock(Graphics g, int x, int y, int w, int h, string label, string value, Color valueColor, Font font)
        {
            using (var b = new SolidBrush(OpsPalette.SurfaceRaised))
                g.FillRectangle(b, x, y, w, h);
            using (var b = new SolidBrush(OpsPalette.TextMuted))
                g.DrawString(label, _labelFont, b, x + 6, y + 1);
            using (var b = new SolidBrush(valueColor))
                g.DrawString(value, font ?? _valueFont, b, x + 5, y + 12);
            return x + w + 4;
        }

        private void DrawTile(Graphics g, Rectangle r, HealthItem item)
        {
            var c = OpsPalette.For(item.Level);
            using (var b = new SolidBrush(OpsPalette.SurfaceRaised))
                g.FillRectangle(b, r);
            using (var b = new SolidBrush(c))
                g.FillRectangle(b, r.X, r.Y, 4, r.Height);

            // warning tiles get a full outline so they read at a glance
            if (item.Level == HealthLevel.Warning)
                using (var p = new Pen(c, 2))
                    g.DrawRectangle(p, r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);

            using (var b = new SolidBrush(OpsPalette.TextMuted))
                g.DrawString(item.Key, _labelFont, b, r.X + 9, r.Y + 1);
            using (var b = new SolidBrush(item.Level == HealthLevel.Unknown ? OpsPalette.TextMuted : c))
                g.DrawString(item.Value, _valueFont, b, r.X + 8, r.Y + 12);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _labelFont.Dispose();
                _valueFont.Dispose();
                _modeFont.Dispose();
                _tip.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
