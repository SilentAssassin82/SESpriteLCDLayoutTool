using System;
using System.Collections.Generic;

namespace SESpriteLCDLayoutTool.Models
{
    [Serializable]
    public class LcdLayout
    {
        public string Name { get; set; } = "New Layout";
        public int SurfaceWidth { get; set; } = 512;
        public int SurfaceHeight { get; set; } = 512;
        public List<SpriteEntry> Sprites { get; set; } = new List<SpriteEntry>();
    }
}
