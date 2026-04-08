using System;

namespace SESpriteLCDLayoutTool.Models
{
    /// <summary>
    /// Lightweight DTO that mirrors the fields of a Torch plugin's <c>LcdSpriteRow</c>
    /// struct.  Captured from a runtime snapshot so the layout tool can replay render
    /// methods with real game values instead of placeholder text.
    /// </summary>
    [Serializable]
    public class SnapshotRowData
    {
        /// <summary>Row kind name (Header, Separator, Item, Bar, Stat, Footer, ItemBar).</summary>
        public string Kind { get; set; } = "Stat";

        public string Text { get; set; } = "";
        public string StatText { get; set; } = "";
        public string IconSprite { get; set; } = "";

        public int TextColorR { get; set; } = 255;
        public int TextColorG { get; set; } = 255;
        public int TextColorB { get; set; } = 255;
        public int TextColorA { get; set; } = 255;

        public float BarFill { get; set; }

        public int BarFillColorR { get; set; }
        public int BarFillColorG { get; set; }
        public int BarFillColorB { get; set; }
        public int BarFillColorA { get; set; } = 255;

        public bool ShowAlert { get; set; }

        /// <summary>
        /// Emits a C# <c>new LcdSpriteRow { … }</c> initializer using the captured values.
        /// </summary>
        public string ToCSharpInitializer()
        {
            var sb = new System.Text.StringBuilder("new LcdSpriteRow { ");
            sb.Append($"RowKind = LcdSpriteRow.Kind.{Kind}");

            if (!string.IsNullOrEmpty(Text))
                sb.Append($", Text = \"{EscapeString(Text)}\"");
            if (!string.IsNullOrEmpty(StatText))
                sb.Append($", StatText = \"{EscapeString(StatText)}\"");
            if (!string.IsNullOrEmpty(IconSprite))
                sb.Append($", IconSprite = \"{EscapeString(IconSprite)}\"");

            sb.Append($", TextColor = new Color({TextColorR}, {TextColorG}, {TextColorB}, {TextColorA})");

            if (BarFill > 0f)
                sb.Append($", BarFill = {BarFill:F4}f");
            if (BarFillColorR != 0 || BarFillColorG != 0 || BarFillColorB != 0)
                sb.Append($", BarFillColor = new Color({BarFillColorR}, {BarFillColorG}, {BarFillColorB}, {BarFillColorA})");
            if (ShowAlert)
                sb.Append(", ShowAlert = true");

            sb.Append(" }");
            return sb.ToString();
        }

        /// <summary>
        /// Returns a human-readable summary (e.g. "Item: Steel Plate (800 / 1000)").
        /// </summary>
        public string ToDisplayString()
        {
            string display = Kind;
            if (!string.IsNullOrEmpty(Text))
                display += ": " + Text;
            if (!string.IsNullOrEmpty(StatText))
                display += " (" + StatText + ")";
            return display;
        }

        private static string EscapeString(string s)
        {
            return (s ?? "")
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t")
                .Replace("\0", "\\0");
        }
    }
}
