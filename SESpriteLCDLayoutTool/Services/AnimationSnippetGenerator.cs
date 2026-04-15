using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
        Keyframed,
    }

    /// <summary>Axis for oscillation movement.</summary>
    public enum OscillateAxis { X, Y, Both }

    /// <summary>Easing function for keyframe interpolation.</summary>
    public enum EasingType
    {
        Linear,
        SineInOut,
        EaseIn,
        EaseOut,
        EaseInOut,
        Bounce,
        Elastic,
    }

    /// <summary>Loop behaviour for keyframe animations.</summary>
    public enum LoopMode { Once, Loop, PingPong }

    /// <summary>Target script type for context-aware animation snippet generation.</summary>
    public enum TargetScriptType
    {
        /// <summary>LCD Helper — standalone render methods with List&lt;MySprite&gt; parameter.</summary>
        LcdHelper,
        /// <summary>Programmable Block — full PB script extending MyGridProgram.</summary>
        ProgrammableBlock,
        /// <summary>Mod — session component or drawing class.</summary>
        Mod,
        /// <summary>Torch / SE Plugin.</summary>
        Plugin,
        /// <summary>Pulsar client-side plugin implementing IPlugin.</summary>
        Pulsar,
    }

    /// <summary>
    /// A single keyframe capturing sprite state at a specific tick.
    /// Only properties with non-null values are interpolated.
    /// </summary>
    public class Keyframe
    {
        public int Tick { get; set; }
        public float? X { get; set; }
        public float? Y { get; set; }
        public float? Width { get; set; }
        public float? Height { get; set; }
        public int? ColorR { get; set; }
        public int? ColorG { get; set; }
        public int? ColorB { get; set; }
        public int? ColorA { get; set; }
        public float? Rotation { get; set; }
        public float? Scale { get; set; }
        public EasingType EasingToNext { get; set; } = EasingType.Linear;

        /// <summary>Creates a keyframe from the current sprite state.</summary>
        public static Keyframe FromSprite(SpriteEntry sp, int tick)
        {
            return new Keyframe
            {
                Tick = tick,
                X = sp.X,
                Y = sp.Y,
                Width = sp.Width,
                Height = sp.Height,
                ColorR = sp.ColorR,
                ColorG = sp.ColorG,
                ColorB = sp.ColorB,
                ColorA = sp.ColorA,
                Rotation = sp.Type == SpriteEntryType.Text ? (float?)null : sp.Rotation,
                Scale = sp.Type == SpriteEntryType.Text ? sp.Scale : (float?)null,
            };
        }

        /// <summary>Returns a short summary for UI display.</summary>
        public string Summary
        {
            get
            {
                var parts = new List<string>();
                if (X.HasValue || Y.HasValue) parts.Add($"Pos({X ?? 0:F0},{Y ?? 0:F0})");
                if (Width.HasValue || Height.HasValue) parts.Add($"Size({Width ?? 0:F0},{Height ?? 0:F0})");
                if (ColorR.HasValue) parts.Add($"RGBA({ColorR},{ColorG},{ColorB},{ColorA})");
                if (Rotation.HasValue) parts.Add($"Rot({Rotation:F2})");
                if (Scale.HasValue) parts.Add($"Scl({Scale:F2})");
                return parts.Count > 0 ? string.Join(" ", parts) : "(empty)";
            }
        }
    }

    /// <summary>Parameters for a keyframe-based animation.</summary>
    public class KeyframeAnimationParams
    {
        public string ListVarName { get; set; } = "sprites";
        public LoopMode Loop { get; set; } = LoopMode.Loop;
        public TargetScriptType TargetScript { get; set; } = TargetScriptType.LcdHelper;
        public List<Keyframe> Keyframes { get; set; } = new List<Keyframe>();
    }

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
                case AnimationType.Keyframed:  return "// Use GenerateKeyframed() for keyframe animations";
                default: return "// Unknown animation type";
            }
        }

        // ── Parameter container ─────────────────────────────────────────────────

        public class AnimationParams
        {
            // Shared
            /// <summary>
            /// Variable name for the sprite list: "sprites" for List&lt;MySprite&gt;
            /// helper methods, "frame" for MySpriteDrawFrame (PB/Mod).
            /// </summary>
            public string ListVarName { get; set; } = "sprites";

            /// <summary>Target script type for context-aware comments.</summary>
            public TargetScriptType TargetScript { get; set; } = TargetScriptType.LcdHelper;

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
            sb.AppendLine($"// ─── Animation: Rotate \"{SpriteName(sp)}\" [{TargetLabel(p.TargetScript)}] ───");
            sb.AppendLine($"// Speed: {p.RotateSpeed}f rad/tick  |  Direction: {(p.Clockwise ? "Clockwise" : "Counter-clockwise")}");
            sb.AppendLine();
            sb.AppendLine(FieldHint(p.TargetScript));
            sb.AppendLine("int _tick = 0;");
            sb.AppendLine();
            sb.AppendLine(RenderHint(p.TargetScript));
            sb.AppendLine("_tick++;");
            sb.AppendLine();
            AppendSpriteBlock(sb, sp, $"{dir}_tick * {p.RotateSpeed}f", animatedProp: "RotationOrScale", listVar: p.ListVarName);
            return sb.ToString();
        }

        private static string GenerateOscillate(SpriteEntry sp, AnimationParams p)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// ─── Animation: Oscillate \"{SpriteName(sp)}\" [{TargetLabel(p.TargetScript)}] ───");
            sb.AppendLine($"// Axis: {p.Axis}  |  Amplitude: {p.OscillateAmplitude}f px  |  Speed: {p.OscillateSpeed}f");
            sb.AppendLine();
            sb.AppendLine(FieldHint(p.TargetScript));
            sb.AppendLine("int _tick = 0;");
            sb.AppendLine();
            sb.AppendLine(RenderHint(p.TargetScript));
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

            AppendSpriteBlock(sb, sp, posExpr, animatedProp: "Position", listVar: p.ListVarName);
            return sb.ToString();
        }

        private static string GeneratePulse(SpriteEntry sp, AnimationParams p)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// ─── Animation: Pulse (Scale) \"{SpriteName(sp)}\" [{TargetLabel(p.TargetScript)}] ───");
            sb.AppendLine($"// Amplitude: ±{p.PulseAmplitude}f  |  Speed: {p.PulseSpeed}f");
            sb.AppendLine();
            sb.AppendLine(FieldHint(p.TargetScript));
            sb.AppendLine("int _tick = 0;");
            sb.AppendLine();
            sb.AppendLine(RenderHint(p.TargetScript));
            sb.AppendLine("_tick++;");
            sb.AppendLine($"float pulseScale = 1f + (float)Math.Sin(_tick * {p.PulseSpeed}f) * {p.PulseAmplitude}f;");
            sb.AppendLine();

            string sizeExpr = $"new Vector2({sp.Width:F1}f * pulseScale, {sp.Height:F1}f * pulseScale)";
            AppendSpriteBlock(sb, sp, sizeExpr, animatedProp: "Size", listVar: p.ListVarName);
            return sb.ToString();
        }

        private static string GenerateFade(SpriteEntry sp, AnimationParams p)
        {
            int range = p.FadeMaxAlpha - p.FadeMinAlpha;
            int mid   = p.FadeMinAlpha + range / 2;
            int half  = range / 2;

            var sb = new StringBuilder();
            sb.AppendLine($"// ─── Animation: Fade \"{SpriteName(sp)}\" [{TargetLabel(p.TargetScript)}] ───");
            sb.AppendLine($"// Alpha: {p.FadeMinAlpha}–{p.FadeMaxAlpha}  |  Speed: {p.FadeSpeed}f");
            sb.AppendLine();
            sb.AppendLine(FieldHint(p.TargetScript));
            sb.AppendLine("int _tick = 0;");
            sb.AppendLine();
            sb.AppendLine(RenderHint(p.TargetScript));
            sb.AppendLine("_tick++;");
            sb.AppendLine($"byte fadeAlpha = (byte)({mid} + (int)(Math.Sin(_tick * {p.FadeSpeed}f) * {half}));");
            sb.AppendLine();

            string colorExpr = $"new Color({sp.ColorR}, {sp.ColorG}, {sp.ColorB}, fadeAlpha)";
            AppendSpriteBlock(sb, sp, colorExpr, animatedProp: "Color", listVar: p.ListVarName);
            return sb.ToString();
        }

        private static string GenerateBlink(SpriteEntry sp, AnimationParams p)
        {
            int period = p.BlinkOnTicks + p.BlinkOffTicks;
            var sb = new StringBuilder();
            sb.AppendLine($"// ─── Animation: Blink \"{SpriteName(sp)}\" [{TargetLabel(p.TargetScript)}] ───");
            sb.AppendLine($"// On: {p.BlinkOnTicks} ticks  |  Off: {p.BlinkOffTicks} ticks  (period: {period})");
            sb.AppendLine();
            sb.AppendLine(FieldHint(p.TargetScript));
            sb.AppendLine("int _tick = 0;");
            sb.AppendLine();
            sb.AppendLine(RenderHint(p.TargetScript));
            sb.AppendLine("_tick++;");
            sb.AppendLine($"bool blinkVisible = (_tick % {period}) < {p.BlinkOnTicks};");
            sb.AppendLine();
            sb.AppendLine("if (blinkVisible)");
            sb.AppendLine("{");

            AppendSpriteBlock(sb, sp, null, animatedProp: null, indent: "    ", listVar: p.ListVarName);

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string GenerateColorCycle(SpriteEntry sp, AnimationParams p)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// ─── Animation: Color Cycle \"{SpriteName(sp)}\" [{TargetLabel(p.TargetScript)}] ───");
            sb.AppendLine($"// Speed: {p.CycleSpeed}f °/tick  |  Brightness: {p.CycleBrightness}f");
            sb.AppendLine();
            sb.AppendLine(FieldHint(p.TargetScript));
            sb.AppendLine("int _tick = 0;");
            sb.AppendLine();
            sb.AppendLine(RenderHint(p.TargetScript));
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

            AppendSpriteBlock(sb, sp, "cycleColor", animatedProp: "Color", listVar: p.ListVarName);
            return sb.ToString();
        }

        // ── Keyframe animation ────────────────────────────────────────────────

        /// <summary>
        /// Generates a complete keyframe-interpolated animation snippet.
        /// Produces a self-contained C# block with easing helpers, keyframe array,
        /// and the sprite add call with all animated properties.
        /// </summary>
        public static string GenerateKeyframed(SpriteEntry sprite, KeyframeAnimationParams kp)
        {
            if (kp.Keyframes == null || kp.Keyframes.Count < 2)
                return "// Need at least 2 keyframes for an animation";

            var frames = kp.Keyframes.OrderBy(k => k.Tick).ToList();
            int totalTicks = frames.Last().Tick;
            if (totalTicks <= 0) totalTicks = 1;

            // Detect which properties are animated (differ across keyframes)
            bool animPos    = HasVariation(frames, k => k.X) || HasVariation(frames, k => k.Y);
            bool animSize   = HasVariation(frames, k => k.Width) || HasVariation(frames, k => k.Height);
            bool animColor  = HasVariation(frames, k => k.ColorR) || HasVariation(frames, k => k.ColorG)
                           || HasVariation(frames, k => k.ColorB) || HasVariation(frames, k => k.ColorA);
            bool animRot    = HasVariation(frames, k => k.Rotation);
            bool animScale  = HasVariation(frames, k => k.Scale);
            bool isText     = sprite.Type == SpriteEntryType.Text;
            string alignStr = "TextAlignment." + sprite.Alignment.ToString().ToUpperInvariant();

            var sb = new StringBuilder();
            sb.AppendLine($"// ─── Keyframe Animation: \"{SpriteName(sprite)}\" [{TargetLabel(kp.TargetScript)}] ───");
            sb.AppendLine($"// {frames.Count} keyframes over {totalTicks} ticks  |  Loop: {kp.Loop}");
            sb.AppendLine();

            // ── Easing helper ──
            var usedEasings = new HashSet<EasingType>(frames.Select(k => k.EasingToNext));
            sb.AppendLine("// ── Easing helper ──");
            sb.AppendLine("float Ease(float t, int easeType)");
            sb.AppendLine("{");
            sb.AppendLine("    switch (easeType)");
            sb.AppendLine("    {");
            sb.AppendLine("        case 0: return t; // Linear");
            sb.AppendLine("        case 1: return (float)(0.5 - 0.5 * Math.Cos(t * Math.PI)); // SineInOut");
            sb.AppendLine("        case 2: return t * t; // EaseIn");
            sb.AppendLine("        case 3: return 1f - (1f - t) * (1f - t); // EaseOut");
            sb.AppendLine("        case 4: return t < 0.5f ? 2f * t * t : 1f - (float)Math.Pow(-2 * t + 2, 2) / 2f; // EaseInOut");
            sb.AppendLine("        case 5: // Bounce");
            sb.AppendLine("        {");
            sb.AppendLine("            float b = 1f - t;");
            sb.AppendLine("            if (b < 1f / 2.75f) return 1f - 7.5625f * b * b;");
            sb.AppendLine("            if (b < 2f / 2.75f) { b -= 1.5f / 2.75f; return 1f - (7.5625f * b * b + 0.75f); }");
            sb.AppendLine("            if (b < 2.5f / 2.75f) { b -= 2.25f / 2.75f; return 1f - (7.5625f * b * b + 0.9375f); }");
            sb.AppendLine("            b -= 2.625f / 2.75f; return 1f - (7.5625f * b * b + 0.984375f);");
            sb.AppendLine("        }");
            sb.AppendLine("        case 6: // Elastic");
            sb.AppendLine("        {");
            sb.AppendLine("            if (t <= 0f) return 0f;");
            sb.AppendLine("            if (t >= 1f) return 1f;");
            sb.AppendLine("            return (float)(-Math.Pow(2, 10 * t - 10) * Math.Sin((t * 10 - 10.75) * (2 * Math.PI / 3)));");
            sb.AppendLine("        }");
            sb.AppendLine("        default: return t;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();

            // ── Keyframe data arrays ──
            sb.AppendLine("// ── Keyframe data ──");
            sb.AppendLine(FieldHint(kp.TargetScript));
            sb.AppendLine("int _tick = 0;");
            sb.AppendLine();

            // Tick array
            sb.Append("int[] kfTick = { ");
            sb.Append(string.Join(", ", frames.Select(k => k.Tick.ToString())));
            sb.AppendLine(" };");

            // Easing array
            sb.Append("int[] kfEase = { ");
            sb.Append(string.Join(", ", frames.Select(k => ((int)k.EasingToNext).ToString())));
            sb.AppendLine(" };");

            // Property arrays — only for animated properties
            if (animPos)
            {
                sb.Append("float[] kfX = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.X ?? sprite.X:F1}f")));
                sb.AppendLine(" };");
                sb.Append("float[] kfY = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Y ?? sprite.Y:F1}f")));
                sb.AppendLine(" };");
            }
            if (animSize)
            {
                sb.Append("float[] kfW = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Width ?? sprite.Width:F1}f")));
                sb.AppendLine(" };");
                sb.Append("float[] kfH = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Height ?? sprite.Height:F1}f")));
                sb.AppendLine(" };");
            }
            if (animColor)
            {
                sb.Append("int[] kfR = { ");
                sb.Append(string.Join(", ", frames.Select(k => (k.ColorR ?? sprite.ColorR).ToString())));
                sb.AppendLine(" };");
                sb.Append("int[] kfG = { ");
                sb.Append(string.Join(", ", frames.Select(k => (k.ColorG ?? sprite.ColorG).ToString())));
                sb.AppendLine(" };");
                sb.Append("int[] kfB = { ");
                sb.Append(string.Join(", ", frames.Select(k => (k.ColorB ?? sprite.ColorB).ToString())));
                sb.AppendLine(" };");
                sb.Append("int[] kfA = { ");
                sb.Append(string.Join(", ", frames.Select(k => (k.ColorA ?? sprite.ColorA).ToString())));
                sb.AppendLine(" };");
            }
            if (animRot)
            {
                sb.Append("float[] kfRot = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Rotation ?? sprite.Rotation:F4}f")));
                sb.AppendLine(" };");
            }
            if (animScale)
            {
                sb.Append("float[] kfScl = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Scale ?? sprite.Scale:F2}f")));
                sb.AppendLine(" };");
            }

            sb.AppendLine();

            // ── Interpolation logic ──
            sb.AppendLine(RenderHint(kp.TargetScript));
            sb.AppendLine("_tick++;");

            // Tick wrapping based on loop mode
            switch (kp.Loop)
            {
                case LoopMode.Loop:
                    sb.AppendLine($"int t = _tick % {totalTicks};");
                    break;
                case LoopMode.PingPong:
                    sb.AppendLine($"int raw = _tick % {totalTicks * 2};");
                    sb.AppendLine($"int t = raw < {totalTicks} ? raw : {totalTicks * 2} - raw;");
                    break;
                default: // Once
                    sb.AppendLine($"int t = Math.Min(_tick, {totalTicks});");
                    break;
            }
            sb.AppendLine();

            // Find keyframe segment
            sb.AppendLine("// Find active keyframe segment");
            sb.AppendLine($"int seg = 0;");
            sb.AppendLine($"for (int i = 1; i < {frames.Count}; i++)");
            sb.AppendLine("    if (t >= kfTick[i]) seg = i;");
            sb.AppendLine($"int next = (seg + 1 < {frames.Count}) ? seg + 1 : seg;");
            sb.AppendLine("float span = kfTick[next] - kfTick[seg];");
            sb.AppendLine("float frac = span > 0 ? (t - kfTick[seg]) / span : 0f;");
            sb.AppendLine("float ef = Ease(frac, kfEase[seg]);");
            sb.AppendLine();

            // Interpolated variables
            if (animPos)
            {
                sb.AppendLine("float ax = kfX[seg] + (kfX[next] - kfX[seg]) * ef;");
                sb.AppendLine("float ay = kfY[seg] + (kfY[next] - kfY[seg]) * ef;");
            }
            if (animSize)
            {
                sb.AppendLine("float aw = kfW[seg] + (kfW[next] - kfW[seg]) * ef;");
                sb.AppendLine("float ah = kfH[seg] + (kfH[next] - kfH[seg]) * ef;");
            }
            if (animColor)
            {
                sb.AppendLine("int ar = (int)(kfR[seg] + (kfR[next] - kfR[seg]) * ef);");
                sb.AppendLine("int ag = (int)(kfG[seg] + (kfG[next] - kfG[seg]) * ef);");
                sb.AppendLine("int ab = (int)(kfB[seg] + (kfB[next] - kfB[seg]) * ef);");
                sb.AppendLine("int aa = (int)(kfA[seg] + (kfA[next] - kfA[seg]) * ef);");
            }
            if (animRot)
                sb.AppendLine("float arot = kfRot[seg] + (kfRot[next] - kfRot[seg]) * ef;");
            if (animScale)
                sb.AppendLine("float ascl = kfScl[seg] + (kfScl[next] - kfScl[seg]) * ef;");

            sb.AppendLine();

            // ── Sprite block ──
            string posVal  = animPos   ? "new Vector2(ax, ay)"    : $"new Vector2({sprite.X:F1}f, {sprite.Y:F1}f)";
            string sizeVal = animSize  ? "new Vector2(aw, ah)"    : $"new Vector2({sprite.Width:F1}f, {sprite.Height:F1}f)";
            string colVal  = animColor ? "new Color(ar, ag, ab, aa)" : $"new Color({sprite.ColorR}, {sprite.ColorG}, {sprite.ColorB}, {sprite.ColorA})";
            string rotVal  = animRot   ? "arot"                   : $"{sprite.Rotation:F4}f";
            string sclVal  = animScale ? "ascl"                   : $"{sprite.Scale:F2}f";

            sb.AppendLine($"{kp.ListVarName}.Add(new MySprite");
            sb.AppendLine("{");
            if (isText)
            {
                sb.AppendLine($"    Type           = SpriteType.TEXT,");
                sb.AppendLine($"    Data           = \"{Esc(sprite.Text)}\",");
                sb.AppendLine($"    Position       = {posVal},{(animPos ? "  // ← animated" : "")}");
                sb.AppendLine($"    Color          = {colVal},{(animColor ? "  // ← animated" : "")}");
                sb.AppendLine($"    FontId         = \"{Esc(sprite.FontId)}\",");
                sb.AppendLine($"    Alignment      = {alignStr},");
                sb.AppendLine($"    RotationOrScale = {sclVal},{(animScale ? "  // ← animated" : "")}");
            }
            else
            {
                sb.AppendLine($"    Type           = SpriteType.TEXTURE,");
                sb.AppendLine($"    Data           = \"{Esc(sprite.SpriteName)}\",");
                sb.AppendLine($"    Position       = {posVal},{(animPos ? "  // ← animated" : "")}");
                sb.AppendLine($"    Size           = {sizeVal},{(animSize ? "  // ← animated" : "")}");
                sb.AppendLine($"    Color          = {colVal},{(animColor ? "  // ← animated" : "")}");
                sb.AppendLine($"    Alignment      = {alignStr},");
                sb.AppendLine($"    RotationOrScale = {rotVal},{(animRot ? "  // ← animated" : "")}");
            }
            sb.AppendLine("});");

            return sb.ToString();
        }

        /// <summary>Checks whether a nullable property varies across keyframes.</summary>
        private static bool HasVariation(List<Keyframe> frames, Func<Keyframe, float?> selector)
        {
            float? first = null;
            foreach (var k in frames)
            {
                var v = selector(k);
                if (!v.HasValue) continue;
                if (!first.HasValue) { first = v; continue; }
                if (Math.Abs(v.Value - first.Value) > 0.001f) return true;
            }
            return false;
        }

        private static bool HasVariation(List<Keyframe> frames, Func<Keyframe, int?> selector)
        {
            int? first = null;
            foreach (var k in frames)
            {
                var v = selector(k);
                if (!v.HasValue) continue;
                if (!first.HasValue) { first = v; continue; }
                if (v.Value != first.Value) return true;
            }
            return false;
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static void AppendSpriteBlock(StringBuilder sb, SpriteEntry sp,
            string animatedValue, string animatedProp, string indent = "",
            string listVar = "sprites")
        {
            bool isText = sp.Type == SpriteEntryType.Text;
            string alignStr = "TextAlignment." + sp.Alignment.ToString().ToUpperInvariant();

            sb.AppendLine($"{indent}{listVar}.Add(new MySprite");
            sb.AppendLine($"{indent}{{");

            if (isText)
            {
                sb.AppendLine($"{indent}    Type           = SpriteType.TEXT,");
                sb.AppendLine($"{indent}    Data           = \"{Esc(sp.Text)}\",");
                AppendProp(sb, indent, "Position", $"new Vector2({sp.X:F1}f, {sp.Y:F1}f)", animatedProp, animatedValue);
                AppendProp(sb, indent, "Color", $"new Color({sp.ColorR}, {sp.ColorG}, {sp.ColorB}, {sp.ColorA})", animatedProp, animatedValue);
                sb.AppendLine($"{indent}    FontId         = \"{Esc(sp.FontId)}\",");
                sb.AppendLine($"{indent}    Alignment      = {alignStr},");
                AppendProp(sb, indent, "RotationOrScale", $"{sp.Scale:F2}f", animatedProp, animatedValue);
            }
            else
            {
                sb.AppendLine($"{indent}    Type           = SpriteType.TEXTURE,");
                sb.AppendLine($"{indent}    Data           = \"{Esc(sp.SpriteName)}\",");
                AppendProp(sb, indent, "Position", $"new Vector2({sp.X:F1}f, {sp.Y:F1}f)", animatedProp, animatedValue);
                AppendProp(sb, indent, "Size", $"new Vector2({sp.Width:F1}f, {sp.Height:F1}f)", animatedProp, animatedValue);
                AppendProp(sb, indent, "Color", $"new Color({sp.ColorR}, {sp.ColorG}, {sp.ColorB}, {sp.ColorA})", animatedProp, animatedValue);
                sb.AppendLine($"{indent}    Alignment      = {alignStr},");
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

        /// <summary>Returns a short display label for the target script type.</summary>
        private static string TargetLabel(TargetScriptType t)
        {
            switch (t)
            {
                case TargetScriptType.ProgrammableBlock: return "PB";
                case TargetScriptType.Mod:               return "Mod";
                case TargetScriptType.Plugin:             return "Plugin";
                case TargetScriptType.Pulsar:             return "Pulsar";
                default:                                  return "LCD Helper";
            }
        }

        /// <summary>Returns a context-aware "add to class" comment for the target.</summary>
        private static string FieldHint(TargetScriptType t)
        {
            switch (t)
            {
                case TargetScriptType.ProgrammableBlock: return "// Field — add to your Program class:";
                case TargetScriptType.Mod:               return "// Field — add to your session component:";
                case TargetScriptType.Plugin:             return "// Field — add to your plugin class:";
                case TargetScriptType.Pulsar:             return "// Field — add to your IPlugin class:";
                default:                                  return "// Field — add to your class:";
            }
        }

        /// <summary>Returns a context-aware "in your render method" comment for the target.</summary>
        private static string RenderHint(TargetScriptType t)
        {
            switch (t)
            {
                case TargetScriptType.ProgrammableBlock: return "// In your Main() or render method:";
                case TargetScriptType.Mod:               return "// In your UpdateAfterSimulation() or render method:";
                case TargetScriptType.Plugin:             return "// In your render method:";
                case TargetScriptType.Pulsar:             return "// In your Update() or render method:";
                default:                                  return "// In your render method:";
            }
        }

        private static string Esc(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // ── Animation group code generation ─────────────────────────────────────

        /// <summary>
        /// Generates a keyframe animation snippet that applies the same motion pattern
        /// to multiple sprites. The leader's keyframe data is emitted once as shared
        /// arrays, then each sprite gets its own add-block with position/size offsets
        /// relative to the leader's first-keyframe base values.
        /// </summary>
        public static string GenerateKeyframedGroup(
            SpriteEntry leader,
            KeyframeAnimationParams kp,
            IReadOnlyList<SpriteEntry> followers)
        {
            if (kp.Keyframes == null || kp.Keyframes.Count < 2)
                return "// Need at least 2 keyframes for an animation";
            if (followers == null || followers.Count == 0)
                return GenerateKeyframed(leader, kp); // no followers, just generate single

            var frames = kp.Keyframes.OrderBy(k => k.Tick).ToList();
            int totalTicks = frames.Last().Tick;
            if (totalTicks <= 0) totalTicks = 1;

            bool animPos   = HasVariation(frames, k => k.X) || HasVariation(frames, k => k.Y);
            bool animSize  = HasVariation(frames, k => k.Width) || HasVariation(frames, k => k.Height);
            bool animColor = HasVariation(frames, k => k.ColorR) || HasVariation(frames, k => k.ColorG)
                          || HasVariation(frames, k => k.ColorB) || HasVariation(frames, k => k.ColorA);
            bool animRot   = HasVariation(frames, k => k.Rotation);
            bool animScale = HasVariation(frames, k => k.Scale);

            // All sprites in the group (leader first)
            var allSprites = new List<SpriteEntry> { leader };
            allSprites.AddRange(followers);

            // Leader's base values (first keyframe) — used to compute deltas
            var kf0 = frames[0];
            float baseX = kf0.X ?? leader.X;
            float baseY = kf0.Y ?? leader.Y;
            float baseW = kf0.Width ?? leader.Width;
            float baseH = kf0.Height ?? leader.Height;

            var sb = new StringBuilder();
            sb.AppendLine($"// ─── Animation Group: \"{SpriteName(leader)}\" + {followers.Count} sprite(s) [{TargetLabel(kp.TargetScript)}] ───");
            sb.AppendLine($"// {frames.Count} keyframes over {totalTicks} ticks  |  Loop: {kp.Loop}  |  Group: {allSprites.Count} sprites");
            sb.AppendLine();

            // ── Easing helper (same as single-sprite version) ──
            sb.AppendLine("// ── Easing helper ──");
            sb.AppendLine("float Ease(float t, int easeType)");
            sb.AppendLine("{");
            sb.AppendLine("    switch (easeType)");
            sb.AppendLine("    {");
            sb.AppendLine("        case 0: return t; // Linear");
            sb.AppendLine("        case 1: return (float)(0.5 - 0.5 * Math.Cos(t * Math.PI)); // SineInOut");
            sb.AppendLine("        case 2: return t * t; // EaseIn");
            sb.AppendLine("        case 3: return 1f - (1f - t) * (1f - t); // EaseOut");
            sb.AppendLine("        case 4: return t < 0.5f ? 2f * t * t : 1f - (float)Math.Pow(-2 * t + 2, 2) / 2f; // EaseInOut");
            sb.AppendLine("        case 5: // Bounce");
            sb.AppendLine("        {");
            sb.AppendLine("            float b = 1f - t;");
            sb.AppendLine("            if (b < 1f / 2.75f) return 1f - 7.5625f * b * b;");
            sb.AppendLine("            if (b < 2f / 2.75f) { b -= 1.5f / 2.75f; return 1f - (7.5625f * b * b + 0.75f); }");
            sb.AppendLine("            if (b < 2.5f / 2.75f) { b -= 2.25f / 2.75f; return 1f - (7.5625f * b * b + 0.9375f); }");
            sb.AppendLine("            b -= 2.625f / 2.75f; return 1f - (7.5625f * b * b + 0.984375f);");
            sb.AppendLine("        }");
            sb.AppendLine("        case 6: // Elastic");
            sb.AppendLine("        {");
            sb.AppendLine("            if (t <= 0f) return 0f;");
            sb.AppendLine("            if (t >= 1f) return 1f;");
            sb.AppendLine("            return (float)(-Math.Pow(2, 10 * t - 10) * Math.Sin((t * 10 - 10.75) * (2 * Math.PI / 3)));");
            sb.AppendLine("        }");
            sb.AppendLine("        default: return t;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();

            // ── Shared keyframe arrays ──
            sb.AppendLine("// ── Shared keyframe data (animation group) ──");
            sb.AppendLine(FieldHint(kp.TargetScript));
            sb.AppendLine("int _tick = 0;");
            sb.AppendLine();

            sb.Append("int[] kfTick = { ");
            sb.Append(string.Join(", ", frames.Select(k => k.Tick.ToString())));
            sb.AppendLine(" };");

            sb.Append("int[] kfEase = { ");
            sb.Append(string.Join(", ", frames.Select(k => ((int)k.EasingToNext).ToString())));
            sb.AppendLine(" };");

            if (animPos)
            {
                sb.Append("float[] kfX = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.X ?? leader.X:F1}f")));
                sb.AppendLine(" };");
                sb.Append("float[] kfY = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Y ?? leader.Y:F1}f")));
                sb.AppendLine(" };");
            }
            if (animSize)
            {
                sb.Append("float[] kfW = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Width ?? leader.Width:F1}f")));
                sb.AppendLine(" };");
                sb.Append("float[] kfH = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Height ?? leader.Height:F1}f")));
                sb.AppendLine(" };");
            }
            if (animColor)
            {
                sb.Append("int[] kfR = { ");
                sb.Append(string.Join(", ", frames.Select(k => (k.ColorR ?? leader.ColorR).ToString())));
                sb.AppendLine(" };");
                sb.Append("int[] kfG = { ");
                sb.Append(string.Join(", ", frames.Select(k => (k.ColorG ?? leader.ColorG).ToString())));
                sb.AppendLine(" };");
                sb.Append("int[] kfB = { ");
                sb.Append(string.Join(", ", frames.Select(k => (k.ColorB ?? leader.ColorB).ToString())));
                sb.AppendLine(" };");
                sb.Append("int[] kfA = { ");
                sb.Append(string.Join(", ", frames.Select(k => (k.ColorA ?? leader.ColorA).ToString())));
                sb.AppendLine(" };");
            }
            if (animRot)
            {
                sb.Append("float[] kfRot = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Rotation ?? leader.Rotation:F4}f")));
                sb.AppendLine(" };");
            }
            if (animScale)
            {
                sb.Append("float[] kfScl = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Scale ?? leader.Scale:F2}f")));
                sb.AppendLine(" };");
            }
            sb.AppendLine();

            // ── Shared interpolation ──
            sb.AppendLine(RenderHint(kp.TargetScript));
            sb.AppendLine("_tick++;");

            switch (kp.Loop)
            {
                case LoopMode.Loop:
                    sb.AppendLine($"int t = _tick % {totalTicks};");
                    break;
                case LoopMode.PingPong:
                    sb.AppendLine($"int raw = _tick % {totalTicks * 2};");
                    sb.AppendLine($"int t = raw < {totalTicks} ? raw : {totalTicks * 2} - raw;");
                    break;
                default:
                    sb.AppendLine($"int t = Math.Min(_tick, {totalTicks});");
                    break;
            }
            sb.AppendLine();

            sb.AppendLine("// Find active keyframe segment");
            sb.AppendLine("int seg = 0;");
            sb.AppendLine($"for (int i = 1; i < {frames.Count}; i++)");
            sb.AppendLine("    if (t >= kfTick[i]) seg = i;");
            sb.AppendLine($"int next = (seg + 1 < {frames.Count}) ? seg + 1 : seg;");
            sb.AppendLine("float span = kfTick[next] - kfTick[seg];");
            sb.AppendLine("float frac = span > 0 ? (t - kfTick[seg]) / span : 0f;");
            sb.AppendLine("float ef = Ease(frac, kfEase[seg]);");
            sb.AppendLine();

            // Interpolated delta values (relative to leader's base)
            if (animPos)
            {
                sb.AppendLine($"float dX = (kfX[seg] + (kfX[next] - kfX[seg]) * ef) - {baseX:F1}f;");
                sb.AppendLine($"float dY = (kfY[seg] + (kfY[next] - kfY[seg]) * ef) - {baseY:F1}f;");
            }
            if (animSize)
            {
                sb.AppendLine($"float dW = (kfW[seg] + (kfW[next] - kfW[seg]) * ef) - {baseW:F1}f;");
                sb.AppendLine($"float dH = (kfH[seg] + (kfH[next] - kfH[seg]) * ef) - {baseH:F1}f;");
            }
            if (animColor)
            {
                sb.AppendLine("int ar = (int)(kfR[seg] + (kfR[next] - kfR[seg]) * ef);");
                sb.AppendLine("int ag = (int)(kfG[seg] + (kfG[next] - kfG[seg]) * ef);");
                sb.AppendLine("int ab = (int)(kfB[seg] + (kfB[next] - kfB[seg]) * ef);");
                sb.AppendLine("int aa = (int)(kfA[seg] + (kfA[next] - kfA[seg]) * ef);");
            }
            if (animRot)
                sb.AppendLine("float arot = kfRot[seg] + (kfRot[next] - kfRot[seg]) * ef;");
            if (animScale)
                sb.AppendLine("float ascl = kfScl[seg] + (kfScl[next] - kfScl[seg]) * ef;");
            sb.AppendLine();

            // ── Per-sprite blocks ──
            foreach (var sp in allSprites)
            {
                bool isText    = sp.Type == SpriteEntryType.Text;
                string alignStr = "TextAlignment." + sp.Alignment.ToString().ToUpperInvariant();

                // Position: sprite's own base + delta from leader's motion
                string posVal = animPos
                    ? $"new Vector2({sp.X:F1}f + dX, {sp.Y:F1}f + dY)"
                    : $"new Vector2({sp.X:F1}f, {sp.Y:F1}f)";
                string sizeVal = animSize
                    ? $"new Vector2({sp.Width:F1}f + dW, {sp.Height:F1}f + dH)"
                    : $"new Vector2({sp.Width:F1}f, {sp.Height:F1}f)";
                string colVal = animColor
                    ? "new Color(ar, ag, ab, aa)"
                    : $"new Color({sp.ColorR}, {sp.ColorG}, {sp.ColorB}, {sp.ColorA})";
                string rotVal = animRot ? "arot" : $"{sp.Rotation:F4}f";
                string sclVal = animScale ? "ascl" : $"{sp.Scale:F2}f";

                sb.AppendLine($"// {SpriteName(sp)}{(sp == leader ? " (leader)" : "")}");
                sb.AppendLine($"{kp.ListVarName}.Add(new MySprite");
                sb.AppendLine("{");
                if (isText)
                {
                    sb.AppendLine($"    Type           = SpriteType.TEXT,");
                    sb.AppendLine($"    Data           = \"{Esc(sp.Text)}\",");
                    sb.AppendLine($"    Position       = {posVal},");
                    sb.AppendLine($"    Color          = {colVal},");
                    sb.AppendLine($"    FontId         = \"{Esc(sp.FontId)}\",");
                    sb.AppendLine($"    Alignment      = {alignStr},");
                    sb.AppendLine($"    RotationOrScale = {sclVal},");
                }
                else
                {
                    sb.AppendLine($"    Type           = SpriteType.TEXTURE,");
                    sb.AppendLine($"    Data           = \"{Esc(sp.SpriteName)}\",");
                    sb.AppendLine($"    Position       = {posVal},");
                    sb.AppendLine($"    Size           = {sizeVal},");
                    sb.AppendLine($"    Color          = {colVal},");
                    sb.AppendLine($"    Alignment      = {alignStr},");
                    sb.AppendLine($"    RotationOrScale = {rotVal},");
                }
                sb.AppendLine("});");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        // ── Round-trip parser: code → KeyframeAnimationParams ───────────────────

        // Regex patterns for the well-known array declarations emitted by GenerateKeyframed()
        private static readonly Regex RxIntArray   = new Regex(@"int\[\]\s+(\w+)\s*=\s*\{\s*([^}]+)\}", RegexOptions.Compiled);
        private static readonly Regex RxFloatArray = new Regex(@"float\[\]\s+(\w+)\s*=\s*\{\s*([^}]+)\}", RegexOptions.Compiled);
        private static readonly Regex RxHeader     = new Regex(@"//\s*─+\s*Keyframe Animation:\s*""([^""]+)""\s*\[([^\]]+)\]", RegexOptions.Compiled);
        private static readonly Regex RxGroupHeader = new Regex(@"//\s*─+\s*Animation Group:\s*""([^""]+)""\s*\+\s*\d+\s*sprite", RegexOptions.Compiled);
        private static readonly Regex RxLoopMode   = new Regex(@"Loop:\s*(\w+)", RegexOptions.Compiled);
        private static readonly Regex RxListVar    = new Regex(@"(\w+)\.Add\(\s*new\s+MySprite", RegexOptions.Compiled);

        /// <summary>
        /// Finds the start index and length of a keyframe animation block in source code.
        /// Recognises both single-sprite (<c>// ─── Keyframe Animation:</c>) and group
        /// (<c>// ─── Animation Group:</c>) headers. If the block ends with
        /// <see cref="FooterMarker"/>, the entire range up to (and including) the
        /// footer line is returned; otherwise it falls back to the last <c>});</c>
        /// after the sprite <c>.Add(new MySprite …)</c>.
        /// </summary>
        public static bool FindKeyframedBlockRange(string code, out int blockStart, out int blockLength)
        {
            blockStart = 0;
            blockLength = 0;
            if (string.IsNullOrEmpty(code)) return false;

            // Try single-sprite header first, then group header
            var headerMatch = RxHeader.Match(code);
            if (!headerMatch.Success)
                headerMatch = RxGroupHeader.Match(code);
            if (!headerMatch.Success) return false;

            // Walk back to the start of the header line
            blockStart = headerMatch.Index;
            while (blockStart > 0 && code[blockStart - 1] != '\n')
                blockStart--;

            // Check for footer marker (complete program block)
            int footerIdx = code.IndexOf(FooterMarker, blockStart, StringComparison.Ordinal);
            if (footerIdx >= 0)
            {
                int endIdx = footerIdx + FooterMarker.Length;
                if (endIdx < code.Length && code[endIdx] == '\r') endIdx++;
                if (endIdx < code.Length && code[endIdx] == '\n') endIdx++;
                blockLength = endIdx - blockStart;
                return blockLength > 0;
            }

            // Fallback: find the LAST sprite .Add(new MySprite block after the header
            // (groups have multiple Add calls — we need the last one)
            Match addMatch = null;
            Match candidate = RxListVar.Match(code, headerMatch.Index);
            while (candidate.Success)
            {
                addMatch = candidate;
                candidate = RxListVar.Match(code, candidate.Index + candidate.Length);
            }
            if (addMatch == null) return false;

            // Find the closing }); for the last sprite Add call
            int searchFrom = addMatch.Index + addMatch.Length;
            int closeIdx = code.IndexOf("});", searchFrom, StringComparison.Ordinal);
            if (closeIdx < 0) return false;

            // Move past }); and consume trailing whitespace/newline
            closeIdx += 3;
            if (closeIdx < code.Length && code[closeIdx] == '\r') closeIdx++;
            if (closeIdx < code.Length && code[closeIdx] == '\n') closeIdx++;

            blockLength = closeIdx - blockStart;
            return blockLength > 0;
        }

        /// <summary>
        /// Attempts to parse keyframe animation data from generated code.
        /// Returns a populated <see cref="KeyframeAnimationParams"/> or null if the code
        /// does not contain recognizable keyframe arrays.
        /// </summary>
        public static KeyframeAnimationParams TryParseKeyframed(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;

            // ── Must have kfTick and kfEase arrays at minimum ──
            int[] ticks  = ParseIntArray(code, "kfTick");
            int[] easings = ParseIntArray(code, "kfEase");
            if (ticks == null || easings == null || ticks.Length < 2) return null;
            if (ticks.Length != easings.Length) return null;

            int count = ticks.Length;

            // ── Optional property arrays ──
            float[] xs   = ParseFloatArray(code, "kfX");
            float[] ys   = ParseFloatArray(code, "kfY");
            float[] ws   = ParseFloatArray(code, "kfW");
            float[] hs   = ParseFloatArray(code, "kfH");
            int[]   rs   = ParseIntArray(code, "kfR");
            int[]   gs   = ParseIntArray(code, "kfG");
            int[]   bs   = ParseIntArray(code, "kfB");
            int[]   als  = ParseIntArray(code, "kfA");
            float[] rots = ParseFloatArray(code, "kfRot");
            float[] scls = ParseFloatArray(code, "kfScl");

            // ── Build keyframes ──
            var keyframes = new List<Keyframe>(count);
            for (int i = 0; i < count; i++)
            {
                var kf = new Keyframe
                {
                    Tick = ticks[i],
                    EasingToNext = (i < easings.Length && Enum.IsDefined(typeof(EasingType), easings[i]))
                                   ? (EasingType)easings[i] : EasingType.Linear,
                };

                if (xs != null && i < xs.Length)   kf.X = xs[i];
                if (ys != null && i < ys.Length)   kf.Y = ys[i];
                if (ws != null && i < ws.Length)   kf.Width = ws[i];
                if (hs != null && i < hs.Length)   kf.Height = hs[i];
                if (rs != null && i < rs.Length)   kf.ColorR = rs[i];
                if (gs != null && i < gs.Length)   kf.ColorG = gs[i];
                if (bs != null && i < bs.Length)   kf.ColorB = bs[i];
                if (als != null && i < als.Length) kf.ColorA = als[i];
                if (rots != null && i < rots.Length) kf.Rotation = rots[i];
                if (scls != null && i < scls.Length) kf.Scale = scls[i];

                keyframes.Add(kf);
            }

            // ── Parse metadata from header comment ──
            var loop = LoopMode.Loop; // default
            var target = TargetScriptType.ProgrammableBlock; // default

            var headerMatch = RxHeader.Match(code);
            if (headerMatch.Success)
            {
                string targetStr = headerMatch.Groups[2].Value.Trim();
                if (targetStr.IndexOf("Torch", StringComparison.OrdinalIgnoreCase) >= 0)
                    target = TargetScriptType.Plugin;
                else if (targetStr.IndexOf("Pulsar", StringComparison.OrdinalIgnoreCase) >= 0)
                    target = TargetScriptType.Pulsar;
                else if (targetStr.IndexOf("Mod", StringComparison.OrdinalIgnoreCase) >= 0)
                    target = TargetScriptType.Mod;
                else if (targetStr.IndexOf("LCD", StringComparison.OrdinalIgnoreCase) >= 0)
                    target = TargetScriptType.LcdHelper;
            }

            var loopMatch = RxLoopMode.Match(code);
            if (loopMatch.Success)
            {
                string loopStr = loopMatch.Groups[1].Value;
                if (Enum.TryParse(loopStr, true, out LoopMode parsed))
                    loop = parsed;
            }

            // ── Parse list variable name ──
            string listVar = target == TargetScriptType.LcdHelper ? "sprites" : "frame";
            var listMatch = RxListVar.Match(code);
            if (listMatch.Success)
                listVar = listMatch.Groups[1].Value;

            return new KeyframeAnimationParams
            {
                TargetScript = target,
                Loop = loop,
                ListVarName = listVar,
                Keyframes = keyframes,
            };
        }

        /// <summary>Parses a named int[] array from code. Returns null if not found.</summary>
        private static int[] ParseIntArray(string code, string name)
        {
            foreach (Match m in RxIntArray.Matches(code))
            {
                if (m.Groups[1].Value == name)
                {
                    return m.Groups[2].Value
                        .Split(',')
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0)
                        .Select(s => int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out int v) ? v : 0)
                        .ToArray();
                }
            }
            return null;
        }

        /// <summary>Parses a named float[] array from code. Returns null if not found.</summary>
        private static float[] ParseFloatArray(string code, string name)
        {
            foreach (Match m in RxFloatArray.Matches(code))
            {
                if (m.Groups[1].Value == name)
                {
                    return m.Groups[2].Value
                        .Split(',')
                        .Select(s => s.Trim().TrimEnd('f', 'F'))
                        .Where(s => s.Length > 0)
                        .Select(s => float.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out float v) ? v : 0f)
                        .ToArray();
                }
            }
            return null;
        }

        // ── Smart array-level merge ─────────────────────────────────────────────

        /// <summary>Matches a full kf array declaration line including leading whitespace and trailing comments.</summary>
        private static readonly Regex RxArrayLine = new Regex(
            @"^[ \t]*(?:int|float)\[\]\s+(\w+)\s*=\s*\{[^}]+\}\s*;[^\r\n]*",
            RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>Extracts the full text of a named array declaration line, or null if not found.</summary>
        private static string ExtractArrayLine(string code, string name)
        {
            foreach (Match m in RxArrayLine.Matches(code))
            {
                if (m.Groups[1].Value == name)
                    return m.Value;
            }
            return null;
        }

        /// <summary>Removes an entire source line that contains the given text.</summary>
        private static string RemoveCodeLine(string code, string lineContent)
        {
            int idx = code.IndexOf(lineContent, StringComparison.Ordinal);
            if (idx < 0) return code;

            // Walk back to start of line
            int lineStart = idx;
            while (lineStart > 0 && code[lineStart - 1] != '\n')
                lineStart--;

            // Walk forward past end of line including newline
            int lineEnd = idx + lineContent.Length;
            if (lineEnd < code.Length && code[lineEnd] == '\r') lineEnd++;
            if (lineEnd < code.Length && code[lineEnd] == '\n') lineEnd++;

            return code.Substring(0, lineStart) + code.Substring(lineEnd);
        }

        /// <summary>Returns the leading whitespace of a line.</summary>
        private static string GetIndent(string line)
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            return line.Substring(0, i);
        }

        /// <summary>
        /// Smart array-level merge: updates kfXxx array declarations, keyframe count
        /// references, and max-tick references in existing code using values from a
        /// newly-generated snippet. Works even when the existing code has a different
        /// structure (e.g., a hand-written PB script) than the generated snippet.
        /// Returns the merged code, or <c>null</c> if the existing code has no kfTick array.
        /// </summary>
        public static string MergeKeyframedIntoCode(string existingCode, string newSnippetCode)
        {
            if (string.IsNullOrEmpty(existingCode) || string.IsNullOrEmpty(newSnippetCode))
                return null;

            // Both must have kfTick arrays
            int[] oldTicks = ParseIntArray(existingCode, "kfTick");
            int[] newTicks = ParseIntArray(newSnippetCode, "kfTick");
            if (oldTicks == null || newTicks == null || oldTicks.Length < 2 || newTicks.Length < 2)
                return null;

            int oldCount = oldTicks.Length;
            int newCount = newTicks.Length;
            int oldMaxTick = oldTicks[oldTicks.Length - 1];
            int newMaxTick = newTicks[newTicks.Length - 1];

            string result = existingCode;

            // All known kf array names in declaration order
            string[] arrayNames = { "kfTick", "kfEase", "kfX", "kfY", "kfW", "kfH",
                                    "kfR", "kfG", "kfB", "kfA", "kfRot", "kfScl" };

            string lastFoundLine = null;

            foreach (string name in arrayNames)
            {
                string oldLine = ExtractArrayLine(result, name);
                string newLine = ExtractArrayLine(newSnippetCode, name);

                if (oldLine != null && newLine != null)
                {
                    // Preserve original indentation
                    string indent = GetIndent(oldLine);
                    string replacement = indent + newLine.TrimStart();
                    result = result.Replace(oldLine, replacement);
                    lastFoundLine = replacement;
                }
                else if (oldLine != null && newLine == null)
                {
                    // Property no longer animated — remove the array
                    result = RemoveCodeLine(result, oldLine);
                }
                else if (oldLine == null && newLine != null && lastFoundLine != null)
                {
                    // New property being animated — insert after last known array
                    int insertIdx = result.IndexOf(lastFoundLine, StringComparison.Ordinal);
                    if (insertIdx >= 0)
                    {
                        string indent = GetIndent(lastFoundLine);
                        string toInsert = indent + newLine.TrimStart();
                        int eol = result.IndexOf('\n', insertIdx);
                        if (eol >= 0)
                            result = result.Insert(eol + 1, toInsert + Environment.NewLine);
                        else
                            result += Environment.NewLine + toInsert;
                        lastFoundLine = toInsert;
                    }
                }
            }

            // ── Update keyframe count references ──
            if (oldCount != newCount)
            {
                // for (int i = 1; i < N; i++)
                result = Regex.Replace(result,
                    @"(for\s*\(\s*int\s+\w+\s*=\s*1\s*;\s*\w+\s*<\s*)" + oldCount + @"(\s*;)",
                    "${1}" + newCount + "${2}");

                // (seg + 1 < N)
                result = Regex.Replace(result,
                    @"(\(\s*\w+\s*\+\s*1\s*<\s*)" + oldCount + @"(\s*\))",
                    "${1}" + newCount + "${2}");
            }

            // ── Update max tick references ──
            if (oldMaxTick != newMaxTick)
            {
                // Loop: % maxTick;
                result = Regex.Replace(result,
                    @"(%\s*)" + oldMaxTick + @"(\s*;)",
                    "${1}" + newMaxTick + "${2}");

                // PingPong: % (maxTick*2);
                if (oldMaxTick * 2 != newMaxTick * 2)
                {
                    result = Regex.Replace(result,
                        @"(%\s*)" + (oldMaxTick * 2) + @"(\s*;)",
                        "${1}" + (newMaxTick * 2) + "${2}");
                }
            }

            // ── Update header comment counts if present ──
            result = Regex.Replace(result,
                @"(\d+)\s+keyframes\s+over\s+(\d+)\s+ticks",
                $"{newCount} keyframes over {newMaxTick} ticks");

            // ── Wire animation variables into sprite blocks ──
            // After merging arrays, sprite blocks added by other code paths
            // (e.g. RoslynCodeMerger) may still reference static float/int
            // literals instead of the computed animation variables.
            // Replace them so all sprites in the scope use the shared vars.
            if (result.Contains("float arot ="))
            {
                result = Regex.Replace(result,
                    @"(RotationOrScale\s*=\s*)-?[\d.]+f(\s*,)",
                    "${1}arot${2}");
            }
            else if (result.Contains("float ascl ="))
            {
                result = Regex.Replace(result,
                    @"(RotationOrScale\s*=\s*)-?[\d.]+f(\s*,)",
                    "${1}ascl${2}");
            }

            if (result.Contains("int ar ="))
            {
                result = Regex.Replace(result,
                    @"(Color\s*=\s*)new\s+Color\s*\(\s*\d+\s*,\s*\d+\s*,\s*\d+(?:\s*,\s*\d+)?\s*\)(\s*,)",
                    "${1}new Color(ar, ag, ab, aa)${2}");
            }

            return result;
        }

        // ── Complete program generation (compilable by AnimationPlayer) ─────────

        /// <summary>Footer marker appended to complete programs for block detection.</summary>
        public const string FooterMarker = "// ─── End Keyframe Animation ───";

        /// <summary>
        /// Generates a COMPLETE compilable program wrapping the keyframe animation.
        /// For PB targets: full PB program with Program(), Main(), DrawFrame().
        /// For other targets: LCD Helper with a List&lt;MySprite&gt;-returning method.
        /// </summary>
        public static string GenerateKeyframedComplete(SpriteEntry sprite, KeyframeAnimationParams kp)
        {
            if (kp.Keyframes == null || kp.Keyframes.Count < 2)
                return GenerateKeyframed(sprite, kp);

            if (kp.TargetScript == TargetScriptType.ProgrammableBlock)
                return GenerateKeyframedCompletePB(sprite, kp);

            return GenerateKeyframedCompleteHelper(sprite, kp);
        }

        /// <summary>
        /// Generates a COMPLETE compilable group animation program.
        /// </summary>
        public static string GenerateKeyframedGroupComplete(
            SpriteEntry leader, KeyframeAnimationParams kp, IReadOnlyList<SpriteEntry> followers)
        {
            if (kp.Keyframes == null || kp.Keyframes.Count < 2)
                return GenerateKeyframedGroup(leader, kp, followers);

            if (followers == null || followers.Count == 0)
                return GenerateKeyframedComplete(leader, kp);

            if (kp.TargetScript == TargetScriptType.ProgrammableBlock)
                return GenerateKeyframedGroupCompletePB(leader, kp, followers);

            return GenerateKeyframedGroupCompleteHelper(leader, kp, followers);
        }

        // ── PB complete program ────────────────────────────────────────────────

        private static string GenerateKeyframedCompletePB(SpriteEntry sprite, KeyframeAnimationParams kp)
        {
            var frames = kp.Keyframes.OrderBy(k => k.Tick).ToList();
            int totalTicks = frames.Last().Tick;
            if (totalTicks <= 0) totalTicks = 1;

            bool animPos   = HasVariation(frames, k => k.X) || HasVariation(frames, k => k.Y);
            bool animSize  = HasVariation(frames, k => k.Width) || HasVariation(frames, k => k.Height);
            bool animColor = HasVariation(frames, k => k.ColorR) || HasVariation(frames, k => k.ColorG)
                          || HasVariation(frames, k => k.ColorB) || HasVariation(frames, k => k.ColorA);
            bool animRot   = HasVariation(frames, k => k.Rotation);
            bool animScale = HasVariation(frames, k => k.Scale);
            bool isText    = sprite.Type == SpriteEntryType.Text;
            string alignStr = "TextAlignment." + sprite.Alignment.ToString().ToUpperInvariant();

            var sb = new StringBuilder();
            sb.AppendLine($"// ─── Keyframe Animation: \"{SpriteName(sprite)}\" [PB] ───");
            sb.AppendLine($"// {frames.Count} keyframes over {totalTicks} ticks  |  Loop: {kp.Loop}");
            sb.AppendLine();

            // Fields
            sb.AppendLine("int _tick = 0;");
            sb.AppendLine("IMyTextSurface _surface;");
            sb.AppendLine();

            // Keyframe arrays
            sb.AppendLine("// ── Keyframe data ──");
            AppendKeyframeArrays(sb, frames, sprite, animPos, animSize, animColor, animRot, animScale);
            sb.AppendLine();

            // Easing helper
            AppendEaseFunction(sb, "public ");
            sb.AppendLine();

            // Program constructor
            sb.AppendLine("public Program()");
            sb.AppendLine("{");
            sb.AppendLine("    _surface = Me.GetSurface(0);");
            sb.AppendLine("    _surface.ContentType = ContentType.SCRIPT;");
            sb.AppendLine("    _surface.Script = \"\";");
            sb.AppendLine("    Runtime.UpdateFrequency = UpdateFrequency.Update1;");
            sb.AppendLine("}");
            sb.AppendLine();

            // Main method
            sb.AppendLine("public void Main(string argument, UpdateType updateSource)");
            sb.AppendLine("{");
            sb.AppendLine("    var frame = _surface.DrawFrame();");
            sb.AppendLine();

            // Tick and interpolation
            AppendInterpolationBody(sb, frames, totalTicks, kp.Loop,
                animPos, animSize, animColor, animRot, animScale, "    ");
            sb.AppendLine();

            // Sprite block
            AppendSpriteBlock(sb, sprite, isText, alignStr, "frame",
                animPos, animSize, animColor, animRot, animScale, false, "    ");
            sb.AppendLine();
            sb.AppendLine("    frame.Dispose();");
            sb.AppendLine("}");
            sb.AppendLine(FooterMarker);

            return sb.ToString();
        }

        private static string GenerateKeyframedGroupCompletePB(
            SpriteEntry leader, KeyframeAnimationParams kp, IReadOnlyList<SpriteEntry> followers)
        {
            var frames = kp.Keyframes.OrderBy(k => k.Tick).ToList();
            int totalTicks = frames.Last().Tick;
            if (totalTicks <= 0) totalTicks = 1;

            bool animPos   = HasVariation(frames, k => k.X) || HasVariation(frames, k => k.Y);
            bool animSize  = HasVariation(frames, k => k.Width) || HasVariation(frames, k => k.Height);
            bool animColor = HasVariation(frames, k => k.ColorR) || HasVariation(frames, k => k.ColorG)
                          || HasVariation(frames, k => k.ColorB) || HasVariation(frames, k => k.ColorA);
            bool animRot   = HasVariation(frames, k => k.Rotation);
            bool animScale = HasVariation(frames, k => k.Scale);

            var kf0 = frames[0];
            float baseX = kf0.X ?? leader.X;
            float baseY = kf0.Y ?? leader.Y;
            float baseW = kf0.Width ?? leader.Width;
            float baseH = kf0.Height ?? leader.Height;

            var allSprites = new List<SpriteEntry> { leader };
            allSprites.AddRange(followers);

            var sb = new StringBuilder();
            sb.AppendLine($"// ─── Animation Group: \"{SpriteName(leader)}\" + {followers.Count} sprite(s) [PB] ───");
            sb.AppendLine($"// {frames.Count} keyframes over {totalTicks} ticks  |  Loop: {kp.Loop}  |  Group: {allSprites.Count} sprites");
            sb.AppendLine();

            sb.AppendLine("int _tick = 0;");
            sb.AppendLine("IMyTextSurface _surface;");
            sb.AppendLine();

            sb.AppendLine("// ── Shared keyframe data (animation group) ──");
            AppendKeyframeArrays(sb, frames, leader, animPos, animSize, animColor, animRot, animScale);
            sb.AppendLine();

            AppendEaseFunction(sb, "public ");
            sb.AppendLine();

            sb.AppendLine("public Program()");
            sb.AppendLine("{");
            sb.AppendLine("    _surface = Me.GetSurface(0);");
            sb.AppendLine("    _surface.ContentType = ContentType.SCRIPT;");
            sb.AppendLine("    _surface.Script = \"\";");
            sb.AppendLine("    Runtime.UpdateFrequency = UpdateFrequency.Update1;");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("public void Main(string argument, UpdateType updateSource)");
            sb.AppendLine("{");
            sb.AppendLine("    var frame = _surface.DrawFrame();");
            sb.AppendLine();

            AppendInterpolationBodyGroup(sb, frames, totalTicks, kp.Loop,
                animPos, animSize, animColor, animRot, animScale,
                baseX, baseY, baseW, baseH, "    ");
            sb.AppendLine();

            foreach (var sp in allSprites)
            {
                bool isText = sp.Type == SpriteEntryType.Text;
                string alignStr = "TextAlignment." + sp.Alignment.ToString().ToUpperInvariant();
                sb.AppendLine($"    // {SpriteName(sp)}{(sp == leader ? " (leader)" : "")}");
                AppendSpriteBlock(sb, sp, isText, alignStr, "frame",
                    animPos, animSize, animColor, animRot, animScale, true, "    ");
                sb.AppendLine();
            }

            sb.AppendLine("    frame.Dispose();");
            sb.AppendLine("}");
            sb.AppendLine(FooterMarker);

            return sb.ToString();
        }

        // ── LCD Helper complete program ────────────────────────────────────────

        private static string GenerateKeyframedCompleteHelper(SpriteEntry sprite, KeyframeAnimationParams kp)
        {
            var frames = kp.Keyframes.OrderBy(k => k.Tick).ToList();
            int totalTicks = frames.Last().Tick;
            if (totalTicks <= 0) totalTicks = 1;

            bool animPos   = HasVariation(frames, k => k.X) || HasVariation(frames, k => k.Y);
            bool animSize  = HasVariation(frames, k => k.Width) || HasVariation(frames, k => k.Height);
            bool animColor = HasVariation(frames, k => k.ColorR) || HasVariation(frames, k => k.ColorG)
                          || HasVariation(frames, k => k.ColorB) || HasVariation(frames, k => k.ColorA);
            bool animRot   = HasVariation(frames, k => k.Rotation);
            bool animScale = HasVariation(frames, k => k.Scale);
            bool isText    = sprite.Type == SpriteEntryType.Text;
            string alignStr = "TextAlignment." + sprite.Alignment.ToString().ToUpperInvariant();

            var sb = new StringBuilder();
            sb.AppendLine($"// ─── Keyframe Animation: \"{SpriteName(sprite)}\" [{TargetLabel(kp.TargetScript)}] ───");
            sb.AppendLine($"// {frames.Count} keyframes over {totalTicks} ticks  |  Loop: {kp.Loop}");
            sb.AppendLine();

            // Fields
            sb.AppendLine("int _tick = 0;");
            sb.AppendLine();

            // Keyframe arrays
            sb.AppendLine("// ── Keyframe data ──");
            AppendKeyframeArrays(sb, frames, sprite, animPos, animSize, animColor, animRot, animScale);
            sb.AppendLine();

            // Easing helper
            AppendEaseFunction(sb, "");
            sb.AppendLine();

            // Render method returning List<MySprite>
            sb.AppendLine("public List<MySprite> RenderAnimation()");
            sb.AppendLine("{");
            sb.AppendLine("    var sprites = new List<MySprite>();");
            sb.AppendLine();

            AppendInterpolationBody(sb, frames, totalTicks, kp.Loop,
                animPos, animSize, animColor, animRot, animScale, "    ");
            sb.AppendLine();

            AppendSpriteBlock(sb, sprite, isText, alignStr, "sprites",
                animPos, animSize, animColor, animRot, animScale, false, "    ");
            sb.AppendLine();
            sb.AppendLine("    return sprites;");
            sb.AppendLine("}");
            sb.AppendLine(FooterMarker);

            return sb.ToString();
        }

        private static string GenerateKeyframedGroupCompleteHelper(
            SpriteEntry leader, KeyframeAnimationParams kp, IReadOnlyList<SpriteEntry> followers)
        {
            var frames = kp.Keyframes.OrderBy(k => k.Tick).ToList();
            int totalTicks = frames.Last().Tick;
            if (totalTicks <= 0) totalTicks = 1;

            bool animPos   = HasVariation(frames, k => k.X) || HasVariation(frames, k => k.Y);
            bool animSize  = HasVariation(frames, k => k.Width) || HasVariation(frames, k => k.Height);
            bool animColor = HasVariation(frames, k => k.ColorR) || HasVariation(frames, k => k.ColorG)
                          || HasVariation(frames, k => k.ColorB) || HasVariation(frames, k => k.ColorA);
            bool animRot   = HasVariation(frames, k => k.Rotation);
            bool animScale = HasVariation(frames, k => k.Scale);

            var kf0 = frames[0];
            float baseX = kf0.X ?? leader.X;
            float baseY = kf0.Y ?? leader.Y;
            float baseW = kf0.Width ?? leader.Width;
            float baseH = kf0.Height ?? leader.Height;

            var allSprites = new List<SpriteEntry> { leader };
            allSprites.AddRange(followers);

            var sb = new StringBuilder();
            sb.AppendLine($"// ─── Animation Group: \"{SpriteName(leader)}\" + {followers.Count} sprite(s) [{TargetLabel(kp.TargetScript)}] ───");
            sb.AppendLine($"// {frames.Count} keyframes over {totalTicks} ticks  |  Loop: {kp.Loop}  |  Group: {allSprites.Count} sprites");
            sb.AppendLine();

            sb.AppendLine("int _tick = 0;");
            sb.AppendLine();

            sb.AppendLine("// ── Shared keyframe data (animation group) ──");
            AppendKeyframeArrays(sb, frames, leader, animPos, animSize, animColor, animRot, animScale);
            sb.AppendLine();

            AppendEaseFunction(sb, "");
            sb.AppendLine();

            sb.AppendLine("public List<MySprite> RenderAnimation()");
            sb.AppendLine("{");
            sb.AppendLine("    var sprites = new List<MySprite>();");
            sb.AppendLine();

            AppendInterpolationBodyGroup(sb, frames, totalTicks, kp.Loop,
                animPos, animSize, animColor, animRot, animScale,
                baseX, baseY, baseW, baseH, "    ");
            sb.AppendLine();

            foreach (var sp in allSprites)
            {
                bool isText = sp.Type == SpriteEntryType.Text;
                string alignStr = "TextAlignment." + sp.Alignment.ToString().ToUpperInvariant();
                sb.AppendLine($"    // {SpriteName(sp)}{(sp == leader ? " (leader)" : "")}");
                AppendSpriteBlock(sb, sp, isText, alignStr, "sprites",
                    animPos, animSize, animColor, animRot, animScale, true, "    ");
                sb.AppendLine();
            }

            sb.AppendLine("    return sprites;");
            sb.AppendLine("}");
            sb.AppendLine(FooterMarker);

            return sb.ToString();
        }

        // ── Shared helpers for complete program generation ──────────────────────

        private static void AppendEaseFunction(StringBuilder sb, string accessModifier)
        {
            sb.AppendLine("// ── Easing helper ──");
            sb.AppendLine($"{accessModifier}float Ease(float t, int easeType)");
            sb.AppendLine("{");
            sb.AppendLine("    switch (easeType)");
            sb.AppendLine("    {");
            sb.AppendLine("        case 0: return t;");
            sb.AppendLine("        case 1: return (float)(0.5 - 0.5 * Math.Cos(t * Math.PI));");
            sb.AppendLine("        case 2: return t * t;");
            sb.AppendLine("        case 3: return 1f - (1f - t) * (1f - t);");
            sb.AppendLine("        case 4: return t < 0.5f ? 2f * t * t : 1f - (float)Math.Pow(-2 * t + 2, 2) / 2f;");
            sb.AppendLine("        case 5:");
            sb.AppendLine("        {");
            sb.AppendLine("            float b = 1f - t;");
            sb.AppendLine("            if (b < 1f / 2.75f) return 1f - 7.5625f * b * b;");
            sb.AppendLine("            if (b < 2f / 2.75f) { b -= 1.5f / 2.75f; return 1f - (7.5625f * b * b + 0.75f); }");
            sb.AppendLine("            if (b < 2.5f / 2.75f) { b -= 2.25f / 2.75f; return 1f - (7.5625f * b * b + 0.9375f); }");
            sb.AppendLine("            b -= 2.625f / 2.75f; return 1f - (7.5625f * b * b + 0.984375f);");
            sb.AppendLine("        }");
            sb.AppendLine("        case 6:");
            sb.AppendLine("        {");
            sb.AppendLine("            if (t <= 0f) return 0f;");
            sb.AppendLine("            if (t >= 1f) return 1f;");
            sb.AppendLine("            return (float)(-Math.Pow(2, 10 * t - 10) * Math.Sin((t * 10 - 10.75) * (2 * Math.PI / 3)));");
            sb.AppendLine("        }");
            sb.AppendLine("        default: return t;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
        }

        private static void AppendKeyframeArrays(StringBuilder sb, List<Keyframe> frames,
            SpriteEntry sprite, bool animPos, bool animSize, bool animColor, bool animRot, bool animScale)
        {
            sb.Append("int[] kfTick = { ");
            sb.Append(string.Join(", ", frames.Select(k => k.Tick.ToString())));
            sb.AppendLine(" };");

            sb.Append("int[] kfEase = { ");
            sb.Append(string.Join(", ", frames.Select(k => ((int)k.EasingToNext).ToString())));
            sb.AppendLine(" };");

            if (animPos)
            {
                sb.Append("float[] kfX = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.X ?? sprite.X:F1}f")));
                sb.AppendLine(" };");
                sb.Append("float[] kfY = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Y ?? sprite.Y:F1}f")));
                sb.AppendLine(" };");
            }
            if (animSize)
            {
                sb.Append("float[] kfW = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Width ?? sprite.Width:F1}f")));
                sb.AppendLine(" };");
                sb.Append("float[] kfH = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Height ?? sprite.Height:F1}f")));
                sb.AppendLine(" };");
            }
            if (animColor)
            {
                sb.Append("int[] kfR = { ");
                sb.Append(string.Join(", ", frames.Select(k => (k.ColorR ?? sprite.ColorR).ToString())));
                sb.AppendLine(" };");
                sb.Append("int[] kfG = { ");
                sb.Append(string.Join(", ", frames.Select(k => (k.ColorG ?? sprite.ColorG).ToString())));
                sb.AppendLine(" };");
                sb.Append("int[] kfB = { ");
                sb.Append(string.Join(", ", frames.Select(k => (k.ColorB ?? sprite.ColorB).ToString())));
                sb.AppendLine(" };");
                sb.Append("int[] kfA = { ");
                sb.Append(string.Join(", ", frames.Select(k => (k.ColorA ?? sprite.ColorA).ToString())));
                sb.AppendLine(" };");
            }
            if (animRot)
            {
                sb.Append("float[] kfRot = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Rotation ?? sprite.Rotation:F4}f")));
                sb.AppendLine(" };");
            }
            if (animScale)
            {
                sb.Append("float[] kfScl = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Scale ?? sprite.Scale:F2}f")));
                sb.AppendLine(" };");
            }
        }

        private static void AppendInterpolationBody(StringBuilder sb,
            List<Keyframe> frames, int totalTicks, LoopMode loop,
            bool animPos, bool animSize, bool animColor, bool animRot, bool animScale,
            string indent)
        {
            sb.AppendLine($"{indent}_tick++;");
            switch (loop)
            {
                case LoopMode.Loop:
                    sb.AppendLine($"{indent}int t = _tick % {totalTicks};");
                    break;
                case LoopMode.PingPong:
                    sb.AppendLine($"{indent}int raw = _tick % {totalTicks * 2};");
                    sb.AppendLine($"{indent}int t = raw < {totalTicks} ? raw : {totalTicks * 2} - raw;");
                    break;
                default:
                    sb.AppendLine($"{indent}int t = Math.Min(_tick, {totalTicks});");
                    break;
            }
            sb.AppendLine();
            sb.AppendLine($"{indent}// Find active keyframe segment");
            sb.AppendLine($"{indent}int seg = 0;");
            sb.AppendLine($"{indent}for (int i = 1; i < {frames.Count}; i++)");
            sb.AppendLine($"{indent}    if (t >= kfTick[i]) seg = i;");
            sb.AppendLine($"{indent}int next = (seg + 1 < {frames.Count}) ? seg + 1 : seg;");
            sb.AppendLine($"{indent}float span = kfTick[next] - kfTick[seg];");
            sb.AppendLine($"{indent}float frac = span > 0 ? (t - kfTick[seg]) / span : 0f;");
            sb.AppendLine($"{indent}float ef = Ease(frac, kfEase[seg]);");
            sb.AppendLine();

            if (animPos)
            {
                sb.AppendLine($"{indent}float ax = kfX[seg] + (kfX[next] - kfX[seg]) * ef;");
                sb.AppendLine($"{indent}float ay = kfY[seg] + (kfY[next] - kfY[seg]) * ef;");
            }
            if (animSize)
            {
                sb.AppendLine($"{indent}float aw = kfW[seg] + (kfW[next] - kfW[seg]) * ef;");
                sb.AppendLine($"{indent}float ah = kfH[seg] + (kfH[next] - kfH[seg]) * ef;");
            }
            if (animColor)
            {
                sb.AppendLine($"{indent}int ar = (int)(kfR[seg] + (kfR[next] - kfR[seg]) * ef);");
                sb.AppendLine($"{indent}int ag = (int)(kfG[seg] + (kfG[next] - kfG[seg]) * ef);");
                sb.AppendLine($"{indent}int ab = (int)(kfB[seg] + (kfB[next] - kfB[seg]) * ef);");
                sb.AppendLine($"{indent}int aa = (int)(kfA[seg] + (kfA[next] - kfA[seg]) * ef);");
            }
            if (animRot)
                sb.AppendLine($"{indent}float arot = kfRot[seg] + (kfRot[next] - kfRot[seg]) * ef;");
            if (animScale)
                sb.AppendLine($"{indent}float ascl = kfScl[seg] + (kfScl[next] - kfScl[seg]) * ef;");
        }

        private static void AppendInterpolationBodyGroup(StringBuilder sb,
            List<Keyframe> frames, int totalTicks, LoopMode loop,
            bool animPos, bool animSize, bool animColor, bool animRot, bool animScale,
            float baseX, float baseY, float baseW, float baseH, string indent)
        {
            // Same tick/segment logic
            AppendInterpolationBody(sb, frames, totalTicks, loop,
                false, false, false, false, false, indent); // base tick logic only
            sb.AppendLine();

            // Delta values for group
            if (animPos)
            {
                sb.AppendLine($"{indent}float dX = (kfX[seg] + (kfX[next] - kfX[seg]) * ef) - {baseX:F1}f;");
                sb.AppendLine($"{indent}float dY = (kfY[seg] + (kfY[next] - kfY[seg]) * ef) - {baseY:F1}f;");
            }
            if (animSize)
            {
                sb.AppendLine($"{indent}float dW = (kfW[seg] + (kfW[next] - kfW[seg]) * ef) - {baseW:F1}f;");
                sb.AppendLine($"{indent}float dH = (kfH[seg] + (kfH[next] - kfH[seg]) * ef) - {baseH:F1}f;");
            }
            if (animColor)
            {
                sb.AppendLine($"{indent}int ar = (int)(kfR[seg] + (kfR[next] - kfR[seg]) * ef);");
                sb.AppendLine($"{indent}int ag = (int)(kfG[seg] + (kfG[next] - kfG[seg]) * ef);");
                sb.AppendLine($"{indent}int ab = (int)(kfB[seg] + (kfB[next] - kfB[seg]) * ef);");
                sb.AppendLine($"{indent}int aa = (int)(kfA[seg] + (kfA[next] - kfA[seg]) * ef);");
            }
            if (animRot)
                sb.AppendLine($"{indent}float arot = kfRot[seg] + (kfRot[next] - kfRot[seg]) * ef;");
            if (animScale)
                sb.AppendLine($"{indent}float ascl = kfScl[seg] + (kfScl[next] - kfScl[seg]) * ef;");
        }

        /// <summary>
        /// Appends a sprite block inside a method body.
        /// <paramref name="isGroup"/> controls whether position/size use delta offsets (dX/dY)
        /// or absolute interpolated values (ax/ay).
        /// </summary>
        private static void AppendSpriteBlock(StringBuilder sb, SpriteEntry sp,
            bool isText, string alignStr, string listVar,
            bool animPos, bool animSize, bool animColor, bool animRot, bool animScale,
            bool isGroup, string indent)
        {
            string posVal, sizeVal, colVal, rotVal, sclVal;

            if (isGroup)
            {
                posVal  = animPos   ? $"new Vector2({sp.X:F1}f + dX, {sp.Y:F1}f + dY)"  : $"new Vector2({sp.X:F1}f, {sp.Y:F1}f)";
                sizeVal = animSize  ? $"new Vector2({sp.Width:F1}f + dW, {sp.Height:F1}f + dH)" : $"new Vector2({sp.Width:F1}f, {sp.Height:F1}f)";
            }
            else
            {
                posVal  = animPos   ? "new Vector2(ax, ay)"    : $"new Vector2({sp.X:F1}f, {sp.Y:F1}f)";
                sizeVal = animSize  ? "new Vector2(aw, ah)"    : $"new Vector2({sp.Width:F1}f, {sp.Height:F1}f)";
            }
            colVal  = animColor ? "new Color(ar, ag, ab, aa)" : $"new Color({sp.ColorR}, {sp.ColorG}, {sp.ColorB}, {sp.ColorA})";
            rotVal  = animRot   ? "arot"                      : $"{sp.Rotation:F4}f";
            sclVal  = animScale ? "ascl"                      : $"{sp.Scale:F2}f";

            sb.AppendLine($"{indent}{listVar}.Add(new MySprite");
            sb.AppendLine($"{indent}{{");
            if (isText)
            {
                sb.AppendLine($"{indent}    Type            = SpriteType.TEXT,");
                sb.AppendLine($"{indent}    Data            = \"{Esc(sp.Text)}\",");
                sb.AppendLine($"{indent}    Position        = {posVal},");
                sb.AppendLine($"{indent}    Color           = {colVal},");
                sb.AppendLine($"{indent}    FontId          = \"{Esc(sp.FontId)}\",");
                sb.AppendLine($"{indent}    Alignment       = {alignStr},");
                sb.AppendLine($"{indent}    RotationOrScale = {sclVal},");
            }
            else
            {
                sb.AppendLine($"{indent}    Type            = SpriteType.TEXTURE,");
                sb.AppendLine($"{indent}    Data            = \"{Esc(sp.SpriteName)}\",");
                sb.AppendLine($"{indent}    Position        = {posVal},");
                sb.AppendLine($"{indent}    Size            = {sizeVal},");
                sb.AppendLine($"{indent}    Color           = {colVal},");
                sb.AppendLine($"{indent}    Alignment       = {alignStr},");
                sb.AppendLine($"{indent}    RotationOrScale = {rotVal},");
            }
            sb.AppendLine($"{indent}}});");
        }
    }
}
