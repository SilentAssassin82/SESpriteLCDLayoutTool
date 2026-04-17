using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SESpriteLCDLayoutTool.Models;

namespace SESpriteLCDLayoutTool.Services
{
    public static partial class AnimationSnippetGenerator
    {
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

            // Get unique variable names from registry based on sprite source line
            // IMPORTANT: call GetAnimationIndex ONCE, then use index-based overloads to avoid counter increment per call
            int animIndex = MultiAnimationRegistry.GetAnimationIndex(sprite.SourceLineNumber);
            string tickVar = MultiAnimationRegistry.GetTickVariableName(animIndex, true);
            string tickArr = MultiAnimationRegistry.GetTickArrayName(animIndex, true);
            string easeArr = MultiAnimationRegistry.GetEasingArrayName(animIndex, true);
            string axArr = MultiAnimationRegistry.GetPosXArrayName(animIndex, true);
            string ayArr = MultiAnimationRegistry.GetPosYArrayName(animIndex, true);
            string awArr = MultiAnimationRegistry.GetWidthArrayName(animIndex, true);
            string ahArr = MultiAnimationRegistry.GetHeightArrayName(animIndex, true);
            string arArr = MultiAnimationRegistry.GetColorRArrayName(animIndex, true);
            string agArr = MultiAnimationRegistry.GetColorGArrayName(animIndex, true);
            string abArr = MultiAnimationRegistry.GetColorBArrayName(animIndex, true);
            string aaArr = MultiAnimationRegistry.GetColorAArrayName(animIndex, true);
            string rotArr = MultiAnimationRegistry.GetRotArrayName(animIndex, true);
            string sclArr = MultiAnimationRegistry.GetScaleArrayName(animIndex, true);
            string axVar = MultiAnimationRegistry.GetPositionXVariableName(animIndex, true);
            string ayVar = MultiAnimationRegistry.GetPositionYVariableName(animIndex, true);
            string awVar = MultiAnimationRegistry.GetSizeWidthVariableName(animIndex, true);
            string ahVar = MultiAnimationRegistry.GetSizeHeightVariableName(animIndex, true);
            string arVar = MultiAnimationRegistry.GetColorRVariableName(animIndex, true);
            string agVar = MultiAnimationRegistry.GetColorGVariableName(animIndex, true);
            string abVar = MultiAnimationRegistry.GetColorBVariableName(animIndex, true);
            string aaVar = MultiAnimationRegistry.GetColorAVariableName(animIndex, true);
            string arotVar = MultiAnimationRegistry.GetRotationVariableName(animIndex, true);
            string asclVar = MultiAnimationRegistry.GetScaleVariableName(animIndex, true);

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
            sb.AppendLine($"// {frames.Count} keyframes over {totalTicks} ticks  |  Loop: {kp.Loop}  |  Animation Index: {animIndex}");
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
            sb.AppendLine($"int {tickVar} = 0;");
            sb.AppendLine();

            // Tick array
            sb.Append("int[] ");
            sb.Append(tickArr);
            sb.Append(" = { ");
            sb.Append(string.Join(", ", frames.Select(k => k.Tick.ToString())));
            sb.AppendLine(" };");

            // Easing array
            sb.Append("int[] ");
            sb.Append(easeArr);
            sb.Append(" = { ");
            sb.Append(string.Join(", ", frames.Select(k => ((int)k.EasingToNext).ToString())));
            sb.AppendLine(" };");

            // Property arrays — only for animated properties
            if (animPos)
            {
                sb.Append("float[] ");
                sb.Append(axArr);
                sb.Append(" = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.X ?? sprite.X:F1}f")));
                sb.AppendLine(" };");
                sb.Append("float[] ");
                sb.Append(ayArr);
                sb.Append(" = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Y ?? sprite.Y:F1}f")));
                sb.AppendLine(" };");
            }
            if (animSize)
            {
                sb.Append("float[] ");
                sb.Append(awArr);
                sb.Append(" = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Width ?? sprite.Width:F1}f")));
                sb.AppendLine(" };");
                sb.Append("float[] ");
                sb.Append(ahArr);
                sb.Append(" = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Height ?? sprite.Height:F1}f")));
                sb.AppendLine(" };");
            }
            if (animColor)
            {
                sb.Append("int[] ");
                sb.Append(arArr);
                sb.Append(" = { ");
                sb.Append(string.Join(", ", frames.Select(k => (k.ColorR ?? sprite.ColorR).ToString())));
                sb.AppendLine(" };");
                sb.Append("int[] ");
                sb.Append(agArr);
                sb.Append(" = { ");
                sb.Append(string.Join(", ", frames.Select(k => (k.ColorG ?? sprite.ColorG).ToString())));
                sb.AppendLine(" };");
                sb.Append("int[] ");
                sb.Append(abArr);
                sb.Append(" = { ");
                sb.Append(string.Join(", ", frames.Select(k => (k.ColorB ?? sprite.ColorB).ToString())));
                sb.AppendLine(" };");
                sb.Append("int[] ");
                sb.Append(aaArr);
                sb.Append(" = { ");
                sb.Append(string.Join(", ", frames.Select(k => (k.ColorA ?? sprite.ColorA).ToString())));
                sb.AppendLine(" };");
            }
            if (animRot)
            {
                sb.Append("float[] ");
                sb.Append(rotArr);
                sb.Append(" = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Rotation ?? sprite.Rotation:F4}f")));
                sb.AppendLine(" };");
            }
            if (animScale)
            {
                sb.Append("float[] ");
                sb.Append(sclArr);
                sb.Append(" = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Scale ?? sprite.Scale:F2}f")));
                sb.AppendLine(" };");
            }

            sb.AppendLine();

            // ── Interpolation logic ──
            string ls = MultiAnimationRegistry.Suffix(animIndex); // local variable suffix
            sb.AppendLine(RenderHint(kp.TargetScript));
            sb.AppendLine($"{tickVar}++;");

            // Tick wrapping based on loop mode
            switch (kp.Loop)
            {
                case LoopMode.Loop:
                    sb.AppendLine($"int t{ls} = {tickVar} % {totalTicks};");
                    break;
                case LoopMode.PingPong:
                    sb.AppendLine($"int raw{ls} = {tickVar} % {totalTicks * 2};");
                    sb.AppendLine($"int t{ls} = raw{ls} < {totalTicks} ? raw{ls} : {totalTicks * 2} - raw{ls};");
                    break;
                default: // Once
                    sb.AppendLine($"int t{ls} = Math.Min({tickVar}, {totalTicks});");
                    break;
            }
            sb.AppendLine();

            // Find keyframe segment
            sb.AppendLine("// Find active keyframe segment");
            sb.AppendLine($"int seg{ls} = 0;");
            sb.AppendLine($"for (int i = 1; i < {frames.Count}; i++)");
            sb.AppendLine($"    if (t{ls} >= {tickArr}[i]) seg{ls} = i;");
            sb.AppendLine($"int next{ls} = (seg{ls} + 1 < {frames.Count}) ? seg{ls} + 1 : seg{ls};");
            sb.AppendLine($"float span{ls} = {tickArr}[next{ls}] - {tickArr}[seg{ls}];");
            sb.AppendLine($"float frac{ls} = span{ls} > 0 ? (t{ls} - {tickArr}[seg{ls}]) / span{ls} : 0f;");
            sb.AppendLine($"float ef{ls} = Ease(frac{ls}, {easeArr}[seg{ls}]);");
            sb.AppendLine();

            // Interpolated variables
            if (animPos)
            {
                sb.AppendLine($"float {axVar}_interp = {axArr}[seg{ls}] + ({axArr}[next{ls}] - {axArr}[seg{ls}]) * ef{ls};");
                sb.AppendLine($"float {ayVar}_interp = {ayArr}[seg{ls}] + ({ayArr}[next{ls}] - {ayArr}[seg{ls}]) * ef{ls};");
            }
            if (animSize)
            {
                sb.AppendLine($"float {awVar}_interp = {awArr}[seg{ls}] + ({awArr}[next{ls}] - {awArr}[seg{ls}]) * ef{ls};");
                sb.AppendLine($"float {ahVar}_interp = {ahArr}[seg{ls}] + ({ahArr}[next{ls}] - {ahArr}[seg{ls}]) * ef{ls};");
            }
            if (animColor)
            {
                sb.AppendLine($"int {arVar}_interp = (int)({arArr}[seg{ls}] + ({arArr}[next{ls}] - {arArr}[seg{ls}]) * ef{ls});");
                sb.AppendLine($"int {agVar}_interp = (int)({agArr}[seg{ls}] + ({agArr}[next{ls}] - {agArr}[seg{ls}]) * ef{ls});");
                sb.AppendLine($"int {abVar}_interp = (int)({abArr}[seg{ls}] + ({abArr}[next{ls}] - {abArr}[seg{ls}]) * ef{ls});");
                sb.AppendLine($"int {aaVar}_interp = (int)({aaArr}[seg{ls}] + ({aaArr}[next{ls}] - {aaArr}[seg{ls}]) * ef{ls});");
            }
            if (animRot)
                sb.AppendLine($"float {arotVar}_interp = {rotArr}[seg{ls}] + ({rotArr}[next{ls}] - {rotArr}[seg{ls}]) * ef{ls};");
            if (animScale)
                sb.AppendLine($"float {asclVar}_interp = {sclArr}[seg{ls}] + ({sclArr}[next{ls}] - {sclArr}[seg{ls}]) * ef{ls};");

            sb.AppendLine();

            // ── Sprite block ──
            string posVal  = animPos   ? $"new Vector2({axVar}_interp, {ayVar}_interp)"    : $"new Vector2({sprite.X:F1}f, {sprite.Y:F1}f)";
            string sizeVal = animSize  ? $"new Vector2({awVar}_interp, {ahVar}_interp)"    : $"new Vector2({sprite.Width:F1}f, {sprite.Height:F1}f)";
            string colVal  = animColor ? $"new Color({arVar}_interp, {agVar}_interp, {abVar}_interp, {aaVar}_interp)" : $"new Color({sprite.ColorR}, {sprite.ColorG}, {sprite.ColorB}, {sprite.ColorA})";
            string rotVal  = animRot   ? $"{arotVar}_interp"                   : $"{sprite.Rotation:F4}f";
            string sclVal  = animScale ? $"{asclVar}_interp"                   : $"{sprite.Scale:F2}f";

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

            // Get unique variable names from registry (use leader's line number for the group tick)
            // IMPORTANT: call GetAnimationIndex ONCE, then use index-based overloads
            int animIndex = MultiAnimationRegistry.GetAnimationIndex(leader.SourceLineNumber);
            string tickVar = MultiAnimationRegistry.GetTickVariableName(animIndex, true);
            string tickArr = MultiAnimationRegistry.GetTickArrayName(animIndex, true);
            string easeArr = MultiAnimationRegistry.GetEasingArrayName(animIndex, true);
            string axArr = MultiAnimationRegistry.GetPosXArrayName(animIndex, true);
            string ayArr = MultiAnimationRegistry.GetPosYArrayName(animIndex, true);
            string awArr = MultiAnimationRegistry.GetWidthArrayName(animIndex, true);
            string ahArr = MultiAnimationRegistry.GetHeightArrayName(animIndex, true);
            string arArr = MultiAnimationRegistry.GetColorRArrayName(animIndex, true);
            string agArr = MultiAnimationRegistry.GetColorGArrayName(animIndex, true);
            string abArr = MultiAnimationRegistry.GetColorBArrayName(animIndex, true);
            string aaArr = MultiAnimationRegistry.GetColorAArrayName(animIndex, true);
            string rotArr = MultiAnimationRegistry.GetRotArrayName(animIndex, true);
            string sclArr = MultiAnimationRegistry.GetScaleArrayName(animIndex, true);
            string axVar = MultiAnimationRegistry.GetPositionXVariableName(animIndex, true);
            string ayVar = MultiAnimationRegistry.GetPositionYVariableName(animIndex, true);
            string awVar = MultiAnimationRegistry.GetSizeWidthVariableName(animIndex, true);
            string ahVar = MultiAnimationRegistry.GetSizeHeightVariableName(animIndex, true);
            string arVar = MultiAnimationRegistry.GetColorRVariableName(animIndex, true);
            string agVar = MultiAnimationRegistry.GetColorGVariableName(animIndex, true);
            string abVar = MultiAnimationRegistry.GetColorBVariableName(animIndex, true);
            string aaVar = MultiAnimationRegistry.GetColorAVariableName(animIndex, true);
            string arotVar = MultiAnimationRegistry.GetRotationVariableName(animIndex, true);
            string asclVar = MultiAnimationRegistry.GetScaleVariableName(animIndex, true);

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
            sb.AppendLine($"// {frames.Count} keyframes over {totalTicks} ticks  |  Loop: {kp.Loop}  |  Group: {allSprites.Count} sprites  |  Animation Index: {animIndex}");
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
            sb.AppendLine($"int {tickVar} = 0;");
            sb.AppendLine();

            sb.Append("int[] ");
            sb.Append(tickArr);
            sb.Append(" = { ");
            sb.Append(string.Join(", ", frames.Select(k => k.Tick.ToString())));
            sb.AppendLine(" };");

            sb.Append("int[] ");
            sb.Append(easeArr);
            sb.Append(" = { ");
            sb.Append(string.Join(", ", frames.Select(k => ((int)k.EasingToNext).ToString())));
            sb.AppendLine(" };");

            if (animPos)
            {
                sb.Append("float[] ");
                sb.Append(axArr);
                sb.Append(" = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.X ?? leader.X:F1}f")));
                sb.AppendLine(" };");
                sb.Append("float[] ");
                sb.Append(ayArr);
                sb.Append(" = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Y ?? leader.Y:F1}f")));
                sb.AppendLine(" };");
            }
            if (animSize)
            {
                sb.Append("float[] ");
                sb.Append(awArr);
                sb.Append(" = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Width ?? leader.Width:F1}f")));
                sb.AppendLine(" };");
                sb.Append("float[] ");
                sb.Append(ahArr);
                sb.Append(" = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Height ?? leader.Height:F1}f")));
                sb.AppendLine(" };");
            }
            if (animColor)
            {
                sb.Append("int[] ");
                sb.Append(arArr);
                sb.Append(" = { ");
                sb.Append(string.Join(", ", frames.Select(k => (k.ColorR ?? leader.ColorR).ToString())));
                sb.AppendLine(" };");
                sb.Append("int[] ");
                sb.Append(agArr);
                sb.Append(" = { ");
                sb.Append(string.Join(", ", frames.Select(k => (k.ColorG ?? leader.ColorG).ToString())));
                sb.AppendLine(" };");
                sb.Append("int[] ");
                sb.Append(abArr);
                sb.Append(" = { ");
                sb.Append(string.Join(", ", frames.Select(k => (k.ColorB ?? leader.ColorB).ToString())));
                sb.AppendLine(" };");
                sb.Append("int[] ");
                sb.Append(aaArr);
                sb.Append(" = { ");
                sb.Append(string.Join(", ", frames.Select(k => (k.ColorA ?? leader.ColorA).ToString())));
                sb.AppendLine(" };");
            }
            if (animRot)
            {
                sb.Append("float[] ");
                sb.Append(rotArr);
                sb.Append(" = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Rotation ?? leader.Rotation:F4}f")));
                sb.AppendLine(" };");
            }
            if (animScale)
            {
                sb.Append("float[] ");
                sb.Append(sclArr);
                sb.Append(" = { ");
                sb.Append(string.Join(", ", frames.Select(k => $"{k.Scale ?? leader.Scale:F2}f")));
                sb.AppendLine(" };");
            }
            sb.AppendLine();

            // ── Shared interpolation ──
            string ls = MultiAnimationRegistry.Suffix(animIndex); // local variable suffix
            sb.AppendLine(RenderHint(kp.TargetScript));
            sb.AppendLine($"{tickVar}++;");

            switch (kp.Loop)
            {
                case LoopMode.Loop:
                    sb.AppendLine($"int t{ls} = {tickVar} % {totalTicks};");
                    break;
                case LoopMode.PingPong:
                    sb.AppendLine($"int raw{ls} = {tickVar} % {totalTicks * 2};");
                    sb.AppendLine($"int t{ls} = raw{ls} < {totalTicks} ? raw{ls} : {totalTicks * 2} - raw{ls};");
                    break;
                default:
                    sb.AppendLine($"int t{ls} = Math.Min({tickVar}, {totalTicks});");
                    break;
            }
            sb.AppendLine();

            // Find keyframe segment
            sb.AppendLine("// Find active keyframe segment");
            sb.AppendLine($"int seg{ls} = 0;");
            sb.AppendLine($"for (int i = 1; i < {frames.Count}; i++)");
            sb.AppendLine($"    if (t{ls} >= {tickArr}[i]) seg{ls} = i;");
            sb.AppendLine($"int next{ls} = (seg{ls} + 1 < {frames.Count}) ? seg{ls} + 1 : seg{ls};");
            sb.AppendLine($"float span{ls} = {tickArr}[next{ls}] - {tickArr}[seg{ls}];");
            sb.AppendLine($"float frac{ls} = span{ls} > 0 ? (t{ls} - {tickArr}[seg{ls}]) / span{ls} : 0f;");
            sb.AppendLine($"float ef{ls} = Ease(frac{ls}, {easeArr}[seg{ls}]);");
            sb.AppendLine();

            // Interpolated delta values (relative to leader's base)
            if (animPos)
            {
                sb.AppendLine($"float dX{ls} = ({axArr}[seg{ls}] + ({axArr}[next{ls}] - {axArr}[seg{ls}]) * ef{ls}) - {baseX:F1}f;");
                sb.AppendLine($"float dY{ls} = ({ayArr}[seg{ls}] + ({ayArr}[next{ls}] - {ayArr}[seg{ls}]) * ef{ls}) - {baseY:F1}f;");
            }
            if (animSize)
            {
                sb.AppendLine($"float dW{ls} = ({awArr}[seg{ls}] + ({awArr}[next{ls}] - {awArr}[seg{ls}]) * ef{ls}) - {baseW:F1}f;");
                sb.AppendLine($"float dH{ls} = ({ahArr}[seg{ls}] + ({ahArr}[next{ls}] - {ahArr}[seg{ls}]) * ef{ls}) - {baseH:F1}f;");
            }
            if (animColor)
            {
                sb.AppendLine($"int ar{ls} = (int)({arArr}[seg{ls}] + ({arArr}[next{ls}] - {arArr}[seg{ls}]) * ef{ls});");
                sb.AppendLine($"int ag{ls} = (int)({agArr}[seg{ls}] + ({agArr}[next{ls}] - {agArr}[seg{ls}]) * ef{ls});");
                sb.AppendLine($"int ab{ls} = (int)({abArr}[seg{ls}] + ({abArr}[next{ls}] - {abArr}[seg{ls}]) * ef{ls});");
                sb.AppendLine($"int aa{ls} = (int)({aaArr}[seg{ls}] + ({aaArr}[next{ls}] - {aaArr}[seg{ls}]) * ef{ls});");
            }
            if (animRot)
                sb.AppendLine($"float arot{ls} = {rotArr}[seg{ls}] + ({rotArr}[next{ls}] - {rotArr}[seg{ls}]) * ef{ls};");
            if (animScale)
                sb.AppendLine($"float ascl{ls} = {sclArr}[seg{ls}] + ({sclArr}[next{ls}] - {sclArr}[seg{ls}]) * ef{ls};");
            sb.AppendLine();

            // ── Per-sprite blocks ──
            foreach (var sp in allSprites)
            {
                bool isText    = sp.Type == SpriteEntryType.Text;
                string alignStr = "TextAlignment." + sp.Alignment.ToString().ToUpperInvariant();

                // Position: sprite's own base + delta from leader's motion
                string posVal = animPos
                    ? $"new Vector2({sp.X:F1}f + dX{ls}, {sp.Y:F1}f + dY{ls})"
                    : $"new Vector2({sp.X:F1}f, {sp.Y:F1}f)";
                string sizeVal = animSize
                    ? $"new Vector2({sp.Width:F1}f + dW{ls}, {sp.Height:F1}f + dH{ls})"
                    : $"new Vector2({sp.Width:F1}f, {sp.Height:F1}f)";
                string colVal = animColor
                    ? $"new Color(ar{ls}, ag{ls}, ab{ls}, aa{ls})"
                    : $"new Color({sp.ColorR}, {sp.ColorG}, {sp.ColorB}, {sp.ColorA})";
                string rotVal = animRot ? $"arot{ls}" : $"{sp.Rotation:F4}f";
                string sclVal = animScale ? $"ascl{ls}" : $"{sp.Scale:F2}f";

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
        /// Regex that captures: (1) the array type (int or float), (2) the variable name, (3) the values inside braces.
        /// Used to extract and replace only the values portion of array declarations.
        /// </summary>
        private static readonly Regex RxArrayDecl = new Regex(
            @"^([ \t]*)(int|float)\[\]\s+(\w+)\s*=\s*\{([^}]+)\}\s*;([^\r\n]*)",
            RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>
        /// Known array type prefixes. Each prefix maps to a "kind" so we can match
        /// existing arrays to new arrays by purpose rather than exact name.
        /// Order matters: more specific prefixes first.
        /// </summary>
        private static readonly (string prefix, string kind)[] ArrayPrefixes =
        {
            ("kfTick", "tick"), ("kfEase", "ease"),
            ("kfRot",  "rot"),  ("kfX",    "posX"),  ("kfY",    "posY"),
            ("kfW",    "width"),("kfH",    "height"),("kfScl",  "scale"),
            ("kfR",    "colR"), ("kfG",    "colG"),  ("kfB",    "colB"), ("kfA", "colA"),
        };

        /// <summary>Returns the kind for a variable name, or null if not a known kf array.</summary>
        private static string GetArrayKind(string varName)
        {
            foreach (var (prefix, kind) in ArrayPrefixes)
                if (varName.StartsWith(prefix, StringComparison.Ordinal))
                    return kind;
            return null;
        }

        /// <summary>Extracts the numeric suffix from a kf array name (e.g. "kfTick2" → "2", "kfTick" → "").</summary>
        private static string GetArraySuffix(string varName)
        {
            foreach (var (prefix, _) in ArrayPrefixes)
                if (varName.StartsWith(prefix, StringComparison.Ordinal))
                    return varName.Substring(prefix.Length);
            return "";
        }

        /// <summary>
        /// Extracts the tick array variable name (e.g. "kfTick" or "kfTick2") from a code snippet.
        /// Returns null if no tick array declaration is found.
        /// </summary>
        public static string ExtractTickArrayName(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            foreach (Match m in RxArrayDecl.Matches(code))
            {
                string varName = m.Groups[3].Value;
                if (varName.StartsWith("kfTick", StringComparison.Ordinal))
                    return varName;
            }
            return null;
        }

        /// <summary>
        /// Smart array-level merge: updates keyframe array VALUES in existing code
        /// using values from a newly-generated snippet. Matches arrays by type prefix
        /// (kfTick→kfTick, kfRot→kfRot, etc.) so variable names are NEVER changed.
        /// Only the values inside { } and the keyframe count literals are updated.
        /// Returns the merged code, or <c>null</c> if no tick array can be matched.
        /// </summary>
        public static string MergeKeyframedIntoCode(string existingCode, string newSnippetCode)
        {
            if (string.IsNullOrEmpty(existingCode) || string.IsNullOrEmpty(newSnippetCode))
                return null;

            // ── Step 1: Determine which animation suffix we're merging (e.g. "" or "2") ──
            string snippetTickName = ExtractTickArrayName(newSnippetCode);
            if (snippetTickName == null) return null;
            string targetSuffix = GetArraySuffix(snippetTickName);

            // Parse array declarations, filtering by the target suffix so we only
            // touch the correct animation's arrays (kfTick2/kfRot2, not kfTick/kfRot).
            var existingArrays = new Dictionary<string, (string varName, string values, string fullMatch)>();
            foreach (Match m in RxArrayDecl.Matches(existingCode))
            {
                string varName = m.Groups[3].Value;
                string kind = GetArrayKind(varName);
                if (kind != null && GetArraySuffix(varName) == targetSuffix && !existingArrays.ContainsKey(kind))
                    existingArrays[kind] = (varName, m.Groups[4].Value.Trim(), m.Value);
            }

            var newArrays = new Dictionary<string, (string varName, string values, string fullMatch)>();
            foreach (Match m in RxArrayDecl.Matches(newSnippetCode))
            {
                string varName = m.Groups[3].Value;
                string kind = GetArrayKind(varName);
                if (kind != null && GetArraySuffix(varName) == targetSuffix && !newArrays.ContainsKey(kind))
                    newArrays[kind] = (varName, m.Groups[4].Value.Trim(), m.Value);
            }

            // Must have tick arrays on both sides
            if (!existingArrays.ContainsKey("tick") || !newArrays.ContainsKey("tick"))
                return null;

            // ── Step 2: For each new array kind, find matching existing array and replace values only ──
            string result = existingCode;
            string lastExistingLine = null;

            foreach (var kvp in newArrays)
            {
                string kind = kvp.Key;
                string newValues = kvp.Value.values;

                if (existingArrays.TryGetValue(kind, out var existing))
                {
                    // Replace ONLY the values portion: { OLD_VALUES } → { NEW_VALUES }
                    // Keep the existing variable name, type, indent, and trailing comment structure
                    string oldFullLine = existing.fullMatch;
                    // Build replacement: same line but swap values inside braces
                    string replaced = RxArrayDecl.Replace(oldFullLine, m =>
                    {
                        string indent = m.Groups[1].Value;
                        string type = m.Groups[2].Value;
                        string name = m.Groups[3].Value;
                        // Keep trailing comment only if values didn't change shape
                        return $"{indent}{type}[] {name} = {{ {newValues} }};";
                    });
                    result = result.Replace(oldFullLine, replaced);
                    lastExistingLine = replaced;
                }
                else if (lastExistingLine != null)
                {
                    // New property array that didn't exist before (e.g., adding color animation).
                    // Insert after the last matched array line, using the NEW snippet's variable name.
                    int insertIdx = result.IndexOf(lastExistingLine, StringComparison.Ordinal);
                    if (insertIdx >= 0)
                    {
                        string indent = GetIndent(lastExistingLine);
                        string newLine = kvp.Value.fullMatch.TrimStart();
                        string toInsert = indent + newLine;
                        int eol = result.IndexOf('\n', insertIdx);
                        if (eol >= 0)
                            result = result.Insert(eol + 1, toInsert + Environment.NewLine);
                        else
                            result += Environment.NewLine + toInsert;
                        lastExistingLine = toInsert;
                    }
                }
            }

            // ── Step 3: Remove existing arrays whose kind is no longer in the new snippet ──
            // (e.g., user removed color animation — remove kfR, kfG, kfB, kfA arrays)
            foreach (var kvp in existingArrays)
            {
                string kind = kvp.Key;
                if (kind == "tick" || kind == "ease") continue; // never remove tick/ease
                if (!newArrays.ContainsKey(kind))
                {
                    result = RemoveCodeLine(result, kvp.Value.fullMatch);
                }
            }

            // ── Step 4: Update keyframe count references if array length changed ──
            int[] existingTicks = ParseIntArray(existingCode, existingArrays["tick"].varName);
            int[] newTicks = ParseIntArray(newSnippetCode, newArrays["tick"].varName);
            if (existingTicks != null && newTicks != null && existingTicks.Length != newTicks.Length)
            {
                int oldCount = existingTicks.Length;
                int newCount = newTicks.Length;

                // for (int i = 1; i < N; i++)
                result = Regex.Replace(result,
                    @"(for\s*\(\s*int\s+\w+\s*=\s*1\s*;\s*\w+\s*<\s*)" + oldCount + @"(\s*;)",
                    "${1}" + newCount + "${2}");

                // (seg + 1 < N)
                result = Regex.Replace(result,
                    @"(\(\s*\w+\s*\+\s*1\s*<\s*)" + oldCount + @"(\s*\))",
                    "${1}" + newCount + "${2}");
            }

            // ── Step 5: Update tick modulo if total ticks changed ──
            if (existingTicks != null && newTicks != null)
            {
                int oldTotal = existingTicks.Last();
                int newTotal = newTicks.Last();
                if (oldTotal != newTotal && oldTotal > 0 && newTotal > 0)
                {
                    // _tick % OLD → _tick % NEW  (loop mode)
                    result = Regex.Replace(result,
                        @"(%\s*)" + oldTotal + @"(\s*;)",
                        "${1}" + newTotal + "${2}");

                    // Math.Min(_tick, OLD) (once mode)
                    result = Regex.Replace(result,
                        @"(Math\.Min\s*\(\s*\w+\s*,\s*)" + oldTotal + @"(\s*\))",
                        "${1}" + newTotal + "${2}");

                    // raw % (OLD*2) and (OLD*2) - raw (ping-pong)
                    int oldDouble = oldTotal * 2;
                    int newDouble = newTotal * 2;
                    if (result.Contains(oldDouble.ToString()))
                    {
                        result = result.Replace(oldDouble.ToString(), newDouble.ToString());
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Merges a NEW animation snippet (with indexed variable names) into an
        /// existing COMPLETE PB program. Inserts field declarations before the Ease
        /// function and interpolation + sprite code before frame.Dispose().
        /// Returns the merged code, or null if the structure can't be parsed.
        /// </summary>
        public static string MergeSnippetIntoCompleteProgram(string existingCode, string snippetCode, string spriteName = null)
        {
            if (string.IsNullOrEmpty(existingCode) || string.IsNullOrEmpty(snippetCode))
                return null;

            // ── Remove the original static sprite block for this sprite ──
            // SourceStart/End offsets become stale after earlier merges modify the code,
            // so we find the static block by matching Data = "SpriteName" in a frame.Add block
            // that does NOT contain interpolation variables (i.e. not already animated).
            if (!string.IsNullOrEmpty(spriteName))
            {
                existingCode = RemoveStaticSpriteBlock(existingCode, spriteName);
            }

            // ── Extract field-level declarations from snippet ──
            // These are: tick variable (int _tick2 = 0;) and kf arrays (int[] kfTick2 = {...};)
            // They appear between "// ── Keyframe data ──" and the render hint or interpolation start.
            var fieldLines = new List<string>();
            var renderLines = new List<string>();
            bool inFields = false;
            bool inRender = false;
            string[] snippetLines = snippetCode.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            for (int i = 0; i < snippetLines.Length; i++)
            {
                string line = snippetLines[i];
                string trimmed = line.TrimStart();

                // Skip the Ease function block in the snippet (already in existing code)
                if (trimmed.StartsWith("// ── Easing helper", StringComparison.Ordinal))
                {
                    // Skip until closing brace of the function
                    int braceDepth = 0;
                    bool enteredFunction = false;
                    for (; i < snippetLines.Length; i++)
                    {
                        string fl = snippetLines[i];
                        foreach (char ch in fl)
                        {
                            if (ch == '{') { braceDepth++; enteredFunction = true; }
                            else if (ch == '}') braceDepth--;
                        }
                        if (enteredFunction && braceDepth <= 0) break;
                    }
                    continue;
                }

                // Start collecting field declarations
                if (trimmed.StartsWith("// ── Keyframe data", StringComparison.Ordinal))
                {
                    inFields = true;
                    fieldLines.Add("");
                    fieldLines.Add(line);
                    continue;
                }

                // Transition from fields to render (interpolation) section
                if (inFields && (trimmed.StartsWith("// In your Main()", StringComparison.Ordinal)
                    || trimmed.StartsWith("// In your render method", StringComparison.Ordinal)
                    || trimmed.StartsWith("// Field —", StringComparison.Ordinal)))
                {
                    // Skip hint comments
                    continue;
                }

                if (inFields && !inRender)
                {
                    // Once we hit a non-array, non-empty, non-comment line that looks like
                    // interpolation code (tick increment), switch to render section
                    if (trimmed.Length > 0 && !trimmed.StartsWith("//")
                        && !trimmed.StartsWith("int[]") && !trimmed.StartsWith("float[]")
                        && !trimmed.StartsWith("int _tick") && !trimmed.StartsWith("int _anim"))
                    {
                        inFields = false;
                        inRender = true;
                        renderLines.Add("");
                        renderLines.Add("    // ── Animation (merged) ──");
                    }
                    else if (trimmed.Length > 0 && !trimmed.StartsWith("//"))
                    {
                        fieldLines.Add(line);
                        continue;
                    }
                    else if (trimmed.Length == 0)
                    {
                        // blank line in fields section - keep it
                        fieldLines.Add(line);
                        continue;
                    }
                    else
                    {
                        // comment line in fields section
                        fieldLines.Add(line);
                        continue;
                    }
                }

                if (inRender)
                {
                    // Collect all render lines (interpolation + sprite block)
                    // Indent them for inside Main()
                    if (trimmed.Length > 0)
                        renderLines.Add("    " + trimmed);
                    else
                        renderLines.Add("");
                }
            }

            if (fieldLines.Count == 0 && renderLines.Count == 0)
                return null;

            string result = existingCode;

            // ── Insert field declarations before the Ease function ──
            if (fieldLines.Count > 0)
            {
                // Look for "public float Ease(" or "float Ease(" as insertion point
                int easeIdx = result.IndexOf("public float Ease(", StringComparison.Ordinal);
                if (easeIdx < 0)
                    easeIdx = result.IndexOf("float Ease(", StringComparison.Ordinal);
                if (easeIdx < 0)
                {
                    // Fallback: look for "// ── Easing helper"
                    easeIdx = result.IndexOf("// ── Easing helper", StringComparison.Ordinal);
                }

                if (easeIdx >= 0)
                {
                    // Walk back to line start
                    int lineStart = easeIdx;
                    while (lineStart > 0 && result[lineStart - 1] != '\n') lineStart--;

                    // Also skip preceding blank lines and the "// ── Easing helper" comment
                    // to insert right before them
                    string fieldBlock = string.Join(Environment.NewLine, fieldLines) + Environment.NewLine;
                    result = result.Insert(lineStart, fieldBlock);
                }
            }

            // ── Insert interpolation + sprite block before frame.Dispose() ──
            if (renderLines.Count > 0)
            {
                int disposeIdx = result.IndexOf("frame.Dispose()", StringComparison.Ordinal);
                if (disposeIdx >= 0)
                {
                    // Walk back to line start
                    int lineStart = disposeIdx;
                    while (lineStart > 0 && result[lineStart - 1] != '\n') lineStart--;

                    string renderBlock = string.Join(Environment.NewLine, renderLines) + Environment.NewLine + Environment.NewLine;
                    result = result.Insert(lineStart, renderBlock);
                }
            }

            return result;
        }

        /// <summary>
        /// Removes a static (non-animated) frame.Add(new MySprite { Data = "name" ... }) block
        /// from the code. Only removes blocks that don't contain interpolation markers like "← animated".
        /// </summary>
        private static string RemoveStaticSpriteBlock(string code, string spriteName)
        {
            string dataPattern = "\"" + spriteName + "\"";
            int searchFrom = 0;
            while (searchFrom < code.Length)
            {
                int dataIdx = code.IndexOf(dataPattern, searchFrom, StringComparison.Ordinal);
                if (dataIdx < 0) break;

                // Check this is a Data = "..." assignment
                int lineStart = code.LastIndexOf('\n', dataIdx) + 1;
                string lineText = code.Substring(lineStart, dataIdx - lineStart).TrimStart();
                if (!lineText.StartsWith("Data", StringComparison.Ordinal))
                {
                    searchFrom = dataIdx + dataPattern.Length;
                    continue;
                }

                // Walk backwards to find the frame.Add(new MySprite start
                int blockStart = -1;
                int scan = lineStart - 1;
                while (scan >= 0)
                {
                    int ls = code.LastIndexOf('\n', scan) + 1;
                    string trimLine = code.Substring(ls, scan + 1 - ls).TrimStart();
                    if (trimLine.Contains("frame.Add(") || trimLine.Contains(".Add(new MySprite"))
                    {
                        blockStart = ls;
                        break;
                    }
                    if (trimLine.Contains("new MySprite"))
                    {
                        blockStart = ls;
                        break;
                    }
                    if (trimLine.Length > 0 && !trimLine.StartsWith("{") && !trimLine.StartsWith("//")
                        && !trimLine.StartsWith("Type") && !trimLine.StartsWith("Data")
                        && !trimLine.StartsWith("Position") && !trimLine.StartsWith("Size")
                        && !trimLine.StartsWith("Color") && !trimLine.StartsWith("Alignment")
                        && !trimLine.StartsWith("RotationOrScale") && !trimLine.StartsWith("FontId"))
                    {
                        break;
                    }
                    scan = ls - 2;
                    if (scan < 0) break;
                }

                if (blockStart < 0)
                {
                    searchFrom = dataIdx + dataPattern.Length;
                    continue;
                }

                // Find the end of the block: closing });
                int blockEnd = code.IndexOf("});", dataIdx, StringComparison.Ordinal);
                if (blockEnd < 0)
                {
                    searchFrom = dataIdx + dataPattern.Length;
                    continue;
                }
                blockEnd += 3; // past ");"

                if (blockEnd < code.Length && code[blockEnd] == '\r') blockEnd++;
                if (blockEnd < code.Length && code[blockEnd] == '\n') blockEnd++;

                string block = code.Substring(blockStart, blockEnd - blockStart);

                // Only remove if this is a STATIC block (not animated)
                if (block.Contains("\u2190 animated"))
                {
                    searchFrom = blockEnd;
                    continue;
                }

                // Remove the block (and any preceding blank line)
                int removeStart = blockStart;
                if (removeStart > 0 && code[removeStart - 1] == '\n')
                {
                    removeStart--;
                    if (removeStart > 0 && code[removeStart - 1] == '\r') removeStart--;
                }

                code = code.Remove(removeStart, blockEnd - removeStart);
                break; // only remove the first static instance
            }

            return code;
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
            rotVal  = animRot   ? "arot"                   : $"{sp.Rotation:F4}f";
            sclVal  = animScale ? "ascl"                   : $"{sp.Scale:F2}f";

            sb.AppendLine($"{indent}{listVar}.Add(new MySprite");
            sb.AppendLine($"{indent}{{");
            if (isText)
            {
                sb.AppendLine($"{indent}    Type           = SpriteType.TEXT,");
                sb.AppendLine($"{indent}    Data           = \"{Esc(sp.Text)}\",");
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
