using System;
using System.Collections.Generic;
using System.Drawing;
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
        private Label       _lblSpriteHeader;
        private Label       _lblStatus;
        private Timer       _playTimer;
        private Button      _btnPlay;
        private Label       _lblTick;
        private TrackBar    _speedBar;
        private Label       _lblSpeed;
        private bool        _isPlaying;

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

            // ── Properties panel (right side) ──
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

            var rightPanel = new Panel
            {
                Dock  = DockStyle.Right,
                Width = 280,
            };
            rightPanel.Controls.Add(_propsPanel);
            rightPanel.Controls.Add(_lblSpriteHeader);

            // ── Split: left timeline | right props ──
            var split = new SplitContainer
            {
                Dock         = DockStyle.Fill,
                Orientation  = Orientation.Vertical,
                SplitterWidth = 4,
                BackColor    = Color.FromArgb(24, 24, 28),
            };
            split.Panel1.Controls.Add(timelineContainer);
            split.Panel2.Controls.Add(_propsPanel);
            split.Panel2.Controls.Add(_lblSpriteHeader);

            Controls.Add(split);
            Controls.Add(bottomBar);
            Controls.Add(_lblStatus);

            Load += (s, e) =>
            {
                try { split.SplitterDistance = Math.Max(300, (int)(Width * 0.72)); } catch { }
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
            var sprite = _animatedSprites[idx];
            _lblSpriteHeader.Text = $"   {sprite.DisplayName}  —  {sprite.KeyframeAnimation?.Keyframes?.Count ?? 0} keyframes";
            ShowSpriteKeyframeList(idx);
        }

        private void OnTimelineKeyframeSelected(int spriteIdx, int kfIdx)
        {
            if (spriteIdx < 0 || spriteIdx >= _animatedSprites.Count) return;
            ShowKeyframeProperties(spriteIdx, kfIdx);
        }

        private void OnTimelineKeyframeMoved(int spriteIdx, int kfIdx, int newTick)
        {
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
            SetStatus($"Added keyframe at tick {tick} for '{_animatedSprites[spriteIdx].DisplayName}'");
            ShowSpriteKeyframeList(spriteIdx);
        }

        private void OnPlayheadChanged(int tick)
        {
            _lblTick.Text = $"Tick: {tick}";
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
                n.ValueChanged += (s, e) => { set((float)n.Value); _timeline.RefreshDisplay(); };
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
                n.ValueChanged += (s, e) => { set((int)n.Value); _timeline.RefreshDisplay(); };
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
                c.SelectedIndexChanged += (s, e) => { set(c.SelectedIndex); _timeline.RefreshDisplay(); };
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
