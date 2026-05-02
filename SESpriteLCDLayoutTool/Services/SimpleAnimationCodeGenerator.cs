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
            // Step 7: the new injector is the SOLE merge engine. Validity gating
            // (null inputs, header-type mismatch) lives in the plan builder, which
            // returns null when no merge is possible. An empty plan means
            // "merge attempted but nothing changed", which legacy reported as null.
            if (string.IsNullOrEmpty(existingCode) || string.IsNullOrEmpty(newSnippet))
                return null;

            CodeInjection.InjectionPlan plan;
            try
            {
                plan = BuildShadowPlanForSimpleAnimMerge(existingCode, newSnippet);
            }
            catch
            {
                return null;
            }

            if (plan == null || plan.PropertyPatches.Count == 0)
                return null;

            try
            {
                var injected = new CodeInjection.RoslynCodeInjector().Apply(existingCode, plan);
                if (injected != null && injected.Success &&
                    !string.IsNullOrEmpty(injected.RewrittenSource))
                {
                    return injected.RewrittenSource;
                }
            }
            catch
            {
                // Injector must never break the host.
            }

            return null;
        }

        /// <summary>
        /// Span-precise structured description of what the legacy simple-animation
        /// merge does: rewrite one anim-variable line in place, then rewrite the
        /// sprite <c>.Add(new MySprite { ... });</c> block in place. Both are emitted
        /// as <see cref="CodeInjection.PropertyPatchOp"/> so the new injector
        /// produces byte-identical output to legacy. Used by shadow-compare logging
        /// and by the gated new-injector path. Never throws.
        /// </summary>
        private static CodeInjection.InjectionPlan BuildShadowPlanForSimpleAnimMerge(
            string existingCode, string newSnippet)
        {
            if (string.IsNullOrEmpty(existingCode) || string.IsNullOrEmpty(newSnippet))
                return null;

            // Validity gate: only merge if both sides carry the same anim header type.
            var exHeader = RxSimpleHeader.Match(existingCode);
            var newHeader = RxSimpleHeader.Match(newSnippet);
            if (!exHeader.Success || !newHeader.Success) return null;
            if (!string.Equals(exHeader.Groups[1].Value, newHeader.Groups[1].Value,
                               StringComparison.OrdinalIgnoreCase))
                return null;

            var plan = new CodeInjection.InjectionPlan
            {
                Description = "Simple animation in-place merge (span-precise)",
            };

            // ── 1) Animation-variable line rewrite ────────────────────────────
            // Legacy matches the SAME pattern in both existing & new and
            // string-replaces the match. Only one anim-var pattern matches any
            // given snippet (e.g. only "float oscOffset =" for Oscillate).
            foreach (string pattern in _simpleAnimVarPatterns)
            {
                var rxLine = new Regex(pattern + @"[^\r\n]+", RegexOptions.Compiled);
                var oldMatch = rxLine.Match(existingCode);
                var newMatch = rxLine.Match(newSnippet);
                if (!oldMatch.Success || !newMatch.Success) continue;

                if (!string.Equals(oldMatch.Value, newMatch.Value, StringComparison.Ordinal))
                {
                    string anchor = "simple-anim:varline";
                    int eq = newMatch.Value.IndexOf('=');
                    if (eq > 0)
                    {
                        string lhs = newMatch.Value.Substring(0, eq).Trim();
                        int sp = lhs.LastIndexOf(' ');
                        anchor = "simple-anim:" + (sp >= 0 ? lhs.Substring(sp + 1) : lhs);
                    }

                    plan.PropertyPatches.Add(new CodeInjection.PropertyPatchOp
                    {
                        Anchor = anchor,
                        Start = oldMatch.Index,
                        End = oldMatch.Index + oldMatch.Length,
                        ExpectedOldText = oldMatch.Value,
                        NewText = newMatch.Value,
                        Action = CodeInjection.InjectionAction.Update,
                    });
                }
                break;
            }

            // ── 2) Sprite Add block rewrite ───────────────────────────────────
            // Mirrors ReplaceSpriteAddBlock(): walk back from ".Add(new MySprite"
            // to the start of the line, forward to "});", then swap the spans.
            const string addToken = ".Add(new MySprite";
            const string closeToken = "});";

            int srcAddIdx = newSnippet.IndexOf(addToken, StringComparison.Ordinal);
            int tgtAddIdx = existingCode.IndexOf(addToken, StringComparison.Ordinal);
            if (srcAddIdx >= 0 && tgtAddIdx >= 0)
            {
                int srcLineStart = srcAddIdx;
                while (srcLineStart > 0 && newSnippet[srcLineStart - 1] != '\n') srcLineStart--;
                int srcClose = newSnippet.IndexOf(closeToken, srcAddIdx, StringComparison.Ordinal);

                int tgtLineStart = tgtAddIdx;
                while (tgtLineStart > 0 && existingCode[tgtLineStart - 1] != '\n') tgtLineStart--;
                int tgtClose = existingCode.IndexOf(closeToken, tgtAddIdx, StringComparison.Ordinal);

                if (srcClose >= 0 && tgtClose >= 0)
                {
                    int tgtSpanEnd = tgtClose + closeToken.Length;
                    int srcSpanEnd = srcClose + closeToken.Length;
                    string oldBlock = existingCode.Substring(tgtLineStart, tgtSpanEnd - tgtLineStart);
                    string newBlock = newSnippet.Substring(srcLineStart, srcSpanEnd - srcLineStart);

                    if (!string.Equals(oldBlock, newBlock, StringComparison.Ordinal))
                    {
                        plan.PropertyPatches.Add(new CodeInjection.PropertyPatchOp
                        {
                            Anchor = "simple-anim:sprite-add-block",
                            Start = tgtLineStart,
                            End = tgtSpanEnd,
                            ExpectedOldText = oldBlock,
                            NewText = newBlock,
                            Action = CodeInjection.InjectionAction.Update,
                        });
                    }
                }
            }

            return plan;
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

        private static float? ParseFloat(string code, string pattern)
        {
            var m = Regex.Match(code, pattern);
            if (!m.Success) return null;
            string val = m.Groups[1].Value.TrimEnd('f', 'F');
            return float.TryParse(val, System.Globalization.NumberStyles.Any,
                                  System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : (float?)null;
        }

        /// <summary>
        /// Merges a simple animation snippet into an existing program that has no
        /// simple-animation header. Finds the sprite's <c>.Add(new MySprite</c> block
        /// and replaces it with the animated version, inserting animation variable
        /// computation lines immediately before it. Returns null if no merge point found.
        /// </summary>
        public static string MergeSimpleAnimIntoProgram(string existingCode, string snippet)
        {
            if (string.IsNullOrEmpty(existingCode) || string.IsNullOrEmpty(snippet))
                return null;

            // Extract the animated Add block from the snippet
            const string addToken = ".Add(new MySprite";
            int snipAddIdx = snippet.IndexOf(addToken, StringComparison.Ordinal);
            if (snipAddIdx < 0) return null;

            // Check for blink's "if (blinkVisible)" wrapper
            bool hasBlink = snippet.Contains("if (blinkVisible)");

            // Walk back to get animation variable lines (everything between _tick++ and the Add block)
            string[] snipLines = snippet.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var varLines = new System.Collections.Generic.List<string>();
            bool pastRenderHint = false;
            bool pastTick = false;
            foreach (string line in snipLines)
            {
                string trimmed = line.Trim();
                // Skip header, field hint, _tick declaration, render hint
                if (trimmed.StartsWith("// ─── Animation:")) continue;
                if (trimmed.StartsWith("// Field —") || trimmed.StartsWith("// Add to")) continue;
                if (trimmed == "int _tick = 0;") continue;
                if (trimmed.StartsWith("// In your") || trimmed.StartsWith("// ──"))
                { pastRenderHint = true; continue; }
                if (!pastRenderHint) continue;
                if (trimmed == "_tick++;") { pastTick = true; continue; }
                if (!pastTick) continue;
                // Stop when we hit the Add block
                if (trimmed.Contains(addToken.Trim())) break;
                // Stop at blink wrapper
                if (hasBlink && trimmed == "if (blinkVisible)") break;
                varLines.Add(line);
            }

            // Find the target sprite's Add block in existing code
            int tgtAddIdx = existingCode.IndexOf(addToken, StringComparison.Ordinal);
            if (tgtAddIdx < 0) return null;

            // Walk back to line start
            int tgtLineStart = tgtAddIdx;
            while (tgtLineStart > 0 && existingCode[tgtLineStart - 1] != '\n')
                tgtLineStart--;

            // Detect indentation of the existing Add block
            string existingLine = existingCode.Substring(tgtLineStart, tgtAddIdx - tgtLineStart + addToken.Length);
            string indent = "";
            foreach (char c in existingLine)
            {
                if (c == ' ' || c == '\t') indent += c;
                else break;
            }

            // Find the close of the existing Add block: "});"
            const string closeToken = "});";
            int tgtClose = existingCode.IndexOf(closeToken, tgtAddIdx, StringComparison.Ordinal);
            if (tgtClose < 0) return null;
            int tgtEnd = tgtClose + closeToken.Length;

            // Build the replacement: animation var lines + animated Add block (from snippet)
            // Get the full animated Add block from snippet (including blink wrapper if present)
            int snipBlockStart;
            if (hasBlink)
            {
                int blinkIdx = snippet.IndexOf("if (blinkVisible)", StringComparison.Ordinal);
                snipBlockStart = blinkIdx;
                while (snipBlockStart > 0 && snippet[snipBlockStart - 1] != '\n')
                    snipBlockStart--;
            }
            else
            {
                snipBlockStart = snipAddIdx;
                while (snipBlockStart > 0 && snippet[snipBlockStart - 1] != '\n')
                    snipBlockStart--;
            }
            int snipClose = snippet.LastIndexOf(closeToken, StringComparison.Ordinal);
            if (snipClose < 0) return null;
            int snipEnd = snipClose + closeToken.Length;
            // For blink, include the closing "}"
            if (hasBlink)
            {
                int braceClose = snippet.IndexOf("}", snipEnd, StringComparison.Ordinal);
                if (braceClose >= 0) snipEnd = braceClose + 1;
            }
            string snipBlock = snippet.Substring(snipBlockStart, snipEnd - snipBlockStart);

            // Re-indent snippet block to match existing indentation
            string[] blockLines = snipBlock.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var sb = new StringBuilder();

            // Insert animation variable lines first
            foreach (string vl in varLines)
            {
                string trimmed = vl.Trim();
                if (trimmed.Length == 0) continue; // skip blank lines in var section
                sb.AppendLine(indent + trimmed);
            }

            // Insert the animated Add block
            foreach (string bl in blockLines)
            {
                string trimmed = bl.Trim();
                if (trimmed.Length == 0) continue;
                sb.AppendLine(indent + trimmed);
            }

            string replacement = sb.ToString().TrimEnd('\r', '\n');

            return existingCode.Substring(0, tgtLineStart)
                 + replacement + Environment.NewLine
                 + existingCode.Substring(tgtEnd);
        }

        private static int? ParseInt(string code, string pattern)
        {
            var m = Regex.Match(code, pattern);
            if (!m.Success) return null;
            return int.TryParse(m.Groups[1].Value, out int v) ? v : (int?)null;
        }
    }
}
