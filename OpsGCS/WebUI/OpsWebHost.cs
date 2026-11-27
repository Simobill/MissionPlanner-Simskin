using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using GMap.NET;
using GMap.NET.MapProviders;
using log4net;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json.Linq;

namespace MissionPlanner.OpsGCS.WebUI
{
    /// <summary>
    /// Hosts the operator web UI (opsgcs-web/index.html) in WebView2 over the Flight Data view.
    /// Pages are served from a virtual host mapped to the install folder; map tiles are served from
    /// Mission Planner's own GMap provider and cache through a second virtual host.
    /// Messages: page -> host as JSON objects with "type"; host -> page "state" snapshots.
    /// </summary>
    public class OpsWebHost
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const string PageHost = "opsgcs.local";
        public const string TileHost = "tiles.opsgcs.local";

        private readonly Control _parent;
        private readonly Func<GMapProvider> _provider;
        private WebView2 _view;
        private bool _ready;

        /// <summary>Raised on the UI thread for every message from the page.</summary>
        public event Action<JObject> MessageReceived;

        /// <summary>Raised on the UI thread once, with null on success or the failure reason.</summary>
        public event Action<string> Initialized;

        private readonly bool _visualHosting;

        /// <summary>WebView2's documented switch for hosting mode; read once, when the environment is created.</summary>
        public const string HostingModeVariable = "COREWEBVIEW2_FORCED_HOSTING_MODE";
        public const string WindowToVisual = "COREWEBVIEW2_HOSTING_MODE_WINDOW_TO_VISUAL";

        public OpsWebHost(Control parent, Func<GMapProvider> provider, bool visualHosting = true)
        {
            _parent = parent;
            _provider = provider;
            _visualHosting = visualHosting;
        }

        /// <summary>
        /// Picks the hosting mode before the WebView2 environment exists. A value already set outside the
        /// process (for example by a technician testing) is left alone.
        /// </summary>
        private void ApplyHostingMode()
        {
            var preset = Environment.GetEnvironmentVariable(HostingModeVariable);
            if (!string.IsNullOrEmpty(preset))
            {
                log.Info("OpsGCS web: hosting mode preset outside OpsGCS: " + preset);
                return;
            }

            if (_visualHosting)
                Environment.SetEnvironmentVariable(HostingModeVariable, WindowToVisual);
            log.Info("OpsGCS web: hosting mode " + (_visualHosting ? "window-to-visual" : "windowed"));
        }

        public bool Ready
        {
            get { return _ready; }
        }

        public Control View
        {
            get { return _view; }
        }

        public static string WebFolder
        {
            get { return Path.Combine(MissionPlanner.Utilities.Settings.GetRunningDirectory(), "opsgcs-web"); }
        }

        public async void Start()
        {
            try
            {
                if (!File.Exists(Path.Combine(WebFolder, "index.html")))
                {
                    Fail("opsgcs-web/index.html missing from the install folder");
                    return;
                }

                _view = new WebView2 { Dock = DockStyle.Fill, Visible = false };
                _parent.Controls.Add(_view);
                _view.BringToFront();

                var dataDir = Path.Combine(MissionPlanner.Utilities.Settings.GetUserDataDirectory(), "opsgcs-webview2");
                ApplyHostingMode();
                var env = await CoreWebView2Environment.CreateAsync(null, dataDir, null);
                await _view.EnsureCoreWebView2Async(env);

                var core = _view.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreDevToolsEnabled = true; // F12 for diagnostics on test benches
                core.SetVirtualHostNameToFolderMapping(PageHost, WebFolder, CoreWebView2HostResourceAccessKind.Deny);
                core.AddWebResourceRequestedFilter("https://" + TileHost + "/*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += OnWebResourceRequested;
                core.WebMessageReceived += OnWebMessage;
                core.NavigationStarting += (s, e) =>
                {
                    // the page never navigates away; keep external links out of the GCS window
                    if (!e.Uri.StartsWith("https://" + PageHost + "/", StringComparison.OrdinalIgnoreCase))
                        e.Cancel = true;
                };
                core.NewWindowRequested += (s, e) => e.Handled = true;
                core.Navigate("https://" + PageHost + "/index.html");

                _ready = true;
                if (Initialized != null)
                    Initialized(null);
            }
            catch (Exception ex)
            {
                log.Error("OpsGCS web UI", ex);
                Fail(ex is WebView2RuntimeNotFoundException
                    ? "WebView2 runtime not installed"
                    : ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void Fail(string why)
        {
            _ready = false;
            if (_view != null)
                _view.Visible = false;
            if (Initialized != null)
                Initialized(why);
        }

        public void Show(bool visible)
        {
            if (_view == null || !_ready)
                return;
            _view.Visible = visible;
            if (visible)
                _view.BringToFront();
        }

        public bool Visible
        {
            get { return _view != null && _ready && _view.Visible; }
        }

        /// <summary>UI thread only.</summary>
        public void Post(JObject message)
        {
            if (!_ready || _view == null || _view.CoreWebView2 == null)
                return;
            try
            {
                _view.CoreWebView2.PostWebMessageAsJson(message.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (Exception ex)
            {
                log.Debug("OpsGCS web post", ex);
            }
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var msg = JObject.Parse(e.WebMessageAsJson);
                if (MessageReceived != null)
                    MessageReceived(msg);
            }
            catch (Exception ex)
            {
                log.Warn("OpsGCS web message", ex);
            }
        }

        // ------------------------------------------------------------------ map tiles

        private void OnWebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            int z, x, y;
            string src;
            if (!TileUrl.TryParse(e.Request.Uri, TileHost, out src, out z, out x, out y) || !Basemaps.IsKnown(src))
            {
                e.Response = _view.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }

            var deferral = e.GetDeferral();
            var provider = Basemaps.Resolve(src, _provider != null ? _provider() : null);
            Task.Run(() =>
            {
                byte[] data = null;
                try
                {
                    if (provider != null)
                    {
                        Exception err;
                        var img = GMaps.Instance.GetImageFrom(provider, new GPoint(x, y), z, out err);
                        if (img != null && img.Data != null)
                            data = img.Data.ToArray();
                    }
                }
                catch (Exception ex)
                {
                    log.Debug("OpsGCS tile", ex);
                }

                _parent.BeginInvoke((Action)(() =>
                {
                    try
                    {
                        var env = _view.CoreWebView2.Environment;
                        e.Response = data != null
                            ? env.CreateWebResourceResponse(new MemoryStream(data), 200, "OK",
                                "Content-Type: " + TileUrl.SniffImageType(data) + "\r\nCache-Control: max-age=86400")
                            : env.CreateWebResourceResponse(null, 404, "Not Found", "");
                    }
                    catch (Exception ex)
                    {
                        log.Debug("OpsGCS tile response", ex);
                    }
                    finally
                    {
                        deferral.Complete();
                    }
                }));
            });
        }
    }
}
