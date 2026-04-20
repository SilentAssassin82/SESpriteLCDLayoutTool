using System;
using System.Collections.Generic;
using System.Text;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool.Models
{
    // ── Animation effect data model ─────────────────────────────────────────
    //
    // Each effect is a pure data object that knows how to emit the three
    // structural sections needed for code injection:
    //   1. Fields   — class-level declarations (int _tick, arrays, etc.)
    //   2. Compute  — per-frame logic lines placed before the sprite's Add block
    //   3. Property overrides — which MySprite properties use animated values
    //
    // A sprite can carry multiple effects (e.g. Rotate + ColorCycle).
    // The injector composes them: fields are deduplicated, compute lines are
    // concatenated, and property overrides are merged (last-write-wins per prop).
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A single animation effect that can be applied to a sprite.
    /// Multiple effects can stack on one sprite.
    /// </summary>
    public interface IAnimationEffect
    {
        /// <summary>The animation type for UI display and serialisation.</summary>
        AnimationEffectType EffectType { get; }

        /// <summary>
        /// Emit class/struct-level field declarations.
        /// The <paramref name="suffix"/> disambiguates when multiple sprites
        /// are animated (e.g. "_tick" vs "_tick2").
        /// Return lines WITHOUT leading/trailing blank lines.
        /// </summary>
        List<string> EmitFields(string suffix);

        /// <summary>
        /// Emit per-frame computation lines placed inside the render method,
        /// immediately before the sprite's Add block.
        /// <paramref name="suffix"/> matches the fields.
        /// </summary>
        List<string> EmitCompute(string suffix);

        /// <summary>
        /// Return property-name → expression-string pairs.
        /// The injector replaces the matching MySprite initialiser property
        /// with the expression.  e.g. { "RotationOrScale", "_tick * 0.05f" }
        /// </summary>
        Dictionary<string, string> GetPropertyOverrides(string suffix);

        /// <summary>
        /// Whether this effect needs the shared <c>_tick</c> counter field
        /// and the <c>_tick++;</c> line in the render method.
        /// Most effects do; the injector deduplicates them.
        /// </summary>
        bool NeedsTick { get; }

        /// <summary>
        /// Whether this effect needs the shared Ease() helper method.
        /// Only keyframe effects typically need this.
        /// </summary>
        bool NeedsEaseHelper { get; }
    }

    public enum AnimationEffectType
    {
        Rotate,
        Oscillate,
        Pulse,
        Fade,
        Blink,
        ColorCycle,
        Keyframed,
        GroupFollower,
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Concrete effects
    // ═══════════════════════════════════════════════════════════════════════

    public class RotateEffect : IAnimationEffect
    {
        public float Speed { get; set; } = 0.05f;
        public bool Clockwise { get; set; } = true;

        public AnimationEffectType EffectType => AnimationEffectType.Rotate;
        public bool NeedsTick => true;
        public bool NeedsEaseHelper => false;

        public List<string> EmitFields(string suffix) => new List<string>();
        // No extra fields — just _tick (shared)

        public List<string> EmitCompute(string suffix) => new List<string>();
        // Rotation is computed inline in the property override

        public Dictionary<string, string> GetPropertyOverrides(string suffix)
        {
            string dir = Clockwise ? "" : "-";
            return new Dictionary<string, string>
            {
                { "RotationOrScale", $"{dir}_tick * {Speed}f" }
            };
        }
    }

    public class OscillateEffect : IAnimationEffect
    {
        public enum Axis { X, Y, Both }

        public Axis OscAxis { get; set; } = Axis.X;
        public float Amplitude { get; set; } = 50f;
        public float Speed { get; set; } = 0.05f;

        public AnimationEffectType EffectType => AnimationEffectType.Oscillate;
        public bool NeedsTick => true;
        public bool NeedsEaseHelper => false;

        public List<string> EmitFields(string suffix) => new List<string>();

        public List<string> EmitCompute(string suffix)
        {
            return new List<string>
            {
                $"float oscOffset{suffix} = (float)Math.Sin(_tick * {Speed}f) * {Amplitude}f;"
            };
        }

        public Dictionary<string, string> GetPropertyOverrides(string suffix)
        {
            // The injector needs the sprite's base position to compose.
            // We use placeholder tokens that the injector replaces with actual values.
            switch (OscAxis)
            {
                case Axis.X:
                    return new Dictionary<string, string>
                    {
                        { "Position", $"new Vector2({{baseX}} + oscOffset{suffix}, {{baseY}})" }
                    };
                case Axis.Y:
                    return new Dictionary<string, string>
                    {
                        { "Position", $"new Vector2({{baseX}}, {{baseY}} + oscOffset{suffix})" }
                    };
                default: // Both
                    return new Dictionary<string, string>
                    {
                        { "Position", $"new Vector2({{baseX}} + oscOffset{suffix}, {{baseY}} + oscOffset{suffix})" }
                    };
            }
        }
    }

    public class PulseEffect : IAnimationEffect
    {
        public float Amplitude { get; set; } = 0.3f;
        public float Speed { get; set; } = 0.08f;

        public AnimationEffectType EffectType => AnimationEffectType.Pulse;
        public bool NeedsTick => true;
        public bool NeedsEaseHelper => false;

        public List<string> EmitFields(string suffix) => new List<string>();

        public List<string> EmitCompute(string suffix)
        {
            return new List<string>
            {
                $"float pulseScale{suffix} = 1f + (float)Math.Sin(_tick * {Speed}f) * {Amplitude}f;"
            };
        }

        public Dictionary<string, string> GetPropertyOverrides(string suffix)
        {
            return new Dictionary<string, string>
            {
                { "Size", $"new Vector2({{baseW}} * pulseScale{suffix}, {{baseH}} * pulseScale{suffix})" }
            };
        }
    }

    public class FadeEffect : IAnimationEffect
    {
        public int MinAlpha { get; set; } = 0;
        public int MaxAlpha { get; set; } = 255;
        public float Speed { get; set; } = 0.06f;

        public AnimationEffectType EffectType => AnimationEffectType.Fade;
        public bool NeedsTick => true;
        public bool NeedsEaseHelper => false;

        public List<string> EmitFields(string suffix) => new List<string>();

        public List<string> EmitCompute(string suffix)
        {
            int range = MaxAlpha - MinAlpha;
            return new List<string>
            {
                $"int fadeAlpha{suffix} = {MinAlpha} + (int)(((float)Math.Sin(_tick * {Speed}f) + 1f) / 2f * {range});"
            };
        }

        public Dictionary<string, string> GetPropertyOverrides(string suffix)
        {
            // Fade only contributes to the alpha channel. The injector composes
            // this with any other Color-producing effect (ColorCycle, Keyframe,
            // or the sprite's static RGB) so Fade never cancels RGB animation.
            return new Dictionary<string, string>
            {
                { "Color.A", $"fadeAlpha{suffix}" }
            };
        }
    }

    public class BlinkEffect : IAnimationEffect
    {
        public int OnTicks { get; set; } = 30;
        public int OffTicks { get; set; } = 30;

        public AnimationEffectType EffectType => AnimationEffectType.Blink;
        public bool NeedsTick => true;
        public bool NeedsEaseHelper => false;

        public List<string> EmitFields(string suffix) => new List<string>();

        public List<string> EmitCompute(string suffix)
        {
            int period = OnTicks + OffTicks;
            return new List<string>
            {
                $"bool blinkVisible{suffix} = (_tick % {period}) < {OnTicks};"
            };
        }

        public Dictionary<string, string> GetPropertyOverrides(string suffix)
        {
            // Blink wraps the entire Add block in an if — handled specially by the injector
            return new Dictionary<string, string>
            {
                { "__blink_guard", $"blinkVisible{suffix}" }
            };
        }
    }

    public class ColorCycleEffect : IAnimationEffect
    {
        public float Speed { get; set; } = 2f;
        public float Brightness { get; set; } = 1.0f;

        public AnimationEffectType EffectType => AnimationEffectType.ColorCycle;
        public bool NeedsTick => true;
        public bool NeedsEaseHelper => false;

        public List<string> EmitFields(string suffix) => new List<string>();

        public List<string> EmitCompute(string suffix)
        {
            // HSV→RGB conversion emitted as compute lines
            return new List<string>
            {
                $"float hue{suffix} = (_tick * {Speed}f) % 360f;",
                $"float br{suffix} = {Brightness}f;",
                $"float hSec{suffix} = hue{suffix} / 60f;",
                $"int hi{suffix} = (int)Math.Floor(hSec{suffix}) % 6;",
                $"float ff{suffix} = hSec{suffix} - (float)Math.Floor(hSec{suffix});",
                $"float qq{suffix} = br{suffix} * (1f - ff{suffix});",
                $"float tt{suffix} = br{suffix} * ff{suffix};",
                $"float cr{suffix}, cg{suffix}, cb{suffix};",
                $"switch (hi{suffix})",
                "{",
                $"    case 0: cr{suffix} = br{suffix}; cg{suffix} = tt{suffix}; cb{suffix} = 0;  break;",
                $"    case 1: cr{suffix} = qq{suffix}; cg{suffix} = br{suffix}; cb{suffix} = 0;  break;",
                $"    case 2: cr{suffix} = 0;  cg{suffix} = br{suffix}; cb{suffix} = tt{suffix};  break;",
                $"    case 3: cr{suffix} = 0;  cg{suffix} = qq{suffix}; cb{suffix} = br{suffix}; break;",
                $"    case 4: cr{suffix} = tt{suffix};  cg{suffix} = 0;  cb{suffix} = br{suffix}; break;",
                $"    default: cr{suffix} = br{suffix}; cg{suffix} = 0;  cb{suffix} = qq{suffix};  break;",
                "}",
                $"Color cycleColor{suffix} = new Color((int)(cr{suffix} * 255), (int)(cg{suffix} * 255), (int)(cb{suffix} * 255), 255);",
            };
        }

        public Dictionary<string, string> GetPropertyOverrides(string suffix)
        {
            return new Dictionary<string, string>
            {
                { "Color", $"cycleColor{suffix}" }
            };
        }
    }

    public class KeyframeEffect : IAnimationEffect
    {
        public List<Keyframe> Keyframes { get; set; } = new List<Keyframe>();
        public LoopMode Loop { get; set; } = LoopMode.Loop;

        public AnimationEffectType EffectType => AnimationEffectType.Keyframed;
        public bool NeedsTick => true;
        public bool NeedsEaseHelper => true;

        public List<string> EmitFields(string suffix)
        {
            if (Keyframes == null || Keyframes.Count == 0) return new List<string>();

            var lines = new List<string>();
            int count = Keyframes.Count;

            // Tick array
            lines.Add($"int[] kfTick{suffix} = {{ {string.Join(", ", Keyframes.ConvertAll(k => k.Tick.ToString()))} }};");

            // Easing array
            lines.Add($"int[] kfEase{suffix} = {{ {string.Join(", ", Keyframes.ConvertAll(k => ((int)k.EasingToNext).ToString()))} }};");

            // Only emit arrays for properties that actually change
            if (HasProperty(k => k.X) || HasProperty(k => k.Y))
            {
                lines.Add($"float[] kfX{suffix} = {{ {string.Join(", ", Keyframes.ConvertAll(k => $"{k.X ?? 0:F1}f"))} }};");
                lines.Add($"float[] kfY{suffix} = {{ {string.Join(", ", Keyframes.ConvertAll(k => $"{k.Y ?? 0:F1}f"))} }};");
            }
            if (HasProperty(k => k.Width) || HasProperty(k => k.Height))
            {
                lines.Add($"float[] kfW{suffix} = {{ {string.Join(", ", Keyframes.ConvertAll(k => $"{k.Width ?? 0:F1}f"))} }};");
                lines.Add($"float[] kfH{suffix} = {{ {string.Join(", ", Keyframes.ConvertAll(k => $"{k.Height ?? 0:F1}f"))} }};");
            }
            if (HasProperty(k => k.ColorR))
            {
                lines.Add($"float[] kfR{suffix} = {{ {string.Join(", ", Keyframes.ConvertAll(k => $"{k.ColorR ?? 255}"))} }};");
                lines.Add($"float[] kfG{suffix} = {{ {string.Join(", ", Keyframes.ConvertAll(k => $"{k.ColorG ?? 255}"))} }};");
                lines.Add($"float[] kfB{suffix} = {{ {string.Join(", ", Keyframes.ConvertAll(k => $"{k.ColorB ?? 255}"))} }};");
                lines.Add($"float[] kfA{suffix} = {{ {string.Join(", ", Keyframes.ConvertAll(k => $"{k.ColorA ?? 255}"))} }};");
            }
            if (HasProperty(k => k.Rotation))
                lines.Add($"float[] kfRot{suffix} = {{ {string.Join(", ", Keyframes.ConvertAll(k => $"{k.Rotation ?? 0:F4}f"))} }};");
            if (HasProperty(k => k.Scale))
                lines.Add($"float[] kfScl{suffix} = {{ {string.Join(", ", Keyframes.ConvertAll(k => $"{k.Scale ?? 1:F2}f"))} }};");

            return lines;
        }

        public List<string> EmitCompute(string suffix)
        {
            if (Keyframes == null || Keyframes.Count == 0) return new List<string>();

            int count = Keyframes.Count;
            int totalTicks = Keyframes[Keyframes.Count - 1].Tick;
            var lines = new List<string>();

            // Tick and segment finding
            lines.Add($"int t{suffix} = _tick % {totalTicks};");
            lines.Add($"int seg{suffix} = 0;");
            lines.Add($"for (int i = 1; i < {count}; i++)");
            lines.Add($"    if (t{suffix} >= kfTick{suffix}[i]) seg{suffix} = i;");
            lines.Add($"int next{suffix} = (seg{suffix} + 1 < {count}) ? seg{suffix} + 1 : seg{suffix};");
            lines.Add($"float span{suffix} = kfTick{suffix}[next{suffix}] - kfTick{suffix}[seg{suffix}];");
            lines.Add($"float frac{suffix} = span{suffix} > 0 ? (t{suffix} - kfTick{suffix}[seg{suffix}]) / span{suffix} : 0f;");
            lines.Add($"float ef{suffix} = Ease(frac{suffix}, kfEase{suffix}[seg{suffix}]);");

            // Interpolation lines for each animated property
            if (HasProperty(k => k.X) || HasProperty(k => k.Y))
            {
                lines.Add($"float ax{suffix} = kfX{suffix}[seg{suffix}] + (kfX{suffix}[next{suffix}] - kfX{suffix}[seg{suffix}]) * ef{suffix};");
                lines.Add($"float ay{suffix} = kfY{suffix}[seg{suffix}] + (kfY{suffix}[next{suffix}] - kfY{suffix}[seg{suffix}]) * ef{suffix};");
            }
            if (HasProperty(k => k.Width) || HasProperty(k => k.Height))
            {
                lines.Add($"float aw{suffix} = kfW{suffix}[seg{suffix}] + (kfW{suffix}[next{suffix}] - kfW{suffix}[seg{suffix}]) * ef{suffix};");
                lines.Add($"float ah{suffix} = kfH{suffix}[seg{suffix}] + (kfH{suffix}[next{suffix}] - kfH{suffix}[seg{suffix}]) * ef{suffix};");
            }
            if (HasProperty(k => k.ColorR))
            {
                lines.Add($"int ar{suffix} = (int)(kfR{suffix}[seg{suffix}] + (kfR{suffix}[next{suffix}] - kfR{suffix}[seg{suffix}]) * ef{suffix});");
                lines.Add($"int ag{suffix} = (int)(kfG{suffix}[seg{suffix}] + (kfG{suffix}[next{suffix}] - kfG{suffix}[seg{suffix}]) * ef{suffix});");
                lines.Add($"int ab{suffix} = (int)(kfB{suffix}[seg{suffix}] + (kfB{suffix}[next{suffix}] - kfB{suffix}[seg{suffix}]) * ef{suffix});");
                lines.Add($"int aa{suffix} = (int)(kfA{suffix}[seg{suffix}] + (kfA{suffix}[next{suffix}] - kfA{suffix}[seg{suffix}]) * ef{suffix});");
            }
            if (HasProperty(k => k.Rotation))
                lines.Add($"float arot{suffix} = kfRot{suffix}[seg{suffix}] + (kfRot{suffix}[next{suffix}] - kfRot{suffix}[seg{suffix}]) * ef{suffix};");
            if (HasProperty(k => k.Scale))
                lines.Add($"float ascl{suffix} = kfScl{suffix}[seg{suffix}] + (kfScl{suffix}[next{suffix}] - kfScl{suffix}[seg{suffix}]) * ef{suffix};");

            return lines;
        }

        public Dictionary<string, string> GetPropertyOverrides(string suffix)
        {
            var overrides = new Dictionary<string, string>();

            if (HasProperty(k => k.X) || HasProperty(k => k.Y))
                overrides["Position"] = $"new Vector2(ax{suffix}, ay{suffix})";
            if (HasProperty(k => k.Width) || HasProperty(k => k.Height))
                overrides["Size"] = $"new Vector2(aw{suffix}, ah{suffix})";
            if (HasProperty(k => k.ColorR))
                overrides["Color"] = $"new Color(ar{suffix}, ag{suffix}, ab{suffix}, aa{suffix})";
            if (HasProperty(k => k.Rotation))
                overrides["RotationOrScale"] = $"arot{suffix}";
            if (HasProperty(k => k.Scale))
                overrides["RotationOrScale"] = $"ascl{suffix}";

            return overrides;
        }

        internal bool HasProperty(Func<Keyframe, object> selector)
        {
            if (Keyframes == null || Keyframes.Count < 2) return false;
            var first = selector(Keyframes[0]);
            for (int i = 1; i < Keyframes.Count; i++)
            {
                var val = selector(Keyframes[i]);
                if (!Equals(first, val)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// A follower in an animation group. Emits no fields or compute — instead
    /// references the leader's computed variables (with the leader's suffix)
    /// to apply the same animated deltas relative to the follower's own base.
    /// For position/size, uses delta-based offsets so each follower keeps its
    /// own static position but moves in tandem with the leader.
    /// </summary>
    public class GroupFollowerEffect : IAnimationEffect
    {
        /// <summary>The leader's KeyframeEffect (to check which properties are animated).</summary>
        public KeyframeEffect LeaderEffect { get; set; }

        /// <summary>The suffix assigned to the leader sprite in the injector.</summary>
        public string LeaderSuffix { get; set; } = "";

        /// <summary>The leader sprite's base values (first keyframe or sprite defaults).</summary>
        public float LeaderBaseX { get; set; }
        public float LeaderBaseY { get; set; }
        public float LeaderBaseW { get; set; }
        public float LeaderBaseH { get; set; }

        /// <summary>The follower sprite's own static base values.</summary>
        public float FollowerBaseX { get; set; }
        public float FollowerBaseY { get; set; }
        public float FollowerBaseW { get; set; }
        public float FollowerBaseH { get; set; }

        public AnimationEffectType EffectType => AnimationEffectType.GroupFollower;
        public bool NeedsTick => false;  // leader already handles tick
        public bool NeedsEaseHelper => false;  // leader already handles ease

        public List<string> EmitFields(string suffix) => new List<string>();  // leader owns fields
        public List<string> EmitCompute(string suffix) => new List<string>(); // leader owns compute

        public Dictionary<string, string> GetPropertyOverrides(string suffix)
        {
            // suffix here is the follower's suffix — we ignore it and use LeaderSuffix
            var s = LeaderSuffix;
            var overrides = new Dictionary<string, string>();

            if (LeaderEffect == null) return overrides;

            // Position: follower_base + (leader_animated - leader_base) = follower_base + delta
            if (LeaderEffect.HasProperty(k => k.X) || LeaderEffect.HasProperty(k => k.Y))
                overrides["Position"] = $"new Vector2({FollowerBaseX:F1}f + (ax{s} - {LeaderBaseX:F1}f), {FollowerBaseY:F1}f + (ay{s} - {LeaderBaseY:F1}f))";

            // Size: follower_base + delta
            if (LeaderEffect.HasProperty(k => k.Width) || LeaderEffect.HasProperty(k => k.Height))
                overrides["Size"] = $"new Vector2({FollowerBaseW:F1}f + (aw{s} - {LeaderBaseW:F1}f), {FollowerBaseH:F1}f + (ah{s} - {LeaderBaseH:F1}f))";

            // Color: same as leader (absolute, not delta)
            if (LeaderEffect.HasProperty(k => k.ColorR))
                overrides["Color"] = $"new Color(ar{s}, ag{s}, ab{s}, aa{s})";

            // Rotation: same as leader (absolute)
            if (LeaderEffect.HasProperty(k => k.Rotation))
                overrides["RotationOrScale"] = $"arot{s}";

            // Scale: same as leader (absolute)
            if (LeaderEffect.HasProperty(k => k.Scale))
                overrides["RotationOrScale"] = $"ascl{s}";

            return overrides;
        }
    }

    // ── Ease helper text (shared) ───────────────────────────────────────────

    public static class EaseHelperText
    {
        public static readonly string EaseMethod = @"public float Ease(float t, int easeType)
{
    switch (easeType)
    {
        case 0: return t;
        case 1: return (float)(0.5 - 0.5 * Math.Cos(t * Math.PI));
        case 2: return t * t;
        case 3: return 1f - (1f - t) * (1f - t);
        case 4: return t < 0.5f ? 2f * t * t : 1f - (float)Math.Pow(-2 * t + 2, 2) / 2f;
        case 5:
        {
            float b = 1f - t;
            if (b < 1f / 2.75f) return 1f - 7.5625f * b * b;
            if (b < 2f / 2.75f) { b -= 1.5f / 2.75f; return 1f - (7.5625f * b * b + 0.75f); }
            if (b < 2.5f / 2.75f) { b -= 2.25f / 2.75f; return 1f - (7.5625f * b * b + 0.9375f); }
            b -= 2.625f / 2.75f; return 1f - (7.5625f * b * b + 0.984375f);
        }
        case 6:
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            return (float)(-Math.Pow(2, 10 * t - 10) * Math.Sin((t * 10 - 10.75) * (2 * Math.PI / 3)));
        }
        default: return t;
    }
}";
    }
}
