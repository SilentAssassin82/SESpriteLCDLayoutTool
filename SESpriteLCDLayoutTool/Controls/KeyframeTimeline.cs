using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool.Controls
{
    /// <summary>
    /// Visual timeline control for keyframe animation editing.
    /// Displays property tracks with draggable keyframe markers,
    /// a tick ruler, vertical playhead, and easing curve visualization.
    /// </summary>
    public sealed class KeyframeTimeline : Control
    {
        // ── Constants ───────────────────────────────────────────────────────────
        private const int RulerHeight = 28;
        private const int TrackHeight = 32;
        private const int TrackLabelWidth = 80;
        private const int DiamondSize = 10;
        private const int MinTickSpacing = 40;
        private const float MinZoom = 0.5f;
        private const float MaxZoom = 20f;

        // ── Track definitions ───────────────────────────────────────────────────
        public static readonly string[] AllTracks = { "Position", "Size", "Color", "Rotation", "Scale" };

        private readonly List<string> _visibleTracks = new List<string>();

        // ── Data ────────────────────────────────────────────────────────────────
        private List<Keyframe> _keyframes = new List<Keyframe>();
        private bool _isTextSprite;

        // ── View state ──────────────────────────────────────────────────────────
        private float _zoom = 2f;           // pixels per tick
        private float _scrollX = 0f;        // horizontal scroll offset in pixels
        private int _playhead = 0;          // current tick for preview
        private int _selectedIndex = -1;    // selected keyframe index

        // ── Drag state ──────────────────────────────────────────────────────────
        private bool _draggingKeyframe;
        private bool _draggingPlayhead;
        private int _dragKeyframeIndex = -1;
        private int _dragOriginalTick;
        private bool _panning;
        private float _panStartX;
        private float _panStartScroll;

        // ── Colors ──────────────────────────────────────────────────────────────
        private static readonly Color BgColor = Color.FromArgb(22, 22, 28);
        private static readonly Color RulerBgColor = Color.FromArgb(28, 32, 38);
        private static readonly Color RulerLineColor = Color.FromArgb(60, 65, 75);
        private static readonly Color RulerTextColor = Color.FromArgb(140, 145, 160);
        private static readonly Color TrackBgEven = Color.FromArgb(26, 28, 34);
        private static readonly Color TrackBgOdd = Color.FromArgb(22, 24, 30);
        private static readonly Color TrackLabelBg = Color.FromArgb(30, 32, 40);
        private static readonly Color TrackLabelText = Color.FromArgb(150, 160, 180);
        private static readonly Color TrackLineColor = Color.FromArgb(40, 42, 50);
        private static readonly Color PlayheadColor = Color.FromArgb(255, 80, 80);
        private static readonly Color KeyframeNormal = Color.FromArgb(60, 180, 255);
        private static readonly Color KeyframeSelectedColor = Color.FromArgb(255, 200, 60);
        private static readonly Color EasingCurveColor = Color.FromArgb(80, 60, 180, 255);

        private static readonly Dictionary<string, Color> TrackColors = new Dictionary<string, Color>
        {
            { "Position", Color.FromArgb(100, 200, 100) },
            { "Size",     Color.FromArgb(100, 150, 255) },
            { "Color",    Color.FromArgb(255, 130, 100) },
            { "Rotation", Color.FromArgb(200, 160, 255) },
            { "Scale",    Color.FromArgb(255, 200, 100) },
        };

        // ── Events ──────────────────────────────────────────────────────────────

        /// <summary>Fired when the playhead tick changes (click or drag on ruler).</summary>
        public event Action<int> PlayheadChanged;

        /// <summary>Fired when a keyframe is selected or deselected. Args: index (-1 = none).</summary>
        public event Action<int> KeyframeSelected;

        /// <summary>Fired when a keyframe's tick changes via drag. Args: index, newTick.</summary>
        public event Action<int, int> KeyframeMoved;

        /// <summary>Fired when the user double-clicks on an empty track area to add a keyframe. Args: tick.</summary>
        public event Action<int> KeyframeAddRequested;

        // ── Properties ──────────────────────────────────────────────────────────

        /// <summary>Gets or sets the current playhead tick.</summary>
        public int Playhead
        {
            get => _playhead;
            set
            {
                if (_playhead == value) return;
                _playhead = Math.Max(0, value);
                Invalidate();
            }
        }

        /// <summary>Gets or sets the selected keyframe index (-1 = none).</summary>
        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                if (_selectedIndex == value) return;
                _selectedIndex = value;
                Invalidate();
            }
        }

        /// <summary>Current horizontal zoom level (pixels per tick).</summary>
        public float Zoom
        {
            get => _zoom;
            set
            {
                float clamped = Math.Max(MinZoom, Math.Min(MaxZoom, value));
                if (Math.Abs(_zoom - clamped) < 0.001f) return;
                _zoom = clamped;
                Invalidate();
            }
        }

        // ── Constructor ─────────────────────────────────────────────────────────

        public KeyframeTimeline()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw, true);
            BackColor = BgColor;
            MinimumSize = new Size(200, RulerHeight + TrackHeight);
        }

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>Sets keyframe data and sprite type for display.</summary>
        public void SetData(List<Keyframe> keyframes, bool isTextSprite)
        {
            _keyframes = keyframes ?? new List<Keyframe>();
            _isTextSprite = isTextSprite;
            RebuildTracks();
            Invalidate();
        }

        /// <summary>Refreshes the display after external keyframe data changes.</summary>
        public void RefreshDisplay()
        {
            Invalidate();
        }

        /// <summary>Scrolls the view to ensure the given tick is visible.</summary>
        public void EnsureTickVisible(int tick)
        {
            float px = TickToPixel(tick);
            float viewLeft = _scrollX;
            float viewRight = _scrollX + (Width - TrackLabelWidth);

            if (px < viewLeft + 20)
                _scrollX = Math.Max(0, px - 40);
            else if (px > viewRight - 20)
                _scrollX = px - (Width - TrackLabelWidth) + 40;

            Invalidate();
        }

        /// <summary>Auto-fits the zoom and scroll to show all keyframes.</summary>
        public void ZoomToFit()
        {
            if (_keyframes.Count == 0) return;
            int maxTick = _keyframes.Max(k => k.Tick);
            if (maxTick <= 0) maxTick = 60;

            float availWidth = Math.Max(100, Width - TrackLabelWidth - 40);
            _zoom = Math.Max(MinZoom, Math.Min(MaxZoom, availWidth / (maxTick + 10)));
            _scrollX = 0;
            Invalidate();
        }

        // ── Track management ────────────────────────────────────────────────────

        private void RebuildTracks()
        {
            _visibleTracks.Clear();
            _visibleTracks.Add("Position");
            if (!_isTextSprite) _visibleTracks.Add("Size");
            _visibleTracks.Add("Color");
            _visibleTracks.Add(_isTextSprite ? "Scale" : "Rotation");
        }

        // ── Coordinate helpers ──────────────────────────────────────────────────

        private float TickToPixel(int tick)
        {
            return TrackLabelWidth + tick * _zoom - _scrollX;
        }

        private int PixelToTick(float px)
        {
            float raw = (px - TrackLabelWidth + _scrollX) / _zoom;
            return Math.Max(0, (int)Math.Round(raw));
        }

        private int TrackAtY(int y)
        {
            if (y < RulerHeight) return -1;
            int idx = (y - RulerHeight) / TrackHeight;
            return idx < _visibleTracks.Count ? idx : -1;
        }

        private RectangleF GetDiamondRect(int tick, int trackIndex)
        {
            float cx = TickToPixel(tick);
            float cy = RulerHeight + trackIndex * TrackHeight + TrackHeight / 2f;
            return new RectangleF(cx - DiamondSize, cy - DiamondSize,
                                  DiamondSize * 2, DiamondSize * 2);
        }

        // ── Hit testing ─────────────────────────────────────────────────────────

        /// <summary>Returns the keyframe index at the given point, or -1.</summary>
        private int HitTestKeyframe(Point pt)
        {
            int trackIdx = TrackAtY(pt.Y);
            if (trackIdx < 0) return -1;

            // Check all keyframes on this track
            for (int i = _keyframes.Count - 1; i >= 0; i--)
            {
                // All keyframes appear on all tracks — check each
                var rect = GetDiamondRect(_keyframes[i].Tick, trackIdx);
                if (rect.Contains(pt))
                    return i;
            }
            return -1;
        }

        private bool HitTestPlayhead(Point pt)
        {
            float px = TickToPixel(_playhead);
            return Math.Abs(pt.X - px) < 6 && pt.Y < RulerHeight;
        }

        // ── Painting ────────────────────────────────────────────────────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            DrawTracks(g);
            DrawEasingCurves(g);
            DrawKeyframes(g);
            DrawPlayhead(g);
            DrawRuler(g);
            DrawTrackLabels(g);
        }

        private void DrawRuler(Graphics g)
        {
            // Background
            g.FillRectangle(new SolidBrush(RulerBgColor), 0, 0, Width, RulerHeight);

            // Label area
            g.FillRectangle(new SolidBrush(TrackLabelBg), 0, 0, TrackLabelWidth, RulerHeight);

            using (var pen = new Pen(RulerLineColor))
            using (var font = new Font("Segoe UI", 7.5f))
            using (var brush = new SolidBrush(RulerTextColor))
            {
                // Calculate tick spacing
                int tickStep = CalculateTickStep();

                int startTick = Math.Max(0, PixelToTick(TrackLabelWidth) - tickStep);
                int endTick = PixelToTick(Width) + tickStep;

                for (int t = (startTick / tickStep) * tickStep; t <= endTick; t += tickStep)
                {
                    if (t < 0) continue;
                    float px = TickToPixel(t);
                    if (px < TrackLabelWidth || px > Width) continue;

                    // Major tick line
                    g.DrawLine(pen, px, RulerHeight - 10, px, RulerHeight);

                    // Label
                    string label = t.ToString();
                    var sz = g.MeasureString(label, font);
                    g.DrawString(label, font, brush, px - sz.Width / 2, 4);
                }

                // Minor ticks
                int minorStep = Math.Max(1, tickStep / 5);
                for (int t = (startTick / minorStep) * minorStep; t <= endTick; t += minorStep)
                {
                    if (t < 0 || t % tickStep == 0) continue;
                    float px = TickToPixel(t);
                    if (px < TrackLabelWidth || px > Width) continue;
                    g.DrawLine(pen, px, RulerHeight - 4, px, RulerHeight);
                }

                // Bottom border
                g.DrawLine(pen, 0, RulerHeight - 1, Width, RulerHeight - 1);
            }
        }

        private void DrawTrackLabels(Graphics g)
        {
            using (var font = new Font("Segoe UI", 8f))
            using (var borderPen = new Pen(TrackLineColor))
            {
                for (int i = 0; i < _visibleTracks.Count; i++)
                {
                    int y = RulerHeight + i * TrackHeight;
                    g.FillRectangle(new SolidBrush(TrackLabelBg), 0, y, TrackLabelWidth, TrackHeight);

                    Color trackColor;
                    if (!TrackColors.TryGetValue(_visibleTracks[i], out trackColor))
                        trackColor = TrackLabelText;

                    using (var brush = new SolidBrush(trackColor))
                    {
                        var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
                        g.DrawString(_visibleTracks[i], font, brush,
                            new RectangleF(8, y, TrackLabelWidth - 12, TrackHeight), sf);
                    }

                    // Bottom border
                    g.DrawLine(borderPen, 0, y + TrackHeight - 1, Width, y + TrackHeight - 1);
                }

                // Vertical separator
                g.DrawLine(borderPen, TrackLabelWidth, RulerHeight, TrackLabelWidth, Height);
            }
        }

        private void DrawTracks(Graphics g)
        {
            for (int i = 0; i < _visibleTracks.Count; i++)
            {
                int y = RulerHeight + i * TrackHeight;
                Color bg = i % 2 == 0 ? TrackBgEven : TrackBgOdd;
                g.FillRectangle(new SolidBrush(bg), TrackLabelWidth, y, Width - TrackLabelWidth, TrackHeight);
            }
        }

        private void DrawEasingCurves(Graphics g)
        {
            if (_keyframes.Count < 2) return;

            var sorted = _keyframes.OrderBy(k => k.Tick).ToList();

            for (int trackIdx = 0; trackIdx < _visibleTracks.Count; trackIdx++)
            {
                Color trackColor;
                if (!TrackColors.TryGetValue(_visibleTracks[trackIdx], out trackColor))
                    trackColor = EasingCurveColor;

                using (var pen = new Pen(Color.FromArgb(60, trackColor), 1.5f))
                {
                    pen.DashStyle = DashStyle.Dot;

                    for (int i = 0; i < sorted.Count - 1; i++)
                    {
                        float x1 = TickToPixel(sorted[i].Tick);
                        float x2 = TickToPixel(sorted[i + 1].Tick);
                        float cy = RulerHeight + trackIdx * TrackHeight + TrackHeight / 2f;

                        if (x2 < TrackLabelWidth || x1 > Width) continue;

                        // Draw easing curve
                        var easing = sorted[i].EasingToNext;
                        if (easing == EasingType.Linear)
                        {
                            g.DrawLine(pen, x1, cy, x2, cy);
                        }
                        else
                        {
                            DrawEasingPath(g, pen, easing, x1, x2, cy, TrackHeight * 0.35f);
                        }
                    }
                }
            }
        }

        private void DrawEasingPath(Graphics g, Pen pen, EasingType easing,
            float x1, float x2, float centerY, float amplitude)
        {
            int steps = Math.Max(10, (int)(x2 - x1) / 2);
            var points = new PointF[steps + 1];

            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                float eased = ApplyEasing(t, easing);
                points[i] = new PointF(
                    x1 + t * (x2 - x1),
                    centerY - (eased - 0.5f) * amplitude * 2);
            }

            if (points.Length >= 2)
                g.DrawLines(pen, points);
        }

        private static float ApplyEasing(float t, EasingType easing)
        {
            switch (easing)
            {
                case EasingType.Linear: return t;
                case EasingType.SineInOut: return (float)(0.5 - 0.5 * Math.Cos(t * Math.PI));
                case EasingType.EaseIn: return t * t;
                case EasingType.EaseOut: return 1f - (1f - t) * (1f - t);
                case EasingType.EaseInOut:
                    return t < 0.5f ? 2f * t * t : 1f - (float)Math.Pow(-2 * t + 2, 2) / 2f;
                case EasingType.Bounce:
                {
                    float b = 1f - t;
                    if (b < 1f / 2.75f) return 1f - 7.5625f * b * b;
                    if (b < 2f / 2.75f) { b -= 1.5f / 2.75f; return 1f - (7.5625f * b * b + 0.75f); }
                    if (b < 2.5f / 2.75f) { b -= 2.25f / 2.75f; return 1f - (7.5625f * b * b + 0.9375f); }
                    b -= 2.625f / 2.75f; return 1f - (7.5625f * b * b + 0.984375f);
                }
                case EasingType.Elastic:
                {
                    if (t <= 0f) return 0f;
                    if (t >= 1f) return 1f;
                    return (float)(-Math.Pow(2, 10 * t - 10) * Math.Sin((t * 10 - 10.75) * (2 * Math.PI / 3)));
                }
                default: return t;
            }
        }

        private void DrawKeyframes(Graphics g)
        {
            for (int trackIdx = 0; trackIdx < _visibleTracks.Count; trackIdx++)
            {
                for (int i = 0; i < _keyframes.Count; i++)
                {
                    var kf = _keyframes[i];
                    float cx = TickToPixel(kf.Tick);
                    float cy = RulerHeight + trackIdx * TrackHeight + TrackHeight / 2f;

                    if (cx < TrackLabelWidth - DiamondSize || cx > Width + DiamondSize) continue;

                    bool selected = i == _selectedIndex;

                    Color trackColor;
                    if (!TrackColors.TryGetValue(_visibleTracks[trackIdx], out trackColor))
                        trackColor = KeyframeNormal;

                    Color fillColor = selected ? KeyframeSelectedColor : trackColor;
                    Color outlineColor = selected ? Color.White : Color.FromArgb(180, trackColor);

                    // Draw diamond
                    var diamond = new PointF[]
                    {
                        new PointF(cx, cy - DiamondSize),
                        new PointF(cx + DiamondSize, cy),
                        new PointF(cx, cy + DiamondSize),
                        new PointF(cx - DiamondSize, cy),
                    };

                    using (var fill = new SolidBrush(Color.FromArgb(selected ? 255 : 200, fillColor)))
                        g.FillPolygon(fill, diamond);

                    using (var outline = new Pen(outlineColor, selected ? 2f : 1f))
                        g.DrawPolygon(outline, diamond);
                }
            }
        }

        private void DrawPlayhead(Graphics g)
        {
            float px = TickToPixel(_playhead);
            if (px < TrackLabelWidth || px > Width) return;

            int totalHeight = RulerHeight + _visibleTracks.Count * TrackHeight;

            using (var pen = new Pen(PlayheadColor, 2f))
            {
                g.DrawLine(pen, px, 0, px, totalHeight);
            }

            // Playhead handle (triangle at top)
            var handle = new PointF[]
            {
                new PointF(px, RulerHeight),
                new PointF(px - 6, 0),
                new PointF(px + 6, 0),
            };
            using (var brush = new SolidBrush(PlayheadColor))
                g.FillPolygon(brush, handle);
        }

        // ── Tick step calculation ───────────────────────────────────────────────

        private int CalculateTickStep()
        {
            int[] steps = { 1, 2, 5, 10, 15, 20, 30, 50, 100, 200, 500, 1000 };
            foreach (int step in steps)
            {
                if (step * _zoom >= MinTickSpacing)
                    return step;
            }
            return 1000;
        }

        // ── Mouse handling ──────────────────────────────────────────────────────

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();

            if (e.Button == MouseButtons.Middle)
            {
                _panning = true;
                _panStartX = e.X;
                _panStartScroll = _scrollX;
                Cursor = Cursors.SizeWE;
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            // Check playhead handle or ruler
            if (e.Y < RulerHeight)
            {
                _draggingPlayhead = true;
                _playhead = Math.Max(0, PixelToTick(e.X));
                PlayheadChanged?.Invoke(_playhead);
                Invalidate();
                return;
            }

            // Check keyframe hit
            int hitIdx = HitTestKeyframe(e.Location);
            if (hitIdx >= 0)
            {
                _selectedIndex = hitIdx;
                _draggingKeyframe = true;
                _dragKeyframeIndex = hitIdx;
                _dragOriginalTick = _keyframes[hitIdx].Tick;
                KeyframeSelected?.Invoke(hitIdx);
                Invalidate();
                return;
            }

            // Click on empty track area — deselect
            _selectedIndex = -1;
            KeyframeSelected?.Invoke(-1);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_panning)
            {
                _scrollX = _panStartScroll - (e.X - _panStartX);
                if (_scrollX < 0) _scrollX = 0;
                Invalidate();
                return;
            }

            if (_draggingPlayhead)
            {
                _playhead = Math.Max(0, PixelToTick(e.X));
                PlayheadChanged?.Invoke(_playhead);
                Invalidate();
                return;
            }

            if (_draggingKeyframe && _dragKeyframeIndex >= 0 && _dragKeyframeIndex < _keyframes.Count)
            {
                int newTick = Math.Max(0, PixelToTick(e.X));
                _keyframes[_dragKeyframeIndex].Tick = newTick;
                KeyframeMoved?.Invoke(_dragKeyframeIndex, newTick);
                Invalidate();
                return;
            }

            // Update cursor
            if (e.Y < RulerHeight)
            {
                Cursor = Cursors.Hand;
            }
            else if (HitTestKeyframe(e.Location) >= 0)
            {
                Cursor = Cursors.SizeWE;
            }
            else
            {
                Cursor = Cursors.Default;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (_panning)
            {
                _panning = false;
                Cursor = Cursors.Default;
            }

            _draggingPlayhead = false;

            if (_draggingKeyframe)
            {
                _draggingKeyframe = false;
                _dragKeyframeIndex = -1;
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);

            if (e.Button != MouseButtons.Left) return;

            // Double-click on ruler to set playhead precisely
            if (e.Y < RulerHeight)
            {
                _playhead = Math.Max(0, PixelToTick(e.X));
                PlayheadChanged?.Invoke(_playhead);
                Invalidate();
                return;
            }

            // Double-click on empty track area to request adding a keyframe
            int hitIdx = HitTestKeyframe(e.Location);
            if (hitIdx < 0)
            {
                int tick = PixelToTick(e.X);
                KeyframeAddRequested?.Invoke(tick);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);

            // Horizontal zoom centered on mouse position
            float mouseTickBefore = (e.X - TrackLabelWidth + _scrollX) / _zoom;

            float factor = e.Delta > 0 ? 1.25f : 0.8f;
            _zoom = Math.Max(MinZoom, Math.Min(MaxZoom, _zoom * factor));

            // Adjust scroll to keep the tick under the cursor stationary
            _scrollX = mouseTickBefore * _zoom - (e.X - TrackLabelWidth);
            if (_scrollX < 0) _scrollX = 0;

            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.KeyCode == Keys.Delete && _selectedIndex >= 0)
            {
                // Let the parent handle deletion via event
                e.Handled = true;
            }

            if (e.KeyCode == Keys.Home)
            {
                _playhead = 0;
                PlayheadChanged?.Invoke(_playhead);
                EnsureTickVisible(0);
                e.Handled = true;
            }

            if (e.KeyCode == Keys.End && _keyframes.Count > 0)
            {
                _playhead = _keyframes.Max(k => k.Tick);
                PlayheadChanged?.Invoke(_playhead);
                EnsureTickVisible(_playhead);
                e.Handled = true;
            }

            if (e.KeyCode == Keys.Left)
            {
                _playhead = Math.Max(0, _playhead - (e.Shift ? 10 : 1));
                PlayheadChanged?.Invoke(_playhead);
                EnsureTickVisible(_playhead);
                e.Handled = true;
            }

            if (e.KeyCode == Keys.Right)
            {
                _playhead = _playhead + (e.Shift ? 10 : 1);
                PlayheadChanged?.Invoke(_playhead);
                EnsureTickVisible(_playhead);
                e.Handled = true;
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left:
                case Keys.Right:
                case Keys.Home:
                case Keys.End:
                case Keys.Delete:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        // ── Preferred size ──────────────────────────────────────────────────────

        public override Size GetPreferredSize(Size proposedSize)
        {
            int trackCount = _visibleTracks.Count > 0 ? _visibleTracks.Count : 4;
            return new Size(proposedSize.Width, RulerHeight + trackCount * TrackHeight + 2);
        }
    }
}
