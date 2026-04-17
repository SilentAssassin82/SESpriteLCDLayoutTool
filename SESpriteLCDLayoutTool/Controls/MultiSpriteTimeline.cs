using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using SESpriteLCDLayoutTool.Models;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool.Controls
{
    /// <summary>
    /// NLE-style timeline control showing one track row per animated sprite.
    /// All sprite tracks share a single playhead so animations can be synced visually.
    /// </summary>
    public sealed class MultiSpriteTimeline : Control
    {
        // ── Layout constants ────────────────────────────────────────────────────
        private const int RulerHeight     = 28;
        private const int TrackHeight     = 36;
        private const int TrackLabelWidth = 140;
        private const int DiamondSize     = 9;
        private const int MinTickSpacing  = 40;
        private const float MinZoom       = 0.3f;
        private const float MaxZoom       = 20f;

        // ── Data ────────────────────────────────────────────────────────────────
        private List<SpriteEntry> _sprites = new List<SpriteEntry>();

        // ── View state ──────────────────────────────────────────────────────────
        private float _zoom    = 2f;
        private float _scrollX = 0f;
        private int   _playhead = 0;

        // ── Selection state ─────────────────────────────────────────────────────
        private int _selectedSpriteIdx   = -1;
        private int _selectedKeyframeIdx = -1;

        // ── Drag state ──────────────────────────────────────────────────────────
        private bool  _draggingPlayhead;
        private bool  _draggingKeyframe;
        private int   _dragSpriteIdx;
        private int   _dragKfIdx;
        private bool  _panning;
        private float _panStartX;
        private float _panStartScroll;

        // ── Colors ──────────────────────────────────────────────────────────────
        private static readonly Color BgColor          = Color.FromArgb(22, 22, 28);
        private static readonly Color RulerBgColor     = Color.FromArgb(28, 32, 38);
        private static readonly Color RulerTextColor   = Color.FromArgb(140, 145, 160);
        private static readonly Color RulerLineColor   = Color.FromArgb(60, 65, 75);
        private static readonly Color TrackBgEven      = Color.FromArgb(26, 28, 34);
        private static readonly Color TrackBgOdd       = Color.FromArgb(22, 24, 30);
        private static readonly Color TrackBgSelected  = Color.FromArgb(35, 45, 60);
        private static readonly Color TrackLabelBg     = Color.FromArgb(30, 32, 40);
        private static readonly Color TrackLabelText   = Color.FromArgb(200, 210, 220);
        private static readonly Color TrackLineColor   = Color.FromArgb(40, 42, 50);
        private static readonly Color PlayheadColor    = Color.FromArgb(255, 80, 80);
        private static readonly Color DiamondNormal    = Color.FromArgb(60, 180, 255);
        private static readonly Color DiamondSelected  = Color.FromArgb(255, 200, 60);

        // ── Track hue palette (cycles for each sprite row) ──────────────────────
        private static readonly Color[] TrackPalette =
        {
            Color.FromArgb(100, 200, 100),
            Color.FromArgb(100, 150, 255),
            Color.FromArgb(255, 130, 100),
            Color.FromArgb(200, 160, 255),
            Color.FromArgb(255, 200, 100),
            Color.FromArgb(100, 220, 200),
            Color.FromArgb(255, 120, 200),
        };

        // ── Events ──────────────────────────────────────────────────────────────

        /// <summary>Fired when the playhead tick changes.</summary>
        public event Action<int> PlayheadChanged;

        /// <summary>Fired when a sprite track is selected. Args: sprite index in the _sprites list (-1 = none).</summary>
        public event Action<int> SpriteSelected;

        /// <summary>Fired when a keyframe is selected. Args: sprite index, keyframe index (-1 = none).</summary>
        public event Action<int, int> KeyframeSelected;

        /// <summary>Fired when a keyframe is moved. Args: sprite index, keyframe index, new tick.</summary>
        public event Action<int, int, int> KeyframeMoved;

        /// <summary>Fired when user double-clicks empty track space to add a keyframe. Args: sprite index, tick.</summary>
        public event Action<int, int> KeyframeAddRequested;

        // ── Properties ──────────────────────────────────────────────────────────

        public int Playhead
        {
            get => _playhead;
            set { _playhead = Math.Max(0, value); Invalidate(); }
        }

        public int SelectedSpriteIndex  => _selectedSpriteIdx;
        public int SelectedKeyframeIndex => _selectedKeyframeIdx;

        public float Zoom
        {
            get => _zoom;
            set { _zoom = Math.Max(MinZoom, Math.Min(MaxZoom, value)); Invalidate(); }
        }

        // ── Constructor ─────────────────────────────────────────────────────────

        public MultiSpriteTimeline()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw, true);
            BackColor = BgColor;
        }

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>Replaces the sprite list. Call after adding/removing animated sprites.</summary>
        public void SetSprites(List<SpriteEntry> sprites)
        {
            _sprites = sprites ?? new List<SpriteEntry>();
            if (_selectedSpriteIdx >= _sprites.Count)
            {
                _selectedSpriteIdx   = -1;
                _selectedKeyframeIdx = -1;
            }
            Invalidate();
        }

        /// <summary>Selects a sprite row programmatically (e.g. from layer list click).</summary>
        public void SelectSprite(int spriteIdx, int kfIdx = -1)
        {
            _selectedSpriteIdx   = spriteIdx;
            _selectedKeyframeIdx = kfIdx;
            Invalidate();
        }

        /// <summary>Scrolls view to keep the given tick visible.</summary>
        public void EnsureTickVisible(int tick)
        {
            float px        = TickToPixel(tick);
            float viewRight = _scrollX + (Width - TrackLabelWidth);

            if (px < _scrollX + 20)
                _scrollX = Math.Max(0, px - 40);
            else if (px > viewRight - 20)
                _scrollX = px - (Width - TrackLabelWidth) + 40;

            Invalidate();
        }

        /// <summary>Auto-fits zoom so all keyframes are visible.</summary>
        public void ZoomToFit()
        {
            int maxTick = 60;
            foreach (var sp in _sprites)
            {
                if (sp.KeyframeAnimation?.Keyframes?.Count > 0)
                    maxTick = Math.Max(maxTick, sp.KeyframeAnimation.Keyframes.Max(k => k.Tick));
            }

            float avail = Math.Max(100, Width - TrackLabelWidth - 40);
            _zoom    = Math.Max(MinZoom, Math.Min(MaxZoom, avail / (maxTick + 10)));
            _scrollX = 0;
            Invalidate();
        }

        /// <summary>Repaints without changing data (e.g. after external keyframe edit).</summary>
        public void RefreshDisplay() => Invalidate();

        // ── Coordinate helpers ──────────────────────────────────────────────────

        private float TickToPixel(int tick)    => TrackLabelWidth + tick * _zoom - _scrollX;
        private int   PixelToTick(float px)    => Math.Max(0, (int)Math.Round((px - TrackLabelWidth + _scrollX) / _zoom));
        private int   TrackAtY(int y)
        {
            if (y < RulerHeight) return -1;
            int idx = (y - RulerHeight) / TrackHeight;
            return idx < _sprites.Count ? idx : -1;
        }

        private int TotalHeight => RulerHeight + _sprites.Count * TrackHeight;

        // ── Hit testing ─────────────────────────────────────────────────────────

        private (int spriteIdx, int kfIdx) HitTestKeyframe(Point pt)
        {
            int trackIdx = TrackAtY(pt.Y);
            if (trackIdx < 0 || trackIdx >= _sprites.Count) return (-1, -1);

            var kfs = _sprites[trackIdx].KeyframeAnimation?.Keyframes;
            if (kfs == null) return (-1, -1);

            float cy = RulerHeight + trackIdx * TrackHeight + TrackHeight / 2f;
            for (int i = kfs.Count - 1; i >= 0; i--)
            {
                float cx = TickToPixel(kfs[i].Tick);
                var rect = new RectangleF(cx - DiamondSize, cy - DiamondSize,
                                          DiamondSize * 2, DiamondSize * 2);
                if (rect.Contains(pt)) return (trackIdx, i);
            }
            return (-1, -1);
        }

        // ── Painting ────────────────────────────────────────────────────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode      = SmoothingMode.AntiAlias;
            g.TextRenderingHint  = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            DrawTracks(g);
            DrawEasingLines(g);
            DrawKeyframes(g);
            DrawPlayhead(g);
            DrawRuler(g);
            DrawTrackLabels(g);
        }

        private void DrawRuler(Graphics g)
        {
            g.FillRectangle(new SolidBrush(RulerBgColor), 0, 0, Width, RulerHeight);
            g.FillRectangle(new SolidBrush(TrackLabelBg), 0, 0, TrackLabelWidth, RulerHeight);

            int tickStep  = CalculateTickStep();
            int startTick = Math.Max(0, PixelToTick(TrackLabelWidth) - tickStep);
            int endTick   = PixelToTick(Width) + tickStep;

            using (var pen   = new Pen(RulerLineColor))
            using (var font  = new Font("Segoe UI", 7.5f))
            using (var brush = new SolidBrush(RulerTextColor))
            {
                for (int t = (startTick / tickStep) * tickStep; t <= endTick; t += tickStep)
                {
                    float px = TickToPixel(t);
                    if (px < TrackLabelWidth || px > Width) continue;
                    g.DrawLine(pen, px, RulerHeight - 10, px, RulerHeight);
                    string lbl = t.ToString();
                    var sz = g.MeasureString(lbl, font);
                    g.DrawString(lbl, font, brush, px - sz.Width / 2, 4);
                }

                int minorStep = Math.Max(1, tickStep / 5);
                for (int t = (startTick / minorStep) * minorStep; t <= endTick; t += minorStep)
                {
                    if (t % tickStep == 0) continue;
                    float px = TickToPixel(t);
                    if (px < TrackLabelWidth || px > Width) continue;
                    g.DrawLine(pen, px, RulerHeight - 4, px, RulerHeight);
                }

                g.DrawLine(pen, 0, RulerHeight - 1, Width, RulerHeight - 1);
            }
        }

        private void DrawTrackLabels(Graphics g)
        {
            using (var font      = new Font("Segoe UI", 8.5f))
            using (var borderPen = new Pen(TrackLineColor))
            {
                for (int i = 0; i < _sprites.Count; i++)
                {
                    int y = RulerHeight + i * TrackHeight;
                    g.FillRectangle(new SolidBrush(TrackLabelBg), 0, y, TrackLabelWidth, TrackHeight);

                    Color trackColor = TrackPalette[i % TrackPalette.Length];
                    // Left accent bar
                    g.FillRectangle(new SolidBrush(trackColor), 0, y + 4, 3, TrackHeight - 8);

                    bool selected = i == _selectedSpriteIdx;
                    using (var textBrush = new SolidBrush(selected ? Color.White : TrackLabelText))
                    {
                        string name = _sprites[i].DisplayName ?? $"Sprite {i}";
                        if (name.Length > 16) name = name.Substring(0, 13) + "…";
                        var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
                        g.DrawString(name, font, textBrush, new RectangleF(8, y, TrackLabelWidth - 12, TrackHeight), sf);
                    }

                    g.DrawLine(borderPen, 0, y + TrackHeight - 1, Width, y + TrackHeight - 1);
                }

                // Vertical separator
                g.DrawLine(borderPen, TrackLabelWidth, RulerHeight, TrackLabelWidth, TotalHeight);
            }
        }

        private void DrawTracks(Graphics g)
        {
            for (int i = 0; i < _sprites.Count; i++)
            {
                int y = RulerHeight + i * TrackHeight;
                Color bg = (i == _selectedSpriteIdx)
                    ? TrackBgSelected
                    : (i % 2 == 0 ? TrackBgEven : TrackBgOdd);
                g.FillRectangle(new SolidBrush(bg), TrackLabelWidth, y, Width - TrackLabelWidth, TrackHeight);
            }
        }

        private void DrawEasingLines(Graphics g)
        {
            for (int i = 0; i < _sprites.Count; i++)
            {
                var kfs = _sprites[i].KeyframeAnimation?.Keyframes;
                if (kfs == null || kfs.Count < 2) continue;

                var sorted = kfs.OrderBy(k => k.Tick).ToList();
                float cy   = RulerHeight + i * TrackHeight + TrackHeight / 2f;
                Color col  = TrackPalette[i % TrackPalette.Length];

                using (var pen = new Pen(Color.FromArgb(50, col), 1.5f))
                {
                    pen.DashStyle = DashStyle.Dot;
                    for (int j = 0; j < sorted.Count - 1; j++)
                    {
                        float x1 = TickToPixel(sorted[j].Tick);
                        float x2 = TickToPixel(sorted[j + 1].Tick);
                        if (x2 < TrackLabelWidth || x1 > Width) continue;
                        g.DrawLine(pen, x1, cy, x2, cy);
                    }
                }
            }
        }

        private void DrawKeyframes(Graphics g)
        {
            for (int i = 0; i < _sprites.Count; i++)
            {
                var kfs = _sprites[i].KeyframeAnimation?.Keyframes;
                if (kfs == null) continue;

                float cy        = RulerHeight + i * TrackHeight + TrackHeight / 2f;
                Color trackCol  = TrackPalette[i % TrackPalette.Length];

                for (int j = 0; j < kfs.Count; j++)
                {
                    float cx = TickToPixel(kfs[j].Tick);
                    if (cx < TrackLabelWidth - DiamondSize || cx > Width + DiamondSize) continue;

                    bool kfSelected = (i == _selectedSpriteIdx && j == _selectedKeyframeIdx);
                    Color fill    = kfSelected ? DiamondSelected : trackCol;
                    Color outline = kfSelected ? Color.White : Color.FromArgb(180, trackCol);

                    var diamond = new PointF[]
                    {
                        new PointF(cx,              cy - DiamondSize),
                        new PointF(cx + DiamondSize, cy),
                        new PointF(cx,              cy + DiamondSize),
                        new PointF(cx - DiamondSize, cy),
                    };

                    using (var fb = new SolidBrush(Color.FromArgb(kfSelected ? 255 : 200, fill)))
                        g.FillPolygon(fb, diamond);
                    using (var op = new Pen(outline, kfSelected ? 2f : 1f))
                        g.DrawPolygon(op, diamond);
                }
            }
        }

        private void DrawPlayhead(Graphics g)
        {
            float px = TickToPixel(_playhead);
            if (px < TrackLabelWidth || px > Width) return;

            using (var pen = new Pen(PlayheadColor, 2f))
                g.DrawLine(pen, px, 0, px, TotalHeight);

            var handle = new PointF[]
            {
                new PointF(px,     RulerHeight),
                new PointF(px - 6, 0),
                new PointF(px + 6, 0),
            };
            using (var b = new SolidBrush(PlayheadColor))
                g.FillPolygon(b, handle);
        }

        // ── Mouse handling ──────────────────────────────────────────────────────

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();

            if (e.Button == MouseButtons.Middle)
            {
                _panning      = true;
                _panStartX    = e.X;
                _panStartScroll = _scrollX;
                Cursor = Cursors.SizeWE;
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            if (e.Y < RulerHeight)
            {
                _draggingPlayhead = true;
                _playhead = PixelToTick(e.X);
                PlayheadChanged?.Invoke(_playhead);
                Invalidate();
                return;
            }

            var (si, ki) = HitTestKeyframe(e.Location);
            if (si >= 0)
            {
                _selectedSpriteIdx   = si;
                _selectedKeyframeIdx = ki;
                _draggingKeyframe    = true;
                _dragSpriteIdx       = si;
                _dragKfIdx           = ki;
                SpriteSelected?.Invoke(si);
                KeyframeSelected?.Invoke(si, ki);
                Invalidate();
                return;
            }

            // Click on track row (not on a diamond) — select sprite
            int trackIdx = TrackAtY(e.Y);
            if (trackIdx >= 0)
            {
                _selectedSpriteIdx   = trackIdx;
                _selectedKeyframeIdx = -1;
                SpriteSelected?.Invoke(trackIdx);
                KeyframeSelected?.Invoke(trackIdx, -1);
                Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_panning)
            {
                _scrollX = Math.Max(0, _panStartScroll - (e.X - _panStartX));
                Invalidate();
                return;
            }

            if (_draggingPlayhead)
            {
                _playhead = PixelToTick(e.X);
                PlayheadChanged?.Invoke(_playhead);
                Invalidate();
                return;
            }

            if (_draggingKeyframe && _dragSpriteIdx >= 0 && _dragSpriteIdx < _sprites.Count)
            {
                var kfs = _sprites[_dragSpriteIdx].KeyframeAnimation?.Keyframes;
                if (kfs != null && _dragKfIdx >= 0 && _dragKfIdx < kfs.Count)
                {
                    int newTick = Math.Max(0, PixelToTick(e.X));
                    kfs[_dragKfIdx].Tick = newTick;
                    KeyframeMoved?.Invoke(_dragSpriteIdx, _dragKfIdx, newTick);
                    Invalidate();
                }
                return;
            }

            // Cursor hints
            if (e.Y < RulerHeight)
                Cursor = Cursors.Hand;
            else if (HitTestKeyframe(e.Location).Item1 >= 0)
                Cursor = Cursors.SizeWE;
            else
                Cursor = Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_panning) { _panning = false; Cursor = Cursors.Default; }
            _draggingPlayhead = false;
            _draggingKeyframe = false;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;

            if (e.Y < RulerHeight)
            {
                _playhead = PixelToTick(e.X);
                PlayheadChanged?.Invoke(_playhead);
                Invalidate();
                return;
            }

            int trackIdx = TrackAtY(e.Y);
            if (trackIdx >= 0 && HitTestKeyframe(e.Location).Item1 < 0)
            {
                int tick = PixelToTick(e.X);
                KeyframeAddRequested?.Invoke(trackIdx, tick);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float mouseTickBefore = (e.X - TrackLabelWidth + _scrollX) / _zoom;
            float factor = e.Delta > 0 ? 1.25f : 0.8f;
            _zoom    = Math.Max(MinZoom, Math.Min(MaxZoom, _zoom * factor));
            _scrollX = Math.Max(0, mouseTickBefore * _zoom - (e.X - TrackLabelWidth));
            Invalidate();
        }

        // ── Tick step ───────────────────────────────────────────────────────────

        private int CalculateTickStep()
        {
            int[] steps = { 1, 2, 5, 10, 15, 20, 30, 50, 100, 200, 500, 1000 };
            foreach (int s in steps)
                if (s * _zoom >= MinTickSpacing) return s;
            return 1000;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            int rows = Math.Max(1, _sprites.Count);
            return new Size(proposedSize.Width, RulerHeight + rows * TrackHeight + 2);
        }
    }
}
