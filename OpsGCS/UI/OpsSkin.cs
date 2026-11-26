using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using MissionPlanner.Controls;

namespace MissionPlanner.OpsGCS.UI
{
    /// <summary>
    /// Operational skin applied on top of upstream theming: flat text menu bar, Segoe UI fonts,
    /// dark tab headers, quiet QuickView numbers, muted HUD, dark bordered buttons.
    /// Upstream re-themes a screen each time it is shown, so Apply is idempotent and is re-run
    /// after every screen switch.
    /// </summary>
    public static class OpsSkin
    {
        public const string UiFamily = "Segoe UI";

        private static readonly HashSet<string> LegacyFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft Sans Serif", "Tahoma", "Arial", "MS Sans Serif", "Verdana"
        };

        private static readonly ConditionalWeakTable<TabControl, object> SkinnedTabs = new ConditionalWeakTable<TabControl, object>();
        private static readonly Font MenuFont = new Font(UiFamily, 10f, FontStyle.Bold);
        private static readonly Font BrandFont = new Font(UiFamily, 11f, FontStyle.Bold);
        private static readonly Font TabFont = new Font(UiFamily, 9f, FontStyle.Bold);
        private static ToolStripLabel _brand;

        /// <summary>Screen name per main-menu button, to mark the active one.</summary>
        public static string ScreenFor(ToolStripItem item, MainV2 main)
        {
            if (item == main.MenuFlightData) return "FlightData";
            if (item == main.MenuFlightPlanner) return "FlightPlanner";
            if (item == main.MenuInitConfig) return "HWConfig";
            if (item == main.MenuConfigTune) return "SWConfig";
            if (item == main.MenuSimulation) return "Simulation";
            if (item == main.MenuHelp) return "Help";
            return null;
        }

        public static string CurrentScreen()
        {
            var v = MainV2.View;
            return v != null && v.current != null ? v.current.Name : null;
        }

        // ------------------------------------------------------------------ menu bar

        public static void StyleMenu(MainV2 main, string product)
        {
            var menu = main.MainMenu;
            if (menu == null)
                return;

            menu.SuspendLayout();
            menu.BackgroundImage = null;
            menu.BackColor = OpsPalette.SurfaceDeep;
            menu.Renderer = new OpsMenuRenderer(main);
            menu.Padding = new Padding(8, 4, 8, 4);
            menu.Font = MenuFont;

            SetText(main.MenuFlightData, "FLY");
            SetText(main.MenuFlightPlanner, "PLAN");
            SetText(main.MenuInitConfig, "SETUP");
            SetText(main.MenuConfigTune, "CONFIG");
            SetText(main.MenuSimulation, "SIM");
            SetText(main.MenuHelp, "HELP");

            if (main.MenuConnect != null)
            {
                main.MenuConnect.DisplayStyle = ToolStripItemDisplayStyle.Text;
                main.MenuConnect.Font = MenuFont;
                main.MenuConnect.Padding = new Padding(14, 6, 14, 6);
            }

            if (main.MenuArduPilot != null)
                main.MenuArduPilot.DisplayStyle = ToolStripItemDisplayStyle.None;

            if (_brand == null)
            {
                _brand = new ToolStripLabel(product.ToUpperInvariant())
                {
                    Name = "opsgcsBrand",
                    Font = BrandFont,
                    ForeColor = OpsPalette.Accent,
                    Padding = new Padding(4, 0, 18, 0)
                };
                menu.Items.Insert(0, _brand);
            }
            else
            {
                _brand.Text = product.ToUpperInvariant();
            }

            // connection combos and their host
            foreach (ToolStripItem it in menu.Items)
            {
                var host = it as ToolStripControlHost;
                if (host == null || host.Control == null)
                    continue;
                host.BackColor = OpsPalette.SurfaceDeep;
                host.Control.BackColor = OpsPalette.SurfaceDeep;
                host.Control.ForeColor = OpsPalette.Text;
                ApplyFonts(host.Control);
                foreach (Control c in host.Control.Controls)
                {
                    if (c is ComboBox)
                    {
                        c.BackColor = OpsPalette.SurfaceRaised;
                        c.ForeColor = OpsPalette.Text;
                        ((ComboBox)c).FlatStyle = FlatStyle.Flat;
                    }
                    else
                    {
                        c.BackColor = OpsPalette.SurfaceDeep;
                        c.ForeColor = OpsPalette.TextMuted;
                    }
                }
            }

            menu.ResumeLayout(true);
            menu.Invalidate();
        }

        private static void SetText(ToolStripItem item, string text)
        {
            if (item == null)
                return;
            item.Text = text;
            item.DisplayStyle = ToolStripItemDisplayStyle.Text;
            item.Font = MenuFont;
            item.Padding = new Padding(16, 6, 16, 6);
            item.Margin = new Padding(2, 0, 2, 0);
            item.AutoSize = true;
        }

        private class OpsMenuRenderer : ToolStripProfessionalRenderer
        {
            private readonly MainV2 _main;

            public OpsMenuRenderer(MainV2 main)
            {
                _main = main;
                RoundedEdges = false;
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using (var b = new SolidBrush(OpsPalette.SurfaceDeep))
                    e.Graphics.FillRectangle(b, e.AffectedBounds);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using (var p = new Pen(OpsPalette.Border))
                    e.Graphics.DrawLine(p, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
            }

            protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
            {
                DrawItemBackground(e.Graphics, e.Item);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                DrawItemBackground(e.Graphics, e.Item);
            }

            private void DrawItemBackground(Graphics g, ToolStripItem item)
            {
                var r = new Rectangle(0, 0, item.Width, item.Height);
                var screen = ScreenFor(item, _main);
                var active = screen != null && screen == CurrentScreen();
                var connect = item == _main.MenuConnect;

                if (connect)
                {
                    var open = IsConnected();
                    using (var b = new SolidBrush(open ? OpsPalette.SurfaceRaised : Color.FromArgb(0x2A, 0x23, 0x12)))
                        g.FillRectangle(b, r.X + 2, r.Y + 4, r.Width - 4, r.Height - 8);
                    using (var p = new Pen(open ? OpsPalette.Ok : OpsPalette.Accent))
                        g.DrawRectangle(p, r.X + 2, r.Y + 4, r.Width - 5, r.Height - 9);
                    return;
                }

                if (active)
                {
                    using (var b = new SolidBrush(OpsPalette.SurfaceRaised2))
                        g.FillRectangle(b, r.X + 2, r.Y + 4, r.Width - 4, r.Height - 8);
                    using (var b = new SolidBrush(OpsPalette.Accent))
                        g.FillRectangle(b, r.X + 8, r.Bottom - 6, r.Width - 16, 3);
                }
                else if (item.Selected || item.Pressed)
                {
                    using (var b = new SolidBrush(OpsPalette.SurfaceRaised))
                        g.FillRectangle(b, r.X + 2, r.Y + 4, r.Width - 4, r.Height - 8);
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (e.Item.Name == "opsgcsBrand")
                {
                    e.TextColor = OpsPalette.Accent;
                }
                else if (e.Item == _main.MenuConnect)
                {
                    e.TextColor = IsConnected() ? OpsPalette.Ok : OpsPalette.Accent;
                }
                else
                {
                    var screen = ScreenFor(e.Item, _main);
                    var active = screen != null && screen == CurrentScreen();
                    e.TextColor = active ? Color.White : e.Item.Selected ? OpsPalette.Text : OpsPalette.TextMuted;
                }

                base.OnRenderItemText(e);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                using (var p = new Pen(OpsPalette.Border))
                    e.Graphics.DrawLine(p, e.Item.Width / 2, 6, e.Item.Width / 2, e.Item.Height - 6);
            }

            private static bool IsConnected()
            {
                try
                {
                    return MainV2.comPort != null && MainV2.comPort.BaseStream != null && MainV2.comPort.BaseStream.IsOpen;
                }
                catch
                {
                    return false;
                }
            }
        }

        // ------------------------------------------------------------------ whole-tree pass

        /// <summary>Fonts, tabs, buttons and QuickViews under <paramref name="root"/>.</summary>
        public static void ApplyTree(Control root)
        {
            if (root == null)
                return;

            Walk(root);
        }

        private static void Walk(Control c)
        {
            // our own controls are already styled
            if (c.GetType().Namespace != null && c.GetType().Namespace.StartsWith("MissionPlanner.OpsGCS", StringComparison.Ordinal))
                return;

            if (c is GMap.NET.WindowsForms.GMapControl || c is HUD)
                return;

            var qv = c as QuickView;
            if (qv != null)
            {
                StyleQuickView(qv);
                return; // upstream draws its own children
            }

            ReplaceFont(c);

            var tabs = c as TabControl;
            if (tabs != null)
                SkinTabControl(tabs);

            var mb = c as MyButton;
            if (mb != null)
                StyleButton(mb);

            foreach (Control child in c.Controls)
                Walk(child);
        }

        public static void ApplyFonts(Control root)
        {
            ReplaceFont(root);
            foreach (Control child in root.Controls)
                ApplyFonts(child);
        }

        private static void ReplaceFont(Control c)
        {
            var f = c.Font;
            if (f == null || !LegacyFamilies.Contains(f.FontFamily.Name))
                return;

            // inherited fonts change with the parent; only replace fonts set on this control
            if (c.Parent != null && ReferenceEquals(c.Parent.Font, f))
                return;

            try
            {
                c.Font = new Font(UiFamily, f.Size, f.Style, f.Unit);
            }
            catch (ArgumentException)
            {
                // family missing on this machine: keep upstream font
            }
        }

        // ------------------------------------------------------------------ pieces

        public static void StyleButton(MyButton b)
        {
            b.BGGradTop = OpsPalette.SurfaceRaised2;
            b.BGGradBot = OpsPalette.SurfaceRaised;
            b.TextColor = OpsPalette.Text;
            b.Outline = OpsPalette.Border;
            b.ColorMouseOver = Color.FromArgb(60, OpsPalette.Accent);
            b.ColorMouseDown = Color.FromArgb(110, OpsPalette.Accent);
            b.ColorNotEnabled = Color.FromArgb(150, OpsPalette.Surface);
            b.TextColorNotEnabled = OpsPalette.TextMuted;
        }

        public static void StyleQuickView(QuickView q)
        {
            q.BackColor = OpsPalette.Surface;
            q.ForeColor = OpsPalette.TextMuted;
            q.numberColor = OpsPalette.Text;
            q.numberColorBackup = OpsPalette.Text;
            if (q.Font == null || q.Font.FontFamily.Name != UiFamily)
                q.Font = new Font(UiFamily, q.Font != null ? q.Font.Size : 9f, FontStyle.Bold);
        }

        public static void StyleHud(HUD hud)
        {
            if (hud == null)
                return;
            hud.skyColor1 = Color.FromArgb(0x1C, 0x4E, 0x80);
            hud.skyColor2 = Color.FromArgb(0x4F, 0x86, 0xB8);
            hud.groundColor1 = Color.FromArgb(0x6B, 0x52, 0x32);
            hud.groundColor2 = Color.FromArgb(0x2E, 0x24, 0x16);
            hud.hudcolor = Color.FromArgb(0xE6, 0xEA, 0xF0);
            hud.BackColor = OpsPalette.Surface;
            hud.Invalidate();
        }

        /// <summary>
        /// TabControl headers cannot be recoloured through properties; switch the control to
        /// user painting and draw the header strip ourselves. Hit-testing stays native.
        /// </summary>
        public static void SkinTabControl(TabControl t)
        {
            object marker;
            if (SkinnedTabs.TryGetValue(t, out marker))
                return;
            SkinnedTabs.Add(t, new object());

            var setStyle = typeof(Control).GetMethod("SetStyle", BindingFlags.NonPublic | BindingFlags.Instance);
            if (setStyle == null)
                return;

            setStyle.Invoke(t, new object[]
            {
                ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true
            });
            t.Font = TabFont;
            t.Paint += PaintTabs;
            t.SelectedIndexChanged += (s, e) => t.Invalidate();
            t.Invalidate();
        }

        private static void PaintTabs(object sender, PaintEventArgs e)
        {
            var t = (TabControl)sender;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            g.Clear(OpsPalette.Surface);

            var strip = t.DisplayRectangle.Top - 2;
            using (var b = new SolidBrush(OpsPalette.SurfaceDeep))
                g.FillRectangle(b, 0, 0, t.Width, Math.Max(0, strip));

            for (int i = 0; i < t.TabCount; i++)
            {
                var r = t.GetTabRect(i);
                var selected = i == t.SelectedIndex;
                if (selected)
                {
                    using (var b = new SolidBrush(OpsPalette.Surface))
                        g.FillRectangle(b, r.X, r.Y, r.Width, r.Height + 2);
                    using (var b = new SolidBrush(OpsPalette.Accent))
                        g.FillRectangle(b, r.X, r.Y, r.Width, 2);
                }

                TextRenderer.DrawText(g, t.TabPages[i].Text, t.Font, r,
                    selected ? Color.White : OpsPalette.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }

            using (var p = new Pen(OpsPalette.Border))
                g.DrawLine(p, 0, strip, t.Width, strip);
        }
    }
}
