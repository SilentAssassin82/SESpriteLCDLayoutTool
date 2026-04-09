using System;
using System.Drawing;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool.Models
{
    /// <summary>
    /// Defines a template that can be inserted into the code editor.
    /// Templates are pre-built sprite patterns with animations or common UI elements.
    /// </summary>
    public class TemplateDefinition
    {
        /// <summary>Unique identifier for this template.</summary>
        public string Id { get; set; }

        /// <summary>Display name shown in the gallery.</summary>
        public string Name { get; set; }

        /// <summary>Detailed description of what this template does.</summary>
        public string Description { get; set; }

        /// <summary>Category for filtering (e.g., "Progress Bars", "Animations", "Status Indicators").</summary>
        public string Category { get; set; }

        /// <summary>
        /// Built-in sprite name to use as a visual preview in the gallery.
        /// If null, a generic icon is shown.
        /// </summary>
        public string PreviewSpriteName { get; set; }

        /// <summary>
        /// Preview color for the template thumbnail.
        /// </summary>
        public Color PreviewColor { get; set; } = Color.White;

        /// <summary>
        /// Tags for search/filtering (e.g., "animated", "health", "loading").
        /// </summary>
        public string[] Tags { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Function that generates the template code.
        /// Parameters: (surfaceWidth, surfaceHeight, targetScript)
        /// Returns: The generated C# code snippet.
        /// </summary>
        public Func<float, float, TargetScriptType, string> GenerateCode { get; set; }

        /// <summary>
        /// Whether this template requires animation (tick-based updates).
        /// </summary>
        public bool IsAnimated { get; set; }

        /// <summary>
        /// Estimated sprite count for the template.
        /// </summary>
        public int SpriteCount { get; set; } = 1;

        /// <summary>
        /// Optional: default position hint (0-1 range, relative to surface).
        /// </summary>
        public float DefaultX { get; set; } = 0.5f;
        public float DefaultY { get; set; } = 0.5f;
    }
}
