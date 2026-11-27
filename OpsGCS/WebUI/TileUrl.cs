using System;

namespace MissionPlanner.OpsGCS.WebUI
{
    /// <summary>Tile request parsing for the web map (https://{host}/{z}/{x}/{y}); pure for testing.</summary>
    public static class TileUrl
    {
        public static bool TryParse(string uri, string host, out int z, out int x, out int y)
        {
            z = x = y = 0;
            Uri u;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out u) || !string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase))
                return false;
            var parts = u.AbsolutePath.Trim('/').Split('/');
            return parts.Length == 3 && int.TryParse(parts[0], out z) && int.TryParse(parts[1], out x) &&
                   int.TryParse(parts[2], out y) && z >= 0 && z <= 23 && x >= 0 && y >= 0 &&
                   x < (1 << z) && y < (1 << z);
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
