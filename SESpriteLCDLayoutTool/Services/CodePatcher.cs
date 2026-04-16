using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using SESpriteLCDLayoutTool.Models;

namespace SESpriteLCDLayoutTool.Services
{
    public static partial class CodeGenerator
    {
        // ── Per-sprite dynamic round-trip ─────────────────────────────────────

        /// <summary>
        /// Per-sprite property patching for dynamic code (loops, switch/case, expressions).
        /// Compares each sprite's current values against its ImportBaseline and surgically
        /// replaces only the literal values (colors, textures, fonts) that changed,
        /// preserving all dynamic expressions and surrounding code.
        /// Returns null if per-sprite patching is not applicable.
        /// </summary>
        public static string PatchOriginalSource(LcdLayout layout)
        {
            string original = layout.OriginalSourceCode;
            if (string.IsNullOrWhiteSpace(original)) return null;

            // Collect sprites that have source tracking and a baseline
            var patchable = new System.Collections.Generic.List<SpriteEntry>();
            foreach (var sp in layout.Sprites)
            {
                if (sp.IsReferenceLayout) continue;
                if (sp.SourceStart < 0 || sp.SourceEnd < 0 || sp.ImportBaseline == null) continue;
                patchable.Add(sp);
            }

            if (patchable.Count == 0) return null;

            // Sort by SourceStart descending so we patch from end-to-start (preserves offsets)
            patchable.Sort((a, b) => b.SourceStart.CompareTo(a.SourceStart));

            var result = new StringBuilder(original);

            foreach (var sp in patchable)
            {
                var bl = sp.ImportBaseline;
                if (sp.SourceStart >= result.Length || sp.SourceEnd > result.Length) continue;

                string chunk = result.ToString(sp.SourceStart, sp.SourceEnd - sp.SourceStart);
                string patched = chunk;

                // Patch texture/sprite name (string literal)
                if (sp.Type == SpriteEntryType.Texture &&
                    bl.SpriteName != sp.SpriteName &&
                    bl.SpriteName != null && sp.SpriteName != null)
                {
                    patched = ReplaceStringLiteral(patched, bl.SpriteName, sp.SpriteName);
                }

                // Patch text content (string literal)
                if (sp.Type == SpriteEntryType.Text &&
                    bl.Text != sp.Text &&
                    bl.Text != null && sp.Text != null)
                {
                    patched = ReplaceStringLiteral(patched, bl.Text, sp.Text);
                }

                // Patch font name (string literal)
                if (bl.FontId != sp.FontId &&
                    bl.FontId != null && sp.FontId != null)
                {
                    patched = ReplaceStringLiteral(patched, bl.FontId, sp.FontId);
                }

                // Patch color (new Color(...) literals or named colors like Color.White)
                if (bl.ColorR != sp.ColorR || bl.ColorG != sp.ColorG ||
                    bl.ColorB != sp.ColorB || bl.ColorA != sp.ColorA)
                {
                    patched = PatchColorLiteral(patched, bl, sp);
                }

                // Patch position (literal Vector2)
                if (Math.Abs(bl.X - sp.X) > 0.05f || Math.Abs(bl.Y - sp.Y) > 0.05f)
                {
                    patched = PatchVector2Literal(patched, "Position", sp.X, sp.Y);
                }

                // Patch size (literal Vector2, texture sprites only)
                if (sp.Type == SpriteEntryType.Texture &&
                    (Math.Abs(bl.Width - sp.Width) > 0.05f || Math.Abs(bl.Height - sp.Height) > 0.05f))
                {
                    patched = PatchVector2Literal(patched, "Size", sp.Width, sp.Height);
                }

                // Patch rotation/scale (literal float)
                if (sp.Type == SpriteEntryType.Texture && Math.Abs(bl.Rotation - sp.Rotation) > 0.0005f)
                {
                    patched = PatchFloatLiteral(patched, "RotationOrScale", sp.Rotation, 4);
                }
                else if (sp.Type == SpriteEntryType.Text && Math.Abs(bl.Scale - sp.Scale) > 0.005f)
                {
                    patched = PatchFloatLiteral(patched, "RotationOrScale", sp.Scale, 2);
                }

                // Only substitute if something actually changed
                if (patched != chunk)
                {
                    result.Remove(sp.SourceStart, sp.SourceEnd - sp.SourceStart);
                    result.Insert(sp.SourceStart, patched);
                }
            }

            string final = result.ToString();
            // Return the (possibly unchanged) original — null only when patching is not applicable
            return final;
        }

        /// <summary>
        /// Inserts code blocks for newly added sprites (those with SourceStart &lt; 0) into the
        /// patched source code. After insertion, updates each sprite's SourceStart/SourceEnd
        /// and creates an ImportBaseline so they become tracked for future round-trip patching.
        /// Returns the modified code with new sprites inserted, or the original if no insertion needed.
        /// </summary>
        public static string InsertNewSpritesIntoSource(LcdLayout layout, string patchedCode)
        {
            if (layout == null || string.IsNullOrWhiteSpace(patchedCode)) return patchedCode;

            // Find untracked sprites (newly added, not yet in the code)
            var newSprites = new List<SpriteEntry>();
            foreach (var sp in layout.Sprites)
            {
                if (sp.IsReferenceLayout) continue;
                if (sp.SourceStart >= 0) continue; // already tracked
                newSprites.Add(sp);
            }

            if (newSprites.Count == 0) return patchedCode;

            // Find the insertion point: look for the last "frame.Add(" or ".Add(new MySprite"
            // and insert after its closing ");", or before "}" if we can find the frame block end.
            int insertPos = FindSpriteInsertionPoint(patchedCode);
            if (insertPos < 0) return patchedCode; // couldn't find a safe insertion point

            // Detect indentation from surrounding code
            string indent = DetectIndentation(patchedCode, insertPos);

            var sb = new StringBuilder(patchedCode);
            int offset = 0; // track how much we've shifted positions

            foreach (var sp in newSprites)
            {
                string spriteCode = GenerateSingleSpriteCode(sp, indent);
                int actualInsertPos = insertPos + offset;

                sb.Insert(actualInsertPos, spriteCode);

                // Update the sprite's source tracking so it becomes a tracked sprite
                sp.SourceStart = actualInsertPos;
                sp.SourceEnd = actualInsertPos + spriteCode.Length;
                sp.ImportBaseline = sp.CloneValues();
                sp.ImportLabel = sp.Type == SpriteEntryType.Text ? "Text" : (sp.SpriteName ?? "Texture");

                offset += spriteCode.Length;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Finds the best position to insert new sprite code blocks.
        /// Looks for patterns like "frame.Add(new MySprite" or ".Add(new MySprite"
        /// and returns the position after the last such block's closing ");".
        /// </summary>
        private static int FindSpriteInsertionPoint(string code)
        {
            // Strategy: Find all ".Add(new MySprite" occurrences and pick the last one's end
            int lastAddEnd = -1;
            int searchPos = 0;

            while (searchPos < code.Length)
            {
                // Look for frame.Add or .Add patterns followed by MySprite
                int addIdx = code.IndexOf(".Add(new MySprite", searchPos, StringComparison.Ordinal);
                if (addIdx < 0)
                    addIdx = code.IndexOf(".Add(MySprite.Create", searchPos, StringComparison.Ordinal);
                if (addIdx < 0) break;

                // Find the closing ");" for this sprite block
                int closePos = FindSpriteBlockEnd(code, addIdx);
                if (closePos > lastAddEnd)
                    lastAddEnd = closePos;

                searchPos = addIdx + 10;
            }

            // If we found sprite adds, insert after the last one
            if (lastAddEnd > 0)
                return lastAddEnd;

            // Fallback: look for "using (var frame = " and insert before its closing "}"
            int frameStart = code.IndexOf("using (var frame =", StringComparison.Ordinal);
            if (frameStart < 0)
                frameStart = code.IndexOf("using(var frame =", StringComparison.Ordinal);
            if (frameStart >= 0)
            {
                // Find the opening brace of the using block
                int braceOpen = code.IndexOf('{', frameStart);
                if (braceOpen >= 0)
                {
                    // Find matching closing brace
                    int depth = 1;
                    int pos = braceOpen + 1;
                    while (pos < code.Length && depth > 0)
                    {
                        if (code[pos] == '{') depth++;
                        else if (code[pos] == '}') depth--;
                        if (depth > 0) pos++;
                    }
                    // Insert before the closing brace, with proper newlines
                    if (depth == 0 && pos > braceOpen)
                        return pos;
                }
            }

            return -1;
        }

        /// <summary>
        /// Finds the end position of a sprite block starting from a ".Add(" call.
        /// Returns the position after the closing ");" or "});" for object initializers.
        /// </summary>
        private static int FindSpriteBlockEnd(string code, int addStart)
        {
            // Look for object initializer pattern: .Add(new MySprite { ... });
            int braceOpen = code.IndexOf('{', addStart);
            int parenClose = code.IndexOf(");", addStart, StringComparison.Ordinal);

            // If there's a brace before the first ");", it's an object initializer
            if (braceOpen >= 0 && (parenClose < 0 || braceOpen < parenClose))
            {
                // Find matching closing brace
                int depth = 1;
                int pos = braceOpen + 1;
                while (pos < code.Length && depth > 0)
                {
                    if (code[pos] == '{') depth++;
                    else if (code[pos] == '}') depth--;
                    pos++;
                }
                // Now find ");" after the closing brace
                int endMarker = code.IndexOf(");", pos, StringComparison.Ordinal);
                if (endMarker >= 0)
                {
                    // Include trailing newline if present
                    int end = endMarker + 2;
                    if (end < code.Length && code[end] == '\r') end++;
                    if (end < code.Length && code[end] == '\n') end++;
                    return end;
                }
            }
            else if (parenClose >= 0)
            {
                // Simple constructor call: .Add(MySprite.CreateText(...));
                int end = parenClose + 2;
                if (end < code.Length && code[end] == '\r') end++;
                if (end < code.Length && code[end] == '\n') end++;
                return end;
            }

            return -1;
        }

        /// <summary>
        /// Detects the indentation level at the given position by looking at surrounding code.
        /// </summary>
        private static string DetectIndentation(string code, int pos)
        {
            // Walk backward to find the start of the previous line
            int lineStart = pos - 1;
            while (lineStart > 0 && code[lineStart] != '\n') lineStart--;
            if (lineStart > 0) lineStart++; // skip the \n

            // Count leading whitespace
            var indent = new StringBuilder();
            int i = lineStart;
            while (i < code.Length && (code[i] == ' ' || code[i] == '\t'))
            {
                indent.Append(code[i]);
                i++;
            }

            // Default to 8 spaces if we couldn't detect
            return indent.Length > 0 ? indent.ToString() : "        ";
        }

        /// <summary>
        /// Generates the code block for a single sprite, ready to be inserted into existing source.
        /// Uses the standard frame.Add(new MySprite { ... }); pattern.
        /// </summary>
        public static string GenerateSingleSpriteCode(SpriteEntry sp, string indent = "        ")
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.Append(indent);
            sb.AppendLine($"// [+] {sp.DisplayName}");
            sb.Append(indent);
            sb.AppendLine("frame.Add(new MySprite");
            sb.Append(indent);
            sb.AppendLine("{");

            string innerIndent = indent + "    ";

            if (sp.Type == SpriteEntryType.Text)
            {
                sb.Append(innerIndent);
                sb.AppendLine("Type           = SpriteType.TEXT,");
                sb.Append(innerIndent);
                sb.AppendLine($"Data           = {Q(sp.Text)},");
                sb.Append(innerIndent);
                sb.AppendLine($"Position       = new Vector2({sp.X:F4}f, {sp.Y:F4}f),");
                sb.Append(innerIndent);
                sb.AppendLine($"Color          = new Color({sp.ColorR}, {sp.ColorG}, {sp.ColorB}, {sp.ColorA}),");
                sb.Append(innerIndent);
                sb.AppendLine($"FontId         = {Q(sp.FontId)},");
                sb.Append(innerIndent);
                sb.AppendLine($"Alignment      = TextAlignment.{sp.Alignment.ToString().ToUpperInvariant()},");
                sb.Append(innerIndent);
                sb.AppendLine($"RotationOrScale = {sp.Scale:F2}f,");
            }
            else
            {
                sb.Append(innerIndent);
                sb.AppendLine("Type           = SpriteType.TEXTURE,");
                sb.Append(innerIndent);
                sb.AppendLine($"Data           = {Q(sp.SpriteName)},");
                sb.Append(innerIndent);
                sb.AppendLine($"Position       = new Vector2({sp.X:F4}f, {sp.Y:F4}f),");
                sb.Append(innerIndent);
                sb.AppendLine($"Size           = new Vector2({sp.Width:F4}f, {sp.Height:F4}f),");
                sb.Append(innerIndent);
                sb.AppendLine($"Color          = new Color({sp.ColorR}, {sp.ColorG}, {sp.ColorB}, {sp.ColorA}),");
                sb.Append(innerIndent);
                sb.AppendLine("Alignment      = TextAlignment.CENTER,");
                sb.Append(innerIndent);
                sb.AppendLine($"RotationOrScale = {sp.Rotation:F4}f,");
            }

            sb.Append(indent);
            sb.AppendLine("});");

            return sb.ToString();
        }

        /// <summary>Replaces a quoted string literal in the source text.</summary>
        private static string ReplaceStringLiteral(string text, string oldValue, string newValue)
        {
            string escapedOld = $"\"{Esc(oldValue)}\"";
            string escapedNew = $"\"{Esc(newValue)}\"";
            int idx = text.IndexOf(escapedOld, StringComparison.Ordinal);
            if (idx < 0) return text;
            return text.Substring(0, idx) + escapedNew + text.Substring(idx + escapedOld.Length);
        }

        /// <summary>
        /// Patches a color literal in the source text. Handles new Color(R,G,B),
        /// new Color(R,G,B,A), and named colors like Color.White.
        /// </summary>
        private static string PatchColorLiteral(string text, SpriteEntry baseline, SpriteEntry current)
        {
            string newColor = current.ColorA != 255
                ? $"new Color({current.ColorR}, {current.ColorG}, {current.ColorB}, {current.ColorA})"
                : $"new Color({current.ColorR}, {current.ColorG}, {current.ColorB})";

            // Try matching "new Color(R, G, B)" or "new Color(R, G, B, A)"
            var colorPattern = new Regex(
                @"new\s+Color\s*\(\s*" + baseline.ColorR + @"\s*,\s*" + baseline.ColorG +
                @"\s*,\s*" + baseline.ColorB + @"(?:\s*,\s*\d+)?\s*\)");

            if (colorPattern.IsMatch(text))
                return colorPattern.Replace(text, newColor, 1);

            // Try named colors: Color.White → (255,255,255), Color.Red → (255,0,0), etc.
            string namedColor = MatchNamedColor(baseline);
            if (namedColor != null)
            {
                int idx = text.IndexOf(namedColor, StringComparison.Ordinal);
                if (idx >= 0)
                    return text.Substring(0, idx) + newColor + text.Substring(idx + namedColor.Length);
            }

            return text; // color is an expression — can't patch
        }

        /// <summary>Returns the C# named color expression that matches the baseline values, or null.</summary>
        private static string MatchNamedColor(SpriteEntry bl)
        {
            if (bl.ColorA == 255)
            {
                if (bl.ColorR == 255 && bl.ColorG == 255 && bl.ColorB == 255) return "Color.White";
                if (bl.ColorR == 0   && bl.ColorG == 0   && bl.ColorB == 0)   return "Color.Black";
                if (bl.ColorR == 255 && bl.ColorG == 0   && bl.ColorB == 0)   return "Color.Red";
                if (bl.ColorR == 0   && bl.ColorG == 255 && bl.ColorB == 0)   return "Color.Green";
                if (bl.ColorR == 0   && bl.ColorG == 0   && bl.ColorB == 255) return "Color.Blue";
                if (bl.ColorR == 255 && bl.ColorG == 255 && bl.ColorB == 0)   return "Color.Yellow";
            }
            if (bl.ColorR == 0 && bl.ColorG == 0 && bl.ColorB == 0 && bl.ColorA == 0)
                return "Color.Transparent";
            return null;
        }

        /// <summary>
        /// Patches a literal Vector2 assignment (e.g. Position = new Vector2(100.0f, 200.0f))
        /// in a source chunk.  Only replaces when both components are literal numbers.
        /// </summary>
        private static string PatchVector2Literal(string text, string propName, float newX, float newY)
        {
            var pattern = new Regex(
                @"(" + Regex.Escape(propName) + @"\s*=\s*new\s+Vector2\s*\(\s*)" +
                @"-?[\d.]+f?\s*,\s*-?[\d.]+f?" +
                @"(\s*\))");

            if (!pattern.IsMatch(text)) return text;
            return pattern.Replace(text, m =>
                m.Groups[1].Value + $"{newX:F1}f, {newY:F1}f" + m.Groups[2].Value, 1);
        }

        /// <summary>
        /// Patches a literal float assignment (e.g. RotationOrScale = 0.5000f)
        /// in a source chunk.  Only replaces when the value is a literal number.
        /// </summary>
        private static string PatchFloatLiteral(string text, string propName, float newValue, int decimals)
        {
            var pattern = new Regex(
                @"(" + Regex.Escape(propName) + @"\s*=\s*)" +
                @"-?[\d.]+f" +
                @"(\s*[,;])");

            if (!pattern.IsMatch(text)) return text;
            string formatted = newValue.ToString($"F{decimals}") + "f";
            return pattern.Replace(text, m =>
                m.Groups[1].Value + formatted + m.Groups[2].Value, 1);
        }

        /// <summary>
        /// Detects the surrounding code context for a sprite at a given position.
        /// Looks backwards for case labels, method names, or region comments.
        /// Returns a label like "Header", "Item", "Footer" or null.
        /// </summary>
        public static string DetectSpriteContext(string code, int position)
        {
            if (code == null || position <= 0) return null;

            string before = code.Substring(0, Math.Min(position, code.Length));

            // Strategy 1: nearest case statement (switch-based layouts)
            var caseMatch = Regex.Match(before,
                @"case\s+(?:\w+\.)*(\w+)\s*:", RegexOptions.RightToLeft);
            if (caseMatch.Success)
                return caseMatch.Groups[1].Value;

            // Strategies 2 & 3: search the 400 chars immediately before the sprite
            int searchStart = Math.Max(0, before.Length - 400);
            string recent = before.Substring(searchStart);

            // Strategy 2: nearest variable declaration (var/string/float/int/…)
            var varMatch = Regex.Match(recent,
                @"\b(?:var|string|float|int|bool|double|Vector2|Color)\s+(\w+)\s*=",
                RegexOptions.RightToLeft);
            if (varMatch.Success)
                return varMatch.Groups[1].Value;

            // Strategy 3: nearest single-line // comment
            var commentMatch = Regex.Match(recent,
                @"//\s*([A-Za-z][^\r\n]{1,38})", RegexOptions.RightToLeft);
            if (commentMatch.Success)
            {
                string label = commentMatch.Groups[1].Value.Trim();
                if (label.Length >= 2) return label;
            }

            return null;
        }

        // ── Region-based round-trip (static layouts) ──────────────────────────

        /// <summary>
        /// When OriginalSourceCode is set, splices updated sprite definitions back
        /// into the original pasted code so the user can paste straight back into
        /// their project. Returns null if round-trip is not possible.
        /// </summary>
        public static string GenerateRoundTrip(LcdLayout layout)
        {
            string original = layout.OriginalSourceCode;
            if (string.IsNullOrWhiteSpace(original) || layout.Sprites.Count == 0)
                return null;

            // Find all MySprite definitions in the original code.
            // Match: new MySprite { ... }, new MySprite(...), MySprite.CreateText(...), MySprite.CreateSprite(...)
            var spritePattern = new Regex(
                @"(new\s+MySprite\s*[\({])|(MySprite\s*\.\s*Create(?:Text|Sprite)\s*\()",
                RegexOptions.Compiled);

            var matches = spritePattern.Matches(original);
            if (matches.Count == 0) return null;

            // Safety guard: if the number of non-reference sprites in the layout
            // doesn't match the number of MySprite definitions in the original source,
            // round-trip would produce bloated or incorrect output — fall back to Generate().
            int nonRefCount = 0;
            foreach (var sp in layout.Sprites)
                if (!sp.IsReferenceLayout) nonRefCount++;
            if (nonRefCount != matches.Count) return null;

            // Region start: beginning of the LINE containing the first MySprite match.
            // This ensures we capture "frame.Add(" or similar prefixes on the same line.
            // Also absorb any preceding comment/blank lines (like "// [1] SquareSimple").
            int firstMatchPos = matches[0].Index;
            int regionStart = FindLineStart(original, firstMatchPos);
            regionStart = AbsorbPrecedingComments(original, regionStart);

            // Region end: after the last MySprite block's closing punctuation
            int regionEnd = -1;
            for (int i = matches.Count - 1; i >= 0; i--)
            {
                string matchText = matches[i].Value;
                char lastChar = matchText[matchText.Length - 1];

                if (lastChar == '{')
                {
                    int braceEnd = FindMatchingBrace(original, matches[i].Index + matchText.Length - 1);
                    if (braceEnd >= 0)
                        regionEnd = SkipTrailingPunctuation(original, braceEnd + 1);
                }
                else // '(' — constructor or factory method
                {
                    int parenEnd = FindMatchingParen(original, matches[i].Index + matchText.Length - 1);
                    if (parenEnd >= 0)
                        regionEnd = SkipTrailingPunctuation(original, parenEnd + 1);
                }

                if (regionEnd > 0) break;
            }

            if (regionEnd <= regionStart) return null;

            // Detect indentation from the line containing the first MySprite match
            string indent = DetectIndent(original, firstMatchPos);

            // Detect the inner indent from the first property line inside the
            // initializer block instead of hardcoding 4 spaces.  This preserves
            // the user's indentation style (tabs, 2 spaces, 8 spaces, etc.).
            string innerIndent = DetectInnerIndent(original, matches[0].Index, indent);

            // Detect the wrapper prefix on the same line before "new MySprite"
            // (e.g. "frame.Add(" or "sprites.Add(") so the round-trip preserves
            // the user's original calling convention.
            string wrapperPrefix = DetectWrapperPrefix(original, firstMatchPos);
            string wrapperSuffix = (wrapperPrefix.Length > 0 && wrapperPrefix.TrimEnd().EndsWith("(")) ? ");" : ";";

            // Generate just the sprite definitions with the detected indentation
            var sb = new StringBuilder();
            int actualCount = 0;
            for (int i = 0; i < layout.Sprites.Count; i++)
            {
                var sp = layout.Sprites[i];
                if (sp.IsReferenceLayout) continue;

                if (actualCount > 0) sb.AppendLine();

                sb.AppendLine($"{indent}// [{actualCount + 1}] {sp.DisplayName}");
                sb.AppendLine($"{indent}{wrapperPrefix}new MySprite");
                sb.AppendLine($"{indent}{{");

                if (sp.Type == SpriteEntryType.Text)
                {
                    sb.AppendLine($"{innerIndent}Type           = SpriteType.TEXT,");
                    sb.AppendLine($"{innerIndent}Data           = {Q(sp.Text)},");
                    sb.AppendLine($"{innerIndent}Position       = new Vector2({sp.X:F1}f, {sp.Y:F1}f),");
                    sb.AppendLine($"{innerIndent}Color          = new Color({sp.ColorR}, {sp.ColorG}, {sp.ColorB}, {sp.ColorA}),");
                    sb.AppendLine($"{innerIndent}FontId         = {Q(sp.FontId)},");
                    sb.AppendLine($"{innerIndent}Alignment      = TextAlignment.{sp.Alignment.ToString().ToUpperInvariant()},");
                    sb.AppendLine($"{innerIndent}RotationOrScale = {sp.Scale:F2}f,");
                }
                else
                {
                    sb.AppendLine($"{innerIndent}Type           = SpriteType.TEXTURE,");
                    sb.AppendLine($"{innerIndent}Data           = {Q(sp.SpriteName)},");
                    sb.AppendLine($"{innerIndent}Position       = new Vector2({sp.X:F1}f, {sp.Y:F1}f),");
                    sb.AppendLine($"{innerIndent}Size           = new Vector2({sp.Width:F1}f, {sp.Height:F1}f),");
                    sb.AppendLine($"{innerIndent}Color          = new Color({sp.ColorR}, {sp.ColorG}, {sp.ColorB}, {sp.ColorA}),");
                    sb.AppendLine($"{innerIndent}Alignment      = TextAlignment.CENTER,");
                    sb.AppendLine($"{innerIndent}RotationOrScale = {sp.Rotation:F4}f,");
                }

                sb.Append($"{indent}}}{wrapperSuffix}");
                actualCount++;
            }

            if (actualCount == 0) return null;

            // Ensure the splice ends with a newline
            sb.AppendLine();

            // Splice: original[0..regionStart) + generated + original[regionEnd..)
            string before = original.Substring(0, regionStart);
            string after  = original.Substring(regionEnd);

            return before + sb.ToString() + after;
        }

        /// <summary>Finds the position of the start of the line containing <paramref name="position"/>.</summary>
        private static int FindLineStart(string code, int position)
        {
            int i = position;
            while (i > 0 && code[i - 1] != '\n')
                i--;
            return i;
        }

        /// <summary>
        /// From a line start position, walks backwards to absorb any preceding
        /// comment lines or blank lines that are part of the sprite block.
        /// </summary>
        private static int AbsorbPrecedingComments(string code, int lineStart)
        {
            int pos = lineStart;
            while (pos > 0)
            {
                // Find the start of the preceding line
                int prevEnd = pos - 1; // char before current '\n'
                if (prevEnd >= 0 && code[prevEnd] == '\n') prevEnd--; // skip \n
                if (prevEnd >= 0 && code[prevEnd] == '\r') prevEnd--; // skip \r

                int prevStart = prevEnd;
                while (prevStart > 0 && code[prevStart - 1] != '\n')
                    prevStart--;
                if (prevStart < 0) prevStart = 0;

                // Get the trimmed content of the preceding line
                string prevLine = code.Substring(prevStart, prevEnd - prevStart + 1).Trim();

                // Absorb comment lines and blank lines
                if (prevLine.Length == 0 || prevLine.StartsWith("//"))
                    pos = prevStart;
                else
                    break;
            }
            return pos;
        }

        private static string DetectIndent(string code, int position)
        {
            // Walk backwards from position to find the start of the line
            int lineStart = position;
            while (lineStart > 0 && code[lineStart - 1] != '\n')
                lineStart--;

            // Extract leading whitespace
            var indent = new StringBuilder();
            for (int i = lineStart; i < position && i < code.Length; i++)
            {
                if (code[i] == ' ' || code[i] == '\t')
                    indent.Append(code[i]);
                else
                    break;
            }
            return indent.ToString();
        }

        /// <summary>
        /// Detects the inner indentation used for properties inside the first
        /// MySprite initializer block.  Falls back to <paramref name="outerIndent"/>
        /// plus four spaces when detection fails.
        /// </summary>
        private static string DetectInnerIndent(string code, int matchPos, string outerIndent)
        {
            string fallback = outerIndent + "    ";

            // Find the opening '{' of the initializer after matchPos
            int bracePos = code.IndexOf('{', matchPos);
            if (bracePos < 0) return fallback;

            // Find the start of the next line after '{'
            int pos = bracePos + 1;
            while (pos < code.Length && code[pos] != '\n')
                pos++;
            if (pos >= code.Length) return fallback;
            pos++; // skip past '\n'

            // Extract leading whitespace of the first property line
            var sb = new StringBuilder();
            while (pos < code.Length && (code[pos] == ' ' || code[pos] == '\t'))
            {
                sb.Append(code[pos]);
                pos++;
            }

            // Sanity check: the inner indent should be longer than the outer indent
            // and the line should contain something (not be blank).
            string result = sb.ToString();
            if (result.Length > outerIndent.Length && pos < code.Length && code[pos] != '\r' && code[pos] != '\n')
                return result;
            return fallback;
        }

        /// <summary>
        /// Detects the wrapper prefix on the same line before "new MySprite",
        /// e.g. "frame.Add(" or "sprites.Add(".  Returns empty string when
        /// the "new MySprite" is at the beginning of the non-whitespace content.
        /// </summary>
        private static string DetectWrapperPrefix(string code, int matchPos)
        {
            // Walk backwards from matchPos to the start of the line
            int lineStart = matchPos;
            while (lineStart > 0 && code[lineStart - 1] != '\n')
                lineStart--;

            // Skip leading whitespace to get to the content start
            int contentStart = lineStart;
            while (contentStart < matchPos && (code[contentStart] == ' ' || code[contentStart] == '\t'))
                contentStart++;

            // If the content starts right at matchPos, there's no wrapper
            if (contentStart >= matchPos) return "";

            // Extract the text between contentStart and matchPos (e.g. "frame.Add(")
            return code.Substring(contentStart, matchPos - contentStart);
        }

        private static int SkipTrailingPunctuation(string code, int pos)
        {
            // Skip whitespace, ");", ",", or ";" after a closing brace/paren
            while (pos < code.Length)
            {
                char c = code[pos];
                if (c == ')' || c == ';' || c == ',' || c == ' ' || c == '\t')
                    pos++;
                else if (c == '\r' || c == '\n')
                {
                    pos++;
                    break;
                }
                else
                    break;
            }
            // Consume trailing newline
            if (pos < code.Length && code[pos] == '\n') pos++;
            return pos;
        }

        // ── Expression color extraction ──────────────────────────────────────

        /// <summary>
        /// Scans the source code around a sprite's definition for all Color literals.
        /// Looks backwards up to 600 chars from SourceStart (to catch variable declarations
        /// like <c>var tColor = flash ? new Color(0,200,255) : new Color(0,150,210);</c>)
        /// and within the sprite definition itself.
        /// Returns an empty list if no literals are found.
        /// </summary>
        public static List<ExpressionColor> ExtractColorLiterals(string sourceCode, int sourceStart, int sourceEnd)
        {
            var results = new List<ExpressionColor>();
            if (string.IsNullOrEmpty(sourceCode) || sourceStart < 0 || sourceEnd < 0) return results;

            // Scan window: up to 600 chars before the sprite definition to capture
            // variable declarations with Color expressions, plus the definition itself.
            int scanStart = Math.Max(0, sourceStart - 600);
            int scanEnd = Math.Min(sourceCode.Length, sourceEnd);
            if (scanEnd <= scanStart) return results;

            string window = sourceCode.Substring(scanStart, scanEnd - scanStart);

            // Match: new Color(R, G, B) and new Color(R, G, B, A) where R/G/B/A are integer literals
            var colorCtorPattern = new Regex(
                @"new\s+Color\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)(?:\s*,\s*(\d+))?\s*\)");

            foreach (Match m in colorCtorPattern.Matches(window))
            {
                int r = int.Parse(m.Groups[1].Value);
                int g = int.Parse(m.Groups[2].Value);
                int b = int.Parse(m.Groups[3].Value);
                int a = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 255;

                results.Add(new ExpressionColor
                {
                    R = r, G = g, B = b, A = a,
                    SourceOffset = scanStart + m.Index,
                    SourceLength = m.Length,
                    LiteralText = m.Value,
                });
            }

            // Match named colors: Color.White, Color.Black, Color.Red, etc.
            var namedColorPattern = new Regex(@"Color\.(White|Black|Red|Green|Blue|Yellow|Transparent|Cyan|Magenta|Orange|Gray|LightGray|DarkGray)(?!\w)");
            foreach (Match m in namedColorPattern.Matches(window))
            {
                // Skip if this overlaps with a "new Color(" match (unlikely but safe)
                int absOffset = scanStart + m.Index;
                bool overlaps = false;
                foreach (var ec in results)
                    if (absOffset >= ec.SourceOffset && absOffset < ec.SourceOffset + ec.SourceLength)
                    { overlaps = true; break; }
                if (overlaps) continue;

                var rgba = ResolveNamedColor(m.Groups[1].Value);
                if (rgba == null) continue;

                results.Add(new ExpressionColor
                {
                    R = rgba[0], G = rgba[1], B = rgba[2], A = rgba[3],
                    SourceOffset = absOffset,
                    SourceLength = m.Length,
                    LiteralText = m.Value,
                });
            }

            // Sort by source offset for consistent display order
            results.Sort((a, b) => a.SourceOffset.CompareTo(b.SourceOffset));
            return results;
        }

        /// <summary>Resolves a named color string to RGBA values.</summary>
        private static int[] ResolveNamedColor(string name)
        {
            switch (name)
            {
                case "White":       return new[] { 255, 255, 255, 255 };
                case "Black":       return new[] { 0, 0, 0, 255 };
                case "Red":         return new[] { 255, 0, 0, 255 };
                case "Green":       return new[] { 0, 128, 0, 255 };
                case "Blue":        return new[] { 0, 0, 255, 255 };
                case "Yellow":      return new[] { 255, 255, 0, 255 };
                case "Transparent": return new[] { 0, 0, 0, 0 };
                case "Cyan":        return new[] { 0, 255, 255, 255 };
                case "Magenta":     return new[] { 255, 0, 255, 255 };
                case "Orange":      return new[] { 255, 165, 0, 255 };
                case "Gray":        return new[] { 128, 128, 128, 255 };
                case "LightGray":   return new[] { 211, 211, 211, 255 };
                case "DarkGray":    return new[] { 169, 169, 169, 255 };
                default: return null;
            }
        }

        /// <summary>
        /// Patches a specific Color literal in OriginalSourceCode at a known offset.
        /// Returns the updated source code, or null if the offset is out of range.
        /// </summary>
        public static string PatchColorAtOffset(string sourceCode, ExpressionColor expr, int newR, int newG, int newB, int newA)
        {
            if (sourceCode == null || expr == null) return null;
            if (expr.SourceOffset < 0 || expr.SourceOffset + expr.SourceLength > sourceCode.Length) return null;

            // Verify the literal text still matches at the expected offset
            string actual = sourceCode.Substring(expr.SourceOffset, expr.SourceLength);
            if (actual != expr.LiteralText) return null;

            string replacement = newA != 255
                ? $"new Color({newR}, {newG}, {newB}, {newA})"
                : $"new Color({newR}, {newG}, {newB})";

            return sourceCode.Substring(0, expr.SourceOffset)
                 + replacement
                 + sourceCode.Substring(expr.SourceOffset + expr.SourceLength);
        }

        // ── Expression Vector2 extraction ────────────────────────────────────

        /// <summary>
        /// Scans the source code around a sprite's definition for all <c>new Vector2(X, Y)</c> literals.
        /// Looks backwards up to 600 chars from SourceStart and within the definition itself.
        /// </summary>
        public static List<ExpressionVector2> ExtractVector2Literals(string sourceCode, int sourceStart, int sourceEnd)
        {
            var results = new List<ExpressionVector2>();
            if (string.IsNullOrEmpty(sourceCode) || sourceStart < 0 || sourceEnd < 0) return results;

            int scanStart = Math.Max(0, sourceStart - 600);
            int scanEnd = Math.Min(sourceCode.Length, sourceEnd);
            if (scanEnd <= scanStart) return results;

            string window = sourceCode.Substring(scanStart, scanEnd - scanStart);

            // Match: new Vector2(X, Y) where X and Y are integer or float literals (with optional f suffix)
            var pattern = new Regex(
                @"new\s+Vector2\s*\(\s*(-?[\d.]+)f?\s*,\s*(-?[\d.]+)f?\s*\)");

            // Also try to detect property context from "PropertyName = new Vector2(...)"
            var ctxPattern = new Regex(
                @"(\w+)\s*=\s*new\s+Vector2\s*\(");

            foreach (Match m in pattern.Matches(window))
            {
                if (!float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float x)) continue;
                if (!float.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float y)) continue;

                // Look for property context
                string propCtx = null;
                var ctxMatch = ctxPattern.Match(window, Math.Max(0, m.Index - 80), Math.Min(80, m.Index));
                foreach (Match cm in ctxPattern.Matches(window))
                {
                    // The "new Vector2(" in the context match should align with this match
                    if (cm.Index + cm.Length >= m.Index && cm.Index <= m.Index)
                    {
                        propCtx = cm.Groups[1].Value;
                        break;
                    }
                }

                results.Add(new ExpressionVector2
                {
                    X = x,
                    Y = y,
                    PropertyContext = propCtx,
                    SourceOffset = scanStart + m.Index,
                    SourceLength = m.Length,
                    LiteralText = m.Value,
                });
            }

            results.Sort((a, b) => a.SourceOffset.CompareTo(b.SourceOffset));
            return results;
        }

        // ── Expression float extraction ──────────────────────────────────────

        /// <summary>
        /// Scans the source code around a sprite's definition for float literal assignments
        /// (e.g. <c>RotationOrScale = 0.8f</c>).  Only matches floats that appear as
        /// property/variable assignments to reduce false positives.
        /// </summary>
        public static List<ExpressionFloat> ExtractFloatLiterals(string sourceCode, int sourceStart, int sourceEnd)
        {
            var results = new List<ExpressionFloat>();
            if (string.IsNullOrEmpty(sourceCode) || sourceStart < 0 || sourceEnd < 0) return results;

            int scanStart = Math.Max(0, sourceStart - 600);
            int scanEnd = Math.Min(sourceCode.Length, sourceEnd);
            if (scanEnd <= scanStart) return results;

            string window = sourceCode.Substring(scanStart, scanEnd - scanStart);

            // Match: PropertyName = <float>f  (requires trailing 'f' and assignment context)
            var pattern = new Regex(
                @"(\w+)\s*=\s*(-?[\d.]+)f\s*[,;\r\n]");

            foreach (Match m in pattern.Matches(window))
            {
                string propName = m.Groups[1].Value;

                // Skip assignments that are part of Vector2/Color constructors by checking
                // if this is inside parens — we only want standalone assignments
                // Simple heuristic: skip if propName is a type keyword
                if (propName == "new" || propName == "var" || propName == "int" ||
                    propName == "float" || propName == "double") continue;

                if (!float.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float val)) continue;

                // The literal is just the number+f portion
                string literalText = m.Groups[2].Value + "f";
                int literalOffset = scanStart + m.Index + m.Value.IndexOf(m.Groups[2].Value);

                results.Add(new ExpressionFloat
                {
                    Value = val,
                    PropertyContext = propName,
                    SourceOffset = literalOffset,
                    SourceLength = literalText.Length,
                    LiteralText = literalText,
                });
            }

            results.Sort((a, b) => a.SourceOffset.CompareTo(b.SourceOffset));
            return results;
        }

        // ── Expression string extraction ─────────────────────────────────────

        /// <summary>
        /// Scans the source code around a sprite's definition for quoted string literals
        /// in property assignments (e.g. <c>Data = "SquareSimple"</c>, <c>FontId = "White"</c>).
        /// </summary>
        public static List<ExpressionString> ExtractStringLiterals(string sourceCode, int sourceStart, int sourceEnd)
        {
            var results = new List<ExpressionString>();
            if (string.IsNullOrEmpty(sourceCode) || sourceStart < 0 || sourceEnd < 0) return results;

            int scanStart = Math.Max(0, sourceStart - 600);
            int scanEnd = Math.Min(sourceCode.Length, sourceEnd);
            if (scanEnd <= scanStart) return results;

            string window = sourceCode.Substring(scanStart, scanEnd - scanStart);

            // Match: PropertyName = "value"  (standard quoted string literal in assignment)
            var pattern = new Regex(
                @"(\w+)\s*=\s*""((?:[^""\\]|\\.)*)""");

            foreach (Match m in pattern.Matches(window))
            {
                string propName = m.Groups[1].Value;
                string rawValue = m.Groups[2].Value;

                // Unescape the string value (basic C# unescaping)
                string value = rawValue
                    .Replace("\\\\", "\x01")    // placeholder for backslash
                    .Replace("\\\"", "\"")
                    .Replace("\\n", "\n")
                    .Replace("\\r", "\r")
                    .Replace("\\t", "\t")
                    .Replace("\x01", "\\");

                // Also handle \uXXXX escapes
                value = Regex.Replace(value, @"\\u([0-9A-Fa-f]{4})",
                    em => ((char)Convert.ToInt32(em.Groups[1].Value, 16)).ToString());

                // The literal includes the quotes
                string literalText = "\"" + rawValue + "\"";
                int literalOffset = scanStart + m.Index + m.Value.IndexOf('"');

                results.Add(new ExpressionString
                {
                    Value = value,
                    PropertyContext = propName,
                    SourceOffset = literalOffset,
                    SourceLength = literalText.Length,
                    LiteralText = literalText,
                });
            }

            results.Sort((a, b) => a.SourceOffset.CompareTo(b.SourceOffset));
            return results;
        }

        // ── Offset-targeted patchers ─────────────────────────────────────────

        /// <summary>
        /// Patches a specific Vector2 literal in OriginalSourceCode at a known offset.
        /// Returns the updated source code, or null if the offset is out of range or text doesn't match.
        /// </summary>
        public static string PatchVector2AtOffset(string sourceCode, ExpressionVector2 expr, float newX, float newY)
        {
            if (sourceCode == null || expr == null) return null;
            if (expr.SourceOffset < 0 || expr.SourceOffset + expr.SourceLength > sourceCode.Length) return null;

            string actual = sourceCode.Substring(expr.SourceOffset, expr.SourceLength);
            if (actual != expr.LiteralText) return null;

            string replacement = $"new Vector2({newX:F1}f, {newY:F1}f)";

            return sourceCode.Substring(0, expr.SourceOffset)
                 + replacement
                 + sourceCode.Substring(expr.SourceOffset + expr.SourceLength);
        }

        /// <summary>
        /// Patches a specific float literal in OriginalSourceCode at a known offset.
        /// Returns the updated source code, or null if the offset is out of range or text doesn't match.
        /// </summary>
        public static string PatchFloatAtOffset(string sourceCode, ExpressionFloat expr, float newValue, int decimals = 4)
        {
            if (sourceCode == null || expr == null) return null;
            if (expr.SourceOffset < 0 || expr.SourceOffset + expr.SourceLength > sourceCode.Length) return null;

            string actual = sourceCode.Substring(expr.SourceOffset, expr.SourceLength);
            if (actual != expr.LiteralText) return null;

            string replacement = newValue.ToString($"F{decimals}") + "f";

            return sourceCode.Substring(0, expr.SourceOffset)
                 + replacement
                 + sourceCode.Substring(expr.SourceOffset + expr.SourceLength);
        }

        /// <summary>
        /// Patches a specific string literal in OriginalSourceCode at a known offset.
        /// Returns the updated source code, or null if the offset is out of range or text doesn't match.
        /// </summary>
        public static string PatchStringAtOffset(string sourceCode, ExpressionString expr, string newValue)
        {
            if (sourceCode == null || expr == null || newValue == null) return null;
            if (expr.SourceOffset < 0 || expr.SourceOffset + expr.SourceLength > sourceCode.Length) return null;

            string actual = sourceCode.Substring(expr.SourceOffset, expr.SourceLength);
            if (actual != expr.LiteralText) return null;

            string replacement = $"\"{Esc(newValue)}\"";

            return sourceCode.Substring(0, expr.SourceOffset)
                 + replacement
                 + sourceCode.Substring(expr.SourceOffset + expr.SourceLength);
        }

        // ── Unified offset management ────────────────────────────────────────

        /// <summary>
        /// Shifts source offsets for all expression literals across all sprites after a patch
        /// at <paramref name="patchedOffset"/> that changed source length by <paramref name="delta"/>.
        /// Also adjusts SourceStart/SourceEnd for sprites after the patch point.
        /// </summary>
        /// <param name="sprites">All sprites in the layout.</param>
        /// <param name="patchedOffset">Character offset where the patch was applied.</param>
        /// <param name="delta">Change in source length (newLength - oldLength). Can be negative.</param>
        /// <param name="excludeLiteral">The literal that was patched (skip it during shifting).</param>
        public static void ShiftExpressionOffsets(
            IList<SpriteEntry> sprites, int patchedOffset, int delta, ExpressionLiteral excludeLiteral = null)
        {
            if (delta == 0 || sprites == null) return;

            foreach (var sprite in sprites)
            {
                // Shift color expression offsets
                if (sprite.ExpressionColors != null)
                {
                    foreach (var ec in sprite.ExpressionColors)
                    {
                        if (ec.SourceOffset > patchedOffset)
                            ec.SourceOffset += delta;
                    }
                }

                // Shift Vector2 expression offsets
                if (sprite.ExpressionVectors != null)
                {
                    foreach (var ev in sprite.ExpressionVectors)
                    {
                        if (ReferenceEquals(ev, excludeLiteral)) continue;
                        if (ev.SourceOffset > patchedOffset)
                            ev.SourceOffset += delta;
                    }
                }

                // Shift float expression offsets
                if (sprite.ExpressionFloats != null)
                {
                    foreach (var ef in sprite.ExpressionFloats)
                    {
                        if (ReferenceEquals(ef, excludeLiteral)) continue;
                        if (ef.SourceOffset > patchedOffset)
                            ef.SourceOffset += delta;
                    }
                }

                // Shift string expression offsets
                if (sprite.ExpressionStrings != null)
                {
                    foreach (var es in sprite.ExpressionStrings)
                    {
                        if (ReferenceEquals(es, excludeLiteral)) continue;
                        if (es.SourceOffset > patchedOffset)
                            es.SourceOffset += delta;
                    }
                }

                // Shift SourceStart/SourceEnd for sprites after the patch point
                if (sprite.SourceStart > patchedOffset)
                {
                    sprite.SourceStart += delta;
                    sprite.SourceEnd += delta;
                }
            }
        }

        private static int FindMatchingBrace(string code, int openPos)
        {
            int depth = 1;
            for (int i = openPos + 1; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static int FindMatchingParen(string code, int openPos)
        {
            int depth = 1;
            for (int i = openPos + 1; i < code.Length; i++)
            {
                if (code[i] == '(') depth++;
                else if (code[i] == ')') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }
    }
}
