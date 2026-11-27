using System;

namespace MissionPlanner.OpsGCS.WebUI
{
    /// <summary>
    /// Tile request parsing for the web map: https://{host}/{basemap}/{z}/{x}/{y}, or the older
    /// https://{host}/{z}/{x}/{y} meaning basemap "mp" (Mission Planner's current provider). Pure for testing.
    /// </summary>
    public static class TileUrl
    {
        public static bool TryParse(string uri, string host, out int z, out int x, out int y)
        {
            string src;
            return TryParse(uri, host, out src, out z, out x, out y);
        }

        public static bool TryParse(string uri, string host, out string src, out int z, out int x, out int y)
        {
            z = x = y = 0;
            src = null;
            Uri u;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out u) || !string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase))
                return false;
            var parts = u.AbsolutePath.Trim('/').Split('/');
            int i = 0;
            if (parts.Length == 4)
            {
                if (!IsId(parts[0]))
                    return false;
                src = parts[0].ToLowerInvariant();
                i = 1;
            }
            else if (parts.Length == 3)
                src = "mp";
            else
                return false;

            return int.TryParse(parts[i], out z) && int.TryParse(parts[i + 1], out x) &&
                   int.TryParse(parts[i + 2], out y) && z >= 0 && z <= 23 && x >= 0 && y >= 0 &&
                   x < (1 << z) && y < (1 << z);
        }

        private static bool IsId(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 24)
                return false;
            foreach (var c in s)
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-'))
                    return false;
            return true;
        }

        public static string SniffImageType(byte[] d)
        {
            if (d == null) return "application/octet-stream";
            if (d.Length > 3 && d[0] == 0x89 && d[1] == 0x50) return "image/png";
            if (d.Length > 2 && d[0] == 0xFF && d[1] == 0xD8) return "image/jpeg";
            if (d.Length > 11 && d[8] == 0x57 && d[9] == 0x45 && d[10] == 0x42 && d[11] == 0x50) return "image/webp";
            return "application/octet-stream";
        }
    }
}
