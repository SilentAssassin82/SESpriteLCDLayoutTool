using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;
using SESpriteLCDLayoutTool.Controls;
using SESpriteLCDLayoutTool.Models;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool
{
    /// <summary>
    /// Visual keyframe animation editor dialog.
    /// Hosts a timeline with draggable keyframes, property editors,
    /// a sprite preview at the current playhead tick, and code generation.
    /// </summary>
    public sealed class KeyframeEditorDialog : Form
    {
        // ── Data ────────────────────────────────────────────────────────────────
        private readonly SpriteEntry _sprite;
        private readonly KeyframeAnimationParams _params;
        private readonly bool _isText;
        private readonly SpriteTextureCache _textureCache;

        // ── Controls ────────────────────────────────────────────────────────────
        private KeyframeTimeline _timeline;
        private Panel _previewPanel;
        private TextBox _txtCode;
        private TableLayoutPanel _pnlProps;
        private ListBox _lstKeyframes;
        private Label _lblStatus;
        private Label _lblPreviewInfo;
        private ComboBox _cmbLoop;
        private ComboBox _cmbListVar;
        private Timer _playTimer;
        private bool _isPlaying;

        // ── Preview state ───────────────────────────────────────────────────────
        private float _previewX, _previewY, _previewW, _previewH;
        private int _previewR, _previewG, _previewB, _previewA;
        private float _previewRotation, _previewScale;

        // ── Public results ──────────────────────────────────────────────────────

        /// <summary>The generated code, available after dialog interaction.</summary>
        public string GeneratedCode => _txtCode?.Text ?? "";

        /// <summary>Raised when the user clicks "Update Code" to push edited animation back to the code panel.</summary>
        public event Action<string> CodeUpdateRequested;

        // ── Constructor ─────────────────────────────────────────────────────────

        public KeyframeEditorDialog(SpriteEntry sprite, TargetScriptType target,
                                     KeyframeAnimationParams existing = null,
                                     SpriteTextureCache textureCache = null)
        {
            _sprite = sprite ?? throw new ArgumentNullException(nameof(sprite));
            _isText = sprite.Type == SpriteEntryType.Text;
            _textureCache = textureCache;

            if (existing != null)
            {
                // Re-open with saved keyframe data
                _params = existing;
                _params.TargetScript = target;
            }
            else
            {
                _params = new KeyframeAnimationParams
                {
                    TargetScript = target,
                    ListVarName = target == TargetScriptType.LcdHelper ? "sprites" : "frame",
                };
                _params.Keyframes.Add(Keyframe.FromSprite(sprite, 0));
                _params.Keyframes.Add(Keyframe.FromSprite(sprite, 60));
            }

            // Always keep the sprite's reference to the params up-to-date
            sprite.KeyframeAnimation = _params;

            InitializeComponent();
            WireEvents();
            RefreshAll();
            _timeline.ZoomToFit();
        }

        // ── UI Construction ─────────────────────────────────────────────────────

        private void InitializeComponent()
        {
            string targetLabel = TargetLabel(_params.TargetScript);
            string spriteName = _isText ? _sprite.Text : _sprite.SpriteName;

            Text = $"🎬 Keyframe Editor — \"{spriteName}\" [{targetLabel}]";
            Size = new Size(1050, 720);
            MinimumSize = new Size(800, 560);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(24, 24, 28);
            ForeColor = Color.FromArgb(220, 220, 220);
            Font = new Font("Segoe UI", 9f);
            KeyPreview = true;

            // ── Status bar ──
            _lblStatus = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 22,
                BackColor = Color.FromArgb(20, 20, 24),
                ForeColor = Color.FromArgb(140, 160, 180),
                Padding = new Padding(8, 3, 0, 0),
                Font = new Font("Segoe UI", 8f),
                Text = "Double-click timeline to add keyframes  |  Drag diamonds to move  |  Scroll to zoom  |  Middle-click to pan",
            };

            // ── Top settings bar ──
            var settingsBar = BuildSettingsBar(targetLabel);

            // ── Timeline ──
            _timeline = new KeyframeTimeline
            {
                Dock = DockStyle.Fill,
                Height = 170,
            };
            _timeline.SetData(_params.Keyframes, _isText);

            // ── Timeline toolbar ──
            var timelineToolbar = BuildTimelineToolbar();

            // ── Timeline panel (toolbar + timeline) ──
            var timelinePanel = new Panel
            {
                Dock = DockStyle.Fill,
            };
            timelinePanel.Controls.Add(_timeline);
            timelinePanel.Controls.Add(timelineToolbar);

            // ── Left panel: keyframe list + properties ──
            _lstKeyframes = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 32, 38),
                ForeColor = Color.FromArgb(220, 220, 220),
                Font = new Font("Consolas", 9f),
                IntegralHeight = false,
            };

            _pnlProps = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                ColumnCount = 2,
                Padding = new Padding(4),
            };
            _pnlProps.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            _pnlProps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var splitLeftInner = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                BackColor = Color.FromArgb(24, 24, 28),
                SplitterWidth = 3,
            };
            splitLeftInner.Panel1.Controls.Add(_lstKeyframes);
            splitLeftInner.Panel2.Controls.Add(_pnlProps);

            // ── Preview panel ──
            _previewPanel = new DoubleBufferedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
            };
            _previewPanel.Paint += OnPreviewPaint;

            _lblPreviewInfo = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 20,
                BackColor = Color.FromArgb(20, 20, 24),
                ForeColor = Color.FromArgb(130, 150, 170),
                Font = new Font("Consolas", 7.5f),
                TextAlign = ContentAlignment.MiddleCenter,
            };

            var previewContainer = new Panel { Dock = DockStyle.Fill };
            previewContainer.Controls.Add(_previewPanel);
            previewContainer.Controls.Add(_lblPreviewInfo);

            var lblPreviewHeader = new Label
            {
                Dock = DockStyle.Top,
                Height = 20,
                Text = "   Preview",
                ForeColor = Color.FromArgb(130, 160, 200),
                BackColor = Color.FromArgb(28, 30, 38),
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            };
            previewContainer.Controls.Add(lblPreviewHeader);

            // ── Code preview ──
            _txtCode = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BackColor = Color.FromArgb(18, 18, 22),
                ForeColor = Color.FromArgb(190, 210, 190),
                Font = new Font("Consolas", 9f),
                MaxLength = 0,
            };

            var lblCodeHeader = new Label
            {
                Dock = DockStyle.Top,
                Height = 20,
                Text = "   Generated Code",
                ForeColor = Color.FromArgb(130, 160, 200),
                BackColor = Color.FromArgb(28, 30, 38),
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            };

            var codePanel = new Panel { Dock = DockStyle.Fill };
            codePanel.Controls.Add(_txtCode);
            codePanel.Controls.Add(lblCodeHeader);

            // ── Bottom toolbar ──
            var bottomToolbar = BuildBottomToolbar();

            // ── Layout assembly ──
            // Bottom half: left = keyframe list + props + preview | right = code
            var splitBottom = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                BackColor = Color.FromArgb(24, 24, 28),
                SplitterWidth = 4,
            };

            // Left side of bottom: keyframe list/props above, preview below
            var splitBottomLeft = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                BackColor = Color.FromArgb(24, 24, 28),
                SplitterWidth = 3,
            };
            splitBottomLeft.Panel1.Controls.Add(splitLeftInner);
            splitBottomLeft.Panel2.Controls.Add(previewContainer);

            splitBottom.Panel1.Controls.Add(splitBottomLeft);
            splitBottom.Panel2.Controls.Add(codePanel);

            // Main vertical split: timeline on top, bottom panel below
            var splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                BackColor = Color.FromArgb(24, 24, 28),
                SplitterWidth = 4,
            };
            splitMain.Panel1.Controls.Add(timelinePanel);
            splitMain.Panel2.Controls.Add(splitBottom);

            Controls.Add(splitMain);
            Controls.Add(settingsBar);
            Controls.Add(bottomToolbar);
            Controls.Add(_lblStatus);

            // Set splitter distances after layout
            Load += (s, e) =>
            {
                try
                {
                    splitMain.SplitterDistance = Math.Max(140, (int)(Height * 0.3));
                    splitBottom.SplitterDistance = Math.Max(200, (int)(splitBottom.Width * 0.4));
                    splitBottomLeft.SplitterDistance = Math.Max(100, (int)(splitBottomLeft.Height * 0.55));
                    splitLeftInner.SplitterDistance = Math.Max(80, (int)(splitLeftInner.Height * 0.45));
                }
                catch { }
            };

            // Play timer for animation preview
            _playTimer = new Timer { Interval = 33 };
            _playTimer.Tick += OnPlayTimerTick;
        }

        /// <summary>Panel with double-buffering enabled for flicker-free painting.</summary>
        private sealed class DoubleBufferedPanel : Panel
        {
            public DoubleBufferedPanel()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint, true);
            }
        }

        // ── Settings bar ────────────────────────────────────────────────────────

        private Panel BuildSettingsBar(string targetLabel)
        {
            var bar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                BackColor = Color.FromArgb(28, 30, 38),
                Padding = new Padding(8, 4, 8, 4),
            };

            var lblTarget = new Label
            {
                Text = $"Target: {targetLabel}",
                AutoSize = true,
                Location = new Point(8, 8),
                ForeColor = Color.FromArgb(160, 200, 255),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            };

            var lblLoop = new Label
            {
                Text = "Loop:",
                AutoSize = true,
                Location = new Point(200, 9),
                ForeColor = Color.FromArgb(200, 200, 200),
            };

            _cmbLoop = new ComboBox
            {
                Location = new Point(240, 5),
                Width = 100,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(45, 45, 50),
                ForeColor = Color.FromArgb(220, 220, 220),
            };
            _cmbLoop.Items.AddRange(new[] { "Once", "Loop", "PingPong" });
            _cmbLoop.SelectedIndex = (int)_params.Loop;

            var lblListVar = new Label
            {
                Text = "Variable:",
                AutoSize = true,
                Location = new Point(360, 9),
                ForeColor = Color.FromArgb(200, 200, 200),
            };

            _cmbListVar = new ComboBox
            {
                Location = new Point(418, 5),
                Width = 90,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(45, 45, 50),
                ForeColor = Color.FromArgb(220, 220, 220),
            };
            _cmbListVar.Items.AddRange(new[] { "sprites", "frame" });
            _cmbListVar.SelectedIndex = _params.ListVarName == "frame" ? 1 : 0;

            bar.Controls.Add(lblTarget);
            bar.Controls.Add(lblLoop);
            bar.Controls.Add(_cmbLoop);
            bar.Controls.Add(lblListVar);
            bar.Controls.Add(_cmbListVar);

            return bar;
        }

        // ── Timeline toolbar ────────────────────────────────────────────────────

        private FlowLayoutPanel BuildTimelineToolbar()
        {
            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 30,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = Color.FromArgb(30, 32, 40),
                Padding = new Padding(4, 2, 4, 2),
            };

            var btnAdd = DarkBtn("+ Add Keyframe", Color.FromArgb(0, 100, 80), 120);
            btnAdd.Click += OnAddKeyframeClick;

            var btnCapture = DarkBtn("📷 Capture", Color.FromArgb(0, 80, 140), 100);
            btnCapture.Click += OnCaptureClick;

            var btnDuplicate = DarkBtn("⧉ Duplicate", Color.FromArgb(60, 60, 70), 100);
            btnDuplicate.Click += OnDuplicateClick;

            var btnRemove = DarkBtn("✕ Remove", Color.FromArgb(140, 40, 40), 85);
            btnRemove.Click += OnRemoveClick;

            var sep1 = new Label { Text = "|", ForeColor = Color.FromArgb(60, 60, 70), AutoSize = true, Padding = new Padding(4, 4, 4, 0) };

            var btnPlay = DarkBtn("▶ Play", Color.FromArgb(20, 100, 40), 65);
            btnPlay.Click += OnPlayClick;

            var btnStop = DarkBtn("⏹ Stop", Color.FromArgb(120, 30, 30), 65);
            btnStop.Click += OnStopClick;

            var sep2 = new Label { Text = "|", ForeColor = Color.FromArgb(60, 60, 70), AutoSize = true, Padding = new Padding(4, 4, 4, 0) };

            var btnZoomFit = DarkBtn("⊞ Fit", Color.FromArgb(60, 60, 90), 55);
            btnZoomFit.Click += (s, e) => _timeline.ZoomToFit();

            bar.Controls.Add(btnAdd);
            bar.Controls.Add(btnCapture);
            bar.Controls.Add(btnDuplicate);
            bar.Controls.Add(btnRemove);
            bar.Controls.Add(sep1);
            bar.Controls.Add(btnPlay);
            bar.Controls.Add(btnStop);
            bar.Controls.Add(sep2);
            bar.Controls.Add(btnZoomFit);

            return bar;
        }

        // ── Bottom toolbar ──────────────────────────────────────────────────────

        private FlowLayoutPanel BuildBottomToolbar()
        {
            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 36,
                FlowDirection = FlowDirection.RightToLeft,
                BackColor = Color.FromArgb(24, 24, 28),
                Padding = new Padding(4, 4, 8, 4),
            };

            var btnClose = DarkBtn("Close", Color.FromArgb(70, 70, 70), 80);
            btnClose.Click += (s, e) => Close();

            var btnUpdate = DarkBtn("✏ Update Code", Color.FromArgb(0, 130, 60), 130);
            btnUpdate.Click += (s, e) =>
            {
                string code = GeneratedCode;
                if (string.IsNullOrEmpty(code))
                {
                    SetStatus("No code to update");
                    return;
                }
                CodeUpdateRequested?.Invoke(code);
                SetStatus("Animation code sent to code panel");
            };

            var btnCopy = DarkBtn("📋 Copy Code", Color.FromArgb(0, 100, 180), 130);
            btnCopy.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(_txtCode.Text))
                {
                    Clipboard.SetText(_txtCode.Text);
                    SetStatus("Keyframe animation code copied to clipboard");
                }
            };

            bar.Controls.Add(btnClose);
            bar.Controls.Add(btnUpdate);
            bar.Controls.Add(btnCopy);

            return bar;
        }

        // ── Event wiring ────────────────────────────────────────────────────────

        private void WireEvents()
        {
            _timeline.PlayheadChanged += OnPlayheadChanged;
            _timeline.KeyframeSelected += OnTimelineKeyframeSelected;
            _timeline.KeyframeMoved += OnKeyframeMoved;
            _timeline.KeyframeAddRequested += OnKeyframeAddRequested;

            _lstKeyframes.SelectedIndexChanged += (s, e) =>
            {
                int idx = _lstKeyframes.SelectedIndex;
                _timeline.SelectedIndex = idx;
                ShowKeyframeProperties(idx);
            };

            _cmbLoop.SelectedIndexChanged += (s, e) =>
            {
                _params.Loop = (LoopMode)_cmbLoop.SelectedIndex;
                RefreshCode();
            };

            _cmbListVar.SelectedIndexChanged += (s, e) =>
            {
                _params.ListVarName = _cmbListVar.SelectedIndex == 0 ? "sprites" : "frame";
                RefreshCode();
            };

            FormClosed += (s, e) =>
            {
                _playTimer.Stop();
                _playTimer.Dispose();
            };
        }

        // ── Keyframe operations ─────────────────────────────────────────────────

        private void OnAddKeyframeClick(object sender, EventArgs e)
        {
            int tick = _timeline.Playhead;
            if (_params.Keyframes.Any(k => k.Tick == tick))
                tick = _params.Keyframes.Max(k => k.Tick) + 30;

            AddKeyframeAtTick(tick);
        }

        private void OnCaptureClick(object sender, EventArgs e)
        {
            int idx = _timeline.SelectedIndex;
            if (idx < 0 || idx >= _params.Keyframes.Count)
            {
                SetStatus("Select a keyframe first");
                return;
            }

            int tick = _params.Keyframes[idx].Tick;
            var easing = _params.Keyframes[idx].EasingToNext;
            var captured = Keyframe.FromSprite(_sprite, tick);
            captured.EasingToNext = easing;
            _params.Keyframes[idx] = captured;
            RefreshAll();
            _timeline.SelectedIndex = idx;
            SelectKeyframeInList(idx);
            SetStatus($"Captured current sprite state at tick {tick}");
        }

        private void OnDuplicateClick(object sender, EventArgs e)
        {
            int idx = _timeline.SelectedIndex;
            if (idx < 0 || idx >= _params.Keyframes.Count) return;

            var src = _params.Keyframes[idx];
            var dup = CloneKeyframe(src);
            dup.Tick = src.Tick + 15;
            _params.Keyframes.Add(dup);

            int newIdx = _params.Keyframes.Count - 1;
            RefreshAll();
            _timeline.SelectedIndex = newIdx;
            SelectKeyframeInList(newIdx);
        }

        private void OnRemoveClick(object sender, EventArgs e)
        {
            int idx = _timeline.SelectedIndex;
            if (idx < 0 || idx >= _params.Keyframes.Count) return;
            if (_params.Keyframes.Count <= 2)
            {
                SetStatus("Need at least 2 keyframes");
                return;
            }

            _params.Keyframes.RemoveAt(idx);
            if (idx >= _params.Keyframes.Count) idx = _params.Keyframes.Count - 1;
            RefreshAll();
            _timeline.SelectedIndex = idx;
            SelectKeyframeInList(idx);
        }

        private void OnKeyframeAddRequested(int tick)
        {
            AddKeyframeAtTick(tick);
        }

        private void AddKeyframeAtTick(int tick)
        {
            // Interpolate state at this tick for the new keyframe
            var kf = InterpolateAtTick(tick);
            kf.Tick = tick;
            _params.Keyframes.Add(kf);

            int newIdx = _params.Keyframes.Count - 1;
            RefreshAll();
            _timeline.SelectedIndex = newIdx;
            SelectKeyframeInList(newIdx);
            _timeline.EnsureTickVisible(tick);
            SetStatus($"Added keyframe at tick {tick}");
        }

        // ── Timeline events ─────────────────────────────────────────────────────

        private void OnPlayheadChanged(int tick)
        {
            UpdatePreviewAtTick(tick);
        }

        private void OnTimelineKeyframeSelected(int index)
        {
            SelectKeyframeInList(index);
            ShowKeyframeProperties(index);

            if (index >= 0 && index < _params.Keyframes.Count)
                _timeline.EnsureTickVisible(_params.Keyframes[index].Tick);
        }

        private void OnKeyframeMoved(int index, int newTick)
        {
            RefreshKeyframeList();
            RefreshCode();
            UpdatePreviewAtTick(_timeline.Playhead);
        }

        // ── Playback ────────────────────────────────────────────────────────────

        private void OnPlayClick(object sender, EventArgs e)
        {
            if (_isPlaying)
            {
                _playTimer.Stop();
                _isPlaying = false;
                SetStatus("Playback paused");
                return;
            }

            _isPlaying = true;
            _playTimer.Start();
            SetStatus("Playing preview…");
        }

        private void OnStopClick(object sender, EventArgs e)
        {
            _playTimer.Stop();
            _isPlaying = false;
            _timeline.Playhead = 0;
            UpdatePreviewAtTick(0);
            SetStatus("Playback stopped");
        }

        private void OnPlayTimerTick(object sender, EventArgs e)
        {
            if (_params.Keyframes.Count < 2) return;

            int maxTick = _params.Keyframes.Max(k => k.Tick);
            if (maxTick <= 0) { _playTimer.Stop(); return; }

            int next = _timeline.Playhead + 1;

            switch (_params.Loop)
            {
                case LoopMode.Loop:
                    next = next % maxTick;
                    break;
                case LoopMode.PingPong:
                    next = next % (maxTick * 2);
                    break;
                case LoopMode.Once:
                    if (next > maxTick)
                    {
                        _playTimer.Stop();
                        _isPlaying = false;
                        SetStatus("Playback finished");
                        return;
                    }
                    break;
            }

            _timeline.Playhead = next;
            UpdatePreviewAtTick(next);
            _timeline.EnsureTickVisible(next);
        }

        // ── Refresh helpers ─────────────────────────────────────────────────────

        private void RefreshAll()
        {
            RefreshKeyframeList();
            RefreshCode();
            _timeline.SetData(_params.Keyframes, _isText);
            UpdatePreviewAtTick(_timeline.Playhead);
        }

        private void RefreshKeyframeList()
        {
            int prevIdx = _lstKeyframes.SelectedIndex;
            _lstKeyframes.BeginUpdate();
            _lstKeyframes.Items.Clear();

            var sorted = _params.Keyframes
                .Select((kf, i) => new { kf, i })
                .OrderBy(x => x.kf.Tick)
                .ToList();

            foreach (var item in sorted)
            {
                string easingLabel = item.kf.EasingToNext != EasingType.Linear
                    ? $" [{item.kf.EasingToNext}]"
                    : "";
                _lstKeyframes.Items.Add($"T={item.kf.Tick,4}  {item.kf.Summary}{easingLabel}");
            }

            _lstKeyframes.EndUpdate();

            if (prevIdx >= 0 && prevIdx < _lstKeyframes.Items.Count)
                _lstKeyframes.SelectedIndex = prevIdx;
        }

        private void RefreshCode()
        {
            string code = AnimationSnippetGenerator.GenerateKeyframed(_sprite, _params);
            _txtCode.Text = code;
        }

        private void SelectKeyframeInList(int index)
        {
            if (index >= 0 && index < _lstKeyframes.Items.Count)
                _lstKeyframes.SelectedIndex = index;
            else if (_lstKeyframes.Items.Count > 0)
                _lstKeyframes.SelectedIndex = 0;
        }

        // ── Property editors ────────────────────────────────────────────────────

        private void ShowKeyframeProperties(int index)
        {
            _pnlProps.Controls.Clear();
            _pnlProps.RowStyles.Clear();
            _pnlProps.RowCount = 0;

            if (index < 0 || index >= _params.Keyframes.Count) return;
            var kf = _params.Keyframes[index];
            int row = 0;

            // Tick
            AddPropInt("Tick:", kf.Tick, 0, 9999, v =>
            {
                kf.Tick = v;
                OnKeyframePropChanged();
            }, ref row);

            // Easing
            AddPropCombo("Easing:", Enum.GetNames(typeof(EasingType)),
                (int)kf.EasingToNext, v =>
                {
                    kf.EasingToNext = (EasingType)v;
                    OnKeyframePropChanged();
                }, ref row);

            // Position
            AddPropFloat("X:", kf.X ?? _sprite.X, v => { kf.X = v; OnKeyframePropChanged(); }, ref row);
            AddPropFloat("Y:", kf.Y ?? _sprite.Y, v => { kf.Y = v; OnKeyframePropChanged(); }, ref row);

            // Size (texture only)
            if (!_isText)
            {
                AddPropFloat("Width:", kf.Width ?? _sprite.Width, v => { kf.Width = v; OnKeyframePropChanged(); }, ref row);
                AddPropFloat("Height:", kf.Height ?? _sprite.Height, v => { kf.Height = v; OnKeyframePropChanged(); }, ref row);
            }

            // Color
            AddPropInt("Red:", kf.ColorR ?? _sprite.ColorR, 0, 255, v => { kf.ColorR = v; OnKeyframePropChanged(); }, ref row);
            AddPropInt("Green:", kf.ColorG ?? _sprite.ColorG, 0, 255, v => { kf.ColorG = v; OnKeyframePropChanged(); }, ref row);
            AddPropInt("Blue:", kf.ColorB ?? _sprite.ColorB, 0, 255, v => { kf.ColorB = v; OnKeyframePropChanged(); }, ref row);
            AddPropInt("Alpha:", kf.ColorA ?? _sprite.ColorA, 0, 255, v => { kf.ColorA = v; OnKeyframePropChanged(); }, ref row);

            // Rotation or Scale
            if (_isText)
                AddPropFloat("Scale:", kf.Scale ?? _sprite.Scale, v => { kf.Scale = v; OnKeyframePropChanged(); }, ref row);
            else
                AddPropFloat("Rotation:", kf.Rotation ?? _sprite.Rotation, v => { kf.Rotation = v; OnKeyframePropChanged(); }, ref row);
        }

        private void OnKeyframePropChanged()
        {
            RefreshKeyframeList();
            RefreshCode();
            _timeline.RefreshDisplay();
            UpdatePreviewAtTick(_timeline.Playhead);
        }

        // ── Interpolation engine ────────────────────────────────────────────────

        private Keyframe InterpolateAtTick(int tick)
        {
            if (_params.Keyframes.Count == 0)
                return Keyframe.FromSprite(_sprite, tick);

            var sorted = _params.Keyframes.OrderBy(k => k.Tick).ToList();

            // Before first keyframe
            if (tick <= sorted[0].Tick)
                return CloneKeyframe(sorted[0]);

            // After last keyframe
            if (tick >= sorted[sorted.Count - 1].Tick)
                return CloneKeyframe(sorted[sorted.Count - 1]);

            // Find segment
            int segIdx = 0;
            for (int i = 1; i < sorted.Count; i++)
            {
                if (tick >= sorted[i].Tick) segIdx = i;
            }

            var a = sorted[segIdx];
            var b = (segIdx + 1 < sorted.Count) ? sorted[segIdx + 1] : a;

            float span = b.Tick - a.Tick;
            float frac = span > 0 ? (tick - a.Tick) / span : 0f;
            float ef = ApplyEasing(frac, a.EasingToNext);

            return new Keyframe
            {
                Tick = tick,
                X = Lerp(a.X ?? _sprite.X, b.X ?? _sprite.X, ef),
                Y = Lerp(a.Y ?? _sprite.Y, b.Y ?? _sprite.Y, ef),
                Width = Lerp(a.Width ?? _sprite.Width, b.Width ?? _sprite.Width, ef),
                Height = Lerp(a.Height ?? _sprite.Height, b.Height ?? _sprite.Height, ef),
                ColorR = LerpInt(a.ColorR ?? _sprite.ColorR, b.ColorR ?? _sprite.ColorR, ef),
                ColorG = LerpInt(a.ColorG ?? _sprite.ColorG, b.ColorG ?? _sprite.ColorG, ef),
                ColorB = LerpInt(a.ColorB ?? _sprite.ColorB, b.ColorB ?? _sprite.ColorB, ef),
                ColorA = LerpInt(a.ColorA ?? _sprite.ColorA, b.ColorA ?? _sprite.ColorA, ef),
                Rotation = Lerp(a.Rotation ?? _sprite.Rotation, b.Rotation ?? _sprite.Rotation, ef),
                Scale = Lerp(a.Scale ?? _sprite.Scale, b.Scale ?? _sprite.Scale, ef),
                EasingToNext = EasingType.Linear,
            };
        }

        private void UpdatePreviewAtTick(int tick)
        {
            int maxTick = _params.Keyframes.Count > 0 ? _params.Keyframes.Max(k => k.Tick) : 1;
            int effectiveTick = tick;

            // Handle loop modes for preview
            if (maxTick > 0)
            {
                switch (_params.Loop)
                {
                    case LoopMode.Loop:
                        effectiveTick = tick % maxTick;
                        break;
                    case LoopMode.PingPong:
                        int raw = tick % (maxTick * 2);
                        effectiveTick = raw < maxTick ? raw : maxTick * 2 - raw;
                        break;
                    case LoopMode.Once:
                        effectiveTick = Math.Min(tick, maxTick);
                        break;
                }
            }

            var interp = InterpolateAtTick(effectiveTick);

            _previewX = interp.X ?? _sprite.X;
            _previewY = interp.Y ?? _sprite.Y;
            _previewW = interp.Width ?? _sprite.Width;
            _previewH = interp.Height ?? _sprite.Height;
            _previewR = interp.ColorR ?? _sprite.ColorR;
            _previewG = interp.ColorG ?? _sprite.ColorG;
            _previewB = interp.ColorB ?? _sprite.ColorB;
            _previewA = interp.ColorA ?? _sprite.ColorA;
            _previewRotation = interp.Rotation ?? _sprite.Rotation;
            _previewScale = interp.Scale ?? _sprite.Scale;

            _lblPreviewInfo.Text = $"T={tick}  Pos({_previewX:F0},{_previewY:F0})  " +
                $"Size({_previewW:F0},{_previewH:F0})  " +
                $"RGBA({_previewR},{_previewG},{_previewB},{_previewA})  " +
                (_isText ? $"Scale={_previewScale:F2}" : $"Rot={_previewRotation:F2}");

            _previewPanel.Invalidate();
        }

        // ── Preview painting ────────────────────────────────────────────────────

        private void OnPreviewPaint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(10, 10, 14));

            // Draw a simple representation of the LCD surface
            float panelW = _previewPanel.Width;
            float panelH = _previewPanel.Height;

            // Scale the 512×512 LCD surface to fit the preview
            float surfaceSize = 512f;
            float scale = Math.Min(panelW / surfaceSize, panelH / surfaceSize) * 0.85f;
            float offsetX = (panelW - surfaceSize * scale) / 2f;
            float offsetY = (panelH - surfaceSize * scale) / 2f;

            // Surface background
            using (var surfaceBrush = new SolidBrush(Color.FromArgb(20, 25, 35)))
            {
                g.FillRectangle(surfaceBrush, offsetX, offsetY,
                    surfaceSize * scale, surfaceSize * scale);
            }
            using (var borderPen = new Pen(Color.FromArgb(50, 55, 65), 1f))
            {
                g.DrawRectangle(borderPen, offsetX, offsetY,
                    surfaceSize * scale, surfaceSize * scale);
            }

            // Draw sprite representation
            float sx = offsetX + _previewX * scale;
            float sy = offsetY + _previewY * scale;
            float sw = _previewW * scale;
            float sh = _previewH * scale;

            Color spriteColor = Color.FromArgb(
                Math.Max(0, Math.Min(255, _previewA)),
                Math.Max(0, Math.Min(255, _previewR)),
                Math.Max(0, Math.Min(255, _previewG)),
                Math.Max(0, Math.Min(255, _previewB)));

            if (_isText)
            {
                // Draw text representation
                string text = _sprite.Text ?? "Text";
                using (var brush = new SolidBrush(spriteColor))
                using (var font = new Font("Segoe UI", Math.Max(6, _previewScale * 12 * scale)))
                {
                    g.DrawString(text, font, brush, sx, sy);
                }
            }
            else
            {
                // Draw texture sprite with actual shape
                var state = g.Save();
                g.TranslateTransform(sx, sy);

                if (Math.Abs(_previewRotation) > 0.001f)
                    g.RotateTransform(_previewRotation * 180f / (float)Math.PI);

                var r = new RectangleF(-sw / 2f, -sh / 2f, sw, sh);
                DrawPreviewSprite(g, r, spriteColor);

                g.Restore(state);
            }

            // Draw keyframe position ghost markers
            var sorted = _params.Keyframes.OrderBy(k => k.Tick).ToList();
            foreach (var kf in sorted)
            {
                float kx = offsetX + (kf.X ?? _sprite.X) * scale;
                float ky = offsetY + (kf.Y ?? _sprite.Y) * scale;

                using (var ghostPen = new Pen(Color.FromArgb(60, 180, 255, 180), 1f))
                {
                    ghostPen.DashStyle = DashStyle.Dot;
                    g.DrawEllipse(ghostPen, kx - 4, ky - 4, 8, 8);
                }
            }

            // Draw motion path
            if (sorted.Count >= 2)
            {
                var pathPoints = new List<PointF>();
                int totalTicks = sorted.Last().Tick;
                int pathStep = Math.Max(1, totalTicks / 100);

                for (int t = 0; t <= totalTicks; t += pathStep)
                {
                    var interp = InterpolateAtTick(t);
                    float px = offsetX + (interp.X ?? _sprite.X) * scale;
                    float py = offsetY + (interp.Y ?? _sprite.Y) * scale;
                    pathPoints.Add(new PointF(px, py));
                }

                if (pathPoints.Count >= 2)
                {
                    using (var pathPen = new Pen(Color.FromArgb(40, 100, 200, 255), 1f))
                    {
                        pathPen.DashStyle = DashStyle.Dash;
                        g.DrawLines(pathPen, pathPoints.ToArray());
                    }
                }
            }
        }

        /// <summary>
        /// Draws the sprite shape in the preview panel, matching the LcdCanvas rendering
        /// approach: real texture if available, otherwise GDI+ shape approximation.
        /// </summary>
        private void DrawPreviewSprite(Graphics g, RectangleF r, Color color)
        {
            // Try real texture first (from SE Content directory)
            Bitmap tex = _textureCache?.GetTexture(_sprite.SpriteName);
            if (tex != null)
            {
                DrawTintedTexture(g, tex, r, color);
                return;
            }

            // GDI+ shape approximation based on sprite name
            using (var brush = new SolidBrush(color))
            {
                string key = _sprite.SpriteName?.ToLowerInvariant() ?? "";
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
                        g.FillRectangle(brush, r);
                        if (r.Width > 18 && r.Height > 12)
                        {
                            int lum = (color.R * 299 + color.G * 587 + color.B * 114) / 1000;
                            var textColor = lum > 128
                                ? Color.FromArgb(200, 0, 0, 0)
                                : Color.FromArgb(200, 255, 255, 255);
                            float fs = Math.Max(7f, Math.Min(r.Width * 0.14f, 12f));
                            using (var lFont = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel))
                            using (var lb = new SolidBrush(textColor))
                            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                                g.DrawString(_sprite.SpriteName ?? "", lFont, lb, r, sf);
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Draws a texture bitmap tinted by the sprite's color using a ColorMatrix.
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

        // ── Property editor helpers ─────────────────────────────────────────────

        private void AddPropFloat(string label, float initial, Action<float> onChange, ref int row)
        {
            var lbl = new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(200, 200, 200),
                Font = new Font("Segoe UI", 8.5f),
            };

            var nud = new NumericUpDown
            {
                Dock = DockStyle.Fill,
                Minimum = -9999,
                Maximum = 9999,
                DecimalPlaces = 1,
                Increment = 1,
                Value = (decimal)Math.Max(-9999, Math.Min(9999, initial)),
                BackColor = Color.FromArgb(40, 42, 48),
                ForeColor = Color.FromArgb(220, 220, 220),
            };
            nud.ValueChanged += (s, e) => onChange((float)nud.Value);

            _pnlProps.RowCount = row + 1;
            _pnlProps.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            _pnlProps.Controls.Add(lbl, 0, row);
            _pnlProps.Controls.Add(nud, 1, row);
            row++;
        }

        private void AddPropInt(string label, int initial, int min, int max,
            Action<int> onChange, ref int row)
        {
            var lbl = new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(200, 200, 200),
                Font = new Font("Segoe UI", 8.5f),
            };

            var nud = new NumericUpDown
            {
                Dock = DockStyle.Fill,
                Minimum = min,
                Maximum = max,
                Value = Math.Max(min, Math.Min(max, initial)),
                BackColor = Color.FromArgb(40, 42, 48),
                ForeColor = Color.FromArgb(220, 220, 220),
            };
            nud.ValueChanged += (s, e) => onChange((int)nud.Value);

            _pnlProps.RowCount = row + 1;
            _pnlProps.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            _pnlProps.Controls.Add(lbl, 0, row);
            _pnlProps.Controls.Add(nud, 1, row);
            row++;
        }

        private void AddPropCombo(string label, string[] items, int selected,
            Action<int> onChange, ref int row)
        {
            var lbl = new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(200, 200, 200),
                Font = new Font("Segoe UI", 8.5f),
            };

            var cmb = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(40, 42, 48),
                ForeColor = Color.FromArgb(220, 220, 220),
            };
            cmb.Items.AddRange(items);
            if (selected >= 0 && selected < items.Length)
                cmb.SelectedIndex = selected;
            cmb.SelectedIndexChanged += (s, e) => onChange(cmb.SelectedIndex);

            _pnlProps.RowCount = row + 1;
            _pnlProps.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            _pnlProps.Controls.Add(lbl, 0, row);
            _pnlProps.Controls.Add(cmb, 1, row);
            row++;
        }

        // ── Math helpers ────────────────────────────────────────────────────────

        private static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        private static int LerpInt(int a, int b, float t)
        {
            return (int)(a + (b - a) * t);
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
                    float b2 = 1f - t;
                    if (b2 < 1f / 2.75f) return 1f - 7.5625f * b2 * b2;
                    if (b2 < 2f / 2.75f) { b2 -= 1.5f / 2.75f; return 1f - (7.5625f * b2 * b2 + 0.75f); }
                    if (b2 < 2.5f / 2.75f) { b2 -= 2.25f / 2.75f; return 1f - (7.5625f * b2 * b2 + 0.9375f); }
                    b2 -= 2.625f / 2.75f; return 1f - (7.5625f * b2 * b2 + 0.984375f);
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

        // ── Utilities ───────────────────────────────────────────────────────────

        private static Keyframe CloneKeyframe(Keyframe src)
        {
            return new Keyframe
            {
                Tick = src.Tick,
                X = src.X, Y = src.Y,
                Width = src.Width, Height = src.Height,
                ColorR = src.ColorR, ColorG = src.ColorG,
                ColorB = src.ColorB, ColorA = src.ColorA,
                Rotation = src.Rotation, Scale = src.Scale,
                EasingToNext = src.EasingToNext,
            };
        }

        private void SetStatus(string text)
        {
            _lblStatus.Text = text;
        }

        private static Button DarkBtn(string text, Color back, int width)
        {
            var btn = new Button
            {
                Text = text,
                BackColor = back,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Height = 24,
                Width = width,
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private static string TargetLabel(TargetScriptType t)
        {
            switch (t)
            {
                case TargetScriptType.ProgrammableBlock: return "PB";
                case TargetScriptType.Mod: return "Mod";
                case TargetScriptType.Plugin: return "Plugin";
                case TargetScriptType.Pulsar: return "Pulsar";
                default: return "LCD Helper";
            }
        }
    }
}
