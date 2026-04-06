using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SESpriteLCDLayoutTool.Controls
{
    /// <summary>
    /// A gutter control that draws line numbers alongside a <see cref="RichTextBox"/>.
    /// Dock it <see cref="DockStyle.Left"/> next to the editor so it fills the left edge.
    /// </summary>
    internal sealed class LineNumberGutter : Control
    {
        private const int WM_VSCROLL  = 0x0115;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_KEYDOWN = 0x0100;

        private readonly RichTextBox _editor;
        private int _lineCount;
        private int _currentLine = -1;

        // Cached drawing objects
        private readonly StringFormat _sf;
        private readonly Pen _separatorPen;

        public LineNumberGutter(RichTextBox editor)
        {
            _editor = editor ?? throw new ArgumentNullException(nameof(editor));

            SetStyle(ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw, true);

            TabStop   = false;
            Cursor    = Cursors.Default;
            BackColor = Color.FromArgb(24, 24, 24);
            ForeColor = Color.FromArgb(100, 100, 100);
            Font      = new Font("Consolas", 9f);
            Dock      = DockStyle.Left;
            Width     = 40;
            Padding   = new Padding(4, 0, 6, 0);

            _sf = new StringFormat(StringFormatFlags.NoWrap)
            {
                Alignment     = StringAlignment.Far,
                LineAlignment = StringAlignment.Near,
            };

            _separatorPen = new Pen(Color.FromArgb(50, 50, 50));

            // Hook editor events
            _editor.TextChanged        += (s, e) => OnEditorChanged();
            _editor.VScroll            += (s, e) => Invalidate();
            _editor.SelectionChanged   += (s, e) => UpdateCurrentLine();
            _editor.Resize             += (s, e) => Invalidate();

            // Subclass the editor to catch scroll messages that don't fire VScroll
            _editorFilter = new EditorMessageFilter(this);
            _editor.HandleCreated += (s, e) =>
            {
                if (!_editor.IsDisposed)
                    _editorFilter.Attach(_editor);
            };

            OnEditorChanged();
        }

        private readonly EditorMessageFilter _editorFilter;

        private void OnEditorChanged()
        {
            int newCount = _editor.GetLineFromCharIndex(_editor.TextLength) + 1;
            if (newCount != _lineCount)
            {
                _lineCount = newCount;
                RecalcWidth();
            }
            UpdateCurrentLine();
            Invalidate();
        }

        private void UpdateCurrentLine()
        {
            int line = _editor.GetLineFromCharIndex(_editor.SelectionStart);
            if (line != _currentLine)
            {
                _currentLine = line;
                Invalidate();
            }
        }

        private void RecalcWidth()
        {
            int digits = Math.Max(2, _lineCount.ToString().Length);
            using (var g = CreateGraphics())
            {
                float charW = g.MeasureString("0", Font).Width;
                Width = (int)(charW * digits) + Padding.Left + Padding.Right + 2;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;

            g.Clear(BackColor);

            // Draw separator line on the right edge
            int sepX = Width - 1;
            g.DrawLine(_separatorPen, sepX, 0, sepX, Height);

            if (_editor.TextLength == 0)
            {
                // Still draw line 1
                DrawLineNumber(g, 1, 0, _currentLine == 0);
                return;
            }

            // Get the character index at the top of the visible area
            int firstCharIdx = _editor.GetCharIndexFromPosition(new Point(0, 0));
            int firstLine    = _editor.GetLineFromCharIndex(firstCharIdx);

            // Walk visible lines
            using (var normalBrush  = new SolidBrush(ForeColor))
            using (var currentBrush = new SolidBrush(Color.FromArgb(200, 200, 200)))
            {
                for (int line = firstLine; line <= _lineCount; line++)
                {
                    int charIdx = _editor.GetFirstCharIndexFromLine(line);
                    if (charIdx < 0) break;

                    Point pos = _editor.GetPositionFromCharIndex(charIdx);
                    int y = pos.Y;

                    // Stop once we're past the visible area
                    if (y > _editor.ClientSize.Height) break;

                    bool isCurrent = (line == _currentLine);
                    var brush = isCurrent ? currentBrush : normalBrush;

                    var rect = new RectangleF(
                        Padding.Left,
                        y,
                        Width - Padding.Left - Padding.Right - 2,
                        Font.Height);

                    g.DrawString((line + 1).ToString(), Font, brush, rect, _sf);
                }
            }
        }

        private void DrawLineNumber(Graphics g, int number, int y, bool isCurrent)
        {
            using (var brush = new SolidBrush(isCurrent ? Color.FromArgb(200, 200, 200) : ForeColor))
            {
                var rect = new RectangleF(
                    Padding.Left,
                    y,
                    Width - Padding.Left - Padding.Right - 2,
                    Font.Height);
                g.DrawString(number.ToString(), Font, brush, rect, _sf);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            // Don't steal focus from the editor
            _editor.Focus();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _sf?.Dispose();
                _separatorPen?.Dispose();
                _editorFilter?.ReleaseHandle();
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// Intercepts WM_VSCROLL / WM_MOUSEWHEEL on the editor to keep the
        /// gutter in sync even when <see cref="RichTextBox.VScroll"/> doesn't fire.
        /// </summary>
        private class EditorMessageFilter : System.Windows.Forms.NativeWindow
        {
            private readonly LineNumberGutter _gutter;

            public EditorMessageFilter(LineNumberGutter gutter)
            {
                _gutter = gutter;
            }

            public void Attach(RichTextBox editor)
            {
                if (Handle == IntPtr.Zero)
                    AssignHandle(editor.Handle);
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);

                switch (m.Msg)
                {
                    case WM_VSCROLL:
                    case WM_MOUSEWHEEL:
                    case WM_KEYDOWN:
                        _gutter.Invalidate();
                        break;
                }
            }
        }
    }
}
