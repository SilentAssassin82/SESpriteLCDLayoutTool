using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Represents a numeric "knob" extracted from user code that the parameter
    /// inspector exposes for editing. A knob is either a class-level numeric
    /// constant/field, or a numeric literal that appears inside a render
    /// method body. Each knob carries the exact source span of the literal so
    /// edits can be applied back to the code box without reformatting the
    /// surrounding source.
    /// </summary>
    public sealed class RenderParameterKnob
    {
        public string Name { get; set; }
        public string Category { get; set; }
        /// <summary>
        /// Sub-grouping label (e.g. the local variable the literal feeds, or the
        /// enclosing call name like "MySprite.CreateText"). Used by the inspector
        /// to cluster related literals together. Falls back to Category.
        /// </summary>
        public string GroupKey { get; set; }
        public double OriginalValue { get; set; }
        public double CurrentValue { get; set; }
        public bool IsFloat { get; set; }
        /// <summary>Inclusive start offset of the literal text in the original source.</summary>
        public int LiteralStart { get; set; }
        /// <summary>Length of the literal text (e.g. "26f" = 3 chars).</summary>
        public int LiteralLength { get; set; }
        public string OriginalLiteral { get; set; }

        public string FormatLiteral(double value)
        {
            if (IsFloat)
            {
                string s = value.ToString("0.0############", CultureInfo.InvariantCulture);
                if (!s.Contains(".")) s += ".0";
                return s + "f";
            }
            return value.ToString("0", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// First-slice parameter scanner. Uses regex (not full Roslyn) to keep the
    /// initial implementation tight and dependency-free. Picks up:
    ///   • class-level numeric constants/fields (private const float PAD = 10f;)
    ///   • numeric literals inside a named render method body
    /// Returned knobs reference exact source spans so the inspector can patch
    /// the code box by simple string replacement.
    /// </summary>
    public static class RenderParameterScanner
    {
        // Matches: [modifiers] [const|readonly] (float|double|int) NAME = number[f|d|m]?;
        private static readonly Regex RxConstField = new Regex(
            @"(?:private|public|protected|internal|static|const|readonly|\s)+\s+(?<type>float|double|int)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<lit>-?\d+(?:\.\d+)?)(?<suffix>[fFdDmM]?)\s*;",
            RegexOptions.Compiled);

        // Matches numeric literals like 10f, 0.85f, 26, 0.5
        private static readonly Regex RxLiteral = new Regex(
            @"(?<![A-Za-z_0-9.])(?<lit>-?\d+(?:\.\d+)?)(?<suffix>[fFdD]?)(?![A-Za-z_0-9.])",
            RegexOptions.Compiled);

        /// <summary>
        /// Scans <paramref name="source"/> for class-level numeric constants and
        /// numeric literals inside the body of <paramref name="methodName"/>.
        /// If <paramref name="methodName"/> is null/empty, only constants are returned.
        /// </summary>
        public static List<RenderParameterKnob> Scan(string source, string methodName)
        {
            var knobs = new List<RenderParameterKnob>();
            if (string.IsNullOrEmpty(source)) return knobs;

            // Try Roslyn-based scan first; on any failure, fall back to regex.
            try
            {
                var roslyn = ScanWithRoslyn(source, methodName);
                if (roslyn != null) return roslyn;
            }
            catch { /* fall through to regex */ }

            return ScanWithRegex(source, methodName);
        }

        // ── Roslyn implementation ────────────────────────────────────────────────
        private static List<RenderParameterKnob> ScanWithRoslyn(string source, string methodName)
        {
            // Parse with the same preprocessor symbols the runtime uses, otherwise
            // anything inside #if TORCH / #if DEBUG blocks is dropped from the syntax
            // tree (so switch-case render bodies become invisible to the scanner).
            var parseOpts = new CSharpParseOptions(
                LanguageVersion.Latest,
                preprocessorSymbols: new[] { "TORCH", "DEBUG", "PULSAR" });
            var tree = CSharpSyntaxTree.ParseText(source, parseOpts);
            var root = tree.GetRoot();
            var knobs = new List<RenderParameterKnob>();

            // Class-level numeric fields/constants
            foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                string typeName = field.Declaration.Type.ToString();
                if (typeName != "float" && typeName != "double" && typeName != "int" &&
                    typeName != "Single" && typeName != "Double" && typeName != "Int32")
                    continue;

                bool isFloat = typeName != "int" && typeName != "Int32";
                foreach (var v in field.Declaration.Variables)
                {
                    if (v.Initializer == null) continue;
                    int litStart;
                    int litLen;
                    string litText;
                    double litVal;
                    bool litIsFloat;
                    if (!TryReadNumericLiteral(v.Initializer.Value, out litStart, out litLen, out litText, out litVal, out litIsFloat))
                        continue;

                    knobs.Add(new RenderParameterKnob
                    {
                        Name = v.Identifier.ValueText,
                        Category = "Constants",
                        GroupKey = "Constants",
                        OriginalValue = litVal,
                        CurrentValue = litVal,
                        IsFloat = isFloat || litIsFloat,
                        LiteralStart = litStart,
                        LiteralLength = litLen,
                        OriginalLiteral = litText,
                    });
                }
            }

            // Method-body literals
            if (!string.IsNullOrWhiteSpace(methodName))
            {
                // 1) Try a real method declaration with this name.
                SyntaxNode bodyNode = root.DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.ValueText == methodName)
                    ?.Body;

                // 2) Fall back to switch-case-synthesized "render" methods. The
                //    detection pipeline turns "case Foo:" into a virtual
                //    "RenderFoo" entry. If methodName starts with "Render", look
                //    for a matching case label and use that case's statements.
                if (bodyNode == null && methodName.StartsWith("Render", StringComparison.Ordinal) && methodName.Length > 6)
                {
                    string caseSuffix = methodName.Substring(6); // "Stat", "Header", ...
                    foreach (var sw in root.DescendantNodes().OfType<SwitchStatementSyntax>())
                    {
                        foreach (var section in sw.Sections)
                        {
                            bool matched = section.Labels.OfType<CaseSwitchLabelSyntax>().Any(lbl =>
                            {
                                var v = lbl.Value;
                                // case Stat: / case RowKind.Stat: / case Ns.RowKind.Stat:
                                if (v is IdentifierNameSyntax id && id.Identifier.ValueText == caseSuffix) return true;
                                if (v is MemberAccessExpressionSyntax ma && ma.Name.Identifier.ValueText == caseSuffix) return true;
                                return false;
                            });
                            if (matched)
                            {
                                bodyNode = section;
                                break;
                            }
                        }
                        if (bodyNode != null) break;
                    }
                }

                if (bodyNode != null)
                {
                    int idx = 1;
                    foreach (var lit in bodyNode.DescendantNodes().OfType<LiteralExpressionSyntax>())
                    {
                        if (!lit.IsKind(SyntaxKind.NumericLiteralExpression)) continue;

                        // Skip if inside a string/attribute/case-label context that we don't want.
                        if (lit.Ancestors().Any(a =>
                                a is AttributeSyntax ||
                                a is CaseSwitchLabelSyntax ||
                                a is BracketedArgumentListSyntax))
                            continue;

                        int litStart;
                        int litLen;
                        string litText;
                        double litVal;
                        bool litIsFloat;
                        if (!TryReadNumericLiteral(lit, out litStart, out litLen, out litText, out litVal, out litIsFloat))
                            continue;

                        // Skip 0/1 toggles
                        if (litText == "0" || litText == "1" || litText == "0f" || litText == "1f") continue;

                        string ctx = GetContextLabel(source, litStart);
                        string label = string.IsNullOrEmpty(ctx)
                            ? string.Format("#{0}: {1}", idx, litText)
                            : string.Format("#{0} {1} = {2}", idx, ctx, litText);

                        knobs.Add(new RenderParameterKnob
                        {
                            Name = label,
                            Category = methodName,
                            GroupKey = GetGroupKeyFromSyntax(lit, methodName),
                            OriginalValue = litVal,
                            CurrentValue = litVal,
                            IsFloat = litIsFloat,
                            LiteralStart = litStart,
                            LiteralLength = litLen,
                            OriginalLiteral = litText,
                        });
                        idx++;
                    }
                }
            }

            return knobs;
        }

        private static bool TryReadNumericLiteral(SyntaxNode node, out int start, out int length, out string text, out double value, out bool isFloat)
        {
            start = length = 0;
            text = null;
            value = 0;
            isFloat = false;

            LiteralExpressionSyntax lit = node as LiteralExpressionSyntax;
            bool negative = false;
            if (lit == null)
            {
                var prefix = node as PrefixUnaryExpressionSyntax;
                if (prefix != null && prefix.IsKind(SyntaxKind.UnaryMinusExpression))
                {
                    lit = prefix.Operand as LiteralExpressionSyntax;
                    negative = true;
                }
            }
            if (lit == null || !lit.IsKind(SyntaxKind.NumericLiteralExpression)) return false;

            string raw = lit.Token.Text;
            // Strip suffix
            string suffix = "";
            string numericPart = raw;
            if (raw.Length > 0)
            {
                char last = raw[raw.Length - 1];
                if (last == 'f' || last == 'F' || last == 'd' || last == 'D' || last == 'm' || last == 'M')
                {
                    suffix = last.ToString();
                    numericPart = raw.Substring(0, raw.Length - 1);
                }
            }
            double parsed;
            if (!double.TryParse(numericPart, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                return false;

            value = negative ? -parsed : parsed;
            isFloat = suffix == "f" || suffix == "F" || suffix == "d" || suffix == "D" || numericPart.Contains(".");
            text = raw;
            // Use the literal-token span (we don't include the unary minus in the patched span;
            // sign changes are handled by FormatLiteral emitting "-N" when needed).
            var span = lit.Token.Span;
            start = span.Start;
            length = span.Length;
            return true;
        }

        private static string GetGroupKeyFromSyntax(SyntaxNode lit, string fallback)
        {
            // Walk up looking for an assignment, local declarator, or invocation/object-creation.
            for (var node = lit.Parent; node != null; node = node.Parent)
            {
                var assign = node as AssignmentExpressionSyntax;
                if (assign != null) return assign.Left.ToString().Trim();

                var decl = node as VariableDeclaratorSyntax;
                if (decl != null) return decl.Identifier.ValueText;

                var inv = node as InvocationExpressionSyntax;
                if (inv != null) return inv.Expression.ToString().Trim();

                var oc = node as ObjectCreationExpressionSyntax;
                if (oc != null) return "new " + oc.Type.ToString().Trim();

                if (node is StatementSyntax) break;
            }
            return fallback ?? "(literals)";
        }

        // ── Regex fallback (legacy) ─────────────────────────────────────────────
        private static List<RenderParameterKnob> ScanWithRegex(string source, string methodName)
        {
            var knobs = new List<RenderParameterKnob>();
            if (string.IsNullOrEmpty(source)) return knobs;

            // ── Class-level numeric constants/fields ──
            foreach (Match m in RxConstField.Matches(source))
            {
                var litGrp = m.Groups["lit"];
                string suffix = m.Groups["suffix"].Value;
                string type = m.Groups["type"].Value;
                bool isFloat = type != "int" || suffix.Length > 0 && (suffix == "f" || suffix == "F" || suffix == "d" || suffix == "D");

                double val;
                if (!double.TryParse(litGrp.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out val))
                    continue;

                string fullLit = litGrp.Value + (suffix ?? "");
                knobs.Add(new RenderParameterKnob
                {
                    Name = m.Groups["name"].Value,
                    Category = "Constants",
                    GroupKey = "Constants",
                    OriginalValue = val,
                    CurrentValue = val,
                    IsFloat = isFloat,
                    LiteralStart = litGrp.Index,
                    LiteralLength = fullLit.Length,
                    OriginalLiteral = fullLit,
                });
            }

            // ── Literals inside the named method ──
            if (!string.IsNullOrWhiteSpace(methodName))
            {
                int bodyStart, bodyEnd;
                if (TryFindMethodBody(source, methodName, out bodyStart, out bodyEnd))
                {
                    int idx = 1;
                    foreach (Match m in RxLiteral.Matches(source))
                    {
                        if (m.Index < bodyStart || m.Index >= bodyEnd) continue;

                        var litGrp = m.Groups["lit"];
                        string suffix = m.Groups["suffix"].Value;
                        string fullLit = litGrp.Value + (suffix ?? "");
                        // Skip 0/1 toggles and array indexers — too noisy for the first slice.
                        if (fullLit == "0" || fullLit == "1") continue;
                        // Skip if literal is preceded by '[' (array index) — best-effort
                        if (m.Index > 0 && source[m.Index - 1] == '[') continue;

                        double val;
                        if (!double.TryParse(litGrp.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out val))
                            continue;

                        bool isFloat = suffix == "f" || suffix == "F" || suffix == "d" || suffix == "D" || litGrp.Value.Contains(".");

                        // Compute a short context label (preceding identifier/word) for clarity.
                        string ctx = GetContextLabel(source, m.Index);
                        string label = string.IsNullOrEmpty(ctx)
                            ? string.Format("#{0}: {1}", idx, fullLit)
                            : string.Format("#{0} {1} = {2}", idx, ctx, fullLit);

                        knobs.Add(new RenderParameterKnob
                        {
                            Name = label,
                            Category = methodName,
                            GroupKey = GetGroupKey(source, m.Index, methodName),
                            OriginalValue = val,
                            CurrentValue = val,
                            IsFloat = isFloat,
                            LiteralStart = litGrp.Index,
                            LiteralLength = fullLit.Length,
                            OriginalLiteral = fullLit,
                        });
                        idx++;
                    }
                }
            }

            return knobs;
        }

        /// <summary>Locates the brace span of a top-level method by name.</summary>
        public static bool TryFindMethodBody(string source, string methodName, out int bodyStart, out int bodyEnd)
        {
            bodyStart = bodyEnd = -1;
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(methodName)) return false;

            var rx = new Regex(@"\b" + Regex.Escape(methodName) + @"\s*\([^)]*\)\s*\{", RegexOptions.Singleline);
            Match m = rx.Match(source);
            if (!m.Success) return false;

            int open = m.Index + m.Length - 1; // position of '{'
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                char c = source[i];
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        bodyStart = open + 1;
                        bodyEnd = i;
                        return true;
                    }
                }
            }
            return false;
        }

        private static string GetContextLabel(string source, int litIndex)
        {
            // Walk backwards past whitespace/'(', ',', '*', '/', '+', '-', '=' to find a preceding identifier.
            int i = litIndex - 1;
            while (i >= 0 && " \t\r\n(,*+/-=".IndexOf(source[i]) >= 0) i--;
            int end = i + 1;
            while (i >= 0 && (char.IsLetterOrDigit(source[i]) || source[i] == '_' || source[i] == '.')) i--;
            int start = i + 1;
            if (end > start)
            {
                string s = source.Substring(start, end - start);
                if (s.Length > 24) s = s.Substring(s.Length - 24);
                return s;
            }
            return null;
        }

        /// <summary>
        /// Resolves a sub-group label for a literal inside a method body:
        ///   1. If the literal's line contains an assignment "name = ...",
        ///      group by the LHS identifier.
        ///   2. Otherwise scan back to the nearest enclosing call expression
        ///      and group by that call name (e.g. "MySprite.CreateText").
        ///   3. Fallback to the method name.
        /// </summary>
        private static string GetGroupKey(string source, int litIndex, string fallback)
        {
            int lineStart = source.LastIndexOf('\n', Math.Max(0, litIndex - 1)) + 1;
            int lineEnd = source.IndexOf('\n', litIndex);
            if (lineEnd < 0) lineEnd = source.Length;
            string line = source.Substring(lineStart, lineEnd - lineStart);

            var rxAssign = new Regex(@"^\s*(?:(?:float|double|int|var)\s+)?(?<lhs>[A-Za-z_][A-Za-z0-9_]*)\s*[+\-*/]?=\s*[^=]");
            var am = rxAssign.Match(line);
            if (am.Success)
            {
                string lhs = am.Groups["lhs"].Value;
                if (!string.IsNullOrEmpty(lhs)) return lhs;
            }

            int depth = 0;
            int limit = Math.Max(0, litIndex - 800);
            for (int i = litIndex - 1; i >= limit; i--)
            {
                char c = source[i];
                if (c == ')') depth++;
                else if (c == '(')
                {
                    if (depth == 0)
                    {
                        int end = i;
                        int j = i - 1;
                        while (j >= 0 && (char.IsLetterOrDigit(source[j]) || source[j] == '_' || source[j] == '.'))
                            j--;
                        int start = j + 1;
                        if (end > start)
                        {
                            string call = source.Substring(start, end - start);
                            if (!string.IsNullOrEmpty(call) && !char.IsDigit(call[0]))
                            {
                                if (call.Length > 32) call = call.Substring(call.Length - 32);
                                return call;
                            }
                        }
                        break;
                    }
                    depth--;
                }
                else if (c == '{' || c == '}' || c == ';')
                {
                    break;
                }
            }

            return fallback ?? "(literals)";
        }

        /// <summary>
        /// Applies all knob edits (whose CurrentValue differs from OriginalValue)
        /// back into <paramref name="source"/>. Edits are applied in descending
        /// offset order so earlier offsets remain valid.
        /// </summary>
        public static string ApplyEdits(string source, IList<RenderParameterKnob> knobs)
        {
            if (string.IsNullOrEmpty(source) || knobs == null || knobs.Count == 0) return source;

            // Sort by descending start offset
            var ordered = new List<RenderParameterKnob>(knobs);
            ordered.Sort((a, b) => b.LiteralStart.CompareTo(a.LiteralStart));

            var sb = new System.Text.StringBuilder(source);
            foreach (var k in ordered)
            {
                if (k.LiteralStart < 0 || k.LiteralStart + k.LiteralLength > sb.Length) continue;
                if (k.CurrentValue == k.OriginalValue) continue;
                string newLit = k.FormatLiteral(k.CurrentValue);
                sb.Remove(k.LiteralStart, k.LiteralLength);
                sb.Insert(k.LiteralStart, newLit);
            }
            return sb.ToString();
        }
    }
}
