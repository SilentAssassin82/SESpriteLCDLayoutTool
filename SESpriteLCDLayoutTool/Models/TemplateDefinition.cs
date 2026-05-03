using System;
using System.Drawing;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool.Models
{
    /// <summary>
    /// Describes how well a template cooperates with the app's Roslyn-based
    /// animation/code injection pipeline. Used by the gallery UI to decide
    /// whether the smart-insert/Update-Code path can be enabled, or whether
    /// the user should be warned and limited to a manual paste.
    /// </summary>
    public enum TemplateCompatibility
    {
        /// <summary>
        /// Pure static <c>frame.Add(new MySprite { ... })</c> blocks.
        /// Safe to combine with any other animation; the injector can target
        /// these sprites by name/ordinal/Id and apply per-frame overrides.
        /// </summary>
        Safe,

        /// <summary>
        /// Self-contained animation math (uses its own <c>tick</c>, sin/cos,
        /// or color cycling). Compiles and runs on its own, but does NOT
        /// participate in the keyframe/effect injector. The Update-Code /
        /// smart-merge path should be disabled for these templates so the
        /// user keeps the snippet exactly as inserted.
        /// </summary>
        Standalone,

        /// <summary>
        /// Touches infrastructure the injector also owns (e.g. emits
        /// <c>_tick++</c>, blink-style <c>if (visible) { ... }</c> guards,
        /// or its own Ease helper). Mixing with injected animations on the
        /// same sprite will produce duplicate identifiers or marker
        /// collisions. Insert manually only; the gallery surfaces a warning.
        /// </summary>
        Conflicting,
    }

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

        /// <summary>
        /// How this template cooperates with the Roslyn animation injector.
        /// Defaults to <see cref="TemplateCompatibility.Safe"/> — pure static
        /// Add blocks that can be combined with injected animations freely.
        /// </summary>
        public TemplateCompatibility Compatibility { get; set; } = TemplateCompatibility.Safe;

        /// <summary>
        /// Optional human-readable note shown in the gallery alongside the
        /// compatibility badge. Use this to explain WHY a template is
        /// Standalone or Conflicting and what the user should do instead
        /// (e.g. "Use the Keyframe Animator's Rotate effect for stacking").
        /// </summary>
        public string CompatibilityNote { get; set; }
    }
}
