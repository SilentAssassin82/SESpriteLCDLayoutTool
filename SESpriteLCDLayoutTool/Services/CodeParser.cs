using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using SESpriteLCDLayoutTool.Models;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Parses C# SE LCD script code containing MySprite object initializers
    /// back into <see cref="SpriteEntry"/> objects so an existing layout can be
    /// imported into the visual editor.
    /// </summary>
    public static class CodeParser
    {
        /// <summary>
        /// Extracts a <c>// @SnapshotTag: {value}</c> comment from snapshot text.
        /// Returns null if no tag is found.
        /// </summary>
        public static string ParseSnapshotTag(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            var match = Regex.Match(code, @"//\s*@SnapshotTag:\s*(.+)");
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }

        /// <summary>
        /// Parses all MySprite blocks found in the supplied C# source code.
        /// Supports both object-initializer syntax (new MySprite { … }) and any
        /// reasonable formatting/whitespace.
        /// </summary>
        public static List<SpriteEntry> Parse(string code)
        {
            var results = new List<SpriteEntry>();
            if (string.IsNullOrWhiteSpace(code)) return results;

            // Track which character ranges have already been consumed to avoid duplicates.
            var consumed = new List<int[]>(); // [start, end] pairs

            // ── Pass 1: Object-initializer syntax ──
            // new MySprite { Property = value, ... }
            int searchFrom = 0;
            while (searchFrom < code.Length)
            {
                int idx = code.IndexOf("new MySprite", searchFrom, StringComparison.Ordinal);
                if (idx < 0) break;

                // Skip if this is a longer identifier (e.g. MySprite2)
                int afterName = idx + 12;
                if (afterName < code.Length && char.IsLetterOrDigit(code[afterName]))
                {
                    searchFrom = afterName;
                    continue;
                }

                // Look for '{' as the next non-whitespace significant char
                int braceStart = FindNextNonWhitespace(code, afterName);
                if (braceStart >= 0 && code[braceStart] == '{')
                {
                    int braceEnd = FindMatchingBrace(code, braceStart);
                    if (braceEnd >= 0)
                    {
                        string body = code.Substring(braceStart + 1, braceEnd - braceStart - 1);
                            var sprite = ParseInitializerBody(body);
                            if (sprite != null)
                            {
                                sprite.SourceStart = idx;
                                sprite.SourceEnd = braceEnd + 1;
                                results.Add(sprite);
                                consumed.Add(new[] { idx, braceEnd });
                            }
                        searchFrom = braceEnd + 1;
                        continue;
                    }
                }

                searchFrom = afterName;
            }

            // ── Pass 2: Constructor syntax ──
            // new MySprite(SpriteType.TEXTURE, "data", pos, size, color, fontId, alignment, rotOrScale)
            var ctorPattern = new Regex(
                @"new\s+MySprite\s*\(", RegexOptions.Compiled);
            foreach (Match m in ctorPattern.Matches(code))
            {
                if (IsConsumed(consumed, m.Index)) continue;

                int parenStart = m.Index + m.Length - 1; // the '('
                int parenEnd = FindMatchingParen(code, parenStart);
                if (parenEnd < 0) continue;

                string args = code.Substring(parenStart + 1, parenEnd - parenStart - 1);
                var sprite = ParseConstructorArgs(args);
                if (sprite != null)
                {
                    int trailEnd = ApplyTrailingAssignments(code, m.Index, parenEnd + 1, sprite);
                    sprite.SourceStart = m.Index;
                    sprite.SourceEnd = trailEnd > parenEnd + 1 ? trailEnd : parenEnd + 1;
                    results.Add(sprite);
                    consumed.Add(new[] { m.Index, sprite.SourceEnd - 1 });
                }
            }

            // ── Pass 3: MySprite.CreateText() factory method ──
            // MySprite.CreateText("text", "fontId", color, scale, alignment)
            var createTextPattern = new Regex(
                @"MySprite\s*\.\s*CreateText\s*\(", RegexOptions.Compiled);
            foreach (Match m in createTextPattern.Matches(code))
            {
                if (IsConsumed(consumed, m.Index)) continue;

                int parenStart = m.Index + m.Length - 1;
                int parenEnd = FindMatchingParen(code, parenStart);
                if (parenEnd < 0) continue;

                string args = code.Substring(parenStart + 1, parenEnd - parenStart - 1);
                var sprite = ParseCreateTextArgs(args);
                if (sprite != null)
                {
                    // Check for subsequent .Position assignment on the same variable
                    int trailEnd = ApplyTrailingAssignments(code, m.Index, parenEnd + 1, sprite);
                    sprite.SourceStart = m.Index;
                    sprite.SourceEnd = trailEnd > parenEnd + 1 ? trailEnd : parenEnd + 1;
                    results.Add(sprite);
                    consumed.Add(new[] { m.Index, sprite.SourceEnd - 1 });
                }
            }

            // ── Pass 4: MySprite.CreateSprite() factory method ──
            // MySprite.CreateSprite("spriteName", position, size)
            var createSpritePattern = new Regex(
                @"MySprite\s*\.\s*CreateSprite\s*\(", RegexOptions.Compiled);
            foreach (Match m in createSpritePattern.Matches(code))
            {
                if (IsConsumed(consumed, m.Index)) continue;

                int parenStart = m.Index + m.Length - 1;
                int parenEnd = FindMatchingParen(code, parenStart);
                if (parenEnd < 0) continue;

                string args = code.Substring(parenStart + 1, parenEnd - parenStart - 1);
                var sprite = ParseCreateSpriteArgs(args);
                if (sprite != null)
                {
                    int trailEnd = ApplyTrailingAssignments(code, m.Index, parenEnd + 1, sprite);
                    sprite.SourceStart = m.Index;
                    sprite.SourceEnd = trailEnd > parenEnd + 1 ? trailEnd : parenEnd + 1;
                    results.Add(sprite);
                    consumed.Add(new[] { m.Index, sprite.SourceEnd - 1 });
                }
            }

            // ── Pass 5: Statement-by-statement property assignment ──
            // var sprite = new MySprite();  sprite.Data = "...";  sprite.Type = ...;
            results.AddRange(ParseStatementAssignments(code, consumed));

            return results;
        }

        private static int FindNextNonWhitespace(string code, int start)
        {
            for (int i = start; i < code.Length; i++)
            {
                if (!char.IsWhiteSpace(code[i])) return i;
            }
            return -1;
        }

        private static bool IsConsumed(List<int[]> consumed, int index)
        {
            foreach (var range in consumed)
                if (index >= range[0] && index <= range[1]) return true;
            return false;
        }

        private static int FindMatchingBrace(string code, int openPos)
        {
            int depth = 0;
            bool inString = false;
            bool inChar = false;
            for (int i = openPos; i < code.Length; i++)
            {
                char c = code[i];
                if (inString)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (inChar)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '\'') inChar = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '\'') { inChar = true; continue; }
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static int FindMatchingParen(string code, int openPos)
        {
            int depth = 0;
            bool inString = false;
            bool inChar = false;
            for (int i = openPos; i < code.Length; i++)
            {
                char c = code[i];
                if (inString)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (inChar)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '\'') inChar = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '\'') { inChar = true; continue; }
                if (c == '(') depth++;
                else if (c == ')') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        /// <summary>
        /// Splits constructor arguments at top-level commas (respecting nested parens and strings).
        /// </summary>
        private static List<string> SplitArgs(string argsText)
        {
            var args = new List<string>();
            int depth = 0;
            bool inString = false;
            bool inChar = false;
            int start = 0;
            for (int i = 0; i < argsText.Length; i++)
            {
                char c = argsText[i];
                if (inString)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (inChar)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '\'') inChar = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '\'') { inChar = true; continue; }
                if (c == '(' || c == '{') depth++;
                else if (c == ')' || c == '}') depth--;
                else if (c == ',' && depth == 0)
                {
                    args.Add(argsText.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            if (start < argsText.Length)
                args.Add(argsText.Substring(start).Trim());
            return args;
        }

        /// <summary>
        /// Parses MySprite constructor arguments (positional).
        /// Order: type, data, position?, size?, color?, fontId?, alignment?, rotationOrScale?
        /// </summary>
        private static SpriteEntry ParseConstructorArgs(string argsText)
        {
            var args = SplitArgs(argsText);
            if (args.Count < 2) return null; // must have at least type + data

            var sprite = new SpriteEntry();

            // arg 0: SpriteType
            string typeArg = args[0];
            if (typeArg.IndexOf("TEXT", StringComparison.OrdinalIgnoreCase) >= 0 &&
                typeArg.IndexOf("TEXTURE", StringComparison.OrdinalIgnoreCase) < 0)
                sprite.Type = SpriteEntryType.Text;
            else
                sprite.Type = SpriteEntryType.Texture;

            // arg 1: Data (string)
            string dataArg = args[1];
            var strMatch = Regex.Match(dataArg, @"""((?:[^""\\]|\\.)*)""");
            if (strMatch.Success)
            {
                string val = UnescapeCSharpString(strMatch.Groups[1].Value);
                if (sprite.Type == SpriteEntryType.Text)
                    sprite.Text = val;
                else
                    sprite.SpriteName = val;
            }

            // arg 2: Position (Vector2?) — optional
            if (args.Count > 2 && !IsNullArg(args[2]))
            {
                ParseVector2(args[2], out float px, out float py);
                sprite.X = px;
                sprite.Y = py;
            }

            // arg 3: Size (Vector2?) — optional
            if (args.Count > 3 && !IsNullArg(args[3]))
            {
                ParseVector2(args[3], out float sw, out float sh);
                sprite.Width = sw;
                sprite.Height = sh;
            }

            // arg 4: Color? — optional
            if (args.Count > 4 && !IsNullArg(args[4]))
            {
                ParseColor(args[4], out int cr, out int cg, out int cb, out int ca);
                sprite.ColorR = cr;
                sprite.ColorG = cg;
                sprite.ColorB = cb;
                sprite.ColorA = ca;
            }

            // arg 5: FontId (string) — optional
            if (args.Count > 5 && !IsNullArg(args[5]))
            {
                var fontMatch = Regex.Match(args[5], @"""((?:[^""\\]|\\.)*)""");
                if (fontMatch.Success)
                    sprite.FontId = UnescapeCSharpString(fontMatch.Groups[1].Value);
            }

            // arg 6: Alignment — optional
            if (args.Count > 6 && !IsNullArg(args[6]))
            {
                string al = args[6];
                if (al.IndexOf("LEFT", StringComparison.OrdinalIgnoreCase) >= 0)
                    sprite.Alignment = SpriteTextAlignment.Left;
                else if (al.IndexOf("RIGHT", StringComparison.OrdinalIgnoreCase) >= 0)
                    sprite.Alignment = SpriteTextAlignment.Right;
                else
                    sprite.Alignment = SpriteTextAlignment.Center;
            }
            else if (sprite.Type == SpriteEntryType.Text)
            {
                sprite.Alignment = SpriteTextAlignment.Left;
            }

            // arg 7: RotationOrScale — optional
            if (args.Count > 7 && !IsNullArg(args[7]))
            {
                float val = ParseFloat(args[7]);
                if (sprite.Type == SpriteEntryType.Text)
                    sprite.Scale = val;
                else
                    sprite.Rotation = val;
            }

            return sprite;
        }

        private static bool IsNullArg(string arg)
        {
            string t = arg.Trim();
            return t == "null" || t == "default" || t.Length == 0;
        }

        /// <summary>
        /// Parses MySprite.CreateText(text, fontId, color, scale, alignment).
        /// </summary>
        private static SpriteEntry ParseCreateTextArgs(string argsText)
        {
            var args = SplitArgs(argsText);
            if (args.Count < 1) return null;

            var sprite = new SpriteEntry { Type = SpriteEntryType.Text };

            // arg 0: text (string)
            var strMatch = Regex.Match(args[0], @"""((?:[^""\\]|\\.)*)""");
            if (strMatch.Success)
                sprite.Text = UnescapeCSharpString(strMatch.Groups[1].Value);

            // arg 1: fontId (string) — optional
            if (args.Count > 1 && !IsNullArg(args[1]))
            {
                var fontMatch = Regex.Match(args[1], @"""((?:[^""\\]|\\.)*)""");
                if (fontMatch.Success)
                    sprite.FontId = UnescapeCSharpString(fontMatch.Groups[1].Value);
            }

            // arg 2: color — optional
            if (args.Count > 2 && !IsNullArg(args[2]))
            {
                ParseColor(args[2], out int cr, out int cg, out int cb, out int ca);
                sprite.ColorR = cr;
                sprite.ColorG = cg;
                sprite.ColorB = cb;
                sprite.ColorA = ca;
            }

            // arg 3: scale — optional
            if (args.Count > 3 && !IsNullArg(args[3]))
                sprite.Scale = ParseFloat(args[3]);

            // arg 4: alignment — optional
            if (args.Count > 4 && !IsNullArg(args[4]))
            {
                string al = args[4];
                if (al.IndexOf("LEFT", StringComparison.OrdinalIgnoreCase) >= 0)
                    sprite.Alignment = SpriteTextAlignment.Left;
                else if (al.IndexOf("RIGHT", StringComparison.OrdinalIgnoreCase) >= 0)
                    sprite.Alignment = SpriteTextAlignment.Right;
                else
                    sprite.Alignment = SpriteTextAlignment.Center;
            }
            else
            {
                sprite.Alignment = SpriteTextAlignment.Left;
            }

            return sprite;
        }

        /// <summary>
        /// Parses MySprite.CreateSprite(spriteName, position, size).
        /// </summary>
        private static SpriteEntry ParseCreateSpriteArgs(string argsText)
        {
            var args = SplitArgs(argsText);
            if (args.Count < 1) return null;

            var sprite = new SpriteEntry { Type = SpriteEntryType.Texture };

            // arg 0: sprite name (string)
            var strMatch = Regex.Match(args[0], @"""((?:[^""\\]|\\.)*)""");
            if (strMatch.Success)
                sprite.SpriteName = UnescapeCSharpString(strMatch.Groups[1].Value);

            // arg 1: position (Vector2) — optional
            if (args.Count > 1 && !IsNullArg(args[1]))
            {
                ParseVector2(args[1], out float px, out float py);
                sprite.X = px;
                sprite.Y = py;
            }

            // arg 2: size (Vector2) — optional
            if (args.Count > 2 && !IsNullArg(args[2]))
            {
                ParseVector2(args[2], out float sw, out float sh);
                sprite.Width = sw;
                sprite.Height = sh;
            }

            return sprite;
        }

        /// <summary>
        /// After parsing a CreateText/CreateSprite call, looks for trailing
        /// property assignments on the same variable, e.g.:
        ///   var s = MySprite.CreateText(...);
        ///   s.Position = new Vector2(100, 200);
        ///   s.Color = new Color(255, 0, 0);
        /// </summary>
        /// <summary>
        /// Returns the absolute end position (in <paramref name="code"/>) of the
        /// last matched trailing assignment, or -1 when no trailing assignments
        /// were found.  Callers can use this to extend <c>SourceEnd</c>.
        /// </summary>
        private static int ApplyTrailingAssignments(string code, int exprStart, int exprEnd, SpriteEntry sprite)
        {
            // Walk backwards from the expression to find the variable name:
            // "var s = MySprite.CreateText(...)" or "s = MySprite.CreateText(...)"
            string before = code.Substring(0, exprStart);
            var varMatch = Regex.Match(before, @"(?:var\s+)?(\w+)\s*=\s*$");
            if (!varMatch.Success) return -1;

            string varName = varMatch.Groups[1].Value;

            // Search forward from the expression end for assignments to this variable
            string after = code.Substring(exprEnd);
            var propPattern = new Regex(
                @"(?<!\w)" + Regex.Escape(varName) + @"\s*\.\s*(\w+)\s*=\s*(.+?)\s*;");

            int lastAbsEnd = -1;
            foreach (Match pm in propPattern.Matches(after))
            {
                string prop = pm.Groups[1].Value;
                string val = pm.Groups[2].Value.Trim();

                switch (prop)
                {
                    case "Position":
                        ParseVector2(val, out float px, out float py);
                        sprite.X = px;
                        sprite.Y = py;
                        break;
                    case "Size":
                        ParseVector2(val, out float sw, out float sh);
                        sprite.Width = sw;
                        sprite.Height = sh;
                        break;
                    case "Color":
                        ParseColor(val, out int cr, out int cg, out int cb, out int ca);
                        sprite.ColorR = cr;
                        sprite.ColorG = cg;
                        sprite.ColorB = cb;
                        sprite.ColorA = ca;
                        break;
                    case "RotationOrScale":
                        float rv = ParseFloat(val);
                        if (sprite.Type == SpriteEntryType.Text)
                            sprite.Scale = rv;
                        else
                            sprite.Rotation = rv;
                        break;
                    case "Alignment":
                        if (val.IndexOf("LEFT", StringComparison.OrdinalIgnoreCase) >= 0)
                            sprite.Alignment = SpriteTextAlignment.Left;
                        else if (val.IndexOf("RIGHT", StringComparison.OrdinalIgnoreCase) >= 0)
                            sprite.Alignment = SpriteTextAlignment.Right;
                        else
                            sprite.Alignment = SpriteTextAlignment.Center;
                        break;
                    case "FontId":
                        var fM = Regex.Match(val, @"""((?:[^""\\]|\\.)*)""");
                        if (fM.Success)
                            sprite.FontId = UnescapeCSharpString(fM.Groups[1].Value);
                        break;
                    case "Data":
                        var dM = Regex.Match(val, @"""((?:[^""\\]|\\.)*)""");
                        if (dM.Success)
                        {
                            string s = UnescapeCSharpString(dM.Groups[1].Value);
                            if (sprite.Type == SpriteEntryType.Text)
                                sprite.Text = s;
                            else
                                sprite.SpriteName = s;
                        }
                        break;
                    case "Type":
                        if (val.IndexOf("TEXT", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            val.IndexOf("TEXTURE", StringComparison.OrdinalIgnoreCase) < 0)
                            sprite.Type = SpriteEntryType.Text;
                        else
                            sprite.Type = SpriteEntryType.Texture;
                        break;
                    default:
                        continue; // unknown property — don't extend range
                }
                int absEnd = exprEnd + pm.Index + pm.Length;
                if (absEnd > lastAbsEnd) lastAbsEnd = absEnd;
            }
            return lastAbsEnd;
        }

        /// <summary>
        /// Finds statement-by-statement property assignments:
        ///   var sprite = new MySprite();
        ///   sprite.Type = SpriteType.TEXTURE;
        ///   sprite.Data = "Crosshair";
        /// Groups assignments by variable name and builds SpriteEntry objects.
        /// </summary>
        private static List<SpriteEntry> ParseStatementAssignments(string code, List<int[]> consumed)
        {
            var results = new List<SpriteEntry>();

            // Find:  varName = new MySprite();  or  var varName = new MySprite();
            // Also:  MySprite varName = new MySprite();
            var declPattern = new Regex(
                @"(?:var|MySprite)\s+(\w+)\s*=\s*new\s+MySprite\s*\(\s*\)\s*;",
                RegexOptions.Compiled);

            foreach (Match dm in declPattern.Matches(code))
            {
                if (IsConsumed(consumed, dm.Index)) continue;

                string varName = dm.Groups[1].Value;
                var sprite = new SpriteEntry();
                bool found = false;
                bool alignmentSet = false;

                // Look for: varName.Property = value;
                var propPattern = new Regex(
                    @"(?<!\w)" + Regex.Escape(varName) + @"\s*\.\s*(\w+)\s*=\s*(.+?)\s*;",
                    RegexOptions.Compiled);

                foreach (Match pm in propPattern.Matches(code))
                {
                    string prop = pm.Groups[1].Value;
                    string val = pm.Groups[2].Value.Trim();

                    switch (prop)
                    {
                        case "Type":
                            found = true;
                            if (val.IndexOf("TEXT", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                val.IndexOf("TEXTURE", StringComparison.OrdinalIgnoreCase) < 0)
                                sprite.Type = SpriteEntryType.Text;
                            else
                                sprite.Type = SpriteEntryType.Texture;
                            break;

                        case "Data":
                            found = true;
                            var strM = Regex.Match(val, @"""((?:[^""\\]|\\.)*)""");
                            if (strM.Success)
                            {
                                string s = UnescapeCSharpString(strM.Groups[1].Value);
                                if (sprite.Type == SpriteEntryType.Text)
                                    sprite.Text = s;
                                else
                                    sprite.SpriteName = s;
                            }
                            break;

                        case "Position":
                            found = true;
                            ParseVector2(val, out float px, out float py);
                            sprite.X = px;
                            sprite.Y = py;
                            break;

                        case "Size":
                            found = true;
                            ParseVector2(val, out float sw, out float sh);
                            sprite.Width = sw;
                            sprite.Height = sh;
                            break;

                        case "Color":
                            found = true;
                            ParseColor(val, out int cr, out int cg, out int cb, out int ca);
                            sprite.ColorR = cr;
                            sprite.ColorG = cg;
                            sprite.ColorB = cb;
                            sprite.ColorA = ca;
                            break;

                        case "FontId":
                            found = true;
                            var fM = Regex.Match(val, @"""((?:[^""\\]|\\.)*)""");
                            if (fM.Success)
                                sprite.FontId = UnescapeCSharpString(fM.Groups[1].Value);
                            break;

                        case "Alignment":
                            found = true;
                            alignmentSet = true;
                            if (val.IndexOf("LEFT", StringComparison.OrdinalIgnoreCase) >= 0)
                                sprite.Alignment = SpriteTextAlignment.Left;
                            else if (val.IndexOf("RIGHT", StringComparison.OrdinalIgnoreCase) >= 0)
                                sprite.Alignment = SpriteTextAlignment.Right;
                            else
                                sprite.Alignment = SpriteTextAlignment.Center;
                            break;

                        case "RotationOrScale":
                            found = true;
                            float rv = ParseFloat(val);
                            if (sprite.Type == SpriteEntryType.Text)
                                sprite.Scale = rv;
                            else
                                sprite.Rotation = rv;
                            break;
                    }
                }

                // Fix up: if Data was parsed before Type, re-assign correctly
                if (found)
                {
                    if (sprite.Type == SpriteEntryType.Text && sprite.SpriteName != "SquareSimple")
                    {
                        sprite.Text = sprite.SpriteName;
                        sprite.SpriteName = "SquareSimple";
                    }
                    // SE default for text sprites is LEFT
                    if (sprite.Type == SpriteEntryType.Text && !alignmentSet)
                        sprite.Alignment = SpriteTextAlignment.Left;
                    results.Add(sprite);
                }
            }

            return results;
        }

        private static SpriteEntry ParseInitializerBody(string body)
        {
            var sprite = new SpriteEntry();
            bool foundAnyProperty = false;

            // Extract property assignments: PropertyName = value,
            // We parse known properties individually.

            string type = ExtractValue(body, "Type");
            if (type != null)
            {
                foundAnyProperty = true;
                if (type.IndexOf("TEXT", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    type.IndexOf("TEXTURE", StringComparison.OrdinalIgnoreCase) < 0)
                    sprite.Type = SpriteEntryType.Text;
                else
                    sprite.Type = SpriteEntryType.Texture;
            }

            string data = ExtractStringValue(body, "Data");
            if (data != null)
            {
                foundAnyProperty = true;
                string unescaped = UnescapeCSharpString(data);
                if (sprite.Type == SpriteEntryType.Text)
                    sprite.Text = unescaped;
                else
                    sprite.SpriteName = unescaped;
            }

            string pos = ExtractValue(body, "Position");
            if (pos != null)
            {
                foundAnyProperty = true;
                ParseVector2(pos, out float px, out float py);
                sprite.X = px;
                sprite.Y = py;
            }

            string size = ExtractValue(body, "Size");
            if (size != null)
            {
                foundAnyProperty = true;
                ParseVector2(size, out float sw, out float sh);
                sprite.Width = sw;
                sprite.Height = sh;
            }

            string color = ExtractValue(body, "Color");
            if (color != null)
            {
                foundAnyProperty = true;
                ParseColor(color, out int cr, out int cg, out int cb, out int ca);
                sprite.ColorR = cr;
                sprite.ColorG = cg;
                sprite.ColorB = cb;
                sprite.ColorA = ca;
            }

            string fontId = ExtractStringValue(body, "FontId");
            if (fontId != null)
            {
                foundAnyProperty = true;
                sprite.FontId = UnescapeCSharpString(fontId);
            }

            string alignment = ExtractValue(body, "Alignment");
            if (alignment != null)
            {
                foundAnyProperty = true;
                if (alignment.IndexOf("LEFT", StringComparison.OrdinalIgnoreCase) >= 0)
                    sprite.Alignment = SpriteTextAlignment.Left;
                else if (alignment.IndexOf("RIGHT", StringComparison.OrdinalIgnoreCase) >= 0)
                    sprite.Alignment = SpriteTextAlignment.Right;
                else
                    sprite.Alignment = SpriteTextAlignment.Center;
            }
            else if (sprite.Type == SpriteEntryType.Text)
            {
                // SE default for text sprites is LEFT
                sprite.Alignment = SpriteTextAlignment.Left;
            }

            string rotOrScale = ExtractValue(body, "RotationOrScale");
            if (rotOrScale != null)
            {
                foundAnyProperty = true;
                float val = ParseFloat(rotOrScale);
                if (sprite.Type == SpriteEntryType.Text)
                    sprite.Scale = val;
                else
                    sprite.Rotation = val;
            }

            return foundAnyProperty ? sprite : null;
        }

        // ── Value extraction helpers ──────────────────────────────────────────

        /// <summary>
        /// Extracts the raw value text for a property assignment like "Name = value,"
        /// </summary>
        private static string ExtractValue(string body, string propertyName)
        {
            // Match: PropertyName = value (up to comma or end-of-line comment)
            var pattern = new Regex(
                @"(?<!\w)" + Regex.Escape(propertyName) + @"\s*=\s*(.+?)(?:,\s*(?://.*)?$|$)",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            var match = pattern.Match(body);
            if (!match.Success) return null;
            return match.Groups[1].Value.Trim().TrimEnd(',');
        }

        /// <summary>
        /// Extracts the content of a string literal for a property: Name = "value"
        /// Returns the raw string content (still escaped).
        /// </summary>
        private static string ExtractStringValue(string body, string propertyName)
        {
            var pattern = new Regex(
                @"(?<!\w)" + Regex.Escape(propertyName) + @"\s*=\s*""((?:[^""\\]|\\.)*)""",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            var match = pattern.Match(body);
            if (!match.Success) return null;
            return match.Groups[1].Value;
        }

        // ── Parsing helpers ──────────────────────────────────────────────────

        private static void ParseVector2(string value, out float x, out float y)
        {
            x = 0; y = 0;
            // Match: new Vector2(123.4f, 567.8f)
            var m = Regex.Match(value, @"[\d.-]+(?=\s*f?\s*,)");
            if (m.Success) x = ParseFloat(m.Value);
            // Second number
            var m2 = Regex.Match(value, @",\s*([\d.-]+)");
            if (m2.Success) y = ParseFloat(m2.Groups[1].Value);
        }

        private static void ParseColor(string value, out int r, out int g, out int b, out int a)
        {
            r = 255; g = 255; b = 255; a = 255;
            // Match: new Color(R, G, B, A) or new Color(R, G, B)
            var numbers = Regex.Matches(value, @"\d+");
            if (numbers.Count >= 3)
            {
                r = Clamp(int.Parse(numbers[0].Value));
                g = Clamp(int.Parse(numbers[1].Value));
                b = Clamp(int.Parse(numbers[2].Value));
                if (numbers.Count >= 4)
                    a = Clamp(int.Parse(numbers[3].Value));
            }
        }

        private static float ParseFloat(string s)
        {
            s = s.Trim().TrimEnd('f', 'F');
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                return val;

            // Expression like "0.75f * sc * fs" — extract the leading numeric literal
            var m = Regex.Match(s, @"^-?\d+(?:\.\d+)?");
            if (m.Success && float.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out val))
                return val;

            return 0;
        }

        private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

        private static string UnescapeCSharpString(string s)
        {
            if (s == null) return "";
            // Handle \uXXXX, \\, \", \n, \r, \t
            return Regex.Replace(s, @"\\(u[0-9A-Fa-f]{4}|.)", m =>
            {
                string esc = m.Groups[1].Value;
                if (esc.StartsWith("u"))
                    return ((char)int.Parse(esc.Substring(1), NumberStyles.HexNumber)).ToString();
                switch (esc[0])
                {
                    case '\\': return "\\";
                    case '"':  return "\"";
                    case 'n':  return "\n";
                    case 'r':  return "\r";
                    case 't':  return "\t";
                    default:   return esc;
                }
            });
        }

        /// <summary>
        /// Parses runtime snapshot data from in-game !lcd snapshot files.
        /// Supports two formats:
        ///   1. C# initializer blocks: <c>new LcdSpriteRow { RowKind = ..., Text = ..., ... }</c>
        ///   2. @ROW comment lines:    <c>// @ROW:Kind|Text|StatText|IconSprite|R,G,B,A|BarFill|R,G,B,A|ShowAlert</c>
        /// Returns the list of captured row data for replay in CodeExecutor.
        /// </summary>
        public static List<SnapshotRowData> ParseSnapshotRows(string code)
        {
            var results = new List<SnapshotRowData>();
            if (string.IsNullOrWhiteSpace(code)) return results;

            // ── Pass 1: Find "new LcdSpriteRow { ... }" blocks ──
            int searchFrom = 0;
            while (searchFrom < code.Length)
            {
                int idx = code.IndexOf("new LcdSpriteRow", searchFrom, StringComparison.Ordinal);
                if (idx < 0) break;

                // Find the opening brace
                int braceStart = FindNextNonWhitespace(code, idx + 16);
                if (braceStart >= 0 && code[braceStart] == '{')
                {
                    int braceEnd = FindMatchingBrace(code, braceStart);
                    if (braceEnd >= 0)
                    {
                        string body = code.Substring(braceStart + 1, braceEnd - braceStart - 1);
                        var row = ParseSnapshotRowBody(body);
                        if (row != null)
                            results.Add(row);
                        searchFrom = braceEnd + 1;
                        continue;
                    }
                }
                searchFrom = idx + 16;
            }

            // ── Pass 2: Parse "// @ROW:" comment lines ──
            // Format: // @ROW:Kind|Text|StatText|IconSprite|R,G,B,A|BarFill|R,G,B,A|ShowAlert
            // Pipes in text values are escaped as \|
            const string rowMarker = "// @ROW:";
            searchFrom = 0;
            while (searchFrom < code.Length)
            {
                int idx = code.IndexOf(rowMarker, searchFrom, StringComparison.Ordinal);
                if (idx < 0) break;

                // Extract line content after the marker
                int lineStart = idx + rowMarker.Length;
                int lineEnd = code.IndexOf('\n', lineStart);
                if (lineEnd < 0) lineEnd = code.Length;
                string line = code.Substring(lineStart, lineEnd - lineStart).TrimEnd('\r');

                var row = ParseRowCommentLine(line);
                if (row != null)
                    results.Add(row);

                searchFrom = lineEnd + 1;
            }

            return results;
        }

        /// <summary>
        /// Splits a pipe-delimited <c>@ROW</c> comment payload, respecting <c>\|</c> escapes.
        /// </summary>
        private static string[] SplitRowFields(string line)
        {
            var fields = new System.Collections.Generic.List<string>();
            var current = new System.Text.StringBuilder();
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '\\' && i + 1 < line.Length && line[i + 1] == '|')
                {
                    current.Append('|');
                    i++; // skip the escaped pipe
                }
                else if (line[i] == '|')
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(line[i]);
                }
            }
            fields.Add(current.ToString());
            return fields.ToArray();
        }

        /// <summary>
        /// Parses one <c>@ROW</c> comment line into a <see cref="SnapshotRowData"/>.
        /// Expected 8 fields: Kind|Text|StatText|IconSprite|R,G,B,A|BarFill|R,G,B,A|ShowAlert
        /// </summary>
        private static SnapshotRowData ParseRowCommentLine(string line)
        {
            var fields = SplitRowFields(line);
            if (fields.Length < 8) return null;

            var row = new SnapshotRowData();
            try
            {
                row.Kind = fields[0];
                row.Text = fields[1];
                row.StatText = fields[2];
                row.IconSprite = fields[3];

                // TextColor: R,G,B,A
                var tc = fields[4].Split(',');
                if (tc.Length >= 4)
                {
                    row.TextColorR = int.Parse(tc[0].Trim());
                    row.TextColorG = int.Parse(tc[1].Trim());
                    row.TextColorB = int.Parse(tc[2].Trim());
                    row.TextColorA = int.Parse(tc[3].Trim());
                }

                // BarFill
                float bf;
                if (float.TryParse(fields[5].Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out bf))
                    row.BarFill = bf;

                // BarFillColor: R,G,B,A
                var bfc = fields[6].Split(',');
                if (bfc.Length >= 4)
                {
                    row.BarFillColorR = int.Parse(bfc[0].Trim());
                    row.BarFillColorG = int.Parse(bfc[1].Trim());
                    row.BarFillColorB = int.Parse(bfc[2].Trim());
                    row.BarFillColorA = int.Parse(bfc[3].Trim());
                }

                // ShowAlert
                row.ShowAlert = fields[7].Trim() == "1"
                    || fields[7].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return null; // malformed line — skip
            }

            return row;
        }

        /// <summary>
        /// Parses the body of a "new LcdSpriteRow { RowKind = ..., Text = ..., ... }" initializer.
        /// </summary>
        private static SnapshotRowData ParseSnapshotRowBody(string body)
        {
            var row = new SnapshotRowData();

            // Split assignments by comma (simple split - good enough for snapshot files)
            var assignments = body.Split(',');
            foreach (string assign in assignments)
            {
                string trimmed = assign.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                int eqIdx = trimmed.IndexOf('=');
                if (eqIdx < 0) continue;

                string propName = trimmed.Substring(0, eqIdx).Trim();
                string valueStr = trimmed.Substring(eqIdx + 1).Trim();

                try
                {
                    switch (propName)
                    {
                        case "RowKind":
                            // "LcdSpriteRow.Kind.Header" → "Header"
                            int lastDot = valueStr.LastIndexOf('.');
                            row.Kind = lastDot >= 0 ? valueStr.Substring(lastDot + 1) : valueStr;
                            break;
                        case "Text":
                            row.Text = ParseQuotedString(valueStr);
                            break;
                        case "StatText":
                            row.StatText = ParseQuotedString(valueStr);
                            break;
                        case "IconSprite":
                            row.IconSprite = ParseQuotedString(valueStr);
                            break;
                        case "TextColor":
                            {
                                int r = 0, g = 0, b = 0, a = 255;
                                ParseColorInto(valueStr, ref r, ref g, ref b, ref a);
                                row.TextColorR = r;
                                row.TextColorG = g;
                                row.TextColorB = b;
                                row.TextColorA = a;
                            }
                            break;
                        case "BarFill":
                            row.BarFill = ParseFloat(valueStr);
                            break;
                        case "BarFillColor":
                            {
                                int r = 0, g = 0, b = 0, a = 255;
                                ParseColorInto(valueStr, ref r, ref g, ref b, ref a);
                                row.BarFillColorR = r;
                                row.BarFillColorG = g;
                                row.BarFillColorB = b;
                                row.BarFillColorA = a;
                            }
                            break;
                        case "ShowAlert":
                            row.ShowAlert = valueStr.Equals("true", StringComparison.OrdinalIgnoreCase);
                            break;
                    }
                }
                catch { /* Skip malformed property */ }
            }

            return row;
        }

        /// <summary>
        /// Extracts a string value from a quoted string literal (e.g., "Hello" → Hello).
        /// </summary>
        private static string ParseQuotedString(string expr)
        {
            var match = Regex.Match(expr, @"""((?:[^""\\]|\\.)*)""");
            return match.Success ? UnescapeCSharpString(match.Groups[1].Value) : "";
        }

        /// <summary>
        /// Parses a Color initializer like "new Color(255, 0, 0)" or "Color.White" into RGBA components.
        /// </summary>
        private static void ParseColorInto(string colorExpr, ref int r, ref int g, ref int b, ref int a)
        {
            // Handle named colors first
            if (colorExpr.Contains("Color.White")) { r = 255; g = 255; b = 255; a = 255; return; }
            if (colorExpr.Contains("Color.Black")) { r = 0; g = 0; b = 0; a = 255; return; }
            if (colorExpr.Contains("Color.Red")) { r = 255; g = 0; b = 0; a = 255; return; }
            if (colorExpr.Contains("Color.Green")) { r = 0; g = 255; b = 0; a = 255; return; }
            if (colorExpr.Contains("Color.Blue")) { r = 0; g = 0; b = 255; a = 255; return; }
            if (colorExpr.Contains("Color.Yellow")) { r = 255; g = 255; b = 0; a = 255; return; }
            if (colorExpr.Contains("Color.Cyan")) { r = 0; g = 255; b = 255; a = 255; return; }
            if (colorExpr.Contains("Color.Magenta")) { r = 255; g = 0; b = 255; a = 255; return; }

            // Parse "new Color(r, g, b)" or "new Color(r, g, b, a)"
            var match = Regex.Match(colorExpr, @"new\s+Color\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)(?:\s*,\s*(\d+))?\s*\)");
            if (match.Success)
            {
                r = int.Parse(match.Groups[1].Value);
                g = int.Parse(match.Groups[2].Value);
                b = int.Parse(match.Groups[3].Value);
                a = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 255;
            }
        }
    }
}
