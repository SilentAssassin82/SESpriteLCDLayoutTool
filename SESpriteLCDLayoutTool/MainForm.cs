using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Xml.Serialization;
using SESpriteLCDLayoutTool.Controls;
using SESpriteLCDLayoutTool.Data;
using SESpriteLCDLayoutTool.Models;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool
{
    public class MainForm : Form
    {
        // ── Left panel ────────────────────────────────────────────────────────────
        private TreeView _spriteTree;
        private TextBox  _txtCustomName;
        private Button   _btnAddSprite;
        private Button   _btnAddText;

        // ── Centre ────────────────────────────────────────────────────────────────
        private LcdCanvas _canvas;

        // ── Properties panel ──────────────────────────────────────────────────────
        private NumericUpDown _numX, _numY, _numW, _numH;
        private NumericUpDown _numRotScale;
        private Label         _lblRotScale;
        private Button        _btnColor;
        private Panel         _colorPreview;
        private TrackBar      _trackAlpha;
        private Label         _lblAlpha;
        private FlowLayoutPanel _exprColorPanel;
        private Label           _exprColorLabel;
        private GroupBox      _grpText;
        private TextBox       _txtText;
        private ComboBox      _cmbFont;
        private ComboBox      _cmbAlignment;
        private ListBox       _lstLayers;

        // ── Code panel ────────────────────────────────────────────────────────────
        private RichTextBox _codeBox;
        private ComboBox    _cmbCodeStyle;
        private TextBox     _execCallBox;
        private Label       _execResultLabel;
        private ListBox     _lstDetectedCalls;

        // ── Split containers (distances set on Load) ──────────────────────────────
        private SplitContainer _mainSplit, _workSplit, _topSplit;

        // ── Status bar ────────────────────────────────────────────────────────────
        private ToolStripStatusLabel _statusLabel;

        // ── State ────────────────────────────────────────────────────────────────
        private LcdLayout _layout;
        private bool      _updatingProps;
        private string    _currentFile;
        private readonly UndoManager _undo = new UndoManager();
        private SpriteTextureCache _textureCache;

        // ── Live stream ──────────────────────────────────────────────────────────
        private LivePipeListener _pipeListener;
        private ToolStripMenuItem _mnuListenToggle;
        private ToolStripMenuItem _mnuPauseToggle;
        private ToolStripMenuItem _mnuCaptureSnapshot;
        private Button _btnCaptureSnapshot;

        // ── File-based live stream ───────────────────────────────────────────────
        private LiveFileWatcher _fileWatcher;
        private ToolStripMenuItem _mnuFileWatchToggle;

        // ── Clipboard watcher (PB workflow) ──────────────────────────────────────
        private System.Windows.Forms.Timer _clipboardTimer;
        private int _lastClipboardHash;
        private ToolStripMenuItem _mnuClipboardToggle;
        private bool _liveUndoPushed;

        // ── Live stream state ─────────────────────────────────────────────────────
        // Code-tracked sprites saved before streaming replaces _layout.Sprites.
        // Restored on pause (with merged live positions/colours) so the round-trip
        // patcher can work.  Cleared when streaming stops entirely.
        private List<SpriteEntry> _preLiveCodeSprites;
        private List<SpriteEntry> _lastLiveFrame;

        // ── Isolated call view ────────────────────────────────────────────────
        // When the user double-clicks a detected call, we execute just that call
        // and dim all other sprites.  _fullFrameSprites holds the complete set so
        // "Show All" can restore the full view.
        private List<SpriteEntry> _fullFrameSprites;
        private HashSet<SpriteEntry> _isolatedCallSprites;
        private Button _btnShowAll;

        // ─────────────────────────────────────────────────────────────────────────
        public MainForm()
        {
            Text            = "SE Sprite LCD Layout Tool";
            Size            = new Size(1340, 860);
            MinimumSize     = new Size(960, 640);
            StartPosition   = FormStartPosition.CenterScreen;
            Font            = new Font("Segoe UI", 9f);
            BackColor       = Color.FromArgb(30, 30, 30);
            ForeColor       = Color.FromArgb(220, 220, 220);

            BuildUI();
            UserSpriteCatalog.Load();
            PopulateSpriteTree();
            AppSettings.Load();
            LoadSpriteTextures(AppSettings.GameContentPath ?? AppSettings.AutoDetectContentPath());
            NewLayout();

            Load += (s, e) =>
            {
                // Clamp to screen working area so the window never starts behind the taskbar
                // (e.g. 1366×768 laptops where the default 860px height would be clipped).
                var wa = Screen.FromControl(this).WorkingArea;
                if (Width  > wa.Width)  Width  = wa.Width;
                if (Height > wa.Height) Height = wa.Height;
                Location = new Point(
                    wa.X + Math.Max(0, (wa.Width  - Width)  / 2),
                    wa.Y + Math.Max(0, (wa.Height - Height) / 2));

                // MinSize must be set after layout so internal SplitterDistance validation has real dimensions.
                _mainSplit.Panel1MinSize    = 160;
                _mainSplit.Panel2MinSize    = 300;
                _mainSplit.SplitterDistance = Math.Max(160, Math.Min(_mainSplit.Width - 300 - _mainSplit.SplitterWidth, 210));
                _mainSplit.FixedPanel       = FixedPanel.Panel1;   // sprite palette stays fixed width

                _workSplit.Panel1MinSize    = 200;
                _workSplit.Panel2MinSize    = 180;
                _workSplit.SplitterDistance = Math.Max(200, Math.Min(_workSplit.Height - 180 - _workSplit.SplitterWidth, 560));
                _workSplit.FixedPanel       = FixedPanel.Panel2;   // code panel stays fixed height

                _topSplit.Panel1MinSize     = 200;
                _topSplit.Panel2MinSize     = 240;
                _topSplit.SplitterDistance  = Math.Max(200, _topSplit.Width - 260 - _topSplit.SplitterWidth);
                _topSplit.FixedPanel        = FixedPanel.Panel2;   // properties panel stays fixed width
            };
        }

        // ── UI construction ───────────────────────────────────────────────────────
        private void BuildUI()
        {
            // Menu bar
            var menuStrip = BuildMenuStrip();
            Controls.Add(menuStrip);
            MainMenuStrip = menuStrip;

            // Status bar
            var statusStrip = new StatusStrip { BackColor = Color.FromArgb(45, 45, 48) };
            _statusLabel = new ToolStripStatusLabel("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusStrip.Items.Add(_statusLabel);
            Controls.Add(statusStrip);

            // Main horizontal split: sprite tree | work area
            var mainSplit = new SplitContainer
            {
                Dock        = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                BackColor   = Color.FromArgb(30, 30, 30),
                BorderStyle = BorderStyle.None,
            };
            Controls.Add(mainSplit);
            mainSplit.BringToFront();   // Ensure Fill is laid out after Top/Bottom docked controls

            mainSplit.Panel1.Controls.Add(BuildLeftPanel());

            // Work area: vertical split — top (canvas+props) | bottom (code)
            var workSplit = new SplitContainer
            {
                Dock        = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                BackColor   = Color.FromArgb(30, 30, 30),
                BorderStyle = BorderStyle.None,
            };
            mainSplit.Panel2.Controls.Add(workSplit);

            // Top work area: canvas | properties
            var topSplit = new SplitContainer
            {
                Dock        = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                BackColor   = Color.FromArgb(30, 30, 30),
                BorderStyle = BorderStyle.None,
            };
            workSplit.Panel1.Controls.Add(topSplit);

            // Store splits for Load-time sizing
            _mainSplit = mainSplit;
            _workSplit = workSplit;
            _topSplit  = topSplit;

            // Canvas
            _canvas = new LcdCanvas { Dock = DockStyle.Fill };
            _canvas.SelectionChanged += OnSelectionChanged;
            _canvas.SpriteModified   += OnSpriteModified;
            _canvas.DragStarting     += (ss, ee) => PushUndo();
            _canvas.DragCompleted    += OnDragCompleted;
            _canvas.ContextMenuStrip  = BuildCanvasContextMenu();
            topSplit.Panel1.Controls.Add(_canvas);

            topSplit.Panel2.Controls.Add(BuildPropertiesPanel());

            // Code output
            workSplit.Panel2.Controls.Add(BuildCodePanel());
        }

        private MenuStrip BuildMenuStrip()
        {
            var ms = new MenuStrip { BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(220, 220, 220) };
            ms.Renderer = new DarkMenuRenderer();

            var file = new ToolStripMenuItem("File");
            file.DropDownItems.Add("New",        null, (s, e) => NewLayout());
            file.DropDownItems.Add("Open...",    null, (s, e) => OpenLayout());
            file.DropDownItems.Add("Save",       null, (s, e) => SaveLayout(false));
            file.DropDownItems.Add("Save As...", null, (s, e) => SaveLayout(true));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add("Import Sprite List...", null, (s, e) => ShowImportSpriteDialog());
            file.DropDownItems.Add("Clear Imported Sprites", null, (s, e) => ClearImportedSprites());
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add("Exit",       null, (s, e) => Close());
            ms.Items.Add(file);

            var edit = new ToolStripMenuItem("Edit");
            edit.DropDownItems.Add("Undo\tCtrl+Z",                null, (s, e) => PerformUndo());
            edit.DropDownItems.Add("Redo\tCtrl+Y",                null, (s, e) => PerformRedo());
            edit.DropDownItems.Add(new ToolStripSeparator());
            edit.DropDownItems.Add("Paste Layout Code…\tCtrl+V",  null, (s, e) => ShowPasteLayoutDialog());
            edit.DropDownItems.Add("Apply Runtime Snapshot…",      null, (s, e) => ShowApplySnapshotDialog());
            edit.DropDownItems.Add(new ToolStripSeparator());
            _mnuListenToggle = new ToolStripMenuItem("Start Live Listening", null, (s, e) => ToggleLiveListening());
            edit.DropDownItems.Add(_mnuListenToggle);
            _mnuPauseToggle = new ToolStripMenuItem("Pause Live Stream", null, (s, e) => ToggleLivePause()) { Enabled = false };
            edit.DropDownItems.Add(_mnuPauseToggle);
            _mnuCaptureSnapshot = new ToolStripMenuItem("Capture Live Snapshot", null, (s, e) => ApplyLiveSnapshot()) { Enabled = false };
            edit.DropDownItems.Add(_mnuCaptureSnapshot);
            _mnuFileWatchToggle = new ToolStripMenuItem("Watch Snapshot File…", null, (s, e) => ToggleFileWatching());
            edit.DropDownItems.Add(_mnuFileWatchToggle);
            _mnuClipboardToggle = new ToolStripMenuItem("Watch Clipboard (PB)…", null, (s, e) => ToggleClipboardWatching());
            edit.DropDownItems.Add(_mnuClipboardToggle);
            edit.DropDownItems.Add(new ToolStripSeparator());
            edit.DropDownItems.Add("Duplicate\tCtrl+D",           null, (s, e) => DuplicateSelected());
            edit.DropDownItems.Add("Delete Selected\tDel",        null, (s, e) => DeleteSelected());
            edit.DropDownItems.Add(new ToolStripSeparator());
            edit.DropDownItems.Add("Center on Surface",           null, (s, e) => CenterSelectedOnSurface());
            edit.DropDownItems.Add("Layer Up\tCtrl+]",            null, (s, e) => { PushUndo(); _canvas.MoveSelectedUp();   RefreshLayerList(); RefreshCode(); });
            edit.DropDownItems.Add("Layer Down\tCtrl+[",          null, (s, e) => { PushUndo(); _canvas.MoveSelectedDown(); RefreshLayerList(); RefreshCode(); });
            ms.Items.Add(edit);

            var view = new ToolStripMenuItem("View");
            var snapItem = new ToolStripMenuItem("Snap to Grid\tCtrl+G") { CheckOnClick = true };
            snapItem.CheckedChanged += (s, e) => { _canvas.SnapToGrid = snapItem.Checked; SetStatus(snapItem.Checked ? "Snap to grid enabled" : "Snap to grid disabled"); };
            view.DropDownItems.Add(snapItem);
            view.DropDownItems.Add(new ToolStripSeparator());
            foreach (int gs in new[] { 8, 16, 32, 64 })
            {
                int size = gs;
                view.DropDownItems.Add($"Grid Size: {gs}px", null, (s, e) => { _canvas.GridSize = size; SetStatus($"Grid size set to {size}px"); });
            }
            view.DropDownItems.Add(new ToolStripSeparator());
            view.DropDownItems.Add("Zoom In\tCtrl+=",   null, (s, e) => { _canvas.Zoom *= 1.25f; });
            view.DropDownItems.Add("Zoom Out\tCtrl+-",  null, (s, e) => { _canvas.Zoom /= 1.25f; });
            view.DropDownItems.Add("Reset View\tCtrl+0", null, (s, e) => { _canvas.ResetView(); });
            view.DropDownItems.Add(new ToolStripSeparator());
            view.DropDownItems.Add("Set SE Game Path...", null, (s, e) => BrowseGamePath());
            view.DropDownItems.Add("Auto-Detect Game Path", null, (s, e) => AutoDetectGamePath());
            view.DropDownItems.Add("Unload Textures", null, (s, e) => UnloadSpriteTextures());
            ms.Items.Add(view);

            var surface = new ToolStripMenuItem("Surface Size");
            for (int i = 0; i < SpriteCatalog.SurfacePresetNames.Length; i++)
            {
                int idx = i;
                surface.DropDownItems.Add(SpriteCatalog.SurfacePresetNames[i], null, (s, e) => ApplySurfacePreset(idx));
            }
            ms.Items.Add(surface);

            return ms;
        }

        // ── Left panel (sprite palette) ───────────────────────────────────────────
        private Panel BuildLeftPanel()
        {
            // Use a 3-row TableLayoutPanel so the toolbar row always has a guaranteed
            // pixel height. DockStyle.Bottom inside a padded Panel was unreliable —
            // the Panel.Padding shifts DisplayRectangle upward and WinForms clips the
            // last row of the bottom-docked child, hiding "Add Text Sprite" no matter
            // how the window is sized.
            var tbl = new TableLayoutPanel
            {
                Dock        = DockStyle.Fill,
                ColumnCount = 1,
                RowCount    = 3,
                BackColor   = Color.FromArgb(37, 37, 38),
                Padding     = new Padding(3, 3, 3, 0),
                Margin      = new Padding(0),
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));  // header
            tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));  // tree (fills)
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 155f)); // toolbar

            var header = new Label
            {
                Text      = "SPRITE PALETTE",
                Dock      = DockStyle.Fill,
                ForeColor = Color.FromArgb(140, 140, 140),
                Font      = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                Padding   = new Padding(2, 6, 0, 0),
            };

            _spriteTree = new TreeView
            {
                Dock        = DockStyle.Fill,
                BackColor   = Color.FromArgb(37, 37, 38),
                ForeColor   = Color.FromArgb(215, 215, 215),
                BorderStyle = BorderStyle.None,
                Font        = new Font("Segoe UI", 9f),
                ShowLines   = true,
            };
            _spriteTree.NodeMouseDoubleClick += (s, e) => AddSelectedTreeSprite();
            _spriteTree.NodeMouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                    _spriteTree.SelectedNode = e.Node;
            };
            _spriteTree.ContextMenuStrip = BuildSpriteTreeContextMenu();

            // Bottom toolbar — 4 rows, all with explicit heights, Dock=Fill inside cell
            var bottomTable = new TableLayoutPanel
            {
                Dock        = DockStyle.Fill,
                ColumnCount = 1,
                RowCount    = 4,
                BackColor   = Color.FromArgb(37, 37, 38),
                Padding     = new Padding(0),
                Margin      = new Padding(0),
            };
            bottomTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottomTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            bottomTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            bottomTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            bottomTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

            var lblCustom = new Label
            {
                Text      = "Custom sprite name:",
                ForeColor = Color.FromArgb(150, 150, 150),
                Font      = new Font("Segoe UI", 7.5f),
                Dock      = DockStyle.Fill,
                TextAlign = ContentAlignment.BottomLeft,
                Margin    = Padding.Empty,
            };

            _txtCustomName = new TextBox
            {
                Dock        = DockStyle.Fill,
                BackColor   = Color.FromArgb(28, 28, 28),
                ForeColor   = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font        = new Font("Consolas", 9f),
                Margin      = Padding.Empty,
            };

            _btnAddSprite = DarkButton("Add Texture Sprite", Color.FromArgb(0, 100, 180));
            _btnAddSprite.Dock   = DockStyle.Fill;
            _btnAddSprite.Margin = Padding.Empty;
            _btnAddSprite.Click += (s, e) => AddSelectedTreeSprite();

            _btnAddText = DarkButton("Add Text Sprite", Color.FromArgb(80, 80, 0));
            _btnAddText.Dock   = DockStyle.Fill;
            _btnAddText.Margin = Padding.Empty;
            _btnAddText.Click += (s, e) => {
                string font = _cmbFont.SelectedItem?.ToString() ?? "White";
                PushUndo();
                var sp = _canvas.AddSprite("Text", isText: true);
                if (sp != null) { sp.FontId = font; OnSelectionChanged(_canvas, EventArgs.Empty); }
                RefreshLayerList(); RefreshCode();
            };

            bottomTable.Controls.Add(lblCustom,      0, 0);
            bottomTable.Controls.Add(_txtCustomName,  0, 1);
            bottomTable.Controls.Add(_btnAddSprite,   0, 2);
            bottomTable.Controls.Add(_btnAddText,     0, 3);

            tbl.Controls.Add(header,      0, 0);
            tbl.Controls.Add(_spriteTree, 0, 1);
            tbl.Controls.Add(bottomTable, 0, 2);

            return tbl;
        }

        // ── Properties panel ──────────────────────────────────────────────────────
        private Panel BuildPropertiesPanel()
        {
            var outer = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 48), Padding = new Padding(3) };

            var header = new Label
            {
                Text      = "PROPERTIES",
                Dock      = DockStyle.Top,
                Height    = 24,
                ForeColor = Color.FromArgb(140, 140, 140),
                Font      = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                Padding   = new Padding(2, 6, 0, 0),
            };

            // Layer list at bottom
            var lblLayers = new Label
            {
                Text      = "LAYER ORDER (bottom → top)",
                Dock      = DockStyle.Bottom,
                Height    = 18,
                ForeColor = Color.FromArgb(140, 140, 140),
                Font      = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                Padding   = new Padding(2, 4, 0, 0),
            };
            _lstLayers = new ListBox
            {
                Dock        = DockStyle.Bottom,
                Height      = 100,
                BackColor   = Color.FromArgb(30, 30, 30),
                ForeColor   = Color.FromArgb(215, 215, 215),
                BorderStyle = BorderStyle.None,
                Font        = new Font("Segoe UI", 8.5f),
            };
            _lstLayers.SelectedIndexChanged += OnLayerListSelectionChanged;
            _lstLayers.MouseDoubleClick  += OnLayerListDoubleClick;
            _lstLayers.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    int idx = _lstLayers.IndexFromPoint(e.Location);
                    if (idx >= 0) _lstLayers.SelectedIndex = idx;
                }
            };
            _lstLayers.ContextMenuStrip = BuildLayerContextMenu();

            // Scrollable properties area
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(45, 45, 48) };

            var flow = new FlowLayoutPanel
            {
                Width         = 230,
                AutoSize      = true,
                AutoSizeMode  = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents  = false,
                BackColor     = Color.FromArgb(45, 45, 48),
                Padding       = new Padding(2),
            };

            // ── Position ──────────────────────────────────────────────────────────
            flow.Controls.Add(SectionLabel("POSITION", 230));
            var posRow = MakeLabeledNumRow("X", out _numX, "Y", out _numY, 230, -8192, 8192, 0);
            _numX.ValueChanged += OnPropChanged;
            _numY.ValueChanged += OnPropChanged;
            flow.Controls.Add(posRow);

            // ── Size ──────────────────────────────────────────────────────────────
            flow.Controls.Add(SectionLabel("SIZE", 230));
            var sizeRow = MakeLabeledNumRow("W", out _numW, "H", out _numH, 230, 1, 8192, 0);
            _numW.ValueChanged += OnPropChanged;
            _numH.ValueChanged += OnPropChanged;
            flow.Controls.Add(sizeRow);

            // ── Rotation / Scale (shared) ─────────────────────────────────────────
            _lblRotScale = SectionLabel("ROTATION (radians)", 230);
            flow.Controls.Add(_lblRotScale);
            _numRotScale = new NumericUpDown
            {
                Width         = 230,
                Height        = 24,
                Minimum       = -7,
                Maximum       = 7,
                DecimalPlaces = 3,
                Increment     = 0.05M,
                BackColor     = Color.FromArgb(30, 30, 30),
                ForeColor     = Color.White,
            };
            _numRotScale.ValueChanged += OnPropChanged;
            flow.Controls.Add(_numRotScale);

            // ── Color ─────────────────────────────────────────────────────────────
            flow.Controls.Add(SectionLabel("COLOR", 230));
            var colorRow = new FlowLayoutPanel { Width = 230, Height = 28, BackColor = Color.Transparent, FlowDirection = FlowDirection.LeftToRight };
            _colorPreview = new Panel { Width = 28, Height = 24, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Cursor = Cursors.Hand };
            _colorPreview.Click += OnColorClick;
            _btnColor = DarkButton("Pick Color", Color.FromArgb(70, 70, 70));
            _btnColor.Size = new Size(90, 24);
            _btnColor.Click += OnColorClick;
            colorRow.Controls.Add(_colorPreview);
            colorRow.Controls.Add(_btnColor);
            flow.Controls.Add(colorRow);

            // Alpha
            flow.Controls.Add(SectionLabel("ALPHA", 230));
            var alphaRow = new FlowLayoutPanel { Width = 230, Height = 26, BackColor = Color.Transparent, FlowDirection = FlowDirection.LeftToRight };
            _trackAlpha = new TrackBar { Width = 175, Height = 24, Minimum = 0, Maximum = 255, Value = 255, TickFrequency = 32, SmallChange = 1, BackColor = Color.FromArgb(45, 45, 48) };
            _trackAlpha.ValueChanged += OnAlphaChanged;
            _lblAlpha = new Label { Text = "255", Width = 36, Height = 24, ForeColor = Color.FromArgb(180, 180, 180), TextAlign = ContentAlignment.MiddleLeft };
            alphaRow.Controls.Add(_trackAlpha);
            alphaRow.Controls.Add(_lblAlpha);
            flow.Controls.Add(alphaRow);

            // ── Expression Colors (source-code color literals) ───────────────────
            _exprColorLabel = SectionLabel("SOURCE COLORS", 230);
            _exprColorLabel.Visible = false;
            flow.Controls.Add(_exprColorLabel);
            _exprColorPanel = new FlowLayoutPanel
            {
                Width         = 230,
                AutoSize      = true,
                MinimumSize   = new Size(230, 0),
                MaximumSize   = new Size(230, 200),
                BackColor     = Color.Transparent,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents  = true,
                Visible       = false,
            };
            flow.Controls.Add(_exprColorPanel);

            // ── Text Properties ───────────────────────────────────────────────────
            _grpText = new GroupBox
            {
                Text      = "Text Properties",
                Width     = 228,
                Height    = 148,
                ForeColor = Color.FromArgb(160, 160, 160),
                Font      = new Font("Segoe UI", 8f, FontStyle.Bold),
                BackColor = Color.Transparent,
                Padding   = new Padding(4),
            };

            var textFlow = new FlowLayoutPanel
            {
                Dock          = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents  = false,
                BackColor     = Color.Transparent,
            };

            _txtText = new TextBox
            {
                Width       = 200,
                Height      = 22,
                BackColor   = Color.FromArgb(30, 30, 30),
                ForeColor   = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
            };
            _txtText.TextChanged += OnPropChanged;

            var fontRow = new FlowLayoutPanel { Width = 210, Height = 26, BackColor = Color.Transparent, FlowDirection = FlowDirection.LeftToRight };
            fontRow.Controls.Add(new Label { Text = "Font:", Width = 36, Height = 22, ForeColor = Color.FromArgb(180, 180, 180), TextAlign = ContentAlignment.MiddleLeft });
            _cmbFont = new ComboBox { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.White };
            _cmbFont.Items.AddRange(SpriteCatalog.Fonts);
            _cmbFont.SelectedIndex = 0;
            _cmbFont.SelectedIndexChanged += OnFontChanged;
            fontRow.Controls.Add(_cmbFont);

            var alignRow = new FlowLayoutPanel { Width = 210, Height = 26, BackColor = Color.Transparent, FlowDirection = FlowDirection.LeftToRight };
            alignRow.Controls.Add(new Label { Text = "Align:", Width = 42, Height = 22, ForeColor = Color.FromArgb(180, 180, 180), TextAlign = ContentAlignment.MiddleLeft });
            _cmbAlignment = new ComboBox { Width = 100, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.White };
            _cmbAlignment.Items.AddRange(new object[] { "Left", "Center", "Right" });
            _cmbAlignment.SelectedIndex = 1;
            _cmbAlignment.SelectedIndexChanged += OnPropChanged;
            alignRow.Controls.Add(_cmbAlignment);

            textFlow.Controls.Add(_txtText);
            textFlow.Controls.Add(fontRow);
            textFlow.Controls.Add(alignRow);
            _grpText.Controls.Add(textFlow);
            flow.Controls.Add(_grpText);

            scroll.Controls.Add(flow);

            // Resize (not ClientSizeChanged) avoids scroll-bar oscillation: always subtract
            // the vertical scrollbar width so horizontal scroll never appears.
            scroll.Resize += (ss, ee) =>
            {
                int w = Math.Max(150, scroll.Width - SystemInformation.VerticalScrollBarWidth - 6);
                flow.Width = w;
                foreach (Control c in flow.Controls)
                    c.Width = w;
                if (_trackAlpha != null)
                    _trackAlpha.Width = Math.Max(80, w - 50);
            };

            outer.Controls.Add(scroll);
            outer.Controls.Add(_lstLayers);
            outer.Controls.Add(lblLayers);
            outer.Controls.Add(header);
            return outer;
        }

        // ── Code output panel ─────────────────────────────────────────────────────
        private void OnExecCodeClick(object sender, EventArgs e)
        {
            if (_layout == null) { SetStatus("No layout loaded."); return; }

            string code = _codeBox.Text;
            if (string.IsNullOrWhiteSpace(code)) { SetStatus("Code panel is empty."); return; }

            // Use stored call expression, or auto-detect from the code
            string call = _execCallBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(call))
            {
                call = CodeExecutor.DetectCallExpression(code);
                if (call == null)
                {
                    var st = CodeExecutor.DetectScriptType(code);
                    string hint = st == ScriptType.ProgrammableBlock
                        ? "Could not auto-detect a Main() entry point in this PB script.\n\n"
                          + "Type the call expression in the '▶ Call:' box below, e.g.:\n"
                          + "  Main(\"\", UpdateType.None)"
                        : st == ScriptType.ModSurface
                        ? "Could not auto-detect a render method with an IMyTextSurface parameter.\n\n"
                          + "Type the call expression in the '▶ Call:' box below, e.g.:\n"
                          + "  DrawHUD(surface)"
                        : "Could not auto-detect a render method with a List<MySprite> parameter.\n\n"
                          + "Type the call expression in the '▶ Call:' box below, e.g.:\n"
                          + "  RenderPanel(sprites, 512f, 10f, 1f)";
                    MessageBox.Show(hint,
                        "No Method Found", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    _execCallBox.Focus();
                    return;
                }
                _execCallBox.Text = call;
            }

            _execResultLabel.Text      = "Running…";
            _execResultLabel.ForeColor = Color.FromArgb(200, 180, 60);
            Refresh();

            var result = CodeExecutor.Execute(code, call);
            if (!result.Success)
            {
                _execResultLabel.Text      = "✗ Error";
                _execResultLabel.ForeColor = Color.FromArgb(220, 80, 80);
                MessageBox.Show(result.Error, "Execution Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string typeTag = result.ScriptType == ScriptType.ProgrammableBlock ? " [PB]"
                           : result.ScriptType == ScriptType.ModSurface        ? " [Mod]"
                           : "";
            _execResultLabel.Text      = "✔ " + result.Sprites.Count + typeTag;
            _execResultLabel.ForeColor = Color.FromArgb(80, 220, 100);

            PushUndo();

            // When source-tracked sprites exist, merge execution results as a
            // snapshot so round-trip editing and click-to-jump continue to work.
            if (_layout.OriginalSourceCode != null)
            {
                var editable = new List<SpriteEntry>();
                foreach (var sp in _layout.Sprites)
                    if (!sp.IsReferenceLayout && sp.SourceStart >= 0 && sp.ImportBaseline != null)
                        editable.Add(sp);

                if (editable.Count > 0)
                {
                    var mergeResult = SnapshotMerger.Merge(editable, result.Sprites, applyColors: true);

                    // Update baselines to lock in executed positions for round-trip code generation
                    foreach (var sp in editable)
                    {
                        if (sp.ImportBaseline != null)
                            sp.ImportBaseline = sp.CloneValues();
                    }

                    // Add orphan sprites (loop/expression-generated) as untracked entries
                    foreach (var orphan in mergeResult.UnmatchedSnapshots)
                    {
                        orphan.SourceStart = -1;
                        orphan.SourceEnd   = -1;
                        _layout.Sprites.Add(orphan);
                    }

                    _canvas.SelectedSprite = editable.Count > 0 ? editable[0] : null;
                    _canvas.Invalidate();
                    RefreshLayerList();
                    RefreshCode();
                    SetStatus($"Executed — {mergeResult.Summary}");
                    return;
                }
            }

            // No source tracking — full replacement
            _layout.Sprites.Clear();
            foreach (var sp in result.Sprites)
                _layout.Sprites.Add(sp);

            _canvas.SelectedSprite = result.Sprites.Count > 0 ? result.Sprites[0] : null;
            _canvas.Invalidate();
            RefreshLayerList();
            RefreshCode();
            SetStatus($"Executed — {result.Sprites.Count} sprite(s) captured.");
        }

        private Panel BuildCodePanel()
        {
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 20, 20), Padding = new Padding(3) };

            var toolbar = new FlowLayoutPanel
            {
                Dock          = DockStyle.Top,
                Height        = 36,
                BackColor     = Color.FromArgb(30, 30, 30),
                FlowDirection = FlowDirection.LeftToRight,
                Padding       = new Padding(4, 4, 4, 0),
            };

            var lblCode = new Label { Text = "Generated C# Code", Width = 150, Height = 26, ForeColor = Color.FromArgb(150, 200, 255), TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };

            var btnCopy = DarkButton("Copy to Clipboard", Color.FromArgb(0, 100, 180));
            btnCopy.Size   = new Size(140, 26);
            btnCopy.Click += (s, e) =>
            {
                if (string.IsNullOrEmpty(_codeBox.Text)) { SetStatus("Nothing to copy."); return; }
                Clipboard.SetText(_codeBox.Text);
                SetStatus("Code copied to clipboard!");
            };

            var btnRefresh = DarkButton("⟳ Generate Code", Color.FromArgb(0, 122, 60));
            btnRefresh.Size   = new Size(120, 26);
            btnRefresh.Click += (s, e) => RefreshCode();

            var btnResetSource = DarkButton("Reset Source", Color.FromArgb(120, 60, 0));
            btnResetSource.Size = new Size(95, 26);
            btnResetSource.Click += (s, e) =>
            {
                if (_layout != null) _layout.OriginalSourceCode = null;
                RefreshCode();
                SetStatus("Round-trip source cleared — using generated template.");
            };

            _cmbCodeStyle = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width         = 110,
                Height        = 26,
                BackColor     = Color.FromArgb(45, 45, 48),
                ForeColor     = Color.FromArgb(220, 220, 220),
                FlatStyle     = FlatStyle.Flat,
            };
            _cmbCodeStyle.Items.AddRange(new object[] { "In-Game (PB)", "Mod", "Plugin / Torch" });
            _cmbCodeStyle.SelectedIndex = 0;
            _cmbCodeStyle.SelectedIndexChanged += (s, e) => RefreshCode();

            _btnCaptureSnapshot = DarkButton("📷 Capture Snapshot", Color.FromArgb(140, 90, 0));
            _btnCaptureSnapshot.Size = new Size(140, 26);
            _btnCaptureSnapshot.Visible = false;
            _btnCaptureSnapshot.Click += (s, e) => ApplyLiveSnapshot();

            _btnShowAll = DarkButton("👁 Show All", Color.FromArgb(40, 110, 60));
            _btnShowAll.Size = new Size(100, 26);
            _btnShowAll.Visible = false;
            _btnShowAll.Click += (s, e) => RestoreFullView();

            toolbar.Controls.Add(lblCode);
            toolbar.Controls.Add(btnCopy);
            toolbar.Controls.Add(btnRefresh);
            toolbar.Controls.Add(btnResetSource);
            toolbar.Controls.Add(_cmbCodeStyle);
            toolbar.Controls.Add(_btnCaptureSnapshot);
            toolbar.Controls.Add(_btnShowAll);

            _codeBox = new RichTextBox
            {
                Dock          = DockStyle.Fill,
                BackColor     = Color.FromArgb(14, 14, 14),
                ForeColor     = Color.FromArgb(212, 212, 212),
                Font          = new Font("Consolas", 9f),
                ReadOnly      = true,
                BorderStyle   = BorderStyle.None,
                ScrollBars    = RichTextBoxScrollBars.Both,
                WordWrap      = false,
                HideSelection = false,
            };

            panel.Controls.Add(_codeBox);
            panel.Controls.Add(toolbar);

            // ── Execute-code bar (bottom of code panel) ───────────────────────────
            var lblExecPrefix = new Label
            {
                Text      = "▶ Call:",
                Dock      = DockStyle.Left,
                AutoSize  = false,
                Width     = 58,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding   = new Padding(6, 0, 0, 0),
                ForeColor = Color.FromArgb(130, 200, 255),
            };
            _execResultLabel = new Label
            {
                Text      = "–",
                Dock      = DockStyle.Right,
                AutoSize  = false,
                Width     = 90,
                TextAlign = ContentAlignment.MiddleRight,
                Padding   = new Padding(0, 0, 6, 0),
                ForeColor = Color.FromArgb(130, 130, 130),
            };
            _execCallBox = new TextBox
            {
                Dock        = DockStyle.Fill,
                Font        = new Font("Consolas", 9f),
                BackColor   = Color.FromArgb(18, 24, 38),
                ForeColor   = Color.FromArgb(160, 220, 255),
                BorderStyle = BorderStyle.FixedSingle,
            };
            var btnExecCode = DarkButton("▶ Execute Code", Color.FromArgb(20, 80, 160));
            btnExecCode.Dock  = DockStyle.Right;
            btnExecCode.Width = 110;
            btnExecCode.Click += OnExecCodeClick;

            var execBar = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = 30,
                BackColor = Color.FromArgb(22, 22, 32),
            };
            execBar.Controls.Add(_execCallBox);
            execBar.Controls.Add(_execResultLabel);
            execBar.Controls.Add(btnExecCode);
            execBar.Controls.Add(lblExecPrefix);
            panel.Controls.Add(execBar);

            // ── Detected calls list (bottom of code panel, above exec bar) ────────
            var lblDetected = new Label
            {
                Text      = "Detected methods (double-click to execute):",
                Dock      = DockStyle.Bottom,
                Height    = 18,
                ForeColor = Color.FromArgb(130, 180, 230),
                Font      = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                Padding   = new Padding(4, 3, 0, 0),
            };
            _lstDetectedCalls = new ListBox
            {
                Dock        = DockStyle.Bottom,
                Height      = 52,
                BackColor   = Color.FromArgb(22, 26, 38),
                ForeColor   = Color.FromArgb(160, 220, 255),
                BorderStyle = BorderStyle.None,
                Font        = new Font("Consolas", 9f),
            };
            _lstDetectedCalls.SelectedIndexChanged += (s, e) =>
            {
                if (_lstDetectedCalls.SelectedItem != null)
                    _execCallBox.Text = _lstDetectedCalls.SelectedItem.ToString();
            };
            _lstDetectedCalls.MouseDoubleClick += (s, e) =>
            {
                if (_lstDetectedCalls.SelectedItem == null) return;
                string call = _lstDetectedCalls.SelectedItem.ToString();
                _execCallBox.Text = call;
                IsolateCallSprites(call);
            };
            panel.Controls.Add(_lstDetectedCalls);
            panel.Controls.Add(lblDetected);

            return panel;
        }

        // ── Sprite tree population ────────────────────────────────────────────────
        private void PopulateSpriteTree()
        {
            _spriteTree.BeginUpdate();
            _spriteTree.Nodes.Clear();

            // ── Texture sprites ───────────────────────────────────────────────────
            foreach (var cat in SpriteCatalog.Categories)
            {
                var catNode = new TreeNode(cat.Name)
                {
                    ForeColor = Color.FromArgb(200, 200, 100),
                    NodeFont  = new Font("Segoe UI", 9f, FontStyle.Bold),
                };
                foreach (var name in cat.Sprites)
                    catNode.Nodes.Add(new TreeNode(name) { ForeColor = Color.FromArgb(215, 215, 215) });

                _spriteTree.Nodes.Add(catNode);
                catNode.Expand();
            }

            // ── Imported sprites from SE ──────────────────────────────────────────
            var importedCats = UserSpriteCatalog.GetCategorised();
            if (importedCats.Count > 0)
            {
                var importHeader = new TreeNode($"── IMPORTED SPRITES ({UserSpriteCatalog.Count} total) ──")
                {
                    ForeColor = Color.FromArgb(130, 220, 130),
                    NodeFont  = new Font("Segoe UI", 8.5f, FontStyle.Bold | FontStyle.Italic),
                    Tag       = "header",
                };
                _spriteTree.Nodes.Add(importHeader);

                foreach (var cat in importedCats)
                {
                    var catNode = new TreeNode($"{cat.Name} ({cat.Sprites.Count})")
                    {
                        ForeColor = Color.FromArgb(140, 210, 140),
                        NodeFont  = new Font("Segoe UI", 9f, FontStyle.Bold),
                    };
                    foreach (var name in cat.Sprites)
                        catNode.Nodes.Add(new TreeNode(name) { ForeColor = Color.FromArgb(200, 215, 200) });
                    _spriteTree.Nodes.Add(catNode);
                }
            }

            // ── Font glyphs (PUA + standard Unicode confirmed by SEGlyphScanner) ─
            var glyphHeader = new TreeNode("── FONT GLYPHS (double-click to add as text sprite) ──")
            {
                ForeColor = Color.FromArgb(100, 180, 255),
                NodeFont  = new Font("Segoe UI", 8.5f, FontStyle.Bold | FontStyle.Italic),
                Tag       = "header",
            };
            _spriteTree.Nodes.Add(glyphHeader);

            foreach (var cat in GlyphCatalog.Categories)
            {
                var catNode = new TreeNode(cat.Name)
                {
                    ForeColor = Color.FromArgb(180, 220, 255),
                    NodeFont  = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                    Tag       = "glyphcat:" + cat.FontHint,
                };

                foreach (var g in cat.Glyphs)
                {
                    // Info-only entry (no character to add)
                    if (g.Character == '\0')
                    {
                        catNode.Nodes.Add(new TreeNode(g.Label)
                        {
                            ForeColor = Color.FromArgb(130, 130, 130),
                            NodeFont  = new Font("Segoe UI", 8f, FontStyle.Italic),
                            Tag       = "info",
                        });
                        continue;
                    }

                    var node = new TreeNode(g.Label)
                    {
                        ForeColor = g.Tintable ? Color.FromArgb(200, 255, 200) : Color.FromArgb(255, 200, 130),
                        Tag       = g,
                    };
                    if (g.Notes != null)
                        node.ToolTipText = g.Notes;
                    catNode.Nodes.Add(node);
                }

                _spriteTree.Nodes.Add(catNode);
                // Leave glyph categories collapsed by default (there are many entries)
            }

            _spriteTree.ShowNodeToolTips = true;
            _spriteTree.EndUpdate();
        }

        // ── Layout management ─────────────────────────────────────────────────────
        private void NewLayout()
        {
            _layout       = new LcdLayout();
            _currentFile  = null;
            _lastLiveFrame = null;
            _fullFrameSprites = null;
            _isolatedCallSprites = null;
            _canvas.HighlightedSprites = null;
            if (_btnShowAll != null) _btnShowAll.Visible = false;
            _canvas.CanvasLayout = _layout;
            RefreshLayerList();
            RefreshCode();
            UpdateTitle();
            SetStatus("New layout — surface 512 × 512. Double-click a sprite in the palette or use Add buttons.");
        }

        private void ApplySurfacePreset(int idx)
        {
            if (_layout == null) return;
            _layout.SurfaceWidth  = SpriteCatalog.SurfacePresetWidths[idx];
            _layout.SurfaceHeight = SpriteCatalog.SurfacePresetHeights[idx];
            _canvas.Invalidate();
            RefreshCode();
            SetStatus($"Surface size set to {_layout.SurfaceWidth} × {_layout.SurfaceHeight}");
        }

        private void DeleteSelected()
        {
            PushUndo();
            _canvas.DeleteSelected();
            RefreshLayerList();
            RefreshCode();
        }

        private void AddSelectedTreeSprite()
        {
            var node = _spriteTree.SelectedNode;

            // Glyph node
            if (node?.Tag is GlyphEntry glyph)
            {
                AddGlyphSprite(glyph);
                return;
            }

            // Texture sprite
            string name = null;
            if (node?.Parent != null)
            {
                // Make sure we're on a leaf (sprite name) not a category or info entry
                if (!(node.Tag is string s && s.StartsWith("glyphcat:")) && !(node.Tag is string h && (h == "header" || h == "info")))
                    name = node.Text;
            }

            if (string.IsNullOrWhiteSpace(name))
                name = _txtCustomName.Text.Trim();

            if (string.IsNullOrWhiteSpace(name))
                name = "SquareSimple";

            PushUndo();
            _canvas.AddSprite(name, isText: false);
            RefreshLayerList();
            RefreshCode();
        }

        private void AddGlyphSprite(GlyphEntry glyph)
        {
            string font = _cmbFont.SelectedItem?.ToString() ?? "White";
            PushUndo();
            var sprite = _canvas.AddSprite(glyph.Character.ToString(), isText: true);
            if (sprite == null) return;
            sprite.Text   = glyph.Character.ToString();
            sprite.FontId = font;
            sprite.Scale  = 1.0f;
            // Re-fire selection so the property panel picks up the correct font/text
            OnSelectionChanged(_canvas, EventArgs.Empty);
            _canvas.Invalidate();
            RefreshLayerList();
            RefreshCode();
            SetStatus($"Added glyph U+{(int)glyph.Character:X4} — font: {sprite.FontId}  {(glyph.Tintable ? "(tintable)" : "(baked/not tintable)")}");
        }

        // ── Events from canvas ────────────────────────────────────────────────────
        private void OnSelectionChanged(object sender, EventArgs e)
        {
            var sp = _canvas.SelectedSprite;
            _updatingProps = true;
            try
            {
                bool hasSel = sp != null;
                bool isText = sp?.Type == SpriteEntryType.Text;

                // Enable/disable property controls
                _numX.Enabled           = hasSel;
                _numY.Enabled           = hasSel;
                _numW.Enabled           = hasSel;
                _numH.Enabled           = hasSel;
                _numRotScale.Enabled    = hasSel;
                _btnColor.Enabled       = hasSel;
                _colorPreview.Enabled   = hasSel;
                _trackAlpha.Enabled     = hasSel;
                _grpText.Enabled        = isText;

                if (sp != null)
                {
                    _numX.Value = (decimal)Math.Round(ClampF(sp.X,      -8192f, 8192f), 1);
                    _numY.Value = (decimal)Math.Round(ClampF(sp.Y,      -8192f, 8192f), 1);
                    _numW.Value = (decimal)Math.Round(ClampF(sp.Width,      1f, 8192f), 1);
                    _numH.Value = (decimal)Math.Round(ClampF(sp.Height,     1f, 8192f), 1);

                    if (isText)
                    {
                        _lblRotScale.Text         = "SCALE (RotationOrScale)";
                        _numRotScale.Minimum      = 0.1M;
                        _numRotScale.Maximum      = 20M;
                        _numRotScale.DecimalPlaces = 2;
                        _numRotScale.Increment    = 0.1M;
                        _numRotScale.Value        = (decimal)Math.Round(ClampF(sp.Scale, 0.1f, 20f), 2);

                        _txtText.Text             = sp.Text ?? "";
                        int fi = Array.IndexOf(SpriteCatalog.Fonts, sp.FontId);
                        _cmbFont.SelectedIndex    = fi >= 0 ? fi : 0;
                        _cmbAlignment.SelectedIndex = (int)sp.Alignment;
                    }
                    else
                    {
                        _lblRotScale.Text         = "ROTATION (radians)";
                        _numRotScale.Minimum      = -7M;
                        _numRotScale.Maximum      = 7M;
                        _numRotScale.DecimalPlaces = 3;
                        _numRotScale.Increment    = 0.05M;
                        _numRotScale.Value        = (decimal)Math.Round(ClampF(sp.Rotation, -7f, 7f), 3);
                    }

                    _colorPreview.BackColor = sp.Color;
                    _trackAlpha.Value       = sp.ColorA;
                    _lblAlpha.Text          = sp.ColorA.ToString();
                }

                // Sync layer list selection
                if (sp != null && _layout != null)
                {
                    int idx = _layout.Sprites.IndexOf(sp);
                    if (idx >= 0 && idx < _lstLayers.Items.Count)
                        _lstLayers.SelectedIndex = idx;
                }
                else
                {
                    _lstLayers.SelectedIndex = -1;
                }
            }
            finally { _updatingProps = false; }

            RefreshExpressionColors();
            RefreshCode();
            UpdateStatus();
        }

        private void OnSpriteModified(object sender, EventArgs e)
        {
            var sp = _canvas.SelectedSprite;
            if (sp == null) return;

            _updatingProps = true;
            try
            {
                _numX.Value = (decimal)Math.Round(ClampF(sp.X,      -8192f, 8192f), 1);
                _numY.Value = (decimal)Math.Round(ClampF(sp.Y,      -8192f, 8192f), 1);
                _numW.Value = (decimal)Math.Round(ClampF(sp.Width,      1f, 8192f), 1);
                _numH.Value = (decimal)Math.Round(ClampF(sp.Height,     1f, 8192f), 1);
            }
            finally { _updatingProps = false; }

            RefreshCode();
            UpdateStatus();
        }

        private void OnFontChanged(object sender, EventArgs e)
        {
            if (_updatingProps) return;

            string newFont = _cmbFont.SelectedItem?.ToString() ?? "White";
            bool newIsMono = newFont == "Monospace";

            // Warn if switching font families and existing text sprites use the other family
            if (_layout != null)
            {
                bool hasConflict = false;
                foreach (var s in _layout.Sprites)
                {
                    if (s.Type != SpriteEntryType.Text) continue;
                    if (s == _canvas?.SelectedSprite) continue;
                    bool isMono = s.FontId == "Monospace";
                    if (isMono != newIsMono) { hasConflict = true; break; }
                }
                if (hasConflict)
                {
                    MessageBox.Show(
                        $"Other text sprites on this surface use {(newIsMono ? "White" : "Monospace")} font.\n" +
                        "SE does not allow mixing White and Monospace fonts on the same LCD surface.",
                        "Font Mixing Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            // Apply via normal property handler
            OnPropChanged(sender, e);
        }

        private void OnPropChanged(object sender, EventArgs e)
        {
            if (_updatingProps || _canvas?.SelectedSprite == null) return;
            PushUndo();
            var sp = _canvas.SelectedSprite;

            sp.X = (float)_numX.Value;
            sp.Y = (float)_numY.Value;
            sp.Width  = (float)_numW.Value;
            sp.Height = (float)_numH.Value;

            if (sp.Type == SpriteEntryType.Texture)
                sp.Rotation = (float)_numRotScale.Value;
            else
            {
                sp.Scale     = (float)_numRotScale.Value;
                sp.Text      = _txtText.Text;
                sp.FontId    = _cmbFont.SelectedItem?.ToString() ?? "White";
                sp.Alignment = (SpriteTextAlignment)_cmbAlignment.SelectedIndex;
            }

            _canvas.Invalidate();
            RefreshLayerList();
            RefreshCode();
        }

        private void OnAlphaChanged(object sender, EventArgs e)
        {
            _lblAlpha.Text = _trackAlpha.Value.ToString();
            if (_updatingProps || _canvas?.SelectedSprite == null) return;
            PushUndo();
            _canvas.SelectedSprite.ColorA = _trackAlpha.Value;
            _canvas.Invalidate();
            RefreshCode();
        }

        private void OnColorClick(object sender, EventArgs e)
        {
            if (_canvas.SelectedSprite == null) return;
            using (var dlg = new ColorDialog { Color = _canvas.SelectedSprite.Color, FullOpen = true })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                PushUndo();
                _canvas.SelectedSprite.ColorR = dlg.Color.R;
                _canvas.SelectedSprite.ColorG = dlg.Color.G;
                _canvas.SelectedSprite.ColorB = dlg.Color.B;
                // Preserve alpha
                _colorPreview.BackColor = _canvas.SelectedSprite.Color;
                _canvas.Invalidate();
                RefreshCode();
            }
        }

        /// <summary>
        /// Populates the expression color swatch panel for the currently selected sprite.
        /// Shows all Color literals found in the source context around the sprite's definition.
        /// </summary>
        private void RefreshExpressionColors()
        {
            _exprColorPanel.Controls.Clear();
            var sp = _canvas?.SelectedSprite;
            if (sp == null || _layout?.OriginalSourceCode == null || sp.SourceStart < 0)
            {
                _exprColorPanel.Visible = false;
                _exprColorLabel.Visible = false;
                return;
            }

            var colors = CodeGenerator.ExtractColorLiterals(_layout.OriginalSourceCode, sp.SourceStart, sp.SourceEnd);
            sp.ExpressionColors = colors;

            if (colors == null || colors.Count == 0)
            {
                _exprColorPanel.Visible = false;
                _exprColorLabel.Visible = false;
                return;
            }

            foreach (var ec in colors)
            {
                var swatch = new Panel
                {
                    Width       = 24,
                    Height      = 24,
                    BackColor   = ec.Color,
                    BorderStyle = BorderStyle.FixedSingle,
                    Cursor      = Cursors.Hand,
                    Margin      = new Padding(2),
                    Tag         = ec,
                };
                var tip = new ToolTip();
                tip.SetToolTip(swatch, ec.LiteralText);
                swatch.Click += OnExpressionColorClick;
                _exprColorPanel.Controls.Add(swatch);
            }

            _exprColorPanel.Visible = true;
            _exprColorLabel.Visible = true;
        }

        private void OnExpressionColorClick(object sender, EventArgs e)
        {
            var swatch = sender as Panel;
            var ec = swatch?.Tag as ExpressionColor;
            if (ec == null || _layout?.OriginalSourceCode == null) return;

            using (var dlg = new ColorDialog { Color = ec.Color, FullOpen = true })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                PushUndo();

                string patched = CodeGenerator.PatchColorAtOffset(
                    _layout.OriginalSourceCode, ec, dlg.Color.R, dlg.Color.G, dlg.Color.B, dlg.Color.A);

                if (patched == null) return;

                // Calculate the offset delta so we can adjust all ExpressionColor offsets
                int oldLen = ec.SourceLength;
                string newLiteral = dlg.Color.A != 255
                    ? $"new Color({dlg.Color.R}, {dlg.Color.G}, {dlg.Color.B}, {dlg.Color.A})"
                    : $"new Color({dlg.Color.R}, {dlg.Color.G}, {dlg.Color.B})";
                int delta = newLiteral.Length - oldLen;

                _layout.OriginalSourceCode = patched;

                // Update this expression color entry
                int patchedOffset = ec.SourceOffset;
                ec.R = dlg.Color.R;
                ec.G = dlg.Color.G;
                ec.B = dlg.Color.B;
                ec.A = dlg.Color.A;
                ec.LiteralText = newLiteral;
                ec.SourceLength = newLiteral.Length;

                // Shift offsets of all expression colors (on all sprites) after the patched position
                if (delta != 0)
                {
                    foreach (var sprite in _layout.Sprites)
                    {
                        if (sprite.ExpressionColors == null) continue;
                        foreach (var other in sprite.ExpressionColors)
                        {
                            if (other == ec) continue;
                            if (other.SourceOffset > patchedOffset)
                                other.SourceOffset += delta;
                        }
                        // Also shift SourceStart/SourceEnd for sprites after the patch
                        if (sprite.SourceStart > patchedOffset)
                        {
                            sprite.SourceStart += delta;
                            sprite.SourceEnd += delta;
                        }
                    }
                }

                // Update the swatch
                swatch.BackColor = ec.Color;

                // Re-execute the code to get updated sprite values on the canvas
                TryReExecuteCode();
            }
        }

        /// <summary>
        /// Re-executes the current OriginalSourceCode to refresh sprite values on the canvas.
        /// Used after expression color edits.
        /// </summary>
        private void TryReExecuteCode()
        {
            if (_layout?.OriginalSourceCode == null) return;

            var callExpr = _execCallBox?.Text?.Trim();
            if (string.IsNullOrEmpty(callExpr)) callExpr = null;

            try
            {
                var result = CodeExecutor.Execute(_layout.OriginalSourceCode, callExpr);
                if (result.Success && result.Sprites.Count > 0)
                {
                    // Collect source-tracked sprites for merge
                    var editable = new List<SpriteEntry>();
                    foreach (var sp in _layout.Sprites)
                        if (!sp.IsReferenceLayout && sp.SourceStart >= 0 && sp.ImportBaseline != null)
                            editable.Add(sp);

                    if (editable.Count > 0)
                    {
                        SnapshotMerger.Merge(editable, result.Sprites, applyColors: true);

                        // Update baselines to lock in executed positions
                        foreach (var sp in editable)
                        {
                            if (sp.ImportBaseline != null)
                                sp.ImportBaseline = sp.CloneValues();
                        }
                    }

                    _canvas.Invalidate();
                    RefreshLayerList();
                    RefreshCode();
                    RefreshExpressionColors();
                }
            }
            catch
            {
                // Execution failed — just refresh the code display
                RefreshCode();
            }
        }

        /// <summary>
        /// Maps a layer list index to the actual SpriteEntry, accounting for
        /// isolation mode where the list only shows highlighted sprites.
        /// </summary>
        private SpriteEntry SpriteFromLayerIndex(int listIdx)
        {
            if (_layout == null || listIdx < 0) return null;
            var highlighted = _canvas.HighlightedSprites;
            if (highlighted == null)
            {
                // Normal mode — list index == sprite index
                return listIdx < _layout.Sprites.Count ? _layout.Sprites[listIdx] : null;
            }
            // Isolation mode — count only highlighted sprites
            int cur = 0;
            foreach (var sp in _layout.Sprites)
            {
                if (!highlighted.Contains(sp)) continue;
                if (cur == listIdx) return sp;
                cur++;
            }
            return null;
        }

        private void OnLayerListSelectionChanged(object sender, EventArgs e)
        {
            if (_updatingProps || _layout == null) return;
            int idx = _lstLayers.SelectedIndex;
            var sprite = SpriteFromLayerIndex(idx);
            if (sprite != null)
                _canvas.SelectedSprite = sprite;
        }

        private void OnLayerListDoubleClick(object sender, MouseEventArgs e)
        {
            if (_layout == null || _codeBox.TextLength == 0) return;
            int listIdx = _lstLayers.IndexFromPoint(e.Location);
            var sprite = SpriteFromLayerIndex(listIdx);
            if (sprite == null) return;

            int spriteIdx = _layout.Sprites.IndexOf(sprite);
            string code = _codeBox.Text;
            int targetPos = -1;

            // Count non-ref, source-tracked sprites before this one (for round-trip ordinal)
            int nonRefOrdinal = 0;
            for (int i = 0; i < spriteIdx; i++)
                if (!_layout.Sprites[i].IsReferenceLayout && _layout.Sprites[i].SourceStart >= 0)
                    nonRefOrdinal++;

            // Strategy 1: GenerateRoundTrip comment marker  // [nonRefOrd+1] DisplayName
            if (!sprite.IsReferenceLayout)
            {
                string marker = $"// [{nonRefOrdinal + 1}] {sprite.DisplayName}";
                targetPos = code.IndexOf(marker, StringComparison.Ordinal);
            }

            // Strategy 2: Generate comment marker  // [spriteIdx+1] DisplayName
            if (targetPos < 0)
            {
                string marker = $"// [{spriteIdx + 1}] {sprite.DisplayName}";
                targetPos = code.IndexOf(marker, StringComparison.Ordinal);
            }

            // Strategy 3: Nth "new MySprite" / "MySprite.Create" occurrence (PatchOriginalSource / no markers)
            if (targetPos < 0 && !sprite.IsReferenceLayout && sprite.SourceStart >= 0)
            {
                int pos = -1;
                for (int n = 0; n <= nonRefOrdinal; n++)
                {
                    int a = code.IndexOf("new MySprite", pos + 1, StringComparison.Ordinal);
                    int b = code.IndexOf("MySprite.Create", pos + 1, StringComparison.Ordinal);
                    if (a < 0 && b < 0) { pos = -1; break; }
                    pos = (a >= 0 && b >= 0) ? Math.Min(a, b) : (a >= 0 ? a : b);
                }
                if (pos >= 0) targetPos = pos;
            }

            if (targetPos < 0) return;

            // Walk back to line start
            int lineStart = targetPos;
            while (lineStart > 0 && code[lineStart - 1] != '\n') lineStart--;

            // Find block end at "});" or next blank line
            int blockEnd = code.IndexOf("})", targetPos, StringComparison.Ordinal);
            if (blockEnd >= 0)
            {
                blockEnd += 2;
                // Include trailing semicolon and newline if present
                if (blockEnd < code.Length && code[blockEnd] == ';') blockEnd++;
                if (blockEnd < code.Length && code[blockEnd] == '\r') blockEnd++;
                if (blockEnd < code.Length && code[blockEnd] == '\n') blockEnd++;
            }
            else
            {
                blockEnd = code.IndexOf('\n', targetPos);
                if (blockEnd < 0) blockEnd = code.Length;
                else blockEnd++;
            }

            _codeBox.Select(lineStart, blockEnd - lineStart);
            _codeBox.ScrollToCaret();
            _codeBox.Focus();
        }

        // ── Refresh helpers ───────────────────────────────────────────────────────
        private void RefreshLayerList()
        {
            _updatingProps = true;
            try
            {
                _lstLayers.Items.Clear();
                if (_layout == null) return;

                var highlighted = _canvas.HighlightedSprites;
                foreach (var s in _layout.Sprites)
                {
                    // During isolation mode, hide dimmed (non-highlighted) sprites from the list
                    if (highlighted != null && !highlighted.Contains(s))
                        continue;

                    string prefix = s.IsHidden ? "⊘ "
                        : s.IsReferenceLayout ? "[REF] "
                        : s.SourceStart < 0 ? "· "
                        : "";
                    _lstLayers.Items.Add(prefix + s.DisplayName);
                }

                var sel = _canvas.SelectedSprite;
                if (sel != null)
                {
                    int idx = _layout.Sprites.IndexOf(sel);
                    // During isolation, the layer list index != sprite list index
                    if (highlighted != null)
                    {
                        int listIdx = 0;
                        foreach (var s in _layout.Sprites)
                        {
                            if (!highlighted.Contains(s)) continue;
                            if (s == sel) { _lstLayers.SelectedIndex = listIdx; break; }
                            listIdx++;
                        }
                    }
                    else if (idx >= 0)
                    {
                        _lstLayers.SelectedIndex = idx;
                    }
                }
            }
            finally { _updatingProps = false; }
        }

        private CodeStyle SelectedCodeStyle
        {
            get
            {
                if (_cmbCodeStyle == null) return CodeStyle.InGame;
                switch (_cmbCodeStyle.SelectedIndex)
                {
                    case 1: return CodeStyle.Mod;
                    case 2: return CodeStyle.Plugin;
                    default: return CodeStyle.InGame;
                }
            }
        }

        private bool IsActivelyStreaming =>
            (_pipeListener != null && _pipeListener.IsListening && !_pipeListener.IsPaused)
            || (_fileWatcher != null && _fileWatcher.IsListening && !_fileWatcher.IsPaused)
            || (_clipboardTimer != null);

        private void RefreshCode()
        {
            if (_layout == null) { _codeBox.Text = ""; RefreshDetectedCalls(); return; }

            // While any live source is actively streaming, the layout contains
            // runtime-expanded sprites (loops unrolled, expressions resolved)
            // that don't match the original source structure.  Freeze the code
            // panel so the user keeps seeing their compact source code.
            if (IsActivelyStreaming) return;

            // Try round-trip: splice updated sprites back into the original pasted code
            if (_layout.OriginalSourceCode != null)
            {
                // Per-sprite patching (dynamic code — loops, switch/case, expressions)
                string patched = CodeGenerator.PatchOriginalSource(_layout);
                if (patched != null)
                {
                    _codeBox.Text = patched;
                    RefreshDetectedCalls();
                    return;
                }

                // Region-based replacement (static layouts with literal positions)
                string roundTrip = CodeGenerator.GenerateRoundTrip(_layout);
                if (roundTrip != null)
                {
                    _codeBox.Text = roundTrip;
                    RefreshDetectedCalls();
                    return;
                }

                // Both round-trip paths failed.  If no sprites have source tracking
                // (e.g. sprites were replaced by a live stream) keep the original
                // source instead of producing verbose per-sprite Generate() output.
                bool hasTracking = false;
                foreach (var sp in _layout.Sprites)
                    if (sp.SourceStart >= 0 && sp.ImportBaseline != null) { hasTracking = true; break; }

                if (!hasTracking)
                {
                    _codeBox.Text = _layout.OriginalSourceCode;
                    RefreshDetectedCalls();
                    return;
                }
            }

            _codeBox.Text = CodeGenerator.Generate(_layout, SelectedCodeStyle);
            RefreshDetectedCalls();
        }

        private void RefreshDetectedCalls()
        {
            if (_lstDetectedCalls == null) return;
            var calls = CodeExecutor.DetectAllCallExpressions(_codeBox.Text);
            _lstDetectedCalls.Items.Clear();
            foreach (var c in calls)
                _lstDetectedCalls.Items.Add(c);
            if (string.IsNullOrWhiteSpace(_execCallBox.Text) && calls.Count > 0)
                _execCallBox.Text = calls[0];
        }

        private void UpdateTitle()
        {
            Text = _currentFile != null
                ? $"SE Sprite LCD Layout Tool — {Path.GetFileName(_currentFile)}"
                : "SE Sprite LCD Layout Tool — New Layout";
        }

        // ── Live LCD Stream ───────────────────────────────────────────────────────

        private void ToggleLiveListening()
        {
            if (_pipeListener != null && _pipeListener.IsListening)
            {
                _pipeListener.Stop();
                _pipeListener.Dispose();
                _pipeListener = null;
                _mnuListenToggle.Text = "Start Live Listening";
                _mnuPauseToggle.Enabled = false;
                _mnuPauseToggle.Text = "Pause Live Stream";
                _liveUndoPushed = false;
                RestoreCodeSpritesIfStreamingEnded();
                RefreshCode();
                UpdateSnapshotButtonState();
                SetStatus("Live listening stopped");
                return;
            }

            _pipeListener = new LivePipeListener();
            _pipeListener.FrameReceived += OnLiveFrameReceived;
            _pipeListener.Connected += () => BeginInvoke((Action)(() =>
            {
                SetStatus("Live stream connected");
                _mnuPauseToggle.Enabled = true;
            }));
            _pipeListener.Disconnected += () => BeginInvoke((Action)(() =>
            {
                SetStatus("Live stream disconnected — waiting for reconnect…");
                _mnuPauseToggle.Enabled = false;
                _mnuPauseToggle.Text = "Pause Live Stream";
            }));
            _pipeListener.Start();
            _mnuListenToggle.Text = "Stop Live Listening";
            SetStatus("Live listening — waiting for plugin connection on pipe…");
        }

        private void ToggleLivePause()
        {
            if (_pipeListener == null && _fileWatcher == null) return;

            bool nowPaused = false;
            if (_pipeListener != null)
            {
                _pipeListener.IsPaused = !_pipeListener.IsPaused;
                nowPaused = _pipeListener.IsPaused;
                _mnuPauseToggle.Text = _pipeListener.IsPaused ? "Resume Live Stream" : "Pause Live Stream";
                SetStatus(_pipeListener.IsPaused ? "Live stream paused — editing freely" : "Live stream resumed");
            }
            if (_fileWatcher != null)
            {
                _fileWatcher.IsPaused = !_fileWatcher.IsPaused;
                nowPaused = _fileWatcher.IsPaused;
                _mnuPauseToggle.Text = _fileWatcher.IsPaused ? "Resume Live Stream" : "Pause Live Stream";
                SetStatus(_fileWatcher.IsPaused ? "Live stream paused — editing freely" : "Live stream resumed");
            }

            // When transitioning to paused: swap in the code-tracked sprites
            // (merged with the last live frame's positions and colours) so the
            // user can select and edit them with PatchOriginalSource working.
            // Loop-generated sprites that have no code counterpart are kept as
            // untracked entries so the canvas still looks like the live frame.
            if (nowPaused && _preLiveCodeSprites != null && _lastLiveFrame != null)
            {
                _layout.Sprites.Clear();
                foreach (var sp in _preLiveCodeSprites)
                    _layout.Sprites.Add(sp);

                // Merge last live frame — preserve user-edited colours.
                var editable = new List<SpriteEntry>();
                foreach (var sp in _layout.Sprites)
                    if (!sp.IsReferenceLayout) editable.Add(sp);
                var mergeResult = SnapshotMerger.Merge(editable, _lastLiveFrame, applyColors: true);

                // Add loop-generated sprites that have no code counterpart so the
                // paused visual matches the live frame.
                foreach (var orphan in mergeResult.UnmatchedSnapshots)
                {
                    orphan.SourceStart = -1;
                    orphan.SourceEnd   = -1;
                    _layout.Sprites.Add(orphan);
                }

                _canvas.CanvasLayout = _layout;
                RefreshLayerList();
                // Give the layer list focus so the first double-click
                // jumps to code immediately without needing a focus click first.
                _lstLayers.Focus();
            }

            // Reset so the next live frame saves an undo snapshot of the
            // user's paused edits before overwriting the canvas.
            _liveUndoPushed = false;

            // Update the code panel immediately — during pause IsActivelyStreaming
            // returns false, so RefreshCode will run PatchOriginalSource and show
            // the round-trip patched code based on the frozen live frame.
            RefreshCode();
            UpdateSnapshotButtonState();
        }

        private void ToggleFileWatching()
        {
            if (_fileWatcher != null && _fileWatcher.IsListening)
            {
                _fileWatcher.Stop();
                _fileWatcher.Dispose();
                _fileWatcher = null;
                _mnuFileWatchToggle.Text = "Watch Snapshot File…";
                _mnuPauseToggle.Enabled = _pipeListener != null && _pipeListener.IsListening;
                _liveUndoPushed = false;
                RestoreCodeSpritesIfStreamingEnded();
                RefreshCode();
                UpdateSnapshotButtonState();
                SetStatus("File watching stopped");
                return;
            }

            using (var dlg = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Select snapshot file to watch",
                Filter = "C# files (*.cs)|*.cs|All files (*.*)|*.*",
                CheckFileExists = false, // file may not exist yet
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                _fileWatcher = new LiveFileWatcher();
                _fileWatcher.FrameReceived += OnLiveFrameReceived;
                _fileWatcher.Connected += () => BeginInvoke((Action)(() =>
                {
                    SetStatus($"Watching: {_fileWatcher?.FilePath}");
                    _mnuPauseToggle.Enabled = true;
                }));
                _fileWatcher.Disconnected += () => BeginInvoke((Action)(() =>
                {
                    SetStatus("File watching stopped");
                    if (_pipeListener == null || !_pipeListener.IsListening)
                    {
                        _mnuPauseToggle.Enabled = false;
                        _mnuPauseToggle.Text = "Pause Live Stream";
                    }
                }));
                _fileWatcher.Start(dlg.FileName);
                _mnuFileWatchToggle.Text = "Stop Watching File";
                _mnuPauseToggle.Enabled = true;
                SetStatus($"Watching: {dlg.FileName}");
            }
        }

        private void ToggleClipboardWatching()
        {
            if (_clipboardTimer != null)
            {
                _clipboardTimer.Stop();
                _clipboardTimer.Dispose();
                _clipboardTimer = null;
                _mnuClipboardToggle.Text = "Watch Clipboard (PB)…";
                _liveUndoPushed = false;
                RestoreCodeSpritesIfStreamingEnded();
                RefreshCode();
                UpdateSnapshotButtonState();
                SetStatus("Clipboard watching stopped");
                return;
            }

            _lastClipboardHash = 0;
            _clipboardTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _clipboardTimer.Tick += OnClipboardTick;
            _clipboardTimer.Start();
            _mnuClipboardToggle.Text = "Stop Watching Clipboard";
            SetStatus("Watching clipboard — copy PB Custom Data to auto-import…");
        }

        private void OnClipboardTick(object sender, EventArgs e)
        {
            string text;
            try { text = Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch { return; } // clipboard locked by another process — skip tick

            if (string.IsNullOrWhiteSpace(text)) return;
            if (!text.Contains("frame.Add(new MySprite")) return;

            int hash = text.GetHashCode();
            if (hash == _lastClipboardHash) return;
            _lastClipboardHash = hash;

            OnLiveFrameReceived(text);
        }

        /// <summary>
        /// Called when all streaming sources have stopped.  Restores the canvas to
        /// code-tracked sprites so editing and click-to-jump work correctly.
        /// If the user paused before stopping the code sprites are already on canvas —
        /// the last live frame is merged to finalise positions.
        /// If the user stopped without pausing, code sprites are restored from the
        /// pre-stream snapshot and merged with the last live frame.  Loop-generated
        /// sprites that have no code counterpart are kept as untracked entries so the
        /// visual remains complete even though they are not round-trip editable.
        /// </summary>
        private void RestoreCodeSpritesIfStreamingEnded()
        {
            if (_preLiveCodeSprites == null) return;

            // Only act once every source is gone.
            bool anyActive = (_pipeListener != null && _pipeListener.IsListening)
                || (_fileWatcher != null && _fileWatcher.IsListening)
                || (_clipboardTimer != null);
            if (anyActive) return;

            // Detect which mode the canvas is currently in.
            // If code sprites are showing (user paused before stopping) finalise
            // the merge and leave them in place for round-trip editing.
            // If live sprites are showing (user stopped without pausing) keep
            // them — the visual is already correct and removing them would make
            // loop-generated sprites disappear.
            bool showingCodeSprites = _layout.Sprites.Exists(
                sp => !sp.IsReferenceLayout && sp.SourceStart >= 0 && sp.ImportBaseline != null);

            if (showingCodeSprites && _lastLiveFrame != null)
            {
                // User paused before stopping — code sprites are already on canvas;
                // just finalise the position/colour merge and we're done.
                var editable = new List<SpriteEntry>();
                foreach (var sp in _layout.Sprites)
                    if (!sp.IsReferenceLayout) editable.Add(sp);
                SnapshotMerger.Merge(editable, _lastLiveFrame, applyColors: true);
                _canvas.CanvasLayout = _layout;
                RefreshLayerList();
            }
            else if (_preLiveCodeSprites != null)
            {
                // User stopped without pausing — restore the code-tracked sprites so
                // editing and click-to-jump work correctly after the stream ends.
                // Loop-generated sprites that have no code counterpart are kept as
                // untracked entries (visible on canvas but not round-trip editable).
                _layout.Sprites.Clear();
                foreach (var sp in _preLiveCodeSprites)
                    _layout.Sprites.Add(sp);

                if (_lastLiveFrame != null)
                {
                    var editable = new List<SpriteEntry>();
                    foreach (var sp in _layout.Sprites)
                        if (!sp.IsReferenceLayout) editable.Add(sp);
                    var mergeResult = SnapshotMerger.Merge(editable, _lastLiveFrame, applyColors: true);

                    foreach (var orphan in mergeResult.UnmatchedSnapshots)
                    {
                        orphan.SourceStart = -1;
                        orphan.SourceEnd   = -1;
                        _layout.Sprites.Add(orphan);
                    }
                }

                _canvas.CanvasLayout = _layout;
                RefreshLayerList();
            }

            _preLiveCodeSprites = null;
            // Keep _lastLiveFrame so the user can still capture/apply it
            // after stopping the stream.  It is cleared in NewLayout().
            UpdateSnapshotButtonState();
        }

        private void ApplyLiveSnapshot()
        {
            if (_lastLiveFrame == null || _lastLiveFrame.Count == 0)
            {
                SetStatus("No live frame captured — wait for at least one frame.");
                return;
            }
            if (_layout == null) return;

            var editable = new List<SpriteEntry>();
            foreach (var sp in _layout.Sprites)
                if (!sp.IsReferenceLayout) editable.Add(sp);

            if (editable.Count == 0)
            {
                SetStatus("No editable sprites to apply snapshot to.");
                return;
            }

            PushUndo();
            var result = SnapshotMerger.Merge(editable, _lastLiveFrame, applyColors: true);

            // Update baselines to lock in the live positions for round-trip code generation
            foreach (var sp in editable)
            {
                if (sp.ImportBaseline != null)
                    sp.ImportBaseline = sp.CloneValues();
            }

            _canvas.Invalidate();
            RefreshLayerList();
            RefreshCode();
            SetStatus($"Live snapshot captured — {result.Summary}");
        }

        private void UpdateSnapshotButtonState()
        {
            bool canCapture = _lastLiveFrame != null && _lastLiveFrame.Count > 0 && !IsActivelyStreaming;
            if (_btnCaptureSnapshot != null) _btnCaptureSnapshot.Visible = canCapture;
            if (_mnuCaptureSnapshot != null) _mnuCaptureSnapshot.Enabled = canCapture;
        }

        /// <summary>
        /// Executes a single detected call, then highlights only its sprites on the
        /// canvas while dimming the rest of the full frame.  The user can edit the
        /// isolated sprites and click "Show All" to restore the complete view.
        /// </summary>
        private void IsolateCallSprites(string call)
        {
            if (_layout == null || string.IsNullOrWhiteSpace(call)) return;

            string code = _codeBox.Text;
            if (string.IsNullOrWhiteSpace(code)) return;

            // Save the full frame so we can restore it later
            if (_fullFrameSprites == null)
            {
                _fullFrameSprites = new List<SpriteEntry>();
                foreach (var sp in _layout.Sprites)
                    _fullFrameSprites.Add(sp);
            }

            var result = CodeExecutor.Execute(code, call);
            if (!result.Success)
            {
                _execResultLabel.Text      = "✗ Error";
                _execResultLabel.ForeColor = Color.FromArgb(220, 80, 80);
                MessageBox.Show(result.Error, "Execution Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (result.Sprites.Count == 0)
            {
                SetStatus($"Call returned 0 sprites — nothing to isolate.");
                return;
            }

            _execResultLabel.Text      = $"✔ {result.Sprites.Count} (isolated)";
            _execResultLabel.ForeColor = Color.FromArgb(80, 220, 100);

            PushUndo();

            // Merge the executed sprites into the full frame using SnapshotMerger
            // to match them by type+data, then build the highlighted set.
            var nonRef = new List<SpriteEntry>();
            foreach (var sp in _layout.Sprites)
                if (!sp.IsReferenceLayout) nonRef.Add(sp);

            var mergeResult = SnapshotMerger.Merge(nonRef, result.Sprites, applyColors: true);

            // The highlighted set = sprites that matched the executed call
            _isolatedCallSprites = new HashSet<SpriteEntry>();
            foreach (var sp in nonRef)
            {
                // Check if this sprite was matched (position/color updated from the call)
                foreach (var execSp in result.Sprites)
                {
                    bool typeMatch = (sp.Type == SpriteEntryType.Text && execSp.Type == SpriteEntryType.Text
                                        && sp.Text == execSp.Text)
                                   || (sp.Type == SpriteEntryType.Texture && execSp.Type == SpriteEntryType.Texture
                                        && sp.SpriteName == execSp.SpriteName);
                    if (typeMatch && Math.Abs(sp.X - execSp.X) < 1f && Math.Abs(sp.Y - execSp.Y) < 1f)
                    {
                        _isolatedCallSprites.Add(sp);
                        break;
                    }
                }
            }

            // Also add any orphan (unmatched) sprites from the execution
            foreach (var orphan in mergeResult.UnmatchedSnapshots)
            {
                orphan.SourceStart = -1;
                orphan.SourceEnd   = -1;
                _layout.Sprites.Add(orphan);
                _isolatedCallSprites.Add(orphan);
            }

            // If no matches found, highlight all executed sprites directly
            if (_isolatedCallSprites.Count == 0)
            {
                foreach (var sp in result.Sprites)
                    _isolatedCallSprites.Add(sp);
            }

            _canvas.HighlightedSprites = _isolatedCallSprites;
            SpriteEntry firstIsolated = null;
            foreach (var sp in _isolatedCallSprites) { firstIsolated = sp; break; }
            _canvas.SelectedSprite = firstIsolated;
            _canvas.Invalidate();
            RefreshLayerList();
            if (_btnShowAll != null) _btnShowAll.Visible = true;
            SetStatus($"Isolated: {call} — {_isolatedCallSprites.Count} sprite(s). Edit, then click Show All.");
        }

        /// <summary>
        /// Restores the full frame view after an isolated call edit session.
        /// Merges any edits back and removes the dimming.
        /// </summary>
        private void RestoreFullView()
        {
            _canvas.HighlightedSprites = null;
            _isolatedCallSprites = null;

            if (_fullFrameSprites != null)
            {
                // The sprites in _layout.Sprites may have been edited;
                // _fullFrameSprites are the originals.  We need to reconcile:
                // keep the current layout sprites (which include edits) but
                // clear the isolation state.  The full set is already on canvas
                // since we never removed non-highlighted sprites.
                _fullFrameSprites = null;
            }

            // Unhide all layers when restoring full view
            if (_layout != null)
                foreach (var sp in _layout.Sprites)
                    sp.IsHidden = false;

            if (_btnShowAll != null) _btnShowAll.Visible = false;
            _canvas.Invalidate();
            RefreshLayerList();
            SetStatus("Full view restored.");
        }

        /// <summary>Hides or shows the currently selected sprite layer.</summary>
        private void ToggleSelectedLayerVisibility(bool hide)
        {
            var sel = _canvas.SelectedSprite;
            if (sel == null || _layout == null) return;

            sel.IsHidden = hide;
            if (hide)
            {
                // Deselect the hidden sprite so the user can click others
                _canvas.SelectedSprite = null;
            }
            _canvas.Invalidate();
            RefreshLayerList();
            SetStatus(hide ? $"Layer hidden: {sel.DisplayName}" : $"Layer shown: {sel.DisplayName}");
        }

        /// <summary>Shows all hidden layers, restoring full visibility.</summary>
        private void ShowAllLayers()
        {
            if (_layout == null) return;
            foreach (var sp in _layout.Sprites)
                sp.IsHidden = false;
            _canvas.Invalidate();
            RefreshLayerList();
            SetStatus("All layers visible.");
        }

        private void OnLiveFrameReceived(string frame)
        {
            // Called on background/timer thread — always marshal to UI thread
            BeginInvoke((Action)(() =>
            {
                var sprites = CodeParser.Parse(frame);
                if (sprites.Count == 0) return;

                // Create a default layout if none is loaded so live frames always land somewhere
                if (_layout == null)
                {
                    _layout = new LcdLayout();
                    _canvas.CanvasLayout = _layout;
                }

                // Save ONE undo snapshot for the pre-stream state, then skip
                // undo + code-gen for subsequent frames to avoid flooding the
                // undo stack and burning CPU on code generation at ~20 FPS.
                if (!_liveUndoPushed)
                {
                    PushUndo();
                    _liveUndoPushed = true;
                }

                // When code with source tracking is loaded, save the code sprites
                // before the first live replace so we can restore them on pause.
                // The canvas always shows the full live frame (correct visual),
                // and the merge into code sprites happens only on pause — where
                // the round-trip patcher actually needs them.
                bool hasTracking = _layout.OriginalSourceCode != null
                    && _layout.Sprites.Exists(sp => !sp.IsReferenceLayout && sp.SourceStart >= 0);

                if (hasTracking && _preLiveCodeSprites == null)
                {
                    // Save references to the code-tracked sprite objects before
                    // we replace _layout.Sprites.  Same object references survive
                    // the Clear/AddRange below, kept alive by this list.
                    _preLiveCodeSprites = new List<SpriteEntry>();
                    foreach (var sp in _layout.Sprites)
                        if (!sp.IsReferenceLayout) _preLiveCodeSprites.Add(sp);
                }

                if (hasTracking) _lastLiveFrame = sprites;

                // Always replace for a correct full-resolution visual.
                // (The merge into code sprites happens in ToggleLivePause.)
                _layout.Sprites.Clear();
                _layout.Sprites.AddRange(sprites);
                _canvas.CanvasLayout = _layout;
                RefreshLayerList();
                SetStatus($"Live frame: {sprites.Count} sprites");
            }));
        }

        private void SetStatus(string msg) => _statusLabel.Text = msg;

        private void UpdateStatus()
        {
            if (_layout == null) return;
            var sp = _canvas.SelectedSprite;
            string sel = sp != null ? $" | Selected: {sp.DisplayName}  ({sp.X:F0}, {sp.Y:F0})  {sp.Width:F0}×{sp.Height:F0}" : "";
            _statusLabel.Text = $"Surface: {_layout.SurfaceWidth}×{_layout.SurfaceHeight}  Sprites: {_layout.Sprites.Count}{sel}";
        }

        // ── File I/O ──────────────────────────────────────────────────────────────
        private void OpenLayout()
        {
            using (var dlg = new OpenFileDialog { Filter = "LCD Layout (*.seld)|*.seld|All files (*.*)|*.*", Title = "Open Layout" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var xs = new XmlSerializer(typeof(LcdLayout));
                    using (var fs = File.OpenRead(dlg.FileName))
                        _layout = (LcdLayout)xs.Deserialize(fs);
                    _currentFile   = dlg.FileName;
                    _canvas.CanvasLayout = _layout;
                    RefreshLayerList();
                    RefreshCode();
                    UpdateTitle();
                    SetStatus($"Opened: {Path.GetFileName(_currentFile)}");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not open layout:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void SaveLayout(bool forceDialog)
        {
            if (forceDialog || _currentFile == null)
            {
                using (var dlg = new SaveFileDialog
                {
                    Filter   = "LCD Layout (*.seld)|*.seld|All files (*.*)|*.*",
                    Title    = "Save Layout",
                    FileName = _layout?.Name ?? "MyLayout",
                })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    _currentFile = dlg.FileName;
                }
            }

            try
            {
                var xs = new XmlSerializer(typeof(LcdLayout));
                using (var fs = File.Create(_currentFile))
                    xs.Serialize(fs, _layout);
                UpdateTitle();
                SetStatus($"Saved: {Path.GetFileName(_currentFile)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save layout:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ── Sprite import ─────────────────────────────────────────────────────────
        private void ShowImportSpriteDialog()
        {
            using (var dlg = new Form())
            {
                dlg.Text = "Import Sprite List from Space Engineers";
                dlg.Size = new Size(620, 520);
                dlg.MinimumSize = new Size(400, 340);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = Color.FromArgb(30, 30, 30);
                dlg.ForeColor = Color.FromArgb(220, 220, 220);
                dlg.Font = new Font("Segoe UI", 9f);

                const string PbScript =
                    "public void Main() {\r\n"
                  + "    var surface = Me.GetSurface(0);\r\n"
                  + "    var sprites = new List<string>();\r\n"
                  + "    surface.GetSprites(sprites);\r\n"
                  + "    sprites.Sort();\r\n"
                  + "    Me.CustomData = $\"// {sprites.Count} sprites found\\n\"\r\n"
                  + "                  + string.Join(\"\\n\", sprites);\r\n"
                  + "    Echo($\"Done \\u2014 {sprites.Count} sprites written to CustomData\");\r\n"
                  + "}";

                var lblInstructions = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 90,
                    Padding = new Padding(8, 8, 8, 4),
                    Text = "1. Copy the PB script below and paste it into a Programmable Block.\r\n"
                         + "2. Compile & Run — the script writes all sprite names to Custom Data.\r\n"
                         + "3. Copy the PB's Custom Data and paste it into the box below.\r\n\r\n"
                         + "(One sprite name per line — duplicates and built-in names are filtered automatically.)",
                    ForeColor = Color.FromArgb(180, 200, 255),
                    Font = new Font("Segoe UI", 9f),
                };

                var scriptToolbar = new FlowLayoutPanel
                {
                    Dock          = DockStyle.Top,
                    Height        = 34,
                    FlowDirection = FlowDirection.LeftToRight,
                    BackColor     = Color.FromArgb(30, 30, 30),
                    Padding       = new Padding(6, 4, 4, 0),
                };
                var btnCopyScript = DarkButton("\uD83D\uDCCB Copy PB Script to Clipboard", Color.FromArgb(0, 122, 60));
                btnCopyScript.Size = new Size(220, 26);
                btnCopyScript.Click += (s, e) =>
                {
                    Clipboard.SetText(PbScript);
                    SetStatus("PB script copied to clipboard — paste it into a Programmable Block in SE.");
                };
                var lblScriptHint = new Label
                {
                    Text      = "Copies the sprite-dump script for a Programmable Block",
                    Width     = 320,
                    Height    = 24,
                    ForeColor = Color.FromArgb(130, 130, 130),
                    Font      = new Font("Segoe UI", 8f, FontStyle.Italic),
                    TextAlign = ContentAlignment.MiddleLeft,
                };
                scriptToolbar.Controls.Add(btnCopyScript);
                scriptToolbar.Controls.Add(lblScriptHint);

                var txtPaste = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    BackColor = Color.FromArgb(20, 20, 20),
                    ForeColor = Color.FromArgb(200, 220, 200),
                    Font = new Font("Consolas", 9f),
                    AcceptsReturn = true,
                };

                var btnPanel = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    Height = 40,
                    FlowDirection = FlowDirection.RightToLeft,
                    BackColor = Color.FromArgb(30, 30, 30),
                    Padding = new Padding(4),
                };

                var btnImport = DarkButton("Import", Color.FromArgb(0, 100, 180));
                btnImport.Size = new Size(100, 28);
                var btnCancel = DarkButton("Cancel", Color.FromArgb(60, 60, 60));
                btnCancel.Size = new Size(80, 28);

                btnCancel.Click += (s, e) => dlg.Close();
                btnImport.Click += (s, e) =>
                {
                    int added = UserSpriteCatalog.Import(txtPaste.Text);
                    if (added > 0)
                    {
                        PopulateSpriteTree();
                        SetStatus($"Imported {added} new sprite names ({UserSpriteCatalog.Count} total)");
                    }
                    else
                    {
                        SetStatus("No new sprite names found in pasted text.");
                    }
                    dlg.Close();
                };

                var lblCount = new Label
                {
                    Text = UserSpriteCatalog.Count > 0
                        ? $"Currently {UserSpriteCatalog.Count} imported sprites on file."
                        : "No imported sprites yet.",
                    Dock = DockStyle.Bottom,
                    Height = 22,
                    ForeColor = Color.FromArgb(140, 140, 140),
                    Padding = new Padding(8, 2, 0, 0),
                };

                btnPanel.Controls.Add(btnImport);
                btnPanel.Controls.Add(btnCancel);

                dlg.Controls.Add(txtPaste);
                dlg.Controls.Add(scriptToolbar);
                dlg.Controls.Add(lblInstructions);
                dlg.Controls.Add(lblCount);
                dlg.Controls.Add(btnPanel);

                dlg.ShowDialog(this);
            }
        }

        private void ClearImportedSprites()
        {
            if (UserSpriteCatalog.Count == 0)
            {
                SetStatus("No imported sprites to clear.");
                return;
            }
            if (MessageBox.Show($"Remove all {UserSpriteCatalog.Count} imported sprite names?",
                    "Clear Imported Sprites", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            UserSpriteCatalog.Clear();
            PopulateSpriteTree();
            SetStatus("Imported sprite list cleared.");
        }

        // ── Paste layout code ─────────────────────────────────────────────────────
        private void ShowPasteLayoutDialog()
        {
            using (var dlg = new Form())
            {
                dlg.Text = "Paste Layout Code";
                dlg.Size = new Size(700, 660);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = Color.FromArgb(30, 30, 30);
                dlg.ForeColor = Color.FromArgb(220, 220, 220);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;

                var lblInstructions = new Label
                {
                    Text = "Paste SE LCD C# code containing MySprite definitions below.\n"
                         + "Supports PB scripts, mods, and plugins — initializer, constructor, and statement syntax.",
                    Dock = DockStyle.Top,
                    Height = 44,
                    Padding = new Padding(8, 8, 8, 0),
                };

                // ── Main code split: top = original code, bottom = snapshot ──
                var splitCode = new SplitContainer
                {
                    Dock = DockStyle.Fill,
                    Orientation = Orientation.Horizontal,
                    BackColor = Color.FromArgb(30, 30, 30),
                    Panel1MinSize = 120,
                    Panel2MinSize = 60,
                };

                var txtCode = new TextBox
                {
                    Multiline = true,
                    Dock = DockStyle.Fill,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    Font = new Font("Consolas", 9f),
                    BackColor = Color.FromArgb(20, 20, 20),
                    ForeColor = Color.FromArgb(212, 212, 212),
                    AcceptsTab = true,
                };

                var lblSnapshot = new Label
                {
                    Text = "Runtime Snapshot (optional) — paste the output from the snapshot helper to apply real positions:",
                    Dock = DockStyle.Top,
                    Height = 28,
                    Padding = new Padding(8, 6, 8, 0),
                    ForeColor = Color.FromArgb(180, 200, 255),
                };

                var txtSnapshot = new TextBox
                {
                    Multiline = true,
                    Dock = DockStyle.Fill,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    Font = new Font("Consolas", 9f),
                    BackColor = Color.FromArgb(15, 18, 25),
                    ForeColor = Color.FromArgb(180, 210, 255),
                    AcceptsTab = true,
                };

                // ── Code-execution panel (sits above the snapshot box) ────────────────
                var lblCallPrefix = new Label
                {
                    Text = "▶ Execute call:",
                    Dock = DockStyle.Left,
                    AutoSize = false,
                    Width = 108,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(6, 0, 0, 0),
                    ForeColor = Color.FromArgb(130, 200, 255),
                };
                var lblExecResult = new Label
                {
                    Text = "–",
                    Dock = DockStyle.Right,
                    AutoSize = false,
                    Width = 130,
                    TextAlign = ContentAlignment.MiddleRight,
                    Padding = new Padding(0, 0, 6, 0),
                    ForeColor = Color.FromArgb(130, 130, 130),
                };
                var txtCallExpr = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Font = new Font("Consolas", 9f),
                    BackColor = Color.FromArgb(18, 24, 38),
                    ForeColor = Color.FromArgb(160, 220, 255),
                    BorderStyle = BorderStyle.FixedSingle,
                };
                var pnlExec = new Panel { Dock = DockStyle.Bottom, Height = 30 };
                pnlExec.Controls.Add(txtCallExpr);
                pnlExec.Controls.Add(lblExecResult);
                pnlExec.Controls.Add(lblCallPrefix);

                // ── Detected calls list ───────────────────────────────────────────
                var lblDetected = new Label
                {
                    Text      = "Detected methods (double-click to execute):",
                    Dock      = DockStyle.Bottom,
                    Height    = 18,
                    ForeColor = Color.FromArgb(130, 180, 230),
                    Font      = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                    Padding   = new Padding(4, 3, 0, 0),
                };
                var lstDetectedCalls = new ListBox
                {
                    Dock        = DockStyle.Bottom,
                    Height      = 60,
                    BackColor   = Color.FromArgb(22, 26, 38),
                    ForeColor   = Color.FromArgb(160, 220, 255),
                    BorderStyle = BorderStyle.None,
                    Font        = new Font("Consolas", 9f),
                };

                // Snapshot on top (paste first), code editor below with exec bar underneath
                splitCode.Panel1.Controls.Add(txtSnapshot);
                splitCode.Panel1.Controls.Add(lblSnapshot);
                splitCode.Panel2.Controls.Add(txtCode);
                splitCode.Panel2.Controls.Add(lstDetectedCalls);
                splitCode.Panel2.Controls.Add(lblDetected);
                splitCode.Panel2.Controls.Add(pnlExec);

                var chkReplace = new CheckBox
                {
                    Text = "Replace current layout (uncheck to append)",
                    Checked = true,
                    Dock = DockStyle.Bottom,
                    Height = 26,
                    Padding = new Padding(8, 4, 0, 0),
                    ForeColor = Color.FromArgb(200, 200, 200),
                };

                var chkReference = new CheckBox
                {
                    Text = "Import as reference layout (positions for visual reference only — commented out on export)",
                    Checked = false,
                    Dock = DockStyle.Bottom,
                    Height = 26,
                    Padding = new Padding(8, 2, 0, 0),
                    ForeColor = Color.FromArgb(180, 220, 180),
                };

                var btnPanel = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    FlowDirection = FlowDirection.RightToLeft,
                    Height = 36,
                    Padding = new Padding(4),
                };

                var btnImport = DarkButton("Import Sprites", Color.FromArgb(0, 122, 60));
                btnImport.Width = 130;
                var btnCancel = DarkButton("Cancel", Color.FromArgb(60, 60, 60));
                btnCancel.Width = 80;
                btnCancel.Click += (s, e) => dlg.Close();

                var btnSnapshot = DarkButton("📋 Copy Snapshot Helper", Color.FromArgb(100, 80, 0));
                btnSnapshot.Width = 170;
                btnSnapshot.Click += (s, e) =>
                {
                    Clipboard.SetText(CodeGenerator.GenerateSnapshotHelper(SelectedCodeStyle));
                    SetStatus("Snapshot helper script copied to clipboard!");
                };

                // ── Execution state (shared between Execute button and Import button) ──
                List<SpriteEntry> executedSprites = null;

                var btnExec = DarkButton("▶ Execute Code", Color.FromArgb(20, 80, 160));
                btnExec.Dock = DockStyle.Right;
                btnExec.Width = 120;
                pnlExec.Controls.Add(btnExec); // sits directly under the code editor
                btnExec.Click += (s, e) =>
                {
                    string call = txtCallExpr.Text.Trim();
                    if (string.IsNullOrWhiteSpace(call))
                    {
                        call = CodeExecutor.DetectCallExpression(txtCode.Text);
                        if (call == null)
                        {
                            var st = CodeExecutor.DetectScriptType(txtCode.Text);
                            string hint = st == ScriptType.ProgrammableBlock
                                ? "Could not detect a Main() entry point in this PB script.\n\n"
                                  + "Enter the call expression manually, e.g.:\n"
                                  + "  Main(\"\", UpdateType.None)"
                                : st == ScriptType.ModSurface
                                ? "Could not detect a render method with an IMyTextSurface parameter.\n\n"
                                  + "Enter the call expression manually, e.g.:\n"
                                  + "  DrawHUD(surface)"
                                : "Could not detect a render method with a List<MySprite> parameter.\n\n"
                                  + "Enter the call expression manually in the 'Execute call' box, e.g.:\n"
                                  + "  RenderPanel(sprites, 512f, 10f, 1f)";
                            MessageBox.Show(hint,
                                "No Method Found", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            return;
                        }
                        txtCallExpr.Text = call;
                    }

                    lblExecResult.Text = "Running…";
                    lblExecResult.ForeColor = Color.FromArgb(200, 180, 60);
                    dlg.Refresh();

                    var execResult = CodeExecutor.Execute(txtCode.Text, call);
                    if (!execResult.Success)
                    {
                        executedSprites = null;
                        lblExecResult.Text = "✗ Error";
                        lblExecResult.ForeColor = Color.FromArgb(220, 80, 80);
                        MessageBox.Show(execResult.Error, "Execution Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    executedSprites = execResult.Sprites;
                    string typeTag = execResult.ScriptType == ScriptType.ProgrammableBlock ? " [PB]"
                                   : execResult.ScriptType == ScriptType.ModSurface        ? " [Mod]"
                                   : "";
                    lblExecResult.Text = "✔ " + executedSprites.Count + " sprites" + typeTag;
                    lblExecResult.ForeColor = Color.FromArgb(80, 220, 100);
                };

                // Auto-populate the detected calls list when the user leaves the code box
                txtCode.Leave += (s, e) =>
                {
                    var calls = CodeExecutor.DetectAllCallExpressions(txtCode.Text);
                    lstDetectedCalls.Items.Clear();
                    foreach (var c in calls)
                        lstDetectedCalls.Items.Add(c);
                    // Auto-fill the call box with the first detected call if empty
                    if (string.IsNullOrWhiteSpace(txtCallExpr.Text) && calls.Count > 0)
                        txtCallExpr.Text = calls[0];
                };

                // Single-click populates the call box
                lstDetectedCalls.SelectedIndexChanged += (s, e) =>
                {
                    if (lstDetectedCalls.SelectedItem != null)
                        txtCallExpr.Text = lstDetectedCalls.SelectedItem.ToString();
                };

                // Double-click executes the selected call
                lstDetectedCalls.MouseDoubleClick += (s, e) =>
                {
                    if (lstDetectedCalls.SelectedItem == null) return;
                    txtCallExpr.Text = lstDetectedCalls.SelectedItem.ToString();
                    btnExec.PerformClick();
                };

                btnImport.Click += (s, e) =>
                {
                    string sourceCode = txtCode.Text;
                    var sprites = CodeParser.Parse(sourceCode);
                    if (sprites.Count == 0)
                    {
                        MessageBox.Show(
                            "No MySprite definitions found in the pasted code.\n\n"
                            + "Supported patterns:\n"
                            + "  new MySprite { Type = SpriteType.TEXTURE, ... }\n"
                            + "  new MySprite(SpriteType.TEXTURE, \"data\", ...)\n"
                            + "  MySprite.CreateText(\"text\", ...)\n"
                            + "  sprite.Type = SpriteType.TEXTURE; sprite.Data = ...;",
                            "Nothing Found", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    bool isReference = chkReference.Checked;
                    if (isReference)
                    {
                        foreach (var sprite in sprites)
                            sprite.IsReferenceLayout = true;
                    }

                    // ── Per-sprite source tracking for dynamic round-trip ──
                    if (!isReference)
                    {
                        // Detect context labels and store baselines
                        var contextCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        bool hasDynamicPositions = false;
                        foreach (var sprite in sprites)
                        {
                            // Context label from surrounding case/method
                            string ctx = sprite.SourceStart >= 0
                                ? CodeGenerator.DetectSpriteContext(sourceCode, sprite.SourceStart)
                                : null;

                            string typeHint = sprite.Type == SpriteEntryType.Text
                                ? "Text"
                                : sprite.SpriteName ?? "Texture";

                            string label = ctx != null ? $"{ctx}: {typeHint}" : typeHint;

                            // Disambiguate duplicates within the same context
                            if (!contextCounts.TryGetValue(label, out int count))
                                count = 0;
                            contextCounts[label] = count + 1;
                            if (count > 0)
                                label += $".{count + 1}";

                            sprite.ImportLabel = label;
                            sprite.ImportBaseline = sprite.CloneValues();

                            // Check if positions look like expression-parsed defaults
                            // (256,256) = SpriteEntry default, (0,0) = ParseVector2 failure on expressions
                            if (sprite.SourceStart >= 0 &&
                                ((sprite.X == 256f && sprite.Y == 256f) ||
                                 (sprite.X == 0f && sprite.Y == 0f)))
                                hasDynamicPositions = true;
                        }

                        // ── Snapshot merge: apply runtime positions ──
                        // Prefer executed sprites (from ▶ Execute Code) over the manual snapshot box.
                        // Execution gives real positions for ALL sprites including loop-generated ones.
                        if (executedSprites != null && executedSprites.Count > 0)
                        {
                            var mergeResult = SnapshotMerger.Merge(sprites, executedSprites);
                            SetStatus(mergeResult.Summary
                                + (mergeResult.UnmatchedSnapshots.Count > 0
                                    ? $"  {mergeResult.UnmatchedSnapshots.Count} loop/dynamic sprite(s) added as orphans."
                                    : ""));
                            hasDynamicPositions = false;

                            // Orphan sprites are loop- or expression-generated; they have accurate
                            // runtime positions but no source tracking (SourceStart stays -1).
                            foreach (var orphan in mergeResult.UnmatchedSnapshots)
                                sprites.Add(orphan);
                        }
                        else
                        {
                        string snapshotCode = txtSnapshot.Text;
                        if (!string.IsNullOrWhiteSpace(snapshotCode))
                        {
                            var snapshotSprites = CodeParser.Parse(snapshotCode);
                            if (snapshotSprites.Count > 0)
                            {
                                var mergeResult = SnapshotMerger.Merge(sprites, snapshotSprites);
                                SetStatus(mergeResult.Summary);
                                hasDynamicPositions = false; // snapshot resolved positions
                            }
                        }
                        }

                        // Auto-position sprites with default positions so they're visible/selectable
                        if (hasDynamicPositions)
                        {
                            float centerX = _layout.SurfaceWidth / 2f;
                            float yPos = 30f;
                            foreach (var sprite in sprites)
                            {
                                if ((sprite.X == 256f && sprite.Y == 256f) ||
                                    (sprite.X == 0f && sprite.Y == 0f))
                                {
                                    sprite.X = centerX;
                                    sprite.Y = yPos;
                                    yPos += 28f;
                                }
                                if (sprite.Width == 100f && sprite.Height == 100f &&
                                    sprite.Type == SpriteEntryType.Texture)
                                {
                                    sprite.Width = 24f;
                                    sprite.Height = 24f;
                                }
                            }
                        }
                    }

                    PushUndo();

                    if (chkReplace.Checked)
                        _layout.Sprites.Clear();

                    foreach (var sprite in sprites)
                        _layout.Sprites.Add(sprite);

                    // Store original source for round-trip code generation
                    if (!isReference)
                        _layout.OriginalSourceCode = sourceCode;

                    _canvas.SelectedSprite = sprites.Count > 0 ? sprites[0] : null;
                    _canvas.Invalidate();
                    RefreshLayerList();
                    RefreshCode();

                    string refNote = isReference ? " as reference" : "";
                    SetStatus($"Imported {sprites.Count} sprite(s){refNote} from pasted code.");
                    dlg.Close();
                };

                btnPanel.Controls.Add(btnImport);
                btnPanel.Controls.Add(btnCancel);
                btnPanel.Controls.Add(btnSnapshot);

                // Set splitter distance after adding to the form so layout is valid
                dlg.Load += (s, e) =>
                {
                    splitCode.SplitterDistance = Math.Max(80, splitCode.Height / 3);
                };

                dlg.Controls.Add(splitCode);
                dlg.Controls.Add(lblInstructions);
                dlg.Controls.Add(chkReference);
                dlg.Controls.Add(chkReplace);
                dlg.Controls.Add(btnPanel);

                dlg.ShowDialog(this);
            }
        }

        // ── Apply Runtime Snapshot ────────────────────────────────────────────────
        private void ShowApplySnapshotDialog()
        {
            if (_layout == null || _layout.Sprites.Count == 0)
            {
                MessageBox.Show(
                    "Import a layout first (Edit → Paste Layout Code) before applying a snapshot.",
                    "No Layout", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Check there are non-reference sprites to merge into
            bool hasEditable = false;
            foreach (var sp in _layout.Sprites)
                if (!sp.IsReferenceLayout) { hasEditable = true; break; }

            if (!hasEditable)
            {
                MessageBox.Show(
                    "The current layout only contains reference sprites.\n"
                    + "Import editable sprites first (uncheck 'Import as reference layout').",
                    "No Editable Sprites", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new Form())
            {
                dlg.Text = "Apply Runtime Snapshot";
                dlg.Size = new Size(650, 420);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = Color.FromArgb(30, 30, 30);
                dlg.ForeColor = Color.FromArgb(220, 220, 220);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;

                var lblInfo = new Label
                {
                    Text = "Paste the runtime snapshot output (from the snapshot helper snippet) below.\n"
                         + "Positions and sizes will be merged into the current layout sprites.",
                    Dock = DockStyle.Top,
                    Height = 44,
                    Padding = new Padding(8, 8, 8, 0),
                    ForeColor = Color.FromArgb(180, 200, 255),
                };

                var txtSnap = new TextBox
                {
                    Multiline = true,
                    Dock = DockStyle.Fill,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    Font = new Font("Consolas", 9f),
                    BackColor = Color.FromArgb(15, 18, 25),
                    ForeColor = Color.FromArgb(180, 210, 255),
                    AcceptsTab = true,
                };

                var btnPanel = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    FlowDirection = FlowDirection.RightToLeft,
                    Height = 36,
                    Padding = new Padding(4),
                };

                var btnApply = DarkButton("Apply Snapshot", Color.FromArgb(0, 90, 140));
                btnApply.Width = 130;
                var btnCancel = DarkButton("Cancel", Color.FromArgb(60, 60, 60));
                btnCancel.Width = 80;
                btnCancel.Click += (s, e) => dlg.Close();

                btnApply.Click += (s, e) =>
                {
                    string snapCode = txtSnap.Text;
                    if (string.IsNullOrWhiteSpace(snapCode))
                    {
                        MessageBox.Show("Paste snapshot code first.", "Empty",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    var snapshotSprites = CodeParser.Parse(snapCode);
                    if (snapshotSprites.Count == 0)
                    {
                        MessageBox.Show(
                            "No MySprite definitions found in the snapshot text.",
                            "Nothing Found", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    // Collect editable sprites from layout
                    var editable = new List<SpriteEntry>();
                    foreach (var sp in _layout.Sprites)
                        if (!sp.IsReferenceLayout) editable.Add(sp);

                    PushUndo();
                    var result = SnapshotMerger.Merge(editable, snapshotSprites);

                    _canvas.Invalidate();
                    RefreshCode();
                    SetStatus(result.Summary);
                    dlg.Close();
                };

                btnPanel.Controls.Add(btnApply);
                btnPanel.Controls.Add(btnCancel);

                dlg.Controls.Add(txtSnap);
                dlg.Controls.Add(lblInfo);
                dlg.Controls.Add(btnPanel);
                dlg.ShowDialog(this);
            }
        }

        // ── Sprite texture loading ────────────────────────────────────────────────
        private void LoadSpriteTextures(string contentPath)
        {
            if (string.IsNullOrWhiteSpace(contentPath) || !Directory.Exists(contentPath))
                return;

            _textureCache?.Dispose();
            _textureCache = new SpriteTextureCache();
            string result = _textureCache.LoadFromContent(contentPath);
            _canvas.TextureCache = _textureCache;

            AppSettings.GameContentPath = contentPath;
            AppSettings.Save();
            SetStatus($"Textures: {result}");
        }

        private void BrowseGamePath()
        {
            using (var dlg = new FolderBrowserDialog
            {
                Description = "Select the Space Engineers Content directory\n(e.g. …\\SpaceEngineers\\Content)",
                ShowNewFolderButton = false,
            })
            {
                // Pre-select existing path if available
                if (!string.IsNullOrEmpty(AppSettings.GameContentPath) && Directory.Exists(AppSettings.GameContentPath))
                    dlg.SelectedPath = AppSettings.GameContentPath;

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string selected = dlg.SelectedPath;
                // If user picked the game root instead of Content, adjust
                if (!selected.EndsWith("Content", StringComparison.OrdinalIgnoreCase))
                {
                    string sub = Path.Combine(selected, "Content");
                    if (Directory.Exists(sub)) selected = sub;
                }

                string dataDir = Path.Combine(selected, "Data");
                if (!Directory.Exists(dataDir))
                {
                    MessageBox.Show("The selected folder doesn't appear to be an SE Content directory\n(no Data subfolder found).",
                        "Invalid Path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                LoadSpriteTextures(selected);
            }
        }

        private void AutoDetectGamePath()
        {
            string path = AppSettings.AutoDetectContentPath();
            if (path != null)
            {
                LoadSpriteTextures(path);
                SetStatus($"Auto-detected SE at: {path}  — {_textureCache?.LoadedCount ?? 0} textures loaded");
            }
            else
            {
                SetStatus("Could not auto-detect SE installation. Use View → Set SE Game Path to browse manually.");
                MessageBox.Show(
                    "Could not find a Space Engineers installation.\n\n" +
                    "Use View → Set SE Game Path… to manually browse to your\n" +
                    "SpaceEngineers\\Content directory.",
                    "SE Not Found", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void UnloadSpriteTextures()
        {
            _textureCache?.Dispose();
            _textureCache = null;
            _canvas.TextureCache = null;
            AppSettings.GameContentPath = null;
            AppSettings.Save();
            SetStatus("Sprite textures unloaded — using placeholder rendering.");
        }

        // ── Keyboard shortcuts ────────────────────────────────────────────────────
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Delete:
                    DeleteSelected();
                    return true;

                // Undo / Redo
                case Keys.Control | Keys.Z:
                    PerformUndo();
                    return true;
                case Keys.Control | Keys.Y:
                    PerformRedo();
                    return true;

                // Duplicate
                case Keys.Control | Keys.D:
                    DuplicateSelected();
                    return true;

                // Arrow-key nudge (1 px) and Shift+Arrow (10 px)
                case Keys.Up:
                    PushUndo(); _canvas.NudgeSelected(0, -1); RefreshCode();
                    return true;
                case Keys.Down:
                    PushUndo(); _canvas.NudgeSelected(0, 1); RefreshCode();
                    return true;
                case Keys.Left:
                    PushUndo(); _canvas.NudgeSelected(-1, 0); RefreshCode();
                    return true;
                case Keys.Right:
                    PushUndo(); _canvas.NudgeSelected(1, 0); RefreshCode();
                    return true;
                case Keys.Shift | Keys.Up:
                    PushUndo(); _canvas.NudgeSelected(0, -10); RefreshCode();
                    return true;
                case Keys.Shift | Keys.Down:
                    PushUndo(); _canvas.NudgeSelected(0, 10); RefreshCode();
                    return true;
                case Keys.Shift | Keys.Left:
                    PushUndo(); _canvas.NudgeSelected(-10, 0); RefreshCode();
                    return true;
                case Keys.Shift | Keys.Right:
                    PushUndo(); _canvas.NudgeSelected(10, 0); RefreshCode();
                    return true;

                // Layer order
                case Keys.Control | Keys.OemCloseBrackets:
                    PushUndo(); _canvas.MoveSelectedUp(); RefreshLayerList(); RefreshCode();
                    return true;
                case Keys.Control | Keys.OemOpenBrackets:
                    PushUndo(); _canvas.MoveSelectedDown(); RefreshLayerList(); RefreshCode();
                    return true;

                // Snap-to-grid toggle
                case Keys.Control | Keys.G:
                    _canvas.SnapToGrid = !_canvas.SnapToGrid;
                    SetStatus(_canvas.SnapToGrid ? "Snap to grid enabled" : "Snap to grid disabled");
                    return true;

                // Zoom
                case Keys.Control | Keys.Oemplus:
                    _canvas.Zoom *= 1.25f;
                    return true;
                case Keys.Control | Keys.OemMinus:
                    _canvas.Zoom /= 1.25f;
                    return true;
                case Keys.Control | Keys.D0:
                    _canvas.ResetView();
                    return true;

                // File
                case Keys.Control | Keys.S:
                    SaveLayout(false);
                    return true;
                case Keys.Control | Keys.N:
                    if (MessageBox.Show("Start a new layout? Unsaved changes will be lost.", "New Layout",
                            MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                        NewLayout();
                    return true;

                // Paste layout code
                case Keys.Control | Keys.V:
                    ShowPasteLayoutDialog();
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ── Undo / Redo / Duplicate / Center helpers ──────────────────────────────
        private void PushUndo() => _undo.PushUndo(_layout);

        private void OnDragCompleted(object sender, EventArgs e)
        {
            // Undo snapshot was pushed in BeginDrag via OnMouseDown;
            // nothing extra needed here — the drag's final state is the "current" state.
            RefreshCode();
        }

        private void PerformUndo()
        {
            if (!_undo.CanUndo) { SetStatus("Nothing to undo."); return; }
            string selId = _canvas.SelectedSprite?.Id;
            _undo.Undo(_layout);
            _canvas.CanvasLayout = _layout;  // rebind (sprites list was replaced)
            RestoreSelectionById(selId);
            RefreshLayerList();
            RefreshCode();
            SetStatus("Undo");
        }

        private void PerformRedo()
        {
            if (!_undo.CanRedo) { SetStatus("Nothing to redo."); return; }
            string selId = _canvas.SelectedSprite?.Id;
            _undo.Redo(_layout);
            _canvas.CanvasLayout = _layout;
            RestoreSelectionById(selId);
            RefreshLayerList();
            RefreshCode();
            SetStatus("Redo");
        }

        private void RestoreSelectionById(string id)
        {
            if (id == null || _layout == null) return;
            foreach (var sp in _layout.Sprites)
            {
                if (sp.Id == id)
                {
                    _canvas.SelectedSprite = sp;
                    return;
                }
            }
        }

        private void DuplicateSelected()
        {
            if (_canvas.SelectedSprite == null) { SetStatus("Nothing selected to duplicate."); return; }
            PushUndo();
            _canvas.DuplicateSelected();
            RefreshLayerList();
            RefreshCode();
            SetStatus("Duplicated sprite");
        }

        private void CenterSelectedOnSurface()
        {
            if (_canvas.SelectedSprite == null) return;
            PushUndo();
            _canvas.CenterSelected();
            RefreshLayerList();
            RefreshCode();
            SetStatus("Centered on surface");
        }

        private void StretchToSurface()
        {
            if (_canvas.SelectedSprite == null) { SetStatus("Nothing selected to stretch."); return; }
            PushUndo();
            var sp = _canvas.SelectedSprite;
            sp.Width  = _layout.SurfaceWidth;
            sp.Height = _layout.SurfaceHeight;
            sp.X = _layout.SurfaceWidth  / 2f;
            sp.Y = _layout.SurfaceHeight / 2f;
            _canvas.Invalidate();
            OnSelectionChanged(_canvas, EventArgs.Empty);
            RefreshCode();
            SetStatus("Stretched to surface");
        }

        // ── Canvas context menu ───────────────────────────────────────────────────
        private ContextMenuStrip BuildCanvasContextMenu()
        {
            var ctx = new ContextMenuStrip { BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(220, 220, 220) };
            ctx.Renderer = new DarkMenuRenderer();

            ctx.Items.Add("Duplicate\tCtrl+D",       null, (s, e) => DuplicateSelected());
            ctx.Items.Add("Delete\tDel",              null, (s, e) => DeleteSelected());
            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add("Center on Surface",        null, (s, e) => CenterSelectedOnSurface());
            ctx.Items.Add("Stretch to Surface",       null, (s, e) => StretchToSurface());
            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add("Layer Up\tCtrl+]",         null, (s, e) => { PushUndo(); _canvas.MoveSelectedUp();   RefreshLayerList(); RefreshCode(); });
            ctx.Items.Add("Layer Down\tCtrl+[",       null, (s, e) => { PushUndo(); _canvas.MoveSelectedDown(); RefreshLayerList(); RefreshCode(); });
            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add("Reset View\tCtrl+0",       null, (s, e) => _canvas.ResetView());

            return ctx;
        }

        // ── Layer list context menu ──────────────────────────────────────────────
        private ContextMenuStrip BuildLayerContextMenu()
        {
            var ctx = new ContextMenuStrip { BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(220, 220, 220) };
            ctx.Renderer = new DarkMenuRenderer();

            var moveUp   = ctx.Items.Add("Move Up",    null, (s, e) => { PushUndo(); _canvas.MoveSelectedUp();   RefreshLayerList(); RefreshCode(); });
            var moveDown = ctx.Items.Add("Move Down",  null, (s, e) => { PushUndo(); _canvas.MoveSelectedDown(); RefreshLayerList(); RefreshCode(); });
            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add("Duplicate",  null, (s, e) => DuplicateSelected());
            ctx.Items.Add("Delete",     null, (s, e) => DeleteSelected());
            ctx.Items.Add(new ToolStripSeparator());
            var hideItem    = ctx.Items.Add("Hide Layer",       null, (s, e) => ToggleSelectedLayerVisibility(true));
            var showItem    = ctx.Items.Add("Show Layer",       null, (s, e) => ToggleSelectedLayerVisibility(false));
            var showAllItem = ctx.Items.Add("Show All Layers",  null, (s, e) => ShowAllLayers());

            ctx.Opening += (s, e) =>
            {
                if (_layout == null || _canvas.SelectedSprite == null)
                {
                    e.Cancel = true;
                    return;
                }
                int idx = _layout.Sprites.IndexOf(_canvas.SelectedSprite);
                moveUp.Enabled   = idx < _layout.Sprites.Count - 1;
                moveDown.Enabled = idx > 0;

                bool hidden = _canvas.SelectedSprite.IsHidden;
                hideItem.Visible = !hidden;
                showItem.Visible = hidden;

                bool anyHidden = false;
                foreach (var sp in _layout.Sprites)
                    if (sp.IsHidden) { anyHidden = true; break; }
                showAllItem.Enabled = anyHidden;
            };

            return ctx;
        }

        // ── Sprite tree context menu ─────────────────────────────────────────────
        private ContextMenuStrip BuildSpriteTreeContextMenu()
        {
            var ctx = new ContextMenuStrip { BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(220, 220, 220) };
            ctx.Renderer = new DarkMenuRenderer();

            ctx.Items.Add("Add to Layout",                null, (s, e) => AddSelectedTreeSprite());
            ctx.Items.Add("Replace Selected Sprite",      null, (s, e) => ReplaceSelectedSprite());

            ctx.Opening += (s, e) =>
            {
                var node = _spriteTree.SelectedNode;
                bool isLeaf = node?.Parent != null
                    && !(node.Tag is string t && (t == "header" || t == "info" || t.StartsWith("glyphcat:")));

                // "Add" always available on leaf nodes
                ctx.Items[0].Enabled = isLeaf;

                // "Replace" only when a sprite is selected on the canvas AND a leaf is right-clicked
                bool hasSelection = _canvas.SelectedSprite != null;
                ctx.Items[1].Enabled = isLeaf && hasSelection;
                ctx.Items[1].Text = hasSelection
                    ? $"Replace \"{_canvas.SelectedSprite.DisplayName}\""
                    : "Replace Selected Sprite";

                if (!isLeaf)
                    e.Cancel = true;
            };

            return ctx;
        }

        private void ReplaceSelectedSprite()
        {
            var sprite = _canvas.SelectedSprite;
            if (sprite == null) return;

            var node = _spriteTree.SelectedNode;
            if (node == null || node.Parent == null) return;

            // Glyph replacement — switch to text sprite with the glyph character
            if (node.Tag is GlyphEntry glyph)
            {
                PushUndo();
                string font = _cmbFont.SelectedItem?.ToString() ?? "White";
                sprite.Type       = SpriteEntryType.Text;
                sprite.SpriteName = null;
                sprite.Text       = glyph.Character.ToString();
                sprite.FontId     = font;
                sprite.Scale      = 1.0f;
                UpdateImportLabel(sprite, "Text");
                OnSelectionChanged(_canvas, EventArgs.Empty);
                _canvas.Invalidate();
                RefreshLayerList();
                RefreshCode();
                SetStatus($"Replaced with glyph U+{(int)glyph.Character:X4}");
                return;
            }

            // Texture replacement
            string name = node.Text;
            if (string.IsNullOrWhiteSpace(name)) return;

            PushUndo();
            sprite.Type       = SpriteEntryType.Texture;
            sprite.SpriteName = name;
            sprite.Text       = name;
            UpdateImportLabel(sprite, name);
            OnSelectionChanged(_canvas, EventArgs.Empty);
            _canvas.Invalidate();
            RefreshLayerList();
            RefreshCode();
            SetStatus($"Replaced sprite texture with \"{name}\"");
        }

        /// <summary>
        /// Updates the ImportLabel for a sprite after replacement, preserving the
        /// context prefix (e.g. "Header: ") while updating the type hint.
        /// </summary>
        private static void UpdateImportLabel(SpriteEntry sprite, string typeHint)
        {
            if (sprite.ImportLabel == null) return;
            int colonIdx = sprite.ImportLabel.IndexOf(": ");
            string prefix = colonIdx >= 0 ? sprite.ImportLabel.Substring(0, colonIdx + 2) : "";
            sprite.ImportLabel = prefix + typeHint;
        }

        // ── Helper factory methods ────────────────────────────────────────────────
        private static Label SectionLabel(string text, int width) =>
            new Label
            {
                Text      = text,
                AutoSize  = false,
                Width     = width,
                Height    = 18,
                ForeColor = Color.FromArgb(130, 130, 130),
                Font      = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                Padding   = new Padding(0, 5, 0, 1),
            };

        private void InitializeComponent()
        {
            this.SuspendLayout();
            // 
            // MainForm
            // 
            this.ClientSize = new System.Drawing.Size(583, 528);
            this.Name = "MainForm";
            this.Load += new System.EventHandler(this.MainForm_Load);
            this.ResumeLayout(false);

        }

        private void MainForm_Load(object sender, EventArgs e)
        {

        }

        private static TableLayoutPanel MakeLabeledNumRow(
            string lbl1, out NumericUpDown num1,
            string lbl2, out NumericUpDown num2,
            int totalWidth, int min, int max, int decimals)
        {
            var row = new TableLayoutPanel
            {
                Width       = totalWidth,
                Height      = 24,
                ColumnCount = 4,
                RowCount    = 1,
                BackColor   = Color.Transparent,
                Padding     = new Padding(0),
                Margin      = new Padding(0),
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,  50));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,  50));

            var l1 = new Label { Text = lbl1, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(170, 170, 170), TextAlign = ContentAlignment.MiddleLeft };
            var l2 = new Label { Text = lbl2, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(170, 170, 170), TextAlign = ContentAlignment.MiddleLeft };

            num1 = new NumericUpDown { Dock = DockStyle.Fill, Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = 1, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.White };
            num2 = new NumericUpDown { Dock = DockStyle.Fill, Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = 1, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.White };

            row.Controls.Add(l1,   0, 0);
            row.Controls.Add(num1, 1, 0);
            row.Controls.Add(l2,   2, 0);
            row.Controls.Add(num2, 3, 0);
            return row;
        }

        private static Button DarkButton(string text, Color back)
        {
            var btn = new Button
            {
                Text      = text,
                BackColor = back,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Height    = 26,
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private static float ClampF(float v, float min, float max) => v < min ? min : v > max ? max : v;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _pipeListener?.Dispose();
                _fileWatcher?.Dispose();
                _clipboardTimer?.Dispose();
                _textureCache?.Dispose();
            }
            base.Dispose(disposing);
        }

        // ── Dark theme menu renderer ──────────────────────────────────────────────
        private class DarkMenuRenderer : ToolStripProfessionalRenderer
        {
            public DarkMenuRenderer()
                : base(new DarkColorTable()) { }
        }

        private class DarkColorTable : ProfessionalColorTable
        {
            public override Color MenuItemSelected         => Color.FromArgb(60, 60, 62);
            public override Color MenuItemBorder           => Color.FromArgb(80, 80, 80);
            public override Color MenuBorder               => Color.FromArgb(60, 60, 60);
            public override Color ToolStripDropDownBackground => Color.FromArgb(45, 45, 48);
            public override Color MenuStripGradientBegin   => Color.FromArgb(45, 45, 48);
            public override Color MenuStripGradientEnd     => Color.FromArgb(45, 45, 48);
            public override Color MenuItemSelectedGradientBegin => Color.FromArgb(60, 60, 62);
            public override Color MenuItemSelectedGradientEnd   => Color.FromArgb(60, 60, 62);
            public override Color MenuItemPressedGradientBegin  => Color.FromArgb(70, 70, 72);
            public override Color MenuItemPressedGradientEnd    => Color.FromArgb(70, 70, 72);
            public override Color ImageMarginGradientBegin => Color.FromArgb(45, 45, 48);
            public override Color ImageMarginGradientMiddle => Color.FromArgb(45, 45, 48);
            public override Color ImageMarginGradientEnd   => Color.FromArgb(45, 45, 48);
        }
    }
}
