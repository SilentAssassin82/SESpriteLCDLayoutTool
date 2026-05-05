using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using SESpriteLCDLayoutTool.Models;
using SESpriteLCDLayoutTool.Models.Rig;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool.Controls
{
    /// <summary>Alignment operations available for multi-sprite selections.</summary>
    public enum AlignMode
    {
        Left, Right, Top, Bottom,
        CenterH, CenterV,
        SpaceH, SpaceV,
    }

    /// <summary>
    /// Top-level canvas interaction mode. In <see cref="Sprites"/> mode the canvas behaves
    /// exactly as before (sprite select/drag/resize). In <see cref="Rig"/> mode all sprite
    /// gestures are suppressed so bone editing can't accidentally move sprites; pan/zoom
    /// still work. Bone gestures themselves arrive in a later phase — for now Rig mode is
    /// purely a safe-viewing mode that draws the bone overlay.
    /// </summary>
    public enum CanvasEditMode
    {
        Sprites,
        Rig,
    }

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
        private HashSet<SpriteEntry> _selectedSprites = new HashSet<SpriteEntry>();
        private SpriteTextureCache _textureCache;

        private DragMode _dragMode = DragMode.None;
        private PointF _dragStart;
        private float _dragOrigX, _dragOrigY, _dragOrigW, _dragOrigH;

        // Multi-sprite drag: original positions keyed by sprite Id
        private Dictionary<string, PointF> _multiDragOrigPositions = new Dictionary<string, PointF>();

        // Box-select (rubber band)
        private bool _isBoxSelecting;
        private PointF _boxSelectStart;
        private RectangleF _boxSelectRect;

        // Zoom & pan
        private float _zoom = 1f;
        private PointF _panOffset = PointF.Empty;  // screen-pixel offset applied after fit-to-view
        private bool _isPanning;
        private PointF _panStart;
        private PointF _panOrigOffset;

        // Snap-to-grid
        private bool _snapToGrid;
        private int _gridSize = 16;

        // Snap-to-sprite
        private bool _snapToSprite;
        private const float SnapSpriteThreshold = 8f;  // screen pixels
        // Active snap guide lines (surface coords, NaN = none)
        private float _snapGuideX = float.NaN;
        private float _snapGuideY = float.NaN;

        // Drag-performance: cached bitmap of everything except the dragged sprite
        private Bitmap _dragCache;

        // ── Rig-mode interactive state ──────────────────────────────────────────
        private enum BoneDragMode { None, Joint, Tip, Rotate, RigTranslate, BindingOffset }
        private BoneDragMode _boneDrag;
        private Rig _activeRig;          // rig containing the bone being dragged or hovered
        private Bone _activeBone;        // bone currently grabbed (joint or tip)
        private float _boneDragStartLocalX, _boneDragStartLocalY;
        private float _boneDragStartRotation;
        private float _boneDragStartLength;
        // For joint drag: parent's world transform is needed to convert mouse→local.
        private RigTransform _boneDragParentWorld;
        // For rotate drag (FK): joint world position + reference mouse angle at drag start.
        private float _boneRotateJointX, _boneRotateJointY;
        private float _boneRotateRefAngle;
        // For whole-rig translate (Shift+drag in Rig mode).
        private float _rigDragStartOriginX, _rigDragStartOriginY;
        private float _rigDragStartSurfaceX, _rigDragStartSurfaceY;
        // For Alt+drag binding-offset adjustment.
        private SpriteBinding _activeBinding;
        private float _bindingDragStartOffX, _bindingDragStartOffY;
        private float _bindingDragStartSurfaceX, _bindingDragStartSurfaceY;
        private RigTransform _bindingDragBoneWorld;
        // For IK tip-drag: also auto-key the parent bone whose rotation we changed.
        private Bone _ikSecondaryBone;
        // Cached world transforms for the most recently painted frame; used for hit-testing.
        private Dictionary<string, (Rig rig, Bone bone, RigTransform world)> _lastBoneWorlds
            = new Dictionary<string, (Rig, Bone, RigTransform)>();
        // Per-sprite render override produced from rig bindings (not persisted).
        private Dictionary<int, RigEvaluator.EvaluatedSprite> _spriteRigOverride
            = new Dictionary<int, RigEvaluator.EvaluatedSprite>();

        // ── Rig animation preview ──────────────────────────────────────────────
        // When an animation is being scrubbed/played in the rig editor, the canvas
        // applies sampled clip overrides on top of the rest pose. This is purely a
        // render-time effect; it never mutates the rig.
        /// <summary>
        /// Current animation preview time in seconds. Set by the rig editor's
        /// timeline/playback; the canvas folds clip samples into the rig pose.
        /// </summary>
        public float AnimationPreviewTime { get; set; }

        /// <summary>If true, animation overrides are applied even outside Rig edit mode.</summary>
        public bool AnimationPreviewEnabled { get; set; }

        /// <summary>
        /// If true and a clip is being previewed, the rig is also drawn (faintly) at the
        /// previous and next keyframe times so the user can pose between them.
        /// </summary>
        public bool OnionSkinEnabled { get; set; }

        /// <summary>
        /// How many keyframes either side of the playhead to render as onion-skin ghosts.
        /// Clamped to [1, 5]. Each successive ghost fades by roughly 1/(n+1) so older
        /// frames don't dominate the canvas.
        /// </summary>
        public int OnionSkinKeyCount { get; set; } = 1;

        // ── Events ───────────────────────────────────────────────────────────────
        public event EventHandler SelectionChanged;
        public event EventHandler SpriteModified;
        /// <summary>Fired once before a drag operation begins — push undo snapshot here.</summary>
        public event EventHandler DragStarting;
        /// <summary>Fired once when a drag operation ends.</summary>
        public event EventHandler DragCompleted;
        /// <summary>Fired when the user picks a different bone via canvas gestures (Rig mode).</summary>
        public event EventHandler<Bone> BoneSelected;
        /// <summary>Fired after an interactive bone edit (move joint / rotate / drag tip) so hosts can refresh inspectors.</summary>
        public event EventHandler<Bone> BoneEdited;
        /// <summary>
        /// Fired when a bone gesture ends (mouse up). Hosts can use this to auto-key the
        /// bone's pose at the current preview time during animation authoring.
        /// </summary>
        public event EventHandler<Bone> BoneDragCompleted;
        /// <summary>
        /// Fired on every mouse move over the canvas surface.
        /// Args are the mouse position in surface coordinates (may be outside 0…surfaceSize when panned).
        /// </summary>
        public event Action<float, float> SurfaceMouseMoved;

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
                // Single-selection clears the multi-select set and adds only this sprite
                _selectedSprites.Clear();
                if (value != null)
                    _selectedSprites.Add(value);
                Invalidate();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Returns the set of currently selected sprites for multi-select operations.
        /// </summary>
        public HashSet<SpriteEntry> SelectedSprites => _selectedSprites;

        public float Zoom
        {
            get => _zoom;
            set { _zoom = Math.Max(0.1f, Math.Min(value, 8f)); InvalidateDragCache(); Invalidate(); }
        }

        public bool SnapToGrid
        {
            get => _snapToGrid;
            set { _snapToGrid = value; Invalidate(); }
        }

        public bool SnapToSprite
        {
            get => _snapToSprite;
            set { _snapToSprite = value; Invalidate(); }
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

        /// <summary>
        /// When true (default), each text sprite is drawn with a gold dashed
        /// bounding-box outline as a layout aid. Turn off for clean screenshots
        /// or GIF exports.
        /// </summary>
        public bool ShowTextBoundingBoxes
        {
            get => _showTextBoundingBoxes;
            set { _showTextBoundingBoxes = value; Invalidate(); }
        }

        private DebugOverlayMode _overlayMode = DebugOverlayMode.None;
        private bool _showSizeWarnings;
        private bool _showTextBoundingBoxes = true;

        /// <summary>Cached size warnings, refreshed externally when layout changes.</summary>
        internal List<Services.DebugAnalyzer.SizeWarning> SizeWarnings { get; set; }

        /// <summary>True while the user is actively dragging a sprite (move or resize).</summary>
        public bool IsDragging => _dragMode != DragMode.None;

        /// <summary>
        /// Current top-level interaction mode. Defaults to <see cref="CanvasEditMode.Sprites"/>.
        /// In <see cref="CanvasEditMode.Rig"/> mode all sprite mouse gestures are suppressed
        /// (no select, drag, resize, box-select) so the user can author bones without
        /// accidentally nudging sprites. Pan (middle-click) and zoom still work.
        /// </summary>
        public CanvasEditMode EditMode
        {
            get => _editMode;
            set
            {
                if (_editMode == value) return;
                _editMode = value;
                // Cancel any in-flight sprite gesture when leaving Sprites mode.
                _dragMode = DragMode.None;
                _isBoxSelecting = false;
                _boxSelectRect = RectangleF.Empty;
                InvalidateDragCache();
                Invalidate();
            }
        }
        private CanvasEditMode _editMode = CanvasEditMode.Sprites;

        /// <summary>
        /// Bone currently highlighted in the rig overlay (drawn brighter). Setting this
        /// only repaints; it does not change rig data. Cleared when leaving Rig mode.
        /// </summary>
        public Bone HighlightedBone
        {
            get => _highlightedBone;
            set { _highlightedBone = value; Invalidate(); }
        }
        private Bone _highlightedBone;

        /// <summary>
        /// When true, sprite centres are clamped to [0, SurfaceWidth] × [0, SurfaceHeight]
        /// during drag and nudge operations so sprites cannot be moved off the LCD surface.
        /// </summary>
        public bool ConstrainToSurface { get; set; }

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

        // ── Public transform accessor (used by rulers) ────────────────────────────
        /// <summary>Returns the current canvas-to-screen scale and surface origin.</summary>
        public void GetCurrentTransform(out float scale, out PointF origin)
            => ComputeTransform(out scale, out origin);

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

        /// <summary>
        /// Checks all other sprites' edges and centres and snaps the dragged sprite
        /// to the nearest within <see cref="SnapSpriteThreshold"/> screen pixels.
        /// Sets <see cref="_snapGuideX"/> / <see cref="_snapGuideY"/> for guide-line rendering.
        /// </summary>
        private void ApplySnapToSprite(SpriteEntry dragged, ref float newX, ref float newY, float scale)
        {
            _snapGuideX = float.NaN;
            _snapGuideY = float.NaN;
            if (_layout == null) return;

            float hw = Math.Abs(dragged.Width)  / 2f;
            float hh = Math.Abs(dragged.Height) / 2f;
            float thresh = SnapSpriteThreshold / scale;

            // Candidate snap values: dragged sprite's own edges and centre
            // vs target sprite's edges and centre (in surface coords)
            float bestDx = thresh + 1f;
            float bestDy = thresh + 1f;
            float snapX  = float.NaN;
            float snapY  = float.NaN;

            foreach (var other in _layout.Sprites)
            {
                if (other == dragged || other.IsHidden) continue;

                float ow = Math.Abs(other.Width)  / 2f;
                float oh = Math.Abs(other.Height) / 2f;

                // X snap: left-to-left, right-to-right, left-to-right, right-to-left, centre-to-centre
                float[] selfX  = { newX - hw, newX, newX + hw };
                float[] otherX = { other.X - ow, other.X, other.X + ow };
                foreach (float sx in selfX)
                {
                    foreach (float ox in otherX)
                    {
                        float d = Math.Abs(sx - ox);
                        if (d < bestDx) { bestDx = d; snapX = ox - (sx - newX); }
                    }
                }

                // Y snap: top, centre, bottom
                float[] selfY  = { newY - hh, newY, newY + hh };
                float[] otherY = { other.Y - oh, other.Y, other.Y + oh };
                foreach (float sy in selfY)
                {
                    foreach (float oy in otherY)
                    {
                        float d = Math.Abs(sy - oy);
                        if (d < bestDy) { bestDy = d; snapY = oy - (sy - newY); }
                    }
                }
            }

            if (!float.IsNaN(snapX) && bestDx <= thresh) { newX = snapX; _snapGuideX = snapX - (newX - snapX) == 0 ? newX : newX; _snapGuideX = newX; }
            if (!float.IsNaN(snapY) && bestDy <= thresh) { newY = snapY; _snapGuideY = newY; }
        }

        /// <summary>Draws magenta snap guide lines when snap-to-sprite is active during drag.</summary>
        private void DrawSnapGuides(Graphics g, float scale, PointF origin, float dh, float dw)
        {
            using (var guidePen = new Pen(Color.FromArgb(220, 255, 60, 200), 1f) { DashStyle = DashStyle.Dash })
            {
                if (!float.IsNaN(_snapGuideX))
                {
                    float sx = origin.X + _snapGuideX * scale;
                    g.DrawLine(guidePen, sx, origin.Y, sx, origin.Y + dh);
                }
                if (!float.IsNaN(_snapGuideY))
                {
                    float sy = origin.Y + _snapGuideY * scale;
                    g.DrawLine(guidePen, origin.X, sy, origin.X + dw, sy);
                }
            }
        }

        /// <summary>
        /// Draws every rig in <see cref="_layout"/> as a bone hierarchy overlay.
        /// Each bone is rendered as a coloured line from its world origin out to its tip
        /// (along the bone's local +X axis), with a small filled dot at the joint.
        /// In Sprites mode the overlay is heavily dimmed so it doesn't compete with sprites;
        /// in Rig mode it draws at full opacity. Read-only — no hit-testing here.
        /// </summary>
        private void DrawRigOverlay(Graphics g, float scale, PointF origin, bool fullOpacity)
        {
            if (_layout == null || _layout.Rigs == null) return;

            int alpha = fullOpacity ? 255 : 200;
            int dotAlpha = fullOpacity ? 255 : 220;

            // Reset hit-test cache; only populated while actually drawing the overlay.
            if (fullOpacity) _lastBoneWorlds.Clear();

            foreach (var rig in _layout.Rigs)
            {
                if (rig == null || !rig.Enabled) continue;
                var clipOverrides = SampleActiveClipOverrides(rig);
                var bones = RigEvaluator.EvaluateBones(rig, clipOverrides);

                // Rig origin marker (small ring).
                float ox = origin.X + rig.OriginX * scale;
                float oy = origin.Y + rig.OriginY * scale;
                using (var ringPen = new Pen(Color.FromArgb(alpha, 200, 200, 80), 1.25f))
                    g.DrawEllipse(ringPen, ox - 4f, oy - 4f, 8f, 8f);

                if (rig.Bones == null) continue;

                foreach (var bone in rig.Bones)
                {
                    if (bone == null || bone.Hidden) continue;
                    if (!bones.TryGetValue(bone.Id, out var world)) continue;

                    if (fullOpacity && !string.IsNullOrEmpty(bone.Id))
                        _lastBoneWorlds[bone.Id] = (rig, bone, world);

                    // Bone tip = origin + (Length, 0) rotated by world rotation.
                    float cos = (float)Math.Cos(world.Rotation);
                    float sin = (float)Math.Sin(world.Rotation);
                    float tipLocalX = bone.Length * world.ScaleX;
                    float tipX = world.X + tipLocalX * cos;
                    float tipY = world.Y + tipLocalX * sin;

                    float bx1 = origin.X + world.X * scale;
                    float by1 = origin.Y + world.Y * scale;
                    float bx2 = origin.X + tipX * scale;
                    float by2 = origin.Y + tipY * scale;

                    bool highlight = bone == _highlightedBone && fullOpacity;
                    var col = bone.OverlayColor;
                    var penColor = Color.FromArgb(alpha, col.R, col.G, col.B);
                    float thickness = highlight ? 3f : 1.75f;
                    using (var bonePen = new Pen(penColor, thickness))
                        g.DrawLine(bonePen, bx1, by1, bx2, by2);

                    // Joint dot.
                    using (var jointBrush = new SolidBrush(Color.FromArgb(dotAlpha, 255, 255, 255)))
                        g.FillEllipse(jointBrush, bx1 - 2.5f, by1 - 2.5f, 5f, 5f);

                    if (highlight)
                    {
                        // Selection ring at the joint.
                        using (var selPen = new Pen(Color.FromArgb(255, 255, 220, 120), 1.5f))
                            g.DrawEllipse(selPen, bx1 - 5f, by1 - 5f, 10f, 10f);
                        // Tip handle (rotate / extend).
                        using (var tipBrush = new SolidBrush(Color.FromArgb(255, 255, 220, 120)))
                            g.FillEllipse(tipBrush, bx2 - 3.5f, by2 - 3.5f, 7f, 7f);
                    }
                }
            }
        }

        /// <summary>
        /// Draws ghost copies of the rig at the previous and next keyframe times of the
        /// active clip. Read-only — never mutates the rig or the clip.
        /// </summary>
        private void DrawOnionSkin(Graphics g, float scale, PointF origin)
        {
            if (_layout?.Rigs == null) return;

            foreach (var rig in _layout.Rigs)
            {
                if (rig == null || !rig.Enabled) continue;
                if (rig.Clips == null || rig.Clips.Count == 0) continue;
                if (string.IsNullOrEmpty(rig.ActiveClipId)) continue;
                var clip = rig.Clips.Find(c => c != null && c.Id == rig.ActiveClipId);
                if (clip == null || clip.Tracks == null) continue;

                // Collect distinct keyframe times across all tracks, sorted.
                var times = new List<float>();
                foreach (var trk in clip.Tracks)
                {
                    if (trk?.Keys == null) continue;
                    foreach (var k in trk.Keys)
                        if (k != null && !times.Contains(k.Time)) times.Add(k.Time);
                }
                if (times.Count < 1) continue;
                times.Sort();

                float now = AnimationPreviewTime;
                const float eps = 0.0005f;

                // Playback time keeps incrementing past Duration on looping clips, but
                // keyframe times live in [0, Duration]. Wrap `now` into that range so the
                // prev/next search picks neighbours relative to the *current loop* instead
                // of treating every wrapped frame as "past the last key".
                if (clip.Loop && clip.Duration > 0f)
                {
                    now = now % clip.Duration;
                    if (now < 0f) now += clip.Duration;
                }

                // Build ordered lists of "previous" times (descending from playhead) and
                // "next" times (ascending). With looping we wrap so the user always sees
                // the configured number of ghosts on each side.
                int n = Math.Max(1, Math.Min(5, OnionSkinKeyCount));
                var prevs = new List<float>(n);
                var nexts = new List<float>(n);

                int idxPrev = -1, idxNext = -1;
                for (int i = 0; i < times.Count; i++)
                {
                    if (times[i] < now - eps) idxPrev = i;
                    if (times[i] > now + eps) { idxNext = i; break; }
                }

                // If the playhead is past the last key (or before the first), there's no
                // direct prev/next within the array — wrap when the clip loops so ghosts
                // keep appearing at clip boundaries instead of vanishing.
                if (idxPrev < 0 && clip.Loop) idxPrev = times.Count - 1;
                if (idxNext < 0 && clip.Loop) idxNext = 0;

                int p = idxPrev;
                while (prevs.Count < n && p >= 0)
                {
                    prevs.Add(times[p]);
                    p--;
                    if (p < 0 && clip.Loop && times.Count > 0) p = times.Count - 1;
                    if (prevs.Count >= times.Count) break; // avoid infinite loop on tiny clips
                }

                int q = idxNext;
                while (nexts.Count < n && q >= 0 && q < times.Count)
                {
                    nexts.Add(times[q]);
                    q++;
                    if (q >= times.Count && clip.Loop) q = 0;
                    if (nexts.Count >= times.Count) break;
                }

                // Draw farthest-first so closer ghosts paint on top.
                for (int i = prevs.Count - 1; i >= 0; i--)
                {
                    int alpha = (int)(160f * (1f - (float)i / (n + 1)));
                    if (alpha < 30) alpha = 30;
                    DrawRigGhost(g, rig, RigClipSampler.Sample(clip, prevs[i]), scale, origin,
                                 Color.FromArgb(alpha, 120, 180, 255));
                }
                for (int i = nexts.Count - 1; i >= 0; i--)
                {
                    int alpha = (int)(160f * (1f - (float)i / (n + 1)));
                    if (alpha < 30) alpha = 30;
                    DrawRigGhost(g, rig, RigClipSampler.Sample(clip, nexts[i]), scale, origin,
                                 Color.FromArgb(alpha, 255, 160, 120));
                }
            }
        }

        /// <summary>Draws a single ghost pose of <paramref name="rig"/> using the supplied bone overrides.</summary>
        private void DrawRigGhost(Graphics g, Rig rig, Dictionary<string, RigKeyframe> overrides,
                                  float scale, PointF origin, Color tint)
        {
            var bones = RigEvaluator.EvaluateBones(rig, overrides);

            using (var bonePen = new Pen(tint, 1.25f))
            using (var jointBrush = new SolidBrush(Color.FromArgb(tint.A, 255, 255, 255)))
            {
                foreach (var bone in rig.Bones)
                {
                    if (bone == null || bone.Hidden) continue;
                    if (!bones.TryGetValue(bone.Id, out var world)) continue;

                    float cos = (float)Math.Cos(world.Rotation);
                    float sin = (float)Math.Sin(world.Rotation);
                    float tipLocalX = bone.Length * world.ScaleX;
                    float tipX = world.X + tipLocalX * cos;
                    float tipY = world.Y + tipLocalX * sin;

                    float bx1 = origin.X + world.X * scale;
                    float by1 = origin.Y + world.Y * scale;
                    float bx2 = origin.X + tipX * scale;
                    float by2 = origin.Y + tipY * scale;

                    g.DrawLine(bonePen, bx1, by1, bx2, by2);
                    g.FillEllipse(jointBrush, bx1 - 2f, by1 - 2f, 4f, 4f);
                }
            }

            // Bound-sprite ghosts — outline only, so they don't compete with the live frame.
            if (_layout?.Sprites == null || rig.Bindings == null) return;
            var poses = RigEvaluator.EvaluateBindings(rig, _layout, overrides);
            using (var spritePen = new Pen(tint, 1f))
            {
                foreach (var p in poses)
                {
                    if (p.SpriteIndex < 0 || p.SpriteIndex >= _layout.Sprites.Count) continue;
                    var sp = _layout.Sprites[p.SpriteIndex];
                    if (sp == null || sp.IsHidden) continue;

                    float w = Math.Abs(sp.Width  * p.ScaleX) * scale;
                    float h = Math.Abs(sp.Height * p.ScaleY) * scale;
                    float cx = origin.X + p.X * scale;
                    float cy = origin.Y + p.Y * scale;
                    var rect = new RectangleF(cx - w / 2f, cy - h / 2f, w, h);

                    var saved = g.Transform;
                    g.TranslateTransform(cx, cy);
                    g.RotateTransform(p.Rotation * 57.29578f); // rad→deg
                    g.TranslateTransform(-cx, -cy);
                    g.DrawRectangle(spritePen, rect.X, rect.Y, rect.Width, rect.Height);
                    g.Transform = saved;
                }
            }
        }

        private RectangleF GetSpriteScreenRect(SpriteEntry sprite, float scale, PointF origin)
        {
            // Apply per-frame rig override (non-destructive). When a binding poses this
            // sprite, X/Y are replaced by the rig-driven world position and width/height
            // are multiplied by the binding's scale. Rotation is read separately by
            // DrawTextureSprite via GetEffectiveRotation.
            float spX = sprite.X, spY = sprite.Y, spW = sprite.Width, spH = sprite.Height;
            if (TryGetSpriteOverride(sprite, out var ov))
            {
                spX = ov.X; spY = ov.Y;
                spW = sprite.Width * ov.ScaleX;
                spH = sprite.Height * ov.ScaleY;
            }

            // Use absolute values for rect calculation - negative sizes mean "flip" in SE
            float w = Math.Abs(spW) * scale;
            float h = Math.Abs(spH) * scale;

            if (sprite.Type == SpriteEntryType.Text)
            {
                // SE text positioning: Y = top edge, X depends on Alignment
                float x = origin.X + spX * scale;
                float y = origin.Y + spY * scale;

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
            float cx = origin.X + spX * scale;
            float cy = origin.Y + spY * scale;
            float hw = w / 2f;
            float hh = h / 2f;
            return new RectangleF(cx - hw, cy - hh, hw * 2f, hh * 2f);
        }

        // ── Painting ─────────────────────────────────────────────────────────────
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_layout == null) return;

            var g = e.Graphics;

            // Fast path: during drag, blit the cached background and draw only the active sprites
            if (_dragMode != DragMode.None && _selectedSprite != null && _dragCache != null)
            {
                g.DrawImageUnscaled(_dragCache, 0, 0);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                ComputeTransform(out float s, out PointF o);
                if (_selectedSprites.Count > 1)
                {
                    foreach (var sp in _selectedSprites)
                        DrawSprite(g, sp, true, s, o);
                }
                else
                {
                    DrawSprite(g, _selectedSprite, true, s, o);
                }
                // Draw snap guides on top during drag
                if (_snapToSprite && _dragMode == DragMode.Move && _layout != null)
                {
                    float dw = _layout.SurfaceWidth  * s;
                    float dh = _layout.SurfaceHeight * s;
                    DrawSnapGuides(g, s, o, dh, dw);
                }
                return;
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            PaintScene(g, null, true);
        }

        /// <summary>
        /// Renders the full scene. When <paramref name="excludeSprite"/> is non-null that
        /// sprite is skipped (used when building the drag background cache).
        /// When <paramref name="drawOverlays"/> is false, debug overlays are omitted.
        /// </summary>
        private void PaintScene(Graphics g, SpriteEntry excludeSprite, bool drawOverlays)
        {
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

            // Build sprite-rig override map for this frame (non-destructive).
            BuildSpriteRigOverrides();

            // Sprites — bottom layer first
            foreach (var sprite in _layout.Sprites)
            {
                if (sprite.IsHidden) continue;
                if (sprite == excludeSprite) continue;
                DrawSprite(g, sprite, _selectedSprites.Contains(sprite), scale, origin);
            }

            // Rig overlay: faint in Sprites mode (so users can see rigs exist), full in Rig mode.
            if (_layout.Rigs != null && _layout.Rigs.Count > 0)
            {
                if (_editMode == CanvasEditMode.Rig && OnionSkinEnabled && AnimationPreviewEnabled)
                    DrawOnionSkin(g, scale, origin);
                DrawRigOverlay(g, scale, origin, _editMode == CanvasEditMode.Rig);
            }

            if (drawOverlays)
            {
                // ── Debug overlays ───────────────────────────────────────────────────
                if (_overlayMode == DebugOverlayMode.OverdrawHeatmap)
                    DrawOverdrawHeatmap(g, scale, origin, dw, dh);
                else if (_overlayMode == DebugOverlayMode.BoundingBoxes)
                    DrawBoundingBoxOverlay(g, scale, origin);

                // Per-sprite size warnings (⚠)
                if (_showSizeWarnings && SizeWarnings != null)
                    DrawSizeWarnings(g, scale, origin);
            }

            // Surface size label + zoom
            string zoomLabel = _zoom >= 0.995f && _zoom <= 1.005f ? "" : $"  Zoom: {_zoom:P0}";
                 using (var lf = new Font("Segoe UI", 8f))
                using (var lb = new SolidBrush(Color.FromArgb(80, 160, 160)))
                    g.DrawString($"{_layout.SurfaceWidth} × {_layout.SurfaceHeight}{zoomLabel}", lf, lb,
                        origin.X + 3, origin.Y + dh + 3);

            // Rig-mode badge (top-left of surface) — clear visual cue that sprite gestures are disabled.
            if (_editMode == CanvasEditMode.Rig)
            {
                using (var bf = new Font("Segoe UI", 8.25f, FontStyle.Bold))
                using (var bg = new SolidBrush(Color.FromArgb(220, 60, 30, 80)))
                using (var fg = new SolidBrush(Color.FromArgb(255, 255, 220, 120)))
                {
                    const string label = " RIG MODE ";
                    var sz = g.MeasureString(label, bf);
                    var rect = new RectangleF(origin.X + 4, origin.Y + 4, sz.Width, sz.Height);
                    g.FillRectangle(bg, rect);
                    g.DrawString(label, bf, fg, rect.X, rect.Y);
                }
            }

            // Rubber-band box-select overlay
            if (_isBoxSelecting && (_boxSelectRect.Width > 1 || _boxSelectRect.Height > 1))
            {
                using (var fillBrush = new SolidBrush(Color.FromArgb(40, 80, 180, 255)))
                    g.FillRectangle(fillBrush, _boxSelectRect);
                using (var borderPen = new Pen(Color.FromArgb(200, 80, 180, 255), 1f) { DashStyle = DashStyle.Dash })
                    g.DrawRectangle(borderPen, _boxSelectRect.X, _boxSelectRect.Y, _boxSelectRect.Width, _boxSelectRect.Height);
            }

            // Snap-to-sprite guide lines
            if (_snapToSprite && (_dragMode == DragMode.Move))
                DrawSnapGuides(g, scale, origin, dh, dw);
            }

            /// <summary>
            /// Renders everything except the selected sprite(s) into <see cref="_dragCache"/>
            /// so that OnPaint only needs to blit this bitmap + draw the active sprites.
            /// </summary>
            private void BuildDragCache()
            {
                InvalidateDragCache();
                if (_layout == null || _selectedSprite == null) return;
                if (Width <= 0 || Height <= 0) return;

                _dragCache = new Bitmap(Width, Height);
                using (var g = Graphics.FromImage(_dragCache))
                {
                    g.Clear(BackColor);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                    // When multi-selecting, exclude ALL selected sprites from the cache
                    if (_selectedSprites.Count > 1)
                    {
                        ComputeTransform(out float scale, out PointF origin);
                        float dw = _layout.SurfaceWidth  * scale;
                        float dh = _layout.SurfaceHeight * scale;

                        // Draw the scene manually so we can skip the whole selectedSprites set
                        using (var bg = new SolidBrush(Color.FromArgb(12, 18, 30)))
                            g.FillRectangle(bg, origin.X, origin.Y, dw, dh);
                        using (var border = new Pen(Color.FromArgb(55, 120, 210), 1.5f))
                            g.DrawRectangle(border, origin.X, origin.Y, dw, dh);

                        foreach (var sp in _layout.Sprites)
                        {
                            if (sp.IsHidden) continue;
                            if (_selectedSprites.Contains(sp)) continue;
                            DrawSprite(g, sp, false, scale, origin);
                        }
                    }
                    else
                    {
                        PaintScene(g, _selectedSprite, false);
                    }
                }
            }

            private void InvalidateDragCache()
            {
                _dragCache?.Dispose();
                _dragCache = null;
            }

            /// <summary>
            /// Renders the current layout (background + visible sprites) to a fresh bitmap
            /// of the given pixel size, with no overlays, no grid, no selection handles,
            /// no dimming, and no zoom/pan applied. Used by the animated-GIF exporter.
            /// </summary>
            public Bitmap RenderLayoutToBitmap(int pixelWidth, int pixelHeight, bool hideReferenceBoxes = false)
            {
                if (pixelWidth  < 1) pixelWidth  = 1;
                if (pixelHeight < 1) pixelHeight = 1;

                var bmp = new Bitmap(pixelWidth, pixelHeight, PixelFormat.Format32bppArgb);
                if (_layout == null)
                    return bmp;

                float scale = Math.Min(
                    pixelWidth  / (float)_layout.SurfaceWidth,
                    pixelHeight / (float)_layout.SurfaceHeight);
                float displayW = _layout.SurfaceWidth  * scale;
                float displayH = _layout.SurfaceHeight * scale;
                var origin = new PointF((pixelWidth - displayW) / 2f, (pixelHeight - displayH) / 2f);

                // Temporarily clear the highlight set so the GIF output never shows dimming.
                var savedHighlight = HighlightedSprites;
                HighlightedSprites = null;
                // Optionally suppress the gold text bounding boxes for a cleaner GIF.
                bool savedBoxes = _showTextBoundingBoxes;
                if (hideReferenceBoxes) _showTextBoundingBoxes = false;
                try
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode     = SmoothingMode.AntiAlias;
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                        // Black bezel
                        g.Clear(Color.Black);
                        // LCD surface background (matches PaintScene)
                        using (var bg = new SolidBrush(Color.FromArgb(12, 18, 30)))
                            g.FillRectangle(bg, origin.X, origin.Y, displayW, displayH);

                        foreach (var sprite in _layout.Sprites)
                        {
                            if (sprite.IsHidden) continue;
                            if (hideReferenceBoxes && sprite.IsReferenceLayout) continue;
                            DrawSprite(g, sprite, selected: false, scale: scale, origin: origin);
                        }
                    }
                }
                finally
                {
                    HighlightedSprites = savedHighlight;
                    _showTextBoundingBoxes = savedBoxes;
                }

                return bmp;
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

            // Dashed bounding box (layout aid — toggleable via ShowTextBoundingBoxes)
            if (_showTextBoundingBoxes)
            {
                using (var boxPen = new Pen(Color.FromArgb(100, 255, 200, 0), 1f) { DashStyle = DashStyle.Dash })
                    g.DrawRectangle(boxPen, rect.X, rect.Y, rect.Width, rect.Height);
            }

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
            if (rect.Width <= 0 || rect.Height <= 0) return;

            var color = sprite.Color;
            var state = g.Save();

            g.TranslateTransform(rect.X + rect.Width  / 2f, rect.Y + rect.Height / 2f);

            // Apply flipping if sprite has negative width/height (SE convention)
            float scaleX = sprite.Width < 0 ? -1f : 1f;
            float scaleY = sprite.Height < 0 ? -1f : 1f;
            if (scaleX != 1f || scaleY != 1f)
                g.ScaleTransform(scaleX, scaleY);

            g.RotateTransform(GetEffectiveRotation(sprite) * 180f / (float)Math.PI);
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
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

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
            if (_layout == null)
            {
                System.Diagnostics.Debug.WriteLine("[LcdCanvas.OnMouseDown] _layout is NULL — ignoring click");
                return;
            }
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

            // Rig mode: hit-test bones and start a bone gesture if applicable.
            // Sprite gestures (selection, drag, box-select) remain suppressed.
            if (_editMode == CanvasEditMode.Rig)
            {
                ComputeTransform(out float rscale, out PointF rorigin);
                var rpt = new PointF(e.X, e.Y);
                bool shiftHeldRig = (Control.ModifierKeys & Keys.Shift) != 0;
                bool altHeldRig   = (Control.ModifierKeys & Keys.Alt)   != 0;

                // Bone joint/tip handles take precedence (small targets).
                if (TryHitTestBone(rpt, rscale, rorigin, out var hRig, out var hBone, out var hMode))
                {
                    _dragStart = rpt;
                    BeginBoneDrag(hRig, hBone, hMode);
                    return;
                }

                // Otherwise: clicking on a sprite bound to a bone.
                //   Alt+drag   → adjust that one binding's offset (fine-tune)
                //   Shift+drag → translate the whole rig
                //   plain drag → FK rotate the bone
                if (TryHitTestBoundSpriteBinding(rpt, rscale, rorigin, out var sRig, out var sBone, out var sBinding))
                {
                    _dragStart = rpt;
                    if (altHeldRig)
                        BeginBindingOffsetDrag(sRig, sBone, sBinding, rpt, rscale, rorigin);
                    else if (shiftHeldRig)
                        BeginRigTranslate(sRig, rpt, rscale, rorigin);
                    else
                        BeginBoneRotate(sRig, sBone, rpt, rscale, rorigin);
                    return;
                }

                // Empty click with Shift: still allow translating the active/first rig.
                if (shiftHeldRig && _layout != null && _layout.Rigs != null && _layout.Rigs.Count > 0)
                {
                    var firstRig = _layout.Rigs[0];
                    _dragStart = rpt;
                    BeginRigTranslate(firstRig, rpt, rscale, rorigin);
                    return;
                }

                HighlightedBone = null;
                BoneSelected?.Invoke(this, null);
                return;
            }

            bool shiftHeld = (Control.ModifierKeys & Keys.Shift) != 0;
            ComputeTransform(out float scale, out PointF origin);
            var pt = new PointF(e.X, e.Y);

            System.Diagnostics.Debug.WriteLine($"[LcdCanvas.OnMouseDown] Click at ({pt.X:F0},{pt.Y:F0}), sprites={_layout.Sprites.Count}, scale={scale:F3}, origin=({origin.X:F0},{origin.Y:F0}), highlighted={HighlightedSprites?.Count.ToString() ?? "null"}");

            // Check resize handles on the currently selected sprite first
            if (_selectedSprite != null && !_selectedSprite.IsHidden)
            {
                var selRect = GetSpriteScreenRect(_selectedSprite, scale, origin);
                var mode = HitTestHandle(pt, selRect);
                if (mode != DragMode.None) { BeginDrag(mode, pt, _selectedSprite); return; }
                if (selRect.Contains(pt))  { BeginDrag(DragMode.Move, pt, _selectedSprite); return; }
            }

            // Hit-test all sprites in reverse (top-layer first)
            // Stale-ref guard: if HighlightedSprites is set but contains NONE of the
            // current layout sprites, it's from a previous execution — clear it so
            // clicking isn't silently blocked.
            if (HighlightedSprites != null && _layout.Sprites.Count > 0)
            {
                bool anyMatch = false;
                foreach (var sp in _layout.Sprites)
                {
                    if (HighlightedSprites.Contains(sp)) { anyMatch = true; break; }
                }
                if (!anyMatch)
                {
                    System.Diagnostics.Debug.WriteLine($"[LcdCanvas.OnMouseDown] ⚠ HighlightedSprites is STALE ({HighlightedSprites.Count} entries match none of {_layout.Sprites.Count} sprites) — clearing");
                    HighlightedSprites = null;
                }
            }

            int skippedHidden = 0, skippedHighlight = 0, testedCount = 0;
            for (int i = _layout.Sprites.Count - 1; i >= 0; i--)
            {
                if (_layout.Sprites[i].IsHidden) { skippedHidden++; continue; }
                if (_layout.Sprites[i].IsLocked) { continue; }
                // During isolation, skip dimmed (non-highlighted) sprites
                if (HighlightedSprites != null && !HighlightedSprites.Contains(_layout.Sprites[i])) { skippedHighlight++; continue; }
                var rect = GetSpriteScreenRect(_layout.Sprites[i], scale, origin);
                testedCount++;
                if (rect.Contains(pt))
                {
                    var clickedSprite = _layout.Sprites[i];
                    System.Diagnostics.Debug.WriteLine($"[LcdCanvas.OnMouseDown] ✓ Hit sprite [{i}] '{clickedSprite.DisplayName}' rect=({rect.X:F0},{rect.Y:F0},{rect.Width:F0},{rect.Height:F0})");

                    if (shiftHeld)
                    {
                        // Shift+click: toggle selection (add/remove from multi-select)
                        if (_selectedSprites.Contains(clickedSprite))
                        {
                            _selectedSprites.Remove(clickedSprite);
                            // Update _selectedSprite to another selected sprite, or null
                            _selectedSprite = _selectedSprites.Count > 0 ? GetFirstSelectedSprite() : null;
                        }
                        else
                        {
                            _selectedSprites.Add(clickedSprite);
                            _selectedSprite = clickedSprite;
                        }
                        Invalidate();
                        SelectionChanged?.Invoke(this, EventArgs.Empty);
                    }
                    else
                    {
                        // Normal click on a sprite already in multi-select: start group drag
                        // Normal click on a different sprite: single-select and drag
                        if (!_selectedSprites.Contains(clickedSprite))
                            SelectedSprite = clickedSprite;
                        else
                            _selectedSprite = clickedSprite; // keep multi-select, update primary
                        BeginDrag(DragMode.Move, pt, _selectedSprite);
                    }
                    return;
                }
            }

            System.Diagnostics.Debug.WriteLine($"[LcdCanvas.OnMouseDown] ✗ No sprite hit — tested={testedCount}, skippedHidden={skippedHidden}, skippedHighlight={skippedHighlight}");

            // Clicked empty — start rubber-band box-select (not an immediate deselect)
            if (!shiftHeld)
            {
                _selectedSprites.Clear();
                _selectedSprite = null;
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
            _isBoxSelecting = true;
            _boxSelectStart = pt;
            _boxSelectRect  = new RectangleF(pt.X, pt.Y, 0, 0);
            Capture = true;
            Invalidate();
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

            // Capture original positions of all selected sprites for group move
            _multiDragOrigPositions.Clear();
            foreach (var s in _selectedSprites)
                _multiDragOrigPositions[s.Id] = new PointF(s.X, s.Y);

            Capture = true;
            BuildDragCache();
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
                // Fire ruler update even while panning
                ComputeTransform(out float ps, out PointF po);
                SurfaceMouseMoved?.Invoke((e.X - po.X) / ps, (e.Y - po.Y) / ps);
                return;
            }

            ComputeTransform(out float scale, out PointF origin);
            var pt = new PointF(e.X, e.Y);

            // Fire surface position for ruler hairline tracking
            float surfX = (pt.X - origin.X) / scale;
            float surfY = (pt.Y - origin.Y) / scale;
            SurfaceMouseMoved?.Invoke(surfX, surfY);

            // Bone gesture in Rig mode takes precedence over everything else.
            if (_boneDrag != BoneDragMode.None)
            {
                UpdateBoneDrag(pt, scale, origin);
                return;
            }

            // Hover cursor in Rig mode: switch to Hand over a bone joint/tip OR a bound sprite.
            if (_editMode == CanvasEditMode.Rig)
            {
                bool overBone = TryHitTestBone(pt, scale, origin, out _, out _, out _);
                bool overBound = !overBone && TryHitTestBoundSprite(pt, scale, origin, out _, out _);
                Cursor = (overBone || overBound) ? Cursors.Hand : Cursors.Default;
            }

            // Box-select rubber-band update
            if (_isBoxSelecting)
            {
                float rx = Math.Min(_boxSelectStart.X, pt.X);
                float ry = Math.Min(_boxSelectStart.Y, pt.Y);
                float rw = Math.Abs(pt.X - _boxSelectStart.X);
                float rh = Math.Abs(pt.Y - _boxSelectStart.Y);
                _boxSelectRect = new RectangleF(rx, ry, rw, rh);
                Invalidate();
                return;
            }

            if (_dragMode == DragMode.None || _selectedSprite == null)
            {
                // In Rig mode we don't update sprite-resize cursors; keep the default arrow.
                if (_editMode == CanvasEditMode.Rig) { Cursor = Cursors.Default; return; }
                UpdateCursor(pt, scale, origin);
                return;
            }

            float dx = (pt.X - _dragStart.X) / scale;
            float dy = (pt.Y - _dragStart.Y) / scale;
            var s = _selectedSprite;

            switch (_dragMode)
            {
                case DragMode.Move:
                    // Move all selected sprites by the same delta
                    if (_selectedSprites.Count > 1)
                    {
                        _snapGuideX = float.NaN;
                        _snapGuideY = float.NaN;
                        foreach (var sp in _selectedSprites)
                        {
                            if (_multiDragOrigPositions.TryGetValue(sp.Id, out PointF orig))
                            {
                                sp.X = Snap(orig.X + dx);
                                sp.Y = Snap(orig.Y + dy);
                                if (ConstrainToSurface && _layout != null)
                                {
                                    sp.X = Math.Max(0f, Math.Min(_layout.SurfaceWidth,  sp.X));
                                    sp.Y = Math.Max(0f, Math.Min(_layout.SurfaceHeight, sp.Y));
                                }
                            }
                        }
                    }
                    else
                    {
                        float newX = Snap(_dragOrigX + dx);
                        float newY = Snap(_dragOrigY + dy);

                        // Snap-to-sprite: find nearest edge/centre on any other sprite
                        if (_snapToSprite)
                            ApplySnapToSprite(s, ref newX, ref newY, scale);
                        else { _snapGuideX = float.NaN; _snapGuideY = float.NaN; }

                        s.X = newX;
                        s.Y = newY;
                        if (ConstrainToSurface && _layout != null)
                        {
                            s.X = Math.Max(0f, Math.Min(_layout.SurfaceWidth,  s.X));
                            s.Y = Math.Max(0f, Math.Min(_layout.SurfaceHeight, s.Y));
                        }
                    }
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

            // End an active bone gesture (Rig mode).
            if (_boneDrag != BoneDragMode.None)
            {
                EndBoneDrag();
                return;
            }

            // Finalize rubber-band box-select
            if (_isBoxSelecting)
            {
                _isBoxSelecting = false;
                Capture = false;

                // Only run hit-test if the user actually dragged a meaningful rectangle
                if (_boxSelectRect.Width > 4 || _boxSelectRect.Height > 4)
                {
                    ComputeTransform(out float scale, out PointF origin);
                    bool shiftHeld = (Control.ModifierKeys & Keys.Shift) != 0;
                    if (!shiftHeld) _selectedSprites.Clear();

                    SpriteEntry last = null;
                    foreach (var sp in _layout?.Sprites ?? new System.Collections.Generic.List<SpriteEntry>())
                    {
                        if (sp.IsHidden) continue;
                        var sr = GetSpriteScreenRect(sp, scale, origin);
                        if (_boxSelectRect.IntersectsWith(sr))
                        {
                            _selectedSprites.Add(sp);
                            last = sp;
                        }
                    }
                    if (last != null) _selectedSprite = last;
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                }

                _boxSelectRect = RectangleF.Empty;
                Invalidate();
                return;
            }

            if (_dragMode != DragMode.None)
            {
                _snapGuideX = float.NaN;
                _snapGuideY = float.NaN;
                InvalidateDragCache();
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

        /// <summary>Returns the first sprite from the multi-select set (used when removing from selection).</summary>
        private SpriteEntry GetFirstSelectedSprite()
        {
            foreach (var sp in _selectedSprites)
                return sp;
            return null;
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
            if (_selectedSprites.Count > 1)
            {
                foreach (var s in _selectedSprites)
                {
                    s.X = Snap(s.X + dx);
                    s.Y = Snap(s.Y + dy);
                    if (ConstrainToSurface && _layout != null)
                    {
                        s.X = Math.Max(0f, Math.Min(_layout.SurfaceWidth,  s.X));
                        s.Y = Math.Max(0f, Math.Min(_layout.SurfaceHeight, s.Y));
                    }
                }
            }
            else if (_selectedSprite != null)
            {
                _selectedSprite.X = Snap(_selectedSprite.X + dx);
                _selectedSprite.Y = Snap(_selectedSprite.Y + dy);
                if (ConstrainToSurface && _layout != null)
                {
                    _selectedSprite.X = Math.Max(0f, Math.Min(_layout.SurfaceWidth,  _selectedSprite.X));
                    _selectedSprite.Y = Math.Max(0f, Math.Min(_layout.SurfaceHeight, _selectedSprite.Y));
                }
            }
            else return;
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

        /// <summary>Selects all visible sprites on the canvas.</summary>
        public void SelectAll()
        {
            if (_layout == null) return;
            _selectedSprites.Clear();
            SpriteEntry last = null;
            foreach (var sp in _layout.Sprites)
            {
                if (!sp.IsHidden) { _selectedSprites.Add(sp); last = sp; }
            }
            _selectedSprite = last;
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Aligns or distributes all sprites in <see cref="SelectedSprites"/>
        /// according to <paramref name="mode"/>.
        /// </summary>
        public void AlignSelection(AlignMode mode)
        {
            if (_selectedSprites.Count < 2) return;
            var sprites = new List<SpriteEntry>(_selectedSprites);

            // Helper: get axis-aligned bounding rect of a sprite in surface coords
            RectangleF SR(SpriteEntry s)
            {
                float w = Math.Abs(s.Width), h = Math.Abs(s.Height);
                if (s.Type == SpriteEntryType.Text)
                {
                    switch (s.Alignment)
                    {
                        case SpriteTextAlignment.Right:  return new RectangleF(s.X - w, s.Y, w, h);
                        case SpriteTextAlignment.Center: return new RectangleF(s.X - w / 2f, s.Y, w, h);
                        default:                         return new RectangleF(s.X, s.Y, w, h);
                    }
                }
                return new RectangleF(s.X - w / 2f, s.Y - h / 2f, w, h);
            }

            // Compute union bounds
            float minL = float.MaxValue, minT = float.MaxValue;
            float maxR = float.MinValue, maxB = float.MinValue;
            foreach (var s in sprites)
            {
                var r = SR(s);
                if (r.Left   < minL) minL = r.Left;
                if (r.Top    < minT) minT = r.Top;
                if (r.Right  > maxR) maxR = r.Right;
                if (r.Bottom > maxB) maxB = r.Bottom;
            }

            switch (mode)
            {
                case AlignMode.Left:
                    foreach (var s in sprites)
                    {
                        var r = SR(s); float delta = minL - r.Left;
                        if (s.Type == SpriteEntryType.Texture) s.X += delta;
                        else s.X += delta;
                    }
                    break;
                case AlignMode.Right:
                    foreach (var s in sprites)
                    {
                        var r = SR(s); float delta = maxR - r.Right;
                        s.X += delta;
                    }
                    break;
                case AlignMode.Top:
                    foreach (var s in sprites)
                    {
                        var r = SR(s); float delta = minT - r.Top;
                        s.Y += delta;
                    }
                    break;
                case AlignMode.Bottom:
                    foreach (var s in sprites)
                    {
                        var r = SR(s); float delta = maxB - r.Bottom;
                        s.Y += delta;
                    }
                    break;
                case AlignMode.CenterH:
                    float midH = (minT + maxB) / 2f;
                    foreach (var s in sprites)
                    {
                        var r = SR(s);
                        s.Y += midH - (r.Top + r.Height / 2f);
                    }
                    break;
                case AlignMode.CenterV:
                    float midV = (minL + maxR) / 2f;
                    foreach (var s in sprites)
                    {
                        var r = SR(s);
                        s.X += midV - (r.Left + r.Width / 2f);
                    }
                    break;
                case AlignMode.SpaceH:
                    if (sprites.Count < 3) break;
                    sprites.Sort((a, b) => SR(a).Left.CompareTo(SR(b).Left));
                    float totalW = 0f;
                    foreach (var s in sprites) totalW += SR(s).Width;
                    float gapH = (maxR - minL - totalW) / (sprites.Count - 1);
                    float curX = minL;
                    foreach (var s in sprites)
                    {
                        var r = SR(s);
                        float delta = curX - r.Left;
                        s.X += delta;
                        curX += r.Width + gapH;
                    }
                    break;
                case AlignMode.SpaceV:
                    if (sprites.Count < 3) break;
                    sprites.Sort((a, b) => SR(a).Top.CompareTo(SR(b).Top));
                    float totalH = 0f;
                    foreach (var s in sprites) totalH += SR(s).Height;
                    float gapV = (maxB - minT - totalH) / (sprites.Count - 1);
                    float curY = minT;
                    foreach (var s in sprites)
                    {
                        var r = SR(s);
                        float delta = curY - r.Top;
                        s.Y += delta;
                        curY += r.Height + gapV;
                    }
                    break;
            }

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
                SelectionChanged?.Invoke(this, EventArgs.Empty);
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
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); InvalidateDragCache(); Invalidate(); }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SurfaceMouseMoved?.Invoke(-1f, -1f);
        }

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

        // ── Rig render override + bone gestures ─────────────────────────────────

        /// <summary>
        /// Recomputes the per-sprite override map from all enabled rigs in the layout.
        /// Called once per paint; safe even when no rigs exist.
        /// Overrides are only applied while in Rig edit mode so that normal sprite editing
        /// (move/resize/rotate) outside Rig mode is never blocked by a binding.
        /// </summary>
        private void BuildSpriteRigOverrides()
        {
            _spriteRigOverride.Clear();
            if (_layout?.Rigs == null || _layout.Rigs.Count == 0) return;

            foreach (var rig in _layout.Rigs)
            {
                if (rig == null || !rig.Enabled) continue;
                var clipOverrides = SampleActiveClipOverrides(rig);
                var poses = RigEvaluator.EvaluateBindings(rig, _layout, clipOverrides);
                foreach (var p in poses)
                {
                    // Last-rig-wins is fine; users author one rig per binding in practice.
                    _spriteRigOverride[p.SpriteIndex] = p;
                }
            }
        }

        /// <summary>
        /// Returns per-bone local-transform overrides sampled from the rig's active clip
        /// at <see cref="AnimationPreviewTime"/>, or null if there is no usable clip or
        /// animation preview is disabled.
        /// </summary>
        private Dictionary<string, RigKeyframe> SampleActiveClipOverrides(Rig rig)
        {
            if (!AnimationPreviewEnabled) return null;
            if (rig == null || rig.Clips == null || rig.Clips.Count == 0) return null;
            if (string.IsNullOrEmpty(rig.ActiveClipId)) return null;
            var clip = rig.Clips.Find(c => c != null && c.Id == rig.ActiveClipId);
            if (clip == null) return null;
            var sampled = RigClipSampler.Sample(clip, AnimationPreviewTime);
            // While the user is interactively editing a bone, exclude it from the
            // sampled overrides so the live LocalX/Y/Rotation/Length the drag is
            // writing actually shows on screen. Otherwise the clip sample masks it
            // and the rig appears "locked" after the first keys are set.
            if (sampled != null && _activeBone != null && _activeRig == rig &&
                _boneDrag != BoneDragMode.None && _boneDrag != BoneDragMode.RigTranslate)
            {
                sampled.Remove(_activeBone.Id);
            }
            return sampled;
        }

        /// <summary>
        /// If the active clip currently samples a pose for <paramref name="bone"/>, copy
        /// that pose onto the bone's local fields. Lets a drag continue from the
        /// on-screen pose instead of jumping to the unsampled rest values.
        /// </summary>
        private void PrimeBoneFromSampledPose(Rig rig, Bone bone)
        {
            if (rig == null || bone == null) return;
            if (!AnimationPreviewEnabled) return;
            if (rig.Clips == null || rig.Clips.Count == 0) return;
            if (string.IsNullOrEmpty(rig.ActiveClipId)) return;
            var clip = rig.Clips.Find(c => c != null && c.Id == rig.ActiveClipId);
            if (clip == null) return;
            var sampled = RigClipSampler.Sample(clip, AnimationPreviewTime);
            if (sampled == null) return;
            if (!sampled.TryGetValue(bone.Id, out var k) || k == null) return;
            bone.LocalX = k.LocalX;
            bone.LocalY = k.LocalY;
            bone.LocalRotation = k.LocalRotation;
            bone.LocalScaleX = k.LocalScaleX;
            bone.LocalScaleY = k.LocalScaleY;
            bone.Length = k.Length;
        }

        private bool TryGetSpriteOverride(SpriteEntry sprite, out RigEvaluator.EvaluatedSprite ov)
        {
            ov = default;
            if (_layout == null || _spriteRigOverride.Count == 0) return false;
            int idx = _layout.Sprites.IndexOf(sprite);
            if (idx < 0) return false;
            return _spriteRigOverride.TryGetValue(idx, out ov);
        }

        private float GetEffectiveRotation(SpriteEntry sprite)
        {
            if (TryGetSpriteOverride(sprite, out var ov)) return ov.Rotation;
            return sprite.Rotation;
        }

        // ── Bone hit-testing & drag (Rig mode) ──────────────────────────────────

        private const float BoneJointHitRadius = 8f;   // screen px
        private const float BoneTipHitRadius   = 8f;

        private bool TryHitTestBone(PointF screenPt, float scale, PointF origin,
                                     out Rig hitRig, out Bone hitBone, out BoneDragMode hitMode)
        {
            hitRig = null; hitBone = null; hitMode = BoneDragMode.None;
            if (_lastBoneWorlds.Count == 0) return false;

            float bestDist = float.MaxValue;
            foreach (var kv in _lastBoneWorlds)
            {
                var (rig, bone, world) = kv.Value;
                if (bone.Locked || bone.Hidden) continue;

                float jx = origin.X + world.X * scale;
                float jy = origin.Y + world.Y * scale;

                // Tip in world space.
                float cos = (float)Math.Cos(world.Rotation);
                float sin = (float)Math.Sin(world.Rotation);
                float tipLocalX = bone.Length * world.ScaleX;
                float tx = origin.X + (world.X + tipLocalX * cos) * scale;
                float ty = origin.Y + (world.Y + tipLocalX * sin) * scale;

                float dJoint = Distance(screenPt, jx, jy);
                float dTip   = Distance(screenPt, tx, ty);

                if (dJoint <= BoneJointHitRadius && dJoint < bestDist)
                {
                    bestDist = dJoint; hitRig = rig; hitBone = bone; hitMode = BoneDragMode.Joint;
                }
                if (dTip <= BoneTipHitRadius && dTip < bestDist)
                {
                    bestDist = dTip; hitRig = rig; hitBone = bone; hitMode = BoneDragMode.Tip;
                }
            }
            return hitMode != BoneDragMode.None;
        }

        private static float Distance(PointF p, float x, float y)
        {
            float dx = p.X - x, dy = p.Y - y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Begin a bone gesture in Rig mode.</summary>
        private void BeginBoneDrag(Rig rig, Bone bone, BoneDragMode mode)
        {
            DragStarting?.Invoke(this, EventArgs.Empty);
            _activeRig = rig;
            _activeBone = bone;
            _boneDrag = mode;
            // If an animation preview is keying this bone right now, fold the sampled
            // pose onto the bone's local fields before the drag starts so the gesture
            // continues from where the bone is visually drawn (no snap-back).
            PrimeBoneFromSampledPose(rig, bone);
            _boneDragStartLocalX = bone.LocalX;
            _boneDragStartLocalY = bone.LocalY;
            _boneDragStartRotation = bone.LocalRotation;
            _boneDragStartLength = bone.Length;
            _boneDragParentWorld = ComputeBoneParentWorld(rig, bone);
            HighlightedBone = bone;
            BoneSelected?.Invoke(this, bone);
            Capture = true;
        }

        /// <summary>
        /// Begin an FK rotate of <paramref name="bone"/> around its joint, anchored on the
        /// initial mouse position. Children of the bone follow naturally.
        /// </summary>
        private void BeginBoneRotate(Rig rig, Bone bone, PointF screenPt, float scale, PointF origin)
        {
            DragStarting?.Invoke(this, EventArgs.Empty);
            _activeRig = rig;
            _activeBone = bone;
            _boneDrag = BoneDragMode.Rotate;
            // Fold the active animation sample into LocalRotation (etc.) before grabbing,
            // otherwise the clip sampler keeps overriding our writes during the drag and
            // the bone looks pinned to the canvas.
            PrimeBoneFromSampledPose(rig, bone);
            _boneDragStartRotation = bone.LocalRotation;

            var worlds = RigEvaluator.EvaluateBones(rig);
            if (worlds.TryGetValue(bone.Id, out var bw))
            {
                _boneRotateJointX = bw.X;
                _boneRotateJointY = bw.Y;
            }
            else
            {
                _boneRotateJointX = rig.OriginX;
                _boneRotateJointY = rig.OriginY;
            }

            float sxSurface = (screenPt.X - origin.X) / scale;
            float sySurface = (screenPt.Y - origin.Y) / scale;
            _boneRotateRefAngle = (float)Math.Atan2(
                sySurface - _boneRotateJointY,
                sxSurface - _boneRotateJointX);

            HighlightedBone = bone;
            BoneSelected?.Invoke(this, bone);
            Capture = true;
        }

        /// <summary>Begin translating the entire rig (Shift+drag in Rig mode).</summary>
        private void BeginRigTranslate(Rig rig, PointF screenPt, float scale, PointF origin)
        {
            DragStarting?.Invoke(this, EventArgs.Empty);
            _activeRig = rig;
            _activeBone = null;
            _boneDrag = BoneDragMode.RigTranslate;
            _rigDragStartOriginX = rig.OriginX;
            _rigDragStartOriginY = rig.OriginY;
            _rigDragStartSurfaceX = (screenPt.X - origin.X) / scale;
            _rigDragStartSurfaceY = (screenPt.Y - origin.Y) / scale;
            HighlightedBone = null;
            BoneSelected?.Invoke(this, null);
            Capture = true;
        }

        /// <summary>
        /// Begin Alt+drag of a binding's offset — moves only this bound sprite relative
        /// to its bone, leaving the bone (and any other sprites bound to it) untouched.
        /// </summary>
        private void BeginBindingOffsetDrag(Rig rig, Bone bone, SpriteBinding binding,
                                            PointF screenPt, float scale, PointF origin)
        {
            DragStarting?.Invoke(this, EventArgs.Empty);
            _activeRig = rig;
            _activeBone = bone;
            _activeBinding = binding;
            _boneDrag = BoneDragMode.BindingOffset;
            _bindingDragStartOffX = binding.OffsetX;
            _bindingDragStartOffY = binding.OffsetY;
            _bindingDragStartSurfaceX = (screenPt.X - origin.X) / scale;
            _bindingDragStartSurfaceY = (screenPt.Y - origin.Y) / scale;
            // Resolve current bone world transform once; we invert it on each move to
            // convert mouse delta into bone-local space.
            var worlds = RigEvaluator.EvaluateBones(rig, SampleActiveClipOverrides(rig));
            if (!worlds.TryGetValue(bone.Id, out _bindingDragBoneWorld))
                _bindingDragBoneWorld = new RigTransform(rig.OriginX, rig.OriginY, 0f, 1f, 1f);
            HighlightedBone = bone;
            BoneSelected?.Invoke(this, bone);
            Capture = true;
        }

        /// <summary>
        /// Hit-test sprites that are bound to a bone in any rig. Returns the topmost bound
        /// sprite under the cursor, the bone it is bound to, and the binding itself.
        /// </summary>
        private bool TryHitTestBoundSpriteBinding(PointF screenPt, float scale, PointF origin,
                                                  out Rig hitRig, out Bone hitBone, out SpriteBinding hitBinding)
        {
            hitRig = null; hitBone = null; hitBinding = null;
            if (_layout == null || _layout.Rigs == null || _layout.Rigs.Count == 0) return false;

            // Top-most first
            for (int i = _layout.Sprites.Count - 1; i >= 0; i--)
            {
                var sp = _layout.Sprites[i];
                if (sp.IsHidden || sp.IsLocked) continue;
                var rect = GetSpriteScreenRect(sp, scale, origin);
                if (!rect.Contains(screenPt)) continue;

                // Find a binding for this sprite index in any enabled rig.
                foreach (var rig in _layout.Rigs)
                {
                    if (rig == null || !rig.Enabled || rig.Bindings == null) continue;
                    foreach (var b in rig.Bindings)
                    {
                        if (b == null || b.Muted) continue;
                        if (b.SpriteIndex != i) continue;
                        var bone = rig.Bones?.Find(x => x != null && x.Id == b.BoneId);
                        if (bone == null || bone.Locked || bone.Hidden) continue;
                        hitRig = rig;
                        hitBone = bone;
                        hitBinding = b;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Cursor-hover convenience that ignores the binding.</summary>
        private bool TryHitTestBoundSprite(PointF screenPt, float scale, PointF origin,
                                           out Rig hitRig, out Bone hitBone)
        {
            return TryHitTestBoundSpriteBinding(screenPt, scale, origin, out hitRig, out hitBone, out _);
        }

        /// <summary>
        /// Compute the parent's world transform (or rig origin if the bone is a root).
        /// Used for converting screen→bone-local during a joint drag.
        /// </summary>
        private RigTransform ComputeBoneParentWorld(Rig rig, Bone bone)
        {
            var worlds = RigEvaluator.EvaluateBones(rig);
            if (!string.IsNullOrEmpty(bone.ParentId) && worlds.TryGetValue(bone.ParentId, out var pw))
                return pw;
            return new RigTransform(rig.OriginX, rig.OriginY, 0f, 1f, 1f);
        }

        /// <summary>
        /// Two-bone analytic IK. Given a parent bone whose joint sits at <paramref name="parentWorld"/>
        /// and a child bone (the dragged tip), rotates both so the child's tip reaches
        /// (<paramref name="targetX"/>, <paramref name="targetY"/>) in world/surface space.
        /// Bone lengths are preserved. The "elbow" prefers to bend in the same direction
        /// it currently bends so the limb doesn't suddenly flip across the chain.
        /// </summary>
        private void SolveTwoBoneIK(Bone parent, Bone child, RigTransform parentWorld,
                                    float targetX, float targetY,
                                    Dictionary<string, RigTransform> worlds)
        {
            // Find parent's joint world position. parentWorld already represents the parent
            // bone's frame at its own joint — its X/Y is the joint position.
            float jx = parentWorld.X;
            float jy = parentWorld.Y;

            // Effective lengths in surface space (account for accumulated parent scales).
            float gpScale = 1f;
            if (!string.IsNullOrEmpty(parent.ParentId) && worlds.TryGetValue(parent.ParentId, out var gpw))
                gpScale = gpw.ScaleX == 0f ? 1f : gpw.ScaleX;
            float parentScaleX = parentWorld.ScaleX == 0f ? 1f : parentWorld.ScaleX;

            float l1 = Math.Max(0.0001f, parent.Length * gpScale);
            float l2 = Math.Max(0.0001f, child.Length * parentScaleX);

            float dx = targetX - jx;
            float dy = targetY - jy;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy);

            // Clamp target into reachable annulus [|l1-l2|, l1+l2] so acos stays valid.
            float minR = Math.Abs(l1 - l2);
            float maxR = l1 + l2;
            float clamped = Math.Max(minR + 0.0001f, Math.Min(maxR - 0.0001f, dist));

            // Law of cosines for the interior angle at the parent's joint and the child's joint.
            float cosA = (l1 * l1 + clamped * clamped - l2 * l2) / (2f * l1 * clamped);
            if (cosA < -1f) cosA = -1f; else if (cosA > 1f) cosA = 1f;
            float a = (float)Math.Acos(cosA);

            float cosB = (l1 * l1 + l2 * l2 - clamped * clamped) / (2f * l1 * l2);
            if (cosB < -1f) cosB = -1f; else if (cosB > 1f) cosB = 1f;
            float b = (float)Math.Acos(cosB);

            float baseAngle = (float)Math.Atan2(dy, dx);

            // Elbow side: keep current sign of child's local rotation so we don't flip.
            float bendSign = child.LocalRotation >= 0f ? 1f : -1f;
            // π - b is the interior bend; signed by bendSign.
            float childLocalRot = bendSign * ((float)Math.PI - b);
            float parentWorldRot = baseAngle - bendSign * a;

            // Convert parent's desired world rotation back to its local rotation.
            float gpRot = 0f;
            if (!string.IsNullOrEmpty(parent.ParentId) && worlds.TryGetValue(parent.ParentId, out var gpw2))
                gpRot = gpw2.Rotation;
            else
                gpRot = (_activeRig != null) ? 0f : 0f; // rig origin contributes no rotation

            parent.LocalRotation = parentWorldRot - gpRot;
            child.LocalRotation = childLocalRot;
        }

        private void UpdateBoneDrag(PointF screenPt, float scale, PointF origin)
        {
            if (_activeBone == null || _activeRig == null) return;

            // Mouse in surface coordinates.
            float sxSurface = (screenPt.X - origin.X) / scale;
            float sySurface = (screenPt.Y - origin.Y) / scale;

            if (_boneDrag == BoneDragMode.Joint)
            {
                // Convert mouse surface point into the parent's local space, then assign to the bone's local pos.
                ToParentLocal(_boneDragParentWorld, sxSurface, sySurface, out float lx, out float ly);
                _activeBone.LocalX = lx;
                _activeBone.LocalY = ly;
            }
            else if (_boneDrag == BoneDragMode.Tip)
            {
                // Compute current world joint position to derive the new bone vector.
                var worlds = RigEvaluator.EvaluateBones(_activeRig);
                if (!worlds.TryGetValue(_activeBone.Id, out var bw)) return;
                float dx = sxSurface - bw.X;
                float dy = sySurface - bw.Y;

                // 2-bone IK when Ctrl is held and the dragged bone has a parent.
                // Rotates parent + this bone so the tip reaches the cursor while
                // both bones keep their existing lengths (like an arm/leg).
                bool ikRequested = (Control.ModifierKeys & Keys.Control) != 0;
                Bone parentBone = null;
                if (ikRequested && !string.IsNullOrEmpty(_activeBone.ParentId))
                    parentBone = _activeRig.Bones?.Find(x => x != null && x.Id == _activeBone.ParentId);

                if (parentBone != null && !parentBone.Locked
                    && worlds.TryGetValue(parentBone.Id, out var pw))
                {
                    SolveTwoBoneIK(parentBone, _activeBone, pw, sxSurface, sySurface, worlds);
                    _ikSecondaryBone = parentBone;
                }
                else
                {
                    float length = (float)Math.Sqrt(dx * dx + dy * dy);
                    float worldAngle = (float)Math.Atan2(dy, dx);

                    // local rotation = worldAngle - parent.Rotation
                    _activeBone.LocalRotation = worldAngle - _boneDragParentWorld.Rotation;
                    // Length is unscaled local length; divide by accumulated parent scale on X.
                    float parentSx = _boneDragParentWorld.ScaleX == 0 ? 1f : _boneDragParentWorld.ScaleX;
                    _activeBone.Length = Math.Max(1f, length / parentSx);
                }
            }
            else if (_boneDrag == BoneDragMode.Rotate)
            {
                // FK rotate: pivot around the bone's joint world position. Children move with us.
                float dx = sxSurface - _boneRotateJointX;
                float dy = sySurface - _boneRotateJointY;
                float curAngle = (float)Math.Atan2(dy, dx);
                float delta = curAngle - _boneRotateRefAngle;
                _activeBone.LocalRotation = _boneDragStartRotation + delta;
            }
            else if (_boneDrag == BoneDragMode.RigTranslate)
            {
                if (_activeRig != null)
                {
                    _activeRig.OriginX = _rigDragStartOriginX + (sxSurface - _rigDragStartSurfaceX);
                    _activeRig.OriginY = _rigDragStartOriginY + (sySurface - _rigDragStartSurfaceY);
                }
            }
            else if (_boneDrag == BoneDragMode.BindingOffset)
            {
                if (_activeBinding != null)
                {
                    // Convert the world-space mouse delta into bone-local space, then
                    // accumulate onto the binding's stored offset.
                    float dxW = sxSurface - _bindingDragStartSurfaceX;
                    float dyW = sySurface - _bindingDragStartSurfaceY;
                    float cos = (float)Math.Cos(-_bindingDragBoneWorld.Rotation);
                    float sin = (float)Math.Sin(-_bindingDragBoneWorld.Rotation);
                    float dxL = (dxW * cos - dyW * sin);
                    float dyL = (dxW * sin + dyW * cos);
                    float sx = _bindingDragBoneWorld.ScaleX == 0 ? 1f : _bindingDragBoneWorld.ScaleX;
                    float sy = _bindingDragBoneWorld.ScaleY == 0 ? 1f : _bindingDragBoneWorld.ScaleY;
                    _activeBinding.OffsetX = _bindingDragStartOffX + dxL / sx;
                    _activeBinding.OffsetY = _bindingDragStartOffY + dyL / sy;
                }
            }

            BoneEdited?.Invoke(this, _activeBone);
            Invalidate();
        }

        /// <summary>Inverse of RigTransform.TransformPoint: convert a world point into the parent's local space.</summary>
        private static void ToParentLocal(RigTransform parent, float wx, float wy, out float lx, out float ly)
        {
            float dx = wx - parent.X;
            float dy = wy - parent.Y;
            float cos = (float)Math.Cos(-parent.Rotation);
            float sin = (float)Math.Sin(-parent.Rotation);
            float rx = dx * cos - dy * sin;
            float ry = dx * sin + dy * cos;
            float sx = parent.ScaleX == 0 ? 1f : parent.ScaleX;
            float sy = parent.ScaleY == 0 ? 1f : parent.ScaleY;
            lx = rx / sx;
            ly = ry / sy;
        }

        private void EndBoneDrag()
        {
            if (_boneDrag == BoneDragMode.None) return;
            var endedBone = _activeBone;
            var endedSecondary = _ikSecondaryBone;
            bool wasBoneEdit = _boneDrag != BoneDragMode.RigTranslate
                            && _boneDrag != BoneDragMode.BindingOffset;
            _boneDrag = BoneDragMode.None;
            _activeRig = null;
            _activeBone = null;
            _activeBinding = null;
            _ikSecondaryBone = null;
            Capture = false;
            if (wasBoneEdit && endedBone != null)
                BoneDragCompleted?.Invoke(this, endedBone);
            if (wasBoneEdit && endedSecondary != null && endedSecondary != endedBone)
                BoneDragCompleted?.Invoke(this, endedSecondary);
            DragCompleted?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }
}
