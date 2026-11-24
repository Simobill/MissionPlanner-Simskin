using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using MissionPlanner.OpsGCS.Core;

namespace MissionPlanner.OpsGCS.UI
{
    public class FleetRow
    {
        public MAVLinkInterface Port;
        public MAVState Mav;
        public string Name;
        public VehicleSnapshot Snapshot;
        public List<HealthItem> Health;
        public bool Active;
    }

    /// <summary>
    /// One row per vehicle across every open link. Click a row to make it the active vehicle.
    /// </summary>
    public class FleetPanel : Control
    {
        private List<FleetRow> _rows = new List<FleetRow>();
        private readonly Font _labelFont = OpsPalette.Label(7.5f);
        private readonly Font _rowFont = OpsPalette.Value(9.5f);
        private int Header { get { return _labelFont.Height + 10; } }
        private int RowH { get { return _rowFont.Height + 10; } }

        public event Action<FleetRow> VehicleSelected;

        public FleetPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = OpsPalette.Surface;
            Cursor = Cursors.Hand;
        }

        public void SetRows(List<FleetRow> rows)
        {
            _rows = rows ?? new List<FleetRow>();
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int idx = (e.Y - Header) / RowH;
            if (e.Y >= Header && idx >= 0 && idx < _rows.Count && VehicleSelected != null)
                VehicleSelected(_rows[idx]);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(OpsPalette.Surface);
            var inv = CultureInfo.InvariantCulture;

            // column layout: proportional so the panel works in the narrow left pane
            var cols = new[] { 0.00f, 0.22f, 0.44f, 0.58f, 0.72f, 0.86f };
            var heads = new[] { "VEHICLE", "MODE", "BATT", "LINK", "ALT", "WP" };

            using (var b = new SolidBrush(OpsPalette.TextMuted))
                for (int i = 0; i < heads.Length; i++)
                    g.DrawString(heads[i], _labelFont, b, 8 + cols[i] * (Width - 16), 5);

            using (var p = new Pen(OpsPalette.Border))
                g.DrawLine(p, 0, Header - 1, Width, Header - 1);

            if (_rows.Count == 0)
            {
                using (var b = new SolidBrush(OpsPalette.TextMuted))
                    g.DrawString("No vehicles. Connect a link to populate the fleet.", _rowFont, b, 8, Header + 6);
                return;
            }

            for (int r = 0; r < _rows.Count; r++)
            {
                var row = _rows[r];
                int y = Header + r * RowH;
                var worst = HealthEvaluator.Worst(row.Health ?? new List<HealthItem>());

                using (var b = new SolidBrush(row.Active ? OpsPalette.SurfaceRaised : OpsPalette.Surface))
                    g.FillRectangle(b, 0, y, Width, RowH - 1);
                using (var b = new SolidBrush(OpsPalette.For(worst)))
                    g.FillRectangle(b, 0, y, 4, RowH - 1);
                if (row.Active)
                    using (var p = new Pen(OpsPalette.Accent))
                        g.DrawRectangle(p, 0, y, Width - 1, RowH - 2);

                var s = row.Snapshot ?? new VehicleSnapshot();
                var cells = new[]
                {
                    row.Name,
                    string.IsNullOrEmpty(s.Mode) ? "--" : s.Mode.ToUpperInvariant(),
                    s.BatteryRemainingPct >= 0 ? s.BatteryRemainingPct + "%" : string.Format(inv, "{0:0.0}V", s.BatteryVoltage),
                    s.LinkQualityPct + "%",
                    string.Format(inv, "{0:0}", s.AltM),
                    s.MissionCount > 1 ? string.Format(inv, "{0}/{1}", s.CurrentWp, s.MissionCount - 1) : "--"
                };

                var itemColors = new Color[cells.Length];
                for (int i = 0; i < cells.Length; i++)
                    itemColors[i] = OpsPalette.Text;
                if (row.Health != null)
                    foreach (var h in row.Health)
                    {
                        if (h.Key == "BATT") itemColors[2] = h.Level >= HealthLevel.Caution ? OpsPalette.For(h.Level) : OpsPalette.Text;
                        if (h.Key == "LINK") itemColors[3] = h.Level >= HealthLevel.Caution ? OpsPalette.For(h.Level) : OpsPalette.Text;
                    }
                itemColors[0] = row.Active ? OpsPalette.Accent : OpsPalette.Text;

                for (int i = 0; i < cells.Length; i++)
                    using (var b = new SolidBrush(itemColors[i]))
                        g.DrawString(cells[i], _rowFont, b, 8 + cols[i] * (Width - 16), y + 5);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _labelFont.Dispose();
                _rowFont.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
