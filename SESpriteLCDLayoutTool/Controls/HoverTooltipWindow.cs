using System;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;
using Microsoft.CodeAnalysis;

namespace SESpriteLCDLayoutTool.Controls
{
    /// <summary>
    /// VS Code-style hover popup for semantic type info and diagnostic messages.
    /// Borderless, non-activating, owner-drawn with a dark theme and a coloured
    /// left accent bar that reflects the content kind (error/warning/info/hover).
    /// </summary>
    internal sealed class HoverTooltipWindow : Form
    {
        // ── Colours ──────────────────────────────────────────────────────────
        private static readonly Color BgColor        = Color.FromArgb(30, 30, 30);
        private static readonly Color BorderColor     = Color.FromArgb(80, 80, 80);
        private static readonly Color KindColor       = Color.FromArgb(130, 130, 145);
        private static readonly Color SigColor        = Color.FromArgb(220, 220, 220);
        private static readonly Color AccentError     = Color.FromArgb(240,  70,  70);
        private static readonly Color AccentWarning   = Color.FromArgb(220, 170,  40);
        private static readonly Color AccentInfo      = Color.FromArgb( 80, 170, 255);
        private static readonly Color AccentHover     = Color.FromArgb( 78, 201, 176); // teal — matches StyleType

        // ── Layout ───────────────────────────────────────────────────────────
        private const int AccentW   = 3;   // left accent bar width
        private const int PadH      = 10;  // horizontal padding (after accent)
        private const int PadV      = 7;   // vertical padding
        private const int MaxWidth  = 640;
        private const int MinWidth  = 120;

        // ── Fonts ────────────────────────────────────────────────────────────
        private static readonly Font _kindFont = new Font("Segoe UI",  8.5f, FontStyle.Regular, GraphicsUnit.Point);
        private static readonly Font _sigFont  = new Font("Consolas", 9.5f, FontStyle.Regular, GraphicsUnit.Point);

        // ── State ─────────────────────────────────────────────────────────────
        private string _kindLine;   // e.g. "(method)"  — shown dimmed above the signature
        private string _sigLine;    // e.g. "void Foo.Bar(int x)"
        private Color  _accent;
        private System.Windows.Forms.Timer _hideTimer;

        public HoverTooltipWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition   = FormStartPosition.Manual;
            ShowInTaskbar   = false;
            DoubleBuffered  = true;
            BackColor       = BgColor;

            // Never steal focus from the editor
            SetStyle(ControlStyles.Selectable, false);

            _hideTimer = new System.Windows.Forms.Timer { Interval = 8000 };
            _hideTimer.Tick += (s, e) => { _hideTimer.Stop(); Hide(); };
        }

        // Prevent the window from stealing focus / appearing in Alt+Tab
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                return cp;
            }
        }

        /// <summary>
        /// Displays the hover popup near <paramref name="anchorScreen"/> (screen coords).
        /// </summary>
        public void ShowHover(string text, Point anchorScreen, Control owner)
        {
            if (string.IsNullOrWhiteSpace(text))
            { Hide(); return; }

            ParseText(text, DiagnosticSeverity.Hidden, out _kindLine, out _sigLine, out _accent);
            LayoutAndPosition(anchorScreen, owner);

            _hideTimer.Stop();
            _hideTimer.Start();

            if (!Visible) Show(owner);
            else Invalidate();
        }

        /// <summary>
        /// Displays a diagnostic hover (error / warning / info) near the anchor.
        /// </summary>
        public void ShowDiagnostic(string text, DiagnosticSeverity severity, Point anchorScreen, Control owner)
        {
            if (string.IsNullOrWhiteSpace(text))
            { Hide(); return; }

            ParseText(text, severity, out _kindLine, out _sigLine, out _accent);
            LayoutAndPosition(anchorScreen, owner);

            _hideTimer.Stop();
            _hideTimer.Start();

            if (!Visible) Show(owner);
            else Invalidate();
        }

        public new void Hide()
        {
            _hideTimer?.Stop();
            base.Hide();
        }

        // ── Parsing ───────────────────────────────────────────────────────────

        private static void ParseText(
            string raw,
            DiagnosticSeverity severity,
            out string kindLine,
            out string sigLine,
            out Color accent)
        {
            // Diagnostic messages start with "ERROR CS0103: ..." or "WARNING CS0200: ..."
            // Hover info starts with "(method) ..." / "(property) ..." / "(local) ..." etc.

            kindLine = null;
            sigLine  = raw;
            accent   = AccentHover;

            if (severity == DiagnosticSeverity.Error)
            {
                accent = AccentError;
                // Keep full message as sigLine, put severity label as kindLine
                kindLine = "● error";
                return;
            }
            if (severity == DiagnosticSeverity.Warning)
            {
                accent = AccentWarning;
                kindLine = "▲ warning";
                return;
            }
            if (severity == DiagnosticSeverity.Info)
            {
                accent = AccentInfo;
                kindLine = "ℹ info";
                return;
            }

            // Hover info: "(kind) rest…"
            if (raw.Length > 2 && raw[0] == '(')
            {
                int close = raw.IndexOf(')', 1);
                if (close > 0)
                {
                    kindLine = raw.Substring(0, close + 1);
                    sigLine  = raw.Substring(close + 1).TrimStart();

                    // Pick accent based on kind
                    string k = kindLine.ToLowerInvariant();
                    if (k.Contains("error"))              accent = AccentError;
                    else if (k.Contains("warn"))          accent = AccentWarning;
                    else if (k.Contains("namespace"))     accent = AccentInfo;
                    else if (k.Contains("class")
                          || k.Contains("struct")
                          || k.Contains("interface")
                          || k.Contains("enum")
                          || k.Contains("delegate"))      accent = Color.FromArgb(78, 201, 176);
                    else if (k.Contains("method")
                          || k.Contains("constructor"))   accent = Color.FromArgb(220, 220, 170);
                    else if (k.Contains("property"))      accent = Color.FromArgb(86, 156, 214);
                    else if (k.Contains("field")
                          || k.Contains("const")
                          || k.Contains("static"))        accent = Color.FromArgb(180, 150, 220);
                    else if (k.Contains("local")
                          || k.Contains("parameter"))     accent = Color.FromArgb(156, 220, 254);
                    else                                  accent = AccentHover;
                }
            }
        }

        // ── Layout ────────────────────────────────────────────────────────────

        private void LayoutAndPosition(Point anchorScreen, Control owner)
        {
            using (var g = CreateGraphics())
            {
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                SizeF kindSz = string.IsNullOrEmpty(_kindLine)
                    ? SizeF.Empty
                    : g.MeasureString(_kindLine, _kindFont, MaxWidth - AccentW - PadH * 2);
                SizeF sigSz  = g.MeasureString(_sigLine ?? "", _sigFont, MaxWidth - AccentW - PadH * 2);

                int contentW = (int)Math.Max(kindSz.Width, sigSz.Width) + 1;
                int contentH = (int)(kindSz.Height + sigSz.Height);

                int w = Math.Max(MinWidth, AccentW + PadH * 2 + contentW);
                int h = contentH + PadV * 2;

                // Position: below-right of cursor; flip up if off screen bottom
                Screen scr = Screen.FromPoint(anchorScreen);
                int x = anchorScreen.X + 14;
                int y = anchorScreen.Y + 20;
                if (x + w > scr.WorkingArea.Right)  x = scr.WorkingArea.Right - w - 4;
                if (y + h > scr.WorkingArea.Bottom) y = anchorScreen.Y - h - 4;
                if (x < scr.WorkingArea.Left) x = scr.WorkingArea.Left + 2;

                SetBounds(x, y, w, h);
            }
        }

        // ── Painting ──────────────────────────────────────────────────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var r = ClientRectangle;

            // Background
            using (var bg = new SolidBrush(BgColor))
                g.FillRectangle(bg, r);

            // Border
            using (var pen = new Pen(BorderColor))
                g.DrawRectangle(pen, 0, 0, r.Width - 1, r.Height - 1);

            // Left accent bar
            using (var acc = new SolidBrush(_accent))
                g.FillRectangle(acc, 1, 1, AccentW, r.Height - 2);

            int textX = AccentW + PadH;
            int textY = PadV;
            int textW = r.Width - textX - PadH;

            // Kind line (dim)
            if (!string.IsNullOrEmpty(_kindLine))
            {
                using (var kindBrush = new SolidBrush(Color.FromArgb(160, _accent)))
                using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter })
                {
                    var kindRect = new RectangleF(textX, textY, textW, r.Height);
                    g.DrawString(_kindLine, _kindFont, kindBrush, kindRect, sf);
                    textY += (int)g.MeasureString(_kindLine, _kindFont, textW).Height;
                }
            }

            // Signature line (bright)
            if (!string.IsNullOrEmpty(_sigLine))
            {
                using (var sigBrush = new SolidBrush(SigColor))
                using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter })
                {
                    var sigRect = new RectangleF(textX, textY, textW, r.Height - textY);
                    g.DrawString(_sigLine, _sigFont, sigBrush, sigRect, sf);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hideTimer?.Dispose();
                _hideTimer = null;
            }
            base.Dispose(disposing);
        }
    }
}
