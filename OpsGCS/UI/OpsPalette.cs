using System.Drawing;
using MissionPlanner.OpsGCS.Core;

namespace MissionPlanner.OpsGCS.UI
{
    /// <summary>
    /// Dark operational palette: low-luminance surfaces for night use, amber accent,
    /// status colours reserved for health state only.
    /// </summary>
    public static class OpsPalette
    {
        public static readonly Color Surface = Color.FromArgb(0x10, 0x16, 0x1F);
        public static readonly Color SurfaceRaised = Color.FromArgb(0x18, 0x21, 0x2D);
        public static readonly Color Border = Color.FromArgb(0x2A, 0x36, 0x46);
        public static readonly Color Text = Color.FromArgb(0xD8, 0xDE, 0xE6);
        public static readonly Color TextMuted = Color.FromArgb(0x8A, 0x96, 0xA6);
        public static readonly Color Accent = Color.FromArgb(0xE0, 0xA8, 0x32);

        public static readonly Color Ok = Color.FromArgb(0x3F, 0xB9, 0x6E);
        public static readonly Color Caution = Color.FromArgb(0xE8, 0xB3, 0x2E);
        public static readonly Color Warning = Color.FromArgb(0xE5, 0x48, 0x4D);
        public static readonly Color Unknown = Color.FromArgb(0x5A, 0x66, 0x76);

        public static Color For(HealthLevel level)
        {
            switch (level)
            {
                case HealthLevel.Ok: return Ok;
                case HealthLevel.Caution: return Caution;
                case HealthLevel.Warning: return Warning;
                default: return Unknown;
            }
        }

        public static Font Label(float size = 8f)
        {
            return new Font("Segoe UI", size, FontStyle.Bold);
        }

        public static Font Value(float size = 11f)
        {
            return new Font("Consolas", size, FontStyle.Bold);
        }
    }
}
