using System;
using System.Collections.Generic;
using System.Drawing;
using SESpriteLCDLayoutTool.Models;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Static catalog of pre-built templates for common LCD sprite patterns.
    /// </summary>
    public static class TemplateGallery
    {
        public static readonly List<TemplateDefinition> Templates = new List<TemplateDefinition>();

        static TemplateGallery()
        {
            // ═══════════════════════════════════════════════════════════════════
            // PROGRESS BARS
            // ═══════════════════════════════════════════════════════════════════

            Templates.Add(new TemplateDefinition
            {
                Id = "progress-horizontal",
                Name = "Horizontal Progress Bar",
                Description = "A horizontal progress bar with background track and fill. " +
                              "Includes a percentage label. Good for health, cargo, power displays.",
                Category = "Progress Bars",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(80, 200, 80),
                Tags = new[] { "progress", "bar", "health", "cargo", "power" },
                IsAnimated = false,
                SpriteCount = 3,
                GenerateCode = (w, h, target) => GenerateHorizontalProgressBar(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "progress-vertical",
                Name = "Vertical Progress Bar",
                Description = "A vertical progress bar that fills from bottom to top. " +
                              "Great for fuel gauges, tank levels, or ammunition.",
                Category = "Progress Bars",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(100, 180, 255),
                Tags = new[] { "progress", "bar", "fuel", "tank", "ammo", "vertical" },
                IsAnimated = false,
                SpriteCount = 2,
                GenerateCode = (w, h, target) => GenerateVerticalProgressBar(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "progress-segmented",
                Name = "Segmented Progress Bar",
                Description = "A progress bar divided into discrete segments. " +
                              "Perfect for shield bars, discrete inventory slots, or step indicators.",
                Category = "Progress Bars",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(255, 200, 80),
                Tags = new[] { "progress", "segments", "shield", "discrete" },
                IsAnimated = false,
                SpriteCount = 10,
                GenerateCode = (w, h, target) => GenerateSegmentedProgressBar(w, h, target),
            });

            // ═══════════════════════════════════════════════════════════════════
            // STATUS INDICATORS
            // ═══════════════════════════════════════════════════════════════════

            Templates.Add(new TemplateDefinition
            {
                Id = "status-icon-label",
                Name = "Icon + Label Status",
                Description = "An icon paired with a text label. " +
                              "Use for status displays like 'Online', 'Docked', 'Alert'.",
                Category = "Status Indicators",
                PreviewSpriteName = "Circle",
                PreviewColor = Color.FromArgb(80, 255, 80),
                Tags = new[] { "status", "icon", "label", "indicator" },
                IsAnimated = false,
                SpriteCount = 2,
                GenerateCode = (w, h, target) => GenerateIconLabel(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "status-warning-panel",
                Name = "Warning Panel",
                Description = "A bordered panel with warning icon and message. " +
                              "Suitable for alerts, errors, or important notifications.",
                Category = "Status Indicators",
                PreviewSpriteName = "Triangle",
                PreviewColor = Color.FromArgb(255, 200, 60),
                Tags = new[] { "warning", "alert", "error", "panel", "notification" },
                IsAnimated = false,
                SpriteCount = 4,
                GenerateCode = (w, h, target) => GenerateWarningPanel(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "status-grid",
                Name = "Status Grid (2x2)",
                Description = "A 2x2 grid of status indicators with icons and labels. " +
                              "Perfect for system overview panels.",
                Category = "Status Indicators",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(150, 150, 200),
                Tags = new[] { "grid", "status", "overview", "systems" },
                IsAnimated = false,
                SpriteCount = 8,
                GenerateCode = (w, h, target) => GenerateStatusGrid(w, h, target),
            });

            // ═══════════════════════════════════════════════════════════════════
            // ANIMATIONS
            // ═══════════════════════════════════════════════════════════════════

            Templates.Add(new TemplateDefinition
            {
                Id = "anim-rotating-radar",
                Name = "Rotating Radar Sweep",
                Description = "A radar sweep line that rotates around a center point. " +
                              "Classic sci-fi scanning animation.",
                Category = "Animations",
                PreviewSpriteName = "SemiCircle",
                PreviewColor = Color.FromArgb(80, 255, 80),
                Tags = new[] { "radar", "sweep", "scan", "rotate", "animated" },
                IsAnimated = true,
                SpriteCount = 2,
                Compatibility = TemplateCompatibility.Standalone,
                CompatibilityNote = "Self-contained tick math. Insert manually — do NOT mix with the keyframe animator on the same sprite. For stackable rotation use the Rotate effect in the Keyframe Animator.",
                GenerateCode = (w, h, target) => GenerateRotatingRadar(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "anim-pulsing-alert",
                Name = "Pulsing Alert Icon",
                Description = "An icon that pulses (scales up and down) to draw attention. " +
                              "Great for warnings or active status indicators.",
                Category = "Animations",
                PreviewSpriteName = "Circle",
                PreviewColor = Color.FromArgb(255, 80, 80),
                Tags = new[] { "pulse", "alert", "warning", "animated", "attention" },
                IsAnimated = true,
                SpriteCount = 1,
                Compatibility = TemplateCompatibility.Standalone,
                CompatibilityNote = "Self-contained pulse math. For stackable pulsing use the Pulse effect in the Keyframe Animator instead.",
                GenerateCode = (w, h, target) => GeneratePulsingAlert(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "anim-loading-spinner",
                Name = "Loading Spinner",
                Description = "A circular loading spinner with rotating segments. " +
                              "Shows activity or processing state.",
                Category = "Animations",
                PreviewSpriteName = "CircleHollow",
                PreviewColor = Color.FromArgb(100, 200, 255),
                Tags = new[] { "loading", "spinner", "processing", "animated" },
                IsAnimated = true,
                SpriteCount = 1,
                Compatibility = TemplateCompatibility.Standalone,
                CompatibilityNote = "Self-contained tick math. For stackable rotation use the Rotate effect in the Keyframe Animator instead.",
                GenerateCode = (w, h, target) => GenerateLoadingSpinner(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "anim-blinking-cursor",
                Name = "Blinking Cursor",
                Description = "A text cursor that blinks on/off. " +
                              "Adds a terminal/console feel to text displays.",
                Category = "Animations",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(200, 200, 200),
                Tags = new[] { "cursor", "blink", "terminal", "text", "animated" },
                IsAnimated = true,
                SpriteCount = 1,
                Compatibility = TemplateCompatibility.Conflicting,
                CompatibilityNote = "Wraps its Add block in an if(visible) guard, which collides with the injector's BLINK markers. Insert manually only. For stackable blink use the Blink effect in the Keyframe Animator.",
                GenerateCode = (w, h, target) => GenerateBlinkingCursor(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "anim-color-cycle",
                Name = "Rainbow Color Cycle",
                Description = "A sprite that cycles through rainbow colors. " +
                              "Eye-catching decorative effect.",
                Category = "Animations",
                PreviewSpriteName = "Circle",
                PreviewColor = Color.FromArgb(255, 100, 100),
                Tags = new[] { "rainbow", "color", "cycle", "animated", "decorative" },
                IsAnimated = true,
                SpriteCount = 1,
                Compatibility = TemplateCompatibility.Standalone,
                CompatibilityNote = "Self-contained HSV cycle. For stackable color cycling use the ColorCycle effect in the Keyframe Animator instead.",
                GenerateCode = (w, h, target) => GenerateColorCycle(w, h, target),
            });

            // ═══════════════════════════════════════════════════════════════════
            // LAYOUT HELPERS
            // ═══════════════════════════════════════════════════════════════════

            Templates.Add(new TemplateDefinition
            {
                Id = "layout-header",
                Name = "Header Bar",
                Description = "A header bar with title text and optional icon. " +
                              "Spans the top of the display.",
                Category = "Layout Helpers",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(60, 80, 120),
                Tags = new[] { "header", "title", "bar", "top" },
                IsAnimated = false,
                SpriteCount = 2,
                GenerateCode = (w, h, target) => GenerateHeaderBar(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "layout-footer",
                Name = "Footer Bar",
                Description = "A footer bar with text and timestamp. " +
                              "Spans the bottom of the display.",
                Category = "Layout Helpers",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(50, 50, 60),
                Tags = new[] { "footer", "bottom", "bar", "status" },
                IsAnimated = false,
                SpriteCount = 2,
                GenerateCode = (w, h, target) => GenerateFooterBar(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "layout-bordered-panel",
                Name = "Bordered Panel",
                Description = "A rectangular panel with visible border. " +
                              "Use to group related content.",
                Category = "Layout Helpers",
                PreviewSpriteName = "SquareHollow",
                PreviewColor = Color.FromArgb(100, 100, 120),
                Tags = new[] { "panel", "border", "container", "box" },
                IsAnimated = false,
                SpriteCount = 2,
                GenerateCode = (w, h, target) => GenerateBorderedPanel(w, h, target),
            });

            // ═══════════════════════════════════════════════════════════════════
            // GAUGES & METERS
            // ═══════════════════════════════════════════════════════════════════

            Templates.Add(new TemplateDefinition
            {
                Id = "gauge-circular",
                Name = "Circular Gauge",
                Description = "A circular gauge with arc fill. " +
                              "Great for speed, power levels, or percentages.",
                Category = "Gauges & Meters",
                PreviewSpriteName = "CircleHollow",
                PreviewColor = Color.FromArgb(80, 200, 255),
                Tags = new[] { "gauge", "circular", "arc", "meter", "speed" },
                IsAnimated = false,
                SpriteCount = 3,
                GenerateCode = (w, h, target) => GenerateCircularGauge(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "gauge-thermometer",
                Name = "Thermometer",
                Description = "A thermometer-style vertical gauge with bulb. " +
                              "Perfect for temperature or heat displays.",
                Category = "Gauges & Meters",
                PreviewSpriteName = "Circle",
                PreviewColor = Color.FromArgb(255, 100, 80),
                Tags = new[] { "thermometer", "temperature", "heat", "vertical" },
                IsAnimated = false,
                SpriteCount = 3,
                GenerateCode = (w, h, target) => GenerateThermometer(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "gauge-battery",
                Name = "Battery Indicator",
                Description = "A horizontal battery icon with segmented charge level and outline. " +
                              "Adjust the charge value to control how many cells are lit.",
                Category = "Gauges & Meters",
                PreviewSpriteName = "SquareHollow",
                PreviewColor = Color.FromArgb(120, 220, 120),
                Tags = new[] { "battery", "charge", "power", "cells" },
                IsAnimated = false,
                SpriteCount = 7,
                GenerateCode = (w, h, target) => GenerateBatteryIndicator(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "gauge-speedometer",
                Name = "Speedometer",
                Description = "A semi-circular speedometer with a needle indicator. " +
                              "Adjust the value (0..1) to control the needle angle.",
                Category = "Gauges & Meters",
                PreviewSpriteName = "SemiCircle",
                PreviewColor = Color.FromArgb(255, 180, 60),
                Tags = new[] { "speedometer", "dial", "needle", "meter" },
                IsAnimated = false,
                SpriteCount = 4,
                GenerateCode = (w, h, target) => GenerateSpeedometer(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "gauge-compass",
                Name = "Compass",
                Description = "A compass rose with cardinal direction labels and a heading needle. " +
                              "Adjust the heading (degrees) to rotate the needle.",
                Category = "Gauges & Meters",
                PreviewSpriteName = "Circle",
                PreviewColor = Color.FromArgb(180, 220, 255),
                Tags = new[] { "compass", "heading", "direction", "navigation" },
                IsAnimated = false,
                SpriteCount = 6,
                GenerateCode = (w, h, target) => GenerateCompass(w, h, target),
            });

            // ═══════════════════════════════════════════════════════════════════
            // LAYOUT HELPERS (extended)
            // ═══════════════════════════════════════════════════════════════════

            Templates.Add(new TemplateDefinition
            {
                Id = "layout-title-subtitle",
                Name = "Title + Subtitle",
                Description = "A large title with a smaller subtitle line beneath. " +
                              "Use as a top-of-screen header or section heading.",
                Category = "Layout Helpers",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(80, 100, 140),
                Tags = new[] { "title", "subtitle", "heading", "header" },
                IsAnimated = false,
                SpriteCount = 2,
                GenerateCode = (w, h, target) => GenerateTitleSubtitle(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "layout-divider",
                Name = "Divider Line",
                Description = "A thin horizontal divider line for separating content sections.",
                Category = "Layout Helpers",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(120, 120, 140),
                Tags = new[] { "divider", "separator", "line", "rule" },
                IsAnimated = false,
                SpriteCount = 1,
                GenerateCode = (w, h, target) => GenerateDivider(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "layout-side-panel",
                Name = "Side Panel",
                Description = "A vertical side panel/gutter on the left edge. " +
                              "Use for navigation rails or sidebar content.",
                Category = "Layout Helpers",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(40, 50, 70),
                Tags = new[] { "sidebar", "panel", "rail", "left", "gutter" },
                IsAnimated = false,
                SpriteCount = 1,
                GenerateCode = (w, h, target) => GenerateSidePanel(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "layout-three-column",
                Name = "Three-Column Row",
                Description = "Three evenly spaced text columns for tabular layouts. " +
                              "Edit the labels to fit your data.",
                Category = "Layout Helpers",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(180, 180, 200),
                Tags = new[] { "columns", "row", "table", "layout" },
                IsAnimated = false,
                SpriteCount = 3,
                GenerateCode = (w, h, target) => GenerateThreeColumnRow(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "layout-corner-brackets",
                Name = "Corner Brackets",
                Description = "Four HUD-style L-shaped brackets at the screen corners. " +
                              "Adds a tactical/sci-fi frame around your content.",
                Category = "Layout Helpers",
                PreviewSpriteName = "SquareHollow",
                PreviewColor = Color.FromArgb(80, 220, 220),
                Tags = new[] { "brackets", "corners", "hud", "frame", "tactical" },
                IsAnimated = false,
                SpriteCount = 8,
                GenerateCode = (w, h, target) => GenerateCornerBrackets(w, h, target),
            });

            // ═══════════════════════════════════════════════════════════════════
            // TEXT & LABELS
            // ═══════════════════════════════════════════════════════════════════

            Templates.Add(new TemplateDefinition
            {
                Id = "text-big-number",
                Name = "Big Number Display",
                Description = "A large centered number with a small caption underneath. " +
                              "Great for headline stats like speed, count, or ETA.",
                Category = "Text & Labels",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(220, 220, 240),
                Tags = new[] { "number", "stat", "headline", "big" },
                IsAnimated = false,
                SpriteCount = 2,
                GenerateCode = (w, h, target) => GenerateBigNumber(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "text-label-value",
                Name = "Label : Value Pair",
                Description = "A left-aligned label followed by a right-aligned value. " +
                              "Stack multiple to build a stats panel.",
                Category = "Text & Labels",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(200, 200, 220),
                Tags = new[] { "label", "value", "pair", "stat" },
                IsAnimated = false,
                SpriteCount = 2,
                GenerateCode = (w, h, target) => GenerateLabelValue(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "text-multiline",
                Name = "Multi-line Text Block",
                Description = "Three stacked lines of text for paragraph-style content. " +
                              "Edit each line independently.",
                Category = "Text & Labels",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(200, 200, 220),
                Tags = new[] { "text", "paragraph", "lines", "block" },
                IsAnimated = false,
                SpriteCount = 3,
                GenerateCode = (w, h, target) => GenerateMultilineText(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "text-centered-title",
                Name = "Centered Title",
                Description = "A single large centered title line. Use as a section banner.",
                Category = "Text & Labels",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(255, 255, 255),
                Tags = new[] { "title", "banner", "centered", "heading" },
                IsAnimated = false,
                SpriteCount = 1,
                GenerateCode = (w, h, target) => GenerateCenteredTitle(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "text-timestamp",
                Name = "Timestamp Display",
                Description = "A formatted DateTime.Now timestamp aligned to the bottom-right. " +
                              "Useful for last-updated indicators.",
                Category = "Text & Labels",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(140, 160, 200),
                Tags = new[] { "timestamp", "clock", "datetime", "updated" },
                IsAnimated = false,
                SpriteCount = 1,
                GenerateCode = (w, h, target) => GenerateTimestamp(w, h, target),
            });

            // ═══════════════════════════════════════════════════════════════════
            // BACKGROUNDS
            // ═══════════════════════════════════════════════════════════════════

            Templates.Add(new TemplateDefinition
            {
                Id = "bg-solid-fill",
                Name = "Solid Background",
                Description = "A full-screen solid colored background fill. " +
                              "Place first in your sprite list so other sprites draw on top.",
                Category = "Backgrounds",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(20, 24, 36),
                Tags = new[] { "background", "fill", "solid", "color" },
                IsAnimated = false,
                SpriteCount = 1,
                GenerateCode = (w, h, target) => GenerateSolidBackground(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "bg-vignette",
                Name = "Vignette Border",
                Description = "Four dark edges around the screen perimeter to focus the eye " +
                              "toward the center. Place after background fills.",
                Category = "Backgrounds",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(0, 0, 0),
                Tags = new[] { "vignette", "border", "edge", "frame" },
                IsAnimated = false,
                SpriteCount = 4,
                GenerateCode = (w, h, target) => GenerateVignette(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "bg-grid",
                Name = "Grid Background",
                Description = "A faint orthogonal grid for technical/blueprint-style displays. " +
                              "Adjust spacing to control density.",
                Category = "Backgrounds",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(60, 80, 100),
                Tags = new[] { "grid", "background", "blueprint", "technical" },
                IsAnimated = false,
                SpriteCount = 12,
                GenerateCode = (w, h, target) => GenerateGridBackground(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "bg-diagonal-stripes",
                Name = "Diagonal Stripes",
                Description = "A pattern of diagonal warning stripes. " +
                              "Place behind or alongside critical-state content.",
                Category = "Backgrounds",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(255, 200, 60),
                Tags = new[] { "stripes", "diagonal", "hazard", "warning", "pattern" },
                IsAnimated = false,
                SpriteCount = 8,
                GenerateCode = (w, h, target) => GenerateDiagonalStripes(w, h, target),
            });

            // ═══════════════════════════════════════════════════════════════════
            // HUD & TACTICAL
            // ═══════════════════════════════════════════════════════════════════

            Templates.Add(new TemplateDefinition
            {
                Id = "hud-crosshair",
                Name = "Crosshair",
                Description = "A simple centered crosshair with horizontal and vertical reticle lines.",
                Category = "HUD & Tactical",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(120, 220, 220),
                Tags = new[] { "crosshair", "reticle", "aim", "hud" },
                IsAnimated = false,
                SpriteCount = 4,
                GenerateCode = (w, h, target) => GenerateCrosshair(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "hud-targeting-reticle",
                Name = "Targeting Reticle",
                Description = "A circular targeting reticle with corner ticks. " +
                              "Use as a target lock indicator.",
                Category = "HUD & Tactical",
                PreviewSpriteName = "CircleHollow",
                PreviewColor = Color.FromArgb(255, 80, 80),
                Tags = new[] { "target", "reticle", "lock", "hud" },
                IsAnimated = false,
                SpriteCount = 5,
                GenerateCode = (w, h, target) => GenerateTargetingReticle(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "hud-minimap-frame",
                Name = "Minimap Frame",
                Description = "A square minimap frame in the top-right corner with crosshairs and a center dot.",
                Category = "HUD & Tactical",
                PreviewSpriteName = "SquareHollow",
                PreviewColor = Color.FromArgb(80, 200, 120),
                Tags = new[] { "minimap", "radar", "frame", "hud", "corner" },
                IsAnimated = false,
                SpriteCount = 5,
                GenerateCode = (w, h, target) => GenerateMinimapFrame(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "hud-notification-toast",
                Name = "Notification Toast",
                Description = "A pill-shaped notification banner with text, anchored near the top of the display.",
                Category = "HUD & Tactical",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(60, 80, 100),
                Tags = new[] { "toast", "notification", "banner", "alert" },
                IsAnimated = false,
                SpriteCount = 3,
                GenerateCode = (w, h, target) => GenerateNotificationToast(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "hud-kpi-tile",
                Name = "KPI Tile",
                Description = "A small dashboard tile with title, value, and a status bar. " +
                              "Stack multiple to build a KPI grid.",
                Category = "HUD & Tactical",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(60, 100, 140),
                Tags = new[] { "kpi", "tile", "dashboard", "metric" },
                IsAnimated = false,
                SpriteCount = 5,
                GenerateCode = (w, h, target) => GenerateKpiTile(w, h, target),
            });

            Templates.Add(new TemplateDefinition
            {
                Id = "hud-button-row",
                Name = "Button Row",
                Description = "Three side-by-side button-style panels with labels. " +
                              "Decorative only — Space Engineers LCDs cannot receive input.",
                Category = "HUD & Tactical",
                PreviewSpriteName = "SquareSimple",
                PreviewColor = Color.FromArgb(80, 120, 160),
                Tags = new[] { "buttons", "row", "menu", "ui" },
                IsAnimated = false,
                SpriteCount = 6,
                GenerateCode = (w, h, target) => GenerateButtonRow(w, h, target),
            });
        }

        /// <summary>Returns all unique categories in the template catalog.</summary>
        public static List<string> GetCategories()
        {
            var categories = new HashSet<string>();
            foreach (var t in Templates)
                categories.Add(t.Category);
            var list = new List<string>(categories);
            list.Sort();
            return list;
        }

        /// <summary>Returns templates filtered by category (null = all).</summary>
        public static List<TemplateDefinition> GetByCategory(string category)
        {
            if (string.IsNullOrEmpty(category))
                return new List<TemplateDefinition>(Templates);

            var result = new List<TemplateDefinition>();
            foreach (var t in Templates)
                if (t.Category == category)
                    result.Add(t);
            return result;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // CODE GENERATORS
        // ═══════════════════════════════════════════════════════════════════════════

        private static string GetListVar(TargetScriptType target) =>
            target == TargetScriptType.LcdHelper ? "sprites" : "frame";

        private static string GenerateHorizontalProgressBar(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float cy = h / 2f;
            float barW = w * 0.7f;
            float barH = 24f;

            return $@"// ═══ Horizontal Progress Bar ═══
// Adjust 'progress' (0.0 to 1.0) to control fill level
float progress = 0.65f;
float barWidth = {barW:F0}f;
float barHeight = {barH:F0}f;
float barX = {cx:F0}f;
float barY = {cy:F0}f;

// Background track
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(barX, barY),
    Size = new Vector2(barWidth, barHeight),
    Color = new Color(40, 40, 40),
}});

// Fill bar (grows from left)
float fillWidth = barWidth * progress;
float fillX = barX - (barWidth - fillWidth) / 2f;
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(fillX, barY),
    Size = new Vector2(fillWidth, barHeight - 4),
    Color = new Color(80, 200, 80),
}});

// Percentage text
{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = $""{{(int)(progress * 100)}}%"",
    Position = new Vector2(barX, barY - barHeight / 2 - 20),
    Color = Color.White,
    FontId = ""White"",
    Alignment = TextAlignment.CENTER,
    RotationOrScale = 0.8f,
}});
";
        }

        private static string GenerateVerticalProgressBar(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float cy = h / 2f;
            float barW = 32f;
            float barH = h * 0.6f;

            return $@"// ═══ Vertical Progress Bar ═══
// Adjust 'progress' (0.0 to 1.0) to control fill level
float progress = 0.75f;
float barWidth = {barW:F0}f;
float barHeight = {barH:F0}f;
float barX = {cx:F0}f;
float barY = {cy:F0}f;

// Background track
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(barX, barY),
    Size = new Vector2(barWidth, barHeight),
    Color = new Color(40, 40, 40),
}});

// Fill bar (grows from bottom)
float fillHeight = barHeight * progress;
float fillY = barY + (barHeight - fillHeight) / 2f;
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(barX, fillY),
    Size = new Vector2(barWidth - 4, fillHeight),
    Color = new Color(100, 180, 255),
}});
";
        }

        private static string GenerateSegmentedProgressBar(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float cy = h / 2f;

            return $@"// ═══ Segmented Progress Bar ═══
// Adjust 'filledSegments' to control how many segments are lit
int totalSegments = 10;
int filledSegments = 7;
float segmentWidth = 20f;
float segmentHeight = 24f;
float gap = 4f;
float totalWidth = totalSegments * (segmentWidth + gap) - gap;
float startX = {cx:F0}f - totalWidth / 2f + segmentWidth / 2f;
float barY = {cy:F0}f;

for (int i = 0; i < totalSegments; i++)
{{
    bool isFilled = i < filledSegments;
    {list}.Add(new MySprite {{
        Type = SpriteType.TEXTURE,
        Data = ""SquareSimple"",
        Position = new Vector2(startX + i * (segmentWidth + gap), barY),
        Size = new Vector2(segmentWidth, segmentHeight),
        Color = isFilled ? new Color(255, 200, 80) : new Color(50, 50, 50),
    }});
}}
";
        }

        private static string GenerateIconLabel(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float cy = h / 2f;

            return $@"// ═══ Icon + Label Status ═══
// Change icon sprite and text as needed
string statusText = ""ONLINE"";
Color statusColor = new Color(80, 255, 80);
float iconSize = 32f;
float posX = {cx:F0}f;
float posY = {cy:F0}f;

// Status icon
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""Circle"",
    Position = new Vector2(posX - 50, posY),
    Size = new Vector2(iconSize, iconSize),
    Color = statusColor,
}});

// Status label
{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = statusText,
    Position = new Vector2(posX + 10, posY - 12),
    Color = statusColor,
    FontId = ""White"",
    Alignment = TextAlignment.LEFT,
    RotationOrScale = 1f,
}});
";
        }

        private static string GenerateWarningPanel(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float cy = h / 2f;
            float panelW = w * 0.8f;
            float panelH = 80f;

            return $@"// ═══ Warning Panel ═══
string warningMessage = ""LOW FUEL WARNING"";
float panelWidth = {panelW:F0}f;
float panelHeight = {panelH:F0}f;
float panelX = {cx:F0}f;
float panelY = {cy:F0}f;

// Panel background
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(panelX, panelY),
    Size = new Vector2(panelWidth, panelHeight),
    Color = new Color(80, 60, 20),
}});

// Panel border
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareHollow"",
    Position = new Vector2(panelX, panelY),
    Size = new Vector2(panelWidth, panelHeight),
    Color = new Color(255, 200, 60),
}});

// Warning icon
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""Triangle"",
    Position = new Vector2(panelX - panelWidth / 2 + 40, panelY),
    Size = new Vector2(40, 40),
    Color = new Color(255, 200, 60),
}});

// Warning text
{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = warningMessage,
    Position = new Vector2(panelX + 10, panelY - 12),
    Color = new Color(255, 200, 60),
    FontId = ""White"",
    Alignment = TextAlignment.CENTER,
    RotationOrScale = 1f,
}});
";
        }

        private static string GenerateStatusGrid(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float cy = h / 2f;

            return $@"// ═══ Status Grid (2x2) ═══
// Customize status values and colors
var statuses = new[] {{
    (""POWER"", true),
    (""OXYGEN"", true),
    (""HYDROGEN"", false),
    (""DAMAGE"", true),
}};
float gridX = {cx:F0}f;
float gridY = {cy:F0}f;
float cellWidth = 120f;
float cellHeight = 50f;

for (int i = 0; i < 4; i++)
{{
    int col = i % 2;
    int row = i / 2;
    float x = gridX + (col - 0.5f) * cellWidth;
    float y = gridY + (row - 0.5f) * cellHeight;
    bool ok = statuses[i].Item2;
    Color c = ok ? new Color(80, 200, 80) : new Color(200, 80, 80);

    // Icon
    {list}.Add(new MySprite {{
        Type = SpriteType.TEXTURE,
        Data = ok ? ""Circle"" : ""Cross"",
        Position = new Vector2(x - 35, y),
        Size = new Vector2(20, 20),
        Color = c,
    }});

    // Label
    {list}.Add(new MySprite {{
        Type = SpriteType.TEXT,
        Data = statuses[i].Item1,
        Position = new Vector2(x, y - 10),
        Color = c,
        FontId = ""White"",
        Alignment = TextAlignment.CENTER,
        RotationOrScale = 0.7f,
    }});
}}
";
        }

        private static string GenerateRotatingRadar(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float cy = h / 2f;
            float radius = Math.Min(w, h) * 0.35f;

            return $@"// ═══ Rotating Radar Sweep ═══
// Call this every tick with an incrementing 'tick' value
float radarX = {cx:F0}f;
float radarY = {cy:F0}f;
float radarRadius = {radius:F0}f;
float rotationSpeed = 0.05f;
float angle = tick * rotationSpeed;

// Radar background circle
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""CircleHollow"",
    Position = new Vector2(radarX, radarY),
    Size = new Vector2(radarRadius * 2, radarRadius * 2),
    Color = new Color(40, 80, 40),
}});

// Sweep line (rotates)
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SemiCircle"",
    Position = new Vector2(radarX, radarY),
    Size = new Vector2(radarRadius, radarRadius * 2),
    Color = new Color(80, 255, 80, 180),
    RotationOrScale = angle,
}});
";
        }

        private static string GeneratePulsingAlert(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float cy = h / 2f;

            return $@"// ═══ Pulsing Alert Icon ═══
// Call this every tick with an incrementing 'tick' value
float alertX = {cx:F0}f;
float alertY = {cy:F0}f;
float baseSize = 48f;
float pulseAmplitude = 8f;
float pulseSpeed = 0.15f;

float scale = 1f + (float)Math.Sin(tick * pulseSpeed) * (pulseAmplitude / baseSize);
float size = baseSize * scale;

{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""Circle"",
    Position = new Vector2(alertX, alertY),
    Size = new Vector2(size, size),
    Color = new Color(255, 80, 80),
}});
";
        }

        private static string GenerateLoadingSpinner(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float cy = h / 2f;

            return $@"// ═══ Loading Spinner ═══
// Call this every tick with an incrementing 'tick' value
float spinnerX = {cx:F0}f;
float spinnerY = {cy:F0}f;
float spinnerSize = 64f;
float rotationSpeed = 0.1f;
float angle = tick * rotationSpeed;

{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""CircleHollow"",
    Position = new Vector2(spinnerX, spinnerY),
    Size = new Vector2(spinnerSize, spinnerSize),
    Color = new Color(100, 200, 255),
    RotationOrScale = angle,
}});
";
        }

        private static string GenerateBlinkingCursor(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float cy = h / 2f;

            return $@"// ═══ Blinking Cursor ═══
// Call this every tick with an incrementing 'tick' value
float cursorX = {cx:F0}f;
float cursorY = {cy:F0}f;
int blinkOnTicks = 30;
int blinkOffTicks = 30;
int cycleTicks = blinkOnTicks + blinkOffTicks;
bool visible = (tick % cycleTicks) < blinkOnTicks;

if (visible)
{{
    {list}.Add(new MySprite {{
        Type = SpriteType.TEXTURE,
        Data = ""SquareSimple"",
        Position = new Vector2(cursorX, cursorY),
        Size = new Vector2(8, 24),
        Color = new Color(200, 200, 200),
    }});
}}
";
        }

        private static string GenerateColorCycle(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float cy = h / 2f;

            return $@"// ═══ Rainbow Color Cycle ═══
// Call this every tick with an incrementing 'tick' value
float spriteX = {cx:F0}f;
float spriteY = {cy:F0}f;
float spriteSize = 64f;
float cycleSpeed = 2f;  // degrees per tick

float hue = (tick * cycleSpeed) % 360f;
Color rainbowColor = HsvToRgb(hue, 1f, 1f);

{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""Circle"",
    Position = new Vector2(spriteX, spriteY),
    Size = new Vector2(spriteSize, spriteSize),
    Color = rainbowColor,
}});

// Helper function (add to your class):
// Color HsvToRgb(float h, float s, float v)
// {{
//     float c = v * s;
//     float x = c * (1 - Math.Abs((h / 60f) % 2 - 1));
//     float m = v - c;
//     float r, g, b;
//     if (h < 60) {{ r = c; g = x; b = 0; }}
//     else if (h < 120) {{ r = x; g = c; b = 0; }}
//     else if (h < 180) {{ r = 0; g = c; b = x; }}
//     else if (h < 240) {{ r = 0; g = x; b = c; }}
//     else if (h < 300) {{ r = x; g = 0; b = c; }}
//     else {{ r = c; g = 0; b = x; }}
//     return new Color((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
// }}
";
        }

        private static string GenerateHeaderBar(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);

            return $@"// ═══ Header Bar ═══
string headerTitle = ""SYSTEM STATUS"";
float headerHeight = 36f;
float headerY = headerHeight / 2f;

// Header background
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2({w / 2f:F0}f, headerY),
    Size = new Vector2({w:F0}f, headerHeight),
    Color = new Color(60, 80, 120),
}});

// Header title
{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = headerTitle,
    Position = new Vector2({w / 2f:F0}f, headerY - 14),
    Color = Color.White,
    FontId = ""White"",
    Alignment = TextAlignment.CENTER,
    RotationOrScale = 1f,
}});
";
        }

        private static string GenerateFooterBar(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float footerH = 28f;
            float footerY = h - footerH / 2f;

            return $@"// ═══ Footer Bar ═══
float footerHeight = {footerH:F0}f;
float footerY = {footerY:F0}f;

// Footer background
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2({w / 2f:F0}f, footerY),
    Size = new Vector2({w:F0}f, footerHeight),
    Color = new Color(40, 40, 50),
}});

// Footer text
{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = $""Updated: {{DateTime.Now:HH:mm:ss}}"",
    Position = new Vector2({w / 2f:F0}f, footerY - 10),
    Color = new Color(120, 120, 140),
    FontId = ""White"",
    Alignment = TextAlignment.CENTER,
    RotationOrScale = 0.6f,
}});
";
        }

        private static string GenerateBorderedPanel(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float cy = h / 2f;
            float panelW = w * 0.7f;
            float panelH = h * 0.5f;

            return $@"// ═══ Bordered Panel ═══
float panelX = {cx:F0}f;
float panelY = {cy:F0}f;
float panelWidth = {panelW:F0}f;
float panelHeight = {panelH:F0}f;

// Panel fill
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(panelX, panelY),
    Size = new Vector2(panelWidth, panelHeight),
    Color = new Color(30, 30, 40),
}});

// Panel border
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareHollow"",
    Position = new Vector2(panelX, panelY),
    Size = new Vector2(panelWidth, panelHeight),
    Color = new Color(100, 100, 120),
}});
";
        }

        private static string GenerateCircularGauge(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float cy = h / 2f;
            float radius = Math.Min(w, h) * 0.3f;

            return $@"// ═══ Circular Gauge ═══
// Adjust 'progress' (0.0 to 1.0) to control arc fill
float progress = 0.72f;
float gaugeX = {cx:F0}f;
float gaugeY = {cy:F0}f;
float gaugeRadius = {radius:F0}f;

// Background ring
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""CircleHollow"",
    Position = new Vector2(gaugeX, gaugeY),
    Size = new Vector2(gaugeRadius * 2, gaugeRadius * 2),
    Color = new Color(50, 50, 60),
}});

// Progress arc (approximated with a rotated semi-circle)
// Note: For a true arc, you'd need multiple segments
float arcAngle = (float)(-Math.PI / 2 + progress * Math.PI * 2);
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SemiCircle"",
    Position = new Vector2(gaugeX, gaugeY),
    Size = new Vector2(gaugeRadius, gaugeRadius * 2),
    Color = new Color(80, 200, 255),
    RotationOrScale = arcAngle,
}});

// Center value
{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = $""{{(int)(progress * 100)}}%"",
    Position = new Vector2(gaugeX, gaugeY - 14),
    Color = Color.White,
    FontId = ""White"",
    Alignment = TextAlignment.CENTER,
    RotationOrScale = 1.2f,
}});
";
        }

        private static string GenerateThermometer(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float cy = h / 2f;

            return $@"// ═══ Thermometer ═══
// Adjust 'temperature' (0.0 to 1.0) to control fill level
float temperature = 0.6f;
float thermX = {cx:F0}f;
float thermY = {cy:F0}f;
float tubeWidth = 24f;
float tubeHeight = {h * 0.5f:F0}f;
float bulbSize = 40f;

// Tube background
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(thermX, thermY - bulbSize / 2),
    Size = new Vector2(tubeWidth, tubeHeight),
    Color = new Color(50, 50, 60),
}});

// Mercury fill
float fillHeight = tubeHeight * temperature;
float fillY = thermY - bulbSize / 2 + (tubeHeight - fillHeight) / 2;
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(thermX, fillY),
    Size = new Vector2(tubeWidth - 6, fillHeight),
    Color = new Color(255, 100, 80),
}});

// Bulb
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""Circle"",
    Position = new Vector2(thermX, thermY + tubeHeight / 2),
    Size = new Vector2(bulbSize, bulbSize),
    Color = new Color(255, 100, 80),
}});
";
        }

        private static string GenerateBatteryIndicator(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float cy = h / 2f;
            float bodyW = Math.Min(w * 0.5f, 220f);
            float bodyH = 60f;

            return $@"// ═══ Battery Indicator ═══
// Adjust 'charge' (0.0 to 1.0) to control how many cells are lit
float charge = 0.65f;
int totalCells = 5;
int litCells = (int)Math.Round(charge * totalCells);
float bodyX = {cx:F0}f;
float bodyY = {cy:F0}f;
float bodyWidth = {bodyW:F0}f;
float bodyHeight = {bodyH:F0}f;
float capWidth = 10f;
float capHeight = bodyHeight * 0.5f;

// Battery body fill
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(bodyX, bodyY),
    Size = new Vector2(bodyWidth, bodyHeight),
    Color = new Color(30, 30, 35),
}});

// Battery body outline
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareHollow"",
    Position = new Vector2(bodyX, bodyY),
    Size = new Vector2(bodyWidth, bodyHeight),
    Color = new Color(200, 200, 210),
}});

// Battery cap
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(bodyX + bodyWidth / 2 + capWidth / 2, bodyY),
    Size = new Vector2(capWidth, capHeight),
    Color = new Color(200, 200, 210),
}});

// Cells
float pad = 6f;
float cellW = (bodyWidth - pad * (totalCells + 1)) / totalCells;
float cellH = bodyHeight - pad * 2;
float startX = bodyX - bodyWidth / 2 + pad + cellW / 2;
for (int i = 0; i < totalCells; i++)
{{
    bool lit = i < litCells;
    Color cellColor = lit
        ? (charge > 0.5f ? new Color(120, 220, 120)
                         : charge > 0.2f ? new Color(220, 200, 80)
                                         : new Color(220, 80, 80))
        : new Color(60, 60, 70);
    {list}.Add(new MySprite {{
        Type = SpriteType.TEXTURE,
        Data = ""SquareSimple"",
        Position = new Vector2(startX + i * (cellW + pad), bodyY),
        Size = new Vector2(cellW, cellH),
        Color = cellColor,
    }});
}}
";
        }

        private static string GenerateSpeedometer(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float cy = h * 0.6f;
            float radius = Math.Min(w, h) * 0.35f;

            return $@"// ═══ Speedometer ═══
// Adjust 'value' (0.0 to 1.0) to control the needle position
float value = 0.55f;
float dialX = {cx:F0}f;
float dialY = {cy:F0}f;
float dialRadius = {radius:F0}f;

// Outer ring
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""CircleHollow"",
    Position = new Vector2(dialX, dialY),
    Size = new Vector2(dialRadius * 2, dialRadius * 2),
    Color = new Color(40, 40, 50),
}});

// Inner face
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""Circle"",
    Position = new Vector2(dialX, dialY),
    Size = new Vector2(dialRadius * 1.85f, dialRadius * 1.85f),
    Color = new Color(20, 22, 30),
}});

// Needle (rotates from -90deg at value=0 to +90deg at value=1)
float needleAngle = (float)(-Math.PI / 2 + value * Math.PI);
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(dialX + (float)Math.Cos(needleAngle) * dialRadius * 0.4f,
                           dialY + (float)Math.Sin(needleAngle) * dialRadius * 0.4f),
    Size = new Vector2(dialRadius * 0.85f, 4f),
    Color = new Color(255, 180, 60),
    RotationOrScale = needleAngle,
}});

// Center hub
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""Circle"",
    Position = new Vector2(dialX, dialY),
    Size = new Vector2(dialRadius * 0.18f, dialRadius * 0.18f),
    Color = new Color(180, 140, 60),
}});
";
        }

        private static string GenerateCompass(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float cy = h / 2f;
            float radius = Math.Min(w, h) * 0.35f;

            return $@"// ═══ Compass ═══
// Adjust 'headingDeg' (0..360) to rotate the needle
float headingDeg = 45f;
float compassX = {cx:F0}f;
float compassY = {cy:F0}f;
float compassRadius = {radius:F0}f;
float headingRad = (float)(headingDeg * Math.PI / 180.0);

// Outer ring
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""CircleHollow"",
    Position = new Vector2(compassX, compassY),
    Size = new Vector2(compassRadius * 2, compassRadius * 2),
    Color = new Color(120, 160, 200),
}});

// Cardinal labels
{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = ""N"",
    Position = new Vector2(compassX, compassY - compassRadius - 4),
    Color = new Color(220, 220, 240),
    FontId = ""White"",
    Alignment = TextAlignment.CENTER,
    RotationOrScale = 0.9f,
}});
{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = ""S"",
    Position = new Vector2(compassX, compassY + compassRadius - 18),
    Color = new Color(220, 220, 240),
    FontId = ""White"",
    Alignment = TextAlignment.CENTER,
    RotationOrScale = 0.9f,
}});
{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = ""E"",
    Position = new Vector2(compassX + compassRadius - 12, compassY - 14),
    Color = new Color(220, 220, 240),
    FontId = ""White"",
    Alignment = TextAlignment.CENTER,
    RotationOrScale = 0.9f,
}});
{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = ""W"",
    Position = new Vector2(compassX - compassRadius + 12, compassY - 14),
    Color = new Color(220, 220, 240),
    FontId = ""White"",
    Alignment = TextAlignment.CENTER,
    RotationOrScale = 0.9f,
}});

// Heading needle
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""Triangle"",
    Position = new Vector2(compassX + (float)Math.Sin(headingRad) * compassRadius * 0.55f,
                           compassY - (float)Math.Cos(headingRad) * compassRadius * 0.55f),
    Size = new Vector2(20, compassRadius * 0.9f),
    Color = new Color(220, 80, 80),
    RotationOrScale = headingRad,
}});
";
        }

        private static string GenerateTitleSubtitle(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float topY = Math.Min(h * 0.18f, 60f);

            return $@"// ═══ Title + Subtitle ═══
string titleText = ""SYSTEM STATUS"";
string subtitleText = ""All sectors nominal"";
float titleX = {cx:F0}f;
float titleY = {topY:F0}f;

{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = titleText,
    Position = new Vector2(titleX, titleY),
    Color = Color.White,
    FontId = ""White"",
    Alignment = TextAlignment.CENTER,
    RotationOrScale = 1.4f,
}});

{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = subtitleText,
    Position = new Vector2(titleX, titleY + 32),
    Color = new Color(160, 180, 220),
    FontId = ""White"",
    Alignment = TextAlignment.CENTER,
    RotationOrScale = 0.7f,
}});
";
        }

        private static string GenerateDivider(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float cy = h / 2f;
            float lineW = w * 0.8f;

            return $@"// ═══ Divider Line ═══
float lineX = {cx:F0}f;
float lineY = {cy:F0}f;
float lineWidth = {lineW:F0}f;

{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(lineX, lineY),
    Size = new Vector2(lineWidth, 2f),
    Color = new Color(120, 120, 140),
}});
";
        }

        private static string GenerateSidePanel(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float panelW = Math.Min(w * 0.18f, 80f);

            return $@"// ═══ Side Panel ═══
float panelWidth = {panelW:F0}f;
float panelX = panelWidth / 2f;
float panelY = {h / 2f:F0}f;
float panelHeight = {h:F0}f;

{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(panelX, panelY),
    Size = new Vector2(panelWidth, panelHeight),
    Color = new Color(40, 50, 70),
}});
";
        }

        private static string GenerateThreeColumnRow(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cy = h / 2f;
            float colW = w / 3f;

            return $@"// ═══ Three-Column Row ═══
string col1Text = ""Alpha"";
string col2Text = ""Beta"";
string col3Text = ""Gamma"";
float rowY = {cy:F0}f;
float colWidth = {colW:F0}f;

{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = col1Text,
    Position = new Vector2(colWidth * 0.5f, rowY - 12),
    Color = Color.White,
    FontId = ""White"",
    Alignment = TextAlignment.CENTER,
    RotationOrScale = 0.9f,
}});
{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = col2Text,
    Position = new Vector2(colWidth * 1.5f, rowY - 12),
    Color = Color.White,
    FontId = ""White"",
    Alignment = TextAlignment.CENTER,
    RotationOrScale = 0.9f,
}});
{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = col3Text,
    Position = new Vector2(colWidth * 2.5f, rowY - 12),
    Color = Color.White,
    FontId = ""White"",
    Alignment = TextAlignment.CENTER,
    RotationOrScale = 0.9f,
}});
";
        }

        private static string GenerateCornerBrackets(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float armLen = 30f;
            float armT = 4f;

            return $@"// ═══ Corner Brackets ═══
float armLength = {armLen:F0}f;
float armThickness = {armT:F0}f;
float screenW = {w:F0}f;
float screenH = {h:F0}f;
Color bracketColor = new Color(80, 220, 220);

// Top-left
{list}.Add(new MySprite {{ Type = SpriteType.TEXTURE, Data = ""SquareSimple"",
    Position = new Vector2(armLength / 2, armThickness / 2),
    Size = new Vector2(armLength, armThickness), Color = bracketColor }});
{list}.Add(new MySprite {{ Type = SpriteType.TEXTURE, Data = ""SquareSimple"",
    Position = new Vector2(armThickness / 2, armLength / 2),
    Size = new Vector2(armThickness, armLength), Color = bracketColor }});

// Top-right
{list}.Add(new MySprite {{ Type = SpriteType.TEXTURE, Data = ""SquareSimple"",
    Position = new Vector2(screenW - armLength / 2, armThickness / 2),
    Size = new Vector2(armLength, armThickness), Color = bracketColor }});
{list}.Add(new MySprite {{ Type = SpriteType.TEXTURE, Data = ""SquareSimple"",
    Position = new Vector2(screenW - armThickness / 2, armLength / 2),
    Size = new Vector2(armThickness, armLength), Color = bracketColor }});

// Bottom-left
{list}.Add(new MySprite {{ Type = SpriteType.TEXTURE, Data = ""SquareSimple"",
    Position = new Vector2(armLength / 2, screenH - armThickness / 2),
    Size = new Vector2(armLength, armThickness), Color = bracketColor }});
{list}.Add(new MySprite {{ Type = SpriteType.TEXTURE, Data = ""SquareSimple"",
    Position = new Vector2(armThickness / 2, screenH - armLength / 2),
    Size = new Vector2(armThickness, armLength), Color = bracketColor }});

// Bottom-right
{list}.Add(new MySprite {{ Type = SpriteType.TEXTURE, Data = ""SquareSimple"",
    Position = new Vector2(screenW - armLength / 2, screenH - armThickness / 2),
    Size = new Vector2(armLength, armThickness), Color = bracketColor }});
{list}.Add(new MySprite {{ Type = SpriteType.TEXTURE, Data = ""SquareSimple"",
    Position = new Vector2(screenW - armThickness / 2, screenH - armLength / 2),
    Size = new Vector2(armThickness, armLength), Color = bracketColor }});
";
        }

        private static string GenerateBigNumber(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float cy = h / 2f;

            return $@"// ═══ Big Number Display ═══
string bigValue = ""128"";
string caption = ""UNITS"";
float numX = {cx:F0}f;
float numY = {cy:F0}f;

{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = bigValue,
    Position = new Vector2(numX, numY - 50),
    Color = Color.White,
    FontId = ""White"",
    Alignment = TextAlignment.CENTER,
    RotationOrScale = 3.0f,
}});

{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = caption,
    Position = new Vector2(numX, numY + 38),
    Color = new Color(160, 180, 220),
    FontId = ""White"",
    Alignment = TextAlignment.CENTER,
    RotationOrScale = 0.7f,
}});
";
        }

        private static string GenerateLabelValue(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cy = h / 2f;
            float pad = w * 0.1f;

            return $@"// ═══ Label : Value Pair ═══
string labelText = ""SHIELDS"";
string valueText = ""87%"";
float rowY = {cy:F0}f;

{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = labelText,
    Position = new Vector2({pad:F0}f, rowY - 12),
    Color = new Color(160, 180, 220),
    FontId = ""White"",
    Alignment = TextAlignment.LEFT,
    RotationOrScale = 0.9f,
}});

{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = valueText,
    Position = new Vector2({w - pad:F0}f, rowY - 12),
    Color = Color.White,
    FontId = ""White"",
    Alignment = TextAlignment.RIGHT,
    RotationOrScale = 0.9f,
}});
";
        }

        private static string GenerateMultilineText(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float cy = h / 2f;

            return $@"// ═══ Multi-line Text Block ═══
string line1 = ""Connection established."";
string line2 = ""Verifying credentials..."";
string line3 = ""Standby for handshake."";
float blockX = {cx:F0}f;
float blockY = {cy:F0}f;
float lineHeight = 22f;

{list}.Add(new MySprite {{
    Type = SpriteType.TEXT, Data = line1,
    Position = new Vector2(blockX, blockY - lineHeight - 10),
    Color = Color.White, FontId = ""White"",
    Alignment = TextAlignment.CENTER, RotationOrScale = 0.8f,
}});
{list}.Add(new MySprite {{
    Type = SpriteType.TEXT, Data = line2,
    Position = new Vector2(blockX, blockY - 10),
    Color = Color.White, FontId = ""White"",
    Alignment = TextAlignment.CENTER, RotationOrScale = 0.8f,
}});
{list}.Add(new MySprite {{
    Type = SpriteType.TEXT, Data = line3,
    Position = new Vector2(blockX, blockY + lineHeight - 10),
    Color = Color.White, FontId = ""White"",
    Alignment = TextAlignment.CENTER, RotationOrScale = 0.8f,
}});
";
        }

        private static string GenerateCenteredTitle(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float cy = h / 2f;

            return $@"// ═══ Centered Title ═══
string titleText = ""CONTROL CENTER"";
{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = titleText,
    Position = new Vector2({cx:F0}f, {cy:F0}f - 20),
    Color = Color.White,
    FontId = ""White"",
    Alignment = TextAlignment.CENTER,
    RotationOrScale = 1.6f,
}});
";
        }

        private static string GenerateTimestamp(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float pad = 10f;

            return $@"// ═══ Timestamp Display ═══
{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = $""Updated: {{DateTime.Now:HH:mm:ss}}"",
    Position = new Vector2({w - pad:F0}f, {h - pad:F0}f - 18),
    Color = new Color(140, 160, 200),
    FontId = ""White"",
    Alignment = TextAlignment.RIGHT,
    RotationOrScale = 0.6f,
}});
";
        }

        private static string GenerateSolidBackground(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);

            return $@"// ═══ Solid Background ═══
// Place this FIRST so other sprites draw on top.
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2({w / 2f:F0}f, {h / 2f:F0}f),
    Size = new Vector2({w:F0}f, {h:F0}f),
    Color = new Color(20, 24, 36),
}});
";
        }

        private static string GenerateVignette(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float edge = 24f;

            return $@"// ═══ Vignette Border ═══
float edgeSize = {edge:F0}f;
Color edgeColor = new Color(0, 0, 0, 200);

// Top
{list}.Add(new MySprite {{ Type = SpriteType.TEXTURE, Data = ""SquareSimple"",
    Position = new Vector2({w / 2f:F0}f, edgeSize / 2),
    Size = new Vector2({w:F0}f, edgeSize), Color = edgeColor }});
// Bottom
{list}.Add(new MySprite {{ Type = SpriteType.TEXTURE, Data = ""SquareSimple"",
    Position = new Vector2({w / 2f:F0}f, {h:F0}f - edgeSize / 2),
    Size = new Vector2({w:F0}f, edgeSize), Color = edgeColor }});
// Left
{list}.Add(new MySprite {{ Type = SpriteType.TEXTURE, Data = ""SquareSimple"",
    Position = new Vector2(edgeSize / 2, {h / 2f:F0}f),
    Size = new Vector2(edgeSize, {h:F0}f), Color = edgeColor }});
// Right
{list}.Add(new MySprite {{ Type = SpriteType.TEXTURE, Data = ""SquareSimple"",
    Position = new Vector2({w:F0}f - edgeSize / 2, {h / 2f:F0}f),
    Size = new Vector2(edgeSize, {h:F0}f), Color = edgeColor }});
";
        }

        private static string GenerateGridBackground(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);

            return $@"// ═══ Grid Background ═══
float gridSpacing = 48f;
float screenW = {w:F0}f;
float screenH = {h:F0}f;
Color gridColor = new Color(60, 80, 100, 90);

// Vertical lines
for (float x = gridSpacing; x < screenW; x += gridSpacing)
{{
    {list}.Add(new MySprite {{
        Type = SpriteType.TEXTURE,
        Data = ""SquareSimple"",
        Position = new Vector2(x, screenH / 2),
        Size = new Vector2(1f, screenH),
        Color = gridColor,
    }});
}}

// Horizontal lines
for (float y = gridSpacing; y < screenH; y += gridSpacing)
{{
    {list}.Add(new MySprite {{
        Type = SpriteType.TEXTURE,
        Data = ""SquareSimple"",
        Position = new Vector2(screenW / 2, y),
        Size = new Vector2(screenW, 1f),
        Color = gridColor,
    }});
}}
";
        }

        private static string GenerateDiagonalStripes(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);

            return $@"// ═══ Diagonal Stripes ═══
float stripeWidth = 24f;
float stripeSpacing = 48f;
float screenW = {w:F0}f;
float screenH = {h:F0}f;
float diag = (float)Math.Sqrt(screenW * screenW + screenH * screenH);
float angle = (float)(Math.PI / 4);  // 45 degrees
Color stripeColor = new Color(255, 200, 60, 160);

int count = (int)Math.Ceiling(diag / stripeSpacing) + 2;
float startOffset = -diag / 2f;
for (int i = 0; i < count; i++)
{{
    float offset = startOffset + i * stripeSpacing;
    float px = screenW / 2f + (float)Math.Cos(angle + Math.PI / 2) * offset;
    float py = screenH / 2f + (float)Math.Sin(angle + Math.PI / 2) * offset;
    {list}.Add(new MySprite {{
        Type = SpriteType.TEXTURE,
        Data = ""SquareSimple"",
        Position = new Vector2(px, py),
        Size = new Vector2(stripeWidth, diag),
        Color = stripeColor,
        RotationOrScale = angle,
    }});
}}
";
        }

        private static string GenerateCrosshair(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float cy = h / 2f;
            float armLen = Math.Min(w, h) * 0.06f;
            float gap = 6f;

            return $@"// ═══ Crosshair ═══
float cx = {cx:F0}f;
float cy = {cy:F0}f;
float armLen = {armLen:F0}f;
float gap = {gap:F0}f;
Color reticleColor = new Color(120, 220, 220);

// Left arm
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(cx - gap - armLen / 2f, cy),
    Size = new Vector2(armLen, 2f),
    Color = reticleColor,
}});

// Right arm
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(cx + gap + armLen / 2f, cy),
    Size = new Vector2(armLen, 2f),
    Color = reticleColor,
}});

// Top arm
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(cx, cy - gap - armLen / 2f),
    Size = new Vector2(2f, armLen),
    Color = reticleColor,
}});

// Bottom arm
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(cx, cy + gap + armLen / 2f),
    Size = new Vector2(2f, armLen),
    Color = reticleColor,
}});
";
        }

        private static string GenerateTargetingReticle(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float cx = w / 2f;
            float cy = h / 2f;
            float ringSize = Math.Min(w, h) * 0.25f;
            float tickLen = ringSize * 0.18f;

            return $@"// ═══ Targeting Reticle ═══
float cx = {cx:F0}f;
float cy = {cy:F0}f;
float ringSize = {ringSize:F0}f;
float tickLen = {tickLen:F0}f;
Color targetColor = new Color(255, 80, 80);

// Outer ring
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""CircleHollow"",
    Position = new Vector2(cx, cy),
    Size = new Vector2(ringSize, ringSize),
    Color = targetColor,
}});

// Top tick
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(cx, cy - ringSize / 2f - tickLen / 2f),
    Size = new Vector2(2f, tickLen),
    Color = targetColor,
}});

// Bottom tick
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(cx, cy + ringSize / 2f + tickLen / 2f),
    Size = new Vector2(2f, tickLen),
    Color = targetColor,
}});

// Left tick
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(cx - ringSize / 2f - tickLen / 2f, cy),
    Size = new Vector2(tickLen, 2f),
    Color = targetColor,
}});

// Right tick
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(cx + ringSize / 2f + tickLen / 2f, cy),
    Size = new Vector2(tickLen, 2f),
    Color = targetColor,
}});
";
        }

        private static string GenerateMinimapFrame(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float size = Math.Min(w, h) * 0.28f;
            float pad = 12f;
            float cx = w - size / 2f - pad;
            float cy = size / 2f + pad;

            return $@"// ═══ Minimap Frame ═══
float mmCx = {cx:F0}f;
float mmCy = {cy:F0}f;
float mmSize = {size:F0}f;
Color frameColor = new Color(80, 200, 120);
Color bgColor = new Color(10, 20, 14, 180);

// Background fill
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(mmCx, mmCy),
    Size = new Vector2(mmSize, mmSize),
    Color = bgColor,
}});

// Frame outline
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareHollow"",
    Position = new Vector2(mmCx, mmCy),
    Size = new Vector2(mmSize, mmSize),
    Color = frameColor,
}});

// Horizontal crosshair
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(mmCx, mmCy),
    Size = new Vector2(mmSize, 1f),
    Color = new Color(80, 200, 120, 120),
}});

// Vertical crosshair
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(mmCx, mmCy),
    Size = new Vector2(1f, mmSize),
    Color = new Color(80, 200, 120, 120),
}});

// Center dot (player marker)
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""Circle"",
    Position = new Vector2(mmCx, mmCy),
    Size = new Vector2(6f, 6f),
    Color = Color.White,
}});
";
        }

        private static string GenerateNotificationToast(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float toastW = Math.Min(w * 0.6f, 360f);
            float toastH = 44f;
            float cx = w / 2f;
            float cy = toastH / 2f + 16f;

            return $@"// ═══ Notification Toast ═══
float toastCx = {cx:F0}f;
float toastCy = {cy:F0}f;
float toastW = {toastW:F0}f;
float toastH = {toastH:F0}f;

// Background pill
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(toastCx, toastCy),
    Size = new Vector2(toastW, toastH),
    Color = new Color(30, 40, 56, 220),
}});

// Accent stripe (left edge)
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(toastCx - toastW / 2f + 4f, toastCy),
    Size = new Vector2(6f, toastH),
    Color = new Color(120, 200, 255),
}});

// Message text
{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = ""System ready"",
    Position = new Vector2(toastCx, toastCy - 12f),
    Color = Color.White,
    FontId = ""White"",
    Alignment = TextAlignment.CENTER,
    RotationOrScale = 0.9f,
}});
";
        }

        private static string GenerateKpiTile(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float tileW = Math.Min(w * 0.5f, 220f);
            float tileH = Math.Min(h * 0.4f, 140f);
            float cx = w / 2f;
            float cy = h / 2f;

            return $@"// ═══ KPI Tile ═══
float kpiCx = {cx:F0}f;
float kpiCy = {cy:F0}f;
float kpiW = {tileW:F0}f;
float kpiH = {tileH:F0}f;
float progress = 0.65f; // 0..1

// Tile background
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(kpiCx, kpiCy),
    Size = new Vector2(kpiW, kpiH),
    Color = new Color(28, 36, 48),
}});

// Title
{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = ""POWER"",
    Position = new Vector2(kpiCx, kpiCy - kpiH / 2f + 6f),
    Color = new Color(160, 200, 240),
    FontId = ""White"",
    Alignment = TextAlignment.CENTER,
    RotationOrScale = 0.8f,
}});

// Big value
{list}.Add(new MySprite {{
    Type = SpriteType.TEXT,
    Data = ""65%"",
    Position = new Vector2(kpiCx, kpiCy - 24f),
    Color = Color.White,
    FontId = ""White"",
    Alignment = TextAlignment.CENTER,
    RotationOrScale = 1.8f,
}});

// Status bar background
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(kpiCx, kpiCy + kpiH / 2f - 14f),
    Size = new Vector2(kpiW - 24f, 8f),
    Color = new Color(50, 60, 78),
}});

// Status bar fill
float kpiFillW = (kpiW - 24f) * progress;
{list}.Add(new MySprite {{
    Type = SpriteType.TEXTURE,
    Data = ""SquareSimple"",
    Position = new Vector2(kpiCx - (kpiW - 24f) / 2f + kpiFillW / 2f, kpiCy + kpiH / 2f - 14f),
    Size = new Vector2(kpiFillW, 8f),
    Color = new Color(100, 200, 255),
}});
";
        }

        private static string GenerateButtonRow(float w, float h, TargetScriptType target)
        {
            string list = GetListVar(target);
            float btnW = (w * 0.9f - 24f) / 3f;
            float btnH = 56f;
            float cy = h - btnH / 2f - 16f;
            float startX = w * 0.05f + btnW / 2f;
            float gap = 12f;

            return $@"// ═══ Button Row ═══
float btnW = {btnW:F0}f;
float btnH = {btnH:F0}f;
float btnCy = {cy:F0}f;
float btnStartX = {startX:F0}f;
float btnGap = {gap:F0}f;
string[] btnLabels = new[] {{ ""STATUS"", ""POWER"", ""ALERTS"" }};
Color btnBg = new Color(40, 60, 84);

for (int i = 0; i < 3; i++)
{{
    float bx = btnStartX + i * (btnW + btnGap);

    {list}.Add(new MySprite {{
        Type = SpriteType.TEXTURE,
        Data = ""SquareSimple"",
        Position = new Vector2(bx, btnCy),
        Size = new Vector2(btnW, btnH),
        Color = btnBg,
    }});

    {list}.Add(new MySprite {{
        Type = SpriteType.TEXT,
        Data = btnLabels[i],
        Position = new Vector2(bx, btnCy - 12f),
        Color = Color.White,
        FontId = ""White"",
        Alignment = TextAlignment.CENTER,
        RotationOrScale = 0.9f,
    }});
}}
";
        }
    }
}
