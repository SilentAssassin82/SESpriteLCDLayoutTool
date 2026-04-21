using System;
using System.Drawing;
using System.Windows.Forms;
using ScintillaNET;

namespace SESpriteLCDLayoutTool.Controls
{
    /// <summary>
    /// Scintilla-based code editor with RichTextBox-compatible API surface.
    /// Drop-in replacement for the code editor panel — existing call sites
    /// that use Select(), SelectionStart, SelectedText, etc. continue to work.
    /// </summary>
    public sealed class ScintillaCodeBox : Scintilla
    {
        // ── Indicator numbers for diagnostics ─────────────────────────────────
        internal const int IndicatorError   = 8;
        internal const int IndicatorWarning = 9;
        internal const int IndicatorInfo    = 10;
        internal const int IndicatorHeatmap = 11;
        internal const int IndicatorHeatmap2 = 12;
        internal const int IndicatorHeatmap3 = 13;
        internal const int IndicatorHeatmap4 = 14;
        internal const int IndicatorWordHighlight = 15;
        private static readonly int[] HeatmapIndicators = { 11, 12, 13, 14 };

        private string _wordHighlight; // currently highlighted word (null = none)

        // ── Style constants for C# syntax colouring ───────────────────────────
        internal const int StyleDefault    = Style.Default;
        internal const int StyleKeyword    = 1;
        internal const int StyleControl    = 2;
        internal const int StyleType       = 3;
        internal const int StyleString     = 4;
        internal const int StyleComment    = 5;
        internal const int StyleNumber     = 6;
        internal const int StylePreproc    = 7;
        internal const int StyleDisabled   = 8;
        internal const int StyleIdentifier = 9;

        public ScintillaCodeBox()
        {
            ConfigureDefaults();
        }

        private void ConfigureDefaults()
        {
            // ── Dark theme ────────────────────────────────────────────────────
            StyleResetDefault();
            Styles[Style.Default].BackColor = Color.FromArgb(14, 14, 14);
            Styles[Style.Default].ForeColor = Color.FromArgb(212, 212, 212);
            Styles[Style.Default].Font = "Consolas";
            Styles[Style.Default].Size = 10;
            StyleClearAll();

            // ── Syntax styles (VS Dark / VS Code-inspired) ────────────────────
            Styles[StyleKeyword].ForeColor  = Color.FromArgb(86,  156, 214); // blue
            Styles[StyleControl].ForeColor  = Color.FromArgb(197, 134, 192); // pink-purple
            Styles[StyleType].ForeColor     = Color.FromArgb(78,  201, 176); // teal
            Styles[StyleString].ForeColor   = Color.FromArgb(206, 145, 120); // orange-brown
            Styles[StyleComment].ForeColor  = Color.FromArgb(106, 153,  85); // green
            Styles[StyleNumber].ForeColor   = Color.FromArgb(181, 206, 168); // light green
            Styles[StylePreproc].ForeColor  = Color.FromArgb(155, 155, 155); // grey
            Styles[StyleDisabled].ForeColor = Color.FromArgb(100, 100, 100); // dark grey

            // ── Line numbers ──────────────────────────────────────────────────
            Margins[0].Type = MarginType.Number;
            Margins[0].Width = 40;
            Margins[0].BackColor = Color.FromArgb(24, 24, 24);
            Styles[Style.LineNumber].ForeColor = Color.FromArgb(100, 100, 100);
            Styles[Style.LineNumber].BackColor = Color.FromArgb(24, 24, 24);

            // Margin 1: fold margin (the clickable [+]/[-] gutter)
            Margins[1].Type  = MarginType.Symbol;
            Margins[1].Mask  = Marker.MaskFolders;
            Margins[1].Sensitive = true;   // clicks toggle fold state
            Margins[1].Width = 16;
            Margins[1].BackColor = Color.FromArgb(24, 24, 24);

            // Disable margin 2
            Margins[2].Width = 0;

            // ── Fold marker glyphs (VS Code-style box expand/collapse) ─────────────
            // FolderOpen / FolderOpenMid : expanded node  [−]
            Markers[Marker.FolderOpen].Symbol    = MarkerSymbol.BoxMinusConnected;
            Markers[Marker.FolderOpen].SetForeColor(Color.FromArgb(180, 180, 180));
            Markers[Marker.FolderOpen].SetBackColor(Color.FromArgb(40,  40,  40));
            Markers[Marker.Folder].Symbol        = MarkerSymbol.BoxPlusConnected;
            Markers[Marker.Folder].SetForeColor(Color.FromArgb(180, 180, 180));
            Markers[Marker.Folder].SetBackColor(Color.FromArgb(40,  40,  40));
            Markers[Marker.FolderSub].Symbol     = MarkerSymbol.VLine;
            Markers[Marker.FolderSub].SetForeColor(Color.FromArgb(80, 80, 80));
            Markers[Marker.FolderSub].SetBackColor(Color.FromArgb(24, 24, 24));
            Markers[Marker.FolderTail].Symbol    = MarkerSymbol.LCorner;
            Markers[Marker.FolderTail].SetForeColor(Color.FromArgb(80, 80, 80));
            Markers[Marker.FolderTail].SetBackColor(Color.FromArgb(24, 24, 24));
            Markers[Marker.FolderEnd].Symbol     = MarkerSymbol.BoxPlusConnected;
            Markers[Marker.FolderEnd].SetForeColor(Color.FromArgb(180, 180, 180));
            Markers[Marker.FolderEnd].SetBackColor(Color.FromArgb(40,  40,  40));
            Markers[Marker.FolderOpenMid].Symbol = MarkerSymbol.BoxMinusConnected;
            Markers[Marker.FolderOpenMid].SetForeColor(Color.FromArgb(180, 180, 180));
            Markers[Marker.FolderOpenMid].SetBackColor(Color.FromArgb(40,  40,  40));
            Markers[Marker.FolderMidTail].Symbol = MarkerSymbol.TCorner;
            Markers[Marker.FolderMidTail].SetForeColor(Color.FromArgb(80, 80, 80));
            Markers[Marker.FolderMidTail].SetBackColor(Color.FromArgb(24, 24, 24));

            // ── Folding behaviour ─────────────────────────────────────────────
            SetProperty("fold",                     "1");
            SetProperty("fold.compact",             "0"); // don't collapse blank lines into fold
            AutomaticFold = AutomaticFold.Show | AutomaticFold.Click | AutomaticFold.Change;

            // ── Editor settings ───────────────────────────────────────────────
            TabWidth = 4;
            UseTabs = false;
            ViewWhitespace = WhitespaceMode.Invisible;
            IndentationGuides = IndentView.LookBoth;
            WrapMode = WrapMode.None;
            CaretForeColor = Color.White;
            CaretLineVisible = true;
            CaretLineBackColor = Color.FromArgb(30, 30, 30);
            SetSelectionBackColor(true, Color.FromArgb(38, 79, 120));
            SetSelectionForeColor(false, Color.Transparent);
            BorderStyle = (ScintillaNET.BorderStyle)0; // None

            // ── Scrollbar styling ─────────────────────────────────────────────
            HScrollBar = true;
            VScrollBar = true;

            // ── Indicators for diagnostics ────────────────────────────────────
            Indicators[IndicatorError].Style = IndicatorStyle.Squiggle;
            Indicators[IndicatorError].ForeColor = Color.FromArgb(255, 80, 80);
            Indicators[IndicatorError].Under = true;

            Indicators[IndicatorWarning].Style = IndicatorStyle.Squiggle;
            Indicators[IndicatorWarning].ForeColor = Color.FromArgb(220, 180, 50);
            Indicators[IndicatorWarning].Under = true;

            Indicators[IndicatorInfo].Style = IndicatorStyle.Dots;
            Indicators[IndicatorInfo].ForeColor = Color.FromArgb(80, 180, 255);
            Indicators[IndicatorInfo].Under = true;

            Indicators[IndicatorHeatmap].Style = IndicatorStyle.FullBox;
            Indicators[IndicatorHeatmap].Alpha = 128;
            Indicators[IndicatorHeatmap].OutlineAlpha = 40;
            Indicators[IndicatorHeatmap].Under = true;
            Indicators[IndicatorHeatmap].ForeColor = Color.FromArgb(40, 120, 40);   // green (fast)

            Indicators[IndicatorHeatmap2].Style = IndicatorStyle.FullBox;
            Indicators[IndicatorHeatmap2].Alpha = 128;
            Indicators[IndicatorHeatmap2].OutlineAlpha = 40;
            Indicators[IndicatorHeatmap2].Under = true;
            Indicators[IndicatorHeatmap2].ForeColor = Color.FromArgb(140, 140, 40);  // yellow

            Indicators[IndicatorHeatmap3].Style = IndicatorStyle.FullBox;
            Indicators[IndicatorHeatmap3].Alpha = 128;
            Indicators[IndicatorHeatmap3].OutlineAlpha = 40;
            Indicators[IndicatorHeatmap3].Under = true;
            Indicators[IndicatorHeatmap3].ForeColor = Color.FromArgb(200, 120, 40); // orange

            Indicators[IndicatorHeatmap4].Style = IndicatorStyle.FullBox;
            Indicators[IndicatorHeatmap4].Alpha = 128;
            Indicators[IndicatorHeatmap4].OutlineAlpha = 40;
            Indicators[IndicatorHeatmap4].Under = true;
            Indicators[IndicatorHeatmap4].ForeColor = Color.FromArgb(220, 50, 50); // red (slow)

            // ── Multi-caret / selection ────────────────────────────────────────
            MultipleSelection = false;

            // ── Word occurrence highlight ──────────────────────────────────────
            // Subtle box outline — same feel as VS / VS Code "other references"
            Indicators[IndicatorWordHighlight].Style        = IndicatorStyle.StraightBox;
            Indicators[IndicatorWordHighlight].ForeColor    = Color.FromArgb(100, 150, 200);
            Indicators[IndicatorWordHighlight].Alpha        = 40;
            Indicators[IndicatorWordHighlight].OutlineAlpha = 140;
            Indicators[IndicatorWordHighlight].Under        = false;

            // ── Brace matching highlight styles ───────────────────────────────
            // BraceLight: matched pair — gold text on a subtle dark-gold background
            Styles[Style.BraceLight].ForeColor = Color.FromArgb(255, 215, 0);   // gold
            Styles[Style.BraceLight].BackColor = Color.FromArgb(50, 45, 10);    // dark gold tint
            Styles[Style.BraceLight].Bold = true;
            // BraceBad: unmatched brace — red so the user knows there is no partner
            Styles[Style.BraceBad].ForeColor  = Color.FromArgb(255, 80, 80);    // red
            Styles[Style.BraceBad].BackColor  = Color.FromArgb(50, 10, 10);     // dark red tint
            Styles[Style.BraceBad].Bold = true;
        }

        // ── Brace matching ────────────────────────────────────────────────────

        private static bool IsBrace(char c)
            => c == '{' || c == '}' || c == '(' || c == ')' || c == '[' || c == ']';

        /// <summary>
        /// Fires on every caret move / selection change. Highlights the brace pair
        /// under or immediately before the caret, or marks it red if unmatched.
        /// Also clears word-occurrence highlights when the caret leaves the word.
        /// </summary>
        protected override void OnUpdateUI(UpdateUIEventArgs e)
        {
            base.OnUpdateUI(e);

            // ── Brace highlight ───────────────────────────────────────────────
            int caret = CurrentPosition;
            int bracePos = -1;
            if (caret > 0 && IsBrace((char)GetCharAt(caret - 1)))
                bracePos = caret - 1;
            else if (caret < TextLength && IsBrace((char)GetCharAt(caret)))
                bracePos = caret;

            if (bracePos < 0)
                BraceHighlight(-1, -1);
            else
            {
                int matchPos = BraceMatch(bracePos);
                if (matchPos == InvalidPosition)
                    BraceBadLight(bracePos);
                else
                    BraceHighlight(bracePos, matchPos);
            }

            // ── Word highlight: clear when caret moves off the highlighted word ─
            if (_wordHighlight != null)
            {
                int wStart = WordStartPosition(caret, true);
                int wEnd   = WordEndPosition(caret, true);
                string current = wEnd > wStart ? GetTextRange(wStart, wEnd - wStart) : "";
                if (current != _wordHighlight)
                    ClearWordHighlights();
            }
        }

        // ── Word occurrence highlight ──────────────────────────────────────────

        /// <summary>
        /// Double-click: highlight all occurrences of the word under the caret.
        /// </summary>
        protected override void OnDoubleClick(DoubleClickEventArgs e)
        {
            base.OnDoubleClick(e);
            HighlightWordUnderCaret();
        }

        private void HighlightWordUnderCaret()
        {
            int pos    = CurrentPosition;
            int wStart = WordStartPosition(pos, true);
            int wEnd   = WordEndPosition(pos, true);

            if (wEnd <= wStart)
            { ClearWordHighlights(); return; }

            string word = GetTextRange(wStart, wEnd - wStart);

            // Only highlight identifier-like words of 2+ chars
            if (word.Length < 2 || !IsWordChar(word[0]))
            { ClearWordHighlights(); return; }

            _wordHighlight = word;

            IndicatorCurrent = IndicatorWordHighlight;
            IndicatorClearRange(0, TextLength);

            string text = Text;
            int search = 0;
            while ((search = text.IndexOf(word, search, StringComparison.Ordinal)) >= 0)
            {
                bool startOk = search == 0 || !IsWordChar(text[search - 1]);
                bool endOk   = search + word.Length >= text.Length || !IsWordChar(text[search + word.Length]);
                if (startOk && endOk)
                    IndicatorFillRange(search, word.Length);
                search += word.Length;
            }
        }

        private void ClearWordHighlights()
        {
            _wordHighlight = null;
            IndicatorCurrent = IndicatorWordHighlight;
            IndicatorClearRange(0, TextLength);
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        // ── Auto-indent & auto-close brackets ─────────────────────────────────

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled) return;

            if (e.KeyCode == Keys.Enter && !e.Control && !e.Alt)
            {
                e.Handled = true;
                HandleEnter();
            }
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (e.Handled) return;

            switch (e.KeyChar)
            {
                case '{': e.Handled = true; InsertPair('{', '}'); break;
                case '(': e.Handled = true; InsertPair('(', ')'); break;
                case '[': e.Handled = true; InsertPair('[', ']'); break;
                case '"': e.Handled = true; InsertPair('"', '"'); break;
                case '}': e.Handled = true; SkipOrInsert('}'); break;
                case ')': e.Handled = true; SkipOrInsert(')'); break;
                case ']': e.Handled = true; SkipOrInsert(']'); break;
            }
        }

        private void HandleEnter()
        {
            int pos      = CurrentPosition;
            int line     = LineFromPosition(pos);
            string indent = GetLineIndent(line);

            // If the character before the caret is '{', add one extra indent level.
            int lookBack = pos - 1;
            while (lookBack >= 0 && GetCharAt(lookBack) == ' ') lookBack--;
            bool afterBrace = lookBack >= 0 && (char)GetCharAt(lookBack) == '{';

            // If the character at the caret is '}', put it on its own dedented line.
            int lookAhead = pos;
            while (lookAhead < TextLength && GetCharAt(lookAhead) == ' ') lookAhead++;
            bool beforeBrace = lookAhead < TextLength && (char)GetCharAt(lookAhead) == '}';

            if (afterBrace && beforeBrace)
            {
                // Split: new line for body (indented) + new line for closing brace (same as opener)
                string bodyIndent = indent + "    ";
                string insertion  = "\n" + bodyIndent + "\n" + indent;
                int insertPos     = pos;
                InsertText(insertPos, insertion);
                SetEmptySelection(insertPos + 1 + bodyIndent.Length);
            }
            else if (afterBrace)
            {
                string bodyIndent = indent + "    ";
                string insertion  = "\n" + bodyIndent;
                InsertText(pos, insertion);
                SetEmptySelection(pos + insertion.Length);
            }
            else
            {
                string insertion = "\n" + indent;
                InsertText(pos, insertion);
                SetEmptySelection(pos + insertion.Length);
            }

            ScrollCaret();
        }

        private void InsertPair(char open, char close)
        {
            int selStart = Math.Min(base.SelectionStart, base.SelectionEnd);
            int selEnd   = Math.Max(base.SelectionStart, base.SelectionEnd);

            if (selEnd > selStart)
            {
                // Wrap selection
                string selected = GetTextRange(selStart, selEnd - selStart);
                ReplaceSelection(open + selected + close);
                SetEmptySelection(selStart + 1 + selected.Length);
            }
            else
            {
                ReplaceSelection(open.ToString() + close.ToString());
                SetEmptySelection(selStart + 1);
            }
        }

        private void SkipOrInsert(char close)
        {
            int pos = CurrentPosition;
            if (pos < TextLength && (char)GetCharAt(pos) == close)
                SetEmptySelection(pos + 1); // skip over the already-inserted closer
            else
                ReplaceSelection(close.ToString());
        }

        private string GetLineIndent(int line)
        {
            if (line < 0 || line >= Lines.Count) return "";
            string text = Lines[line].Text.TrimEnd('\r', '\n');
            int len = text.Length - text.TrimStart(' ', '\t').Length;
            return text.Substring(0, len);
        }

        // ── RichTextBox-compatible API shims ──────────────────────────────────

        /// <summary>
        /// Gets/sets the caret position (RichTextBox SelectionStart equivalent).
        /// Note: Scintilla already has SelectionStart but it can be > SelectionEnd
        /// when selection is made right-to-left.  This always returns the lower bound.
        /// </summary>
        public new int SelectionStart
        {
            get => Math.Min(base.SelectionStart, base.SelectionEnd);
            set => SetEmptySelection(value);
        }

        /// <summary>Length of the current selection.</summary>
        public int SelectionLength
        {
            get => Math.Abs(base.SelectionEnd - base.SelectionStart);
            set
            {
                int start = SelectionStart;
                SetSelection(start + value, start);
            }
        }

        /// <summary>
        /// Gets/sets the selected text. Setter replaces the current selection.
        /// </summary>
        public string SelectedText
        {
            get
            {
                int s = Math.Min(base.SelectionStart, base.SelectionEnd);
                int e = Math.Max(base.SelectionStart, base.SelectionEnd);
                if (e <= s) return "";
                return GetTextRange(s, e - s);
            }
            set
            {
                ReplaceSelection(value ?? "");
            }
        }

        /// <summary>Selects a range of text (RichTextBox.Select compatible).</summary>
        public void Select(int start, int length)
        {
            int safeStart = Math.Max(0, Math.Min(start, TextLength));
            int safeEnd = Math.Max(safeStart, Math.Min(start + length, TextLength));
            SetSelection(safeEnd, safeStart);
        }

        /// <summary>Selects all text.</summary>
        public new void SelectAll()
        {
            SetSelection(0, TextLength);
        }

        /// <summary>Scrolls to the caret position.</summary>
        public void ScrollToCaret()
        {
            ScrollCaret();
        }

        /// <summary>
        /// Ensures a line is visible by expanding any folded regions that contain it.
        /// Equivalent to Scintilla SCI_ENSUREVISIBLE.
        /// </summary>
        public void EnsureVisible(int line)
        {
            Lines[line].EnsureVisible();
        }

        /// <summary>RichTextBox.GetCharIndexFromPosition compatible.</summary>
        public int GetCharIndexFromPosition(Point pt)
        {
            int pos = CharPositionFromPoint(pt.X, pt.Y);
            return pos >= 0 ? pos : 0;
        }

        /// <summary>RichTextBox.GetPositionFromCharIndex compatible.</summary>
        public Point GetPositionFromCharIndex(int charIndex)
        {
            int line = LineFromPosition(charIndex);
            int x = PointXFromPosition(charIndex);
            int y = PointYFromPosition(charIndex);
            return new Point(x, y);
        }

        /// <summary>RichTextBox.GetLineFromCharIndex compatible.</summary>
        public int GetLineFromCharIndex(int charIndex)
        {
            return LineFromPosition(charIndex);
        }

        /// <summary>RichTextBox.GetFirstCharIndexFromLine compatible.</summary>
        public int GetFirstCharIndexFromLine(int lineNumber)
        {
            if (lineNumber < 0 || lineNumber >= Lines.Count) return -1;
            return Lines[lineNumber].Position;
        }

        /// <summary>RichTextBox.Paste compatible.</summary>
        public new void Paste()
        {
            base.Paste();
        }

        // ── Heatmap background colouring (replaces SelectionBackColor) ────────

        // Pre-defined heat band colors matching the 4 indicators
        private static readonly Color[] _heatBandColors = {
            Color.FromArgb(40, 120, 40),   // band 0: green
            Color.FromArgb(140, 140, 40),  // band 1: yellow
            Color.FromArgb(200, 120, 40),  // band 2: orange
            Color.FromArgb(220, 50, 50),   // band 3: red
        };

        /// <summary>
        /// Sets background colour for a character range using the closest heatmap band.
        /// </summary>
        public void SetRangeBackColor(int start, int length, Color color)
        {
            if (length <= 0) return;
            // Pick closest band by color distance
            int best = 0;
            double bestDist = double.MaxValue;
            for (int i = 0; i < _heatBandColors.Length; i++)
            {
                var c = _heatBandColors[i];
                double d = Math.Pow(c.R - color.R, 2) + Math.Pow(c.G - color.G, 2) + Math.Pow(c.B - color.B, 2);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            IndicatorCurrent = HeatmapIndicators[best];
            IndicatorFillRange(start, length);
        }

        /// <summary>Clears all heatmap background colouring.</summary>
        public void ClearBackColors()
        {
            int len = TextLength;
            for (int i = 0; i < HeatmapIndicators.Length; i++)
            {
                IndicatorCurrent = HeatmapIndicators[i];
                IndicatorClearRange(0, len);
            }
        }
    }
}
