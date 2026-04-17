using System;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SESpriteLCDLayoutTool.Controls
{
    /// <summary>
    /// Inline Find &amp; Replace bar for the code editor RichTextBox.
    /// Docked at the top of the code panel, toggled by Ctrl+F (find) / Ctrl+H (find+replace).
    /// </summary>
    internal sealed class CodeFindReplaceBar : Panel
    {
        private readonly RichTextBox _codeBox;
        private readonly TextBox _txtFind;
        private readonly TextBox _txtReplace;
        private readonly Label _lblStatus;
        private readonly Panel _replaceRow;
        private readonly CheckBox _chkCase;
        private readonly CheckBox _chkWholeWord;
        private readonly CheckBox _chkRegex;

        private int _lastMatchStart = -1;

        public CodeFindReplaceBar(RichTextBox codeBox)
        {
            _codeBox = codeBox ?? throw new ArgumentNullException(nameof(codeBox));

            Dock = DockStyle.Top;
            AutoSize = true;
            BackColor = Color.FromArgb(30, 30, 30);
            Padding = new Padding(6, 4, 6, 4);
            Visible = false;

            // ── Find row ──
            var findRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 28,
                AutoSize = false,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent,
            };

            var lblFind = new Label
            {
                Text = "Find:",
                Width = 42,
                Height = 24,
                ForeColor = Color.FromArgb(180, 180, 180),
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 8.5f),
            };

            _txtFind = new TextBox
            {
                Width = 220,
                Height = 24,
                Font = new Font("Consolas", 9f),
                BackColor = Color.FromArgb(18, 18, 24),
                ForeColor = Color.FromArgb(220, 220, 220),
                BorderStyle = BorderStyle.FixedSingle,
            };
            _txtFind.KeyDown += OnFindKeyDown;

            var btnNext = MiniButton("▼ Next", 60);
            btnNext.Click += (s, e) => FindNext();

            var btnPrev = MiniButton("▲ Prev", 60);
            btnPrev.Click += (s, e) => FindPrevious();

            _chkCase = new CheckBox
            {
                Text = "Aa",
                ForeColor = Color.FromArgb(160, 160, 160),
                Font = new Font("Consolas", 8f, FontStyle.Bold),
                AutoSize = true,
                Padding = new Padding(4, 0, 0, 0),
            };
            _chkCase.CheckedChanged += (s, e) => _lastMatchStart = -1;

            _chkWholeWord = new CheckBox
            {
                Text = "W",
                ForeColor = Color.FromArgb(160, 160, 160),
                Font = new Font("Consolas", 8f, FontStyle.Bold),
                AutoSize = true,
                Padding = new Padding(2, 0, 0, 0),
            };
            _chkWholeWord.CheckedChanged += (s, e) => _lastMatchStart = -1;

            _chkRegex = new CheckBox
            {
                Text = ".*",
                ForeColor = Color.FromArgb(160, 160, 160),
                Font = new Font("Consolas", 8f, FontStyle.Bold),
                AutoSize = true,
                Padding = new Padding(2, 0, 0, 0),
            };
            _chkRegex.CheckedChanged += (s, e) => _lastMatchStart = -1;

            _lblStatus = new Label
            {
                Text = "",
                Width = 100,
                Height = 24,
                ForeColor = Color.FromArgb(140, 140, 140),
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 8f),
            };

            var btnClose = MiniButton("✕", 28);
            btnClose.Click += (s, e) => Hide();

            findRow.Controls.Add(lblFind);
            findRow.Controls.Add(_txtFind);
            findRow.Controls.Add(btnNext);
            findRow.Controls.Add(btnPrev);
            findRow.Controls.Add(_chkCase);
            findRow.Controls.Add(_chkWholeWord);
            findRow.Controls.Add(_chkRegex);
            findRow.Controls.Add(_lblStatus);
            findRow.Controls.Add(btnClose);

            // ── Replace row ──
            _replaceRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 28,
                AutoSize = false,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent,
                Visible = false,
            };

            var lblReplace = new Label
            {
                Text = "Replace:",
                Width = 56,
                Height = 24,
                ForeColor = Color.FromArgb(180, 180, 180),
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 8.5f),
            };

            _txtReplace = new TextBox
            {
                Width = 206,
                Height = 24,
                Font = new Font("Consolas", 9f),
                BackColor = Color.FromArgb(18, 18, 24),
                ForeColor = Color.FromArgb(220, 220, 220),
                BorderStyle = BorderStyle.FixedSingle,
            };
            _txtReplace.KeyDown += OnReplaceKeyDown;

            var btnReplace = MiniButton("Replace", 60);
            btnReplace.Click += (s, e) => ReplaceNext();

            var btnReplaceAll = MiniButton("All", 36);
            btnReplaceAll.Click += (s, e) => ReplaceAll();

            _replaceRow.Controls.Add(lblReplace);
            _replaceRow.Controls.Add(_txtReplace);
            _replaceRow.Controls.Add(btnReplace);
            _replaceRow.Controls.Add(btnReplaceAll);

            // Add rows (reverse order because DockStyle.Top stacks bottom-up)
            Controls.Add(_replaceRow);
            Controls.Add(findRow);
        }

        // ── Public API ──

        /// <summary>Show find bar (Ctrl+F). Pre-fill with selected text if any.</summary>
        public void ShowFind()
        {
            _replaceRow.Visible = false;
            ShowBar();
        }

        /// <summary>Show find+replace bar (Ctrl+H). Pre-fill with selected text if any.</summary>
        public void ShowFindReplace()
        {
            _replaceRow.Visible = true;
            ShowBar();
        }

        public void FindNext() => DoFind(forward: true);
        public void FindPrevious() => DoFind(forward: false);

        public void ReplaceNext()
        {
            // If current selection matches the search, replace it then find next
            string find = _txtFind.Text;
            if (string.IsNullOrEmpty(find)) return;

            string sel = _codeBox.SelectedText;
            if (!string.IsNullOrEmpty(sel) && IsMatch(sel, find))
            {
                string replacement = _txtReplace.Text ?? "";
                if (_chkRegex.Checked)
                {
                    var opts = _chkCase.Checked ? RegexOptions.None : RegexOptions.IgnoreCase;
                    replacement = Regex.Replace(sel, BuildPattern(find), replacement, opts);
                }
                _codeBox.SelectedText = replacement;
            }
            FindNext();
        }

        public void ReplaceAll()
        {
            string find = _txtFind.Text;
            if (string.IsNullOrEmpty(find)) return;
            string replacement = _txtReplace.Text ?? "";

            string text = _codeBox.Text;
            string newText;
            int count;

            if (_chkRegex.Checked)
            {
                var opts = _chkCase.Checked ? RegexOptions.None : RegexOptions.IgnoreCase;
                var regex = new Regex(BuildPattern(find), opts);
                count = regex.Matches(text).Count;
                newText = regex.Replace(text, replacement);
            }
            else
            {
                var comparison = _chkCase.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                count = 0;
                var sb = new System.Text.StringBuilder();
                int pos = 0;
                while (pos < text.Length)
                {
                    int idx = text.IndexOf(find, pos, comparison);
                    if (idx < 0)
                    {
                        sb.Append(text, pos, text.Length - pos);
                        break;
                    }

                    // Whole word check
                    if (_chkWholeWord.Checked && !IsWholeWord(text, idx, find.Length))
                    {
                        sb.Append(text, pos, idx - pos + 1);
                        pos = idx + 1;
                        continue;
                    }

                    sb.Append(text, pos, idx - pos);
                    sb.Append(replacement);
                    count++;
                    pos = idx + find.Length;
                }
                newText = sb.ToString();
            }

            if (count > 0)
            {
                int caretPos = _codeBox.SelectionStart;
                _codeBox.Text = newText;
                if (caretPos <= _codeBox.TextLength)
                    _codeBox.SelectionStart = caretPos;
            }

            _lblStatus.Text = $"{count} replaced";
        }

        // ── Internals ──

        private void ShowBar()
        {
            string sel = _codeBox.SelectedText;
            if (!string.IsNullOrEmpty(sel) && !sel.Contains("\n"))
                _txtFind.Text = sel;

            Visible = true;
            _txtFind.Focus();
            _txtFind.SelectAll();
            _lastMatchStart = -1;
            _lblStatus.Text = "";
        }

        private void DoFind(bool forward)
        {
            string find = _txtFind.Text;
            if (string.IsNullOrEmpty(find))
            {
                _lblStatus.Text = "";
                return;
            }

            string text = _codeBox.Text;
            if (string.IsNullOrEmpty(text))
            {
                _lblStatus.Text = "No text";
                return;
            }

            int startPos = forward
                ? (_codeBox.SelectionStart + _codeBox.SelectionLength)
                : (_codeBox.SelectionStart - 1);

            int matchIndex = -1;
            int matchLength = find.Length;

            if (_chkRegex.Checked)
            {
                var opts = _chkCase.Checked ? RegexOptions.None : RegexOptions.IgnoreCase;
                var regex = new Regex(BuildPattern(find), opts);
                if (forward)
                {
                    if (startPos < 0) startPos = 0;
                    var m = regex.Match(text, startPos);
                    if (!m.Success) m = regex.Match(text, 0); // wrap
                    if (m.Success) { matchIndex = m.Index; matchLength = m.Length; }
                }
                else
                {
                    // Reverse: find all matches up to startPos, take last
                    Match best = null;
                    foreach (Match m in regex.Matches(text))
                    {
                        if (m.Index < startPos) best = m;
                        else if (best == null)
                        {
                            // Wrap: take last match in entire document
                            Match wrap = null;
                            foreach (Match w in regex.Matches(text)) wrap = w;
                            best = wrap;
                            break;
                        }
                        else break;
                    }
                    if (best != null) { matchIndex = best.Index; matchLength = best.Length; }
                }
            }
            else
            {
                var comparison = _chkCase.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                if (forward)
                {
                    if (startPos < 0) startPos = 0;
                    matchIndex = FindWithWholeWord(text, find, startPos, comparison);
                    if (matchIndex < 0) matchIndex = FindWithWholeWord(text, find, 0, comparison); // wrap
                }
                else
                {
                    if (startPos < 0) startPos = text.Length - 1;
                    matchIndex = FindBackwardWithWholeWord(text, find, startPos, comparison);
                    if (matchIndex < 0) matchIndex = FindBackwardWithWholeWord(text, find, text.Length - 1, comparison); // wrap
                }
            }

            if (matchIndex >= 0)
            {
                _codeBox.Select(matchIndex, matchLength);
                _codeBox.ScrollToCaret();
                _lastMatchStart = matchIndex;

                // Count total matches for status
                int total = CountMatches(text, find);
                _lblStatus.Text = total > 0 ? $"{total} matches" : "";
            }
            else
            {
                _lblStatus.Text = "Not found";
                _lblStatus.ForeColor = Color.FromArgb(255, 120, 100);
                var timer = new Timer { Interval = 2000 };
                timer.Tick += (s, e) => { _lblStatus.ForeColor = Color.FromArgb(140, 140, 140); timer.Stop(); timer.Dispose(); };
                timer.Start();
            }
        }

        private int FindWithWholeWord(string text, string find, int startPos, StringComparison comparison)
        {
            int pos = startPos;
            while (pos < text.Length)
            {
                int idx = text.IndexOf(find, pos, comparison);
                if (idx < 0) return -1;
                if (!_chkWholeWord.Checked || IsWholeWord(text, idx, find.Length))
                    return idx;
                pos = idx + 1;
            }
            return -1;
        }

        private int FindBackwardWithWholeWord(string text, string find, int startPos, StringComparison comparison)
        {
            int pos = Math.Min(startPos, text.Length - find.Length);
            while (pos >= 0)
            {
                int idx = text.LastIndexOf(find, pos, pos + 1, comparison);
                if (idx < 0) return -1;
                if (!_chkWholeWord.Checked || IsWholeWord(text, idx, find.Length))
                    return idx;
                pos = idx - 1;
            }
            return -1;
        }

        private static bool IsWholeWord(string text, int index, int length)
        {
            if (index > 0 && IsWordChar(text[index - 1])) return false;
            int end = index + length;
            if (end < text.Length && IsWordChar(text[end])) return false;
            return true;
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        private bool IsMatch(string selected, string find)
        {
            if (_chkRegex.Checked)
            {
                var opts = _chkCase.Checked ? RegexOptions.None : RegexOptions.IgnoreCase;
                return Regex.IsMatch(selected, "^" + BuildPattern(find) + "$", opts);
            }
            var comparison = _chkCase.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return string.Equals(selected, find, comparison);
        }

        private string BuildPattern(string find)
        {
            string pattern = _chkRegex.Checked ? find : Regex.Escape(find);
            if (_chkWholeWord.Checked && !_chkRegex.Checked)
                pattern = @"\b" + pattern + @"\b";
            return pattern;
        }

        private int CountMatches(string text, string find)
        {
            if (_chkRegex.Checked)
            {
                var opts = _chkCase.Checked ? RegexOptions.None : RegexOptions.IgnoreCase;
                try { return Regex.Matches(text, BuildPattern(find), opts).Count; }
                catch { return 0; }
            }

            var comparison = _chkCase.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int count = 0, pos = 0;
            while (pos < text.Length)
            {
                int idx = text.IndexOf(find, pos, comparison);
                if (idx < 0) break;
                if (!_chkWholeWord.Checked || IsWholeWord(text, idx, find.Length))
                    count++;
                pos = idx + 1;
            }
            return count;
        }

        private void OnFindKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                if (e.Shift) FindPrevious(); else FindNext();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                Hide();
                _codeBox.Focus();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void OnReplaceKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                ReplaceNext();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                Hide();
                _codeBox.Focus();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private static Button MiniButton(string text, int width)
        {
            return new Button
            {
                Text = text,
                Width = width,
                Height = 24,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(50, 50, 60),
                ForeColor = Color.FromArgb(200, 200, 200),
                Font = new Font("Segoe UI", 8f),
                Cursor = Cursors.Hand,
                Margin = new Padding(2, 0, 2, 0),
            };
        }
    }
}
