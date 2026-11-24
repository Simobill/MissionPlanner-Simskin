using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MissionPlanner.OpsGCS.Core;

namespace MissionPlanner.OpsGCS.UI
{
    /// <summary>
    /// Content of the OPS tab in the Flight Data left pane: role header, fleet table and
    /// a per-item health breakdown of the active vehicle.
    /// </summary>
    public class OpsTabPanel : UserControl
    {
        public readonly FleetPanel Fleet = new FleetPanel();
        private readonly HealthDetail _detail = new HealthDetail();
        private readonly Label _header = new Label();
        private readonly Button _roleButton = new Button();

        public event EventHandler RoleButtonClicked;

        public OpsTabPanel(bool showFleet)
        {
            BackColor = OpsPalette.Surface;
            Dock = DockStyle.Fill;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = OpsPalette.Surface,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, showFleet ? 50 : 0));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, showFleet ? 50 : 100));

            var top = new Panel { Dock = DockStyle.Fill, BackColor = OpsPalette.Surface, Margin = new Padding(0) };
            _header.Dock = DockStyle.Fill;
            _header.ForeColor = OpsPalette.Accent;
            _header.Font = OpsPalette.Label(9f);
            _header.TextAlign = ContentAlignment.MiddleLeft;
            _header.Padding = new Padding(6, 0, 0, 0);

            _roleButton.Dock = DockStyle.Right;
            _roleButton.Width = 110;
            _roleButton.FlatStyle = FlatStyle.Flat;
            _roleButton.FlatAppearance.BorderColor = OpsPalette.Border;
            _roleButton.BackColor = OpsPalette.SurfaceRaised;
            _roleButton.ForeColor = OpsPalette.Text;
            _roleButton.Font = OpsPalette.Label(8f);
            _roleButton.Click += (s, e) =>
            {
                if (RoleButtonClicked != null)
                    RoleButtonClicked(this, EventArgs.Empty);
            };

            top.Controls.Add(_header);
            top.Controls.Add(_roleButton);

            Fleet.Dock = DockStyle.Fill;
            Fleet.Margin = new Padding(0);
            Fleet.Visible = showFleet;
            _detail.Dock = DockStyle.Fill;
            _detail.Margin = new Padding(0);

            layout.Controls.Add(top, 0, 0);
            layout.Controls.Add(Fleet, 0, 1);
            layout.Controls.Add(_detail, 0, 2);
            Controls.Add(layout);
        }

        public void SetRole(string productName, OpsRole role, bool engineerAvailable)
        {
            _header.Text = productName.ToUpperInvariant() + "  |  " + (role == OpsRole.Engineer ? "ENGINEER" : "OPERATOR");
            _roleButton.Text = role == OpsRole.Engineer ? "OPERATOR MODE" : "ENGINEER...";
            _roleButton.Visible = role == OpsRole.Engineer || engineerAvailable;
        }

        public void SetDetail(List<HealthItem> items)
        {
            _detail.SetItems(items);
        }

        private class HealthDetail : Control
        {
            private List<HealthItem> _items = new List<HealthItem>();
            private readonly Font _keyFont = OpsPalette.Label(8.5f);
            private readonly Font _valFont = OpsPalette.Value(10f);
            private readonly Font _detFont = new Font("Segoe UI", 8.5f);

            public HealthDetail()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = OpsPalette.Surface;
            }

            public void SetItems(List<HealthItem> items)
            {
                _items = items ?? new List<HealthItem>();
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(OpsPalette.Surface);
                using (var p = new Pen(OpsPalette.Border))
                    g.DrawLine(p, 0, 0, Width, 0);

                int y = 8, rowH = 30;
                foreach (var it in _items)
                {
                    var c = OpsPalette.For(it.Level);
                    using (var b = new SolidBrush(c))
                        g.FillEllipse(b, 8, y + 7, 10, 10);
                    using (var b = new SolidBrush(OpsPalette.TextMuted))
                        g.DrawString(it.Key, _keyFont, b, 24, y + 5);
                    using (var b = new SolidBrush(it.Level == HealthLevel.Unknown ? OpsPalette.TextMuted : c))
                        g.DrawString(it.Value, _valFont, b, 70, y + 3);
                    using (var b = new SolidBrush(OpsPalette.Text))
                        g.DrawString(it.Detail, _detFont, b, 160, y + 6);
                    y += rowH;
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _keyFont.Dispose();
                    _valFont.Dispose();
                    _detFont.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
