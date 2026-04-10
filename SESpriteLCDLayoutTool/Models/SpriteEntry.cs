using System;
using System.Collections.Generic;
using System.Drawing;
using System.Xml.Serialization;

namespace SESpriteLCDLayoutTool.Models
{
    public enum SpriteEntryType { Texture, Text }
    public enum SpriteTextAlignment { Left, Center, Right }

    /// <summary>
    /// Represents a single Color literal found in the source code surrounding a sprite definition.
    /// Used to display editable swatches for expression colors (e.g. ternary branches).
    /// </summary>
    public class ExpressionColor
    {
        public int R { get; set; }
        public int G { get; set; }
        public int B { get; set; }
        public int A { get; set; } = 255;

        /// <summary>Character offset in OriginalSourceCode where this Color literal starts.</summary>
        public int SourceOffset { get; set; }

        /// <summary>Length of the Color literal text in OriginalSourceCode.</summary>
        public int SourceLength { get; set; }

        /// <summary>The original literal text (e.g. "new Color(0, 200, 255)" or "Color.White").</summary>
        public string LiteralText { get; set; }

        /// <summary>Convenience property for UI display.</summary>
        public Color Color => Color.FromArgb(A, R, G, B);
    }

    /// <summary>Discriminator for expression literal types.</summary>
    public enum ValueKind { Color, Vector2, Float, String }

    /// <summary>
    /// Base class for any source-code literal found near a sprite definition.
    /// Carries the source offset/length for offset-targeted patching.
    /// </summary>
    public abstract class ExpressionLiteral
    {
        /// <summary>What kind of literal this is.</summary>
        public abstract ValueKind Kind { get; }

        /// <summary>Character offset in OriginalSourceCode where this literal starts.</summary>
        public int SourceOffset { get; set; }

        /// <summary>Length of the literal text in OriginalSourceCode.</summary>
        public int SourceLength { get; set; }

        /// <summary>The original literal text as it appears in the source.</summary>
        public string LiteralText { get; set; }
    }

    /// <summary>
    /// Represents a <c>new Vector2(X, Y)</c> literal found in source code.
    /// </summary>
    public class ExpressionVector2 : ExpressionLiteral
    {
        public override ValueKind Kind => ValueKind.Vector2;
        public float X { get; set; }
        public float Y { get; set; }

        /// <summary>Optional property context (e.g. "Position", "Size") if the literal is an assignment.</summary>
        public string PropertyContext { get; set; }
    }

    /// <summary>
    /// Represents a float literal (e.g. <c>0.8f</c>, <c>1.2f</c>) found in source code.
    /// </summary>
    public class ExpressionFloat : ExpressionLiteral
    {
        public override ValueKind Kind => ValueKind.Float;
        public float Value { get; set; }

        /// <summary>Optional property context (e.g. "RotationOrScale") if the literal is an assignment.</summary>
        public string PropertyContext { get; set; }
    }

    /// <summary>
    /// Represents a quoted string literal (e.g. <c>"SquareSimple"</c>) found in source code.
    /// </summary>
    public class ExpressionString : ExpressionLiteral
    {
        public override ValueKind Kind => ValueKind.String;
        public string Value { get; set; }

        /// <summary>Optional property context (e.g. "Data", "FontId") if the literal is an assignment.</summary>
        public string PropertyContext { get; set; }
    }

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

        /// <summary>
        /// When true, this sprite came from runtime snapshot data (LcdSpriteRow[])
        /// captured via live pipe or in-game !lcd snapshot file. Used to preserve
        /// snapshot positions during Execute Code.
        /// </summary>
        public bool IsSnapshotData { get; set; }

        /// <summary>
        /// When true, this sprite came from executing code (Pulsar/Mod script).
        /// These sprites have no source tracking but should NOT be inserted as new code -
        /// they already exist in the original source as runtime-generated sprites.
        /// Only sprites with IsFromExecution=false AND SourceStart=-1 are truly "new".
        /// </summary>
        [XmlIgnore] public bool IsFromExecution { get; set; }

        /// <summary>
        /// Optional note displayed in UI showing which runtime snapshot row this sprite represents
        /// (e.g. "Header: Inventory", "ItemBar: Steel Plate (800/1000)").
        /// </summary>
        public string RuntimeDataNote { get; set; }

        // ── Source tracking (for per-sprite round-trip patching) ──────────────

        /// <summary>Character offset in OriginalSourceCode where this sprite definition starts (-1 = not tracked).</summary>
        [XmlIgnore] public int SourceStart { get; set; } = -1;

        /// <summary>Character offset (exclusive) in OriginalSourceCode where this sprite definition ends (-1 = not tracked).</summary>
        [XmlIgnore] public int SourceEnd { get; set; } = -1;

        /// <summary>Contextual label from surrounding code (e.g. "Header: Text", "Item: Triangle"). Overrides DisplayName when set.</summary>
        [XmlIgnore] public string ImportLabel { get; set; }

        /// <summary>Variable name extracted from code (e.g. "sprites.Add(header)", "frame.Add(titleBar)") for layer list annotation.</summary>
        [XmlIgnore] public string VariableName { get; set; }

        /// <summary>
        /// The name of the method that created this sprite during execution.
        /// Populated when running full code with method tracking enabled.
        /// Used for Execute &amp; Isolate to filter sprites by source method.
        /// </summary>
        [XmlIgnore] public string SourceMethodName { get; set; }

        /// <summary>
        /// Index of this sprite within its source method (0-based).
        /// Allows distinguishing multiple sprites with the same name from the same method.
        /// </summary>
        [XmlIgnore] public int SourceMethodIndex { get; set; } = -1;

        /// <summary>Snapshot of property values at import time, used for diffing in per-sprite round-trip.</summary>
        [XmlIgnore] public SpriteEntry ImportBaseline { get; set; }

        /// <summary>When true, this sprite is temporarily hidden from the canvas and hit-testing.</summary>
        [XmlIgnore] public bool IsHidden { get; set; }

        /// <summary>
        /// All Color literals found in the source context surrounding this sprite's definition.
        /// Populated when the sprite has source tracking (SourceStart/SourceEnd &gt;= 0).
        /// </summary>
        [XmlIgnore] public List<ExpressionColor> ExpressionColors { get; set; }

        /// <summary>
        /// All Vector2 literals found in the source context surrounding this sprite's definition.
        /// </summary>
        [XmlIgnore] public List<ExpressionVector2> ExpressionVectors { get; set; }

        /// <summary>
        /// All float literals found in the source context surrounding this sprite's definition.
        /// </summary>
        [XmlIgnore] public List<ExpressionFloat> ExpressionFloats { get; set; }

        /// <summary>
        /// All string literals found in the source context surrounding this sprite's definition.
        /// </summary>
        [XmlIgnore] public List<ExpressionString> ExpressionStrings { get; set; }

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
