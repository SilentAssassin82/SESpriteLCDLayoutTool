using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SESpriteLCDLayoutTool.Controls;
using SESpriteLCDLayoutTool.Models;
using SESpriteLCDLayoutTool.Models.Rig;

namespace SESpriteLCDLayoutTool.Forms
{
    /// <summary>
    /// Floating, non-modal rig editor window (Phase 3b.2 — option C).
    /// Provides rig CRUD, bone tree (add child / sibling / delete / reparent),
    /// numeric inspector, and sprite binding management. All mutations route through
    /// the host's <see cref="UndoCallback"/> so rig edits participate in undo/redo.
    /// </summary>
    public class RigEditorWindow : Form
    {
        // ── Host wiring ────────────────────────────────────────────────────────
        private readonly LcdLayout _layout;
        private readonly LcdCanvas _canvas;

        /// <summary>Called immediately before any rig mutation so the host can snapshot for undo.</summary>
        public Action UndoCallback { get; set; }

        /// <summary>Called after a rig mutation so the host can repaint / refresh dependent UI.</summary>
        public Action ChangedCallback { get; set; }

        /// <summary>
        /// Called when the user requests rig code injection. The host is expected to take
        /// the generated rig snippet and merge it into the main code panel using its normal
        /// code-update pipeline (diff + undo + write-back). Returns a short status string
        /// to display in the editor (e.g. "Rig code injected." or "No code to inject into.").
        /// </summary>
        public Func<string> InjectCallback { get; set; }

        // ── UI ────────────────────────────────────────────────────────────────
        private ComboBox _cmbRig;
        private Button _btnAddRig, _btnRenameRig, _btnDeleteRig;
        private CheckBox _chkRigEnabled;
        private NumericUpDown _numOriginX, _numOriginY;

        private TreeView _treeBones;
        private Button _btnAddRoot, _btnAddChild, _btnDeleteBone, _btnReparent;

        // Bone inspector
        private TextBox _txtBoneName;
        private NumericUpDown _numLocalX, _numLocalY, _numLocalRot, _numScaleX, _numScaleY, _numLength;
        private CheckBox _chkLocked, _chkHidden;
        private Button _btnBoneColor;
        private Panel _boneColorPreview;

        // Bindings
        private ListBox _lstBindings;
        private Button _btnBindSelected, _btnUnbind, _btnMute;
        private NumericUpDown _numBindOffX, _numBindOffY, _numBindRot, _numBindSx, _numBindSy;

        private Label _statusLbl;

        // Animation panel
        private ComboBox _cmbClip;
        private Button _btnAddClip, _btnRenameClip, _btnDeleteClip;
        private NumericUpDown _numClipDuration;
        private CheckBox _chkClipLoop;
        private TrackBar _trkTime;
        private NumericUpDown _numTime;
        private Button _btnPlay, _btnStop, _btnSetKey, _btnDeleteKey;        private Label _lblKeyInfo;
        private ListBox _lstKeys;
        private CheckBox _chkOnionSkin;
        private NumericUpDown _numOnionCount;
        private ComboBox _cmbEasing;
        private EaseCurvePreview _easePreview;
        private Timer _playTimer;
        private DateTime _playStart;
        private float _playStartTime;

        private bool _suppress; // re-entrancy guard while populating UI from data

        public RigEditorWindow(LcdLayout layout, LcdCanvas canvas)
        {
            _layout = layout ?? throw new ArgumentNullException(nameof(layout));
            _canvas = canvas;

            Text = "Rig Editor";
            StartPosition = FormStartPosition.Manual;
            Size = new Size(420, 720);
            MinimumSize = new Size(360, 520);
            BackColor = Color.FromArgb(30, 30, 30);
            ForeColor = Color.FromArgb(220, 220, 220);
            Font = new Font("Segoe UI", 8.5f);
            ShowInTaskbar = false;
            KeyPreview = true;

            BuildUi();
            RefreshFromLayout();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Layout
        // ─────────────────────────────────────────────────────────────────────
        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = BackColor,
                Padding = new Padding(6),
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // rig row
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // splitters fill the rest
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // animation panel
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // status
            Controls.Add(root);

            root.Controls.Add(BuildRigPanel(), 0, 0);

            // Outer split: Bones (top) | Inspector + Bindings (bottom).
            // Inner split: Bone Inspector (top) | Sprite Bindings (bottom).
            // Both splitters are user-draggable so panels can be resized; everything
            // grows with the window because each SplitContainer is Dock=Fill.
            var innerSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                BackColor = BackColor,
                SplitterWidth = 5,
            };
            innerSplit.Panel1.Controls.Add(BuildInspectorPanel());
            innerSplit.Panel2.Controls.Add(BuildBindingsPanel());

            var outerSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                BackColor = BackColor,
                SplitterWidth = 5,
            };
            outerSplit.Panel1.Controls.Add(BuildBonesPanel());
            outerSplit.Panel2.Controls.Add(innerSplit);
            root.Controls.Add(outerSplit, 0, 1);

            // Defer min-size + initial splitter positions until the form is laid out.
            // Setting them too early (before docking) trips SplitContainer's validation
            // because it measures against the default 150×100 size.
            this.Shown += (s, e) =>
            {
                try
                {
                    SafeSetSplit(outerSplit, panel1Min: 80, panel2Min: 140, fraction: 0.32);
                    SafeSetSplit(innerSplit, panel1Min: 90,  panel2Min: 80,  fraction: 0.45);
                }
                catch { /* SplitContainer is picky about distance vs size; ignore */ }
            };

            _statusLbl = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                Height = 22,
                ForeColor = Color.FromArgb(170, 200, 170),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Ready",
            };
            root.Controls.Add(BuildAnimationPanel(), 0, 2);
            root.Controls.Add(_statusLbl, 0, 3);
        }

        private GroupBox BuildAnimationPanel()
        {
            var gb = MakeGroup("Animation");
            gb.AutoSize = true;
            gb.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 8, RowCount = 6, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            for (int i = 0; i < 8; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles[1] = new ColumnStyle(SizeType.Percent, 100);
            for (int i = 0; i < 4; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));

            // Row 0: clip CRUD + duration + loop
            _cmbClip = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
            _cmbClip.SelectedIndexChanged += (s, e) =>
            {
                if (_suppress) return;
                var rig = SelectedRig();
                var clip = SelectedClip();
                if (rig != null) rig.ActiveClipId = clip?.Id;
                RefreshAnimationInspector();
                ChangedCallback?.Invoke();
            };
            _btnAddClip = MakeBtn("+ Clip", AddClip);
            _btnRenameClip = MakeBtn("Rename", RenameClip);
            _btnDeleteClip = MakeBtn("Delete", DeleteClip);
            _numClipDuration = MakeNum(0.05m, 600m, 2, 1m);
            _numClipDuration.ValueChanged += (s, e) =>
            {
                var c = SelectedClip(); if (_suppress || c == null) return;
                Mutate(() => c.Duration = (float)_numClipDuration.Value);
                _trkTime.Maximum = Math.Max(1, (int)((float)_numClipDuration.Value * 1000f));
            };
            _chkClipLoop = new CheckBox { Text = "Loop", AutoSize = true, ForeColor = ForeColor, Checked = true };
            _chkClipLoop.CheckedChanged += (s, e) =>
            {
                var c = SelectedClip(); if (_suppress || c == null) return;
                Mutate(() => c.Loop = _chkClipLoop.Checked);
            };

            t.Controls.Add(MakeLbl("Clip"), 0, 0);
            t.Controls.Add(_cmbClip, 1, 0);
            t.Controls.Add(_btnAddClip, 2, 0);
            t.Controls.Add(_btnRenameClip, 3, 0);
            t.Controls.Add(_btnDeleteClip, 4, 0);
            t.Controls.Add(MakeLbl("Dur(s)"), 5, 0);
            t.Controls.Add(_numClipDuration, 6, 0);
            t.Controls.Add(_chkClipLoop, 7, 0);

            // Row 1: scrub bar + numeric time
            _trkTime = new TrackBar
            {
                Dock = DockStyle.Fill,
                Minimum = 0,
                Maximum = 1000,
                TickFrequency = 100,
                SmallChange = 10,
                LargeChange = 100,
                Height = 30,
            };
            _trkTime.Scroll += (s, e) =>
            {
                if (_suppress) return;
                float t2 = _trkTime.Value / 1000f;
                SetPreviewTime(t2, fromTrack: true);
            };
            _numTime = MakeNum(0, 600, 3);
            _numTime.ValueChanged += (s, e) =>
            {
                if (_suppress) return;
                SetPreviewTime((float)_numTime.Value, fromTrack: false);
            };
            t.Controls.Add(MakeLbl("Time"), 0, 1);
            t.Controls.Add(_trkTime, 1, 1);
            t.SetColumnSpan(_trkTime, 5);
            t.Controls.Add(_numTime, 6, 1);

            // Row 2: transport + key actions
            _btnPlay = MakeBtn("▶ Play", TogglePlay);
            _btnStop = MakeBtn("■ Stop", StopPlayback);
            _btnSetKey = MakeBtn("Set Key", SetKey);
            _btnDeleteKey = MakeBtn("Delete Key", DeleteKeyAtTime);
            _lblKeyInfo = new Label { AutoSize = true, ForeColor = Color.FromArgb(160, 200, 160), Margin = new Padding(4, 6, 4, 0), Text = "—" };
            t.Controls.Add(_btnPlay, 0, 2);
            t.Controls.Add(_btnStop, 1, 2);
            t.Controls.Add(_btnSetKey, 2, 2);
            t.Controls.Add(_btnDeleteKey, 3, 2);
            t.Controls.Add(_lblKeyInfo, 4, 2);
            t.SetColumnSpan(_lblKeyInfo, 4);

            // Row 3: code generation + onion skin + key easing
            var btnCopyCode = MakeBtn("Generate…", CopyRigCodeToClipboard);
            t.Controls.Add(btnCopyCode, 0, 3);

            var btnInjectCode = MakeBtn("Inject", InjectRigCodeRequest);
            t.Controls.Add(btnInjectCode, 1, 3);

            _chkOnionSkin = new CheckBox
            {
                Text = "Onion ±",
                AutoSize = true,
                ForeColor = Color.FromArgb(220, 220, 220),
                Margin = new Padding(8, 6, 0, 0),
            };
            _chkOnionSkin.CheckedChanged += (s, e) =>
            {
                if (_canvas == null) return;
                _canvas.OnionSkinEnabled = _chkOnionSkin.Checked;
                // Onion skin is gated on AnimationPreviewEnabled so the rig samples
                // overrides; when loading a saved scene the user may not have scrubbed
                // yet, so flip preview on (and seed the time) when ghosts are requested.
                if (_chkOnionSkin.Checked)
                {
                    _canvas.AnimationPreviewEnabled = true;
                    _canvas.AnimationPreviewTime = (float)_numTime.Value;
                }
                _canvas.Invalidate();
            };
            t.Controls.Add(_chkOnionSkin, 2, 3);

            _numOnionCount = new NumericUpDown
            {
                Minimum = 1, Maximum = 5, Value = 1, Width = 42,
                Margin = new Padding(0, 4, 4, 0),
            };
            _numOnionCount.ValueChanged += (s, e) =>
            {
                if (_canvas == null) return;
                _canvas.OnionSkinKeyCount = (int)_numOnionCount.Value;
                _canvas.Invalidate();
            };
            t.Controls.Add(_numOnionCount, 3, 3);
            t.Controls.Add(MakeLbl("Easing"), 4, 3);
            _cmbEasing = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
            _cmbEasing.Items.Add(RigEasing.Linear);
            _cmbEasing.Items.Add(RigEasing.EaseInOut);
            _cmbEasing.Items.Add(RigEasing.Step);
            _cmbEasing.SelectedIndex = 0;
            _cmbEasing.SelectedIndexChanged += (s, e) =>
            {
                if (_easePreview != null && _cmbEasing.SelectedItem is RigEasing ce)
                    _easePreview.Easing = ce;
                if (_suppress) return;
                ApplyEasingToSelectedKey();
            };
            t.Controls.Add(_cmbEasing, 5, 3);
            t.SetColumnSpan(_cmbEasing, 2);

            // Mini ease curve graph that mirrors the sampler's evaluation so users can
            // see Linear / EaseInOut / Step at a glance instead of guessing from the name.
            _easePreview = new EaseCurvePreview
            {
                Width = 56,
                Height = 22,
                Margin = new Padding(4, 4, 4, 0),
            };
            t.Controls.Add(_easePreview, 7, 3);

            // Row 4: pose operations (copy/paste/mirror across all bones in the rig)
            var btnCopyPose  = MakeBtn("Copy Pose",   () => CopyRigPoseToClipboard());
            var btnPastePose = MakeBtn("Paste Pose",  () => PasteRigPoseFromClipboard());
            var btnMirrorX   = MakeBtn("Mirror X",    () => MirrorRigPose(true));
            var btnMirrorY   = MakeBtn("Mirror Y",    () => MirrorRigPose(false));
            t.Controls.Add(btnCopyPose,  0, 4);
            t.Controls.Add(btnPastePose, 1, 4);
            t.Controls.Add(btnMirrorX,   2, 4);
            t.Controls.Add(btnMirrorY,   3, 4);

            // Row 5: keyframe list for the currently selected bone
            _lstKeys = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 20, 20),
                ForeColor = Color.FromArgb(220, 220, 220),
                Font = new Font("Consolas", 8.5f),
                IntegralHeight = false,
                BorderStyle = BorderStyle.FixedSingle,
            };
            _lstKeys.SelectedIndexChanged += (s, e) =>
            {
                if (_suppress) return;
                var item = _lstKeys.SelectedItem as KeyItem;
                if (item == null) return;
                SetPreviewTime(item.Key.Time, fromTrack: false);
            };
            t.Controls.Add(_lstKeys, 0, 5);
            t.SetColumnSpan(_lstKeys, 8);

            _playTimer = new Timer { Interval = 33 };
            _playTimer.Tick += (s, e) =>
            {
                var c = SelectedClip();
                if (c == null) { StopPlayback(); return; }
                float dt = (float)(DateTime.UtcNow - _playStart).TotalSeconds;
                float t2 = _playStartTime + dt;
                if (!c.Loop && t2 >= c.Duration) { SetPreviewTime(c.Duration, false); StopPlayback(); return; }
                SetPreviewTime(t2, false);
            };

            gb.Controls.Add(t);
            return gb;
        }

        private GroupBox BuildRigPanel()
        {
            var gb = MakeGroup("Rig");
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 2, AutoSize = true };
            for (int i = 0; i < 6; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _cmbRig = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
            _cmbRig.SelectedIndexChanged += (s, e) => { if (!_suppress) { RefreshBones(); RefreshInspectors(); } };
            _btnAddRig = MakeBtn("+ Rig", AddRig);
            _btnRenameRig = MakeBtn("Rename", RenameRig);
            _btnDeleteRig = MakeBtn("Delete", DeleteRig);
            _chkRigEnabled = new CheckBox { Text = "Enabled", AutoSize = true, ForeColor = ForeColor };
            _chkRigEnabled.CheckedChanged += (s, e) =>
            {
                if (_suppress) return; var rig = SelectedRig(); if (rig == null) return;
                Mutate(() => rig.Enabled = _chkRigEnabled.Checked);
            };

            t.Controls.Add(_cmbRig, 0, 0);
            t.Controls.Add(_btnAddRig, 1, 0);
            t.Controls.Add(_btnRenameRig, 2, 0);
            t.Controls.Add(_btnDeleteRig, 3, 0);
            t.Controls.Add(_chkRigEnabled, 4, 0);

            t.Controls.Add(MakeLbl("Origin X"), 0, 1);
            _numOriginX = MakeNum(-10000, 10000, 0);
            _numOriginX.ValueChanged += (s, e) => { var r = SelectedRig(); if (!_suppress && r != null) Mutate(() => r.OriginX = (float)_numOriginX.Value); };
            t.Controls.Add(_numOriginX, 1, 1);
            t.Controls.Add(MakeLbl("Y"), 2, 1);
            _numOriginY = MakeNum(-10000, 10000, 0);
            _numOriginY.ValueChanged += (s, e) => { var r = SelectedRig(); if (!_suppress && r != null) Mutate(() => r.OriginY = (float)_numOriginY.Value); };
            t.Controls.Add(_numOriginY, 3, 1);

            gb.Controls.Add(t);
            return gb;
        }

        private GroupBox BuildBonesPanel()
        {
            var gb = MakeGroup("Bones");
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _treeBones = new TreeView
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = ForeColor,
                BorderStyle = BorderStyle.FixedSingle,
                HideSelection = false,
                FullRowSelect = true,
            };
            _treeBones.AfterSelect += (s, e) => { if (!_suppress) RefreshInspectors(); };
            t.Controls.Add(_treeBones, 0, 0);

            var btnRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Height = 28 };
            _btnAddRoot = MakeBtn("+ Root", () => AddBone(asChild: false));
            _btnAddChild = MakeBtn("+ Child", () => AddBone(asChild: true));
            _btnDeleteBone = MakeBtn("Delete", DeleteSelectedBone);
            _btnReparent = MakeBtn("Reparent…", ReparentBone);
            btnRow.Controls.AddRange(new Control[] { _btnAddRoot, _btnAddChild, _btnDeleteBone, _btnReparent });
            t.Controls.Add(btnRow, 0, 1);

            gb.Controls.Add(t);
            return gb;
        }

        private GroupBox BuildInspectorPanel()
        {
            var gb = MakeGroup("Bone Inspector");
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 4, AutoSize = true };
            for (int i = 0; i < 6; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            t.Controls.Add(MakeLbl("Name"), 0, 0);
            _txtBoneName = new TextBox { Width = 140, BackColor = Color.FromArgb(45, 45, 45), ForeColor = ForeColor, BorderStyle = BorderStyle.FixedSingle };
            _txtBoneName.TextChanged += (s, e) => { var b = SelectedBone(); if (!_suppress && b != null) Mutate(() => b.Name = _txtBoneName.Text, refreshTree: true); };
            t.Controls.Add(_txtBoneName, 1, 0);
            t.SetColumnSpan(_txtBoneName, 2);

            _chkLocked = new CheckBox { Text = "Locked", AutoSize = true, ForeColor = ForeColor };
            _chkLocked.CheckedChanged += (s, e) => { var b = SelectedBone(); if (!_suppress && b != null) Mutate(() => b.Locked = _chkLocked.Checked); };
            _chkHidden = new CheckBox { Text = "Hidden", AutoSize = true, ForeColor = ForeColor };
            _chkHidden.CheckedChanged += (s, e) => { var b = SelectedBone(); if (!_suppress && b != null) Mutate(() => b.Hidden = _chkHidden.Checked); };
            t.Controls.Add(_chkLocked, 3, 0);
            t.Controls.Add(_chkHidden, 4, 0);

            t.Controls.Add(MakeLbl("X"), 0, 1);
            _numLocalX = MakeNum(-10000, 10000, 2);
            _numLocalX.ValueChanged += (s, e) => { var b = SelectedBone(); if (!_suppress && b != null) Mutate(() => b.LocalX = (float)_numLocalX.Value); };
            t.Controls.Add(_numLocalX, 1, 1);
            t.Controls.Add(MakeLbl("Y"), 2, 1);
            _numLocalY = MakeNum(-10000, 10000, 2);
            _numLocalY.ValueChanged += (s, e) => { var b = SelectedBone(); if (!_suppress && b != null) Mutate(() => b.LocalY = (float)_numLocalY.Value); };
            t.Controls.Add(_numLocalY, 3, 1);
            t.Controls.Add(MakeLbl("Rot°"), 4, 1);
            _numLocalRot = MakeNum(-720, 720, 2);
            _numLocalRot.ValueChanged += (s, e) =>
            {
                var b = SelectedBone(); if (_suppress || b == null) return;
                Mutate(() => b.LocalRotation = (float)((double)_numLocalRot.Value * Math.PI / 180.0));
            };
            t.Controls.Add(_numLocalRot, 5, 1);

            t.Controls.Add(MakeLbl("Sx"), 0, 2);
            _numScaleX = MakeNum(-100, 100, 3, 1);
            _numScaleX.ValueChanged += (s, e) => { var b = SelectedBone(); if (!_suppress && b != null) Mutate(() => b.LocalScaleX = (float)_numScaleX.Value); };
            t.Controls.Add(_numScaleX, 1, 2);
            t.Controls.Add(MakeLbl("Sy"), 2, 2);
            _numScaleY = MakeNum(-100, 100, 3, 1);
            _numScaleY.ValueChanged += (s, e) => { var b = SelectedBone(); if (!_suppress && b != null) Mutate(() => b.LocalScaleY = (float)_numScaleY.Value); };
            t.Controls.Add(_numScaleY, 3, 2);
            t.Controls.Add(MakeLbl("Len"), 4, 2);
            _numLength = MakeNum(0, 10000, 1, 32);
            _numLength.ValueChanged += (s, e) => { var b = SelectedBone(); if (!_suppress && b != null) Mutate(() => b.Length = (float)_numLength.Value); };
            t.Controls.Add(_numLength, 5, 2);

            t.Controls.Add(MakeLbl("Color"), 0, 3);
            _boneColorPreview = new Panel { Width = 22, Height = 18, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.Yellow };
            _btnBoneColor = MakeBtn("Pick…", PickBoneColor);
            t.Controls.Add(_boneColorPreview, 1, 3);
            t.Controls.Add(_btnBoneColor, 2, 3);

            gb.Controls.Add(t);
            return gb;
        }

        private GroupBox BuildBindingsPanel()
        {
            var gb = MakeGroup("Sprite Bindings (selected bone)");
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _lstBindings = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = ForeColor,
                BorderStyle = BorderStyle.FixedSingle,
                IntegralHeight = false,
            };
            _lstBindings.SelectedIndexChanged += (s, e) => { if (!_suppress) RefreshBindingInspector(); };
            t.Controls.Add(_lstBindings, 0, 0);

            var btnRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Height = 28 };
            _btnBindSelected = MakeBtn("Bind Selected Sprite", BindSelectedSprite);
            _btnUnbind = MakeBtn("Unbind", UnbindSelected);
            _btnMute = MakeBtn("Toggle Mute", ToggleMute);
            btnRow.Controls.AddRange(new Control[] { _btnBindSelected, _btnUnbind, _btnMute });
            t.Controls.Add(btnRow, 0, 1);

            var inspector = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 2, AutoSize = true };
            for (int i = 0; i < 6; i++) inspector.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            inspector.Controls.Add(MakeLbl("Off X"), 0, 0);
            _numBindOffX = MakeNum(-10000, 10000, 2);
            _numBindOffX.ValueChanged += (s, e) => { var bd = SelectedBinding(); if (!_suppress && bd != null) Mutate(() => bd.OffsetX = (float)_numBindOffX.Value); };
            inspector.Controls.Add(_numBindOffX, 1, 0);
            inspector.Controls.Add(MakeLbl("Y"), 2, 0);
            _numBindOffY = MakeNum(-10000, 10000, 2);
            _numBindOffY.ValueChanged += (s, e) => { var bd = SelectedBinding(); if (!_suppress && bd != null) Mutate(() => bd.OffsetY = (float)_numBindOffY.Value); };
            inspector.Controls.Add(_numBindOffY, 3, 0);
            inspector.Controls.Add(MakeLbl("Rot°"), 4, 0);
            _numBindRot = MakeNum(-720, 720, 2);
            _numBindRot.ValueChanged += (s, e) =>
            {
                var bd = SelectedBinding(); if (_suppress || bd == null) return;
                Mutate(() => bd.RotationOffset = (float)((double)_numBindRot.Value * Math.PI / 180.0));
            };
            inspector.Controls.Add(_numBindRot, 5, 0);

            inspector.Controls.Add(MakeLbl("Sx"), 0, 1);
            _numBindSx = MakeNum(-100, 100, 3, 1);
            _numBindSx.ValueChanged += (s, e) => { var bd = SelectedBinding(); if (!_suppress && bd != null) Mutate(() => bd.ScaleX = (float)_numBindSx.Value); };
            inspector.Controls.Add(_numBindSx, 1, 1);
            inspector.Controls.Add(MakeLbl("Sy"), 2, 1);
            _numBindSy = MakeNum(-100, 100, 3, 1);
            _numBindSy.ValueChanged += (s, e) => { var bd = SelectedBinding(); if (!_suppress && bd != null) Mutate(() => bd.ScaleY = (float)_numBindSy.Value); };
            inspector.Controls.Add(_numBindSy, 3, 1);

            t.Controls.Add(inspector, 0, 2);

            gb.Controls.Add(t);
            return gb;
        }

        // ─────────────────────────────────────────────────────────────────────
        // UI helpers
        // ─────────────────────────────────────────────────────────────────────
        private GroupBox MakeGroup(string title) => new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(180, 200, 220),
            Padding = new Padding(4),
            Margin = new Padding(0, 2, 0, 2),
        };
        private Label MakeLbl(string text) => new Label { Text = text, AutoSize = true, ForeColor = ForeColor, Margin = new Padding(4, 6, 2, 0) };
        private Button MakeBtn(string text, Action onClick)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                BackColor = Color.FromArgb(60, 60, 64),
                ForeColor = ForeColor,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(2),
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 95);
            b.Click += (s, e) => { try { onClick(); } catch (Exception ex) { SetStatus("Error: " + ex.Message); } };
            return b;
        }

        private static void SafeSetSplit(SplitContainer sc, int panel1Min, int panel2Min, double fraction)
        {
            int span = sc.Orientation == Orientation.Horizontal ? sc.Height : sc.Width;
            if (span <= 0) return;
            // Clamp mins so they never exceed the available span.
            int budget = span - sc.SplitterWidth - 4;
            if (panel1Min + panel2Min > budget)
            {
                int half = Math.Max(20, budget / 2);
                panel1Min = Math.Min(panel1Min, half);
                panel2Min = Math.Min(panel2Min, Math.Max(20, budget - panel1Min));
            }
            sc.Panel1MinSize = Math.Max(0, panel1Min);
            sc.Panel2MinSize = Math.Max(0, panel2Min);

            int distance = (int)(span * fraction);
            int maxDist = span - sc.Panel2MinSize - sc.SplitterWidth;
            distance = Math.Max(sc.Panel1MinSize, Math.Min(distance, maxDist));
            if (distance > 0 && distance >= sc.Panel1MinSize && distance <= maxDist)
                sc.SplitterDistance = distance;
        }

        private NumericUpDown MakeNum(decimal min, decimal max, int decimals, decimal value = 0)
        {
            var n = new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                DecimalPlaces = decimals,
                Increment = decimals == 0 ? 1m : (decimals >= 3 ? 0.05m : 0.5m),
                Width = 70,
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = ForeColor,
                BorderStyle = BorderStyle.FixedSingle,
                Value = value,
            };
            return n;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Mutation helper — pushes undo, runs action, refreshes UI + canvas.
        // ─────────────────────────────────────────────────────────────────────
        private void Mutate(Action mutation, bool refreshTree = false, bool refreshBindings = false)
        {
            UndoCallback?.Invoke();
            mutation();
            if (refreshTree) RefreshBones();
            if (refreshBindings) RefreshBindings();
            ChangedCallback?.Invoke();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>Re-syncs the entire window from the current layout. Call after undo/redo or layout swap.</summary>
        public void RefreshFromLayout()
        {
            _suppress = true;
            try
            {
                var prevId = (_cmbRig.SelectedItem as RigItem)?.Rig?.Id;
                _cmbRig.Items.Clear();
                foreach (var r in _layout.Rigs)
                    _cmbRig.Items.Add(new RigItem(r));

                if (_cmbRig.Items.Count > 0)
                {
                    int idx = 0;
                    if (prevId != null)
                    {
                        for (int i = 0; i < _cmbRig.Items.Count; i++)
                            if (((RigItem)_cmbRig.Items[i]).Rig.Id == prevId) { idx = i; break; }
                    }
                    _cmbRig.SelectedIndex = idx;
                }
            }
            finally { _suppress = false; }

            RefreshBones();
            RefreshInspectors();
            RefreshAnimationPanel();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Rig commands
        // ─────────────────────────────────────────────────────────────────────
        private void AddRig()
        {
            UndoCallback?.Invoke();
            var rig = new Rig { Name = "Rig " + (_layout.Rigs.Count + 1) };
            _layout.Rigs.Add(rig);
            RefreshFromLayout();
            for (int i = 0; i < _cmbRig.Items.Count; i++)
                if (((RigItem)_cmbRig.Items[i]).Rig.Id == rig.Id) { _cmbRig.SelectedIndex = i; break; }
            ChangedCallback?.Invoke();
            SetStatus("Added rig: " + rig.Name);
        }

        private void RenameRig()
        {
            var rig = SelectedRig(); if (rig == null) return;
            var name = Prompt("Rename rig", rig.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            Mutate(() => rig.Name = name);
            RefreshFromLayout();
        }

        private void DeleteRig()
        {
            var rig = SelectedRig(); if (rig == null) return;
            if (MessageBox.Show(this, $"Delete rig '{rig.Name}' and its {rig.Bones.Count} bones?",
                "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            UndoCallback?.Invoke();
            _layout.Rigs.Remove(rig);
            RefreshFromLayout();
            ChangedCallback?.Invoke();
            SetStatus("Deleted rig.");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Bone commands
        // ─────────────────────────────────────────────────────────────────────
        private void AddBone(bool asChild)
        {
            var rig = SelectedRig(); if (rig == null) { SetStatus("No rig selected."); return; }
            string parentId = null;
            float startX = 0f, startY = 0f;
            if (asChild)
            {
                var parent = SelectedBone();
                if (parent == null) { SetStatus("Select a parent bone first."); return; }
                parentId = parent.Id;
                // Place a new child at the parent's tip so chained bones are visibly distinct
                // instead of stacking on top of each other at the parent's joint.
                startX = parent.Length;
                startY = 0f;
            }
            else if (rig.Bones.Count > 0)
            {
                // Stagger sibling roots so they don't sit exactly on top of an existing root.
                startX = 0f;
                startY = rig.Bones.Count * 24f;
            }
            UndoCallback?.Invoke();
            var bone = new Bone
            {
                Name = "Bone " + (rig.Bones.Count + 1),
                ParentId = parentId,
                LocalX = startX,
                LocalY = startY,
            };
            rig.Bones.Add(bone);
            RefreshBones();
            SelectBone(bone.Id);
            ChangedCallback?.Invoke();
            SetStatus("Added bone: " + bone.Name);
        }

        private void DeleteSelectedBone()
        {
            var rig = SelectedRig(); var bone = SelectedBone();
            if (rig == null || bone == null) return;
            UndoCallback?.Invoke();
            // collect descendants
            var toRemove = new HashSet<string> { bone.Id };
            bool added;
            do
            {
                added = false;
                foreach (var b in rig.Bones)
                    if (b.ParentId != null && toRemove.Contains(b.ParentId) && toRemove.Add(b.Id)) added = true;
            } while (added);

            rig.Bones.RemoveAll(b => toRemove.Contains(b.Id));
            rig.Bindings.RemoveAll(bd => toRemove.Contains(bd.BoneId));
            RefreshBones();
            RefreshInspectors();
            ChangedCallback?.Invoke();
            SetStatus("Deleted bone and " + (toRemove.Count - 1) + " descendant(s).");
        }

        private void ReparentBone()
        {
            var rig = SelectedRig(); var bone = SelectedBone();
            if (rig == null || bone == null) return;

            // Build candidate list: any bone except self and descendants, plus "<root>".
            var descendants = new HashSet<string> { bone.Id };
            bool added;
            do
            {
                added = false;
                foreach (var b in rig.Bones)
                    if (b.ParentId != null && descendants.Contains(b.ParentId) && descendants.Add(b.Id)) added = true;
            } while (added);

            var options = new List<string> { "<root>" };
            var ids = new List<string> { null };
            foreach (var b in rig.Bones)
            {
                if (descendants.Contains(b.Id)) continue;
                options.Add(b.Name + " (" + b.Id.Substring(0, Math.Min(6, b.Id.Length)) + ")");
                ids.Add(b.Id);
            }

            using (var dlg = new ChoiceDialog("Reparent " + bone.Name, "Choose new parent:", options))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Mutate(() => bone.ParentId = ids[dlg.SelectedIndex], refreshTree: true);
                SelectBone(bone.Id);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Binding commands
        // ─────────────────────────────────────────────────────────────────────
        private void BindSelectedSprite()
        {
            var rig = SelectedRig(); var bone = SelectedBone();
            if (rig == null || bone == null) { SetStatus("Select a bone first."); return; }
            var sprite = _canvas?.SelectedSprite;
            if (sprite == null) { SetStatus("Select a sprite on the canvas first."); return; }
            int idx = _layout.Sprites.IndexOf(sprite);
            if (idx < 0) { SetStatus("Sprite not found in layout."); return; }
            if (rig.Bindings.Any(b => b.BoneId == bone.Id && b.SpriteIndex == idx))
            {
                SetStatus("Already bound."); return;
            }
            Mutate(() => rig.Bindings.Add(new SpriteBinding { BoneId = bone.Id, SpriteIndex = idx }), refreshBindings: true);
            SetStatus("Bound sprite #" + idx + " to " + bone.Name);
        }

        private void UnbindSelected()
        {
            var rig = SelectedRig(); var bd = SelectedBinding();
            if (rig == null || bd == null) return;
            Mutate(() => rig.Bindings.Remove(bd), refreshBindings: true);
            SetStatus("Unbound.");
        }

        private void ToggleMute()
        {
            var bd = SelectedBinding(); if (bd == null) return;
            Mutate(() => bd.Muted = !bd.Muted, refreshBindings: true);
        }

        private void PickBoneColor()
        {
            var bone = SelectedBone(); if (bone == null) return;
            using (var dlg = new ColorDialog { Color = bone.OverlayColor, FullOpen = true })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Mutate(() => bone.OverlayColor = dlg.Color);
                _boneColorPreview.BackColor = dlg.Color;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Refreshers
        // ─────────────────────────────────────────────────────────────────────
        private void RefreshBones()
        {
            _suppress = true;
            try
            {
                var prevId = SelectedBone()?.Id;
                _treeBones.BeginUpdate();
                _treeBones.Nodes.Clear();
                var rig = SelectedRig();
                if (rig != null)
                {
                    var byParent = rig.Bones.ToLookup(b => b.ParentId ?? "");
                    void AddChildren(TreeNodeCollection target, string parentId)
                    {
                        foreach (var b in byParent[parentId ?? ""])
                        {
                            var node = new TreeNode(b.Name + (b.Hidden ? " (hidden)" : "") + (b.Locked ? " 🔒" : "")) { Tag = b };
                            target.Add(node);
                            AddChildren(node.Nodes, b.Id);
                        }
                    }
                    AddChildren(_treeBones.Nodes, null);
                    _treeBones.ExpandAll();

                    if (prevId != null) SelectBone(prevId);
                }
                _treeBones.EndUpdate();
            }
            finally { _suppress = false; }
        }

        /// <summary>
        /// Lightweight refresh used during canvas-driven bone drags. Updates only the
        /// numeric inspector fields for the currently selected bone — does NOT rebuild
        /// the rig combo, bone tree, or bindings list. This keeps the canvas responsive
        /// while values stream in at mouse-move frequency.
        /// </summary>
        public void RefreshSelectedBoneInspector()
        {
            var bone = SelectedBone();
            if (bone == null) return;
            _suppress = true;
            try
            {
                _numLocalX.Value   = (decimal)Clamp(bone.LocalX, _numLocalX);
                _numLocalY.Value   = (decimal)Clamp(bone.LocalY, _numLocalY);
                _numLocalRot.Value = (decimal)Clamp((float)(bone.LocalRotation * 180.0 / Math.PI), _numLocalRot);
                _numScaleX.Value   = (decimal)Clamp(bone.LocalScaleX, _numScaleX);
                _numScaleY.Value   = (decimal)Clamp(bone.LocalScaleY, _numScaleY);
                _numLength.Value   = (decimal)Clamp(bone.Length, _numLength);
            }
            finally { _suppress = false; }
        }

        /// <summary>
        /// Called by the host when the canvas selects a bone via mouse gestures.
        /// Selects the matching node in the tree (one cheap traversal) instead of
        /// rebuilding the entire window from layout.
        /// </summary>
        public void SyncSelectedBoneFromCanvas(Bone bone)
        {
            if (bone == null) return;
            // If selection already matches, no-op.
            if (SelectedBone()?.Id == bone.Id) return;
            _suppress = true;
            try { SelectBone(bone.Id); }
            finally { _suppress = false; }
            RefreshSelectedBoneInspector();
        }

        private void RefreshInspectors()
        {
            _suppress = true;
            try
            {
                var rig = SelectedRig();
                bool haveRig = rig != null;
                _btnRenameRig.Enabled = _btnDeleteRig.Enabled = _chkRigEnabled.Enabled = haveRig;
                _numOriginX.Enabled = _numOriginY.Enabled = haveRig;
                _btnAddRoot.Enabled = haveRig;
                if (haveRig)
                {
                    _chkRigEnabled.Checked = rig.Enabled;
                    _numOriginX.Value = (decimal)Clamp(rig.OriginX, _numOriginX);
                    _numOriginY.Value = (decimal)Clamp(rig.OriginY, _numOriginY);
                }

                var bone = SelectedBone();
                bool haveBone = bone != null;
                _txtBoneName.Enabled = haveBone;
                _numLocalX.Enabled = _numLocalY.Enabled = _numLocalRot.Enabled = haveBone;
                _numScaleX.Enabled = _numScaleY.Enabled = _numLength.Enabled = haveBone;
                _chkLocked.Enabled = _chkHidden.Enabled = _btnBoneColor.Enabled = haveBone;
                _btnAddChild.Enabled = _btnDeleteBone.Enabled = _btnReparent.Enabled = haveBone;
                if (haveBone)
                {
                    _txtBoneName.Text = bone.Name ?? "";
                    _numLocalX.Value = (decimal)Clamp(bone.LocalX, _numLocalX);
                    _numLocalY.Value = (decimal)Clamp(bone.LocalY, _numLocalY);
                    _numLocalRot.Value = (decimal)Clamp((float)(bone.LocalRotation * 180.0 / Math.PI), _numLocalRot);
                    _numScaleX.Value = (decimal)Clamp(bone.LocalScaleX, _numScaleX);
                    _numScaleY.Value = (decimal)Clamp(bone.LocalScaleY, _numScaleY);
                    _numLength.Value = (decimal)Clamp(bone.Length, _numLength);
                    _chkLocked.Checked = bone.Locked;
                    _chkHidden.Checked = bone.Hidden;
                    _boneColorPreview.BackColor = bone.OverlayColor;
                }
            }
            finally { _suppress = false; }

            RefreshBindings();
            RefreshKeyInfoLabel();
        }

        private void RefreshBindings()
        {
            _suppress = true;
            try
            {
                _lstBindings.Items.Clear();
                var rig = SelectedRig(); var bone = SelectedBone();
                if (rig != null && bone != null)
                {
                    foreach (var bd in rig.Bindings.Where(b => b.BoneId == bone.Id))
                    {
                        string label = "sprite #" + bd.SpriteIndex;
                        if (bd.SpriteIndex >= 0 && bd.SpriteIndex < _layout.Sprites.Count)
                            label += " — " + (_layout.Sprites[bd.SpriteIndex].DisplayName ?? "(unnamed)");
                        if (bd.Muted) label += " [muted]";
                        _lstBindings.Items.Add(new BindingItem(bd, label));
                    }
                }
            }
            finally { _suppress = false; }
            RefreshBindingInspector();
        }

        private void RefreshBindingInspector()
        {
            _suppress = true;
            try
            {
                var bd = SelectedBinding();
                bool have = bd != null;
                _btnUnbind.Enabled = _btnMute.Enabled = have;
                _numBindOffX.Enabled = _numBindOffY.Enabled = _numBindRot.Enabled = have;
                _numBindSx.Enabled = _numBindSy.Enabled = have;
                if (have)
                {
                    _numBindOffX.Value = (decimal)Clamp(bd.OffsetX, _numBindOffX);
                    _numBindOffY.Value = (decimal)Clamp(bd.OffsetY, _numBindOffY);
                    _numBindRot.Value = (decimal)Clamp((float)(bd.RotationOffset * 180.0 / Math.PI), _numBindRot);
                    _numBindSx.Value = (decimal)Clamp(bd.ScaleX, _numBindSx);
                    _numBindSy.Value = (decimal)Clamp(bd.ScaleY, _numBindSy);
                }
            }
            finally { _suppress = false; }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Selection accessors
        // ─────────────────────────────────────────────────────────────────────
        private Rig SelectedRig() => (_cmbRig.SelectedItem as RigItem)?.Rig;
        private Bone SelectedBone() => _treeBones.SelectedNode?.Tag as Bone;
        private SpriteBinding SelectedBinding() => (_lstBindings.SelectedItem as BindingItem)?.Binding;

        private void SelectBone(string id)
        {
            TreeNode FindNode(TreeNodeCollection nodes)
            {
                foreach (TreeNode n in nodes)
                {
                    if ((n.Tag as Bone)?.Id == id) return n;
                    var c = FindNode(n.Nodes); if (c != null) return c;
                }
                return null;
            }
            var found = FindNode(_treeBones.Nodes);
            if (found != null) _treeBones.SelectedNode = found;
        }

        private static float Clamp(float value, NumericUpDown nud)
        {
            decimal v = (decimal)value;
            if (v < nud.Minimum) v = nud.Minimum;
            if (v > nud.Maximum) v = nud.Maximum;
            return (float)v;
        }

        private static string Prompt(string title, string initial)
        {
            using (var dlg = new Form
            {
                Text = title,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                Size = new Size(360, 130),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(220, 220, 220),
            })
            {
                var tb = new TextBox { Left = 12, Top = 12, Width = 320, Text = initial, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.FromArgb(220, 220, 220), BorderStyle = BorderStyle.FixedSingle };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 170, Top = 50, Width = 75 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 257, Top = 50, Width = 75 };
                dlg.Controls.AddRange(new Control[] { tb, ok, cancel });
                dlg.AcceptButton = ok; dlg.CancelButton = cancel;
                return dlg.ShowDialog() == DialogResult.OK ? tb.Text : null;
            }
        }

        private void SetStatus(string s) { if (_statusLbl != null) _statusLbl.Text = s; }

        // ─────────────────────────────────────────────────────────────────────
        // Animation commands
        // ─────────────────────────────────────────────────────────────────────
        private RigClip SelectedClip() => (_cmbClip.SelectedItem as ClipItem)?.Clip;

        private void AddClip()
        {
            var rig = SelectedRig();
            if (rig == null) { SetStatus("Select a rig first."); return; }
            UndoCallback?.Invoke();
            var clip = new RigClip { Name = "Clip " + (rig.Clips.Count + 1) };
            rig.Clips.Add(clip);
            rig.ActiveClipId = clip.Id;
            RefreshAnimationPanel();
            ChangedCallback?.Invoke();
            SetStatus("Added clip: " + clip.Name);
        }

        private void RenameClip()
        {
            var clip = SelectedClip(); if (clip == null) return;
            var name = Prompt("Rename clip", clip.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            Mutate(() => clip.Name = name);
            RefreshAnimationPanel();
        }

        private void DeleteClip()
        {
            var rig = SelectedRig(); var clip = SelectedClip();
            if (rig == null || clip == null) return;
            if (MessageBox.Show(this, $"Delete clip '{clip.Name}'?", "Confirm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            UndoCallback?.Invoke();
            rig.Clips.Remove(clip);
            if (rig.ActiveClipId == clip.Id) rig.ActiveClipId = rig.Clips.Count > 0 ? rig.Clips[0].Id : null;
            RefreshAnimationPanel();
            ChangedCallback?.Invoke();
        }

        private void TogglePlay()
        {
            if (_playTimer.Enabled) { StopPlayback(); return; }
            var c = SelectedClip(); if (c == null) { SetStatus("No clip."); return; }
            _playStart = DateTime.UtcNow;
            _playStartTime = (float)_numTime.Value;
            if (!c.Loop && _playStartTime >= c.Duration) _playStartTime = 0f;
            _playTimer.Start();
            _btnPlay.Text = "❚❚ Pause";
        }

        private void StopPlayback()
        {
            _playTimer.Stop();
            _btnPlay.Text = "▶ Play";
        }

        /// <summary>Sets the canvas/UI preview time and refreshes related UI without loops.</summary>
        private void SetPreviewTime(float seconds, bool fromTrack)
        {
            var c = SelectedClip();
            if (c != null && !c.Loop)
            {
                if (seconds < 0f) seconds = 0f;
                if (seconds > c.Duration) seconds = c.Duration;
            }
            _suppress = true;
            try
            {
                int trk = (int)Math.Max(0, Math.Min(_trkTime.Maximum, seconds * 1000f));
                if (!fromTrack) _trkTime.Value = trk;
                _numTime.Value = (decimal)Math.Max((double)_numTime.Minimum, Math.Min((double)_numTime.Maximum, seconds));
            }
            finally { _suppress = false; }

            if (_canvas != null)
            {
                _canvas.AnimationPreviewTime = seconds;
                _canvas.AnimationPreviewEnabled = true;
                _canvas.Invalidate();
            }
            RefreshKeyInfoLabel();
        }

        private void SetKey()
        {
            var rig = SelectedRig(); var clip = SelectedClip(); var bone = SelectedBone();
            if (rig == null || clip == null) { SetStatus("No clip selected."); return; }
            if (bone == null) { SetStatus("Select a bone to key."); return; }

            float t = (float)_numTime.Value;
            UndoCallback?.Invoke();
            var track = clip.GetOrCreateTrack(bone.Id);
            // Replace any existing key within a small epsilon, otherwise insert sorted.
            const float eps = 0.0005f;
            var existing = track.Keys.Find(k => Math.Abs(k.Time - t) <= eps);
            // Preserve existing easing (or use combo selection for brand-new keys) so
            // re-keying doesn't silently reset to Linear.
            RigEasing easing = existing != null ? existing.Easing
                : (_cmbEasing?.SelectedItem is RigEasing ce ? ce : RigEasing.Linear);
            var key = new RigKeyframe
            {
                Time = t,
                LocalX = bone.LocalX,
                LocalY = bone.LocalY,
                LocalRotation = bone.LocalRotation,
                LocalScaleX = bone.LocalScaleX,
                LocalScaleY = bone.LocalScaleY,
                Length = bone.Length,
                Easing = easing,
            };
            if (existing != null)
            {
                int idx = track.Keys.IndexOf(existing);
                track.Keys[idx] = key;
            }
            else
            {
                track.Keys.Add(key);
                track.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
            }
            ChangedCallback?.Invoke();
            RefreshKeyInfoLabel();
            SetStatus($"Key set at {t:0.000}s on {bone.Name}.");
        }

        /// <summary>
        /// Auto-key the given bone at the current preview time. Used by the canvas after a
        /// bone drag finishes so the new pose is captured into the active clip and won't
        /// be overwritten by the clip sampler on the next paint. No-op when there is no
        /// active clip — in that case the drag already mutates the rest pose directly.
        /// </summary>
        public void AutoKeyBoneAtCurrentTime(Models.Rig.Bone bone)
        {
            if (bone == null) return;
            var rig = SelectedRig();
            var clip = SelectedClip();
            if (rig == null || clip == null) return;
            // Only auto-key while the preview is engaged; otherwise the user is editing
            // the rest pose and we shouldn't silently create animation data.
            if (_canvas == null || !_canvas.AnimationPreviewEnabled) return;

            float t = (float)_numTime.Value;
            UndoCallback?.Invoke();
            var track = clip.GetOrCreateTrack(bone.Id);
            const float eps = 0.0005f;
            var existing = track.Keys.Find(k => Math.Abs(k.Time - t) <= eps);
            // Preserve any easing that was already set on this key; auto-key from a drag
            // must not silently reset the user's easing choice back to Linear.
            RigEasing easing = existing != null ? existing.Easing
                : (_cmbEasing?.SelectedItem is RigEasing ce ? ce : RigEasing.Linear);
            var key = new RigKeyframe
            {
                Time = t,
                LocalX = bone.LocalX,
                LocalY = bone.LocalY,
                LocalRotation = bone.LocalRotation,
                LocalScaleX = bone.LocalScaleX,
                LocalScaleY = bone.LocalScaleY,
                Length = bone.Length,
                Easing = easing,
            };
            if (existing != null)
            {
                int idx = track.Keys.IndexOf(existing);
                track.Keys[idx] = key;
            }
            else
            {
                track.Keys.Add(key);
                track.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
            }
            ChangedCallback?.Invoke();
            RefreshKeyInfoLabel();
            RefreshKeyList();
            SetStatus($"Auto-keyed {bone.Name} at {t:0.000}s.");
        }

        private void DeleteKeyAtTime()
        {
            var clip = SelectedClip(); var bone = SelectedBone();
            if (clip == null || bone == null) return;
            var track = clip.Tracks.Find(x => x.BoneId == bone.Id);
            if (track == null) return;
            float t = (float)_numTime.Value;
            const float eps = 0.0005f;
            var key = track.Keys.Find(k => Math.Abs(k.Time - t) <= eps);
            if (key == null) { SetStatus("No key at this time."); return; }
            UndoCallback?.Invoke();
            track.Keys.Remove(key);
            ChangedCallback?.Invoke();
            RefreshKeyInfoLabel();
            SetStatus($"Deleted key at {t:0.000}s.");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Pose copy / paste / mirror
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Plain-text marker so we can recognise a pose payload on the clipboard.</summary>
        private const string PoseClipboardHeader = "RigPoseV1";

        /// <summary>
        /// Snapshot every visible bone's local transform on the active rig and place it on
        /// the clipboard as a simple line-based payload. Identifying bones by Name keeps
        /// the payload portable across rigs that share a naming convention.
        /// </summary>
        private void CopyRigPoseToClipboard()
        {
            var rig = SelectedRig();
            if (rig == null || rig.Bones == null || rig.Bones.Count == 0)
            {
                SetStatus("No rig to copy."); return;
            }

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(PoseClipboardHeader);
            foreach (var b in rig.Bones)
            {
                if (b == null) continue;
                // name | x | y | rot | scaleX | scaleY | length
                sb.AppendFormat(inv, "{0}|{1}|{2}|{3}|{4}|{5}|{6}",
                    b.Name ?? string.Empty,
                    b.LocalX, b.LocalY, b.LocalRotation,
                    b.LocalScaleX, b.LocalScaleY, b.Length);
                sb.AppendLine();
            }

            try { Clipboard.SetText(sb.ToString()); SetStatus($"Copied pose ({rig.Bones.Count} bones)."); }
            catch (Exception ex) { SetStatus("Copy failed: " + ex.Message); }
        }

        /// <summary>
        /// Read a pose payload from the clipboard and apply it to bones with matching names.
        /// While previewing, also auto-keys each modified bone at the current time so the
        /// pasted pose isn't immediately overridden by the clip sampler.
        /// </summary>
        private void PasteRigPoseFromClipboard()
        {
            var rig = SelectedRig();
            if (rig == null) { SetStatus("No rig selected."); return; }

            string text;
            try { text = Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch { text = null; }
            if (string.IsNullOrWhiteSpace(text)) { SetStatus("Clipboard has no pose."); return; }

            var lines = text.Replace("\r", "").Split('\n');
            if (lines.Length < 2 || lines[0].Trim() != PoseClipboardHeader)
            { SetStatus("Clipboard data isn't a rig pose."); return; }

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            UndoCallback?.Invoke();
            int applied = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                var ln = lines[i];
                if (string.IsNullOrWhiteSpace(ln)) continue;
                var parts = ln.Split('|');
                if (parts.Length < 7) continue;
                var name = parts[0];
                var bone = rig.Bones?.Find(x => x != null && x.Name == name);
                if (bone == null) continue;
                if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out var lx)) continue;
                if (!float.TryParse(parts[2], System.Globalization.NumberStyles.Float, inv, out var ly)) continue;
                if (!float.TryParse(parts[3], System.Globalization.NumberStyles.Float, inv, out var lr)) continue;
                if (!float.TryParse(parts[4], System.Globalization.NumberStyles.Float, inv, out var sx)) continue;
                if (!float.TryParse(parts[5], System.Globalization.NumberStyles.Float, inv, out var sy)) continue;
                if (!float.TryParse(parts[6], System.Globalization.NumberStyles.Float, inv, out var len)) continue;
                bone.LocalX = lx; bone.LocalY = ly;
                bone.LocalRotation = lr;
                bone.LocalScaleX = sx; bone.LocalScaleY = sy;
                bone.Length = len;
                applied++;
                if (_canvas != null && _canvas.AnimationPreviewEnabled)
                    AutoKeyBoneAtCurrentTime(bone);
            }
            ChangedCallback?.Invoke();
            RefreshSelectedBoneInspector();
            _canvas?.Invalidate();
            SetStatus(applied > 0 ? $"Pasted pose to {applied} bone(s)." : "No matching bone names.");
        }

        /// <summary>
        /// Mirror the active rig's pose along the X (horizontal flip) or Y (vertical flip)
        /// axis. We negate the appropriate local translation component and reflect the local
        /// rotation so child orientation stays consistent. While previewing, modified bones
        /// are auto-keyed.
        /// </summary>
        private void MirrorRigPose(bool mirrorAcrossX)
        {
            var rig = SelectedRig();
            if (rig == null || rig.Bones == null || rig.Bones.Count == 0)
            { SetStatus("No rig to mirror."); return; }

            UndoCallback?.Invoke();
            foreach (var b in rig.Bones)
            {
                if (b == null) continue;
                if (mirrorAcrossX)
                {
                    // Flip horizontally: negate X translation and reflect rotation about Y axis.
                    b.LocalX = -b.LocalX;
                    b.LocalRotation = (float)(Math.PI - b.LocalRotation);
                }
                else
                {
                    // Flip vertically: negate Y translation and reflect rotation about X axis.
                    b.LocalY = -b.LocalY;
                    b.LocalRotation = -b.LocalRotation;
                }
                if (_canvas != null && _canvas.AnimationPreviewEnabled)
                    AutoKeyBoneAtCurrentTime(b);
            }
            ChangedCallback?.Invoke();
            RefreshSelectedBoneInspector();
            _canvas?.Invalidate();
            SetStatus(mirrorAcrossX ? "Mirrored pose horizontally." : "Mirrored pose vertically.");
        }

        private void CopyRigCodeToClipboard()
        {
            try
            {
                string code = Services.RigCodeGenerator.Generate(_layout);
                ShowCodeDialog("Rig Runtime Code", code);
                SetStatus("Rig runtime code generated.");
            }
            catch (Exception ex)
            {
                SetStatus("Generate failed: " + ex.Message);
                MessageBox.Show(this, "Generate failed:\r\n" + ex, "Rig Code", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Asks the host to merge a freshly-generated rig snippet into the main code panel.
        /// The host owns code-panel state, source tracking, undo, and write-back, so we
        /// just delegate via <see cref="InjectCallback"/> and surface the returned status.
        /// </summary>
        private void InjectRigCodeRequest()
        {
            if (InjectCallback == null)
            {
                SetStatus("Injection not wired by host.");
                return;
            }
            try
            {
                string status = InjectCallback();
                if (!string.IsNullOrEmpty(status)) SetStatus(status);
            }
            catch (Exception ex)
            {
                SetStatus("Inject failed: " + ex.Message);
                MessageBox.Show(this, "Inject failed:\r\n" + ex, "Rig Code", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowCodeDialog(string title, string code)
        {
            using (var dlg = new Form
            {
                Text = title,
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(900, 700),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(220, 220, 220),
                ShowInTaskbar = false,
                MinimizeBox = false,
                MaximizeBox = true,
            })
            {
                var tb = new TextBox
                {
                    Multiline = true,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    Dock = DockStyle.Fill,
                    Font = new Font("Consolas", 9.5f),
                    BackColor = Color.FromArgb(20, 20, 20),
                    ForeColor = Color.FromArgb(220, 220, 220),
                    ReadOnly = false,
                    Text = code,
                };
                var bottom = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    Height = 36,
                    FlowDirection = FlowDirection.RightToLeft,
                    Padding = new Padding(6),
                    BackColor = Color.FromArgb(30, 30, 30),
                };
                var btnClose = MakeBtn("Close", () => dlg.Close());
                var btnCopy = MakeBtn("Copy to Clipboard", () =>
                {
                    try { Clipboard.SetText(tb.Text); SetStatus("Copied to clipboard."); }
                    catch (Exception ex) { MessageBox.Show(dlg, ex.Message, "Copy failed"); }
                });
                bottom.Controls.Add(btnClose);
                bottom.Controls.Add(btnCopy);
                dlg.Controls.Add(tb);
                dlg.Controls.Add(bottom);
                dlg.ShowDialog(this);
            }
        }

        private void RefreshAnimationPanel()
        {
            _suppress = true;
            try
            {
                _cmbClip.Items.Clear();
                var rig = SelectedRig();
                if (rig != null)
                {
                    foreach (var c in rig.Clips) _cmbClip.Items.Add(new ClipItem(c));
                    if (_cmbClip.Items.Count > 0)
                    {
                        int idx = 0;
                        if (!string.IsNullOrEmpty(rig.ActiveClipId))
                        {
                            for (int i = 0; i < _cmbClip.Items.Count; i++)
                                if (((ClipItem)_cmbClip.Items[i]).Clip.Id == rig.ActiveClipId) { idx = i; break; }
                        }
                        _cmbClip.SelectedIndex = idx;
                    }
                }
            }
            finally { _suppress = false; }
            RefreshAnimationInspector();
        }

        private void RefreshAnimationInspector()
        {
            _suppress = true;
            try
            {
                var clip = SelectedClip();
                bool have = clip != null;
                _btnRenameClip.Enabled = _btnDeleteClip.Enabled = have;
                _numClipDuration.Enabled = _chkClipLoop.Enabled = have;
                _trkTime.Enabled = _numTime.Enabled = have;
                _btnPlay.Enabled = _btnStop.Enabled = have;
                _btnSetKey.Enabled = _btnDeleteKey.Enabled = have;
                if (have)
                {
                    _numClipDuration.Value = (decimal)Math.Max(0.05f, Math.Min(600f, clip.Duration));
                    _chkClipLoop.Checked = clip.Loop;
                    _trkTime.Maximum = Math.Max(1, (int)(clip.Duration * 1000f));
                    if (_trkTime.Value > _trkTime.Maximum) _trkTime.Value = _trkTime.Maximum;
                }
            }
            finally { _suppress = false; }
            RefreshKeyInfoLabel();
        }

        private void RefreshKeyInfoLabel()
        {
            if (_lblKeyInfo == null) return;
            var clip = SelectedClip(); var bone = SelectedBone();
            if (clip == null || bone == null) { _lblKeyInfo.Text = "—"; RefreshKeyList(); return; }
            var track = clip.Tracks.Find(x => x.BoneId == bone.Id);
            int n = track?.Keys?.Count ?? 0;
            float t = (float)_numTime.Value;
            const float eps = 0.0005f;
            bool atKey = track != null && track.Keys.Exists(k => Math.Abs(k.Time - t) <= eps);
            _lblKeyInfo.Text = $"{bone.Name}: {n} key(s){(atKey ? "  • on key" : "")}";
            RefreshKeyList();
        }

        private void RefreshKeyList()
        {
            if (_lstKeys == null) return;
            _suppress = true;
            try
            {
                _lstKeys.BeginUpdate();
                _lstKeys.Items.Clear();
                var clip = SelectedClip(); var bone = SelectedBone();
                if (clip != null && bone != null)
                {
                    var track = clip.Tracks.Find(x => x.BoneId == bone.Id);
                    if (track != null && track.Keys != null)
                    {
                        var sorted = new List<RigKeyframe>(track.Keys);
                        sorted.Sort((a, b) => a.Time.CompareTo(b.Time));
                        const float eps = 0.0005f;
                        float currentTime = (float)_numTime.Value;
                        int select = -1;
                        for (int i = 0; i < sorted.Count; i++)
                        {
                            _lstKeys.Items.Add(new KeyItem(sorted[i]));
                            if (Math.Abs(sorted[i].Time - currentTime) <= eps) select = i;
                        }
                        if (select >= 0) _lstKeys.SelectedIndex = select;
                        // Sync easing combo to selected key (or the on-time key if no list selection).
                        if (_cmbEasing != null)
                        {
                            RigKeyframe target = select >= 0 ? sorted[select] : null;
                            if (target != null) _cmbEasing.SelectedItem = target.Easing;
                        }
                    }
                }
                _lstKeys.EndUpdate();
            }
            finally { _suppress = false; }
        }

        /// <summary>Apply the easing combo's value to the keyframe selected in the list (or the key at current time).</summary>
        private void ApplyEasingToSelectedKey()
        {
            var clip = SelectedClip(); var bone = SelectedBone();
            if (clip == null || bone == null || _cmbEasing == null) return;
            var track = clip.Tracks?.Find(x => x.BoneId == bone.Id);
            if (track == null || track.Keys == null || track.Keys.Count == 0) return;
            if (!(_cmbEasing.SelectedItem is RigEasing easing)) return;

            RigKeyframe target = (_lstKeys?.SelectedItem as KeyItem)?.Key;
            if (target == null)
            {
                // Fall back to the closest key to the current time so the combo still
                // works even when nothing is selected in the list.
                float t = (float)_numTime.Value;
                RigKeyframe best = null; float bestDt = float.MaxValue;
                foreach (var k in track.Keys)
                {
                    if (k == null) continue;
                    float dt = Math.Abs(k.Time - t);
                    if (dt < bestDt) { bestDt = dt; best = k; }
                }
                target = best;
            }
            if (target == null) { SetStatus("No keyframe to apply easing to."); return; }
            if (target.Easing == easing) return;

            UndoCallback?.Invoke();
            target.Easing = easing;
            ChangedCallback?.Invoke();
            // Refresh list text (it includes the easing label) without losing selection.
            int sel = _lstKeys?.SelectedIndex ?? -1;
            RefreshKeyList();
            if (_lstKeys != null && sel >= 0 && sel < _lstKeys.Items.Count) _lstKeys.SelectedIndex = sel;
            _canvas?.Invalidate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Stop animation preview when the editor closes; the rig itself stays bound
            // so sprites remain in their rest pose instead of jumping to (0,0).
            StopPlayback();
            if (_canvas != null)
            {
                _canvas.AnimationPreviewEnabled = false;
                _canvas.AnimationPreviewTime = 0f;
                _canvas.OnionSkinEnabled = false;
                _canvas.Invalidate();
            }
            base.OnFormClosing(e);
        }

        private class ClipItem
        {
            public RigClip Clip { get; }
            public ClipItem(RigClip c) { Clip = c; }
            public override string ToString() => Clip?.Name ?? "(null)";
        }

        private class KeyItem
        {
            public RigKeyframe Key { get; }
            public KeyItem(RigKeyframe k) { Key = k; }
            public override string ToString()
            {
                var k = Key;
                return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "T={0,6:0.000}s  Pos({1,7:0.0},{2,7:0.0})  Rot({3,5:0.00})  Scl({4:0.00}×{5:0.00})  Len({6:0.0})  [{7}]",
                    k.Time, k.LocalX, k.LocalY, k.LocalRotation, k.LocalScaleX, k.LocalScaleY, k.Length, k.Easing);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Wrapper items
        // ─────────────────────────────────────────────────────────────────────
        private class RigItem
        {
            public Rig Rig { get; }
            public RigItem(Rig r) { Rig = r; }
            public override string ToString() => Rig?.Name ?? "(null)";
        }

        private class BindingItem
        {
            public SpriteBinding Binding { get; }
            public string Label { get; }
            public BindingItem(SpriteBinding b, string label) { Binding = b; Label = label; }
            public override string ToString() => Label;
        }

        private class ChoiceDialog : Form
        {
            private readonly ListBox _list;
            public int SelectedIndex => _list.SelectedIndex;
            public ChoiceDialog(string title, string prompt, IList<string> options)
            {
                Text = title;
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MinimizeBox = false; MaximizeBox = false;
                Size = new Size(360, 360);
                BackColor = Color.FromArgb(30, 30, 30);
                ForeColor = Color.FromArgb(220, 220, 220);

                var lbl = new Label { Text = prompt, Left = 12, Top = 10, AutoSize = true };
                _list = new ListBox { Left = 12, Top = 32, Width = 320, Height = 240, BackColor = Color.FromArgb(45, 45, 45), ForeColor = ForeColor, BorderStyle = BorderStyle.FixedSingle };
                foreach (var o in options) _list.Items.Add(o);
                if (_list.Items.Count > 0) _list.SelectedIndex = 0;
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 170, Top = 282, Width = 75 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 257, Top = 282, Width = 75 };
                Controls.AddRange(new Control[] { lbl, _list, ok, cancel });
                AcceptButton = ok; CancelButton = cancel;
            }
        }

        /// <summary>
        /// Tiny inline graph that visualises an easing function as f(u) for u in [0,1].
        /// Mirrors <see cref="Services.RigClipSampler"/>'s easing math so what users see
        /// matches what the sampler produces at runtime.
        /// </summary>
        private class EaseCurvePreview : Control
        {
            private RigEasing _easing = RigEasing.Linear;

            public RigEasing Easing
            {
                get => _easing;
                set { if (_easing != value) { _easing = value; Invalidate(); } }
            }

            public EaseCurvePreview()
            {
                DoubleBuffered = true;
                BackColor = Color.FromArgb(28, 28, 28);
                ForeColor = Color.FromArgb(200, 200, 200);
                SetStyle(ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                var rect = ClientRectangle;
                using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, rect);

                // Frame + axes
                using (var frame = new Pen(Color.FromArgb(70, 70, 70)))
                using (var axis  = new Pen(Color.FromArgb(50, 50, 50)))
                {
                    g.DrawRectangle(frame, 0, 0, rect.Width - 1, rect.Height - 1);
                    // baseline + top reference
                    g.DrawLine(axis, 4, rect.Height - 4, rect.Width - 4, rect.Height - 4);
                    g.DrawLine(axis, 4, 4, rect.Width - 4, 4);
                }

                if (rect.Width < 8 || rect.Height < 8) return;

                // Plot f(u) for u in [0,1] across the inner rect.
                int x0 = 4, y0 = rect.Height - 4;
                int x1 = rect.Width - 4, y1 = 4;
                int span = x1 - x0;
                if (span < 2) return;

                using (var pen = new Pen(Color.FromArgb(120, 200, 255), 1.4f))
                {
                    PointF prev = default;
                    bool have = false;
                    for (int i = 0; i <= span; i++)
                    {
                        float u = (float)i / span;
                        float v = Evaluate(u, _easing);
                        float x = x0 + i;
                        float y = y0 + (y1 - y0) * v;
                        var pt = new PointF(x, y);
                        if (have) g.DrawLine(pen, prev, pt);
                        prev = pt; have = true;
                    }
                }
            }

            private static float Evaluate(float u, RigEasing easing)
            {
                if (u < 0f) u = 0f; else if (u > 1f) u = 1f;
                switch (easing)
                {
                    case RigEasing.Step: return u >= 1f ? 1f : 0f;
                    case RigEasing.EaseInOut: return u * u * (3f - 2f * u);
                    case RigEasing.Linear:
                    default: return u;
                }
            }
        }
    }
}
