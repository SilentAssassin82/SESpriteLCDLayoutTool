using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SESpriteLCDLayoutTool.Models;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Roslyn-based animation code injector.  Uses the Roslyn syntax tree to
    /// locate each sprite's <c>.Add(new MySprite { Data = "name" })</c> block,
    /// then injects animation fields, per-frame computation, and property
    /// overrides — all idempotently via marker comments.
    ///
    /// Supports stacking multiple effects on a single sprite and animating
    /// multiple sprites in the same program.  One method for all animation
    /// types, forever.
    /// </summary>
    public static class RoslynAnimationInjector
    {
        // ── Marker comments (idempotent boundaries) ──────────────────────────
        private const string FieldsStart  = "// ──▶ ANIM-FIELDS ◀──";
        private const string FieldsEnd    = "// ──▶ END ANIM-FIELDS ◀──";
        private const string ComputeTag   = "// ──▶ ANIM:";      // followed by sprite name + " ◀──"
        private const string ComputeEnd   = "// ──▶ END ANIM:";  // followed by sprite name + " ◀──"
        private const string EaseStart    = "// ──▶ ANIM-EASE ◀──";
        private const string EaseEnd      = "// ──▶ END ANIM-EASE ◀──";

        // ── Result ───────────────────────────────────────────────────────────

        public class InjectionResult
        {
            public bool Success { get; set; }
            public string Code { get; set; }
            public string Error { get; set; }
            public int SpritesAnimated { get; set; }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Public API
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Injects animation code for all sprites that have
        /// <see cref="SpriteEntry.AnimationEffects"/>.  Fully idempotent —
        /// re-running replaces previously injected regions.
        ///
        /// The method:
        /// 1. Strips any previously-injected marker regions
        /// 2. Uses Roslyn to find each animated sprite's Add block by Data="name"
        /// 3. Inserts fields at the class/struct level (or before Program())
        /// 4. Inserts Ease() helper if needed
        /// 5. Inserts per-frame compute lines before each sprite's Add block
        /// 6. Replaces static property values with animated expressions
        /// </summary>
        public static InjectionResult InjectAnimations(
            string sourceCode,
            IEnumerable<SpriteEntry> allSprites)
        {
            var result = new InjectionResult { Success = false };

            if (string.IsNullOrWhiteSpace(sourceCode))
            {
                result.Error = "Source code is empty";
                return result;
            }

            // Collect sprites with effects
            var animated = new List<SpriteEntry>();
            foreach (var sp in allSprites)
            {
                if (sp.AnimationEffects != null && sp.AnimationEffects.Count > 0)
                    animated.Add(sp);
            }

            if (animated.Count == 0)
            {
                result.Success = true;
                result.Code = sourceCode;
                return result;
            }

            try
            {
                // Step 0: Strip previously injected regions
                string code = StripInjectedRegions(sourceCode);

                // Step 1: Parse with Roslyn to find structural anchors
                var tree = CSharpSyntaxTree.ParseText(code);
                var root = tree.GetRoot();

                // Step 2: Find each animated sprite's Add block
                var spriteLocations = new List<SpriteLocation>();
                int suffixCounter = 0;
                bool needsEase = false;
                bool needsTick = false;

                foreach (var sp in animated)
                {
                    string spriteName = sp.Type == SpriteEntryType.Text
                        ? sp.Text : sp.SpriteName;
                    if (string.IsNullOrEmpty(spriteName)) continue;

                    var addNode = FindSpriteAddNode(root, spriteName);
                    if (addNode == null)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[RoslynAnimationInjector] Could not find Add block for '{spriteName}'");
                        continue;
                    }

                    suffixCounter++;
                    string suffix = suffixCounter == 1 ? "" : suffixCounter.ToString();

                    foreach (var fx in sp.AnimationEffects)
                    {
                        if (fx.NeedsTick) needsTick = true;
                        if (fx.NeedsEaseHelper) needsEase = true;
                    }

                    spriteLocations.Add(new SpriteLocation
                    {
                        Sprite = sp,
                        SpriteName = spriteName,
                        AddNode = addNode,
                        Suffix = suffix,
                    });
                }

                if (spriteLocations.Count == 0)
                {
                    result.Error = "Could not find any animated sprite Add blocks in code";
                    return result;
                }

                // Step 2b: Resolve leader suffixes for GroupFollowerEffects.
                // The leader sprite has a KeyframeEffect; followers reference it.
                // Build a map: sprite → suffix for sprites that own a KeyframeEffect.
                var leaderSuffixMap = new Dictionary<KeyframeEffect, string>();
                foreach (var loc in spriteLocations)
                {
                    foreach (var fx in loc.Sprite.AnimationEffects)
                    {
                        if (fx is KeyframeEffect kfe && !leaderSuffixMap.ContainsKey(kfe))
                            leaderSuffixMap[kfe] = loc.Suffix;
                    }
                }
                foreach (var loc in spriteLocations)
                {
                    foreach (var fx in loc.Sprite.AnimationEffects)
                    {
                        if (fx is GroupFollowerEffect gfe && gfe.LeaderEffect != null)
                        {
                            if (leaderSuffixMap.TryGetValue(gfe.LeaderEffect, out string ls))
                                gfe.LeaderSuffix = ls;
                        }
                    }
                }

                // Step 3: Build the fields block
                var fieldLines = new List<string>();
                if (needsTick)
                {
                    // Only emit _tick if not already declared in user code
                    if (!HasExistingField(code, "_tick"))
                        fieldLines.Add("int _tick = 0;");
                }

                // Collect fields from all effects (deduplicated)
                var seenFieldLines = new HashSet<string>();
                foreach (var loc in spriteLocations)
                {
                    foreach (var fx in loc.Sprite.AnimationEffects)
                    {
                        foreach (string line in fx.EmitFields(loc.Suffix))
                        {
                            if (seenFieldLines.Add(line))
                                fieldLines.Add(line);
                        }
                    }
                }

                // Step 4: Apply changes from bottom to top (preserves char offsets)
                // Sort sprite locations by position descending
                spriteLocations.Sort((a, b) => b.AddNode.SpanStart.CompareTo(a.AddNode.SpanStart));

                string working = code;

                foreach (var loc in spriteLocations)
                {
                    working = InjectForSprite(working, loc);
                }

                // Step 5: Insert Ease helper if needed
                if (needsEase && !HasEaseFunction(working))
                {
                    working = InsertEaseHelper(working);
                }

                // Step 6: Insert fields block
                if (fieldLines.Count > 0)
                {
                    working = InsertFieldsBlock(working, fieldLines);
                }

                // Step 7: Insert _tick++ in the render method if needed
                if (needsTick && !HasTickIncrement(working))
                {
                    working = InsertTickIncrement(working);
                }

                result.Success = true;
                result.Code = working;
                result.SpritesAnimated = spriteLocations.Count;
            }
            catch (Exception ex)
            {
                result.Error = $"Animation injection error: {ex.Message}";
                System.Diagnostics.Debug.WriteLine(
                    $"[RoslynAnimationInjector] ERROR: {ex}");
            }

            return result;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Roslyn sprite finder
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Finds the <c>ExpressionStatementSyntax</c> for a sprite's
        /// <c>frame.Add(new MySprite { Data = "spriteName" })</c> call.
        /// </summary>
        private static ExpressionStatementSyntax FindSpriteAddNode(
            SyntaxNode root, string spriteName)
        {
            // Find all *.Add(new MySprite { ... }) statements
            var addStatements = root.DescendantNodes()
                .OfType<ExpressionStatementSyntax>()
                .Where(stmt =>
                    stmt.Expression is InvocationExpressionSyntax inv &&
                    inv.Expression is MemberAccessExpressionSyntax ma &&
                    ma.Name.Identifier.Text == "Add");

            foreach (var stmt in addStatements)
            {
                var inv = (InvocationExpressionSyntax)stmt.Expression;

                // Look for ObjectCreationExpression with MySprite
                var creation = inv.DescendantNodes()
                    .OfType<ObjectCreationExpressionSyntax>()
                    .FirstOrDefault(c => c.Type.ToString().Contains("MySprite"));

                if (creation?.Initializer == null) continue;

                // Find Data = "spriteName" in the initialiser
                foreach (var expr in creation.Initializer.Expressions)
                {
                    if (expr is AssignmentExpressionSyntax assign &&
                        assign.Left is IdentifierNameSyntax id &&
                        id.Identifier.Text == "Data")
                    {
                        string dataValue = assign.Right.ToString().Trim('"');
                        if (string.Equals(dataValue, spriteName, StringComparison.Ordinal))
                            return stmt;
                    }
                }
            }

            return null;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Per-sprite injection
        // ═════════════════════════════════════════════════════════════════════

        private class SpriteLocation
        {
            public SpriteEntry Sprite;
            public string SpriteName;
            public ExpressionStatementSyntax AddNode;
            public string Suffix;
        }

        /// <summary>
        /// Injects compute lines before the sprite's Add block and replaces
        /// property values in the initialiser.
        /// </summary>
        private static string InjectForSprite(string code, SpriteLocation loc)
        {
            // Re-parse to get fresh positions (previous injections shifted offsets)
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetRoot();
            var addNode = FindSpriteAddNode(root, loc.SpriteName);
            if (addNode == null) return code;

            // ── Collect all effects' compute lines and property overrides ──
            var allCompute = new List<string>();
            var allOverrides = new Dictionary<string, string>();

            foreach (var fx in loc.Sprite.AnimationEffects)
            {
                allCompute.AddRange(fx.EmitCompute(loc.Suffix));
                foreach (var kv in fx.GetPropertyOverrides(loc.Suffix))
                    allOverrides[kv.Key] = kv.Value; // last-write-wins per property
            }

            // Resolve placeholder tokens with sprite's actual values
            ResolveBasePlaceholders(allOverrides, loc.Sprite);

            // ── Replace property values in the initialiser ──
            string result = code;
            string blinkGuard = null;

            if (allOverrides.ContainsKey("__blink_guard"))
            {
                blinkGuard = allOverrides["__blink_guard"];
                allOverrides.Remove("__blink_guard");
            }

            // Find the initialiser and replace properties
            var creation = addNode.DescendantNodes()
                .OfType<ObjectCreationExpressionSyntax>()
                .First(c => c.Type.ToString().Contains("MySprite"));

            if (creation.Initializer != null)
            {
                foreach (var expr in creation.Initializer.Expressions)
                {
                    if (expr is AssignmentExpressionSyntax assign &&
                        assign.Left is IdentifierNameSyntax propId)
                    {
                        string propName = propId.Identifier.Text;
                        if (allOverrides.ContainsKey(propName))
                        {
                            string oldValue = assign.Right.ToString();
                            string newValue = allOverrides[propName];
                            // Replace this specific assignment's value
                            int valueStart = assign.Right.SpanStart;
                            int valueEnd = assign.Right.Span.End;
                            result = result.Substring(0, valueStart)
                                   + newValue
                                   + result.Substring(valueEnd);

                            // Re-parse after each replacement to keep positions valid
                            tree = CSharpSyntaxTree.ParseText(result);
                            root = tree.GetRoot();
                            addNode = FindSpriteAddNode(root, loc.SpriteName);
                            if (addNode == null) return result;
                            creation = addNode.DescendantNodes()
                                .OfType<ObjectCreationExpressionSyntax>()
                                .First(c => c.Type.ToString().Contains("MySprite"));
                        }
                    }
                }
            }

            // ── Insert compute lines before the Add block ──
            if (allCompute.Count > 0)
            {
                // Re-parse to get fresh position
                tree = CSharpSyntaxTree.ParseText(result);
                root = tree.GetRoot();
                addNode = FindSpriteAddNode(root, loc.SpriteName);
                if (addNode == null) return result;

                string indent = GetIndent(result, addNode.SpanStart);
                var sb = new StringBuilder();
                sb.AppendLine($"{indent}{ComputeTag}{loc.SpriteName} ◀──");
                foreach (string line in allCompute)
                {
                    if (line.Trim().Length == 0)
                        sb.AppendLine();
                    else
                        sb.AppendLine(indent + line);
                }
                sb.AppendLine($"{indent}{ComputeEnd}{loc.SpriteName} ◀──");

                int insertPos = LineStartOf(result, addNode.SpanStart);
                result = result.Insert(insertPos, sb.ToString());
            }

            // ── Blink guard: wrap Add block in if ──
            if (blinkGuard != null)
            {
                tree = CSharpSyntaxTree.ParseText(result);
                root = tree.GetRoot();
                addNode = FindSpriteAddNode(root, loc.SpriteName);
                if (addNode != null)
                {
                    string indent = GetIndent(result, addNode.SpanStart);
                    string addText = addNode.ToFullString();

                    // Re-indent the Add block inside the if
                    string[] addLines = addText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                    var wrapped = new StringBuilder();
                    wrapped.AppendLine($"{indent}if ({blinkGuard})");
                    wrapped.AppendLine($"{indent}{{");
                    foreach (string line in addLines)
                    {
                        if (line.Trim().Length == 0)
                            wrapped.AppendLine();
                        else
                            wrapped.AppendLine("    " + line);
                    }
                    wrapped.Append($"{indent}}}");

                    int stmtStart = addNode.FullSpan.Start;
                    int stmtEnd = addNode.FullSpan.End;
                    result = result.Substring(0, stmtStart)
                           + wrapped.ToString()
                           + result.Substring(stmtEnd);
                }
            }

            return result;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Field & helper insertion
        // ═════════════════════════════════════════════════════════════════════

        private static string InsertFieldsBlock(string code, List<string> fieldLines)
        {
            // Find insertion point: before Program(), before first method, or top of file
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetRoot();

            int insertPos = -1;
            string indent = "";

            // Try before Program() constructor
            var ctor = root.DescendantNodes()
                .OfType<ConstructorDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text == "Program");

            if (ctor != null)
            {
                insertPos = LineStartOf(code, ctor.SpanStart);
                indent = GetIndent(code, ctor.SpanStart);
            }
            else
            {
                // Before first method
                var firstMethod = root.DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault();
                if (firstMethod != null)
                {
                    insertPos = LineStartOf(code, firstMethod.SpanStart);
                    indent = GetIndent(code, firstMethod.SpanStart);
                }
                else
                {
                    // Before first field/class member
                    var firstMember = root.DescendantNodes()
                        .OfType<FieldDeclarationSyntax>()
                        .FirstOrDefault();
                    if (firstMember != null)
                    {
                        insertPos = LineStartOf(code, firstMember.SpanStart);
                        indent = GetIndent(code, firstMember.SpanStart);
                    }
                }
            }

            if (insertPos < 0)
            {
                // Last resort: after first blank line or at top
                int firstBlank = code.IndexOf("\n\n", StringComparison.Ordinal);
                insertPos = firstBlank >= 0 ? firstBlank + 2 : 0;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"{indent}{FieldsStart}");
            foreach (string line in fieldLines)
                sb.AppendLine(indent + line);
            sb.AppendLine($"{indent}{FieldsEnd}");
            sb.AppendLine();

            return code.Insert(insertPos, sb.ToString());
        }

        private static string InsertEaseHelper(string code)
        {
            // Insert before Program() or before Main() or at the end
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetRoot();

            int insertPos = -1;
            string indent = "";

            var ctor = root.DescendantNodes()
                .OfType<ConstructorDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text == "Program");
            if (ctor != null)
            {
                insertPos = LineStartOf(code, ctor.SpanStart);
                indent = GetIndent(code, ctor.SpanStart);
            }
            else
            {
                var mainMethod = root.DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.Text == "Main");
                if (mainMethod != null)
                {
                    insertPos = LineStartOf(code, mainMethod.SpanStart);
                    indent = GetIndent(code, mainMethod.SpanStart);
                }
            }

            if (insertPos < 0) return code;

            var sb = new StringBuilder();
            sb.AppendLine($"{indent}{EaseStart}");
            foreach (string line in EaseHelperText.EaseMethod.Split('\n'))
            {
                string trimmed = line.TrimEnd('\r');
                sb.AppendLine(trimmed.Length > 0 ? indent + trimmed : "");
            }
            sb.AppendLine($"{indent}{EaseEnd}");
            sb.AppendLine();

            return code.Insert(insertPos, sb.ToString());
        }

        private static string InsertTickIncrement(string code)
        {
            // Find the render method body and insert _tick++ at the top
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetRoot();

            // Look for Main() first, then any method with DrawFrame or List<MySprite>
            MethodDeclarationSyntax renderMethod = null;

            renderMethod = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == "Main");

            if (renderMethod == null)
            {
                renderMethod = root.DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m =>
                        m.ParameterList.Parameters.Any(p =>
                            p.Type?.ToString().Contains("List<MySprite>") == true ||
                            p.Type?.ToString().Contains("MySpriteDrawFrame") == true));
            }

            if (renderMethod?.Body == null || renderMethod.Body.Statements.Count == 0)
                return code;

            // Insert after the first statement (usually var frame = ...)
            var firstStmt = renderMethod.Body.Statements.First();
            int insertPos = firstStmt.FullSpan.End;
            string indent = GetIndent(code, firstStmt.SpanStart);

            return code.Insert(insertPos, $"{indent}_tick++;\n");
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Strip previously injected regions
        // ═════════════════════════════════════════════════════════════════════

        private static string StripInjectedRegions(string code)
        {
            // Remove ANIM-FIELDS block
            code = StripMarkerBlock(code, FieldsStart, FieldsEnd);

            // Remove ANIM-EASE block
            code = StripMarkerBlock(code, EaseStart, EaseEnd);

            // Remove all ANIM:SpriteName compute blocks
            var computeRx = new Regex(
                @"[ \t]*" + Regex.Escape(ComputeTag) + @"[^\n]*\n" +
                @"(.*?\n)*?" +
                @"[ \t]*" + Regex.Escape(ComputeEnd) + @"[^\n]*\n",
                RegexOptions.Compiled);
            code = computeRx.Replace(code, "");

            // Clean up any double blank lines left behind
            code = Regex.Replace(code, @"\n{3,}", "\n\n");

            return code;
        }

        private static string StripMarkerBlock(string code, string startMarker, string endMarker)
        {
            int start = code.IndexOf(startMarker, StringComparison.Ordinal);
            if (start < 0) return code;

            int end = code.IndexOf(endMarker, start, StringComparison.Ordinal);
            if (end < 0) return code;

            // Extend to cover full lines
            int lineStart = start;
            while (lineStart > 0 && code[lineStart - 1] != '\n') lineStart--;

            int lineEnd = end + endMarker.Length;
            if (lineEnd < code.Length && code[lineEnd] == '\r') lineEnd++;
            if (lineEnd < code.Length && code[lineEnd] == '\n') lineEnd++;

            return code.Substring(0, lineStart) + code.Substring(lineEnd);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Helpers
        // ═════════════════════════════════════════════════════════════════════

        private static void ResolveBasePlaceholders(
            Dictionary<string, string> overrides, SpriteEntry sp)
        {
            var keys = new List<string>(overrides.Keys);
            foreach (string key in keys)
            {
                string val = overrides[key];
                val = val.Replace("{baseX}", $"{sp.X:F1}f");
                val = val.Replace("{baseY}", $"{sp.Y:F1}f");
                val = val.Replace("{baseW}", $"{sp.Width:F1}f");
                val = val.Replace("{baseH}", $"{sp.Height:F1}f");
                val = val.Replace("{baseR}", sp.ColorR.ToString());
                val = val.Replace("{baseG}", sp.ColorG.ToString());
                val = val.Replace("{baseB}", sp.ColorB.ToString());
                val = val.Replace("{baseA}", sp.ColorA.ToString());
                overrides[key] = val;
            }
        }

        private static bool HasExistingField(string code, string fieldName)
        {
            // Check if a field like "int _tick" already exists outside our markers
            string stripped = StripMarkerBlock(code, FieldsStart, FieldsEnd);
            return Regex.IsMatch(stripped,
                @"\b(int|float|bool)\s+" + Regex.Escape(fieldName) + @"\b");
        }

        private static bool HasEaseFunction(string code)
        {
            return Regex.IsMatch(code, @"\bfloat\s+Ease\s*\(\s*float\s+");
        }

        private static bool HasTickIncrement(string code)
        {
            return code.Contains("_tick++");
        }

        /// <summary>Returns the start-of-line character index for a given position.</summary>
        private static int LineStartOf(string code, int pos)
        {
            int i = pos;
            while (i > 0 && code[i - 1] != '\n') i--;
            return i;
        }

        /// <summary>Gets the whitespace indent of the line containing <paramref name="pos"/>.</summary>
        private static string GetIndent(string code, int pos)
        {
            int lineStart = LineStartOf(code, pos);
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
    }
}
