using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using SESpriteLCDLayoutTool.Controls;
using SESpriteLCDLayoutTool.Models;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool
{
    /// <summary>
    /// NLE-style multi-sprite keyframe timeline dialog.
    /// Shows all animated sprites on parallel tracks so their animations can be
    /// synced visually. Selecting a track loads that sprite's keyframe properties
    /// into the right-hand panel for editing.
    /// </summary>
    public sealed class MultiSpriteTimelineDialog : Form
    {
        // ── Data ────────────────────────────────────────────────────────────────
        private readonly List<SpriteEntry> _allSprites;
        private List<SpriteEntry> _animatedSprites = new List<SpriteEntry>();
        private readonly TargetScriptType _target;

        // ── Controls ────────────────────────────────────────────────────────────
        private MultiSpriteTimeline _timeline;
        private Panel       _propsPanel;
        private Panel       _previewPanel;
        private Label       _lblSpriteHeader;
        private Label       _lblPreviewInfo;
        private CheckBox    _chkShowContext;
        private CheckBox    _chkShowPath;
        private CheckBox    _chkShowGhosts;
        private CheckBox    _chkFocusSelected;
        private ComboBox    _cmbFocusDim;
        private Label       _lblStatus;
        private Timer       _playTimer;
        private Button      _btnPlay;
        private Label       _lblTick;
        private TrackBar    _speedBar;
        private Label       _lblSpeed;
        private bool        _isPlaying;
        private int         _selectedSpriteIdx = -1;

        // ── Callbacks to main form ───────────────────────────────────────────────

        /// <summary>
        /// Called each tick during playback with the current tick number so the
        /// main form can drive canvas preview (optional — wire up if desired).
        /// </summary>
        public event Action<int> PlayheadTick;

        /// <summary>
        /// Called when "Update All Code" is clicked. Args: list of sprites whose
        /// animations were edited, so the caller can merge code for each.
        /// </summary>
        public event Action<IReadOnlyList<SpriteEntry>> UpdateCodeRequested;

        // ── Constructor ─────────────────────────────────────────────────────────

        public MultiSpriteTimelineDialog(List<SpriteEntry> allSprites, TargetScriptType target)
        {
            _allSprites = allSprites ?? throw new ArgumentNullException(nameof(allSprites));
            _target     = target;
            RebuildAnimatedList();

            InitializeComponent();
            WireEvents();
            RefreshTimeline();
        }

        private void RebuildAnimatedList()
        {
            _animatedSprites = _allSprites
                .Where(s => s.KeyframeAnimation != null)
                .ToList();
        }

        // ── UI Construction ─────────────────────────────────────────────────────

        private void InitializeComponent()
        {
            Text            = "🎬 Multi-Sprite Keyframe Timeline";
            Size            = new Size(1100, 700);
            MinimumSize     = new Size(800, 500);
            StartPosition   = FormStartPosition.CenterParent;
            BackColor       = Color.FromArgb(24, 24, 28);
            ForeColor       = Color.FromArgb(220, 220, 220);
            Font            = new Font("Segoe UI", 9f);
            KeyPreview      = true;

            // ── Status bar ──
            _lblStatus = new Label
            {
                Dock      = DockStyle.Bottom,
                Height    = 22,
                BackColor = Color.FromArgb(20, 20, 24),
                ForeColor = Color.FromArgb(140, 160, 180),
                Padding   = new Padding(8, 3, 0, 0),
                Font      = new Font("Segoe UI", 8f),
                Text      = "Double-click empty track to add keyframe  |  Drag diamonds to move  |  Scroll to zoom  |  Middle-click to pan",
            };

            // ── Bottom toolbar (play controls) ──
            var bottomBar = BuildBottomBar();

            // ── Timeline ──
            _timeline = new MultiSpriteTimeline
            {
                Dock = DockStyle.Fill,
            };

            // Timeline in a scrollable container so many sprites don't clip
            var timelineScroll = new Panel
            {
                Dock       = DockStyle.Fill,
                AutoScroll = false,
            };
            timelineScroll.Controls.Add(_timeline);

            // ── Timeline toolbar ──
            var timelineBar = BuildTimelineBar();

            var timelineContainer = new Panel { Dock = DockStyle.Fill };
            timelineContainer.Controls.Add(timelineScroll);
            timelineContainer.Controls.Add(timelineBar);

            // ── Preview + properties (right side) ──
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
                Text = "Preview idle",
            };

            var previewHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 20,
                BackColor = Color.FromArgb(28, 30, 38),
            };

            var lblPreviewTitle = new Label
            {
                Dock = DockStyle.Left,
                Width = 70,
                Text = "   Preview",
                ForeColor = Color.FromArgb(130, 160, 200),
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
            };

            _chkShowContext = new CheckBox
            {
                Dock = DockStyle.Right,
                Width = 72,
                Text = "Context",
                Checked = true,
                ForeColor = Color.FromArgb(185, 200, 215),
                Font = new Font("Segoe UI", 7.5f),
                FlatStyle = FlatStyle.Flat,
            };
            _chkShowContext.FlatAppearance.BorderSize = 0;

            _chkShowPath = new CheckBox
            {
                Dock = DockStyle.Right,
                Width = 54,
                Text = "Path",
                Checked = true,
                ForeColor = Color.FromArgb(185, 200, 215),
                Font = new Font("Segoe UI", 7.5f),
                FlatStyle = FlatStyle.Flat,
            };
            _chkShowPath.FlatAppearance.BorderSize = 0;

            _chkShowGhosts = new CheckBox
            {
                Dock = DockStyle.Right,
                Width = 62,
                Text = "Ghosts",
                Checked = true,
                ForeColor = Color.FromArgb(185, 200, 215),
                Font = new Font("Segoe UI", 7.5f),
                FlatStyle = FlatStyle.Flat,
            };
            _chkShowGhosts.FlatAppearance.BorderSize = 0;

            _chkFocusSelected = new CheckBox
            {
                Dock = DockStyle.Right,
                Width = 56,
                Text = "Focus",
                Checked = true,
                ForeColor = Color.FromArgb(185, 200, 215),
                Font = new Font("Segoe UI", 7.5f),
                FlatStyle = FlatStyle.Flat,
            };
            _chkFocusSelected.FlatAppearance.BorderSize = 0;

            _cmbFocusDim = new ComboBox
            {
                Dock = DockStyle.Right,
                Width = 58,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(40, 42, 48),
                ForeColor = Color.FromArgb(220, 220, 220),
                Font = new Font("Segoe UI", 7.5f),
            };
            _cmbFocusDim.Items.AddRange(new object[] { "20%", "35%", "50%" });
            _cmbFocusDim.SelectedIndex = 1;

            _chkShowContext.CheckedChanged += (s, e) => InvalidatePreview();
            _chkShowPath.CheckedChanged += (s, e) => InvalidatePreview();
            _chkShowGhosts.CheckedChanged += (s, e) => InvalidatePreview();
            _chkFocusSelected.CheckedChanged += (s, e) => InvalidatePreview();
            _cmbFocusDim.SelectedIndexChanged += (s, e) => InvalidatePreview();

            previewHeader.Controls.Add(_chkShowContext);
            previewHeader.Controls.Add(_chkShowPath);
            previewHeader.Controls.Add(_chkShowGhosts);
            previewHeader.Controls.Add(_cmbFocusDim);
            previewHeader.Controls.Add(_chkFocusSelected);
            previewHeader.Controls.Add(lblPreviewTitle);

            var previewContainer = new Panel { Dock = DockStyle.Fill };
            previewContainer.Controls.Add(_previewPanel);
            previewContainer.Controls.Add(_lblPreviewInfo);
            previewContainer.Controls.Add(previewHeader);

            _lblSpriteHeader = new Label
            {
                Dock      = DockStyle.Top,
                Height    = 24,
                Text      = "   Select a sprite track to edit keyframes",
                BackColor = Color.FromArgb(28, 30, 38),
                ForeColor = Color.FromArgb(130, 160, 200),
                Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Padding   = new Padding(4, 4, 0, 0),
            };

            _propsPanel = new Panel
            {
                Dock       = DockStyle.Fill,
                AutoScroll = true,
                BackColor  = Color.FromArgb(26, 28, 34),
                Padding    = new Padding(6),
            };

            var rightSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                BackColor = Color.FromArgb(24, 24, 28),
                SplitterWidth = 3,
            };
            rightSplit.Panel1.Controls.Add(previewContainer);
            rightSplit.Panel2.Controls.Add(_propsPanel);
            rightSplit.Panel2.Controls.Add(_lblSpriteHeader);

            // ── Split: left timeline | right preview+props ──
            var split = new SplitContainer
            {
                Dock         = DockStyle.Fill,
                Orientation  = Orientation.Vertical,
                SplitterWidth = 4,
                BackColor    = Color.FromArgb(24, 24, 28),
            };
            split.Panel1.Controls.Add(timelineContainer);
            split.Panel2.Controls.Add(rightSplit);

            Controls.Add(split);
            Controls.Add(bottomBar);
            Controls.Add(_lblStatus);

            Load += (s, e) =>
            {
                try
                {
                    split.SplitterDistance = Math.Max(300, (int)(Width * 0.70));
                    rightSplit.SplitterDistance = Math.Max(180, (int)(rightSplit.Height * 0.62));
                }
                catch { }
            };

            _playTimer = new Timer { Interval = 33 };
            _playTimer.Tick += OnPlayTimerTick;
        }

        private FlowLayoutPanel BuildTimelineBar()
        {
            var bar = new FlowLayoutPanel
            {
                Dock           = DockStyle.Top,
                Height         = 30,
                FlowDirection  = FlowDirection.LeftToRight,
                BackColor      = Color.FromArgb(30, 32, 40),
                Padding        = new Padding(4, 2, 4, 2),
            };

            var btnAdd = DarkBtn("+ Add Keyframe", Color.FromArgb(0, 100, 80), 120);
            btnAdd.Click += OnAddKeyframeClick;

            var btnRemove = DarkBtn("✕ Remove", Color.FromArgb(140, 40, 40), 85);
            btnRemove.Click += OnRemoveKeyframeClick;

            var sep = new Label { Text = "|", ForeColor = Color.FromArgb(60, 60, 70), AutoSize = true, Padding = new Padding(4, 4, 4, 0) };

            var btnFit = DarkBtn("⊞ Fit", Color.FromArgb(60, 60, 90), 55);
            btnFit.Click += (s, e) => _timeline.ZoomToFit();

            var btnRefresh = DarkBtn("↻ Refresh", Color.FromArgb(50, 70, 50), 80);
            btnRefresh.Click += (s, e) => { RebuildAnimatedList(); RefreshTimeline(); SetStatus("Refreshed — showing all animated sprites"); };

            bar.Controls.Add(btnAdd);
            bar.Controls.Add(btnRemove);
            bar.Controls.Add(sep);
            bar.Controls.Add(btnFit);
            bar.Controls.Add(btnRefresh);
            return bar;
        }

        private Panel BuildBottomBar()
        {
            var bar = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = 40,
                BackColor = Color.FromArgb(28, 30, 38),
                Padding   = new Padding(6, 4, 6, 4),
            };

            _btnPlay = DarkBtn("▶ Play", Color.FromArgb(20, 100, 40), 75);
            _btnPlay.Location = new Point(6, 7);
            _btnPlay.Click   += OnPlayClick;

            var btnStop = DarkBtn("⏹ Stop", Color.FromArgb(120, 30, 30), 65);
            btnStop.Location = new Point(88, 7);
            btnStop.Click   += OnStopClick;

            _lblTick = new Label
            {
                Text      = "Tick: 0",
                Location  = new Point(162, 10),
                AutoSize  = true,
                ForeColor = Color.FromArgb(180, 200, 220),
                Font      = new Font("Consolas", 9f),
            };

            var lblSpeedLabel = new Label
            {
                Text      = "Speed:",
                Location  = new Point(260, 10),
                AutoSize  = true,
                ForeColor = Color.FromArgb(170, 170, 180),
            };

            _speedBar = new TrackBar
            {
                Minimum   = 1,
                Maximum   = 8,
                Value     = 4,
                TickStyle = TickStyle.None,
                Width     = 90,
                Height    = 28,
                Location  = new Point(308, 6),
                BackColor = Color.FromArgb(28, 30, 38),
            };
            _speedBar.ValueChanged += (s, e) => UpdateSpeedLabel();

            _lblSpeed = new Label
            {
                Text      = "1×",
                Location  = new Point(404, 10),
                AutoSize  = true,
                ForeColor = Color.FromArgb(160, 200, 160),
                Font      = new Font("Consolas", 9f),
            };

            var btnUpdateAll = DarkBtn("✏ Update All Code", Color.FromArgb(0, 120, 60), 145);
            btnUpdateAll.Anchor   = AnchorStyles.Right | AnchorStyles.Top;
            btnUpdateAll.Location = new Point(bar.Width - 155, 7);
            btnUpdateAll.Click   += OnUpdateAllCodeClick;

            var btnClose = DarkBtn("Close", Color.FromArgb(70, 70, 70), 70);
            btnClose.Anchor   = AnchorStyles.Right | AnchorStyles.Top;
            btnClose.Location = new Point(bar.Width - 80, 7);
            btnClose.Click   += (s, e) => Close();

            bar.Controls.Add(_btnPlay);
            bar.Controls.Add(btnStop);
            bar.Controls.Add(_lblTick);
            bar.Controls.Add(lblSpeedLabel);
            bar.Controls.Add(_speedBar);
            bar.Controls.Add(_lblSpeed);
            bar.Controls.Add(btnUpdateAll);
            bar.Controls.Add(btnClose);

            bar.Resize += (s, e) =>
            {
                btnUpdateAll.Left = bar.Width - 230;
                btnClose.Left     = bar.Width - 80;
            };

            UpdateSpeedLabel();
            return bar;
        }

        // ── Event wiring ────────────────────────────────────────────────────────

        private void WireEvents()
        {
            _timeline.SpriteSelected      += OnTimelineSpriteSelected;
            _timeline.KeyframeSelected    += OnTimelineKeyframeSelected;
            _timeline.KeyframeMoved       += OnTimelineKeyframeMoved;
            _timeline.KeyframeAddRequested += OnTimelineKeyframeAddRequested;
            _timeline.PlayheadChanged     += OnPlayheadChanged;

            FormClosed += (s, e) => { _playTimer.Stop(); _playTimer.Dispose(); };
        }

        // ── Timeline population ─────────────────────────────────────────────────

        private void RefreshTimeline()
        {
            _timeline.SetSprites(_animatedSprites);
            _timeline.ZoomToFit();
            InvalidatePreview();

            if (_animatedSprites.Count == 0)
            {
                _lblSpriteHeader.Text = "   No animated sprites — right-click a sprite → Edit Animation… to create one";
                ClearPropsPanel();
                SetStatus("No animated sprites found");
            }
            else
            {
                _lblSpriteHeader.Text = $"   {_animatedSprites.Count} animated sprite{(_animatedSprites.Count == 1 ? "" : "s")}";
                SetStatus($"{_animatedSprites.Count} animated sprites loaded");
            }
        }

        // ── Timeline event handlers ─────────────────────────────────────────────

        private void OnTimelineSpriteSelected(int idx)
        {
            if (idx < 0 || idx >= _animatedSprites.Count) return;
            _selectedSpriteIdx = idx;
            var sprite = _animatedSprites[idx];
            _lblSpriteHeader.Text = $"   {sprite.DisplayName}  —  {sprite.KeyframeAnimation?.Keyframes?.Count ?? 0} keyframes";
            ShowSpriteKeyframeList(idx);
            InvalidatePreview();
        }

        private void OnTimelineKeyframeSelected(int spriteIdx, int kfIdx)
        {
            if (spriteIdx < 0 || spriteIdx >= _animatedSprites.Count) return;
            ShowKeyframeProperties(spriteIdx, kfIdx);
            InvalidatePreview();
        }

        private void OnTimelineKeyframeMoved(int spriteIdx, int kfIdx, int newTick)
        {
            InvalidatePreview();
            SetStatus($"Keyframe moved to tick {newTick}");
        }

        private void OnTimelineKeyframeAddRequested(int spriteIdx, int tick)
        {
            if (spriteIdx < 0 || spriteIdx >= _animatedSprites.Count) return;
            var kfs = _animatedSprites[spriteIdx].KeyframeAnimation?.Keyframes;
            if (kfs == null) return;

            // Avoid duplicate tick
            if (kfs.Any(k => k.Tick == tick))
                tick = (kfs.Count > 0 ? kfs.Max(k => k.Tick) : 0) + 15;

            var newKf = new Keyframe { Tick = tick };
            // Copy last keyframe values as starting point
            var last = kfs.OrderByDescending(k => k.Tick).FirstOrDefault();
            if (last != null)
            {
                newKf.X        = last.X;
                newKf.Y        = last.Y;
                newKf.Width    = last.Width;
                newKf.Height   = last.Height;
                newKf.ColorR   = last.ColorR;
                newKf.ColorG   = last.ColorG;
                newKf.ColorB   = last.ColorB;
                newKf.ColorA   = last.ColorA;
                newKf.Rotation = last.Rotation;
                newKf.Scale    = last.Scale;
            }

            kfs.Add(newKf);
            _timeline.RefreshDisplay();
            InvalidatePreview();
            SetStatus($"Added keyframe at tick {tick} for '{_animatedSprites[spriteIdx].DisplayName}'");
            ShowSpriteKeyframeList(spriteIdx);
        }

        private void OnPlayheadChanged(int tick)
        {
            _lblTick.Text = $"Tick: {tick}";
            InvalidatePreview();
            PlayheadTick?.Invoke(tick);
        }

        // ── Add / Remove keyframe buttons ───────────────────────────────────────

        private void OnAddKeyframeClick(object sender, EventArgs e)
        {
            int si = _timeline.SelectedSpriteIndex;
            if (si < 0 || si >= _animatedSprites.Count)
            {
                SetStatus("Select a sprite track first");
                return;
            }
            int tick = _timeline.Playhead;
            OnTimelineKeyframeAddRequested(si, tick);
        }

        private void OnRemoveKeyframeClick(object sender, EventArgs e)
        {
            int si  = _timeline.SelectedSpriteIndex;
            int ki  = _timeline.SelectedKeyframeIndex;
            if (si < 0 || ki < 0) { SetStatus("Select a keyframe first"); return; }

            var kfs = _animatedSprites[si].KeyframeAnimation?.Keyframes;
            if (kfs == null || kfs.Count <= 2) { SetStatus("Need at least 2 keyframes"); return; }

            kfs.RemoveAt(ki);
            _timeline.SelectSprite(si, -1);
            _timeline.RefreshDisplay();
            InvalidatePreview();
            ShowSpriteKeyframeList(si);
            SetStatus("Keyframe removed");
        }

        // ── Playback ────────────────────────────────────────────────────────────

        private void OnPlayClick(object sender, EventArgs e)
        {
            if (_isPlaying)
            {
                _playTimer.Stop();
                _isPlaying        = false;
                _btnPlay.Text     = "▶ Play";
                _btnPlay.BackColor = Color.FromArgb(20, 100, 40);
                SetStatus("Paused");
            }
            else
            {
                _isPlaying         = true;
                _btnPlay.Text      = "⏸ Pause";
                _btnPlay.BackColor = Color.FromArgb(120, 80, 0);
                UpdateTimerInterval();
                _playTimer.Start();
                SetStatus("Playing…");
            }
        }

        private void OnStopClick(object sender, EventArgs e)
        {
            _playTimer.Stop();
            _isPlaying         = false;
            _btnPlay.Text      = "▶ Play";
            _btnPlay.BackColor = Color.FromArgb(20, 100, 40);
            _timeline.Playhead = 0;
            _lblTick.Text      = "Tick: 0";
            InvalidatePreview();
            PlayheadTick?.Invoke(0);
            SetStatus("Stopped");
        }

        private void OnPlayTimerTick(object sender, EventArgs e)
        {
            int maxTick = 0;
            foreach (var sp in _animatedSprites)
            {
                if (sp.KeyframeAnimation?.Keyframes?.Count > 0)
                    maxTick = Math.Max(maxTick, sp.KeyframeAnimation.Keyframes.Max(k => k.Tick));
            }
            if (maxTick <= 0) { _playTimer.Stop(); return; }

            int next = (_timeline.Playhead + 1) % maxTick;
            _timeline.Playhead = next;
            _lblTick.Text      = $"Tick: {next}";
            InvalidatePreview();
            PlayheadTick?.Invoke(next);
        }

        private void UpdateTimerInterval()
        {
            // Speed 1 (slowest) = 100ms/tick, Speed 4 (default) = 33ms, Speed 8 (fastest) = ~8ms
            float[] intervals = { 100f, 66f, 50f, 33f, 25f, 16f, 12f, 8f };
            int     idx       = Math.Max(0, Math.Min(7, _speedBar.Value - 1));
            _playTimer.Interval = (int)intervals[idx];
        }

        private void UpdateSpeedLabel()
        {
            float[] labels = { 0.33f, 0.5f, 0.75f, 1f, 1.5f, 2f, 3f, 4f };
            int idx = Math.Max(0, Math.Min(7, _speedBar.Value - 1));
            _lblSpeed.Text = $"{labels[idx]}×";
            if (_isPlaying) UpdateTimerInterval();
        }

        // ── Update All Code ─────────────────────────────────────────────────────

        private void OnUpdateAllCodeClick(object sender, EventArgs e)
        {
            if (_animatedSprites.Count == 0) { SetStatus("No animated sprites to update"); return; }
            UpdateCodeRequested?.Invoke(_animatedSprites.AsReadOnly());
            SetStatus($"Sent {_animatedSprites.Count} animation(s) to code panel");
        }

        // ── Properties panel ────────────────────────────────────────────────────

        private void ClearPropsPanel()
        {
            _propsPanel.Controls.Clear();
        }

        private void ShowSpriteKeyframeList(int spriteIdx)
        {
            ClearPropsPanel();
            if (spriteIdx < 0 || spriteIdx >= _animatedSprites.Count) return;

            var sprite = _animatedSprites[spriteIdx];
            var kfs    = sprite.KeyframeAnimation?.Keyframes;
            if (kfs == null) return;

            var sorted = kfs.OrderBy(k => k.Tick).ToList();

            var lbl = new Label
            {
                Text      = $"Keyframes ({sorted.Count})",
                Dock      = DockStyle.Top,
                Height    = 20,
                ForeColor = Color.FromArgb(160, 190, 220),
                Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Padding   = new Padding(2, 2, 0, 0),
            };
            _propsPanel.Controls.Add(lbl);

            var lst = new ListBox
            {
                Dock      = DockStyle.Top,
                Height    = Math.Min(160, sorted.Count * 18 + 6),
                BackColor = Color.FromArgb(30, 32, 38),
                ForeColor = Color.FromArgb(220, 220, 220),
                Font      = new Font("Consolas", 8.5f),
                BorderStyle = BorderStyle.None,
            };
            foreach (var kf in sorted)
                lst.Items.Add($"T={kf.Tick,4}  {kf.Summary}");

            lst.SelectedIndexChanged += (s, e) =>
            {
                int li = lst.SelectedIndex;
                if (li >= 0 && li < sorted.Count)
                {
                    int kfIdx = kfs.IndexOf(sorted[li]);
                    _timeline.SelectSprite(spriteIdx, kfIdx);
                    ShowKeyframeProperties(spriteIdx, kfIdx);
                    InvalidatePreview();
                }
            };

            _propsPanel.Controls.Add(lst);
            lbl.BringToFront();
        }

        private void ShowKeyframeProperties(int spriteIdx, int kfIdx)
        {
            if (spriteIdx < 0 || spriteIdx >= _animatedSprites.Count) return;
            var sprite = _animatedSprites[spriteIdx];
            var kfs    = sprite.KeyframeAnimation?.Keyframes;
            if (kfs == null || kfIdx < 0 || kfIdx >= kfs.Count) return;

            // Remove existing prop table if present, keep the list at top
            var existing = _propsPanel.Controls.OfType<TableLayoutPanel>().FirstOrDefault();
            if (existing != null) _propsPanel.Controls.Remove(existing);

            var kf   = kfs[kfIdx];
            bool txt = sprite.Type == SpriteEntryType.Text;
            int  row = 0;

            var tbl = new TableLayoutPanel
            {
                Dock        = DockStyle.Top,
                AutoSize    = true,
                ColumnCount = 2,
                BackColor   = Color.FromArgb(26, 28, 34),
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            void AddFloat(string label, float val, decimal min, decimal max, int dps, decimal inc, Action<float> set)
            {
                var l = new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(190, 190, 200), Font = new Font("Segoe UI", 8f) };
                decimal safe = (decimal)Math.Max((float)min, Math.Min((float)max, val));
                var n = new NumericUpDown { Dock = DockStyle.Fill, Minimum = min, Maximum = max, DecimalPlaces = dps, Increment = inc, Value = safe, BackColor = Color.FromArgb(40, 42, 48), ForeColor = Color.FromArgb(220, 220, 220) };
                n.ValueChanged += (s, e) => { set((float)n.Value); _timeline.RefreshDisplay(); InvalidatePreview(); };
                tbl.RowCount = row + 1;
                tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
                tbl.Controls.Add(l, 0, row);
                tbl.Controls.Add(n, 1, row);
                row++;
            }

            void AddInt(string label, int val, int min, int max, Action<int> set)
            {
                var l = new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(190, 190, 200), Font = new Font("Segoe UI", 8f) };
                var n = new NumericUpDown { Dock = DockStyle.Fill, Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, val)), BackColor = Color.FromArgb(40, 42, 48), ForeColor = Color.FromArgb(220, 220, 220) };
                n.ValueChanged += (s, e) => { set((int)n.Value); _timeline.RefreshDisplay(); InvalidatePreview(); };
                tbl.RowCount = row + 1;
                tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
                tbl.Controls.Add(l, 0, row);
                tbl.Controls.Add(n, 1, row);
                row++;
            }

            void AddCombo(string label, string[] items, int sel, Action<int> set)
            {
                var l = new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(190, 190, 200), Font = new Font("Segoe UI", 8f) };
                var c = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(40, 42, 48), ForeColor = Color.FromArgb(220, 220, 220) };
                c.Items.AddRange(items);
                if (sel >= 0 && sel < items.Length) c.SelectedIndex = sel;
                c.SelectedIndexChanged += (s, e) => { set(c.SelectedIndex); _timeline.RefreshDisplay(); InvalidatePreview(); };
                tbl.RowCount = row + 1;
                tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
                tbl.Controls.Add(l, 0, row);
                tbl.Controls.Add(c, 1, row);
                row++;
            }

            AddInt("Tick:",  kf.Tick, 0, 9999, v => { kf.Tick = v; });
            AddCombo("Easing:", Enum.GetNames(typeof(EasingType)), (int)kf.EasingToNext, v => kf.EasingToNext = (EasingType)v);
            AddFloat("X:", kf.X ?? sprite.X, -9999, 9999, 1, 1m, v => kf.X = v);
            AddFloat("Y:", kf.Y ?? sprite.Y, -9999, 9999, 1, 1m, v => kf.Y = v);
            if (!txt)
            {
                AddFloat("Width:",  kf.Width  ?? sprite.Width,  0, 9999, 1, 1m, v => kf.Width  = v);
                AddFloat("Height:", kf.Height ?? sprite.Height, 0, 9999, 1, 1m, v => kf.Height = v);
            }
            AddInt("Red:",   kf.ColorR ?? sprite.ColorR, 0, 255, v => kf.ColorR = v);
            AddInt("Green:", kf.ColorG ?? sprite.ColorG, 0, 255, v => kf.ColorG = v);
            AddInt("Blue:",  kf.ColorB ?? sprite.ColorB, 0, 255, v => kf.ColorB = v);
            AddInt("Alpha:", kf.ColorA ?? sprite.ColorA, 0, 255, v => kf.ColorA = v);
            if (txt)
                AddFloat("Scale:", kf.Scale ?? sprite.Scale, -100, 100, 2, 0.01m, v => kf.Scale = v);
            else
                AddFloat("Rotation:", kf.Rotation ?? sprite.Rotation, -100, 100, 4, 0.0001m, v => kf.Rotation = v);

            _propsPanel.Controls.Add(tbl);
            tbl.BringToFront();
        }

        // ── Embedded preview ───────────────────────────────────────────────────

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

        private void InvalidatePreview()
        {
            if (_previewPanel == null) return;
            UpdatePreviewInfo();
            _previewPanel.Invalidate();
        }

        private void UpdatePreviewInfo()
        {
            if (_lblPreviewInfo == null) return;

            if (_animatedSprites.Count == 0)
            {
                _lblPreviewInfo.Text = "No animated sprites to preview";
                return;
            }

            int tick = _timeline?.Playhead ?? 0;
            int previewCount = (_chkShowContext != null && _chkShowContext.Checked) ? _allSprites.Count : _animatedSprites.Count;
            if (_selectedSpriteIdx >= 0 && _selectedSpriteIdx < _animatedSprites.Count)
            {
                var sprite = _animatedSprites[_selectedSpriteIdx];
                var state = InterpolateSpriteAtTick(sprite, tick);
                float x = state.X ?? sprite.X;
                float y = state.Y ?? sprite.Y;
                float w = state.Width ?? sprite.Width;
                float h = state.Height ?? sprite.Height;
                _lblPreviewInfo.Text = $"T={tick}  Selected={sprite.DisplayName}  Pos({x:F0},{y:F0})  Size({w:F0},{h:F0})  View={previewCount}";
                return;
            }

            _lblPreviewInfo.Text = $"T={tick}  Previewing {previewCount} sprite(s)";
        }

        private void OnPreviewPaint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(10, 10, 14));

            float panelW = _previewPanel.Width;
            float panelH = _previewPanel.Height;
            if (panelW <= 0 || panelH <= 0) return;

            float surfaceW = 512f;
            float surfaceH = 512f;
            float scale = Math.Min(panelW / surfaceW, panelH / surfaceH) * 0.9f;
            float offsetX = (panelW - surfaceW * scale) / 2f;
            float offsetY = (panelH - surfaceH * scale) / 2f;

            using (var surfaceBrush = new SolidBrush(Color.FromArgb(20, 25, 35)))
                g.FillRectangle(surfaceBrush, offsetX, offsetY, surfaceW * scale, surfaceH * scale);
            using (var borderPen = new Pen(Color.FromArgb(50, 55, 65), 1f))
                g.DrawRectangle(borderPen, offsetX, offsetY, surfaceW * scale, surfaceH * scale);

            if (_allSprites.Count == 0) return;

            int tick = _timeline?.Playhead ?? 0;
            var selectedAnimated = (_selectedSpriteIdx >= 0 && _selectedSpriteIdx < _animatedSprites.Count)
                ? _animatedSprites[_selectedSpriteIdx]
                : null;

            var previewSprites = (_chkShowContext != null && _chkShowContext.Checked)
                ? _allSprites
                : _animatedSprites;

            for (int i = 0; i < previewSprites.Count; i++)
            {
                var sprite = previewSprites[i];
                bool isSelected = selectedAnimated != null && ReferenceEquals(sprite, selectedAnimated);
                bool dimOthers = selectedAnimated != null && _chkFocusSelected != null && _chkFocusSelected.Checked;
                var state = sprite.KeyframeAnimation != null
                    ? InterpolateSpriteAtTick(sprite, tick)
                    : Keyframe.FromSprite(sprite, tick);

                float x = state.X ?? sprite.X;
                float y = state.Y ?? sprite.Y;
                float w = state.Width ?? sprite.Width;
                float h = state.Height ?? sprite.Height;
                int r = state.ColorR ?? sprite.ColorR;
                int gr = state.ColorG ?? sprite.ColorG;
                int b = state.ColorB ?? sprite.ColorB;
                int a = state.ColorA ?? sprite.ColorA;
                float rotation = state.Rotation ?? sprite.Rotation;
                float textScale = state.Scale ?? sprite.Scale;

                Color spriteColor = Color.FromArgb(
                    Math.Max(0, Math.Min(255, a)),
                    Math.Max(0, Math.Min(255, r)),
                    Math.Max(0, Math.Min(255, gr)),
                    Math.Max(0, Math.Min(255, b)));

                if (dimOthers && !isSelected)
                {
                    int dimPercent = GetFocusDimPercent();
                    float keep = (100f - dimPercent) / 100f;
                    int fadedA = Math.Max(20, (int)(spriteColor.A * keep));
                    spriteColor = Color.FromArgb(fadedA, spriteColor.R, spriteColor.G, spriteColor.B);
                }

                float sx = offsetX + x * scale;
                float sy = offsetY + y * scale;
                float sw = w * scale;
                float sh = h * scale;

                if (sprite.Type == SpriteEntryType.Text)
                {
                    using (var brush = new SolidBrush(spriteColor))
                    using (var font = new Font("Segoe UI", Math.Max(6, textScale * 12 * scale)))
                    {
                        g.DrawString(sprite.Text ?? "Text", font, brush, sx, sy);
                    }
                }
                else
                {
                    var saved = g.Save();
                    g.TranslateTransform(sx, sy);
                    if (Math.Abs(rotation) > 0.001f)
                        g.RotateTransform(rotation * 180f / (float)Math.PI);

                    var rect = new RectangleF(-sw / 2f, -sh / 2f, sw, sh);
                    DrawPreviewSprite(g, rect, sprite.SpriteName, spriteColor);

                    if (isSelected)
                    {
                        using (var pen = new Pen(Color.FromArgb(220, 255, 230, 120), 2f))
                            g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                    }

                    g.Restore(saved);
                }
            }

            if (selectedAnimated?.KeyframeAnimation?.Keyframes != null && selectedAnimated.KeyframeAnimation.Keyframes.Count > 0)
            {
                DrawSelectedMotionOverlay(g, selectedAnimated, offsetX, offsetY, scale);
            }
        }

        private void DrawSelectedMotionOverlay(Graphics g, SpriteEntry sprite, float offsetX, float offsetY, float scale)
        {
            var keyframes = sprite.KeyframeAnimation?.Keyframes;
            if (keyframes == null || keyframes.Count == 0) return;

            bool drawGhosts = _chkShowGhosts != null && _chkShowGhosts.Checked;
            bool drawPath = _chkShowPath != null && _chkShowPath.Checked;

            var sorted = keyframes.OrderBy(k => k.Tick).ToList();

            if (drawGhosts)
            {
                using (var ghostPen = new Pen(Color.FromArgb(140, 130, 200, 255), 1.2f))
                {
                    ghostPen.DashStyle = DashStyle.Dot;
                    foreach (var kf in sorted)
                    {
                        float gx = offsetX + (kf.X ?? sprite.X) * scale;
                        float gy = offsetY + (kf.Y ?? sprite.Y) * scale;
                        g.DrawEllipse(ghostPen, gx - 4, gy - 4, 8, 8);
                    }
                }
            }

            if (drawPath && sorted.Count >= 2)
            {
                int maxTick = sorted[sorted.Count - 1].Tick;
                if (maxTick > 0)
                {
                    var points = new List<PointF>();
                    int step = Math.Max(1, maxTick / 120);
                    for (int t = 0; t <= maxTick; t += step)
                    {
                        var interp = InterpolateSpriteAtTick(sprite, t);
                        points.Add(new PointF(
                            offsetX + (interp.X ?? sprite.X) * scale,
                            offsetY + (interp.Y ?? sprite.Y) * scale));
                    }

                    if (points.Count >= 2)
                    {
                        using (var pathPen = new Pen(Color.FromArgb(120, 100, 200, 255), 1.2f))
                        {
                            pathPen.DashStyle = DashStyle.Dash;
                            g.DrawLines(pathPen, points.ToArray());
                        }
                    }
                }
            }
        }

        private static void DrawPreviewSprite(Graphics g, RectangleF r, string spriteName, Color color)
        {
            using (var brush = new SolidBrush(color))
            {
                string key = (spriteName ?? string.Empty).ToLowerInvariant();
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
                            new PointF(0f, r.Top),
                            new PointF(r.Right, r.Bottom),
                            new PointF(r.Left, r.Bottom),
                        });
                        break;

                    case "righttriangle":
                        g.FillPolygon(brush, new[]
                        {
                            new PointF(r.Left, r.Top),
                            new PointF(r.Right, r.Bottom),
                            new PointF(r.Left, r.Bottom),
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
                                g.DrawString(spriteName ?? string.Empty, lFont, lb, r, sf);
                        }
                        break;
                }
            }
        }

        private int GetFocusDimPercent()
        {
            if (_cmbFocusDim == null || _cmbFocusDim.SelectedItem == null)
                return 35;

            string text = _cmbFocusDim.SelectedItem.ToString();
            if (string.IsNullOrEmpty(text)) return 35;
            text = text.Replace("%", "").Trim();
            if (int.TryParse(text, out int value))
                return Math.Max(0, Math.Min(90, value));
            return 35;
        }

        private static Keyframe CloneKeyframe(Keyframe src)
        {
            if (src == null) return null;
            return new Keyframe
            {
                Tick = src.Tick,
                X = src.X,
                Y = src.Y,
                Width = src.Width,
                Height = src.Height,
                ColorR = src.ColorR,
                ColorG = src.ColorG,
                ColorB = src.ColorB,
                ColorA = src.ColorA,
                Rotation = src.Rotation,
                Scale = src.Scale,
                EasingToNext = src.EasingToNext,
            };
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
        private static int LerpInt(int a, int b, float t) => (int)Math.Round(a + (b - a) * t);

        private static float ApplyEasing(float t, EasingType easing)
        {
            switch (easing)
            {
                case EasingType.Linear: return t;
                case EasingType.SineInOut: return (float)(0.5 - 0.5 * Math.Cos(t * Math.PI));
                case EasingType.EaseIn: return t * t;
                case EasingType.EaseOut: return 1f - (1f - t) * (1f - t);
                case EasingType.EaseInOut:
                    return t < 0.5f ? 2f * t * t : 1f - 2f * (1f - t) * (1f - t);
                case EasingType.Bounce:
                    if (t < 1f / 2.75f) return 7.5625f * t * t;
                    if (t < 2f / 2.75f) { t -= 1.5f / 2.75f; return 7.5625f * t * t + 0.75f; }
                    if (t < 2.5f / 2.75f) { t -= 2.25f / 2.75f; return 7.5625f * t * t + 0.9375f; }
                    t -= 2.625f / 2.75f; return 7.5625f * t * t + 0.984375f;
                case EasingType.Elastic:
                    if (t == 0f || t == 1f) return t;
                    return (float)(Math.Pow(2, -10 * t) * Math.Sin((t - 0.075f) * (2 * Math.PI) / 0.3f) + 1);
                default:
                    return t;
            }
        }

        private static Keyframe InterpolateSpriteAtTick(SpriteEntry sprite, int tick)
        {
            if (sprite?.KeyframeAnimation?.Keyframes == null || sprite.KeyframeAnimation.Keyframes.Count == 0)
                return sprite == null ? null : Keyframe.FromSprite(sprite, tick);

            var animation = sprite.KeyframeAnimation;
            var sorted = animation.Keyframes.OrderBy(k => k.Tick).ToList();
            int maxTick = sorted.Count > 0 ? sorted[sorted.Count - 1].Tick : 1;
            int effectiveTick = tick;

            if (maxTick > 0)
            {
                switch (animation.Loop)
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

            if (effectiveTick <= sorted[0].Tick)
                return CloneKeyframe(sorted[0]);

            if (effectiveTick >= sorted[sorted.Count - 1].Tick)
                return CloneKeyframe(sorted[sorted.Count - 1]);

            int segIdx = 0;
            for (int i = 1; i < sorted.Count; i++)
            {
                if (effectiveTick >= sorted[i].Tick) segIdx = i;
            }

            var a = sorted[segIdx];
            var b = segIdx + 1 < sorted.Count ? sorted[segIdx + 1] : a;

            float span = b.Tick - a.Tick;
            float frac = span > 0 ? (effectiveTick - a.Tick) / span : 0f;
            float ef = ApplyEasing(frac, a.EasingToNext);

            return new Keyframe
            {
                Tick = effectiveTick,
                X = Lerp(a.X ?? sprite.X, b.X ?? sprite.X, ef),
                Y = Lerp(a.Y ?? sprite.Y, b.Y ?? sprite.Y, ef),
                Width = Lerp(a.Width ?? sprite.Width, b.Width ?? sprite.Width, ef),
                Height = Lerp(a.Height ?? sprite.Height, b.Height ?? sprite.Height, ef),
                ColorR = LerpInt(a.ColorR ?? sprite.ColorR, b.ColorR ?? sprite.ColorR, ef),
                ColorG = LerpInt(a.ColorG ?? sprite.ColorG, b.ColorG ?? sprite.ColorG, ef),
                ColorB = LerpInt(a.ColorB ?? sprite.ColorB, b.ColorB ?? sprite.ColorB, ef),
                ColorA = LerpInt(a.ColorA ?? sprite.ColorA, b.ColorA ?? sprite.ColorA, ef),
                Rotation = Lerp(a.Rotation ?? sprite.Rotation, b.Rotation ?? sprite.Rotation, ef),
                Scale = Lerp(a.Scale ?? sprite.Scale, b.Scale ?? sprite.Scale, ef),
                EasingToNext = EasingType.Linear,
            };
        }

        // ── Utilities ────────────────────────────────────────────────────────────

        private void SetStatus(string text) => _lblStatus.Text = text;

        private static Button DarkBtn(string text, Color back, int width)
        {
            var btn = new Button
            {
                Text      = text,
                BackColor = back,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Height    = 24,
                Width     = width,
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }
    }
}
