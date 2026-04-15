using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

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
        private static readonly Color ColDefault     = Color.FromArgb(212, 212, 212); // light grey
        private static readonly Color ColKeyword     = Color.FromArgb(86,  156, 214); // blue
        private static readonly Color ColControl     = Color.FromArgb(197, 134, 192); // pink-purple (control-flow)
        private static readonly Color ColType        = Color.FromArgb(78,  201, 176); // teal
        private static readonly Color ColString      = Color.FromArgb(206, 145, 120); // orange-brown
        private static readonly Color ColComment     = Color.FromArgb(106, 153,  85); // green
        private static readonly Color ColNumber      = Color.FromArgb(181, 206, 168); // light green
        private static readonly Color ColPunctuation = Color.FromArgb(212, 212, 212); // same as default
        private static readonly Color ColPreproc     = Color.FromArgb(155, 155, 155); // grey

        // Control-flow keywords get a distinct colour so they stand out.
        private static readonly System.Collections.Generic.HashSet<SyntaxKind> _controlFlow =
            new System.Collections.Generic.HashSet<SyntaxKind>
            {
                SyntaxKind.IfKeyword,    SyntaxKind.ElseKeyword,
                SyntaxKind.ForKeyword,   SyntaxKind.ForEachKeyword,
                SyntaxKind.WhileKeyword, SyntaxKind.DoKeyword,
                SyntaxKind.SwitchKeyword,SyntaxKind.CaseKeyword,
                SyntaxKind.DefaultKeyword,
                SyntaxKind.BreakKeyword, SyntaxKind.ContinueKeyword,
                SyntaxKind.ReturnKeyword,SyntaxKind.ThrowKeyword,
                SyntaxKind.TryKeyword,   SyntaxKind.CatchKeyword,
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

            // Parse tokens only — no full compilation needed.
            SyntaxTree tree = CSharpSyntaxTree.ParseText(source,
                CSharpParseOptions.Default.WithKind(SourceCodeKind.Script));

            // Save state
            int selStart  = rtb.SelectionStart;
            int selLength = rtb.SelectionLength;

            rtb.SuspendLayout();
            rtb.BeginUpdate(); // suppress WM_PAINT during bulk colour changes

            try
            {
                // Reset all foreground colour to default in one pass
                rtb.SelectAll();
                rtb.SelectionColor = ColDefault;

                // Walk tokens and colourise
                foreach (SyntaxToken token in tree.GetRoot().DescendantTokens())
                {
                    // ── Leading trivia (comments, directives, whitespace) ─────
                    foreach (SyntaxTrivia trivia in token.LeadingTrivia)
                        ApplyTrivia(rtb, trivia);

                    // ── Token itself ──────────────────────────────────────────
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

                    // ── Trailing trivia ───────────────────────────────────────
                    foreach (SyntaxTrivia trivia in token.TrailingTrivia)
                        ApplyTrivia(rtb, trivia);
                }
            }
            finally
            {
                rtb.EndUpdate();
                rtb.ResumeLayout();

                // Restore cursor / selection
                if (selStart >= 0 && selStart <= rtb.TextLength)
                    rtb.Select(selStart, selLength);
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static void ApplyTrivia(RichTextBox rtb, SyntaxTrivia trivia)
        {
            int  start = trivia.SpanStart;
            int  len   = trivia.Span.Length;
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

            // ── String / character / interpolated literals ────────────────────
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
                    return ColString;

                // ── Numeric literals ──────────────────────────────────────────
                case SyntaxKind.NumericLiteralToken:
                    return ColNumber;

                // ── Identifiers: colour known built-in types ──────────────────
                case SyntaxKind.IdentifierToken:
                    return ColourIdentifier(token);
            }

            // ── Keywords ──────────────────────────────────────────────────────
            if (SyntaxFacts.IsKeywordKind(kind))
            {
                if (_controlFlow.Contains(kind)) return ColControl;
                return ColKeyword;
            }

            return ColDefault;
        }

        private static Color ColourIdentifier(SyntaxToken token)
        {
            // Colour identifiers that are type names in declarations / usages.
            SyntaxNode parent = token.Parent;
            if (parent == null) return ColDefault;

            switch (parent.Kind())
            {
                // class Foo, struct Foo, enum Foo, interface IFoo, delegate Foo
                case SyntaxKind.ClassDeclaration:
                case SyntaxKind.StructDeclaration:
                case SyntaxKind.EnumDeclaration:
                case SyntaxKind.InterfaceDeclaration:
                case SyntaxKind.DelegateDeclaration:
                case SyntaxKind.RecordDeclaration:
                    var decl = parent as BaseTypeDeclarationSyntax;
                    if (decl != null && decl.Identifier == token) return ColType;
                    break;

                // variable / field type: Foo x = …   or parameter type
                case SyntaxKind.IdentifierName:
                    // When the grandparent is a type context, colour as a type
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

                // method / constructor name
                case SyntaxKind.MethodDeclaration:
                {
                    var md = parent as MethodDeclarationSyntax;
                    if (md != null && md.Identifier == token) return ColDefault; // keep default
                    break;
                }
            }

            return ColDefault;
        }

        private static Color? ClassifyTrivia(SyntaxTrivia trivia)
        {
            switch (trivia.Kind())
            {
                case SyntaxKind.SingleLineCommentTrivia:
                case SyntaxKind.MultiLineCommentTrivia:
                case SyntaxKind.SingleLineDocumentationCommentTrivia:
                case SyntaxKind.MultiLineDocumentationCommentTrivia:
                case SyntaxKind.DocumentationCommentExteriorTrivia:
                case SyntaxKind.XmlComment:
                    return ColComment;

                case SyntaxKind.RegionDirectiveTrivia:
                case SyntaxKind.EndRegionDirectiveTrivia:
                case SyntaxKind.IfDirectiveTrivia:
                case SyntaxKind.ElseDirectiveTrivia:
                case SyntaxKind.EndIfDirectiveTrivia:
                case SyntaxKind.PragmaWarningDirectiveTrivia:
                case SyntaxKind.PragmaChecksumDirectiveTrivia:
                case SyntaxKind.ReferenceDirectiveTrivia:
                case SyntaxKind.LoadDirectiveTrivia:
                case SyntaxKind.DefineDirectiveTrivia:
                case SyntaxKind.UndefDirectiveTrivia:
                    return ColPreproc;

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
