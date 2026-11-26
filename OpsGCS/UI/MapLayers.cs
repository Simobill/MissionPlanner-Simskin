using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using GMap.NET;
using GMap.NET.WindowsForms;
using MissionPlanner.OpsGCS.Core;

namespace MissionPlanner.OpsGCS.UI
{
    /// <summary>Draws a geo-referenced bitmap stretched over a lat/lon rectangle.</summary>
    public class GMapMarkerRaster : GMapMarker
    {
        private readonly Bitmap _image;
        private readonly RectLatLng _rect;

        public GMapMarkerRaster(Bitmap image, double north, double south, double west, double east)
            : base(new PointLatLng((north + south) / 2, (west + east) / 2))
        {
            _image = image;
            _rect = RectLatLng.FromLTRB(west, north, east, south);
            IsHitTestVisible = false;
        }

        public override void OnRender(IGraphics g)
        {
            if (_image == null || Overlay == null || Overlay.Control == null)
                return;

            var tl = Overlay.Control.FromLatLngToLocal(_rect.LocationTopLeft);
            var br = Overlay.Control.FromLatLngToLocal(_rect.LocationRightBottom);

            var old = g.Transform;
            var oldInterp = g.InterpolationMode;
            var oldOffset = g.PixelOffsetMode;
            g.ResetTransform();
            g.CompositingMode = CompositingMode.SourceOver;
            g.InterpolationMode = InterpolationMode.NearestNeighbor; // keep cell edges and hatch crisp
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_image, (float)tl.X, (float)tl.Y, (float)(br.X - tl.X), (float)(br.Y - tl.Y));
            g.InterpolationMode = oldInterp;
            g.PixelOffsetMode = oldOffset;
            g.Transform = old;
        }

        public override void Dispose()
        {
            if (_image != null)
                _image.Dispose();
            base.Dispose();
        }
    }

    /// <summary>
    /// Model precipitation as translucent cells (violet, so it never reads as a health colour)
    /// and a wind barb at each grid point.
    /// </summary>
    public class GMapMarkerWeather : GMapMarker
    {
        private readonly List<WeatherPoint> _grid;
        private readonly double _dLat, _dLon;
        private static readonly Pen BarbPen = new Pen(Color.FromArgb(235, 0xE6, 0xEA, 0xF0), 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        private static readonly Pen BarbShadow = new Pen(Color.FromArgb(160, 5, 8, 12), 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        private static readonly Brush BarbFill = new SolidBrush(Color.FromArgb(235, 0xE6, 0xEA, 0xF0));
        private static readonly Font LabelFont = new Font("Segoe UI", 8f, FontStyle.Bold);

        public GMapMarkerWeather(WeatherSnapshot snap)
            : base(new PointLatLng(snap.AtVehicle != null ? snap.AtVehicle.Lat : 0, snap.AtVehicle != null ? snap.AtVehicle.Lon : 0))
        {
            _grid = snap.Grid ?? new List<WeatherPoint>();
            _dLat = snap.GridCellDegLat;
            _dLon = snap.GridCellDegLon;
            IsHitTestVisible = false;
        }

        public static int PrecipAlpha(double mm)
        {
            if (mm < 0.1) return 0;
            if (mm < 0.5) return 45;
            if (mm < 2) return 80;
            if (mm < 5) return 115;
            return 150;
        }

        public override void OnRender(IGraphics g)
        {
            if (Overlay == null || Overlay.Control == null || _grid.Count == 0)
                return;

            var old = g.Transform;
            g.ResetTransform();
            var oldSmooth = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            foreach (var p in _grid)
            {
                var a = PrecipAlpha(p.PrecipMm ?? 0);
                if (a == 0)
                    continue;
                var nw = Overlay.Control.FromLatLngToLocal(new PointLatLng(p.Lat + _dLat / 2, p.Lon - _dLon / 2));
                var se = Overlay.Control.FromLatLngToLocal(new PointLatLng(p.Lat - _dLat / 2, p.Lon + _dLon / 2));
                using (var b = new SolidBrush(Color.FromArgb(a, 0x9B, 0x7B, 0xDE)))
                    g.FillPolygon(b, new[]
                    {
                        new PointF(nw.X, nw.Y), new PointF(se.X, nw.Y), new PointF(se.X, se.Y), new PointF(nw.X, se.Y)
                    });
            }

            foreach (var p in _grid)
            {
                if (!p.WindKt.HasValue || !p.WindDirDeg.HasValue)
                    continue;
                var c = Overlay.Control.FromLatLngToLocal(new PointLatLng(p.Lat, p.Lon));
                DrawBarb(g, c.X, c.Y, p.WindDirDeg.Value, p.WindKt.Value);
            }

            g.SmoothingMode = oldSmooth;
            g.Transform = old;
        }

        /// <summary>Staff points to where the wind comes from; feathers on the clockwise side.</summary>
        private static void DrawBarb(IGraphics g, float x, float y, double fromDeg, double kt)
        {
            int pennants, full, half;
            WindBarb.Glyph(kt, out pennants, out full, out half);

            if (pennants + full + half == 0)
            {
                // calm: open circle
                g.DrawEllipse(BarbShadow, x - 5, y - 5, 10, 10);
                g.DrawEllipse(BarbPen, x - 5, y - 5, 10, 10);
                return;
            }

            const float staff = 30f, featherLen = 12f, spacing = 5f;
            var rad = fromDeg * Math.PI / 180.0;
            var dx = (float)Math.Sin(rad);
            var dy = (float)-Math.Cos(rad); // screen y grows downward
            var px = -dy; // clockwise perpendicular
            var py = dx;

            var tipX = x + dx * staff;
            var tipY = y + dy * staff;

            var lines = new List<PointF[]>();
            lines.Add(new[] { new PointF(x, y), new PointF(tipX, tipY) });

            var pos = 0f;
            var polys = new List<PointF[]>();
            for (int i = 0; i < pennants; i++)
            {
                var b0 = new PointF(tipX - dx * pos, tipY - dy * pos);
                var b1 = new PointF(tipX - dx * (pos + spacing * 1.6f), tipY - dy * (pos + spacing * 1.6f));
                var apex = new PointF(b0.X + px * featherLen, b0.Y + py * featherLen);
                polys.Add(new[] { b0, apex, b1 });
                pos += spacing * 2f;
            }

            for (int i = 0; i < full; i++)
            {
                var b0 = new PointF(tipX - dx * pos, tipY - dy * pos);
                lines.Add(new[] { b0, new PointF(b0.X + px * featherLen + dx * 4, b0.Y + py * featherLen + dy * 4) });
                pos += spacing;
            }

            if (half == 1)
            {
                if (pennants + full == 0)
                    pos += spacing; // a lone half barb sits off the tip
                var b0 = new PointF(tipX - dx * pos, tipY - dy * pos);
                lines.Add(new[] { b0, new PointF(b0.X + px * featherLen / 2 + dx * 2, b0.Y + py * featherLen / 2 + dy * 2) });
            }

            foreach (var l in lines)
                g.DrawLine(BarbShadow, l[0].X, l[0].Y, l[1].X, l[1].Y);
            foreach (var poly in polys)
                g.FillPolygon(BarbShadow.Brush, poly);
            foreach (var l in lines)
                g.DrawLine(BarbPen, l[0].X, l[0].Y, l[1].X, l[1].Y);
            foreach (var poly in polys)
                g.FillPolygon(BarbFill, poly);

            g.FillEllipse(BarbFill, x - 2.5f, y - 2.5f, 5, 5);
        }
    }

    /// <summary>
    /// Owns the OpsGCS overlay on the Flight Data map. All methods must run on the UI thread.
    /// </summary>
    public class MapLayers
    {
        private readonly GMapControl _map;
        private readonly GMapOverlay _viewshed = new GMapOverlay("opsgcs_viewshed");
        private readonly GMapOverlay _weather = new GMapOverlay("opsgcs_weather");

        public MapLayers(GMapControl map)
        {
            _map = map;
            // weather under the viewshed, both under upstream's vehicle/route overlays added later at the top
            _map.Overlays.Insert(0, _weather);
            _map.Overlays.Insert(1, _viewshed);
        }

        public bool HasViewshed
        {
            get { return _viewshed.Markers.Count > 0; }
        }

        public void SetViewshed(Bitmap image, ViewshedResult r)
        {
            ClearMarkers(_viewshed);
            if (image != null && r != null)
                _viewshed.Markers.Add(new GMapMarkerRaster(image, r.North, r.South, r.West, r.East));
            _map.Invalidate();
        }

        public void ClearViewshed()
        {
            if (_viewshed.Markers.Count == 0)
                return;
            ClearMarkers(_viewshed);
            _map.Invalidate();
        }

        public void SetWeather(WeatherSnapshot snap)
        {
            ClearMarkers(_weather);
            if (snap != null && snap.Grid != null && snap.Grid.Count > 0)
                _weather.Markers.Add(new GMapMarkerWeather(snap));
            _map.Invalidate();
        }

        public void ClearWeather()
        {
            if (_weather.Markers.Count == 0)
                return;
            ClearMarkers(_weather);
            _map.Invalidate();
        }

        private static void ClearMarkers(GMapOverlay o)
        {
            var old = new List<GMapMarker>(o.Markers);
            o.Markers.Clear();
            foreach (var m in old)
                m.Dispose();
        }
    }
}
