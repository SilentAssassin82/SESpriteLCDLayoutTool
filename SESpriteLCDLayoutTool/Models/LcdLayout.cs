using System;
using System.Collections.Generic;
using System.Xml.Serialization;

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
        /// Optional 2D bone rigs attached to this layout. Each rig binds a subset of sprites
        /// to a bone hierarchy so they can be posed/animated together. Persisted with the layout
        /// so rigs survive save/load.
        /// </summary>
        public List<Rig.Rig> Rigs { get; set; } = new List<Rig.Rig>();

        /// <summary>
        /// When a layout is imported from pasted code, the full original source is stored here.
        /// Code generation can then splice updated sprite definitions back into the original,
        /// so the user can paste directly back into their project.
        /// Persisted in .seld files so animation/script data survives save/load.
        /// Null when the layout was created from scratch.
        /// </summary>
        public string OriginalSourceCode { get; set; }

        /// <summary>
        /// When true, the layout contains sprites from a Pulsar plugin or Mod script
        /// execution. These sprites have no source tracking (SourceStart = -1) because
        /// they come from runtime DrawFrame() calls. Code regeneration should be
        /// skipped to preserve the original source code in the code panel.
        /// </summary>
        public bool IsPulsarOrModLayout { get; set; }

        /// <summary>
        /// Runtime snapshot data captured from Torch plugin (!lcd snapshot file)
        /// or live capture pipe. Used to replay render methods with real game values
        /// instead of placeholder text.
        /// </summary>
        public List<SnapshotRowData> CapturedRows { get; set; }

        /// <summary>
        /// Runtime mapping of element/method names to the sprites they produce.
        /// Built by running N frames of animation and collecting all unique sprites.
        /// Used for accurate Execute &amp; Isolate filtering.
        /// Not serialized - rebuilt when needed or loaded from .sprmap file.
        /// </summary>
        [NonSerialized]
        [XmlIgnore]
        public ElementSpriteMapping SpriteMapping;
    }
}
