using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Applies C# syntax highlighting to a <see cref="RichTextBox"/> using the
    /// Roslyn tokenizer already referenced by this project.
    /// Preserves <see cref="RichTextBox.SelectionBackColor"/> so the code heatmap
    /// overlay continues to work after highlighting is applied.
    /// </summary>
    internal static class SyntaxHighlighter
    {
        private sealed class DiagnosticMarker
        {
            public int Start;
            public int Length;
            public DiagnosticSeverity Severity;
            public string Id;
            public string Message;
        }

        private static readonly ConditionalWeakTable<RichTextBox, List<DiagnosticMarker>> _diagnosticCache =
            new ConditionalWeakTable<RichTextBox, List<DiagnosticMarker>>();

        private static readonly object _semanticRefLock = new object();
        private static List<MetadataReference> _semanticReferences;

        // ── Colour palette (VS Dark / VS Code-inspired) ───────────────────────
        private static readonly Color ColDefault  = Color.FromArgb(212, 212, 212); // light grey
        private static readonly Color ColKeyword  = Color.FromArgb(86,  156, 214); // blue
        private static readonly Color ColControl  = Color.FromArgb(197, 134, 192); // pink-purple (control-flow)
        private static readonly Color ColType     = Color.FromArgb(78,  201, 176); // teal
        private static readonly Color ColString   = Color.FromArgb(206, 145, 120); // orange-brown
        private static readonly Color ColComment  = Color.FromArgb(106, 153,  85); // green
        private static readonly Color ColNumber   = Color.FromArgb(181, 206, 168); // light green
        private static readonly Color ColPreproc  = Color.FromArgb(155, 155, 155); // grey
        private static readonly Color ColDisabled = Color.FromArgb(100, 100, 100); // dark grey (inactive #if branch)

        // Preprocessor symbols defined when parsing — covers all SE plugin variants.
        // Using Regular mode so Roslyn tokenises class/namespace code correctly.
        // TORCH is listed first so #if TORCH blocks are treated as the active branch.
        private static readonly CSharpParseOptions _parseOptions = new CSharpParseOptions(
            kind: SourceCodeKind.Regular,
            preprocessorSymbols: new[] { "TORCH", "STABLE", "DEBUG", "RELEASE" });

        // Control-flow keywords get a distinct colour so they stand out.
        private static readonly System.Collections.Generic.HashSet<SyntaxKind> _controlFlow =
            new System.Collections.Generic.HashSet<SyntaxKind>
            {
                SyntaxKind.IfKeyword,     SyntaxKind.ElseKeyword,
                SyntaxKind.ForKeyword,    SyntaxKind.ForEachKeyword,
                SyntaxKind.WhileKeyword,  SyntaxKind.DoKeyword,
                SyntaxKind.SwitchKeyword, SyntaxKind.CaseKeyword,
                SyntaxKind.DefaultKeyword,
                SyntaxKind.BreakKeyword,  SyntaxKind.ContinueKeyword,
                SyntaxKind.ReturnKeyword, SyntaxKind.ThrowKeyword,
                SyntaxKind.TryKeyword,    SyntaxKind.CatchKeyword,
                SyntaxKind.FinallyKeyword,
                SyntaxKind.GotoKeyword,
            };

        /// <summary>
        /// Applies syntax colouring to <paramref name="rtb"/>.
        /// Call this after setting the text; it preserves scroll position and
        /// selection, and avoids touching <see cref="RichTextBox.SelectionBackColor"/>
        /// so heatmap backgrounds are untouched.
        /// </summary>
        public static void Highlight(RichTextBox rtb)
        {
            if (rtb == null) return;

            string source = rtb.Text;
            if (string.IsNullOrEmpty(source))
            {
                SetDiagnosticCache(rtb, new List<DiagnosticMarker>());
                return;
            }

            // Parse as Regular (not Script) so class/namespace structure is valid.
            // Preprocessor symbols are defined so all #if branches are included in
            // the token stream — #if TORCH blocks are never dropped as disabled trivia.
            SyntaxTree tree = CSharpSyntaxTree.ParseText(source, _parseOptions);

            int selStart  = rtb.SelectionStart;
            int selLength = rtb.SelectionLength;
            var scrollPos = rtb.GetScrollPos();

            rtb.SuspendLayout();
            rtb.BeginUpdate();

            try
            {
                // Reset all text to the default colour in one pass.
                rtb.SelectAll();
                rtb.SelectionColor = ColDefault;

                // Clear stale diagnostic underline formatting from previous passes.
                // RichTextBox keeps underline char-format until explicitly reset.
                NativeMethods.ClearDiagnosticUnderline(rtb);

                // Walk every token in document order, colouring leading trivia,
                // the token itself, then trailing trivia.
                foreach (SyntaxToken token in tree.GetRoot().DescendantTokens(descendIntoTrivia: true))
                {
                    foreach (SyntaxTrivia trivia in token.LeadingTrivia)
                        ApplyTrivia(rtb, trivia);

                    int start = token.SpanStart;
                    int len   = token.Span.Length;
                    if (len > 0)
                    {
                        Color col = ClassifyToken(token);
                        if (col != ColDefault)
                        {
                            rtb.Select(start, len);
                            rtb.SelectionColor = col;
                        }
                    }

                    foreach (SyntaxTrivia trivia in token.TrailingTrivia)
                        ApplyTrivia(rtb, trivia);
                }

                // ── Diagnostic underlines (errors + warnings + info) ─────────
                // SE PB scripts write class members at file scope (no "class Program"
                // wrapper) — Roslyn Regular mode reports these as syntax errors even
                // though they're valid PB code.  Wrap bare scripts in a dummy class
                // so the diagnostic tree sees them as valid member declarations.
                // When no wrapping is needed we reuse the already-parsed tree (free).
                const string diagPrefix = "class __D__ {\n";
                // Any file-scope code without a class declaration (PB Main, LCD helper methods,
                // generated templates) needs wrapping so Roslyn doesn't flag member declarations
                // at file scope as syntax errors.
                bool isBareScript = source.IndexOf("class ", StringComparison.Ordinal) < 0;
                SyntaxTree diagTree;
                int diagOffset;
                if (isBareScript)
                {
                    diagTree   = CSharpSyntaxTree.ParseText(diagPrefix + source + "\n}", _parseOptions);
                    diagOffset = diagPrefix.Length;
                }
                else
                {
                    diagTree   = tree;   // reuse, zero cost
                    diagOffset = 0;
                }

                // Cap total underlines so large broken files don't stall the UI thread.
                const int maxSquiggles = 80;
                int squiggleCount = 0;
                var markers = new List<DiagnosticMarker>();
                var diagnostics = diagTree.GetDiagnostics()
                    .Concat(GetSemanticDiagnostics(diagTree))
                    .Where(d => d.Location != null && d.Location.IsInSource)
                    .GroupBy(d => new
                    {
                        Start = d.Location.SourceSpan.Start,
                        Length = d.Location.SourceSpan.Length,
                        d.Id,
                        d.Severity,
                    })
                    .Select(g => g.First())
                    .OrderByDescending(d => d.Severity)
                    .ThenBy(d => d.Location.SourceSpan.Start);

                foreach (var diag in diagnostics)
                {
                    if (diag.Severity != DiagnosticSeverity.Error &&
                        diag.Severity != DiagnosticSeverity.Warning &&
                        diag.Severity != DiagnosticSeverity.Info)
                        continue;

                    if (++squiggleCount > maxSquiggles) break;

                    var span    = diag.Location.SourceSpan;
                    int eStart  = span.Start - diagOffset;
                    int eLen    = Math.Max(1, span.Length);
                    if (eStart < 0 || eStart >= rtb.TextLength) continue;
                    eLen = Math.Min(eLen, rtb.TextLength - eStart);

                    markers.Add(new DiagnosticMarker
                    {
                        Start = eStart,
                        Length = eLen,
                        Severity = diag.Severity,
                        Id = diag.Id,
                        Message = BuildDiagnosticMessage(diag, diagTree),
                    });

                    rtb.Select(eStart, eLen);
                    NativeMethods.ApplyDiagnosticUnderline(rtb, diag.Severity);
                }

                SetDiagnosticCache(rtb, markers);
            }
            finally
            {
                rtb.EndUpdate();
                rtb.ResumeLayout();

                // Clear the native undo buffer so formatting changes don't
                // pollute Ctrl+Z — we use a custom undo stack for text edits.
                NativeMethods.SendMessage(rtb.Handle, NativeMethods.EM_EMPTYUNDOBUFFER, IntPtr.Zero, IntPtr.Zero);

                if (selStart >= 0 && selStart <= rtb.TextLength)
                    rtb.Select(selStart, selLength);

                // Keep viewport stable after recolouring; restoring selection alone
                // can still snap the control to caret/top in some RichEdit builds.
                rtb.SetScrollPos(scrollPos);
            }
        }

        /// <summary>
        /// Returns tooltip text for the diagnostic (if any) covering <paramref name="charIndex"/>.
        /// </summary>
        public static bool TryGetDiagnosticTooltip(RichTextBox rtb, int charIndex, out string tooltipText)
        {
            tooltipText = null;
            if (rtb == null || charIndex < 0) return false;

            if (!_diagnosticCache.TryGetValue(rtb, out var markers) || markers == null || markers.Count == 0)
                return false;

            DiagnosticMarker best = null;
            foreach (var m in markers)
            {
                if (charIndex < m.Start || charIndex >= m.Start + m.Length) continue;

                if (best == null)
                {
                    best = m;
                    continue;
                }

                // Prefer higher severity, then tighter span for overlapping diagnostics.
                if (m.Severity > best.Severity ||
                    (m.Severity == best.Severity && m.Length < best.Length))
                {
                    best = m;
                }
            }

            if (best == null) return false;

            string sev = best.Severity.ToString().ToUpperInvariant();
            tooltipText = string.IsNullOrWhiteSpace(best.Id)
                ? $"{sev}: {best.Message}"
                : $"{sev} {best.Id}: {best.Message}";
            return true;
        }

        /// <summary>
        /// Clears cached diagnostics for a RichTextBox. Useful while text is being edited
        /// before the next debounced highlight pass runs.
        /// </summary>
        public static void ClearDiagnosticCache(RichTextBox rtb)
        {
            SetDiagnosticCache(rtb, new List<DiagnosticMarker>());
        }

        private static void SetDiagnosticCache(RichTextBox rtb, List<DiagnosticMarker> markers)
        {
            if (rtb == null) return;

            if (_diagnosticCache.TryGetValue(rtb, out var existing))
            {
                existing.Clear();
                if (markers != null) existing.AddRange(markers);
                return;
            }

            _diagnosticCache.Add(rtb, markers ?? new List<DiagnosticMarker>());
        }

        // Semantic diagnostics are used for editor-only typo feedback such as
        // "name does not exist in the current context" (CS0103).
        private static IEnumerable<Diagnostic> GetSemanticDiagnostics(SyntaxTree tree)
        {
            try
            {
                var refs = GetSemanticReferences();
                if (refs == null || refs.Count == 0)
                    return Enumerable.Empty<Diagnostic>();

                var compilation = CSharpCompilation.Create(
                    assemblyName: "SESpriteLCDLayoutTool.EditorAnalysis",
                    syntaxTrees: new[] { tree },
                    references: refs,
                    options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                return compilation.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Where(d => d.Id == "CS0103")
                    .Where(d => d.Location != null && d.Location.IsInSource)
                    .Where(d => HasTypoSuggestion(d, tree))
                    .ToList();
            }
            catch
            {
                // Diagnostics are a best-effort editor aid. If semantic analysis
                // fails in this environment, keep syntax highlighting functional.
                return Enumerable.Empty<Diagnostic>();
            }
        }

        private static List<MetadataReference> GetSemanticReferences()
        {
            if (_semanticReferences != null)
                return _semanticReferences;

            lock (_semanticRefLock)
            {
                if (_semanticReferences != null)
                    return _semanticReferences;

                var refs = new List<MetadataReference>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm == null || asm.IsDynamic) continue;
                    string loc = null;
                    try { loc = asm.Location; }
                    catch { continue; }
                    if (string.IsNullOrWhiteSpace(loc)) continue;
                    if (!seen.Add(loc)) continue;

                    try { refs.Add(MetadataReference.CreateFromFile(loc)); }
                    catch { /* ignore assemblies Roslyn cannot load as metadata refs */ }
                }

                _semanticReferences = refs;
                return _semanticReferences;
            }
        }

        private static string BuildDiagnosticMessage(Diagnostic diag, SyntaxTree tree)
        {
            string msg = diag.GetMessage();
            if (diag == null || tree == null) return msg;

            if (diag.Id != "CS0103" || diag.Location == null || !diag.Location.IsInSource)
                return msg;

            if (!TryGetUndefinedNameSuggestion(diag, tree, out _, out string suggestion))
                return msg;

            return msg + " Did you mean '" + suggestion + "'?";
        }

        private static bool HasTypoSuggestion(Diagnostic diag, SyntaxTree tree)
        {
            if (!TryGetUndefinedNameSuggestion(diag, tree, out string missing, out _))
                return false;

            if (IsLikelyImplicitSeSymbol(missing))
                return false;

            return true;
        }

        private static bool TryGetUndefinedNameSuggestion(
            Diagnostic diag,
            SyntaxTree tree,
            out string missing,
            out string suggestion)
        {
            missing = null;
            suggestion = null;

            if (diag == null || tree == null)
                return false;

            if (diag.Id != "CS0103" || diag.Location == null || !diag.Location.IsInSource)
                return false;

            var span = diag.Location.SourceSpan;
            var text = tree.GetText().ToString();
            if (span.Start < 0 || span.Start >= text.Length || span.Length <= 0)
                return false;

            int len = Math.Min(span.Length, text.Length - span.Start);
            missing = text.Substring(span.Start, len).Trim();
            if (string.IsNullOrWhiteSpace(missing))
                return false;

            suggestion = FindClosestIdentifierSuggestion(tree, missing);
            return !string.IsNullOrEmpty(suggestion);
        }

        // Symbols that are often implicitly available in SE/PB/Mod/Torch/Pulsar
        // execution contexts and can appear unresolved in lightweight editor-only
        // semantic analysis when the host assemblies are not loaded as metadata refs.
        private static bool IsLikelyImplicitSeSymbol(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;

            switch (name)
            {
                // PB / shared sprite API
                case "SpriteType":
                case "TextAlignment":
                case "ContentType":
                case "UpdateType":
                case "UpdateFrequency":
                case "IMyTextSurface":
                case "IMyTextPanel":
                case "MySprite":
                case "Vector2":
                case "Color":
                // Common SE / mod / plugin namespace roots
                case "VRage":
                case "VRageMath":
                case "Sandbox":
                case "Torch":
                case "Pulsar":
                // Common framework types seen in mod / Torch / Pulsar code
                case "MyLog":
                case "MyAPIGateway":
                case "MySessionComponentBase":
                case "TorchPluginBase":
                case "IPlugin":
                case "RenderContext":
                case "LcdSpriteRow":
                case "IMyCubeGrid":
                case "IMyTerminalBlock":
                case "IMyInventory":
                case "IMyInventoryItem":
                case "MyInventoryItem":
                case "MyItemType":
                    return true;
                default:
                    return false;
            }
        }

        private static string FindClosestIdentifierSuggestion(SyntaxTree tree, string missing)
        {
            if (tree == null || string.IsNullOrWhiteSpace(missing))
                return null;

            var root = tree.GetRoot();
            var candidates = root.DescendantTokens()
                .Where(t => t.IsKind(SyntaxKind.IdentifierToken))
                .Select(t => t.ValueText)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(s => !string.Equals(s, missing, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 0)
                return null;

            int maxDistance = missing.Length <= 4 ? 1 : (missing.Length <= 8 ? 2 : 3);

            string best = null;
            int bestDist = int.MaxValue;
            int bestLenDelta = int.MaxValue;

            foreach (var c in candidates)
            {
                int d = LevenshteinDistanceIgnoreCase(missing, c);
                if (d > maxDistance) continue;

                int lenDelta = Math.Abs(c.Length - missing.Length);
                if (d < bestDist || (d == bestDist && lenDelta < bestLenDelta))
                {
                    best = c;
                    bestDist = d;
                    bestLenDelta = lenDelta;
                }
            }

            return best;
        }

        private static int LevenshteinDistanceIgnoreCase(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return string.IsNullOrEmpty(b) ? 0 : b.Length;
            if (string.IsNullOrEmpty(b)) return a.Length;

            a = a.ToLowerInvariant();
            b = b.ToLowerInvariant();

            int[,] dp = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) dp[0, j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    dp[i, j] = Math.Min(
                        Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                        dp[i - 1, j - 1] + cost);
                }
            }

            return dp[a.Length, b.Length];
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static void ApplyTrivia(RichTextBox rtb, SyntaxTrivia trivia)
        {
            int start = trivia.SpanStart;
            int len   = trivia.Span.Length;
            if (len == 0) return;

            Color? col = ClassifyTrivia(trivia);
            if (col.HasValue)
            {
                rtb.Select(start, len);
                rtb.SelectionColor = col.Value;
            }
        }

        private static Color ClassifyToken(SyntaxToken token)
        {
            SyntaxKind kind = token.Kind();

            switch (kind)
            {
                // ── String / character / interpolated literals ────────────────
                case SyntaxKind.StringLiteralToken:
                case SyntaxKind.InterpolatedStringStartToken:
                case SyntaxKind.InterpolatedStringEndToken:
                case SyntaxKind.InterpolatedStringTextToken:
                case SyntaxKind.InterpolatedVerbatimStringStartToken:
                case SyntaxKind.CharacterLiteralToken:
                case SyntaxKind.MultiLineRawStringLiteralToken:
                case SyntaxKind.SingleLineRawStringLiteralToken:
                case SyntaxKind.Utf8StringLiteralToken:
                    return ColString;

                // ── Numeric literals ──────────────────────────────────────────
                case SyntaxKind.NumericLiteralToken:
                    return ColNumber;

                // ── Identifiers: colour type-position names ───────────────────
                case SyntaxKind.IdentifierToken:
                    return ColourIdentifier(token);
            }

            // ── Keywords ──────────────────────────────────────────────────────
            if (SyntaxFacts.IsKeywordKind(kind))
                return _controlFlow.Contains(kind) ? ColControl : ColKeyword;

            return ColDefault;
        }

        private static Color ColourIdentifier(SyntaxToken token)
        {
            SyntaxNode parent = token.Parent;
            if (parent == null) return ColDefault;

            switch (parent.Kind())
            {
                // Type declaration names: class Foo, struct Foo, enum Foo, etc.
                case SyntaxKind.ClassDeclaration:
                case SyntaxKind.StructDeclaration:
                case SyntaxKind.EnumDeclaration:
                case SyntaxKind.InterfaceDeclaration:
                case SyntaxKind.DelegateDeclaration:
                case SyntaxKind.RecordDeclaration:
                    var decl = parent as BaseTypeDeclarationSyntax;
                    if (decl != null && decl.Identifier == token) return ColType;
                    break;

                // Identifiers used as type names in variable declarations,
                // parameters, generics, casts, inheritance lists, etc.
                case SyntaxKind.IdentifierName:
                    SyntaxNode gp = parent.Parent;
                    if (gp == null) break;
                    switch (gp.Kind())
                    {
                        case SyntaxKind.VariableDeclaration:
                        case SyntaxKind.Parameter:
                        case SyntaxKind.ObjectCreationExpression:
                        case SyntaxKind.BaseList:
                        case SyntaxKind.TypeArgumentList:
                        case SyntaxKind.CastExpression:
                        case SyntaxKind.TypeOfExpression:
                        case SyntaxKind.IsPatternExpression:
                        case SyntaxKind.AsExpression:
                        case SyntaxKind.IsExpression:
                        case SyntaxKind.ArrayType:
                        case SyntaxKind.NullableType:
                        case SyntaxKind.PointerType:
                        case SyntaxKind.QualifiedName:
                            return ColType;

                        // PascalCase identifier on left side of member access
                        // (e.g. SpriteType.TEXTURE, Math.Cos, ContentType.SCRIPT)
                        case SyntaxKind.SimpleMemberAccessExpression:
                            var mae = gp as MemberAccessExpressionSyntax;
                            if (mae != null && mae.Expression == parent && IsPascalCase(token.Text))
                                return ColType;
                            break;
                    }
                    break;
            }

            return ColDefault;
        }

        /// <summary>
        /// Returns true if the identifier starts with an uppercase letter and
        /// contains at least one lowercase letter (heuristic for PascalCase type names).
        /// Single-char names like "I" or all-caps like "PI" return false to avoid
        /// false positives on constants and loop variables.
        /// </summary>
        private static bool IsPascalCase(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 2) return false;
            if (!char.IsUpper(name[0])) return false;
            for (int i = 1; i < name.Length; i++)
                if (char.IsLower(name[i])) return true;
            return false; // all-caps like "PI" — probably a constant, not a type
        }

        private static Color? ClassifyTrivia(SyntaxTrivia trivia)
        {
            switch (trivia.Kind())
            {
                // ── Comments ──────────────────────────────────────────────────
                case SyntaxKind.SingleLineCommentTrivia:
                case SyntaxKind.MultiLineCommentTrivia:
                case SyntaxKind.SingleLineDocumentationCommentTrivia:
                case SyntaxKind.MultiLineDocumentationCommentTrivia:
                case SyntaxKind.DocumentationCommentExteriorTrivia:
                case SyntaxKind.XmlComment:
                    return ColComment;

                // ── Preprocessor directives ───────────────────────────────────
                case SyntaxKind.IfDirectiveTrivia:
                case SyntaxKind.ElseDirectiveTrivia:
                case SyntaxKind.ElifDirectiveTrivia:
                case SyntaxKind.EndIfDirectiveTrivia:
                case SyntaxKind.RegionDirectiveTrivia:
                case SyntaxKind.EndRegionDirectiveTrivia:
                case SyntaxKind.DefineDirectiveTrivia:
                case SyntaxKind.UndefDirectiveTrivia:
                case SyntaxKind.PragmaWarningDirectiveTrivia:
                case SyntaxKind.PragmaChecksumDirectiveTrivia:
                case SyntaxKind.ReferenceDirectiveTrivia:
                case SyntaxKind.LoadDirectiveTrivia:
                    return ColPreproc;

                // ── Inactive #if branch (e.g. #else block when #if TORCH is active)
                case SyntaxKind.DisabledTextTrivia:
                    return ColDisabled;

                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Extension helper that wraps WM_SETREDRAW for bulk RichTextBox updates
    /// without triggering a WM_PAINT on every character colour change.
    /// </summary>
    internal static class RichTextBoxExtensions
    {
        private const int WM_SETREDRAW = 0x000B;
        private const int WM_USER = 0x0400;
        private const int EM_GETSCROLLPOS = WM_USER + 221;
        private const int EM_SETSCROLLPOS = WM_USER + 222;

        public static void BeginUpdate(this RichTextBox rtb)
        {
            NativeMethods.SendMessage(rtb.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        }

        public static void EndUpdate(this RichTextBox rtb)
        {
            NativeMethods.SendMessage(rtb.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
            rtb.Invalidate();
        }

        public static Point GetScrollPos(this RichTextBox rtb)
        {
            var pt = new Point();
            NativeMethods.SendMessage(rtb.Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref pt);
            return pt;
        }

        public static void SetScrollPos(this RichTextBox rtb, Point pos)
        {
            NativeMethods.SendMessage(rtb.Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref pos);
        }
    }

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref Point lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref CHARFORMAT2 lParam);

        internal const int EM_EMPTYUNDOBUFFER = 0x00CD;

        // ── EM_SETCHARFORMAT ─────────────────────────────────────────────────
        internal const int  EM_SETCHARFORMAT    = 0x0444;
        internal const int  SCF_SELECTION       = 0x0001;

        internal const uint CFM_UNDERLINE       = 0x00000004;
        internal const uint CFM_UNDERLINETYPE   = 0x00800000;
        internal const uint CFM_UNDERLINECOLOR  = 0x00400000;  // RichEdit 3.0+
        internal const uint CFE_UNDERLINE       = 0x00000004;

        internal const byte CFU_UNDERLINE       = 0x01;
        internal const byte CFU_UNDERLINEDOTTED = 0x04;
        internal const byte CFU_UNDERLINEWAVE   = 0x08;
        internal const byte CFU_UNDERLINENONE   = 0x00;
        internal const byte CFU_COLOR_RED       = 0x06;        // built-in red index

        // Layout matches Windows SDK CHARFORMAT2W with default packing.
        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential,
            Pack = 4, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal struct CHARFORMAT2
        {
            public uint   cbSize;
            public uint   dwMask;
            public uint   dwEffects;
            public int    yHeight;
            public int    yOffset;
            public int    crTextColor;
            public byte   bCharSet;
            public byte   bPitchAndFamily;
            [System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szFaceName;
            public short  wWeight;
            public short  sSpacing;
            public int    crBackColor;
            public uint   lcid;
            public uint   dwReserved;
            public short  sStyle;
            public short  wKerning;
            public byte   bUnderlineType;
            public byte   bAnimation;
            public byte   bRevAuthor;
            public byte   bUnderlineColor;
        }

        /// <summary>
        /// Applies an underline style based on diagnostic severity while leaving
        /// text colours untouched so syntax highlighting is preserved.
        /// </summary>
        internal static void ApplyDiagnosticUnderline(RichTextBox rtb, DiagnosticSeverity severity)
        {
            byte underlineType = CFU_UNDERLINEWAVE;

            switch (severity)
            {
                case DiagnosticSeverity.Error:
                    underlineType = CFU_UNDERLINEWAVE;
                    break;
                case DiagnosticSeverity.Warning:
                    underlineType = CFU_UNDERLINEDOTTED;
                    break;
                case DiagnosticSeverity.Info:
                    underlineType = CFU_UNDERLINE;
                    break;
            }

            ApplyUnderline(rtb, underlineType);
        }

        /// <summary>
        /// Applies an underline type to the current RichTextBox selection using
        /// the native RichEdit CHARFORMAT2 structure.
        /// </summary>
        private static void ApplyUnderline(RichTextBox rtb, byte underlineType)
        {
            var cf = new CHARFORMAT2();
            cf.cbSize         = (uint)System.Runtime.InteropServices.Marshal.SizeOf(cf);
            cf.dwMask         = CFM_UNDERLINE | CFM_UNDERLINETYPE | CFM_UNDERLINECOLOR;
            cf.dwEffects      = CFE_UNDERLINE;
            cf.bUnderlineType = underlineType;
            cf.bUnderlineColor = CFU_COLOR_RED;
            SendMessage(rtb.Handle, EM_SETCHARFORMAT, new IntPtr(SCF_SELECTION), ref cf);
        }

        /// <summary>
        /// Clears diagnostic underline formatting on the current RichTextBox selection.
        /// Caller should select target range first (typically SelectAll()).
        /// </summary>
        internal static void ClearDiagnosticUnderline(RichTextBox rtb)
        {
            var cf = new CHARFORMAT2();
            cf.cbSize         = (uint)System.Runtime.InteropServices.Marshal.SizeOf(cf);
            cf.dwMask         = CFM_UNDERLINE | CFM_UNDERLINETYPE | CFM_UNDERLINECOLOR;
            cf.dwEffects      = 0;
            cf.bUnderlineType = CFU_UNDERLINENONE;
            cf.bUnderlineColor = 0;
            SendMessage(rtb.Handle, EM_SETCHARFORMAT, new IntPtr(SCF_SELECTION), ref cf);
        }
    }
}
