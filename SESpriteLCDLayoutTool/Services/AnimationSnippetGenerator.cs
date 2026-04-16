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
    public static partial class AnimationSnippetGenerator
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

    }
}
