using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Universal animation merge engine. Works with ANY animation type by parsing
    /// generated snippets into structural sections (fields, helpers, render logic,
    /// sprite block) and splicing them into existing code at structural anchor points.
    /// 
    /// Adding a new animation type requires ZERO merge code — just generate the snippet
    /// in the standard format and call <see cref="MergeSnippetIntoExistingCode"/>.
    /// </summary>
    public static class AnimationMergeEngine
    {
        // ── Section markers recognised in any animation snippet ──────────────

        // Add block patterns
        private static readonly Regex RxAddBlock = new Regex(
            @"(\w+)\.Add\(\s*new\s+MySprite", RegexOptions.Compiled);

        // Data = "SpriteName" inside a MySprite initializer
        private static readonly Regex RxSpriteData = new Regex(
            @"Data\s*=\s*""([^""]+)""", RegexOptions.Compiled);

        // Array/variable declarations (fields) — int[], float[], int _tick, bool, byte
        private static readonly Regex RxFieldDecl = new Regex(
            @"^\s*(?:int\[\]|float\[\]|int\s+_tick|int\s+_anim|bool\s+\w+Visible|byte\s+\w+Alpha|float\s+hue)\s",
            RegexOptions.Compiled | RegexOptions.Multiline);

        // Ease function detection
        private static readonly Regex RxEaseFunction = new Regex(
            @"(?:public\s+)?float\s+Ease\s*\(\s*float\s+\w+\s*,\s*int\s+\w+\s*\)",
            RegexOptions.Compiled);

        // ── Parsed snippet sections ──────────────────────────────────────────

        /// <summary>
        /// Structural sections extracted from an animation snippet.
        /// </summary>
        private class SnippetSections
        {
            /// <summary>Field-level declarations: arrays, tick counters, animation variables.</summary>
            public List<string> FieldLines = new List<string>();

            /// <summary>The Ease() helper function block (empty if not present).</summary>
            public List<string> HelperLines = new List<string>();

            /// <summary>Render-time logic: tick increment, interpolation math.</summary>
            public List<string> RenderLines = new List<string>();

            /// <summary>The sprite Add(new MySprite { ... }); block including any wrappers (if/blink).</summary>
            public List<string> SpriteBlockLines = new List<string>();

            /// <summary>The sprite name (Data value) from the Add block.</summary>
            public string SpriteName;
        }

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// Universal merge: splices an animation snippet into existing code.
        /// Works for any animation type (simple, keyframe, future types).
        /// 
        /// The snippet is parsed into structural sections and each section is
        /// inserted at the appropriate anchor point in the existing code:
        /// - Fields → before the Ease function, or before Program(), or at top
        /// - Ease helper → before Program() if not already present
        /// - Render logic → before the target sprite's Add block inside Main/render
        /// - Sprite block → replaces the target sprite's static Add block
        /// 
        /// Returns the merged code, or null if no suitable merge point was found.
        /// </summary>
        /// <param name="existingCode">The user's current code in the editor.</param>
        /// <param name="snippetCode">The generated animation snippet (raw, not complete program).</param>
        /// <param name="targetSpriteName">The sprite name (Data value) to find and replace.
        /// If null, the first Add block in existing code is targeted.</param>
        public static string MergeSnippetIntoExistingCode(
            string existingCode, string snippetCode, string targetSpriteName = null)
        {
            if (string.IsNullOrEmpty(existingCode) || string.IsNullOrEmpty(snippetCode))
                return null;

            // ── Parse snippet into sections ──
            var sections = ParseSnippet(snippetCode);
            if (sections == null) return null;

            // Use provided sprite name or the one from the snippet
            string spriteName = targetSpriteName ?? sections.SpriteName;

            // ── Find and replace the target sprite's Add block ──
            string result = existingCode;

            // Find the target sprite's static Add block
            var spriteRange = FindSpriteAddBlock(result, spriteName);
            if (spriteRange == null) return null; // can't find the sprite

            // Detect indentation from existing code
            string indent = GetLineIndent(result, spriteRange.Value.start);

            // Build the replacement: render logic + animated sprite block
            var replacement = new StringBuilder();

            // Insert render logic (tick++, interpolation)
            if (sections.RenderLines.Count > 0)
            {
                foreach (string line in sections.RenderLines)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0)
                        replacement.AppendLine();
                    else
                        replacement.AppendLine(indent + trimmed);
                }
                replacement.AppendLine();
            }

            // Insert animated sprite block
            foreach (string line in sections.SpriteBlockLines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0)
                    replacement.AppendLine();
                else
                    replacement.AppendLine(indent + trimmed);
            }

            string replacementStr = replacement.ToString().TrimEnd('\r', '\n');

            // Replace the static sprite block
            result = result.Substring(0, spriteRange.Value.start)
                   + replacementStr + Environment.NewLine
                   + result.Substring(spriteRange.Value.end);

            // ── Insert field declarations ──
            if (sections.FieldLines.Count > 0)
            {
                int fieldAnchor = FindFieldInsertionPoint(result);
                if (fieldAnchor >= 0)
                {
                    string fieldIndent = GetLineIndent(result, fieldAnchor);
                    var fieldBlock = new StringBuilder();
                    fieldBlock.AppendLine();
                    foreach (string line in sections.FieldLines)
                    {
                        string trimmed = line.Trim();
                        if (trimmed.Length == 0)
                            fieldBlock.AppendLine();
                        else
                            fieldBlock.AppendLine(fieldIndent + trimmed);
                    }
                    result = result.Insert(fieldAnchor, fieldBlock.ToString());
                }
            }

            // ── Insert Ease helper if needed ──
            if (sections.HelperLines.Count > 0 && !RxEaseFunction.IsMatch(result))
            {
                int helperAnchor = FindHelperInsertionPoint(result);
                if (helperAnchor >= 0)
                {
                    var helperBlock = new StringBuilder();
                    helperBlock.AppendLine();
                    foreach (string line in sections.HelperLines)
                        helperBlock.AppendLine(line);
                    result = result.Insert(helperAnchor, helperBlock.ToString());
                }
            }

            return result;
        }

        /// <summary>
        /// Merges a complete generated program with an existing program using
        /// line-level diff. Computes what the animation adds/changes compared to
        /// a "static base" (the existing code) and applies those changes.
        /// 
        /// This is the highest-level merge: give it the existing code and a
        /// complete generated program, and it figures out what to splice.
        /// 
        /// Falls back to <see cref="MergeSnippetIntoExistingCode"/> internally.
        /// Returns null if merge is not possible.
        /// </summary>
        public static string MergeCompleteIntoExisting(
            string existingCode, string completeCode, string snippetCode, string targetSpriteName = null)
        {
            if (string.IsNullOrEmpty(existingCode))
                return completeCode;

            // Try snippet-based structural merge first (most reliable)
            if (!string.IsNullOrEmpty(snippetCode))
            {
                string merged = MergeSnippetIntoExistingCode(existingCode, snippetCode, targetSpriteName);
                if (merged != null) return merged;
            }

            // Fallback: line-level diff between existing and complete
            return ApplyLineDiff(existingCode, completeCode);
        }

        // ── Snippet parser ───────────────────────────────────────────────────

        /// <summary>
        /// Parses a raw animation snippet into structural sections.
        /// Recognises the standard snippet format used by all animation generators:
        /// [header comment] → [field hint + fields] → [ease function] → [render hint + logic] → [sprite block]
        /// </summary>
        private static SnippetSections ParseSnippet(string snippet)
        {
            if (string.IsNullOrEmpty(snippet)) return null;

            var sections = new SnippetSections();
            string[] lines = snippet.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            // State machine phases
            const int PHASE_HEADER = 0;
            const int PHASE_FIELDS = 1;
            const int PHASE_EASE = 2;
            const int PHASE_RENDER = 3;
            const int PHASE_SPRITE = 4;

            int phase = PHASE_HEADER;
            int braceDepth = 0;
            bool inEaseBody = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();

                // Skip header comments (// ─── Animation: ... ───)
                if (phase == PHASE_HEADER)
                {
                    if (trimmed.StartsWith("// ─── ") || trimmed.StartsWith("// ──") ||
                        trimmed.Length == 0 || trimmed.StartsWith("//"))
                    {
                        // Check if this line is a field hint → transition to fields
                        if (trimmed.StartsWith("// Field —") || trimmed.StartsWith("// Add to"))
                        {
                            phase = PHASE_FIELDS;
                            continue; // skip hint line
                        }
                        // Check if this line starts the Ease function
                        if (RxEaseFunction.IsMatch(trimmed))
                        {
                            phase = PHASE_EASE;
                            // fall through to handle ease
                        }
                        // Check if it's a field declaration directly
                        else if (RxFieldDecl.IsMatch(line))
                        {
                            phase = PHASE_FIELDS;
                            // fall through to handle field
                        }
                        else
                        {
                            continue; // skip header/comment lines
                        }
                    }
                    else if (RxFieldDecl.IsMatch(line))
                    {
                        phase = PHASE_FIELDS;
                        // fall through
                    }
                    else if (RxEaseFunction.IsMatch(trimmed))
                    {
                        phase = PHASE_EASE;
                        // fall through
                    }
                    else if (trimmed.StartsWith("_tick") || trimmed.StartsWith("_anim"))
                    {
                        phase = PHASE_RENDER;
                        // fall through
                    }
                    else
                    {
                        continue;
                    }
                }

                // ── Ease function phase ──
                if (phase == PHASE_EASE || (phase <= PHASE_EASE && RxEaseFunction.IsMatch(trimmed)))
                {
                    if (RxEaseFunction.IsMatch(trimmed) && !inEaseBody)
                    {
                        phase = PHASE_EASE;
                        inEaseBody = false;
                        braceDepth = 0;
                    }

                    sections.HelperLines.Add(line);

                    foreach (char ch in line)
                    {
                        if (ch == '{') { braceDepth++; inEaseBody = true; }
                        else if (ch == '}') braceDepth--;
                    }

                    if (inEaseBody && braceDepth <= 0)
                    {
                        // Ease function complete
                        phase = PHASE_FIELDS; // fields may come after ease in some formats
                        inEaseBody = false;
                    }
                    continue;
                }

                // ── Field declarations phase ──
                if (phase == PHASE_FIELDS)
                {
                    // Skip hint comments
                    if (trimmed.StartsWith("// Field —") || trimmed.StartsWith("// Add to") ||
                        trimmed.StartsWith("// ── Keyframe data"))
                    {
                        sections.FieldLines.Add(line);
                        continue;
                    }

                    // Field declarations
                    if (RxFieldDecl.IsMatch(line) || 
                        Regex.IsMatch(trimmed, @"^(?:int|float|byte|bool)\s") ||
                        Regex.IsMatch(trimmed, @"^(?:int|float)\[\]\s"))
                    {
                        sections.FieldLines.Add(line);
                        continue;
                    }

                    // Blank lines in field section
                    if (trimmed.Length == 0)
                    {
                        sections.FieldLines.Add(line);
                        continue;
                    }

                    // Render hint → transition to render
                    if (trimmed.StartsWith("// In your") || trimmed.StartsWith("// ── In "))
                    {
                        phase = PHASE_RENDER;
                        continue; // skip hint
                    }

                    // Ease function appearing after fields
                    if (RxEaseFunction.IsMatch(trimmed))
                    {
                        phase = PHASE_EASE;
                        i--; // re-process this line
                        continue;
                    }

                    // Anything else → transition to render
                    phase = PHASE_RENDER;
                    // fall through
                }

                // ── Render logic phase ──
                if (phase == PHASE_RENDER)
                {
                    // Skip render hint comments
                    if (trimmed.StartsWith("// In your") || trimmed.StartsWith("// ── In "))
                        continue;

                    // Detect sprite Add block start
                    if (RxAddBlock.IsMatch(trimmed) || 
                        (trimmed.StartsWith("if (") && trimmed.Contains("Visible)")) ||
                        (trimmed.StartsWith("if (") && trimmed.Contains("blinkVisible)")))
                    {
                        // Check if this is a conditional wrapper (blink) or direct Add
                        if (trimmed.StartsWith("if ("))
                        {
                            phase = PHASE_SPRITE;
                            sections.SpriteBlockLines.Add(line);
                            continue;
                        }
                        phase = PHASE_SPRITE;
                        // fall through to sprite phase
                    }
                    else
                    {
                        sections.RenderLines.Add(line);
                        continue;
                    }
                }

                // ── Sprite block phase ──
                if (phase == PHASE_SPRITE)
                {
                    sections.SpriteBlockLines.Add(line);

                    // Extract sprite name from Data = "..."
                    var dataMatch = RxSpriteData.Match(line);
                    if (dataMatch.Success && sections.SpriteName == null)
                        sections.SpriteName = dataMatch.Groups[1].Value;
                }
            }

            // Trim trailing blank lines from each section
            TrimTrailingBlanks(sections.FieldLines);
            TrimTrailingBlanks(sections.RenderLines);
            TrimTrailingBlanks(sections.SpriteBlockLines);

            return sections.SpriteBlockLines.Count > 0 ? sections : null;
        }

        // ── Anchor finders ───────────────────────────────────────────────────

        /// <summary>
        /// Finds the character position in existing code where field declarations
        /// should be inserted. Priority order:
        /// 1. Before "// ── Easing helper" comment
        /// 2. Before "public float Ease(" or "float Ease("
        /// 3. Before "public Program()"
        /// 4. Before "public void Main("
        /// 5. Start of file
        /// </summary>
        private static int FindFieldInsertionPoint(string code)
        {
            // Try each anchor in priority order
            string[] anchors = {
                "// ── Easing helper",
                "public float Ease(",
                "float Ease(",
                "public Program()",
                "public void Main(",
            };

            foreach (string anchor in anchors)
            {
                int idx = code.IndexOf(anchor, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    // Walk back to line start
                    while (idx > 0 && code[idx - 1] != '\n') idx--;
                    return idx;
                }
            }

            return 0; // start of file
        }

        /// <summary>
        /// Finds the position where the Ease helper function should be inserted.
        /// Before Program() or Main().
        /// </summary>
        private static int FindHelperInsertionPoint(string code)
        {
            string[] anchors = { "public Program()", "public void Main(" };
            foreach (string anchor in anchors)
            {
                int idx = code.IndexOf(anchor, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    while (idx > 0 && code[idx - 1] != '\n') idx--;
                    return idx;
                }
            }
            return code.Length;
        }

        /// <summary>
        /// Finds the character range of a sprite's Add(new MySprite { ... }); block
        /// by matching Data = "spriteName". Returns (start, end) character offsets,
        /// or null if not found.
        /// </summary>
        private static (int start, int end)? FindSpriteAddBlock(string code, string spriteName)
        {
            // Find all Add(new MySprite blocks
            var matches = RxAddBlock.Matches(code);
            foreach (Match m in matches)
            {
                // Find the closing "});" for this block
                int closeIdx = FindMatchingClose(code, m.Index);
                if (closeIdx < 0) continue;

                string block = code.Substring(m.Index, closeIdx - m.Index);

                // Check if this block contains our target sprite
                if (spriteName != null)
                {
                    var dataMatch = RxSpriteData.Match(block);
                    if (!dataMatch.Success || dataMatch.Groups[1].Value != spriteName)
                        continue;
                }

                // Also check this is a STATIC block (no interpolation variables)
                // Animated blocks contain "← animated" or variable references like "_interp"
                if (block.Contains("← animated") || block.Contains("_interp"))
                    continue;

                // Walk back to line start
                int lineStart = m.Index;
                while (lineStart > 0 && code[lineStart - 1] != '\n') lineStart--;

                return (lineStart, closeIdx);
            }

            // Fallback: if spriteName is null, target the first static Add block
            if (spriteName == null)
            {
                foreach (Match m in matches)
                {
                    int closeIdx = FindMatchingClose(code, m.Index);
                    if (closeIdx < 0) continue;

                    string block = code.Substring(m.Index, closeIdx - m.Index);
                    if (block.Contains("← animated") || block.Contains("_interp"))
                        continue;

                    int lineStart = m.Index;
                    while (lineStart > 0 && code[lineStart - 1] != '\n') lineStart--;
                    return (lineStart, closeIdx);
                }
            }

            return null;
        }

        /// <summary>
        /// Finds the closing ");" after an Add(new MySprite { ... }) block,
        /// properly handling nested braces.
        /// </summary>
        private static int FindMatchingClose(string code, int startIdx)
        {
            // Find the opening brace of the initializer
            int braceStart = code.IndexOf('{', startIdx);
            if (braceStart < 0) return -1;

            int depth = 0;
            for (int i = braceStart; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}') depth--;

                if (depth == 0)
                {
                    // Found closing brace, now find ");" after it
                    int afterBrace = i + 1;
                    // Skip whitespace
                    while (afterBrace < code.Length && char.IsWhiteSpace(code[afterBrace]) && code[afterBrace] != '\n')
                        afterBrace++;
                    if (afterBrace < code.Length && code[afterBrace] == ')')
                    {
                        afterBrace++;
                        if (afterBrace < code.Length && code[afterBrace] == ';')
                        {
                            afterBrace++;
                            // Include trailing newline
                            if (afterBrace < code.Length && code[afterBrace] == '\r') afterBrace++;
                            if (afterBrace < code.Length && code[afterBrace] == '\n') afterBrace++;
                            return afterBrace;
                        }
                    }
                    return -1;
                }
            }
            return -1;
        }

        // ── Line-level diff merge (fallback) ─────────────────────────────────

        /// <summary>
        /// Applies a line-level diff from the complete generated code onto the existing code.
        /// Uses LCS to identify unchanged regions and applies insertions/modifications.
        /// This is a last-resort fallback when structural merge can't find anchor points.
        /// </summary>
        private static string ApplyLineDiff(string existingCode, string targetCode)
        {
            if (string.IsNullOrEmpty(existingCode)) return targetCode;
            if (string.IsNullOrEmpty(targetCode)) return existingCode;

            var oldLines = existingCode.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var newLines = targetCode.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            int n = oldLines.Length;
            int m = newLines.Length;

            // LCS table
            var lcs = new int[n + 1, m + 1];
            for (int i = 1; i <= n; i++)
                for (int j = 1; j <= m; j++)
                    lcs[i, j] = oldLines[i - 1].Trim() == newLines[j - 1].Trim()
                        ? lcs[i - 1, j - 1] + 1
                        : Math.Max(lcs[i - 1, j], lcs[i, j - 1]);

            // Backtrack to produce merged output
            var result = new List<string>();
            int oi = n, ni = m;
            var stack = new List<(char type, string line)>();

            while (oi > 0 || ni > 0)
            {
                if (oi > 0 && ni > 0 && oldLines[oi - 1].Trim() == newLines[ni - 1].Trim())
                {
                    // Unchanged — keep the existing line (preserves user formatting)
                    stack.Add(('=', oldLines[oi - 1]));
                    oi--; ni--;
                }
                else if (ni > 0 && (oi == 0 || lcs[oi, ni - 1] >= lcs[oi - 1, ni]))
                {
                    // Added in target — include it
                    stack.Add(('+', newLines[ni - 1]));
                    ni--;
                }
                else
                {
                    // Removed from target — keep it (preserve user code)
                    // Only remove if it looks like generated code being replaced
                    stack.Add(('=', oldLines[oi - 1]));
                    oi--;
                }
            }

            stack.Reverse();
            foreach (var entry in stack)
                result.Add(entry.line);

            return string.Join(Environment.NewLine, result);
        }

        // ── Utilities ────────────────────────────────────────────────────────

        /// <summary>Gets the whitespace indentation at a given character position.</summary>
        private static string GetLineIndent(string code, int position)
        {
            // Walk forward from line start to first non-whitespace
            int lineStart = position;
            while (lineStart > 0 && code[lineStart - 1] != '\n') lineStart--;

            var sb = new StringBuilder();
            for (int i = lineStart; i < code.Length; i++)
            {
                if (code[i] == ' ' || code[i] == '\t')
                    sb.Append(code[i]);
                else
                    break;
            }
            return sb.ToString();
        }

        /// <summary>Removes trailing blank lines from a list.</summary>
        private static void TrimTrailingBlanks(List<string> lines)
        {
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[lines.Count - 1]))
                lines.RemoveAt(lines.Count - 1);
        }
    }
}
