using System;
using System.Collections.Generic;
using System.Drawing;
using System.Xml.Serialization;

namespace SESpriteLCDLayoutTool.Models.Rig
{
    /// <summary>
    /// A single bone in a 2D skeletal rig. Bones form a tree via <see cref="ParentId"/>.
    /// Local transform values are relative to the parent bone (or the rig root for root bones).
    /// World transforms are computed at render time by composing the chain.
    /// </summary>
    [Serializable]
    public class Bone
    {
        /// <summary>Stable identifier used by <see cref="SpriteBinding"/> and parent links. Must be unique within a <see cref="Rig"/>.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>Display name for the editor. Not required to be unique.</summary>
        public string Name { get; set; } = "Bone";

        /// <summary>Id of the parent bone, or null/empty for a root bone.</summary>
        public string ParentId { get; set; }

        // ---- Local rest-pose transform (relative to parent) ----

        /// <summary>Local position offset from the parent's tip (or rig origin for root bones), in surface pixels.</summary>
        public float LocalX { get; set; }

        /// <summary>Local position offset from the parent's tip, in surface pixels.</summary>
        public float LocalY { get; set; }

        /// <summary>Local rotation in radians, relative to parent rotation.</summary>
        public float LocalRotation { get; set; }

        /// <summary>Local uniform-ish scale on X. Default 1.</summary>
        public float LocalScaleX { get; set; } = 1f;

        /// <summary>Local uniform-ish scale on Y. Default 1.</summary>
        public float LocalScaleY { get; set; } = 1f;

        /// <summary>
        /// Visual length of the bone in pixels. Used by the editor for picking and to define
        /// the "tip" position that child bones attach to (tip = origin + length along local +X).
        /// </summary>
        public float Length { get; set; } = 32f;

        // ---- Editor-only metadata ----

        /// <summary>Color used to draw the bone overlay in the editor. Stored as ARGB ints for serialization.</summary>
        public int OverlayA { get; set; } = 255;
        public int OverlayR { get; set; } = 255;
        public int OverlayG { get; set; } = 200;
        public int OverlayB { get; set; } = 0;

        /// <summary>Convenience accessor (not serialized).</summary>
        [XmlIgnore]
        public Color OverlayColor
        {
            get { return Color.FromArgb(OverlayA, OverlayR, OverlayG, OverlayB); }
            set { OverlayA = value.A; OverlayR = value.R; OverlayG = value.G; OverlayB = value.B; }
        }

        /// <summary>If true, the bone is locked in the editor and cannot be moved/rotated by gestures.</summary>
        public bool Locked { get; set; }

        /// <summary>If true, the bone is hidden in the editor overlay (children remain unaffected).</summary>
        public bool Hidden { get; set; }
    }
}
