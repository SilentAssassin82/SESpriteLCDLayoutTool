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
        private const string BlinkTag     = "// ──▶ BLINK:";      // followed by sprite name + " ◀──"
        private const string BlinkEnd     = "// ──▶ END BLINK:";  // followed by sprite name + " ◀──"

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

            // Materialize full sprite list for ordinal lookups
            var allSpritesList = allSprites.ToList();

            // Collect sprites with effects
            var animated = new List<SpriteEntry>();
            foreach (var sp in allSpritesList)
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

                // Step 1b: Insert missing sprite Add blocks so every animated
                //          sprite has a code anchor for the injector to target.
                code = EnsureAllSpritesHaveAddBlocks(code, animated, allSpritesList);
                tree = CSharpSyntaxTree.ParseText(code);
                root = tree.GetRoot();

                // Step 2: Find each animated sprite's Add block
                var spriteLocations = new List<SpriteLocation>();
                int suffixCounter = 0;
                bool needsEase = false;
                bool needsTick = false;

                // Build a map: sprite instance → ordinal among ALL sprites with same name.
                // This ensures SemiCircle #2 gets ordinal 1 even if #1 isn't animated.
                var spriteOrdinalMap = new Dictionary<SpriteEntry, int>();
                {
                    var nameCounters = new Dictionary<string, int>();
                    foreach (var sp in allSprites)
                    {
                        string key = sp.Type == SpriteEntryType.Text ? sp.Text : sp.SpriteName;
                        if (string.IsNullOrEmpty(key)) continue;
                        if (!nameCounters.TryGetValue(key, out int cnt))
                            cnt = 0;
                        spriteOrdinalMap[sp] = cnt;
                        nameCounters[key] = cnt + 1;
                    }
                }

                foreach (var sp in animated)
                {
                    string spriteName = sp.Type == SpriteEntryType.Text
                        ? sp.Text : sp.SpriteName;
                    if (string.IsNullOrEmpty(spriteName)) continue;

                    int ord = spriteOrdinalMap.TryGetValue(sp, out int o) ? o : 0;

                    var addNode = FindSpriteAddNode(root, spriteName, ord);
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
                        Ordinal = ord,
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
        /// <param name="ordinal">0-based occurrence index when multiple sprites share the same Data name.</param>
        private static ExpressionStatementSyntax FindSpriteAddNode(
            SyntaxNode root, string spriteName, int ordinal = 0)
        {
            // Find all *.Add(new MySprite { ... }) statements
            var addStatements = root.DescendantNodes()
                .OfType<ExpressionStatementSyntax>()
                .Where(stmt =>
                    stmt.Expression is InvocationExpressionSyntax inv &&
                    inv.Expression is MemberAccessExpressionSyntax ma &&
                    ma.Name.Identifier.Text == "Add");

            int matchIndex = 0;
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
                        {
                            if (matchIndex == ordinal)
                                return stmt;
                            matchIndex++;
                        }
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
            public int Ordinal;  // 0-based occurrence index for duplicate Data names
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
            var addNode = FindSpriteAddNode(root, loc.SpriteName, loc.Ordinal);
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

            // ── Compose channel-scoped overrides into their parent property ──
            // Fade only touches alpha via "Color.A"; combine it with whatever
            // else produces Color (ColorCycle, Keyframe color, or static RGB)
            // so channel-scoped effects never cancel full-property ones.
            if (allOverrides.TryGetValue("Color.A", out string alphaExpr))
            {
                allOverrides.Remove("Color.A");
                if (allOverrides.TryGetValue("Color", out string colorExpr))
                {
                    // Rewrite existing Color: keep RGB, override alpha.
                    allOverrides["Color"] =
                        $"new Color(({colorExpr}).R, ({colorExpr}).G, ({colorExpr}).B, (byte)({alphaExpr}))";
                }
                else
                {
                    // No other Color contributor — fade against the sprite's static RGB.
                    allOverrides["Color"] =
                        $"new Color({{baseR}}, {{baseG}}, {{baseB}}, (byte)({alphaExpr}))";
                }
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
                // Build all value replacements from the current syntax tree, then apply
                // them from end-to-start so offsets remain valid and assignments cannot
                // be accidentally spliced together.
                var valueReplacements = new List<Tuple<int, int, string>>();

                foreach (var expr in creation.Initializer.Expressions)
                {
                    if (expr is AssignmentExpressionSyntax assign &&
                        assign.Left is IdentifierNameSyntax propId)
                    {
                        string propName = propId.Identifier.Text;
                        if (allOverrides.ContainsKey(propName))
                        {
                            string newValue = allOverrides[propName];
                            valueReplacements.Add(Tuple.Create(assign.Right.SpanStart, assign.Right.Span.End, newValue));
                        }
                    }
                }

                foreach (var rep in valueReplacements.OrderByDescending(r => r.Item1))
                {
                    result = result.Substring(0, rep.Item1)
                           + rep.Item3
                           + result.Substring(rep.Item2);
                }
            }

            // ── Insert compute lines before the Add block ──
            if (allCompute.Count > 0)
            {
                // Re-parse to get fresh position
                tree = CSharpSyntaxTree.ParseText(result);
                root = tree.GetRoot();
                addNode = FindSpriteAddNode(root, loc.SpriteName, loc.Ordinal);
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
                addNode = FindSpriteAddNode(root, loc.SpriteName, loc.Ordinal);
                if (addNode != null)
                {
                    string indent = GetIndent(result, addNode.SpanStart);
                    // Use Span (not FullSpan) to avoid capturing leading trivia like END ANIM comments
                    string addText = result.Substring(addNode.SpanStart, addNode.Span.Length);

                    // Re-indent the Add block inside the if
                    string[] addLines = addText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                    var wrapped = new StringBuilder();
                    wrapped.AppendLine($"{indent}{BlinkTag}{loc.SpriteName} ◀──");
                    wrapped.AppendLine($"{indent}if ({blinkGuard})");
                    wrapped.AppendLine($"{indent}{{");
                    foreach (string line in addLines)
                    {
                        if (line.Trim().Length == 0)
                            wrapped.AppendLine();
                        else
                            wrapped.AppendLine("    " + line);
                    }
                    wrapped.AppendLine($"{indent}}}");
                    wrapped.Append($"{indent}{BlinkEnd}{loc.SpriteName} ◀──");

                    int stmtStart = addNode.SpanStart;
                    int stmtEnd = addNode.Span.End;
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
                else
                {
                    // Fallback: any method containing .DrawFrame() — handles DrawLayout(IMyTextSurface)
                    // style methods from CodeGenerator.Generate(); insert Ease before the method.
                    var drawFrameMethod = root.DescendantNodes()
                        .OfType<MethodDeclarationSyntax>()
                        .FirstOrDefault(m =>
                            m.Body?.DescendantNodes()
                                .OfType<InvocationExpressionSyntax>()
                                .Any(inv =>
                                    inv.Expression is MemberAccessExpressionSyntax ma &&
                                    ma.Name.Identifier.Text == "DrawFrame") == true);
                    if (drawFrameMethod != null)
                    {
                        insertPos = LineStartOf(code, drawFrameMethod.SpanStart);
                        indent = GetIndent(code, drawFrameMethod.SpanStart);
                    }
                    else
                    {
                        // Last resort: before the first method declaration of any kind
                        var firstMethod = root.DescendantNodes()
                            .OfType<MethodDeclarationSyntax>()
                            .FirstOrDefault();
                        if (firstMethod != null)
                        {
                            insertPos = LineStartOf(code, firstMethod.SpanStart);
                            indent = GetIndent(code, firstMethod.SpanStart);
                        }
                    }
                }
            }

            if (insertPos < 0)
                return code;

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

            // Fallback: any method containing .DrawFrame() — handles DrawLayout(IMyTextSurface)
            // style methods generated by CodeGenerator.Generate().
            if (renderMethod == null)
            {
                renderMethod = root.DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m =>
                        m.Body?.DescendantNodes()
                            .OfType<InvocationExpressionSyntax>()
                            .Any(inv =>
                                inv.Expression is MemberAccessExpressionSyntax ma &&
                                ma.Name.Identifier.Text == "DrawFrame") == true);
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

            // Unwrap BLINK guard wrappers FIRST (before compute strip),
            // so the ANIM compute regex doesn't cross into blink blocks
            code = UnwrapBlinkGuards(code);

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

        /// <summary>
        /// Unwraps blink guard wrappers injected around Add blocks.
        /// Keeps the inner Add block content, removes the BLINK markers, if(), and braces.
        /// </summary>
        private static string UnwrapBlinkGuards(string code)
        {
            // Match: BLINK:name marker, if line, opening brace, inner content, closing brace, END BLINK:name marker
            var blinkRx = new Regex(
                @"(?<indent>[ \t]*)" + Regex.Escape(BlinkTag) + @"[^\n]*\n" +       // BLINK marker
                @"[ \t]*if\s*\([^)]*\)\s*\n" +                                       // if (blinkVisible)
                @"[ \t]*\{\s*\n" +                                                    // {
                @"(?<inner>(.*?\n)*?)" +                                               // inner content (Add block)
                @"[ \t]*\}\s*\n" +                                                    // }
                @"[ \t]*" + Regex.Escape(BlinkEnd) + @"[^\n]*\n?",                    // END BLINK marker
                RegexOptions.Compiled);

            return blinkRx.Replace(code, m =>
            {
                // Dedent the inner content by 4 spaces (the blink guard indentation).
                // Split on '\n' drops the separators, so we must re-emit them per line
                // to preserve the original line structure (otherwise the Add block
                // collapses onto a single line and the next merge loses content).
                string inner = m.Groups["inner"].Value;
                var lines = inner.Split(new[] { "\n" }, StringSplitOptions.None);
                var sb = new StringBuilder();
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    string dedented;
                    if (line.TrimEnd('\r').Length == 0)
                        dedented = line; // preserve blank lines (incl. any trailing \r)
                    else if (line.StartsWith("    "))
                        dedented = line.Substring(4);
                    else
                        dedented = line;

                    sb.Append(dedented);
                    // Re-append the '\n' that Split() consumed, except after the
                    // final element (which is the trailing piece after the last \n).
                    if (i < lines.Length - 1)
                        sb.Append('\n');
                }
                return sb.ToString();
            });
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

        // ═════════════════════════════════════════════════════════════════════
        //  Missing sprite Add-block insertion
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Ensures every animated sprite has a <c>frame.Add(new MySprite { … });</c>
        /// block in the source code.  When applying a simple animation to a second
        /// sprite and the code panel only contains the first sprite's block, this
        /// inserts a static Add block for the missing sprite so the injector can
        /// find it and apply property overrides.
        /// </summary>
        private static string EnsureAllSpritesHaveAddBlocks(
            string code, List<SpriteEntry> animated, List<SpriteEntry> allSprites)
        {
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetRoot();

            // Build ordinal map: each sprite's position among all sprites with same name
            var ordinalMap = new Dictionary<SpriteEntry, int>();
            var nameCounters = new Dictionary<string, int>();
            foreach (var sp in allSprites)
            {
                string key = sp.Type == SpriteEntryType.Text ? sp.Text : sp.SpriteName;
                if (string.IsNullOrEmpty(key)) continue;
                if (!nameCounters.TryGetValue(key, out int cnt)) cnt = 0;
                ordinalMap[sp] = cnt;
                nameCounters[key] = cnt + 1;
            }

            // Find animated sprites whose ordinal Add block is missing
            var missing = new List<SpriteEntry>();
            foreach (var sp in animated)
            {
                string name = sp.Type == SpriteEntryType.Text ? sp.Text : sp.SpriteName;
                if (string.IsNullOrEmpty(name)) continue;
                int ord = ordinalMap.TryGetValue(sp, out int o) ? o : 0;
                var exactNode = FindSpriteAddNode(root, name, ord);
                if (exactNode == null)
                {
                    // If any Add block for this sprite name already exists, do not
                    // synthesize another static block. Ordinal mismatches can occur
                    // between code and layout ordering and would otherwise duplicate sprites.
                    var anyNode = FindSpriteAddNode(root, name, 0);
                    if (anyNode != null)
                        continue;

                    missing.Add(sp);
                }
            }

            if (missing.Count == 0) return code;

            // Find insertion point: prefer frame.Dispose(), then the DrawFrame using-block's
            // closing brace (DrawLayout style), then end of Main() body.
            int insertPos = -1;
            string indent = "    ";

            // 1. Try frame.Dispose() — explicit disposal in PB scripts
            int disposeIdx = code.IndexOf("frame.Dispose()", StringComparison.Ordinal);
            if (disposeIdx >= 0)
            {
                insertPos = LineStartOf(code, disposeIdx);
                indent = GetIndent(code, disposeIdx);
            }

            // 2. Try the using block that contains DrawFrame() — DrawLayout style
            //    Insert before the using block's closing brace so the new Add block
            //    is correctly inside the frame scope.
            if (insertPos < 0)
            {
                var drawFrameUsing = root.DescendantNodes()
                    .OfType<UsingStatementSyntax>()
                    .FirstOrDefault(u =>
                        u.DescendantNodes()
                            .OfType<InvocationExpressionSyntax>()
                            .Any(inv =>
                                inv.Expression is MemberAccessExpressionSyntax ma &&
                                ma.Name.Identifier.Text == "DrawFrame"));

                if (drawFrameUsing?.Statement is BlockSyntax usingBlock)
                {
                    int closeBrace = usingBlock.CloseBraceToken.SpanStart;
                    insertPos = LineStartOf(code, closeBrace);
                    indent = GetIndent(code, closeBrace) + "    ";
                }
            }

            // 3. Try end of Main() body — fallback for scripts without DrawFrame usage
            if (insertPos < 0)
            {
                var mainMethod = root.DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.Text == "Main");
                if (mainMethod?.Body != null)
                {
                    int closeBrace = mainMethod.Body.CloseBraceToken.SpanStart;
                    insertPos = LineStartOf(code, closeBrace);
                    indent = GetIndent(code, closeBrace) + "    ";
                }
            }

            if (insertPos < 0) return code;

            // Generate static Add blocks for all missing sprites
            var sb = new StringBuilder();
            foreach (var sp in missing)
            {
                sb.AppendLine();
                sb.Append(GenerateStaticAddBlock(sp, indent));
            }

            return code.Insert(insertPos, sb.ToString());
        }

        /// <summary>
        /// Generates a static <c>frame.Add(new MySprite { … });</c> block for a sprite,
        /// using the sprite's current property values.
        /// </summary>
        private static string GenerateStaticAddBlock(SpriteEntry sp, string indent)
        {
            var sb = new StringBuilder();
            bool isText = sp.Type == SpriteEntryType.Text;
            string dataValue = isText ? sp.Text : sp.SpriteName;

            sb.AppendLine($"{indent}frame.Add(new MySprite");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    Type            = SpriteType.{(isText ? "TEXT" : "TEXTURE")},");
            sb.AppendLine($"{indent}    Data            = \"{dataValue}\",");
            sb.AppendLine($"{indent}    Position        = new Vector2({sp.X:F1}f, {sp.Y:F1}f),");
            sb.AppendLine($"{indent}    Size            = new Vector2({sp.Width:F1}f, {sp.Height:F1}f),");
            sb.AppendLine($"{indent}    Color           = new Color({sp.ColorR}, {sp.ColorG}, {sp.ColorB}, {sp.ColorA}),");
            sb.AppendLine($"{indent}    Alignment       = TextAlignment.CENTER,");
            sb.AppendLine($"{indent}    RotationOrScale = {sp.Rotation:F4}f,");
            sb.AppendLine($"{indent}}});");

            return sb.ToString();
        }
    }
}
