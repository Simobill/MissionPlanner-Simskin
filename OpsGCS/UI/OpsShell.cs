using System;
using System.Drawing;
using System.Windows.Forms;
using MissionPlanner.GCSViews;

namespace MissionPlanner.OpsGCS.UI
{
    /// <summary>
    /// Layout v2 for Flight Data: fleet rail + upstream tabs on the left, the upstream map in the
    /// centre, a compact HUD with commands and alerts on the right. The upstream controls are
    /// re-hosted, not copied, so all their behaviour keeps working; upstream's MainH split is hidden.
    /// <see cref="Attach"/> is idempotent and is re-run after anything upstream may have moved
    /// (FlightData_Load's HUD/map swap, layout changes).
    /// </summary>
    public class OpsShell
    {
        // map clutter that the health and mission strips replace
        private static readonly string[] MapClutter =
        {
            "distanceBar1", "windDir1", "label3", "label4", "label5", "label6", "lbl_hdop", "lbl_sats", "CB_tuning"
        };

        private readonly FlightData _fd;
        private readonly TableLayoutPanel _root = new TableLayoutPanel();
        private readonly TableLayoutPanel _left = new TableLayoutPanel();
        private readonly Panel _center = new Panel();
        private readonly TableLayoutPanel _right = new TableLayoutPanel();
        private readonly Panel _hudHost = new Panel();
        private readonly Panel _tabsHost = new Panel();

        public readonly FleetPanel Fleet = new FleetPanel();
        public readonly CommandPanel Commands = new CommandPanel();
        public readonly AlertPanel Alerts = new AlertPanel();

        public OpsShell(FlightData fd)
        {
            _fd = fd;
            var k = Scale(fd);

            _root.Dock = DockStyle.Fill;
            _root.ColumnCount = 3;
            _root.RowCount = 1;
            _root.Margin = new Padding(0);
            _root.Padding = new Padding(0);
            _root.BackColor = OpsPalette.Surface;
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330 * k));
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340 * k));
            _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // left: fleet over the upstream action tabs
            Stack(_left);
            _left.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
            _left.RowStyles.Add(new RowStyle(SizeType.Percent, 66));
            Fleet.Dock = DockStyle.Fill;
            Fleet.Margin = new Padding(0);
            _tabsHost.Dock = DockStyle.Fill;
            _tabsHost.Margin = new Padding(0);
            _tabsHost.BackColor = OpsPalette.Surface;
            _left.Controls.Add(Fleet, 0, 0);
            _left.Controls.Add(_tabsHost, 0, 1);

            _center.Dock = DockStyle.Fill;
            _center.Margin = new Padding(0);
            _center.BackColor = OpsPalette.Surface;

            // right: compact HUD, commands, alerts
            Stack(_right);
            _right.RowStyles.Add(new RowStyle(SizeType.Absolute, 250 * k));
            _right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _hudHost.Dock = DockStyle.Fill;
            _hudHost.Margin = new Padding(0);
            _hudHost.BackColor = OpsPalette.Surface;
            Commands.Dock = DockStyle.Top;
            Commands.Margin = new Padding(0);
            Alerts.Dock = DockStyle.Fill;
            Alerts.Margin = new Padding(0);
            _right.Controls.Add(_hudHost, 0, 0);
            _right.Controls.Add(Commands, 0, 1);
            _right.Controls.Add(Alerts, 0, 2);

            _root.Controls.Add(_left, 0, 0);
            _root.Controls.Add(_center, 1, 0);
            _root.Controls.Add(_right, 2, 0);

            // column separators
            _left.Paint += (s, e) => DrawEdge(e.Graphics, _left, right: true);
            _right.Paint += (s, e) => DrawEdge(e.Graphics, _right, right: false);
        }

        public bool Attached { get; private set; }

        public void Attach()
        {
            var fd = _fd;
            fd.SuspendLayout();
            try
            {
                if (!fd.Controls.Contains(_root))
                {
                    fd.Controls.Add(_root);
                    // Fill-docked control must be in front so the docked strips claim their edges first
                    _root.BringToFront();
                }

                // keep upstream from swapping the HUD back on the next start
                MissionPlanner.Utilities.Settings.Instance["HudSwap"] = "false";

                Host(Find<Control>("hud1"), _hudHost);
                Host(fd.tabControlactions, _tabsHost);
                Host(Find<Control>("tableMap"), _center);

                var mainH = Find<Control>("MainH");
                if (mainH != null)
                    mainH.Visible = false;

                Declutter();
                HideHudSwapItem();
                Attached = true;
            }
            finally
            {
                fd.ResumeLayout(true);
            }
        }

        /// <summary>Upstream may re-show these (tuning toggles, layout refresh); cheap to repeat.</summary>
        public void Declutter()
        {
            foreach (var n in MapClutter)
            {
                var c = Find<Control>(n);
                if (c != null && c.Visible)
                    c.Visible = false;
            }

            var split = Find<SplitContainer>("splitContainer1");
            if (split != null && !split.Panel1Collapsed)
                split.Panel1Collapsed = true; // tuning graph
        }

        private void HideHudSwapItem()
        {
            var hud = Find<Control>("hud1");
            if (hud == null || hud.ContextMenuStrip == null)
                return;
            foreach (ToolStripItem it in hud.ContextMenuStrip.Items)
                if (it.Name == "swapWithMapToolStripMenuItem")
                    it.Visible = false;
        }

        private static void Host(Control c, Control host)
        {
            if (c == null || c.Parent == host)
                return;
            // a dropped-out HUD lives in its own window; it returns to whatever parent it left
            if (c.Parent is Form && c.Parent != host.FindForm())
                return;
            if (c.Parent != null)
                c.Parent.Controls.Remove(c);
            c.Dock = DockStyle.Fill;
            host.Controls.Add(c);
        }

        private T Find<T>(string name) where T : Control
        {
            var found = _fd.Controls.Find(name, true);
            foreach (var c in found)
                if (c is T)
                    return (T)c;
            return null;
        }

        private static void Stack(TableLayoutPanel t)
        {
            t.Dock = DockStyle.Fill;
            t.ColumnCount = 1;
            t.Margin = new Padding(0);
            t.Padding = new Padding(0);
            t.BackColor = OpsPalette.Surface;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        }

        private static void DrawEdge(Graphics g, Control c, bool right)
        {
            using (var p = new Pen(OpsPalette.Border))
            {
                var x = right ? c.Width - 1 : 0;
                g.DrawLine(p, x, 0, x, c.Height);
            }
        }

        private static float Scale(Control c)
        {
            using (var g = c.CreateGraphics())
                return g.DpiX / 96f;
        }
    }
}
