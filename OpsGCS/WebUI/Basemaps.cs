using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using GMap.NET;
using GMap.NET.MapProviders;
using GMap.NET.Projections;

namespace MissionPlanner.OpsGCS.WebUI
{
    /// <summary>
    /// Basemaps the web map offers. Each id maps to a GMap.NET provider, so tiles go through
    /// Mission Planner's tile cache and cached areas still show offline. The page holds the labels.
    /// </summary>
    public static class Basemaps
    {
        private static readonly Dictionary<string, Func<GMapProvider>> Catalogue =
            new Dictionary<string, Func<GMapProvider>>(StringComparer.OrdinalIgnoreCase)
            {
                // "mp" (Mission Planner's current provider) is resolved by the caller
                ["osm"] = () => GMapProviders.OpenStreetMap,
                ["topo"] = () => OpenTopoMapProvider.Instance,
                ["esri-topo"] = () => GMapProviders.ArcGIS_World_Topo_Map,
                ["esri-img"] = () => EsriWorldImageryProvider.Instance,
                ["google-sat"] = () => GMapProviders.GoogleSatelliteMap,
                ["google-hyb"] = () => GMapProviders.GoogleHybridMap,
                ["google-ter"] = () => GMapProviders.GoogleTerrainMap,
                ["bing-hyb"] = () => GMapProviders.BingHybridMap
            };

        public static bool IsKnown(string id)
        {
            return string.Equals(id, "mp", StringComparison.OrdinalIgnoreCase) || (id != null && Catalogue.ContainsKey(id));
        }

        /// <summary>The provider for a basemap id; "mp" or an unknown id gives the current one.</summary>
        public static GMapProvider Resolve(string id, GMapProvider current)
        {
            Func<GMapProvider> f;
            if (id != null && Catalogue.TryGetValue(id, out f))
            {
                try
                {
                    return f() ?? current;
                }
                catch
                {
                    return current;
                }
            }

            return current;
        }
    }

    /// <summary>Plain XYZ tile provider registered with GMap.NET so its tiles share the cache.</summary>
    public abstract class OpsXyzProvider : GMapProvider
    {
        protected static void Register(GMapProvider p)
        {
            // same registration the providers in ExtLibs/Maps use, so the cache can find the provider by DbId
            var field = typeof(GMapProviders).GetField("DbHash", BindingFlags.Static | BindingFlags.NonPublic);
            var list = field != null ? field.GetValue(null) as Dictionary<int, GMapProvider> : null;
            if (list != null && !list.ContainsKey(p.DbId))
                list.Add(p.DbId, p);
        }

        protected abstract string Template { get; }

        protected virtual string Referer
        {
            get { return null; }
        }

        private GMapProvider[] _overlays;

        public override GMapProvider[] Overlays
        {
            get { return _overlays ?? (_overlays = new GMapProvider[] { this }); }
        }

        public override PureProjection Projection
        {
            get { return MercatorProjection.Instance; }
        }

        public override PureImage GetTileImage(GPoint pos, int zoom)
        {
            if (Referer != null)
                RefererUrl = Referer;
            var url = Template
                .Replace("{s}", "abc"[(int)((pos.X + pos.Y) % 3)].ToString())
                .Replace("{z}", zoom.ToString(CultureInfo.InvariantCulture))
                .Replace("{x}", pos.X.ToString(CultureInfo.InvariantCulture))
                .Replace("{y}", pos.Y.ToString(CultureInfo.InvariantCulture));
            return GetTileImageUsingHttp(url);
        }
    }

    /// <summary>OpenTopoMap: contours and hill shading from SRTM over OpenStreetMap data (CC-BY-SA).</summary>
    public sealed class OpenTopoMapProvider : OpsXyzProvider
    {
        public static readonly OpenTopoMapProvider Instance;

        private readonly Guid _id = new Guid("6c1d3b52-0f7e-4a5f-9a43-0b9f2e8d7a11");

        static OpenTopoMapProvider()
        {
            Instance = new OpenTopoMapProvider();
            Register(Instance);
        }

        private OpenTopoMapProvider()
        {
            MaxZoom = 17;
        }

        public override Guid Id
        {
            get { return _id; }
        }

        public override string Name
        {
            get { return "OpsGCS_OpenTopoMap"; }
        }

        protected override string Template
        {
            get { return "https://{s}.tile.opentopomap.org/{z}/{x}/{y}.png"; }
        }

        protected override string Referer
        {
            get { return "https://opentopomap.org/"; }
        }
    }

    /// <summary>Esri World Imagery (the current service; the GMap.NET ArcGIS imagery provider points at a retired one).</summary>
    public sealed class EsriWorldImageryProvider : OpsXyzProvider
    {
        public static readonly EsriWorldImageryProvider Instance;

        private readonly Guid _id = new Guid("b8e7c4f0-3d2a-4e61-8c5b-7a19d0f64e22");

        static EsriWorldImageryProvider()
        {
            Instance = new EsriWorldImageryProvider();
            Register(Instance);
        }

        private EsriWorldImageryProvider()
        {
            MaxZoom = 19;
        }

        public override Guid Id
        {
            get { return _id; }
        }

        public override string Name
        {
            get { return "OpsGCS_EsriWorldImagery"; }
        }

        protected override string Template
        {
            get { return "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}"; }
        }
    }
}
