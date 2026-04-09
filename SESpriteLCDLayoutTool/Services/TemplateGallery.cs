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
    }
}
