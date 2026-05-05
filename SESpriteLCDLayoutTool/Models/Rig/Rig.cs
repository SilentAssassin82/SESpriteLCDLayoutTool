using System;
using System.Collections.Generic;

namespace SESpriteLCDLayoutTool.Models.Rig
{
    /// <summary>
    /// A simple 2D bone rig attached to an <see cref="LcdLayout"/>.
    /// Holds a flat list of bones (parented via <see cref="Bone.ParentId"/>) and the bindings
    /// that map layout sprites onto those bones. Phase 1 stores only the rest pose; future
    /// phases will add named poses / keyframed animations.
    /// </summary>
    [Serializable]
    public class Rig
    {
        /// <summary>Stable identifier for the rig within a layout.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>Display name shown in the editor.</summary>
        public string Name { get; set; } = "Rig";

        /// <summary>World-space origin of the rig on the LCD surface (root bones are positioned relative to this).</summary>
        public float OriginX { get; set; }

        /// <summary>World-space origin of the rig on the LCD surface.</summary>
        public float OriginY { get; set; }

        /// <summary>All bones in the rig. Hierarchy is expressed via <see cref="Bone.ParentId"/>.</summary>
        public List<Bone> Bones { get; set; } = new List<Bone>();

        /// <summary>Sprite-to-bone bindings. Multiple bindings may target the same sprite or bone.</summary>
        public List<SpriteBinding> Bindings { get; set; } = new List<SpriteBinding>();

        /// <summary>If false, the rig is ignored at render time (sprites render untransformed).</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Animation clips authored on this rig. Empty by default.</summary>
        public List<RigClip> Clips { get; set; } = new List<RigClip>();

        /// <summary>Id of the clip currently selected in the editor (also used as the default playback clip).</summary>
        public string ActiveClipId { get; set; }
    }
}
