using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using MissionPlanner.OpsGCS.Core;

namespace MissionPlanner.OpsGCS.UI
{
    public class CommandRequest
    {
        public string Label;   // shown on the button and dialog title
        public string Mode;    // ArduPilot mode name passed to setMode
        public bool Caution;   // amber button (RTL)
    }

    /// <summary>Labelled command buttons for the active vehicle; each opens a hold-to-confirm dialog.</summary>
    public class CommandPanel : UserControl
    {
        private readonly Label _title = new Label();
        private readonly TableLayoutPanel _grid = new TableLayoutPanel();
        private readonly Label _note = new Label();
        private List<CommandRequest> _commands = new List<CommandRequest>();

        public event Action<CommandRequest> CommandClicked;
        public event Action ModeMenuRequested;

        public CommandPanel()
        {
            BackColor = OpsPalette.Surface;
            Padding = new Padding(10, 6, 10, 6);

            _title.Dock = DockStyle.Top;
            _title.Height = OpsPalette.Label(8.5f).Height + 8;
            _title.ForeColor = OpsPalette.TextMuted;
            _title.Font = OpsPalette.Label(8.5f);
            _title.Text = "COMMANDS";

            _grid.Dock = DockStyle.Top;
            _grid.ColumnCount = 2;
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _grid.AutoSize = true;
            _grid.BackColor = OpsPalette.Surface;

            _note.Dock = DockStyle.Top;
            _note.AutoSize = false;
            _note.Height = 22;
            _note.ForeColor = OpsPalette.TextMuted;
            _note.Font = new Font("Segoe UI", 8.5f);
            _note.Text = "Every command asks for a hold-to-confirm.";

            Controls.Add(_note);
            Controls.Add(_grid);
            Controls.Add(_title);
        }

        public void SetTarget(string vehicle, List<CommandRequest> commands)
        {
            _title.Text = "COMMANDS · " + (vehicle ?? "NO VEHICLE");
            if (Same(commands))
                return;

            _commands = commands ?? new List<CommandRequest>();
            _grid.SuspendLayout();
            _grid.Controls.Clear();
            _grid.RowStyles.Clear();
            var all = new List<Button>();
            foreach (var c in _commands)
            {
                var cmd = c;
                var b = MakeButton(c.Label, c.Caution);
                b.Click += (s, e) => { if (CommandClicked != null) CommandClicked(cmd); };
                all.Add(b);
            }

            var mode = MakeButton("Change mode…", false);
            mode.Click += (s, e) => { if (ModeMenuRequested != null) ModeMenuRequested(); };
            all.Add(mode);

            for (int i = 0; i < all.Count; i++)
                _grid.Controls.Add(all[i], i % 2, i / 2);
            _grid.ResumeLayout(true);
            Height = _title.Height + _grid.PreferredSize.Height + _note.Height + Padding.Vertical + 4;
        }

        private bool Same(List<CommandRequest> c)
        {
            if (c == null || c.Count != _commands.Count)
                return false;
            for (int i = 0; i < c.Count; i++)
                if (c[i].Mode != _commands[i].Mode || c[i].Label != _commands[i].Label)
                    return false;
            return true;
        }

        private static Button MakeButton(string text, bool caution)
        {
            var b = new Button
            {
                Text = text,
                Dock = DockStyle.Fill,
                Height = 44,
                Margin = new Padding(3),
                FlatStyle = FlatStyle.Flat,
                BackColor = OpsPalette.SurfaceRaised,
                ForeColor = caution ? OpsPalette.Caution : OpsPalette.Text,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = caution ? OpsPalette.Caution : OpsPalette.Border;
            b.FlatAppearance.MouseOverBackColor = OpsPalette.SurfaceRaised2;
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(0x2A, 0x23, 0x12);
            return b;
        }
    }

    /// <summary>
    /// Modal confirmation: names the target vehicle large, lists what will change, and sends only
    /// after the button (or Space) is held for <see cref="HoldMs"/>. Releasing early cancels the hold.
    /// </summary>
    public class HoldConfirmDialog : Form
    {
        public const int HoldMs = 1500;

        private readonly Timer _timer = new Timer { Interval = 30 };
        private readonly Panel _hold = new Panel();
        private DateTime _start;
        private bool _holding;
        private double _progress;
        private readonly string _holdText;

        public HoldConfirmDialog(string title, string vehicle, string vehicleDetail, IList<KeyValuePair<string, string>> facts, string holdText)
        {
            _holdText = holdText;
            Text = "Confirm command";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = OpsPalette.Surface;
            ForeColor = OpsPalette.Text;
            Font = new Font("Segoe UI", 10f);
            KeyPreview = true;
            Width = 560;

            var stack = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(22, 14, 22, 10),
                BackColor = OpsPalette.Surface,
                AutoScroll = false
            };

            var bar = new Panel { Dock = DockStyle.Top, Height = 4, BackColor = OpsPalette.Caution };
            stack.Controls.Add(new Label { Text = "CONFIRM COMMAND", AutoSize = true, ForeColor = OpsPalette.TextMuted, Font = OpsPalette.Label(8.5f) });
            stack.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI", 18f, FontStyle.Bold), Margin = new Padding(0, 2, 0, 10) });

            var target = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = OpsPalette.SurfaceRaised, Padding = new Padding(12, 8, 12, 8), Margin = new Padding(0, 0, 0, 12) };
            target.Controls.Add(new Label { Text = vehicle, AutoSize = true, ForeColor = OpsPalette.Accent, Font = new Font("Consolas", 24f, FontStyle.Bold), Margin = new Padding(0, 0, 16, 0) }, 0, 0);
            target.Controls.Add(new Label { Text = vehicleDetail + "\nOnly this vehicle receives the command.", AutoSize = true, ForeColor = OpsPalette.Text, Font = new Font("Segoe UI", 9.5f), Anchor = AnchorStyles.Left }, 1, 0);
            stack.Controls.Add(target);

            var factsGrid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = OpsPalette.Surface, Margin = new Padding(0, 0, 0, 10) };
            foreach (var f in facts)
            {
                factsGrid.Controls.Add(new Label { Text = f.Key, AutoSize = true, ForeColor = OpsPalette.TextMuted, Margin = new Padding(0, 3, 24, 3) });
                factsGrid.Controls.Add(new Label { Text = f.Value, AutoSize = true, ForeColor = OpsPalette.Text, Font = new Font("Consolas", 10f), Margin = new Padding(0, 3, 0, 3) });
            }

            stack.Controls.Add(factsGrid);
            stack.Controls.Add(new Label { Text = "Hold the button (or Space) for 1.5 s to send. Release early to cancel.", AutoSize = true, ForeColor = OpsPalette.TextMuted, Margin = new Padding(0, 4, 0, 10) });

            var buttons = new TableLayoutPanel { ColumnCount = 2, Width = 500, Height = 54, BackColor = OpsPalette.Surface };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
            var cancel = new Button { Text = "Cancel", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, BackColor = OpsPalette.SurfaceRaised, ForeColor = OpsPalette.Text, DialogResult = DialogResult.Cancel, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold) };
            cancel.FlatAppearance.BorderColor = OpsPalette.Border;
            CancelButton = cancel;

            _hold.Dock = DockStyle.Fill;
            _hold.Margin = new Padding(8, 3, 0, 3);
            _hold.Cursor = Cursors.Hand;
            _hold.TabStop = true;
            _hold.Paint += PaintHold;
            _hold.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) BeginHold(); };
            _hold.MouseUp += (s, e) => EndHold();
            _hold.MouseLeave += (s, e) => EndHold();
            typeof(Panel).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(_hold, true, null);

            buttons.Controls.Add(cancel, 0, 0);
            buttons.Controls.Add(_hold, 1, 0);
            stack.Controls.Add(buttons);

            Controls.Add(stack);
            Controls.Add(bar);

            KeyDown += (s, e) => { if (e.KeyCode == Keys.Space && !e.Alt && !e.Control) { e.Handled = true; BeginHold(); } };
            KeyUp += (s, e) => { if (e.KeyCode == Keys.Space) { e.Handled = true; EndHold(); } };
            _timer.Tick += Tick;

            Load += (s, e) =>
            {
                var h = stack.PreferredSize.Height + bar.Height + 10;
                ClientSize = new Size(ClientSize.Width, h);
                cancel.Focus();
            };
        }

        private void BeginHold()
        {
            if (_holding)
                return;
            _holding = true;
            _start = DateTime.UtcNow;
            _timer.Start();
        }

        private void EndHold()
        {
            if (!_holding)
                return;
            _holding = false;
            _timer.Stop();
            _progress = 0;
            _hold.Invalidate();
        }

        private void Tick(object sender, EventArgs e)
        {
            _progress = Math.Min(1.0, (DateTime.UtcNow - _start).TotalMilliseconds / HoldMs);
            _hold.Invalidate();
            if (_progress >= 1.0)
            {
                _timer.Stop();
                _holding = false;
                DialogResult = DialogResult.OK;
                Close();
            }
        }

        private void PaintHold(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = _hold.ClientRectangle;
            using (var b = new SolidBrush(Color.FromArgb(0x2A, 0x23, 0x12)))
                g.FillRectangle(b, r);
            using (var b = new SolidBrush(Color.FromArgb(70, OpsPalette.Caution)))
                g.FillRectangle(b, 0, 0, (int)(r.Width * _progress), r.Height);
            using (var p = new Pen(OpsPalette.Caution))
                g.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);
            var text = _holdText + (_holding ? string.Format(CultureInfo.InvariantCulture, "  {0:0.0} s", _progress * HoldMs / 1000.0) : "");
            TextRenderer.DrawText(g, text, new Font("Segoe UI", 10.5f, FontStyle.Bold), r, OpsPalette.Caution,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _timer.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Newest-first alert list with a severity bar per line.</summary>
    public class AlertPanel : Control
    {
        private List<OpsAlert> _items = new List<OpsAlert>();
        private int _version = -1;
        private readonly Font _head = OpsPalette.Label(8.5f);
        private readonly Font _veh = new Font("Segoe UI", 9f, FontStyle.Bold);
        private readonly Font _txt = new Font("Segoe UI", 9f);
        private readonly Font _time = new Font("Consolas", 8.5f);

        public AlertPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = OpsPalette.Surface;
        }

        public void SetItems(AlertLog log)
        {
            if (log == null || log.Version == _version)
                return;
            _version = log.Version;
            _items = log.Snapshot();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(OpsPalette.Surface);
            var k = g.DpiX / 96f;
            float x = 10 * k, y = 6 * k;
            using (var p = new Pen(OpsPalette.Border))
                g.DrawLine(p, 0, 0, Width, 0);
            var warn = 0;
            foreach (var a in _items)
                if (a.Level >= HealthLevel.Caution) warn++;
            using (var b = new SolidBrush(OpsPalette.TextMuted))
                g.DrawString("ALERTS · " + warn, _head, b, x, y);
            y += _head.Height + 6 * k;

            var rowH = _txt.Height * 2 + 10 * k;
            foreach (var a in _items)
            {
                if (y + rowH > Height)
                    break;
                var c = a.Level == HealthLevel.Warning ? OpsPalette.Warning
                    : a.Level == HealthLevel.Caution ? OpsPalette.Caution
                    : a.Level == HealthLevel.Ok ? OpsPalette.Ok
                    : OpsPalette.Unknown;
                var bg = a.Level == HealthLevel.Warning ? Color.FromArgb(0x2A, 0x14, 0x16)
                    : a.Level == HealthLevel.Caution ? Color.FromArgb(0x2A, 0x23, 0x12)
                    : OpsPalette.SurfaceRaised;
                using (var b = new SolidBrush(bg))
                    g.FillRectangle(b, x, y, Width - 2 * x, rowH - 4 * k);
                using (var b = new SolidBrush(c))
                    g.FillRectangle(b, x, y, 4 * k, rowH - 4 * k);
                using (var b = new SolidBrush(c == OpsPalette.Unknown ? OpsPalette.Text : c))
                    g.DrawString(a.Vehicle, _veh, b, x + 10 * k, y + 3 * k);
                using (var b = new SolidBrush(OpsPalette.TextMuted))
                    g.DrawString(a.TimeUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "Z", _time, b, Width - x - 70 * k, y + 4 * k);
                using (var b = new SolidBrush(OpsPalette.Text))
                    g.DrawString(a.Text, _txt, b, new RectangleF(x + 10 * k, y + 3 * k + _veh.Height, Width - 2 * x - 14 * k, _txt.Height + 2), new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
                y += rowH;
            }

            if (_items.Count == 0)
                using (var b = new SolidBrush(OpsPalette.TextMuted))
                    g.DrawString("No alerts.", _txt, b, x, y);
        }
    }
}
