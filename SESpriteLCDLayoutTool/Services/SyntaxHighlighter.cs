using System;
using System.Drawing;
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
            if (string.IsNullOrEmpty(source)) return;

            // Parse as Regular (not Script) so class/namespace structure is valid.
            // Preprocessor symbols are defined so all #if branches are included in
            // the token stream — #if TORCH blocks are never dropped as disabled trivia.
            SyntaxTree tree = CSharpSyntaxTree.ParseText(source, _parseOptions);

            int selStart  = rtb.SelectionStart;
            int selLength = rtb.SelectionLength;

            rtb.SuspendLayout();
            rtb.BeginUpdate();

            try
            {
                // Reset all text to the default colour in one pass.
                rtb.SelectAll();
                rtb.SelectionColor = ColDefault;

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
            }
            finally
            {
                rtb.EndUpdate();
                rtb.ResumeLayout();

                if (selStart >= 0 && selStart <= rtb.TextLength)
                    rtb.Select(selStart, selLength);
            }
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
                    }
                    break;
            }

            return ColDefault;
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

        public static void BeginUpdate(this RichTextBox rtb)
        {
            NativeMethods.SendMessage(rtb.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        }

        public static void EndUpdate(this RichTextBox rtb)
        {
            NativeMethods.SendMessage(rtb.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
            rtb.Invalidate();
        }
    }

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
