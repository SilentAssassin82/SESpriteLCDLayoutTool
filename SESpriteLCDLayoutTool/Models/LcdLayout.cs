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
        /// Persisted in .seld files so animation/script data survives save/load.
        /// Null when the layout was created from scratch.
        /// </summary>
        public string OriginalSourceCode { get; set; }

        /// <summary>
        /// Runtime snapshot data captured from Torch plugin (!lcd snapshot file)
        /// or live capture pipe. Used to replay render methods with real game values
        /// instead of placeholder text.
        /// </summary>
        public List<SnapshotRowData> CapturedRows { get; set; }
    }
}
