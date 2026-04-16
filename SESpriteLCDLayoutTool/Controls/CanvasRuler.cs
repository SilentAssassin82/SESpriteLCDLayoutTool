using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SESpriteLCDLayoutTool.Controls
{
    /// <summary>
    /// A thin ruler strip that mirrors the LCD canvas coordinate space.
    /// Draws tick marks and labels in surface pixels (0 … SurfaceWidth/Height).
    /// The ruler tracks the canvas transform (scale + pan origin) so ticks
    /// stay perfectly aligned with the canvas grid at every zoom level.
    /// </summary>
    public sealed class CanvasRuler : Control
    {
        // ── Configuration ─────────────────────────────────────────────────────────
        public enum Orientation { Horizontal, Vertical }

        private readonly Orientation _orientation;

        /// <summary>Thickness of the ruler strip in pixels.</summary>
        public const int Thickness = 20;

        // ── Transform state (set by LcdCanvas each frame) ─────────────────────────
        private float _scale  = 1f;
        private PointF _origin = PointF.Empty;
        private int _surfaceSize = 512;

        // ── Cursor tracking ───────────────────────────────────────────────────────
        private float _cursorSurfacePos = -1f;  // surface-coord position of the mouse cursor (-1 = hidden)

        // ── Colors ────────────────────────────────────────────────────────────────
        private static readonly Color ColBg      = Color.FromArgb(38, 38, 42);
        private static readonly Color ColTick     = Color.FromArgb(130, 130, 140);
        private static readonly Color ColLabel    = Color.FromArgb(170, 170, 180);
        private static readonly Color ColCursor   = Color.FromArgb(220, 80, 200, 255);
        private static readonly Color ColBorder   = Color.FromArgb(55, 55, 62);

        public CanvasRuler(Orientation orientation)
        {
            _orientation = orientation;
            DoubleBuffered = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer,
                true);
            BackColor = ColBg;

            if (orientation == Orientation.Horizontal)
            {
                Dock   = DockStyle.Top;
                Height = Thickness;
            }
            else
            {
                Dock  = DockStyle.Left;
                Width = Thickness;
            }
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Called by the canvas each time its transform changes (zoom, pan, resize).
        /// </summary>
        public void SetTransform(float scale, PointF origin, int surfaceSize)
        {
            _scale       = scale;
            _origin      = origin;
            _surfaceSize = surfaceSize;
            Invalidate();
        }

        /// <summary>
        /// Called by the canvas when the mouse moves over it.
        /// <paramref name="surfacePos"/> is the position in surface coordinates (or −1 to hide).
        /// </summary>
        public void SetCursorPos(float surfacePos)
        {
            if (Math.Abs(_cursorSurfacePos - surfacePos) < 0.5f) return;
            _cursorSurfacePos = surfacePos;
            Invalidate();
        }

        // ── Painting ──────────────────────────────────────────────────────────────
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.Clear(ColBg);

            if (_scale < 0.01f) return;

            // Border line on the canvas-facing edge
            using (var borderPen = new Pen(ColBorder, 1f))
            {
                if (_orientation == Orientation.Horizontal)
                    g.DrawLine(borderPen, 0, Height - 1, Width, Height - 1);
                else
                    g.DrawLine(borderPen, Width - 1, 0, Width - 1, Height);
            }

            // Choose a tick interval that gives ~40–80px between major ticks on screen
            float minScreenGap = 40f;
            int[] niceSteps = { 1, 2, 4, 5, 8, 10, 16, 20, 25, 32, 50, 64, 100, 128, 256 };
            int majorStep = niceSteps[niceSteps.Length - 1];
            foreach (int step in niceSteps)
            {
                if (step * _scale >= minScreenGap) { majorStep = step; break; }
            }
            int minorStep = majorStep / 4;
            if (minorStep < 1) minorStep = 1;

            using (var tickPen   = new Pen(ColTick, 1f))
            using (var labelFont = new Font("Segoe UI", 6.5f, FontStyle.Regular, GraphicsUnit.Point))
            using (var labelBrush = new SolidBrush(ColLabel))
            {
                // Iterate ticks across the surface range
                // Start slightly before 0 in case canvas is panned
                int firstMinor = (int)Math.Floor(0f / minorStep) * minorStep;
                for (int v = firstMinor; v <= _surfaceSize + minorStep; v += minorStep)
                {
                    bool major = (v % majorStep == 0);
                    float screenPos = _orientation == Orientation.Horizontal
                        ? _origin.X + v * _scale
                        : _origin.Y + v * _scale;

                    // Skip ticks outside the ruler's visible area
                    float rulerLen = _orientation == Orientation.Horizontal ? Width : Height;
                    if (screenPos < -1 || screenPos > rulerLen + 1) continue;

                    int tickLen = major ? (int)(Thickness * 0.55f) : (int)(Thickness * 0.30f);

                    if (_orientation == Orientation.Horizontal)
                    {
                        int sx = (int)screenPos;
                        g.DrawLine(tickPen, sx, Height - tickLen, sx, Height);
                        if (major && v >= 0 && v <= _surfaceSize)
                        {
                            string lbl = v.ToString();
                            var sz = g.MeasureString(lbl, labelFont);
                            float tx = sx - sz.Width / 2f + 1f;
                            float ty = 2f;
                            g.DrawString(lbl, labelFont, labelBrush, tx, ty);
                        }
                    }
                    else
                    {
                        int sy = (int)screenPos;
                        g.DrawLine(tickPen, Width - tickLen, sy, Width, sy);
                        if (major && v >= 0 && v <= _surfaceSize)
                        {
                            string lbl = v.ToString();
                            // Rotate 90° for vertical ruler
                            var state = g.Save();
                            g.TranslateTransform(Width - tickLen - 2f, sy + 1f);
                            g.RotateTransform(-90f);
                            var sz = g.MeasureString(lbl, labelFont);
                            g.DrawString(lbl, labelFont, labelBrush, -sz.Width / 2f, 0f);
                            g.Restore(state);
                        }
                    }
                }
            }

            // Cursor hairline
            if (_cursorSurfacePos >= 0f)
            {
                float screenPos = _orientation == Orientation.Horizontal
                    ? _origin.X + _cursorSurfacePos * _scale
                    : _origin.Y + _cursorSurfacePos * _scale;

                using (var cursorPen = new Pen(ColCursor, 1f))
                {
                    if (_orientation == Orientation.Horizontal)
                        g.DrawLine(cursorPen, (int)screenPos, 0, (int)screenPos, Height);
                    else
                        g.DrawLine(cursorPen, 0, (int)screenPos, Width, (int)screenPos);
                }
            }
        }
    }
}
