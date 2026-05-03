using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

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
