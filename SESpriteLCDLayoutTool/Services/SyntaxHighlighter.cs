using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SESpriteLCDLayoutTool.Controls;
using SESpriteLCDLayoutTool.Data;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Applies C# syntax highlighting and diagnostic indicators to a
    /// <see cref="ScintillaCodeBox"/> using Roslyn tokenization.
    /// </summary>
    internal static class SyntaxHighlighter
    {
        internal sealed class DiagnosticMarker
        {
            public int Start;
            public int Length;
            public DiagnosticSeverity Severity;
            public string Id;
            public string Message;
        }

        public sealed class SemanticMarker
        {
            public int Start;
            public int Length;
            public DiagnosticSeverity Severity;
            public string Id;
            public string Message;
        }

        private static readonly ConditionalWeakTable<ScintillaCodeBox, List<DiagnosticMarker>> _diagnosticCache =
            new ConditionalWeakTable<ScintillaCodeBox, List<DiagnosticMarker>>();

        /// <summary>
        /// Returns the current diagnostic markers for the given editor.
        /// Used by the overlay control to paint squiggly underlines.
        /// </summary>
        public static IReadOnlyList<DiagnosticMarker> GetDiagnosticMarkers(ScintillaCodeBox editor)
        {
            if (editor == null) return Array.Empty<DiagnosticMarker>();
            if (_diagnosticCache.TryGetValue(editor, out var markers))
                return markers;
            return Array.Empty<DiagnosticMarker>();
        }

        private static readonly object _semanticRefLock = new object();
        private static List<MetadataReference> _semanticReferences;

        // ── Style IDs matching ScintillaCodeBox constants ──────────────────────
        private const int StyleDefault    = ScintillaCodeBox.StyleDefault;
        private const int StyleKeyword    = ScintillaCodeBox.StyleKeyword;
        private const int StyleControl    = ScintillaCodeBox.StyleControl;
        private const int StyleType       = ScintillaCodeBox.StyleType;
        private const int StyleString     = ScintillaCodeBox.StyleString;
        private const int StyleComment    = ScintillaCodeBox.StyleComment;
        private const int StyleNumber     = ScintillaCodeBox.StyleNumber;
        private const int StylePreproc    = ScintillaCodeBox.StylePreproc;
        private const int StyleDisabled   = ScintillaCodeBox.StyleDisabled;

        // Preprocessor symbols defined when parsing.
        private static readonly CSharpParseOptions _parseOptions = new CSharpParseOptions(
            kind: SourceCodeKind.Regular,
            preprocessorSymbols: new[] { "TORCH", "STABLE", "DEBUG", "RELEASE" });

        // Control-flow keywords get a distinct colour.
        private static readonly HashSet<SyntaxKind> _controlFlow =
            new HashSet<SyntaxKind>
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
        /// Eagerly loads and caches Roslyn semantic references.
        /// </summary>
        public static void WarmUpReferences() => GetSemanticReferences();

        /// <summary>
        /// Applies syntax colouring and diagnostic indicators to the Scintilla editor.
        /// When <paramref name="skipDiagnostics"/> is true, only token colouring is
        /// applied (no squiggly underlines).  Use this for the fast UI-thread pass and
        /// let the background compilation pass supply accurate diagnostics.
        /// </summary>
        public static void Highlight(ScintillaCodeBox editor, bool includeSemantics = true, bool skipDiagnostics = false)
        {
            if (editor == null) return;

            var perfSw = System.Diagnostics.Stopwatch.StartNew();
            string source = editor.Text;
            if (string.IsNullOrEmpty(source))
            {
                SetDiagnosticCache(editor, new List<DiagnosticMarker>());
                return;
            }

            SyntaxTree tree = CSharpSyntaxTree.ParseText(source, _parseOptions);

            // Apply styling — Scintilla uses StartStyling + SetStyling
            editor.StartStyling(0);

            // First reset all text to default style
            editor.SetStyling(editor.TextLength, StyleDefault);

            // Now apply token-level styles
            foreach (SyntaxToken token in tree.GetRoot().DescendantTokens(descendIntoTrivia: true))
            {
                foreach (SyntaxTrivia trivia in token.LeadingTrivia)
                    ApplyTrivia(editor, trivia);

                int start = token.SpanStart;
                int len = token.Span.Length;
                if (len > 0)
                {
                    int style = ClassifyToken(token);
                    if (style != StyleDefault)
                    {
                        editor.StartStyling(start);
                        editor.SetStyling(len, style);
                    }
                }

                foreach (SyntaxTrivia trivia in token.TrailingTrivia)
                    ApplyTrivia(editor, trivia);
            }

            // When skipDiagnostics is set, leave squiggly lines to the background
            // compilation pass which has full semantic context and avoids false
            // positives from parse-only analysis.
            if (skipDiagnostics)
            {
                perfSw.Stop();
                System.Diagnostics.Debug.WriteLine($"[SyntaxHighlighter.Highlight] {perfSw.ElapsedMilliseconds} ms | len={source.Length} | coloring-only (diagnostics deferred)");
                return;
            }

            // ── Diagnostic indicators ────────────────────────────────────────
            // Clear all diagnostic indicators first
            editor.IndicatorCurrent = ScintillaCodeBox.IndicatorError;
            editor.IndicatorClearRange(0, editor.TextLength);
            editor.IndicatorCurrent = ScintillaCodeBox.IndicatorWarning;
            editor.IndicatorClearRange(0, editor.TextLength);
            editor.IndicatorCurrent = ScintillaCodeBox.IndicatorInfo;
            editor.IndicatorClearRange(0, editor.TextLength);

            // Wrap bare scripts for diagnostics
            const string diagPrefix = "class __D__ {\n";
            bool isBareScript = source.IndexOf("class ", StringComparison.Ordinal) < 0;
            SyntaxTree diagTree;
            int diagOffset;
            if (isBareScript)
            {
                diagTree = CSharpSyntaxTree.ParseText(diagPrefix + source + "\n}", _parseOptions);
                diagOffset = diagPrefix.Length;
            }
            else
            {
                diagTree = tree;
                diagOffset = 0;
            }

            const int maxSquiggles = 80;
            int squiggleCount = 0;
            var markers = new List<DiagnosticMarker>();
            var diagnostics = diagTree.GetDiagnostics()
                .Concat(includeSemantics ? GetSemanticDiagnostics(diagTree) : Enumerable.Empty<Diagnostic>())
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

                var span = diag.Location.SourceSpan;
                int eStart = span.Start - diagOffset;
                int eLen = Math.Max(1, span.Length);
                if (eStart < 0 || eStart >= editor.TextLength) continue;
                eLen = Math.Min(eLen, editor.TextLength - eStart);

                markers.Add(new DiagnosticMarker
                {
                    Start = eStart,
                    Length = eLen,
                    Severity = diag.Severity,
                    Id = diag.Id,
                    Message = BuildDiagnosticMessage(diag, diagTree),
                });

                // Apply Scintilla indicator
                int indicator;
                switch (diag.Severity)
                {
                    case DiagnosticSeverity.Error:   indicator = ScintillaCodeBox.IndicatorError;   break;
                    case DiagnosticSeverity.Warning:  indicator = ScintillaCodeBox.IndicatorWarning; break;
                    default:                          indicator = ScintillaCodeBox.IndicatorInfo;    break;
                }
                editor.IndicatorCurrent = indicator;
                editor.IndicatorFillRange(eStart, eLen);
            }

            SetDiagnosticCache(editor, markers);

            perfSw.Stop();
            System.Diagnostics.Debug.WriteLine($"[SyntaxHighlighter.Highlight] {perfSw.ElapsedMilliseconds} ms | len={source.Length} | squiggles={squiggleCount} | semantics={includeSemantics}");
        }

        /// <summary>
        /// Returns tooltip text for the diagnostic covering <paramref name="charIndex"/>.
        /// </summary>
        public static bool TryGetDiagnosticTooltip(ScintillaCodeBox editor, int charIndex, out string tooltipText)
        {
            tooltipText = null;
            if (editor == null || charIndex < 0) return false;

            if (!_diagnosticCache.TryGetValue(editor, out var markers) || markers == null || markers.Count == 0)
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

        public static void ClearDiagnosticCache(ScintillaCodeBox editor)
        {
            SetDiagnosticCache(editor, new List<DiagnosticMarker>());
        }

        public static void ClearDiagnostics(ScintillaCodeBox editor)
        {
            if (editor == null) return;
            SetDiagnosticCache(editor, new List<DiagnosticMarker>());
        }

        /// <summary>
        /// Clears both diagnostic cache and visual indicators.
        /// </summary>
        public static void ClearDiagnosticsVisual(ScintillaCodeBox editor)
        {
            if (editor == null) return;
            SetDiagnosticCache(editor, new List<DiagnosticMarker>());

            editor.IndicatorCurrent = ScintillaCodeBox.IndicatorError;
            editor.IndicatorClearRange(0, editor.TextLength);
            editor.IndicatorCurrent = ScintillaCodeBox.IndicatorWarning;
            editor.IndicatorClearRange(0, editor.TextLength);
            editor.IndicatorCurrent = ScintillaCodeBox.IndicatorInfo;
            editor.IndicatorClearRange(0, editor.TextLength);
        }

        private static readonly System.Text.RegularExpressions.Regex _compilerErrorRx =
            new System.Text.RegularExpressions.Regex(
                @"\((\d+),(\d+)\):\s*(error|warning)\s+(CS\d+):\s*(.+)",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        public static int ApplyCompilerDiagnosticsFromText(ScintillaCodeBox editor, string compilerOutput)
        {
            System.Diagnostics.Debug.WriteLine($"[ApplyCompilerDiagnosticsFromText] called, output length={compilerOutput?.Length ?? -1}");
            if (!string.IsNullOrWhiteSpace(compilerOutput))
                System.Diagnostics.Debug.WriteLine($"[ApplyCompilerDiagnosticsFromText] first 500 chars:\n{compilerOutput.Substring(0, Math.Min(500, compilerOutput.Length))}");
            ClearDiagnostics(editor);
            if (string.IsNullOrWhiteSpace(compilerOutput) || editor == null) return 0;

            var markers = new List<DiagnosticMarker>();
            var lines = compilerOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var m = _compilerErrorRx.Match(line);
                System.Diagnostics.Debug.WriteLine($"[ApplyCompilerDiag] line: [{line}] match={m.Success}");
                if (!m.Success) continue;

                int lineNum = int.Parse(m.Groups[1].Value) - 1; // 0-based
                int col = int.Parse(m.Groups[2].Value) - 1;
                string severity = m.Groups[3].Value;
                string id = m.Groups[4].Value;
                string message = m.Groups[5].Value;
                System.Diagnostics.Debug.WriteLine($"[ApplyCompilerDiag] parsed: line={lineNum} col={col} editor.Lines.Count={editor.Lines.Count}");

                if (lineNum < 0 || lineNum >= editor.Lines.Count) continue;
                int lineStart = editor.Lines[lineNum].Position;
                int lineLen = editor.Lines[lineNum].Length;
                int charStart = lineStart + Math.Min(col, Math.Max(0, lineLen - 1));

                // Underline from error position to end of line (or at least 1 char)
                int underlineLen = Math.Max(1, lineStart + lineLen - charStart);
                // Trim trailing newline from underline
                string lineText = editor.Lines[lineNum].Text;
                if (lineText.EndsWith("\n") || lineText.EndsWith("\r"))
                    underlineLen = Math.Max(1, underlineLen - 1);
                if (lineText.EndsWith("\r\n"))
                    underlineLen = Math.Max(1, underlineLen - 1);

                bool isError = severity.Equals("error", StringComparison.OrdinalIgnoreCase);
                var sev = isError ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;

                markers.Add(new DiagnosticMarker
                {
                    Start = charStart,
                    Length = underlineLen,
                    Severity = sev,
                    Id = id,
                    Message = $"{id}: {message}",
                });

                int indicator = isError ? ScintillaCodeBox.IndicatorError : ScintillaCodeBox.IndicatorWarning;
                editor.IndicatorCurrent = indicator;
                editor.IndicatorFillRange(charStart, underlineLen);
            }

            SetDiagnosticCache(editor, markers);
            return markers.Count;
        }

        // Cached pre-parsed SE stub syntax tree — compiled once, reused for every
        // live analysis pass so that SE types (MySprite, Vector2, Color, etc.) resolve.
        private static SyntaxTree _stubTree;
        private static SyntaxTree GetStubTree()
        {
            if (_stubTree != null) return _stubTree;
            _stubTree = CSharpSyntaxTree.ParseText(CodeExecutor.StubsSource, _parseOptions);
            return _stubTree;
        }

        // Implicit SE using directives prepended to user code for analysis so that
        // bare PB/Torch code (which normally gets these usings injected at runtime)
        // resolves SE types correctly during live editing.
        private const string ImplicitSeUsings =
            // Standard .NET usings (PB scripts get these implicitly at runtime)
            "using System;\n" +
            "using System.Collections.Generic;\n" +
            "using System.Globalization;\n" +
            "using System.Linq;\n" +
            "using System.Text;\n" +
            // SE-specific usings
            "using VRage.Game.GUI.TextPanel;\n" +
            "using VRageMath;\n" +
            "using Sandbox.ModAPI.Ingame;\n" +
            "using Sandbox.ModAPI;\n" +
            "using VRage;\n" +
            "using VRage.Game.ModAPI.Ingame;\n" +
            "using VRage.Game.ModAPI;\n" +
            "using VRage.Game.Components;\n";

        // Prefix/suffix used to wrap bare PB scripts in a MyGridProgram-derived
        // class so that Me, Runtime, GridTerminalSystem, Echo, Storage resolve.
        private const string PbClassPrefix = "class Program : Sandbox.ModAPI.Ingame.MyGridProgram {\n";
        private const string PbClassSuffix = "\n}";

        /// <summary>
        /// Returns true when the source looks like a bare PB script (methods at
        /// top level, no explicit class declaration).
        /// </summary>
        private static bool IsBareScript(string source)
        {
            return source.IndexOf("class ", StringComparison.Ordinal) < 0;
        }

        public static List<SemanticMarker> ComputeSemanticMarkers(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return new List<SemanticMarker>();

            try
            {
                // Prepend implicit SE usings so bare user code resolves SE types.
                // For bare PB scripts, also wrap in a MyGridProgram-derived class
                // so Me, Runtime, GridTerminalSystem, Echo, Storage etc. resolve.
                bool isBare = IsBareScript(source);
                string prefix = ImplicitSeUsings + (isBare ? PbClassPrefix : "");
                string suffix = isBare ? PbClassSuffix : "";
                int prefixLength = prefix.Length;
                string wrappedSource = prefix + source + suffix;

                SyntaxTree userTree = CSharpSyntaxTree.ParseText(wrappedSource, _parseOptions);

                // Use real SE DLLs when available; fall back to source stubs.
                var trees = HasSeGameDlls
                    ? new[] { userTree }
                    : new[] { userTree, GetStubTree() };

                // Full Roslyn compilation with SE stubs so types like MySprite,
                // Vector2, Color etc. resolve correctly.
                var refs = GetSemanticReferences();
                List<Diagnostic> diagnostics;
                if (refs != null && refs.Count > 0)
                {
                    var compilation = CSharpCompilation.Create(
                        assemblyName: "SESpriteLCDLayoutTool.LiveAnalysis",
                        syntaxTrees: trees,
                        references: refs,
                        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                    // Only report diagnostics from the USER's code, not from stubs
                    diagnostics = compilation.GetDiagnostics()
                        .Where(d => d.Severity == DiagnosticSeverity.Error ||
                                    d.Severity == DiagnosticSeverity.Warning)
                        .Where(d => d.Location != null && d.Location.IsInSource &&
                                    d.Location.SourceTree == userTree)
                        .Where(d => d.Location.SourceSpan.Start >= prefixLength)
                        .GroupBy(d => new { d.Location.SourceSpan.Start, d.Location.SourceSpan.Length, d.Id })
                        .Select(g => g.First())
                        .OrderByDescending(d => d.Severity)
                        .ThenBy(d => d.Location.SourceSpan.Start)
                        .Take(80)
                        .ToList();
                }
                else
                {
                    // Fallback: parse-only diagnostics if no references available
                    diagnostics = userTree.GetDiagnostics()
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Where(d => d.Location != null && d.Location.IsInSource)
                        .Where(d => d.Location.SourceSpan.Start >= prefixLength)
                        .Take(80)
                        .ToList();
                }

                var markers = new List<SemanticMarker>(diagnostics.Count);
                foreach (var d in diagnostics)
                {
                    var span = d.Location.SourceSpan;
                    markers.Add(new SemanticMarker
                    {
                        Start = span.Start - prefixLength,
                        Length = Math.Max(1, span.Length),
                        Severity = d.Severity,
                        Id = d.Id,
                        Message = BuildDiagnosticMessage(d, userTree),
                    });
                }

                return markers;
            }
            catch
            {
                return new List<SemanticMarker>();
            }
        }

        public static void ApplySemanticMarkers(ScintillaCodeBox editor, List<SemanticMarker> markers)
        {
            if (editor == null) return;
            if (markers == null) markers = new List<SemanticMarker>();

            // Clear all existing indicators — the fast pass now skips diagnostics
            // entirely, so this is the sole authority for squiggly lines.
            editor.IndicatorCurrent = ScintillaCodeBox.IndicatorError;
            editor.IndicatorClearRange(0, editor.TextLength);
            editor.IndicatorCurrent = ScintillaCodeBox.IndicatorWarning;
            editor.IndicatorClearRange(0, editor.TextLength);
            editor.IndicatorCurrent = ScintillaCodeBox.IndicatorInfo;
            editor.IndicatorClearRange(0, editor.TextLength);

            var cache = new List<DiagnosticMarker>();
            foreach (var m in markers)
            {
                if (m == null) continue;
                int start = Math.Max(0, m.Start);
                if (start >= editor.TextLength) continue;
                int len = Math.Max(1, m.Length);
                len = Math.Min(len, editor.TextLength - start);

                cache.Add(new DiagnosticMarker
                {
                    Start = start,
                    Length = len,
                    Severity = m.Severity,
                    Id = m.Id,
                    Message = m.Message,
                });

                int indicator;
                switch (m.Severity)
                {
                    case DiagnosticSeverity.Error:   indicator = ScintillaCodeBox.IndicatorError;   break;
                    case DiagnosticSeverity.Warning:  indicator = ScintillaCodeBox.IndicatorWarning; break;
                    default:                          indicator = ScintillaCodeBox.IndicatorInfo;    break;
                }
                editor.IndicatorCurrent = indicator;
                editor.IndicatorFillRange(start, len);
            }

            SetDiagnosticCache(editor, cache);
        }

        private static void SetDiagnosticCache(ScintillaCodeBox editor, List<DiagnosticMarker> markers)
        {
            if (editor == null) return;

            if (_diagnosticCache.TryGetValue(editor, out var existing))
            {
                existing.Clear();
                if (markers != null) existing.AddRange(markers);
                return;
            }

            _diagnosticCache.Add(editor, markers ?? new List<DiagnosticMarker>());
        }

        private static IEnumerable<Diagnostic> GetSemanticDiagnostics(SyntaxTree tree)
        {
            try
            {
                var refs = GetSemanticReferences();
                if (refs == null || refs.Count == 0)
                    return Enumerable.Empty<Diagnostic>();

                // Prepend implicit SE usings so bare user code resolves SE types.
                string originalText = tree.ToString();
                bool isBare = IsBareScript(originalText);
                string prefix = ImplicitSeUsings + (isBare ? PbClassPrefix : "");
                string suffix = isBare ? PbClassSuffix : "";
                int prefixLength = prefix.Length;
                var wrappedTree = CSharpSyntaxTree.ParseText(prefix + originalText + suffix, _parseOptions);

                var trees = HasSeGameDlls
                    ? new[] { wrappedTree }
                    : new[] { wrappedTree, GetStubTree() };

                var compilation = CSharpCompilation.Create(
                    assemblyName: "SESpriteLCDLayoutTool.EditorAnalysis",
                    syntaxTrees: trees,
                    references: refs,
                    options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                return compilation.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Where(d => d.Id == "CS0103")
                    .Where(d => d.Location != null && d.Location.IsInSource &&
                                d.Location.SourceTree == wrappedTree)
                    .Where(d => d.Location.SourceSpan.Start >= prefixLength)
                    .Where(d => HasTypoSuggestion(d, wrappedTree))
                    .ToList();
            }
            catch
            {
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
                    catch { }
                }

                // Add SE game DLLs from Bin64 for accurate type resolution
                LoadSeGameReferences(refs, seen);

                _semanticReferences = refs;
                return _semanticReferences;
            }
        }

        /// <summary>
        /// Key SE DLLs from the game's Bin64 folder that provide the types used
        /// in PB/Torch scripts (MySprite, Vector2, IMyTextSurface, etc.).
        /// Only used as Roslyn MetadataReferences for analysis — never loaded into
        /// the AppDomain.
        /// </summary>
        private static readonly string[] _seBin64Dlls =
        {
            "VRage.Game.dll",
            "VRage.dll",
            "VRage.Library.dll",
            "VRage.Math.dll",
            "VRage.Scripting.dll",
            "Sandbox.Common.dll",
            "Sandbox.Game.dll",
            "SpaceEngineers.Game.dll",
            "SpaceEngineers.ObjectBuilders.dll",
        };

        /// <summary>True when at least one real SE DLL was loaded for analysis.</summary>
        private static bool HasSeGameDlls;

        /// <summary>
        /// Loads SE game DLLs from the detected Bin64 folder as metadata
        /// references for Roslyn analysis only.
        /// </summary>
        private static void LoadSeGameReferences(List<MetadataReference> refs, HashSet<string> seen)
        {
            string contentPath = AppSettings.GameContentPath;
            if (string.IsNullOrEmpty(contentPath)) return;

            // Content is SpaceEngineers/Content, Bin64 is SpaceEngineers/Bin64
            string bin64 = Path.Combine(Path.GetDirectoryName(contentPath), "Bin64");
            if (!Directory.Exists(bin64)) return;

            foreach (string dll in _seBin64Dlls)
            {
                string fullPath = Path.Combine(bin64, dll);
                if (!File.Exists(fullPath) || !seen.Add(fullPath)) continue;
                try
                {
                    refs.Add(MetadataReference.CreateFromFile(fullPath));
                    HasSeGameDlls = true;
                }
                catch { /* DLL may be locked or incompatible — skip */ }
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

        private static bool IsLikelyImplicitSeSymbol(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;

            switch (name)
            {
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
                case "VRage":
                case "VRageMath":
                case "Sandbox":
                case "Torch":
                case "Pulsar":
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

        private static void ApplyTrivia(ScintillaCodeBox editor, SyntaxTrivia trivia)
        {
            int start = trivia.SpanStart;
            int len = trivia.Span.Length;
            if (len == 0) return;

            int? style = ClassifyTrivia(trivia);
            if (style.HasValue)
            {
                editor.StartStyling(start);
                editor.SetStyling(len, style.Value);
            }
        }

        private static int ClassifyToken(SyntaxToken token)
        {
            SyntaxKind kind = token.Kind();

            switch (kind)
            {
                case SyntaxKind.StringLiteralToken:
                case SyntaxKind.InterpolatedStringStartToken:
                case SyntaxKind.InterpolatedStringEndToken:
                case SyntaxKind.InterpolatedStringTextToken:
                case SyntaxKind.InterpolatedVerbatimStringStartToken:
                case SyntaxKind.CharacterLiteralToken:
                case SyntaxKind.MultiLineRawStringLiteralToken:
                case SyntaxKind.SingleLineRawStringLiteralToken:
                case SyntaxKind.Utf8StringLiteralToken:
                    return StyleString;

                case SyntaxKind.NumericLiteralToken:
                    return StyleNumber;

                case SyntaxKind.IdentifierToken:
                    return ColourIdentifier(token);
            }

            if (SyntaxFacts.IsKeywordKind(kind))
                return _controlFlow.Contains(kind) ? StyleControl : StyleKeyword;

            return StyleDefault;
        }

        private static int ColourIdentifier(SyntaxToken token)
        {
            SyntaxNode parent = token.Parent;
            if (parent == null) return StyleDefault;

            switch (parent.Kind())
            {
                case SyntaxKind.ClassDeclaration:
                case SyntaxKind.StructDeclaration:
                case SyntaxKind.EnumDeclaration:
                case SyntaxKind.InterfaceDeclaration:
                case SyntaxKind.DelegateDeclaration:
                case SyntaxKind.RecordDeclaration:
                    var decl = parent as BaseTypeDeclarationSyntax;
                    if (decl != null && decl.Identifier == token) return StyleType;
                    break;

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
                            return StyleType;

                        case SyntaxKind.SimpleMemberAccessExpression:
                            var mae = gp as MemberAccessExpressionSyntax;
                            if (mae != null && mae.Expression == parent && IsPascalCase(token.Text))
                                return StyleType;
                            break;
                    }
                    break;
            }

            return StyleDefault;
        }

        private static bool IsPascalCase(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 2) return false;
            if (!char.IsUpper(name[0])) return false;
            for (int i = 1; i < name.Length; i++)
                if (char.IsLower(name[i])) return true;
            return false;
        }

        private static int? ClassifyTrivia(SyntaxTrivia trivia)
        {
            switch (trivia.Kind())
            {
                case SyntaxKind.SingleLineCommentTrivia:
                case SyntaxKind.MultiLineCommentTrivia:
                case SyntaxKind.SingleLineDocumentationCommentTrivia:
                case SyntaxKind.MultiLineDocumentationCommentTrivia:
                case SyntaxKind.DocumentationCommentExteriorTrivia:
                case SyntaxKind.XmlComment:
                    return StyleComment;

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
                    return StylePreproc;

                case SyntaxKind.DisabledTextTrivia:
                    return StyleDisabled;

                default:
                    return null;
            }
        }
    }
}
