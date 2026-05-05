using System;

namespace SESpriteLCDLayoutTool.Models.Rig
{
    /// <summary>
    /// Binds a sprite (by index into <see cref="LcdLayout.Sprites"/>) to a bone in a <see cref="Rig"/>.
    /// At render time, the bone's world transform is composed with the binding offsets to produce
    /// the sprite's final position/rotation/scale before it is emitted.
    /// </summary>
    [Serializable]
    public class SpriteBinding
    {
        /// <summary>Bone this sprite is parented to.</summary>
        public string BoneId { get; set; }

        /// <summary>
        /// Index into the owning <see cref="LcdLayout.Sprites"/> list at bind time.
        /// Index-based (not by reference) so bindings round-trip through XML serialization.
        /// </summary>
        public int SpriteIndex { get; set; } = -1;

        /// <summary>Local position offset from the bone origin, in pixels (pre-rotation).</summary>
        public float OffsetX { get; set; }

        /// <summary>Local position offset from the bone origin, in pixels (pre-rotation).</summary>
        public float OffsetY { get; set; }

        /// <summary>Additional rotation in radians applied on top of the bone rotation.</summary>
        public float RotationOffset { get; set; }

        /// <summary>Additional scale multiplier on X applied on top of bone scale. Default 1.</summary>
        public float ScaleX { get; set; } = 1f;

        /// <summary>Additional scale multiplier on Y applied on top of bone scale. Default 1.</summary>
        public float ScaleY { get; set; } = 1f;

        /// <summary>If true, the binding is muted: the sprite is rendered at its untransformed pose.</summary>
        public bool Muted { get; set; }
    }
}
