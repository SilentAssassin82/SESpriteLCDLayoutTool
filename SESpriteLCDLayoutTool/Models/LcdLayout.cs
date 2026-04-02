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

        /// <summary>
        /// When a layout is imported from pasted code, the full original source is stored here.
        /// Code generation can then splice updated sprite definitions back into the original,
        /// so the user can paste directly back into their project.
        /// Null when the layout was created from scratch or loaded from a .seld file.
        /// </summary>
        [System.Xml.Serialization.XmlIgnore]
        public string OriginalSourceCode { get; set; }
    }
}
