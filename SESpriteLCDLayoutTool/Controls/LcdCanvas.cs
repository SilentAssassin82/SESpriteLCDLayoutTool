using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using SESpriteLCDLayoutTool.Models;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool.Controls
{
    /// <summary>Debug overlay modes for the LCD canvas.</summary>
    public enum DebugOverlayMode
    {
        None,
        BoundingBoxes,
        OverdrawHeatmap,
    }

    /// <summary>
    /// Interactive canvas that renders the LCD surface and its sprites.
    /// Handles sprite selection, drag-to-move, and drag-to-resize with 8 handles.
    /// </summary>
    public class LcdCanvas : Control
    {
        // ── Resize handle ordering (matches GetHandleRects index) ───────────────
        private enum DragMode
        {
            None,
            Move,
            ResizeNW, ResizeN, ResizeNE,
            ResizeE,
            ResizeSE, ResizeS, ResizeSW,
            ResizeW,
        }

        private static readonly DragMode[] HandleModes =
        {
            DragMode.ResizeNW, DragMode.ResizeN, DragMode.ResizeNE,
            DragMode.ResizeE,
            DragMode.ResizeSE, DragMode.ResizeS, DragMode.ResizeSW,
            DragMode.ResizeW,
        };

        // ── Fields ───────────────────────────────────────────────────────────────
        private LcdLayout _layout;
        private SpriteEntry _selectedSprite;
        private SpriteTextureCache _textureCache;

        private DragMode _dragMode = DragMode.None;
        private PointF _dragStart;
        private float _dragOrigX, _dragOrigY, _dragOrigW, _dragOrigH;

        // Zoom & pan
        private float _zoom = 1f;
        private PointF _panOffset = PointF.Empty;  // screen-pixel offset applied after fit-to-view
        private bool _isPanning;
        private PointF _panStart;
        private PointF _panOrigOffset;

        // Snap-to-grid
        private bool _snapToGrid;
        private int _gridSize = 16;

        // ── Events ───────────────────────────────────────────────────────────────
        public event EventHandler SelectionChanged;
        public event EventHandler SpriteModified;
        /// <summary>Fired once before a drag operation begins — push undo snapshot here.</summary>
        public event EventHandler DragStarting;
        /// <summary>Fired once when a drag operation ends.</summary>
        public event EventHandler DragCompleted;

        // ── Properties ───────────────────────────────────────────────────────────
        public LcdLayout CanvasLayout
        {
            get => _layout;
            set
            {
                _layout = value;
                _selectedSprite = null;
                Invalidate();
            }
        }

        public SpriteEntry SelectedSprite
        {
            get => _selectedSprite;
            set
            {
                _selectedSprite = value;
                Invalidate();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public float Zoom
        {
            get => _zoom;
            set { _zoom = Math.Max(0.1f, Math.Min(value, 8f)); Invalidate(); }
        }

        public bool SnapToGrid
        {
            get => _snapToGrid;
            set { _snapToGrid = value; Invalidate(); }
        }

        public int GridSize
        {
            get => _gridSize;
            set { _gridSize = Math.Max(4, Math.Min(value, 128)); Invalidate(); }
        }

        /// <summary>Set the texture cache to enable real-texture rendering.</summary>
        public SpriteTextureCache TextureCache
        {
            get => _textureCache;
            set { _textureCache = value; Invalidate(); }
        }

        /// <summary>
        /// When non-null, sprites NOT in this set are drawn at reduced opacity
        /// so the user can focus on a specific call's output.
        /// </summary>
        public HashSet<SpriteEntry> HighlightedSprites { get; set; }

        /// <summary>Current debug overlay mode (None, BoundingBoxes, OverdrawHeatmap).</summary>
        public DebugOverlayMode OverlayMode
        {
            get => _overlayMode;
            set { _overlayMode = value; Invalidate(); }
        }

        /// <summary>Show per-sprite size warnings (⚠ icon for oversized textures).</summary>
        public bool ShowSizeWarnings
        {
            get => _showSizeWarnings;
            set { _showSizeWarnings = value; Invalidate(); }
        }

        private DebugOverlayMode _overlayMode = DebugOverlayMode.None;
        private bool _showSizeWarnings;

        /// <summary>Cached size warnings, refreshed externally when layout changes.</summary>
        internal List<Services.DebugAnalyzer.SizeWarning> SizeWarnings { get; set; }

        // ── Constructor ───────────────────────────────────────────────────────────
        public LcdCanvas()
        {
            DoubleBuffered = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer,
                true);
            BackColor = Color.FromArgb(28, 28, 28);
            TabStop = true;
        }

        // ── Coordinate helpers ────────────────────────────────────────────────────
        private void ComputeTransform(out float scale, out PointF origin)
        {
            if (_layout == null) { scale = 1f; origin = new PointF(20f, 20f); return; }

            const int pad = 20;
            float availW = Math.Max(1, Width  - pad * 2);
            float availH = Math.Max(1, Height - pad * 2);
            float baseScale = Math.Min(availW / _layout.SurfaceWidth, availH / _layout.SurfaceHeight);
            scale = baseScale * _zoom;

            float displayW = _layout.SurfaceWidth  * scale;
            float displayH = _layout.SurfaceHeight * scale;
            origin = new PointF(
                (Width  - displayW) / 2f + _panOffset.X,
                (Height - displayH) / 2f + _panOffset.Y);
        }

        private float Snap(float v)
        {
            if (!_snapToGrid || _gridSize <= 0) return v;
            return (float)Math.Round(v / _gridSize) * _gridSize;
        }

        private RectangleF GetSpriteScreenRect(SpriteEntry sprite, float scale, PointF origin)
        {
            float w = sprite.Width  * scale;
            float h = sprite.Height * scale;

            if (sprite.Type == SpriteEntryType.Text)
            {
                // SE text positioning: Y = top edge, X depends on Alignment
                float x = origin.X + sprite.X * scale;
                float y = origin.Y + sprite.Y * scale;

                switch (sprite.Alignment)
                {
                    case SpriteTextAlignment.Left:
                        return new RectangleF(x, y, w, h);
                    case SpriteTextAlignment.Right:
                        return new RectangleF(x - w, y, w, h);
                    default: // Center
                        return new RectangleF(x - w / 2f, y, w, h);
                }
            }

            // TEXTURE: Position = center of sprite
            float cx = origin.X + sprite.X * scale;
            float cy = origin.Y + sprite.Y * scale;
            float hw = w / 2f;
            float hh = h / 2f;
            return new RectangleF(cx - hw, cy - hh, hw * 2f, hh * 2f);
        }

        // ── Painting ─────────────────────────────────────────────────────────────
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            if (_layout == null) return;

            ComputeTransform(out float scale, out PointF origin);
            float dw = _layout.SurfaceWidth  * scale;
            float dh = _layout.SurfaceHeight * scale;

            // LCD surface background
            using (var bg = new SolidBrush(Color.FromArgb(12, 18, 30)))
                g.FillRectangle(bg, origin.X, origin.Y, dw, dh);

            // Surface border
            using (var border = new Pen(Color.FromArgb(55, 120, 210), 1.5f))
                g.DrawRectangle(border, origin.X, origin.Y, dw, dh);

            // Centre crosshair guide
            float cx = origin.X + dw / 2f;
            float cy = origin.Y + dh / 2f;
            using (var guide = new Pen(Color.FromArgb(35, 80, 80), 1f) { DashStyle = DashStyle.Dash })
            {
                g.DrawLine(guide, cx, origin.Y, cx, origin.Y + dh);
                g.DrawLine(guide, origin.X, cy, origin.X + dw, cy);
            }

            // Quarter-grid guide lines
            using (var qGuide = new Pen(Color.FromArgb(22, 60, 60), 1f) { DashStyle = DashStyle.Dot })
            {
                g.DrawLine(qGuide, origin.X + dw * 0.25f, origin.Y, origin.X + dw * 0.25f, origin.Y + dh);
                g.DrawLine(qGuide, origin.X + dw * 0.75f, origin.Y, origin.X + dw * 0.75f, origin.Y + dh);
                g.DrawLine(qGuide, origin.X, origin.Y + dh * 0.25f, origin.X + dw, origin.Y + dh * 0.25f);
                g.DrawLine(qGuide, origin.X, origin.Y + dh * 0.75f, origin.X + dw, origin.Y + dh * 0.75f);
            }

            // Snap grid
            if (_snapToGrid && _gridSize > 0)
            {
                float gridPx = _gridSize * scale;
                if (gridPx >= 4f) // only draw when grid cells are large enough to see
                {
                    using (var gridPen = new Pen(Color.FromArgb(18, 70, 130, 180), 1f))
                    {
                        for (float gx = 0; gx <= _layout.SurfaceWidth; gx += _gridSize)
                        {
                            float sx = origin.X + gx * scale;
                            g.DrawLine(gridPen, sx, origin.Y, sx, origin.Y + dh);
                        }
                        for (float gy = 0; gy <= _layout.SurfaceHeight; gy += _gridSize)
                        {
                            float sy = origin.Y + gy * scale;
                            g.DrawLine(gridPen, origin.X, sy, origin.X + dw, sy);
                        }
                    }
                }
            }

            // Sprites — bottom layer first
            foreach (var sprite in _layout.Sprites)
            {
                if (sprite.IsHidden) continue;
                DrawSprite(g, sprite, sprite == _selectedSprite, scale, origin);
            }

            // ── Debug overlays ───────────────────────────────────────────────────
            if (_overlayMode == DebugOverlayMode.OverdrawHeatmap)
                DrawOverdrawHeatmap(g, scale, origin, dw, dh);
            else if (_overlayMode == DebugOverlayMode.BoundingBoxes)
                DrawBoundingBoxOverlay(g, scale, origin);

            // Per-sprite size warnings (⚠)
            if (_showSizeWarnings && SizeWarnings != null)
                DrawSizeWarnings(g, scale, origin);

            // Surface size label + zoom
            string zoomLabel = _zoom >= 0.995f && _zoom <= 1.005f ? "" : $"  Zoom: {_zoom:P0}";
            using (var lf = new Font("Segoe UI", 8f))
            using (var lb = new SolidBrush(Color.FromArgb(80, 160, 160)))
                g.DrawString($"{_layout.SurfaceWidth} × {_layout.SurfaceHeight}{zoomLabel}", lf, lb,
                    origin.X + 3, origin.Y + dh + 3);
        }

        private void DrawSprite(Graphics g, SpriteEntry sprite, bool selected, float scale, PointF origin)
        {
            var rect = GetSpriteScreenRect(sprite, scale, origin);

            // When isolating a call, dim sprites not in the highlighted set
            bool dimmed = HighlightedSprites != null && !HighlightedSprites.Contains(sprite);
            if (dimmed)
            {
                // Draw at ~25% opacity by wrapping in a temporary container
                var state = g.Save();
                var cm = new ColorMatrix { Matrix33 = 0.2f };
                // We can't apply ColorMatrix to vector drawing easily,
                // so we render into a bitmap and draw that at reduced alpha.
                int bw = Math.Max(1, (int)Math.Ceiling(rect.Width + 2));
                int bh = Math.Max(1, (int)Math.Ceiling(rect.Height + 2));
                // Clamp to avoid huge off-screen allocations
                if (bw > 2048) bw = 2048;
                if (bh > 2048) bh = 2048;
                using (var bmp = new Bitmap(bw, bh))
                using (var bg = Graphics.FromImage(bmp))
                {
                    bg.SmoothingMode = SmoothingMode.AntiAlias;
                    bg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    // Offset so sprite draws at (0,0) within the bitmap
                    var offsetRect = new RectangleF(0, 0, rect.Width, rect.Height);
                    if (sprite.Type == SpriteEntryType.Text)
                        DrawTextSprite(bg, sprite, offsetRect, scale);
                    else
                        DrawTextureSprite(bg, sprite, offsetRect);

                    using (var ia = new ImageAttributes())
                    {
                        ia.SetColorMatrix(cm);
                        g.DrawImage(bmp,
                            new[] { new PointF(rect.Left, rect.Top), new PointF(rect.Right, rect.Top), new PointF(rect.Left, rect.Bottom) },
                            new RectangleF(0, 0, bw, bh),
                            GraphicsUnit.Pixel, ia);
                    }
                }
                g.Restore(state);
                return; // no selection handles or REF label for dimmed sprites
            }

            if (sprite.Type == SpriteEntryType.Text)
                DrawTextSprite(g, sprite, rect, scale);
            else
                DrawTextureSprite(g, sprite, rect);

            // Reference layout indicator — dashed border with label
            if (sprite.IsReferenceLayout)
            {
                using (var refPen = new Pen(Color.FromArgb(120, 255, 200, 60), 1f) { DashStyle = DashStyle.Dot })
                    g.DrawRectangle(refPen, rect.X, rect.Y, rect.Width, rect.Height);

                if (rect.Width > 24 && rect.Height > 10)
                {
                    using (var rf = new Font("Segoe UI", 6.5f, FontStyle.Italic, GraphicsUnit.Pixel))
                    using (var rb = new SolidBrush(Color.FromArgb(140, 255, 200, 60)))
                        g.DrawString("REF", rf, rb, rect.X + 2, rect.Y + 1);
                }
            }

            if (selected)
            {
                // Selection border
                using (var selPen = new Pen(Color.FromArgb(255, 80, 200, 255), 1.5f))
                    g.DrawRectangle(selPen, rect.X, rect.Y, rect.Width, rect.Height);

                DrawHandles(g, rect);
            }
        }

        private void DrawTextSprite(Graphics g, SpriteEntry sprite, RectangleF rect, float viewScale)
        {
            var color = sprite.Color;

            // Dashed bounding box (always shown for text)
            using (var boxPen = new Pen(Color.FromArgb(100, 255, 200, 0), 1f) { DashStyle = DashStyle.Dash })
                g.DrawRectangle(boxPen, rect.X, rect.Y, rect.Width, rect.Height);

            string text = sprite.Text ?? "";
            if (text.Length == 0) return;

            // Try atlas-based glyph rendering first
            var fontAtlas = _textureCache?.FontAtlas;
            if (fontAtlas != null && TryDrawAtlasText(g, fontAtlas, sprite, text, rect, viewScale))
                return;

            // Fallback: GDI+ DrawString (for standard ASCII/Unicode when no atlas loaded)
            DrawGdiFallbackText(g, sprite, text, rect, viewScale);
        }

        /// <summary>
        /// Attempts to render text using SE font atlas glyph bitmaps.
        /// Returns true if at least one character was resolved from the atlas.
        /// For mixed strings (some atlas, some not), renders atlas glyphs as bitmaps
        /// and falls back to GDI+ for unresolved characters.
        /// </summary>
        private bool TryDrawAtlasText(Graphics g, SeFontAtlas fontAtlas, SpriteEntry sprite,
                                      string text, RectangleF rect, float viewScale)
        {
            string fontId = sprite.FontId ?? "White";

            // Check if ANY character in this string has atlas data
            bool hasAnyAtlasGlyph = false;
            for (int i = 0; i < text.Length; i++)
            {
                if (fontAtlas.GetMetrics(fontId, text[i]) != null)
                {
                    hasAnyAtlasGlyph = true;
                    break;
                }
            }
            if (!hasAnyAtlasGlyph) return false;

            // SE font base line height is ~28.8 surface-px at Scale=1.0.
            // The atlas glyphs have a native height (typically 45px for the white font).
            // We scale from native glyph size to the desired display size.
            float desiredLineHeight = sprite.Scale * 28.8f * viewScale;

            // Determine native line height from a reference glyph (space or first available)
            GlyphMetrics? refMetrics = fontAtlas.GetMetrics(fontId, ' ')
                                    ?? fontAtlas.GetMetrics(fontId, 'A');
            float nativeLineHeight = refMetrics.HasValue ? refMetrics.Value.Height : 45f;
            float glyphScale = desiredLineHeight / nativeLineHeight;

            // Calculate total advance width to handle alignment
            float totalAdvance = 0f;
            for (int i = 0; i < text.Length; i++)
            {
                GlyphMetrics? gm = fontAtlas.GetMetrics(fontId, text[i]);
                if (gm.HasValue)
                    totalAdvance += gm.Value.AdvanceWidth * glyphScale;
                else
                    totalAdvance += desiredLineHeight * 0.5f; // estimate for non-atlas chars
            }

            // Horizontal start position based on alignment
            float startX;
            switch (sprite.Alignment)
            {
                case SpriteTextAlignment.Right:
                    startX = rect.Right - totalAdvance;
                    break;
                case SpriteTextAlignment.Center:
                    startX = rect.X + (rect.Width - totalAdvance) / 2f;
                    break;
                default: // Left
                    startX = rect.X;
                    break;
            }

            float cursorX = startX;
            float cursorY = rect.Y;
            var color = sprite.Color;
            var prevMode = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            for (int i = 0; i < text.Length; i++)
            {
                int codepoint = text[i];
                GlyphMetrics? gm = fontAtlas.GetMetrics(fontId, codepoint);

                if (gm.HasValue)
                {
                    Bitmap glyphBmp = fontAtlas.GetGlyph(fontId, codepoint);
                    if (glyphBmp != null)
                    {
                        float drawW = gm.Value.Width * glyphScale;
                        float drawH = gm.Value.Height * glyphScale;
                        float drawX = cursorX + gm.Value.LeftSideBearing * glyphScale;
                        float drawY = cursorY;

                        var destRect = new RectangleF(drawX, drawY, drawW, drawH);

                        // ForceWhite glyphs are alpha-masks — tint with sprite color.
                        // Baked glyphs (swatches etc.) render as-is.
                        if (gm.Value.ForceWhite)
                            DrawTintedTexture(g, glyphBmp, destRect, color);
                        else
                            DrawTintedTexture(g, glyphBmp, destRect, Color.White);
                    }
                    cursorX += gm.Value.AdvanceWidth * glyphScale;
                }
                else
                {
                    // Non-atlas character: render with GDI+ inline
                    float fontSize = Math.Max(6f, desiredLineHeight * 0.7f);
                    using (var font = new Font("Segoe UI", fontSize, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(color))
                    {
                        string ch = text[i].ToString();
                        g.DrawString(ch, font, brush, cursorX, cursorY);
                        var sz = g.MeasureString(ch, font);
                        cursorX += sz.Width * 0.75f; // approximate to avoid GDI+ padding
                    }
                }
            }

            g.InterpolationMode = prevMode;
            return true;
        }

        /// <summary>
        /// Fallback text rendering using GDI+ DrawString (when no font atlas is loaded).
        /// </summary>
        private static void DrawGdiFallbackText(Graphics g, SpriteEntry sprite, string text,
                                                RectangleF rect, float viewScale)
        {
            var color = sprite.Color;

            const float SeBaseFontEm = 20f;
            float fontSize = Math.Max(6f, sprite.Scale * SeBaseFontEm * viewScale);

            StringAlignment sa;
            switch (sprite.Alignment)
            {
                case SpriteTextAlignment.Left:  sa = StringAlignment.Near;  break;
                case SpriteTextAlignment.Right: sa = StringAlignment.Far;   break;
                default:                        sa = StringAlignment.Center; break;
            }

            using (var sf = new StringFormat { Alignment = sa, LineAlignment = StringAlignment.Near, FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip })
            using (var brush = new SolidBrush(color))
            using (var font = new Font("Segoe UI", Math.Max(6f, fontSize), GraphicsUnit.Pixel))
                g.DrawString(text, font, brush, rect, sf);
        }

        private void DrawTextureSprite(Graphics g, SpriteEntry sprite, RectangleF rect)
        {
            var color = sprite.Color;
            var state = g.Save();

            g.TranslateTransform(rect.X + rect.Width  / 2f, rect.Y + rect.Height / 2f);
            g.RotateTransform(sprite.Rotation * 180f / (float)Math.PI);
            var r = new RectangleF(-rect.Width / 2f, -rect.Height / 2f, rect.Width, rect.Height);

            // Try real texture first (from SE Content directory)
            Bitmap tex = _textureCache?.GetTexture(sprite.SpriteName);
            if (tex != null)
            {
                DrawTintedTexture(g, tex, r, color);
                g.Restore(state);
                return;
            }

            using (var brush = new SolidBrush(color))
            {
                string key = sprite.SpriteName?.ToLowerInvariant() ?? "";
                switch (key)
                {
                    case "circle":
                        g.FillEllipse(brush, r);
                        break;

                    case "semicircle":
                        g.FillPie(brush, r.X, r.Y, r.Width, r.Height, 180f, 180f);
                        break;

                    case "triangle":
                        g.FillPolygon(brush, new[]
                        {
                            new PointF(0f,      r.Top),
                            new PointF(r.Right, r.Bottom),
                            new PointF(r.Left,  r.Bottom),
                        });
                        break;

                    case "righttriangle":
                        g.FillPolygon(brush, new[]
                        {
                            new PointF(r.Left,  r.Top),
                            new PointF(r.Right, r.Bottom),
                            new PointF(r.Left,  r.Bottom),
                        });
                        break;

                    case "dot":
                        float d = Math.Min(r.Width, r.Height) * 0.45f;
                        g.FillEllipse(brush, -d / 2f, -d / 2f, d, d);
                        break;

                    case "squaresimple":
                        g.FillRectangle(brush, r);
                        break;

                    default:
                        // No texture available — draw filled rect + centred name label
                        g.FillRectangle(brush, r);
                        if (r.Width > 18 && r.Height > 12)
                        {
                            int lum = (color.R * 299 + color.G * 587 + color.B * 114) / 1000;
                            var textColor = lum > 128 ? Color.FromArgb(200, 0, 0, 0) : Color.FromArgb(200, 255, 255, 255);
                            float fs = Math.Max(7f, Math.Min(r.Width * 0.14f, 12f));
                            using (var lFont = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel))
                            using (var lb = new SolidBrush(textColor))
                            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                                g.DrawString(sprite.SpriteName ?? "", lFont, lb, r, sf);
                        }
                        break;
                }
            }
            g.Restore(state);
        }

        /// <summary>
        /// Draws a texture bitmap tinted by the sprite's color using a ColorMatrix.
        /// SE sprites are typically white textures that get multiplied by the tint color.
        /// </summary>
        private static void DrawTintedTexture(Graphics g, Bitmap tex, RectangleF dest, Color tint)
        {
            float rm = tint.R / 255f;
            float gm = tint.G / 255f;
            float bm = tint.B / 255f;
            float am = tint.A / 255f;

            var cm = new ColorMatrix(new[]
            {
                new[] { rm,  0f,  0f,  0f, 0f },
                new[] { 0f,  gm,  0f,  0f, 0f },
                new[] { 0f,  0f,  bm,  0f, 0f },
                new[] { 0f,  0f,  0f,  am, 0f },
                new[] { 0f,  0f,  0f,  0f, 1f },
            });

            using (var ia = new ImageAttributes())
            {
                ia.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(tex,
                    new[] { new PointF(dest.Left, dest.Top), new PointF(dest.Right, dest.Top), new PointF(dest.Left, dest.Bottom) },
                    new RectangleF(0, 0, tex.Width, tex.Height),
                    GraphicsUnit.Pixel, ia);
            }
        }

        // ── Selection handles ─────────────────────────────────────────────────────
        private const float HandleSize = 8f;
        private const float HandleHalf = HandleSize / 2f;

        private RectangleF[] GetHandleRects(RectangleF r)
        {
            float cx = r.X + r.Width  / 2f;
            float cy = r.Y + r.Height / 2f;
            return new[]
            {
                new RectangleF(r.X     - HandleHalf, r.Y      - HandleHalf, HandleSize, HandleSize), // NW
                new RectangleF(cx      - HandleHalf, r.Y      - HandleHalf, HandleSize, HandleSize), // N
                new RectangleF(r.Right - HandleHalf, r.Y      - HandleHalf, HandleSize, HandleSize), // NE
                new RectangleF(r.Right - HandleHalf, cy       - HandleHalf, HandleSize, HandleSize), // E
                new RectangleF(r.Right - HandleHalf, r.Bottom - HandleHalf, HandleSize, HandleSize), // SE
                new RectangleF(cx      - HandleHalf, r.Bottom - HandleHalf, HandleSize, HandleSize), // S
                new RectangleF(r.X     - HandleHalf, r.Bottom - HandleHalf, HandleSize, HandleSize), // SW
                new RectangleF(r.X     - HandleHalf, cy       - HandleHalf, HandleSize, HandleSize), // W
            };
        }

        private void DrawHandles(Graphics g, RectangleF rect)
        {
            foreach (var h in GetHandleRects(rect))
            {
                g.FillRectangle(Brushes.White, h);
                using (var p = new Pen(Color.FromArgb(255, 60, 180, 255)))
                    g.DrawRectangle(p, h.X, h.Y, h.Width, h.Height);
            }
        }

        private DragMode HitTestHandle(PointF pt, RectangleF spriteRect)
        {
            var rects = GetHandleRects(spriteRect);
            for (int i = 0; i < rects.Length; i++)
            {
                var h = rects[i];
                h.Inflate(2f, 2f);
                if (h.Contains(pt)) return HandleModes[i];
            }
            return DragMode.None;
        }

        // ── Mouse interaction ─────────────────────────────────────────────────────
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_layout == null) return;
            Focus();

            // Middle-click = pan
            if (e.Button == MouseButtons.Middle)
            {
                _isPanning = true;
                _panStart = new PointF(e.X, e.Y);
                _panOrigOffset = _panOffset;
                Capture = true;
                Cursor = Cursors.Hand;
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            ComputeTransform(out float scale, out PointF origin);
            var pt = new PointF(e.X, e.Y);

            // Check resize handles on the currently selected sprite first
            if (_selectedSprite != null && !_selectedSprite.IsHidden)
            {
                var selRect = GetSpriteScreenRect(_selectedSprite, scale, origin);
                var mode = HitTestHandle(pt, selRect);
                if (mode != DragMode.None) { BeginDrag(mode, pt, _selectedSprite); return; }
                if (selRect.Contains(pt))  { BeginDrag(DragMode.Move, pt, _selectedSprite); return; }
            }

            // Hit-test all sprites in reverse (top-layer first)
            for (int i = _layout.Sprites.Count - 1; i >= 0; i--)
            {
                if (_layout.Sprites[i].IsHidden) continue;
                var rect = GetSpriteScreenRect(_layout.Sprites[i], scale, origin);
                if (rect.Contains(pt))
                {
                    SelectedSprite = _layout.Sprites[i];
                    BeginDrag(DragMode.Move, pt, _selectedSprite);
                    return;
                }
            }

            // Clicked empty — deselect
            SelectedSprite = null;
        }

        private void BeginDrag(DragMode mode, PointF screenPt, SpriteEntry sprite)
        {
            DragStarting?.Invoke(this, EventArgs.Empty);
            _dragMode  = mode;
            _dragStart = screenPt;
            _dragOrigX = sprite.X;
            _dragOrigY = sprite.Y;
            _dragOrigW = sprite.Width;
            _dragOrigH = sprite.Height;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_layout == null) return;

            // Handle panning
            if (_isPanning)
            {
                _panOffset = new PointF(
                    _panOrigOffset.X + e.X - _panStart.X,
                    _panOrigOffset.Y + e.Y - _panStart.Y);
                Invalidate();
                return;
            }

            ComputeTransform(out float scale, out PointF origin);
            var pt = new PointF(e.X, e.Y);

            if (_dragMode == DragMode.None || _selectedSprite == null)
            {
                UpdateCursor(pt, scale, origin);
                return;
            }

            float dx = (pt.X - _dragStart.X) / scale;
            float dy = (pt.Y - _dragStart.Y) / scale;
            var s = _selectedSprite;

            switch (_dragMode)
            {
                case DragMode.Move:
                    s.X = Snap(_dragOrigX + dx);
                    s.Y = Snap(_dragOrigY + dy);
                    break;
                case DragMode.ResizeNW:
                    s.Width  = Math.Max(10f, Snap(_dragOrigW - dx * 2f));
                    s.Height = Math.Max(10f, Snap(_dragOrigH - dy * 2f));
                    break;
                case DragMode.ResizeN:
                    s.Height = Math.Max(10f, Snap(_dragOrigH - dy * 2f));
                    break;
                case DragMode.ResizeNE:
                    s.Width  = Math.Max(10f, Snap(_dragOrigW + dx * 2f));
                    s.Height = Math.Max(10f, Snap(_dragOrigH - dy * 2f));
                    break;
                case DragMode.ResizeE:
                    s.Width  = Math.Max(10f, Snap(_dragOrigW + dx * 2f));
                    break;
                case DragMode.ResizeSE:
                    s.Width  = Math.Max(10f, Snap(_dragOrigW + dx * 2f));
                    s.Height = Math.Max(10f, Snap(_dragOrigH + dy * 2f));
                    break;
                case DragMode.ResizeS:
                    s.Height = Math.Max(10f, Snap(_dragOrigH + dy * 2f));
                    break;
                case DragMode.ResizeSW:
                    s.Width  = Math.Max(10f, Snap(_dragOrigW - dx * 2f));
                    s.Height = Math.Max(10f, Snap(_dragOrigH + dy * 2f));
                    break;
                case DragMode.ResizeW:
                    s.Width  = Math.Max(10f, Snap(_dragOrigW - dx * 2f));
                    break;
            }

            SpriteModified?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (_isPanning)
            {
                _isPanning = false;
                Capture = false;
                Cursor = Cursors.Default;
                return;
            }

            if (_dragMode != DragMode.None)
            {
                DragCompleted?.Invoke(this, EventArgs.Empty);
            }
            Capture = false;
            _dragMode = DragMode.None;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float factor = e.Delta > 0 ? 1.15f : 1f / 1.15f;
            Zoom = _zoom * factor;
        }

        private void UpdateCursor(PointF pt, float scale, PointF origin)
        {
            if (_selectedSprite == null) { Cursor = Cursors.Default; return; }

            var rect = GetSpriteScreenRect(_selectedSprite, scale, origin);
            switch (HitTestHandle(pt, rect))
            {
                case DragMode.ResizeNW: case DragMode.ResizeSE: Cursor = Cursors.SizeNWSE;  break;
                case DragMode.ResizeNE: case DragMode.ResizeSW: Cursor = Cursors.SizeNESW;  break;
                case DragMode.ResizeN:  case DragMode.ResizeS:  Cursor = Cursors.SizeNS;    break;
                case DragMode.ResizeE:  case DragMode.ResizeW:  Cursor = Cursors.SizeWE;    break;
                default: Cursor = rect.Contains(pt) ? Cursors.SizeAll : Cursors.Default;    break;
            }
        }

        // ── Public actions ────────────────────────────────────────────────────────
        public SpriteEntry AddSprite(string name, bool isText)
        {
            if (_layout == null) return null;

            var sprite = new SpriteEntry
            {
                Type      = isText ? SpriteEntryType.Text : SpriteEntryType.Texture,
                SpriteName = name,
                Text      = isText ? "Hello LCD" : name,
                X         = _layout.SurfaceWidth  / 2f,
                Y         = _layout.SurfaceHeight / 2f,
                Width     = isText ? 200f : 100f,
                Height    = isText ?  40f : 100f,
            };

            _layout.Sprites.Add(sprite);
            SelectedSprite = sprite;   // fires SelectionChanged + Invalidate
            return sprite;
        }

        public void DeleteSelected()
        {
            if (_selectedSprite == null || _layout == null) return;
            _layout.Sprites.Remove(_selectedSprite);
            SelectedSprite = null;
        }

        public SpriteEntry DuplicateSelected()
        {
            if (_selectedSprite == null || _layout == null) return null;
            var src = _selectedSprite;
            var dup = new SpriteEntry
            {
                Type       = src.Type,
                SpriteName = src.SpriteName,
                X          = src.X + 20f,
                Y          = src.Y + 20f,
                Width      = src.Width,
                Height     = src.Height,
                ColorR     = src.ColorR,
                ColorG     = src.ColorG,
                ColorB     = src.ColorB,
                ColorA     = src.ColorA,
                Rotation   = src.Rotation,
                Text       = src.Text,
                FontId     = src.FontId,
                Alignment  = src.Alignment,
                Scale      = src.Scale,
            };
            _layout.Sprites.Add(dup);
            SelectedSprite = dup;
            return dup;
        }

        public void NudgeSelected(float dx, float dy)
        {
            if (_selectedSprite == null) return;
            _selectedSprite.X = Snap(_selectedSprite.X + dx);
            _selectedSprite.Y = Snap(_selectedSprite.Y + dy);
            SpriteModified?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }

        public void CenterSelected()
        {
            if (_selectedSprite == null || _layout == null) return;
            _selectedSprite.X = _layout.SurfaceWidth  / 2f;
            _selectedSprite.Y = _layout.SurfaceHeight / 2f;
            SpriteModified?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }

        public void ResetView()
        {
            _zoom = 1f;
            _panOffset = PointF.Empty;
            Invalidate();
        }

        public void MoveSelectedUp()
        {
            if (_selectedSprite == null || _layout == null) return;
            int i = _layout.Sprites.IndexOf(_selectedSprite);
            if (i < _layout.Sprites.Count - 1)
            {
                _layout.Sprites.RemoveAt(i);
                _layout.Sprites.Insert(i + 1, _selectedSprite);
                Invalidate();
            }
        }

        public void MoveSelectedDown()
        {
            if (_selectedSprite == null || _layout == null) return;
            int i = _layout.Sprites.IndexOf(_selectedSprite);
            if (i > 0)
            {
                _layout.Sprites.RemoveAt(i);
                _layout.Sprites.Insert(i - 1, _selectedSprite);
                Invalidate();
            }
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

        // ── Debug overlay rendering ──────────────────────────────────────────────

        // Heatmap color ramp: 1 = blue, 2 = green, 3 = yellow, 4+ = red
        private static readonly Color[] HeatmapColors =
        {
            Color.FromArgb(0, 0, 0, 0),       // 0 layers — transparent
            Color.FromArgb(60, 40, 80, 200),   // 1 layer — subtle blue
            Color.FromArgb(80, 40, 180, 80),   // 2 layers — green
            Color.FromArgb(100, 220, 220, 40), // 3 layers — yellow
            Color.FromArgb(120, 220, 80, 30),  // 4 layers — orange
            Color.FromArgb(140, 220, 30, 30),  // 5+ layers — red
        };

        private void DrawOverdrawHeatmap(Graphics g, float scale, PointF origin, float dw, float dh)
        {
            if (_layout == null) return;
            const int cellSize = 8;
            var map = Services.DebugAnalyzer.ComputeOverdrawMap(_layout, cellSize);
            if (map == null) return;

            int cols = map.GetLength(0);
            int rows = map.GetLength(1);
            float cellW = cellSize * scale;
            float cellH = cellSize * scale;

            for (int c = 0; c < cols; c++)
            {
                for (int r = 0; r < rows; r++)
                {
                    int count = map[c, r];
                    if (count == 0) continue;

                    int idx = Math.Min(count, HeatmapColors.Length - 1);
                    var color = HeatmapColors[idx];
                    float x = origin.X + c * cellW;
                    float y = origin.Y + r * cellH;

                    using (var brush = new SolidBrush(color))
                        g.FillRectangle(brush, x, y, cellW, cellH);
                }
            }

            // Legend
            using (var lf = new Font("Segoe UI", 7f))
            using (var lb = new SolidBrush(Color.FromArgb(180, 255, 255, 255)))
            {
                float lx = origin.X + dw + 4;
                float ly = origin.Y;
                g.DrawString("Overdraw", lf, lb, lx, ly);
                for (int i = 1; i < HeatmapColors.Length; i++)
                {
                    float ry = ly + 14 + (i - 1) * 14;
                    using (var cb = new SolidBrush(Color.FromArgb(200, HeatmapColors[i])))
                        g.FillRectangle(cb, lx, ry, 10, 10);
                    string label = i < HeatmapColors.Length - 1 ? $"{i}×" : $"{i}+×";
                    g.DrawString(label, lf, lb, lx + 13, ry - 1);
                }
            }
        }

        private void DrawBoundingBoxOverlay(Graphics g, float scale, PointF origin)
        {
            if (_layout == null) return;

            // Cycle through distinguishable colors for each sprite
            Color[] palette =
            {
                Color.FromArgb(160, 255, 80, 80),
                Color.FromArgb(160, 80, 255, 80),
                Color.FromArgb(160, 80, 80, 255),
                Color.FromArgb(160, 255, 255, 80),
                Color.FromArgb(160, 80, 255, 255),
                Color.FromArgb(160, 255, 80, 255),
                Color.FromArgb(160, 255, 160, 60),
                Color.FromArgb(160, 60, 200, 180),
            };

            int idx = 0;
            foreach (var sprite in _layout.Sprites)
            {
                if (sprite.IsHidden) continue;
                var rect = GetSpriteScreenRect(sprite, scale, origin);
                var c = palette[idx % palette.Length];
                using (var pen = new Pen(c, 1f) { DashStyle = DashStyle.Dash })
                    g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);

                // Draw sprite index label
                using (var lf = new Font("Segoe UI", 6.5f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var lb = new SolidBrush(c))
                    g.DrawString($"#{idx}", lf, lb, rect.X + 1, rect.Y - 9);

                idx++;
            }
        }

        private void DrawSizeWarnings(Graphics g, float scale, PointF origin)
        {
            if (SizeWarnings == null) return;

            using (var warnFont = new Font("Segoe UI", 9f, FontStyle.Bold))
            using (var warnBrush = new SolidBrush(Color.FromArgb(230, 255, 180, 40)))
            using (var bgBrush = new SolidBrush(Color.FromArgb(180, 40, 30, 0)))
            {
                foreach (var w in SizeWarnings)
                {
                    if (w.Sprite.IsHidden) continue;
                    var rect = GetSpriteScreenRect(w.Sprite, scale, origin);
                    string label = $"\u26A0 {w.TextureWidth}\u00D7{w.TextureHeight} \u2192 {w.RenderedWidth:F0}\u00D7{w.RenderedHeight:F0}";
                    var sz = g.MeasureString(label, warnFont);
                    float tx = rect.X + rect.Width / 2f - sz.Width / 2f;
                    float ty = rect.Y - sz.Height - 2;
                    g.FillRectangle(bgBrush, tx - 2, ty, sz.Width + 4, sz.Height);
                    g.DrawString(label, warnFont, warnBrush, tx, ty);
                }
            }
        }
    }
}
