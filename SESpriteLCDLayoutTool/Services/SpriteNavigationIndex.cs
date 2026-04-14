using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Roslyn-based index of every sprite-related literal, variable, and creation site
    /// in user source code.  Built once per code snapshot, the index answers
    /// "where in the code is this sprite's text/name?" via a dictionary lookup.
    ///
    /// Three passes:
    ///   1. Index ALL string literals (value → line/offset/method).
    ///   2. Detect "sprite-helper" methods (methods whose parameter feeds into
    ///      CreateText or new MySprite) and record which parameter carries the text.
    ///   3. Walk every call-site of those helpers and map the literal argument back
    ///      to the call line — so "POWER" maps to the DrawGauge(s, "POWER", …) line,
    ///      not the CreateText(label, …) line inside the helper.
    /// </summary>
    public sealed class SpriteNavigationIndex
    {
        // ── Entry ────────────────────────────────────────────────────────────

        public enum EntryKind
        {
            /// <summary>Literal directly inside CreateText("…") or new MySprite(…, "…").</summary>
            DirectSprite,
            /// <summary>Literal passed as an argument at the call-site of a sprite helper.</summary>
            CallSiteArg,
            /// <summary>Any other string literal in the code.</summary>
            Generic
        }

        public sealed class Entry
        {
            public string Value;       // e.g. "POWER"
            public int Line;           // 1-based
            public int CharOffset;     // from start of file
            public string Method;      // containing method, null if top-level
            public EntryKind Kind;

            public override string ToString()
            {
                string tag = Kind == EntryKind.DirectSprite ? "[sprite]"
                           : Kind == EntryKind.CallSiteArg ? "[call]"
                           : "[lit]";
                return $"  Line {Line,4}: {tag,-8} \"{Value}\"  (in {Method ?? "top-level"})";
            }
        }

        // ── Data ─────────────────────────────────────────────────────────────

        /// <summary>Value → all locations where that value appears as a string literal.</summary>
        private readonly Dictionary<string, List<Entry>> _byValue
            = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, List<Entry>> ByValue => _byValue;

        // ── Build ────────────────────────────────────────────────────────────

        public static SpriteNavigationIndex Build(string sourceCode)
        {
            var idx = new SpriteNavigationIndex();
            if (string.IsNullOrWhiteSpace(sourceCode)) return idx;

            try
            {
                var tree = CSharpSyntaxTree.ParseText(sourceCode);
                var root = tree.GetCompilationUnitRoot();

                // Pass 1 — detect helper methods whose parameter flows into sprite text.
                //   Returns e.g. { "DrawGauge" → 1 } meaning param index 1 is the text.
                var helperTextParam = DetectSpriteHelpers(root);

                // Pass 2 — index every string literal in the code.
                foreach (var lit in root.DescendantNodes()
                    .OfType<LiteralExpressionSyntax>()
                    .Where(n => n.IsKind(SyntaxKind.StringLiteralExpression)))
                {
                    string value = lit.Token.ValueText;
                    if (string.IsNullOrEmpty(value)) continue;

                    var lineSpan = lit.GetLocation().GetLineSpan();
                    var method = lit.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

                    var entry = new Entry
                    {
                        Value = value,
                        Line = lineSpan.StartLinePosition.Line + 1,
                        CharOffset = lit.SpanStart,
                        Method = method?.Identifier.Text,
                        Kind = ClassifyLiteral(lit, helperTextParam)
                    };

                    Add(idx._byValue, value, entry);
                }

                // Pass 3 — for every helper call-site, index the literal argument
                //   that feeds into the sprite text parameter.
                //   (Pass 2 already indexed ALL literals, but this pass RECLASSIFIES
                //    the ones at call sites as CallSiteArg so Lookup prefers them.)
                foreach (var kvp in helperTextParam)
                {
                    string helperName = kvp.Key;
                    int paramIdx = kvp.Value;

                    foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        if (CalledName(inv) != helperName) continue;
                        var args = inv.ArgumentList?.Arguments;
                        if (args == null || args.Value.Count <= paramIdx) continue;

                        var argExpr = args.Value[paramIdx].Expression;
                        if (argExpr is LiteralExpressionSyntax argLit
                            && argLit.IsKind(SyntaxKind.StringLiteralExpression))
                        {
                            string value = argLit.Token.ValueText;
                            if (string.IsNullOrEmpty(value)) continue;

                            // Upgrade any existing Generic entry for this literal to CallSiteArg
                            if (idx._byValue.TryGetValue(value, out var existing))
                            {
                                var match = existing.FirstOrDefault(e => e.CharOffset == argLit.SpanStart);
                                if (match != null)
                                {
                                    match.Kind = EntryKind.CallSiteArg;
                                    continue; // already indexed in Pass 2
                                }
                            }

                            // Not already indexed (shouldn't happen, but be safe)
                            var lineSpan = argLit.GetLocation().GetLineSpan();
                            var caller = inv.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                            Add(idx._byValue, value, new Entry
                            {
                                Value = value,
                                Line = lineSpan.StartLinePosition.Line + 1,
                                CharOffset = argLit.SpanStart,
                                Method = caller?.Identifier.Text,
                                Kind = EntryKind.CallSiteArg
                            });
                        }
                    }
                }

                // Pass 4 — switch/case blocks: find case label literals in call expressions
                //   For IML-style switch(row.RowKind) patterns, the sprite text comes from
                //   CapturedRows at runtime, but hardcoded literals like "!" inside case blocks
                //   should still be indexed as DirectSprite.
                foreach (var switchStmt in root.DescendantNodes().OfType<SwitchStatementSyntax>())
                {
                    foreach (var section in switchStmt.Sections)
                    {
                        // Find CreateText calls within this case section
                        foreach (var inv in section.DescendantNodes().OfType<InvocationExpressionSyntax>())
                        {
                            if (CalledName(inv) != "CreateText") continue;
                            var args = inv.ArgumentList?.Arguments;
                            if (args == null || args.Value.Count == 0) continue;

                            var textArg = args.Value[0].Expression;
                            if (textArg is LiteralExpressionSyntax caseLit
                                && caseLit.IsKind(SyntaxKind.StringLiteralExpression))
                            {
                                // Upgrade to DirectSprite
                                if (idx._byValue.TryGetValue(caseLit.Token.ValueText, out var existing))
                                {
                                    var match = existing.FirstOrDefault(e => e.CharOffset == caseLit.SpanStart);
                                    if (match != null)
                                        match.Kind = EntryKind.DirectSprite;
                                }
                            }
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[SpriteNavIndex] Built: {idx._byValue.Count} unique values, " +
                    $"{idx._byValue.Values.Sum(v => v.Count)} entries, " +
                    $"{helperTextParam.Count} helper method(s)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SpriteNavIndex] Build error: {ex.Message}");
            }

            return idx;
        }

        // ── Lookup ───────────────────────────────────────────────────────────

        /// <summary>
        /// Find the best source location for a sprite with the given text (or sprite name).
        /// Uses method name and index for disambiguation when multiple matches exist.
        /// </summary>
        public Entry Lookup(string text, string sourceMethodName = null, int sourceMethodIndex = -1)
        {
            if (string.IsNullOrEmpty(text)) return null;

            List<Entry> entries;
            if (!_byValue.TryGetValue(text, out entries) || entries.Count == 0)
                return null;

            // 1) Unique literal — THE answer, no ambiguity.
            if (entries.Count == 1)
                return entries[0];

            // 2) Prefer sprite-related entries (DirectSprite or CallSiteArg).
            var spriteEntries = entries.Where(e => e.Kind != EntryKind.Generic).ToList();
            if (spriteEntries.Count == 1)
                return spriteEntries[0];

            // 3) Prefer CallSiteArg (the "POWER" at DrawGauge call-site).
            var callSites = entries.Where(e => e.Kind == EntryKind.CallSiteArg).ToList();
            if (callSites.Count == 1)
                return callSites[0];

            // 4) Prefer DirectSprite.
            var directSprites = entries.Where(e => e.Kind == EntryKind.DirectSprite).ToList();
            if (directSprites.Count == 1)
                return directSprites[0];

            // 5) DISAMBIGUATION: Use method name to narrow down.
            //    The runtime tells us which method created this sprite.
            if (!string.IsNullOrEmpty(sourceMethodName))
            {
                var inMethod = entries.Where(e => e.Method == sourceMethodName).ToList();

                // Fallback: PB scripts wrap top-level code into generated methods
                // (e.g. Render0) at runtime, so the Roslyn index stores Method=null
                // but the runtime reports the generated wrapper name.  Treat top-level
                // entries as belonging to the requested method when no direct match.
                if (inMethod.Count == 0)
                    inMethod = entries.Where(e => e.Method == null).ToList();

                if (inMethod.Count == 1)
                    return inMethod[0];

                // Still multiple in same method — pick Nth occurrence (by source order)
                if (inMethod.Count > 1 && sourceMethodIndex >= 0 && sourceMethodIndex < inMethod.Count)
                {
                    var sorted = inMethod.OrderBy(e => e.CharOffset).ToList();
                    return sorted[sourceMethodIndex];
                }

                // Also try call-site args in the calling method
                var callInMethod = entries.Where(e => e.Kind == EntryKind.CallSiteArg && e.Method == sourceMethodName).ToList();
                if (callInMethod.Count == 0)
                    callInMethod = entries.Where(e => e.Kind == EntryKind.CallSiteArg && e.Method == null).ToList();
                if (callInMethod.Count == 1)
                    return callInMethod[0];
            }

            // 6) Truly ambiguous — return null.
            System.Diagnostics.Debug.WriteLine(
                $"[SpriteNavIndex] Lookup \"{text}\": {entries.Count} entries, method='{sourceMethodName}', idx={sourceMethodIndex} — ambiguous → null");
            return null;
        }

        // ── Dump (debugging / temp file) ─────────────────────────────────────

        /// <summary>
        /// Writes a human-readable tree of the entire index, grouped by method.
        /// </summary>
        public string Dump()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("╔══════════════════════════════════════════════════════════╗");
            sb.AppendLine("║        SPRITE NAVIGATION INDEX  (Roslyn)                ║");
            sb.AppendLine("╚══════════════════════════════════════════════════════════╝");
            sb.AppendLine();

            var all = _byValue.Values.SelectMany(v => v)
                .OrderBy(e => e.CharOffset)
                .ToList();

            if (all.Count == 0)
            {
                sb.AppendLine("  (empty — no string literals found)");
                return sb.ToString();
            }

            var grouped = all.GroupBy(e => e.Method ?? "(top-level)")
                             .OrderBy(g => g.Min(e => e.CharOffset));

            foreach (var group in grouped)
            {
                sb.AppendLine($"┌─ {group.Key}()");
                foreach (var entry in group.OrderBy(e => e.CharOffset))
                {
                    string tag = entry.Kind == EntryKind.DirectSprite ? "[sprite]"
                               : entry.Kind == EntryKind.CallSiteArg ? "[call]  "
                               : "[lit]   ";
                    sb.AppendLine($"│  Line {entry.Line,4}: {tag} \"{Truncate(entry.Value, 40)}\"");
                }
                sb.AppendLine("└───");
                sb.AppendLine();
            }

            // Summary
            int sprites = all.Count(e => e.Kind == EntryKind.DirectSprite);
            int calls = all.Count(e => e.Kind == EntryKind.CallSiteArg);
            int generics = all.Count(e => e.Kind == EntryKind.Generic);
            sb.AppendLine($"Total: {all.Count} entries ({sprites} sprite, {calls} call-site, {generics} generic)");
            sb.AppendLine($"Unique values: {_byValue.Count}");

            return sb.ToString();
        }

        // ── Internals ────────────────────────────────────────────────────────

        /// <summary>
        /// Finds methods that create sprites using a parameter (not a literal) as the
        /// text/name argument, and returns the parameter index.
        /// e.g. DrawGauge(List&lt;MySprite&gt; s, string label, …) where
        /// CreateText(label, …) → returns { "DrawGauge", 1 }.
        /// </summary>
        private static Dictionary<string, int> DetectSpriteHelpers(CompilationUnitSyntax root)
        {
            var result = new Dictionary<string, int>();

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var paramList = method.ParameterList?.Parameters;
                if (paramList == null || paramList.Value.Count == 0) continue;

                // Build a fast set of parameter names → index
                var paramIndex = new Dictionary<string, int>();
                for (int i = 0; i < paramList.Value.Count; i++)
                    paramIndex[paramList.Value[i].Identifier.Text] = i;

                // Check CreateText(identifier, …) calls
                foreach (var inv in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (CalledName(inv) != "CreateText") continue;
                    var args = inv.ArgumentList?.Arguments;
                    if (args == null || args.Value.Count == 0) continue;

                    if (args.Value[0].Expression is IdentifierNameSyntax id
                        && paramIndex.TryGetValue(id.Identifier.Text, out int idx))
                    {
                        string mName = method.Identifier.Text;
                        if (!result.ContainsKey(mName))
                        {
                            result[mName] = idx;
                            System.Diagnostics.Debug.WriteLine(
                                $"[SpriteNavIndex] Detected helper: {mName}() — param[{idx}] '{id.Identifier.Text}' feeds CreateText");
                        }
                    }
                }

                // Check new MySprite(SpriteType.*, identifier, …) calls
                foreach (var creation in method.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
                {
                    if (!creation.Type.ToString().Contains("MySprite")) continue;
                    var args = creation.ArgumentList?.Arguments;
                    if (args == null || args.Value.Count < 2) continue;

                    if (args.Value[1].Expression is IdentifierNameSyntax id
                        && paramIndex.TryGetValue(id.Identifier.Text, out int idx))
                    {
                        string mName = method.Identifier.Text;
                        if (!result.ContainsKey(mName))
                        {
                            result[mName] = idx;
                            System.Diagnostics.Debug.WriteLine(
                                $"[SpriteNavIndex] Detected helper: {mName}() — param[{idx}] '{id.Identifier.Text}' feeds new MySprite");
                        }
                    }
                }
            }

            return result;
        }

        private static EntryKind ClassifyLiteral(LiteralExpressionSyntax literal, Dictionary<string, int> helpers)
        {
            // Walk up the parent chain looking for sprite-related context
            SyntaxNode node = literal;
            while (node != null)
            {
                if (node is ArgumentSyntax)
                {
                    var argList = node.Parent as ArgumentListSyntax;
                    var parentExpr = argList?.Parent;

                    if (parentExpr is InvocationExpressionSyntax inv)
                    {
                        string name = CalledName(inv);
                        if (name == "CreateText") return EntryKind.DirectSprite;
                        if (name != null && helpers.ContainsKey(name)) return EntryKind.CallSiteArg;
                    }
                    if (parentExpr is ObjectCreationExpressionSyntax oc
                        && oc.Type.ToString().Contains("MySprite"))
                        return EntryKind.DirectSprite;

                    break; // don't walk past the argument boundary
                }
                node = node.Parent;
            }
            return EntryKind.Generic;
        }

        private static string CalledName(InvocationExpressionSyntax inv)
        {
            if (inv.Expression is MemberAccessExpressionSyntax member)
                return member.Name.Identifier.Text;
            if (inv.Expression is IdentifierNameSyntax id)
                return id.Identifier.Text;
            return null;
        }

        private static void Add(Dictionary<string, List<Entry>> map, string key, Entry entry)
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<Entry>();
                map[key] = list;
            }
            // Avoid duplicate entries at the same offset (Pass 2 + Pass 3 overlap)
            if (!list.Any(e => e.CharOffset == entry.CharOffset))
                list.Add(entry);
        }

        private static string Truncate(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max - 3) + "...";
        }
    }
}
