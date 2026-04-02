using System;
using System.Drawing;
using System.Xml.Serialization;

namespace SESpriteLCDLayoutTool.Models
{
    public enum SpriteEntryType { Texture, Text }
    public enum SpriteTextAlignment { Left, Center, Right }

    [Serializable]
    public class SpriteEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public SpriteEntryType Type { get; set; } = SpriteEntryType.Texture;

        // Sprite name (Data field in MySprite)
        public string SpriteName { get; set; } = "SquareSimple";

        // Position = CENTER of sprite in LCD surface coordinates
        public float X { get; set; } = 256f;
        public float Y { get; set; } = 256f;
        public float Width { get; set; } = 100f;
        public float Height { get; set; } = 100f;

        // Color components stored separately for XML serialization
        public int ColorR { get; set; } = 255;
        public int ColorG { get; set; } = 255;
        public int ColorB { get; set; } = 255;
        public int ColorA { get; set; } = 255;

        // For TEXTURE sprites: rotation in radians
        // For TEXT sprites: this drives Scale (RotationOrScale in SE)
        public float Rotation { get; set; } = 0f;

        // Text-sprite properties
        public string Text { get; set; } = "Hello LCD";
        public string FontId { get; set; } = "White";
        public SpriteTextAlignment Alignment { get; set; } = SpriteTextAlignment.Center;
        public float Scale { get; set; } = 1.0f;

        /// <summary>
        /// When true, this sprite's position/size came from a live LCD snapshot
        /// and is for visual reference only — not exported as literal values.
        /// </summary>
        public bool IsReferenceLayout { get; set; }

        // ── Source tracking (for per-sprite round-trip patching) ──────────────

        /// <summary>Character offset in OriginalSourceCode where this sprite definition starts (-1 = not tracked).</summary>
        [XmlIgnore] public int SourceStart { get; set; } = -1;

        /// <summary>Character offset (exclusive) in OriginalSourceCode where this sprite definition ends (-1 = not tracked).</summary>
        [XmlIgnore] public int SourceEnd { get; set; } = -1;

        /// <summary>Contextual label from surrounding code (e.g. "Header: Text", "Item: Triangle"). Overrides DisplayName when set.</summary>
        [XmlIgnore] public string ImportLabel { get; set; }

        /// <summary>Snapshot of property values at import time, used for diffing in per-sprite round-trip.</summary>
        [XmlIgnore] public SpriteEntry ImportBaseline { get; set; }

        [XmlIgnore]
        public Color Color
        {
            get => Color.FromArgb(ColorA, ColorR, ColorG, ColorB);
            set { ColorR = value.R; ColorG = value.G; ColorB = value.B; ColorA = value.A; }
        }

        public string DisplayName
        {
            get
            {
                if (ImportLabel != null) return ImportLabel;
                return Type == SpriteEntryType.Text
                    ? $"TEXT \"{(Text != null && Text.Length > 12 ? Text.Substring(0, 9) + "..." : Text)}\""
                    : SpriteName;
            }
        }

        /// <summary>Creates a shallow clone of property values for baseline comparison.</summary>
        public SpriteEntry CloneValues()
        {
            return (SpriteEntry)MemberwiseClone();
        }
    }
}
