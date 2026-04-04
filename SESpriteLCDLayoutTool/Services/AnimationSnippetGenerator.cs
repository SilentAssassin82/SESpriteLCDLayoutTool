using System;
using System.Text;
using SESpriteLCDLayoutTool.Models;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>Supported animation effect types for code snippet generation.</summary>
    public enum AnimationType
    {
        Rotate,
        Oscillate,
        Pulse,
        Fade,
        Blink,
        ColorCycle,
    }

    /// <summary>Axis for oscillation movement.</summary>
    public enum OscillateAxis { X, Y, Both }

    /// <summary>
    /// Generates ready-to-paste C# animation code snippets for SE LCD sprites.
    /// Each snippet includes a tick counter field, animation math, and the full
    /// sprite definition with the animated property highlighted.
    /// </summary>
    public static class AnimationSnippetGenerator
    {
        // ── Main entry point ────────────────────────────────────────────────────

        /// <summary>
        /// Generates a complete animation code snippet for the given sprite and animation type.
        /// </summary>
        public static string Generate(SpriteEntry sprite, AnimationType type, AnimationParams p)
        {
            switch (type)
            {
                case AnimationType.Rotate:     return GenerateRotate(sprite, p);
                case AnimationType.Oscillate:  return GenerateOscillate(sprite, p);
                case AnimationType.Pulse:      return GeneratePulse(sprite, p);
                case AnimationType.Fade:       return GenerateFade(sprite, p);
                case AnimationType.Blink:      return GenerateBlink(sprite, p);
                case AnimationType.ColorCycle:  return GenerateColorCycle(sprite, p);
                default: return "// Unknown animation type";
            }
        }

        // ── Parameter container ─────────────────────────────────────────────────

        public class AnimationParams
        {
            // Rotate
            public float RotateSpeed { get; set; } = 0.05f;
            public bool Clockwise { get; set; } = true;

            // Oscillate
            public OscillateAxis Axis { get; set; } = OscillateAxis.X;
            public float OscillateAmplitude { get; set; } = 50f;
            public float OscillateSpeed { get; set; } = 0.05f;

            // Pulse
            public float PulseAmplitude { get; set; } = 0.3f;
            public float PulseSpeed { get; set; } = 0.08f;

            // Fade
            public int FadeMinAlpha { get; set; } = 0;
            public int FadeMaxAlpha { get; set; } = 255;
            public float FadeSpeed { get; set; } = 0.06f;

            // Blink
            public int BlinkOnTicks { get; set; } = 30;
            public int BlinkOffTicks { get; set; } = 30;

            // Color Cycle
            public float CycleSpeed { get; set; } = 2f;
            public float CycleBrightness { get; set; } = 1.0f;
        }

        // ── Generators ──────────────────────────────────────────────────────────

        private static string GenerateRotate(SpriteEntry sp, AnimationParams p)
        {
            string dir = p.Clockwise ? "" : "-";
            var sb = new StringBuilder();
            sb.AppendLine($"// ─── Animation: Rotate \"{SpriteName(sp)}\" ───");
            sb.AppendLine($"// Speed: {p.RotateSpeed}f rad/tick  |  Direction: {(p.Clockwise ? "Clockwise" : "Counter-clockwise")}");
            sb.AppendLine();
            sb.AppendLine("// Field — add to your class:");
            sb.AppendLine("int _tick = 0;");
            sb.AppendLine();
            sb.AppendLine("// In your render method:");
            sb.AppendLine("_tick++;");
            sb.AppendLine();
            AppendSpriteBlock(sb, sp, $"{dir}_tick * {p.RotateSpeed}f", animatedProp: "RotationOrScale");
            return sb.ToString();
        }

        private static string GenerateOscillate(SpriteEntry sp, AnimationParams p)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// ─── Animation: Oscillate \"{SpriteName(sp)}\" ───");
            sb.AppendLine($"// Axis: {p.Axis}  |  Amplitude: {p.OscillateAmplitude}f px  |  Speed: {p.OscillateSpeed}f");
            sb.AppendLine();
            sb.AppendLine("// Field — add to your class:");
            sb.AppendLine("int _tick = 0;");
            sb.AppendLine();
            sb.AppendLine("// In your render method:");
            sb.AppendLine("_tick++;");
            sb.AppendLine($"float oscOffset = (float)Math.Sin(_tick * {p.OscillateSpeed}f) * {p.OscillateAmplitude}f;");
            sb.AppendLine();

            string posExpr;
            switch (p.Axis)
            {
                case OscillateAxis.X:
                    posExpr = $"new Vector2({sp.X:F1}f + oscOffset, {sp.Y:F1}f)";
                    break;
                case OscillateAxis.Y:
                    posExpr = $"new Vector2({sp.X:F1}f, {sp.Y:F1}f + oscOffset)";
                    break;
                default: // Both
                    posExpr = $"new Vector2({sp.X:F1}f + oscOffset, {sp.Y:F1}f + oscOffset)";
                    break;
            }

            AppendSpriteBlock(sb, sp, posExpr, animatedProp: "Position");
            return sb.ToString();
        }

        private static string GeneratePulse(SpriteEntry sp, AnimationParams p)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// ─── Animation: Pulse (Scale) \"{SpriteName(sp)}\" ───");
            sb.AppendLine($"// Amplitude: ±{p.PulseAmplitude}f  |  Speed: {p.PulseSpeed}f");
            sb.AppendLine();
            sb.AppendLine("// Field — add to your class:");
            sb.AppendLine("int _tick = 0;");
            sb.AppendLine();
            sb.AppendLine("// In your render method:");
            sb.AppendLine("_tick++;");
            sb.AppendLine($"float pulseScale = 1f + (float)Math.Sin(_tick * {p.PulseSpeed}f) * {p.PulseAmplitude}f;");
            sb.AppendLine();

            string sizeExpr = $"new Vector2({sp.Width:F1}f * pulseScale, {sp.Height:F1}f * pulseScale)";
            AppendSpriteBlock(sb, sp, sizeExpr, animatedProp: "Size");
            return sb.ToString();
        }

        private static string GenerateFade(SpriteEntry sp, AnimationParams p)
        {
            int range = p.FadeMaxAlpha - p.FadeMinAlpha;
            int mid   = p.FadeMinAlpha + range / 2;
            int half  = range / 2;

            var sb = new StringBuilder();
            sb.AppendLine($"// ─── Animation: Fade \"{SpriteName(sp)}\" ───");
            sb.AppendLine($"// Alpha: {p.FadeMinAlpha}–{p.FadeMaxAlpha}  |  Speed: {p.FadeSpeed}f");
            sb.AppendLine();
            sb.AppendLine("// Field — add to your class:");
            sb.AppendLine("int _tick = 0;");
            sb.AppendLine();
            sb.AppendLine("// In your render method:");
            sb.AppendLine("_tick++;");
            sb.AppendLine($"byte fadeAlpha = (byte)({mid} + (int)(Math.Sin(_tick * {p.FadeSpeed}f) * {half}));");
            sb.AppendLine();

            string colorExpr = $"new Color({sp.ColorR}, {sp.ColorG}, {sp.ColorB}, fadeAlpha)";
            AppendSpriteBlock(sb, sp, colorExpr, animatedProp: "Color");
            return sb.ToString();
        }

        private static string GenerateBlink(SpriteEntry sp, AnimationParams p)
        {
            int period = p.BlinkOnTicks + p.BlinkOffTicks;
            var sb = new StringBuilder();
            sb.AppendLine($"// ─── Animation: Blink \"{SpriteName(sp)}\" ───");
            sb.AppendLine($"// On: {p.BlinkOnTicks} ticks  |  Off: {p.BlinkOffTicks} ticks  (period: {period})");
            sb.AppendLine();
            sb.AppendLine("// Field — add to your class:");
            sb.AppendLine("int _tick = 0;");
            sb.AppendLine();
            sb.AppendLine("// In your render method:");
            sb.AppendLine("_tick++;");
            sb.AppendLine($"bool blinkVisible = (_tick % {period}) < {p.BlinkOnTicks};");
            sb.AppendLine();
            sb.AppendLine("if (blinkVisible)");
            sb.AppendLine("{");

            AppendSpriteBlock(sb, sp, null, animatedProp: null, indent: "    ");

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string GenerateColorCycle(SpriteEntry sp, AnimationParams p)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// ─── Animation: Color Cycle \"{SpriteName(sp)}\" ───");
            sb.AppendLine($"// Speed: {p.CycleSpeed}f °/tick  |  Brightness: {p.CycleBrightness}f");
            sb.AppendLine();
            sb.AppendLine("// Field — add to your class:");
            sb.AppendLine("int _tick = 0;");
            sb.AppendLine();
            sb.AppendLine("// In your render method:");
            sb.AppendLine("_tick++;");
            sb.AppendLine($"float hue = (_tick * {p.CycleSpeed}f) % 360f;");
            sb.AppendLine("// ── HSV → RGB (S=1, V=brightness) ──");
            sb.AppendLine($"float br = {p.CycleBrightness}f;");
            sb.AppendLine("float hSector = hue / 60f;");
            sb.AppendLine("int hi = (int)Math.Floor(hSector) % 6;");
            sb.AppendLine("float f = hSector - (float)Math.Floor(hSector);");
            sb.AppendLine("float q = br * (1f - f);");
            sb.AppendLine("float t = br * f;");
            sb.AppendLine("float cr, cg, cb;");
            sb.AppendLine("switch (hi)");
            sb.AppendLine("{");
            sb.AppendLine("    case 0: cr = br; cg = t;  cb = 0;  break;");
            sb.AppendLine("    case 1: cr = q;  cg = br; cb = 0;  break;");
            sb.AppendLine("    case 2: cr = 0;  cg = br; cb = t;  break;");
            sb.AppendLine("    case 3: cr = 0;  cg = q;  cb = br; break;");
            sb.AppendLine("    case 4: cr = t;  cg = 0;  cb = br; break;");
            sb.AppendLine("    default: cr = br; cg = 0;  cb = q;  break;");
            sb.AppendLine("}");
            sb.AppendLine("Color cycleColor = new Color((int)(cr * 255), (int)(cg * 255), (int)(cb * 255), 255);");
            sb.AppendLine();

            AppendSpriteBlock(sb, sp, "cycleColor", animatedProp: "Color");
            return sb.ToString();
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static void AppendSpriteBlock(StringBuilder sb, SpriteEntry sp,
            string animatedValue, string animatedProp, string indent = "")
        {
            bool isText = sp.Type == SpriteEntryType.Text;

            sb.AppendLine($"{indent}frame.Add(new MySprite");
            sb.AppendLine($"{indent}{{");

            if (isText)
            {
                sb.AppendLine($"{indent}    Type           = SpriteType.TEXT,");
                sb.AppendLine($"{indent}    Data           = \"{Esc(sp.Text)}\",");
                AppendProp(sb, indent, "Position", $"new Vector2({sp.X:F1}f, {sp.Y:F1}f)", animatedProp, animatedValue);
                AppendProp(sb, indent, "Color", $"new Color({sp.ColorR}, {sp.ColorG}, {sp.ColorB}, {sp.ColorA})", animatedProp, animatedValue);
                sb.AppendLine($"{indent}    FontId         = \"{Esc(sp.FontId)}\",");
                sb.AppendLine($"{indent}    Alignment      = TextAlignment.{sp.Alignment.ToString().ToUpperInvariant()},");
                AppendProp(sb, indent, "RotationOrScale", $"{sp.Scale:F2}f", animatedProp, animatedValue);
            }
            else
            {
                sb.AppendLine($"{indent}    Type           = SpriteType.TEXTURE,");
                sb.AppendLine($"{indent}    Data           = \"{Esc(sp.SpriteName)}\",");
                AppendProp(sb, indent, "Position", $"new Vector2({sp.X:F1}f, {sp.Y:F1}f)", animatedProp, animatedValue);
                AppendProp(sb, indent, "Size", $"new Vector2({sp.Width:F1}f, {sp.Height:F1}f)", animatedProp, animatedValue);
                AppendProp(sb, indent, "Color", $"new Color({sp.ColorR}, {sp.ColorG}, {sp.ColorB}, {sp.ColorA})", animatedProp, animatedValue);
                sb.AppendLine($"{indent}    Alignment      = TextAlignment.CENTER,");
                AppendProp(sb, indent, "RotationOrScale", $"{sp.Rotation:F4}f", animatedProp, animatedValue);
            }

            sb.AppendLine($"{indent}}});");
        }

        private static void AppendProp(StringBuilder sb, string indent, string prop,
            string defaultValue, string animatedProp, string animatedValue)
        {
            bool isAnimated = string.Equals(prop, animatedProp, StringComparison.OrdinalIgnoreCase);
            string value = isAnimated ? animatedValue : defaultValue;
            string marker = isAnimated ? "  // ← animated" : "";

            // Pad prop name for alignment
            string padded = (prop + "  ").PadRight(15);
            sb.AppendLine($"{indent}    {padded}= {value},{marker}");
        }

        private static string SpriteName(SpriteEntry sp)
        {
            return sp.Type == SpriteEntryType.Text ? sp.Text : sp.SpriteName;
        }

        private static string Esc(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
