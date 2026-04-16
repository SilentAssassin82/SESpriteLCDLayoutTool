using System;
using System.Text;
using System.Text.RegularExpressions;
using SESpriteLCDLayoutTool.Models;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Extends <see cref="AnimationSnippetGenerator"/> with round-trip support for
    /// simple (non-keyframe) animations: complete program generation, block detection,
    /// in-place merge, and param parsing from existing code.
    /// </summary>
    public static partial class AnimationSnippetGenerator
    {
        // ── Marker used to bound the complete-program block ──────────────────────
        public const string SimpleFooterMarker = "// ─── End Simple Animation ───";

        // ── Regex to detect the header line emitted by every Generate*() method ─
        private static readonly Regex RxSimpleHeader = new Regex(
            @"//\s*─+\s*Animation:\s*(\w+)\s+""([^""]*)""\s*\[([^\]]+)\]",
            RegexOptions.Compiled);

        // ── Variables we know each generator emits — used for in-place merge ──
        private static readonly string[] _simpleAnimVarPatterns =
        {
            @"float\s+oscOffset\s*=",
            @"float\s+pulseScale\s*=",
            @"byte\s+fadeAlpha\s*=",
            @"bool\s+blinkVisible\s*=",
            @"float\s+hue\s*=",
        };

        // ── Complete program generation ───────────────────────────────────────────

        /// <summary>
        /// Wraps the snippet returned by <see cref="Generate"/> in a full compilable
        /// PB program (for PB targets) or LCD-Helper render method (for all others).
        /// Appends <see cref="SimpleFooterMarker"/> so the block can be detected and
        /// replaced on subsequent applies.
        /// </summary>
        public static string GenerateSimpleComplete(SpriteEntry sprite, AnimationType type, AnimationParams p)
        {
            if (type == AnimationType.Keyframed)
                return "// Use GenerateKeyframedComplete() for keyframe animations";

            bool isPb = p.TargetScript == TargetScriptType.ProgrammableBlock;
            string snippet = Generate(sprite, type, p);

            var sb = new StringBuilder();

            if (isPb)
            {
                // Full PB script
                sb.AppendLine("using System;");
                sb.AppendLine();
                sb.AppendLine("// ─── Fields ───");
                sb.AppendLine("int _tick = 0;");
                sb.AppendLine("IMyTextSurface _surface;");
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

                // Indent the snippet body into Main
                string body = StripFieldAndHints(snippet, p.ListVarName);
                foreach (string line in body.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                    sb.AppendLine("    " + line);

                sb.AppendLine("    frame.Dispose();");
                sb.AppendLine("}");
            }
            else
            {
                // LCD Helper render method
                sb.AppendLine("// ─── Fields ───");
                sb.AppendLine("int _tick = 0;");
                sb.AppendLine();
                sb.AppendLine("public void RenderAnimated(List<MySprite> sprites)");
                sb.AppendLine("{");

                string body = StripFieldAndHints(snippet, p.ListVarName);
                foreach (string line in body.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                    sb.AppendLine("    " + line);

                sb.AppendLine("}");
            }

            // Append the snippet header (first line) before the footer so the header
            // regex can still find the block type and sprite name.
            int headerEnd = snippet.IndexOf('\n');
            string headerLine = headerEnd > 0 ? snippet.Substring(0, headerEnd).TrimEnd() : snippet.TrimEnd();
            // Header is already the first line of the snippet, which was inlined above —
            // but for non-PB the header comment is inside the method body.  Re-emit it
            // above the method for detection purposes by prepending to the output.
            string output = headerLine + Environment.NewLine + sb.ToString().TrimEnd();
            output += Environment.NewLine + SimpleFooterMarker;
            return output;
        }

        // ── Block range detection ─────────────────────────────────────────────────

        /// <summary>
        /// Finds the character range of an existing simple animation block in source code.
        /// Recognises blocks with a <c>// ─── Animation: Type "name" [target] ───</c> header.
        /// If the block ends with <see cref="SimpleFooterMarker"/> that bounds the range;
        /// otherwise the range extends to the last <c>});</c> of the sprite add-call.
        /// </summary>
        public static bool FindSimpleAnimBlockRange(string code, out int blockStart, out int blockLength)
        {
            blockStart = 0;
            blockLength = 0;
            if (string.IsNullOrEmpty(code)) return false;

            var m = RxSimpleHeader.Match(code);
            if (!m.Success) return false;

            // Walk back to start of the header line
            blockStart = m.Index;
            while (blockStart > 0 && code[blockStart - 1] != '\n')
                blockStart--;

            // Footer-bounded (complete program block)
            int footerIdx = code.IndexOf(SimpleFooterMarker, blockStart, StringComparison.Ordinal);
            if (footerIdx >= 0)
            {
                int endIdx = footerIdx + SimpleFooterMarker.Length;
                if (endIdx < code.Length && code[endIdx] == '\r') endIdx++;
                if (endIdx < code.Length && code[endIdx] == '\n') endIdx++;
                blockLength = endIdx - blockStart;
                return blockLength > 0;
            }

            // No footer — find the last });\n after the header
            var addRx = new Regex(@"\w+\.Add\(\s*new\s+MySprite", RegexOptions.Compiled);
            Match addMatch = null;
            Match candidate = addRx.Match(code, m.Index);
            while (candidate.Success)
            {
                addMatch = candidate;
                candidate = addRx.Match(code, candidate.Index + candidate.Length);
            }
            if (addMatch == null) return false;

            int closeIdx = code.IndexOf("});", addMatch.Index + addMatch.Length, StringComparison.Ordinal);
            if (closeIdx < 0) return false;

            closeIdx += 3;
            if (closeIdx < code.Length && code[closeIdx] == '\r') closeIdx++;
            if (closeIdx < code.Length && code[closeIdx] == '\n') closeIdx++;

            blockLength = closeIdx - blockStart;
            return blockLength > 0;
        }

        // ── In-place merge ────────────────────────────────────────────────────────

        /// <summary>
        /// Merges updated simple-animation snippet values into existing code.
        /// Replaces the animation-specific variable line (e.g. <c>float oscOffset = …</c>)
        /// and the sprite <c>Add(new MySprite { … });</c> block in-place, leaving
        /// all surrounding code untouched.
        /// Returns the merged code, or <c>null</c> if no merge point was found.
        /// </summary>
        public static string MergeSimpleAnimIntoCode(string existingCode, string newSnippet)
        {
            if (string.IsNullOrEmpty(existingCode) || string.IsNullOrEmpty(newSnippet))
                return null;

            // Only merge if the existing code has the same animation type header
            var exHeader = RxSimpleHeader.Match(existingCode);
            var newHeader = RxSimpleHeader.Match(newSnippet);
            if (!exHeader.Success || !newHeader.Success) return null;
            if (!string.Equals(exHeader.Groups[1].Value, newHeader.Groups[1].Value,
                               StringComparison.OrdinalIgnoreCase))
                return null; // different animation type — can't merge, caller should replace

            string result = existingCode;

            // Replace animation-variable line(s) — each generator emits exactly one such line
            foreach (string pattern in _simpleAnimVarPatterns)
            {
                var rxOld = new Regex(pattern + @"[^\r\n]+", RegexOptions.Compiled);
                var rxNew = new Regex(pattern + @"[^\r\n]+", RegexOptions.Compiled);

                var oldMatch = rxOld.Match(existingCode);
                var newMatch = rxNew.Match(newSnippet);
                if (oldMatch.Success && newMatch.Success)
                {
                    result = result.Replace(oldMatch.Value, newMatch.Value);
                    break; // each animation type has exactly one such variable
                }
            }

            // Replace the sprite Add(new MySprite { ... }); block
            result = ReplaceSpriteAddBlock(result, newSnippet);

            return result == existingCode ? null : result; // null = nothing changed
        }

        // ── Param parsing from existing code ─────────────────────────────────────

        /// <summary>
        /// Parses an <see cref="AnimationParams"/> instance from code previously generated
        /// by <see cref="Generate"/>. Returns null if no recognisable simple-animation
        /// header is found. Used to pre-populate the dialog when "Edit Existing" is chosen.
        /// </summary>
        public static AnimationParams TryParseSimpleAnim(string code, out AnimationType animType)
        {
            animType = AnimationType.Rotate;
            if (string.IsNullOrEmpty(code)) return null;

            var m = RxSimpleHeader.Match(code);
            if (!m.Success) return null;

            string typeStr   = m.Groups[1].Value.Trim();
            string targetStr = m.Groups[3].Value.Trim();

            if (!Enum.TryParse(typeStr, true, out animType)) return null;

            var p = new AnimationParams();

            // ── Target ──
            if (targetStr.IndexOf("PB", StringComparison.OrdinalIgnoreCase) >= 0)
                p.TargetScript = TargetScriptType.ProgrammableBlock;
            else if (targetStr.IndexOf("Torch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     targetStr.IndexOf("Plugin", StringComparison.OrdinalIgnoreCase) >= 0)
                p.TargetScript = TargetScriptType.Plugin;
            else if (targetStr.IndexOf("Pulsar", StringComparison.OrdinalIgnoreCase) >= 0)
                p.TargetScript = TargetScriptType.Pulsar;
            else if (targetStr.IndexOf("Mod", StringComparison.OrdinalIgnoreCase) >= 0)
                p.TargetScript = TargetScriptType.Mod;
            else
                p.TargetScript = TargetScriptType.LcdHelper;

            // ── List variable ──
            var listRx = new Regex(@"(\w+)\.Add\(\s*new\s+MySprite");
            var listMatch = listRx.Match(code);
            if (listMatch.Success) p.ListVarName = listMatch.Groups[1].Value;

            // ── Type-specific params from header comment and variable lines ──
            switch (animType)
            {
                case AnimationType.Rotate:
                    p.RotateSpeed = ParseFloat(code, @"Speed:\s*([\d.]+)f\s+rad") ?? p.RotateSpeed;
                    p.Clockwise   = !code.Contains("Counter-clockwise");
                    break;

                case AnimationType.Oscillate:
                    p.OscillateAmplitude = ParseFloat(code, @"Amplitude:\s*([\d.]+)f") ?? p.OscillateAmplitude;
                    p.OscillateSpeed     = ParseFloat(code, @"oscOffset\s*=.*\*\s*([\d.]+)f\s*\)") ?? p.OscillateSpeed;
                    if (code.Contains("Axis: Both"))       p.Axis = OscillateAxis.Both;
                    else if (code.Contains("Axis: Y"))     p.Axis = OscillateAxis.Y;
                    else                                   p.Axis = OscillateAxis.X;
                    break;

                case AnimationType.Pulse:
                    p.PulseAmplitude = ParseFloat(code, @"Amplitude:\s*±([\d.]+)f") ?? p.PulseAmplitude;
                    p.PulseSpeed     = ParseFloat(code, @"pulseScale\s*=.*\*\s*([\d.]+)f\s*\)") ?? p.PulseSpeed;
                    break;

                case AnimationType.Fade:
                    p.FadeMinAlpha = ParseInt(code, @"Alpha:\s*(\d+)–")    ?? p.FadeMinAlpha;
                    p.FadeMaxAlpha = ParseInt(code, @"–(\d+)\s*\|")        ?? p.FadeMaxAlpha;
                    p.FadeSpeed    = ParseFloat(code, @"fadeAlpha.*\*\s*([\d.]+)f\s*\)") ?? p.FadeSpeed;
                    break;

                case AnimationType.Blink:
                    p.BlinkOnTicks  = ParseInt(code, @"On:\s*(\d+)\s+ticks")  ?? p.BlinkOnTicks;
                    p.BlinkOffTicks = ParseInt(code, @"Off:\s*(\d+)\s+ticks") ?? p.BlinkOffTicks;
                    break;

                case AnimationType.ColorCycle:
                    p.CycleSpeed      = ParseFloat(code, @"Speed:\s*([\d.]+)f\s+°") ?? p.CycleSpeed;
                    p.CycleBrightness = ParseFloat(code, @"Brightness:\s*([\d.]+)f") ?? p.CycleBrightness;
                    break;
            }

            return p;
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Strips the "field hint / add to class" comment, the <c>int _tick = 0;</c> line,
        /// and the "render hint / in your method" comment from a raw snippet so the body
        /// can be inlined into a generated method without duplication.
        /// </summary>
        private static string StripFieldAndHints(string snippet, string listVar)
        {
            var lines = snippet.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var sb = new StringBuilder();
            bool inBody = false;

            foreach (string line in lines)
            {
                string trimmed = line.Trim();

                // Skip the header comment line (already emitted separately)
                if (trimmed.StartsWith("// ─── Animation:")) { inBody = false; continue; }
                // Skip field hint and _tick field declaration (will be in class body)
                if (trimmed.StartsWith("// Field —")) continue;
                if (trimmed == "int _tick = 0;") continue;
                // Skip the render hint comment
                if (trimmed.StartsWith("// In your") || trimmed.StartsWith("// ──")) { inBody = true; continue; }

                inBody = true;
                sb.AppendLine(line);
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Replaces the sprite <c>Add(new MySprite { … });</c> block in <paramref name="target"/>
        /// with the one from <paramref name="source"/>, preserving the surrounding code.
        /// </summary>
        private static string ReplaceSpriteAddBlock(string target, string source)
        {
            const string addToken = ".Add(new MySprite";
            const string closeToken = "});";

            int srcAddIdx = source.IndexOf(addToken, StringComparison.Ordinal);
            if (srcAddIdx < 0) return target;
            // Walk back to start of the list-var identifier
            int srcLineStart = srcAddIdx;
            while (srcLineStart > 0 && source[srcLineStart - 1] != '\n') srcLineStart--;
            int srcClose = source.IndexOf(closeToken, srcAddIdx, StringComparison.Ordinal);
            if (srcClose < 0) return target;
            string newBlock = source.Substring(srcLineStart, srcClose + closeToken.Length - srcLineStart);

            int tgtAddIdx = target.IndexOf(addToken, StringComparison.Ordinal);
            if (tgtAddIdx < 0) return target;
            int tgtLineStart = tgtAddIdx;
            while (tgtLineStart > 0 && target[tgtLineStart - 1] != '\n') tgtLineStart--;
            int tgtClose = target.IndexOf(closeToken, tgtAddIdx, StringComparison.Ordinal);
            if (tgtClose < 0) return target;

            return target.Substring(0, tgtLineStart)
                 + newBlock
                 + target.Substring(tgtClose + closeToken.Length);
        }

        private static float? ParseFloat(string code, string pattern)
        {
            var m = Regex.Match(code, pattern);
            if (!m.Success) return null;
            string val = m.Groups[1].Value.TrimEnd('f', 'F');
            return float.TryParse(val, System.Globalization.NumberStyles.Any,
                                  System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : (float?)null;
        }

        private static int? ParseInt(string code, string pattern)
        {
            var m = Regex.Match(code, pattern);
            if (!m.Success) return null;
            return int.TryParse(m.Groups[1].Value, out int v) ? v : (int?)null;
        }
    }
}
