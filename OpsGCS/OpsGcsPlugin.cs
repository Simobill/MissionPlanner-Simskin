using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using log4net;
using MissionPlanner.Controls;
using MissionPlanner.GCSViews;
using MissionPlanner.OpsGCS.Core;
using MissionPlanner.OpsGCS.UI;
using MissionPlanner.Utilities;

namespace MissionPlanner.OpsGCS
{
    /// <summary>
    /// Operational GCS layer. Compiled into MissionPlanner.exe and picked up by PluginLoader's
    /// "self" scan, so the upstream sources need only one small hook (MainV2.ShortcutFilter).
    ///
    /// Responsibilities:
    ///  - role model (Operator / Engineer) mapped onto upstream DisplayView flags
    ///  - menu, shortcut and map-context-menu restriction for operators
    ///  - health strip (top of map), mission strip (bottom of map), OPS tab (fleet + health)
    ///  - product branding of the main window
    /// </summary>
    public class OpsGcsPlugin : MissionPlanner.Plugin.Plugin
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const string OpsTabName = "tabOpsGcs";

        private OpsConfig _cfg = new OpsConfig();
        private OpsRole _role = OpsRole.Operator;

        private HealthStrip _healthStrip;
        private MissionStrip _missionStrip;
        private OpsTabPanel _opsPanel;
        private TabPage _opsTab;
        private bool _uiReady;
        private bool _settingTitle;
        private int _busy;

        // MainV2 shortcuts an operator must not reach (dev/test dialogs, param writes, tuning).
        private static readonly HashSet<Keys> OperatorBlockedKeys = new HashSet<Keys>
        {
            Keys.F4,
            Keys.Control | Keys.F, Keys.Control | Keys.P, Keys.Control | Keys.G,
            Keys.Control | Keys.X, Keys.Control | Keys.L, Keys.Control | Keys.W,
            Keys.Control | Keys.Z, Keys.Control | Keys.T, Keys.Control | Keys.Y,
            Keys.Control | Keys.J
        };

        private static readonly Keys RoleToggleKey = Keys.Control | Keys.Shift | Keys.E;

        public override string Name
        {
            get { return "OpsGCS"; }
        }

        public override string Version
        {
            get { return "0.1.0"; }
        }

        public override string Author
        {
            get { return "OpsGCS"; }
        }

        public OpsRole Role
        {
            get { return _role; }
        }

        public override bool Init()
        {
            _cfg = OpsConfig.Load(Settings.GetRunningDirectory());
            _role = _cfg.StartAsEngineer ? OpsRole.Engineer : OpsRole.Operator;
            loopratehz = 4;
            return true;
        }

        public override bool Loaded()
        {
            MainV2.instance.BeginInvokeIfRequired(() =>
            {
                try
                {
                    SetupUi();
                }
                catch (Exception ex)
                {
                    // never take the GCS down because of the overlay
                    log.Error("OpsGCS UI setup failed", ex);
                }
            });
            return true;
        }

        public override bool Exit()
        {
            MainV2.ShortcutFilter = null;
            MainV2.LayoutChanged -= OnLayoutChanged;
            return true;
        }

        // ------------------------------------------------------------------ setup

        private void SetupUi()
        {
            var main = MainV2.instance;
            var fd = FlightData.instance;
            if (main == null || fd == null)
                return;

            // full-width status bar above and mission bar below the whole Flight Data view.
            // Not inside the map panel: upstream overlays (distance bar, wind, legend) are
            // absolutely positioned there and would paint over the strips.
            _healthStrip = new HealthStrip { Dock = DockStyle.Top };
            _missionStrip = new MissionStrip { Dock = DockStyle.Bottom };
            fd.Controls.Add(_healthStrip);
            fd.Controls.Add(_missionStrip);
            // docked edges must sit behind the Fill-docked MainH in z-order to claim their space first
            _healthStrip.SendToBack();
            _missionStrip.SendToBack();

            // OPS tab
            _opsPanel = new OpsTabPanel(_cfg.ShowFleet);
            _opsPanel.Fleet.VehicleSelected += SelectVehicle;
            _opsPanel.RoleButtonClicked += (s, e) => ToggleRole();
            _opsTab = new TabPage("OPS") { Name = OpsTabName, BackColor = OpsPalette.Surface };
            _opsTab.Controls.Add(_opsPanel);
            if (!fd.TabListOriginal.Contains(_opsTab))
                fd.TabListOriginal.Insert(0, _opsTab);
            RegisterOpsTabSetting(fd);
            // FlightData_Load rebuilds the tab list after plugins load, without raising LayoutChanged
            fd.tabControlactions.ControlRemoved += (s, e) =>
            {
                // defer: this fires inside TabPages.Clear(); re-adding there would fight the rebuild
                if (e.Control == _opsTab && fd.IsHandleCreated)
                    fd.BeginInvoke((Action)(() => EnsureOpsTab(false)));
            };

            // map right-click restriction
            fd.contextMenuStripMap.Opening += (s, e) => FilterMapMenu(fd.contextMenuStripMap);

            // title branding
            main.TextChanged += (s, e) => Brand();

            MainV2.ShortcutFilter = FilterShortcut;
            MainV2.LayoutChanged += OnLayoutChanged;

            ApplyOpsThemeOnce(main);

            _uiReady = true;
            ApplyRole(_role);
            Brand();
            EnsureOpsTab(true);
        }

        /// <summary>
        /// Upstream rebuilds the Flight Data tabs from the "tabcontrolactions" setting; list the
        /// OPS tab first there so the rebuild keeps it.
        /// </summary>
        private void RegisterOpsTabSetting(FlightData fd)
        {
            var tabs = Settings.Instance["tabcontrolactions"];
            if (string.IsNullOrEmpty(tabs))
            {
                // upstream does not rebuild while the setting is empty; seed it with the current order
                tabs = string.Join(";", fd.TabListOriginal.Select(t => t.Name)) + ";";
            }

            var names = tabs.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (names.Count > 0 && names[0] == OpsTabName)
                return;

            names.Remove(OpsTabName);
            names.Insert(0, OpsTabName);
            Settings.Instance["tabcontrolactions"] = string.Join(";", names) + ";";
        }

        /// <summary>
        /// Upstream picks custom.mpsystheme only for a profile with no saved theme. Switch an
        /// existing profile once; after that the user's theme choice (engineer mode) is respected.
        /// </summary>
        private static void ApplyOpsThemeOnce(MainV2 main)
        {
            const string flag = "opsgcs_theme_v1";
            if (Settings.Instance[flag] != null)
                return;

            try
            {
                if (File.Exists(Path.Combine(Settings.GetRunningDirectory(), "custom.mpsystheme")))
                {
                    ThemeManager.LoadTheme("custom.mpsystheme");
                    ThemeManager.ApplyThemeTo(main);
                }
            }
            catch (Exception ex)
            {
                log.Warn("OpsGCS theme apply failed", ex);
            }

            Settings.Instance[flag] = "1";
        }

        // ------------------------------------------------------------------ roles

        private void ApplyRole(OpsRole role)
        {
            _role = role;
            log.Info("OpsGCS role " + role);

            // setting DisplayConfiguration persists it and fires LayoutChanged -> OnLayoutChanged
            MainV2.DisplayConfiguration = OpsDisplayViews.For(role);
            _opsPanel.SetRole(_cfg.ProductName, role, _cfg.EngineerModeAvailable);
        }

        private void ToggleRole()
        {
            if (_role == OpsRole.Engineer)
            {
                ApplyRole(OpsRole.Operator);
                return;
            }

            if (!_cfg.EngineerModeAvailable)
                return;

            string pin = "";
            if (InputBox.Show("Engineer mode", "Engineer PIN:", ref pin, true) != DialogResult.OK)
                return;

            if (_cfg.CheckPin(pin))
            {
                ApplyRole(OpsRole.Engineer);
            }
            else
            {
                log.Warn("OpsGCS engineer PIN rejected");
                MissionPlanner.MsgBox.CustomMessageBox.Show("Incorrect PIN.", "Engineer mode");
            }
        }

        private void OnLayoutChanged(object sender, EventArgs e)
        {
            if (!_uiReady)
                return;

            var main = MainV2.instance;
            var engineer = _role == OpsRole.Engineer;

            // upstream updateLayout has already run; layer role restrictions on top
            main.MenuInitConfig.Visible = engineer;
            main.MenuConfigTune.Visible = engineer;
            main.MenuSimulation.Visible = engineer && MainV2.DisplayConfiguration.displaySimulation;
            main.MenuHelp.Visible = engineer && MainV2.DisplayConfiguration.displayHelp;
            if (main.MenuArduPilot != null)
                main.MenuArduPilot.Visible = engineer;

            EnsureOpsTab(false);
        }

        private void EnsureOpsTab(bool select)
        {
            if (_opsTab == null || FlightData.instance == null)
                return;

            var tabs = FlightData.instance.tabControlactions;
            if (!tabs.TabPages.Contains(_opsTab))
                tabs.TabPages.Insert(0, _opsTab);
            if (select)
                tabs.SelectedTab = _opsTab;
        }

        private bool FilterShortcut(Keys key)
        {
            if (key == RoleToggleKey)
            {
                MainV2.instance.BeginInvokeIfRequired(ToggleRole);
                return true;
            }

            return _role == OpsRole.Operator && OperatorBlockedKeys.Contains(key);
        }

        private readonly HashSet<ToolStripItem> _hiddenMapItems = new HashSet<ToolStripItem>();

        private void FilterMapMenu(ContextMenuStrip menu)
        {
            if (_role != OpsRole.Operator)
            {
                // engineer: undo only what this plugin hid, leave upstream's own visibility logic alone
                foreach (var item in _hiddenMapItems)
                    item.Visible = true;
                _hiddenMapItems.Clear();
                return;
            }

            foreach (ToolStripItem item in menu.Items)
            {
                var keep = !(item is ToolStripSeparator) && _cfg.OperatorMapMenuAllowlist.Contains(item.Name);
                if (!keep && item.Visible)
                {
                    item.Visible = false;
                    _hiddenMapItems.Add(item);
                }
            }
        }

        private void Brand()
        {
            if (_settingTitle)
                return;

            var main = MainV2.instance;
            var product = string.IsNullOrWhiteSpace(_cfg.Unit) ? _cfg.ProductName : _cfg.ProductName + " - " + _cfg.Unit;
            var text = main.Text ?? "";
            if (text.StartsWith(product, StringComparison.Ordinal))
                return;

            // upstream titles are "Mission Planner <build>[ <fw version> on <port>]": swap the product name,
            // keep the build and link details for traceability
            const string upstreamName = "Mission Planner";
            var rest = text.StartsWith(upstreamName, StringComparison.Ordinal) ? text.Substring(upstreamName.Length) : " " + text;
            var newTitle = (product + rest).TrimEnd();

            try
            {
                _settingTitle = true;
                main.Text = newTitle;
            }
            finally
            {
                _settingTitle = false;
            }
        }

        // ------------------------------------------------------------------ fleet

        private static readonly HashSet<MAVLink.MAV_TYPE> VehicleTypes = new HashSet<MAVLink.MAV_TYPE>
        {
            MAVLink.MAV_TYPE.FIXED_WING, MAVLink.MAV_TYPE.QUADROTOR, MAVLink.MAV_TYPE.COAXIAL,
            MAVLink.MAV_TYPE.HELICOPTER, MAVLink.MAV_TYPE.GROUND_ROVER, MAVLink.MAV_TYPE.SURFACE_BOAT,
            MAVLink.MAV_TYPE.SUBMARINE, MAVLink.MAV_TYPE.HEXAROTOR, MAVLink.MAV_TYPE.OCTOROTOR,
            MAVLink.MAV_TYPE.TRICOPTER, MAVLink.MAV_TYPE.VTOL_DUOROTOR, MAVLink.MAV_TYPE.VTOL_QUADROTOR,
            MAVLink.MAV_TYPE.VTOL_TILTROTOR, MAVLink.MAV_TYPE.VTOL_RESERVED2, MAVLink.MAV_TYPE.VTOL_RESERVED3,
            MAVLink.MAV_TYPE.VTOL_RESERVED4, MAVLink.MAV_TYPE.VTOL_RESERVED5, MAVLink.MAV_TYPE.DODECAROTOR,
            MAVLink.MAV_TYPE.DECAROTOR, MAVLink.MAV_TYPE.GENERIC_MULTIROTOR, MAVLink.MAV_TYPE.AIRSHIP
        };

        public static string ShortType(MAVLink.MAV_TYPE t)
        {
            switch (t)
            {
                case MAVLink.MAV_TYPE.FIXED_WING: return "FW";
                case MAVLink.MAV_TYPE.GROUND_ROVER: return "UGV";
                case MAVLink.MAV_TYPE.SURFACE_BOAT: return "USV";
                case MAVLink.MAV_TYPE.SUBMARINE: return "UUV";
                case MAVLink.MAV_TYPE.HELICOPTER: return "HELI";
                case MAVLink.MAV_TYPE.AIRSHIP: return "LTA";
                case MAVLink.MAV_TYPE.VTOL_DUOROTOR:
                case MAVLink.MAV_TYPE.VTOL_QUADROTOR:
                case MAVLink.MAV_TYPE.VTOL_TILTROTOR:
                case MAVLink.MAV_TYPE.VTOL_RESERVED2:
                case MAVLink.MAV_TYPE.VTOL_RESERVED3:
                case MAVLink.MAV_TYPE.VTOL_RESERVED4:
                case MAVLink.MAV_TYPE.VTOL_RESERVED5:
                    return "VTOL";
                default: return "MR";
            }
        }

        private static List<MAVLinkInterface> OpenPorts()
        {
            var ports = new List<MAVLinkInterface>();
            if (MainV2.comPort != null)
                ports.Add(MainV2.comPort);
            foreach (var p in MainV2.Comports.ToArray())
                if (p != null && !ports.Contains(p))
                    ports.Add(p);
            return ports;
        }

        private List<FleetRow> BuildRows()
        {
            var rows = new List<FleetRow>();
            var active = MainV2.comPort;

            foreach (var port in OpenPorts())
            {
                MAVState[] mavs;
                try
                {
                    mavs = port.MAVlist.ToArray();
                }
                catch
                {
                    continue; // list mutated while enumerating; catch it next tick
                }

                foreach (var mav in mavs)
                {
                    if (mav == null || !VehicleTypes.Contains(mav.aptype))
                        continue;

                    var snap = SnapshotBuilder.From(port, mav);
                    rows.Add(new FleetRow
                    {
                        Port = port,
                        Mav = mav,
                        Name = ShortType(mav.aptype) + "-" + mav.sysid,
                        Snapshot = snap,
                        Health = HealthEvaluator.Evaluate(snap, _cfg.Thresholds),
                        Active = port == active && mav.sysid == active.sysidcurrent && mav.compid == active.compidcurrent
                    });
                }
            }

            return rows.OrderBy(r => r.Mav.sysid).ToList();
        }

        private void SelectVehicle(FleetRow row)
        {
            if (row == null || row.Port == null || row.Mav == null)
                return;

            try
            {
                MainV2.comPort = row.Port;
                row.Port.sysidcurrent = row.Mav.sysid;
                row.Port.compidcurrent = row.Mav.compid;
            }
            catch (Exception ex)
            {
                log.Error("OpsGCS vehicle select failed", ex);
            }
        }

        // ------------------------------------------------------------------ update loop (plugin thread)

        public override bool Loop()
        {
            if (!_uiReady)
                return true;

            // drop the tick if the UI thread hasn't consumed the previous one
            if (System.Threading.Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
                return true;

            List<FleetRow> rows;
            VehicleSnapshot activeSnap;
            List<HealthItem> activeHealth;
            string activeName;

            try
            {
                rows = BuildRows();
                var activeRow = rows.FirstOrDefault(r => r.Active);
                if (activeRow != null)
                {
                    activeSnap = activeRow.Snapshot;
                    activeHealth = activeRow.Health;
                    activeName = activeRow.Name;
                }
                else
                {
                    activeSnap = SnapshotBuilder.From(MainV2.comPort, MainV2.comPort.MAV);
                    activeHealth = HealthEvaluator.Evaluate(activeSnap, _cfg.Thresholds);
                    activeName = activeSnap.Connected ? "SYS-" + MainV2.comPort.sysidcurrent : "NO VEHICLE";
                }
            }
            catch (Exception ex)
            {
                log.Debug("OpsGCS snapshot failed", ex);
                System.Threading.Interlocked.Exchange(ref _busy, 0);
                return true;
            }

            MainV2.instance.BeginInvokeIfRequired(() =>
            {
                try
                {
                    _healthStrip.SetState(activeName, activeSnap, activeHealth);
                    _missionStrip.SetState(activeSnap);
                    _opsPanel.Fleet.SetRows(rows);
                    _opsPanel.SetDetail(activeHealth);
                }
                catch (Exception ex)
                {
                    log.Debug("OpsGCS UI update failed", ex);
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _busy, 0);
                }
            });

            return true;
        }
    }
}
