using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Serialization;
using SESpriteLCDLayoutTool.Controls;
using SESpriteLCDLayoutTool.Data;
using SESpriteLCDLayoutTool.Models;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool
{
    public partial class MainForm : Form
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
        private Label         _lblRuntimeData;
        private ListBox       _lstLayers;
        private List<SpriteEntry> _layerListSprites = new List<SpriteEntry>();

        // ── Code panel ────────────────────────────────────────────────────────────
        private RichTextBox _codeBox;
        private ComboBox    _cmbCodeStyle;
        private TextBox     _execCallBox;
        private Label       _execResultLabel;
        private ListBox     _lstDetectedCalls;
        private List<DetectedMethodInfo> _detectedMethods;
        private ListView    _lstVariables;
        private CheckBox    _chkShowInternalFields;
        private TabPage     _tabVariables;  // Track Variables tab to update title
        private AnimationPlayer _lastAnimPlayer; // Held for variable inspection

        // ── Watch expressions ─────────────────────────────────────────────────
        private TabPage     _tabWatch;
        private ListView    _lstWatch;
        private TextBox     _txtWatchExpr;
        private readonly List<Models.WatchExpression> _watchExpressions = new List<Models.WatchExpression>();

        // ── Conditional breakpoint ────────────────────────────────────────────
        private Models.WatchExpression _breakCondition;
        private TextBox     _txtBreakCondition;
        private Label       _lblBreakStatus;

        // ── Console / Output log ──────────────────────────────────────────────
        private TabPage     _tabConsole;
        private RichTextBox _rtbConsole;
        private bool        _breakHitThisTick; // Prevents re-triggering until condition goes false

        // ── Diff view for patched code ────────────────────────────────────────
        private TabPage     _tabDiff;
        private RichTextBox _rtbDiffBefore;
        private RichTextBox _rtbDiffAfter;
        private string      _lastPatchOriginal;

        // ── Code heatmap / profiling ──────────────────────────────────────────
        private Dictionary<string, double> _lastHeatmapTimings;
        private bool _heatmapEnabled = true;

        // ── Syntax highlighting ───────────────────────────────────────────────
        private System.Windows.Forms.Timer _syntaxTimer;
        private string _lastHighlightedCode;
        private bool _highlightInProgress;
        private const int LiveHighlightMaxChars = 14000;
        private const int InitialHighlightMaxChars = 120000;

        // ── Timeline scrubber ─────────────────────────────────────────────────
        private Panel    _timelineBar;
        private TrackBar _timelineScrubber;
        private Label    _lblTimelineTick;
        private bool     _isScrubbing;  // true when user is dragging the scrubber

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
        private bool _fileWatchBidirectional;           // true = script sync mode, false = one-way LCD output
        private ToolStripMenuItem _mnuFileWatchToggle;  // "Watch LCD Output File…"
        private ToolStripMenuItem _mnuScriptWatchToggle; // "Sync Script File (VS Code)…"

        // Bidirectional write-back: when the user modifies sprites on the canvas
        // the patched code is written back to the watched file after a short
        // debounce delay so external editors see the change in near-real-time.
        private System.Windows.Forms.Timer _writeBackTimer;
        private string _pendingWriteBack;
        private int _lastWriteBackHash;

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
        private List<Models.DebugVariable> _liveDebugVars;

        // ── Isolated call view ────────────────────────────────────────────────
        // When the user double-clicks a detected call, we execute just that call
        // and dim all other sprites.  _fullFrameSprites holds the complete set so
        // "Show All" can restore the full view.
        private List<SpriteEntry> _fullFrameSprites;
        private HashSet<SpriteEntry> _isolatedCallSprites;
        private Button _btnShowAll;

        // ── Animation playback ────────────────────────────────────────────────
        private AnimationPlayer _animPlayer;
        private string _animFocusCall;                // non-null = focused animation mode
        private HashSet<string> _animFocusSprites;    // Type+Data keys of the focused method's sprites
        private Panel  _animBar;
        private Button _btnAnimPlay, _btnAnimPause, _btnAnimStop, _btnAnimStep;
        private Label  _lblAnimTick;

        // ── Snapshot comparison bookmarks ─────────────────────────────────────
        private Button _btnBookmarkA, _btnBookmarkB, _btnCompareSnapshots;
        private Label  _lblBookmarks;

        // ── Debug tools ──────────────────────────────────────────────────────
        private Panel _debugPanel;
        private Label _lblDebugStats;
        private bool  _debugPanelVisible;
        private ToolStripMenuItem _mnuToggleDebug;
        private ToolStripMenuItem _mnuOverlayBounds;
        private ToolStripMenuItem _mnuOverlayHeatmap;
        private ToolStripMenuItem _mnuSizeWarnings;

        // ── Animation clipboard ────────────────────────────────────────────
        private KeyframeAnimationParams _copiedAnimation;

        // ── Editable code panel ──────────────────────────────────────────────
        private bool  _codeBoxDirty;
        private bool  _suppressCodeBoxEvents;
        private bool  _executingCode;  // true while OnExecCodeClick is running — suppresses RefreshCode from OnSelectionChanged
        private Label _lblCodeTitle;
        private Label _lblCodeMode;
        private Button _btnApplyCode;
        private CodeAutoComplete _autoComplete;

        // ── Pop-out code editor ──────────────────────────────────────────────
        private Panel  _codePanel;
        private Form   _codePopoutForm;
        private Button _btnPopOut;
        private int    _savedWorkSplitDistance;

        /// <summary>
        /// Pre-animation layout sprites saved before playback starts.
        /// Used as a position reference: each animation frame's sprites are merged
        /// with this snapshot via <see cref="SnapshotMerger"/> so that sprites render
        /// at their correct LCD positions even when auto-detected call parameters
        /// produce incorrect coordinates.  Also used to restore the canvas when
        /// animation stops.
        /// </summary>
        private List<SpriteEntry> _animPositionSnapshot;

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
            NewLayout();
            LoadSpriteTexturesAsync(AppSettings.GameContentPath ?? AppSettings.AutoDetectContentPath());

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

            // Debug stats panel (above status bar, hidden by default)
            _debugPanel = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = 48,
                BackColor = Color.FromArgb(22, 28, 22),
                Visible   = false,
                Padding   = new Padding(8, 4, 8, 4),
            };
            _lblDebugStats = new Label
            {
                Dock      = DockStyle.Fill,
                ForeColor = Color.FromArgb(170, 220, 170),
                Font      = new Font("Consolas", 8.5f),
                TextAlign = ContentAlignment.MiddleLeft,
                Text      = "No sprites",
            };
            _debugPanel.Controls.Add(_lblDebugStats);
            Controls.Add(_debugPanel);

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
            _mnuScriptWatchToggle = new ToolStripMenuItem("Sync Script File (VS Code)…", null, (s, e) => ToggleScriptWatching());
            file.DropDownItems.Add(_mnuScriptWatchToggle);
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
            _mnuFileWatchToggle = new ToolStripMenuItem("Watch LCD Output File…", null, (s, e) => ToggleFileWatching());
            edit.DropDownItems.Add(_mnuFileWatchToggle);
            _mnuClipboardToggle = new ToolStripMenuItem("Watch Clipboard (PB)…", null, (s, e) => ToggleClipboardWatching());
            edit.DropDownItems.Add(_mnuClipboardToggle);
            edit.DropDownItems.Add(new ToolStripSeparator());
            edit.DropDownItems.Add("Duplicate\tCtrl+D",           null, (s, e) => DuplicateSelected());
            edit.DropDownItems.Add("Delete Selected\tDel",        null, (s, e) => DeleteSelected());
            edit.DropDownItems.Add(new ToolStripSeparator());
            edit.DropDownItems.Add("Center on Surface",           null, (s, e) => CenterSelectedOnSurface());
            edit.DropDownItems.Add("Layer Up\tCtrl+]",            null, (s, e) => { PushUndo(); _canvas.MoveSelectedUp();   RefreshLayerList(); if (!_codeBoxDirty) RefreshCode(); });
            edit.DropDownItems.Add("Layer Down\tCtrl+[",          null, (s, e) => { PushUndo(); _canvas.MoveSelectedDown(); RefreshLayerList(); if (!_codeBoxDirty) RefreshCode(); });
            ms.Items.Add(edit);

            // ── Insert menu ──
            var insert = new ToolStripMenuItem("Insert");
            insert.DropDownItems.Add("Template Gallery…\tCtrl+T", null, (s, e) => ShowTemplateGallery());
            insert.DropDownItems.Add(new ToolStripSeparator());
            insert.DropDownItems.Add("Add Texture Sprite", null, (s, e) => AddSelectedTreeSprite());
            insert.DropDownItems.Add("Add Text Sprite", null, (s, e) =>
            {
                string font = _cmbFont.SelectedItem?.ToString() ?? "White";
                PushUndo();
                var sp = _canvas.AddSprite("Text", isText: true);
                if (sp != null) { sp.FontId = font; OnSelectionChanged(_canvas, EventArgs.Empty); }
                RefreshLayerList(); if (!_codeBoxDirty) RefreshCode();
            });
            ms.Items.Add(insert);

            var view = new ToolStripMenuItem("View");
            var snapItem = new ToolStripMenuItem("Snap to Grid\tCtrl+G") { CheckOnClick = true };
            snapItem.CheckedChanged += (s, e) => { _canvas.SnapToGrid = snapItem.Checked; SetStatus(snapItem.Checked ? "Snap to grid enabled" : "Snap to grid disabled"); };
            view.DropDownItems.Add(snapItem);

            var constrainItem = new ToolStripMenuItem("Constrain Sprites to Surface") { CheckOnClick = true };
            constrainItem.CheckedChanged += (s, e) =>
            {
                _canvas.ConstrainToSurface = constrainItem.Checked;
                SetStatus(constrainItem.Checked ? "Constrain to surface enabled — sprites cannot be dragged/nudged off the LCD area" : "Constrain to surface disabled");
            };
            view.DropDownItems.Add(constrainItem);
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
            view.DropDownItems.Add("View Texture Load Errors…", null, (s, e) => ShowTextureLoadErrors());
            view.DropDownItems.Add(new ToolStripSeparator());

            // ── Debug tools ──
            _mnuToggleDebug = new ToolStripMenuItem("Show Debug Stats Panel") { CheckOnClick = true };
            _mnuToggleDebug.CheckedChanged += (s, e) => ToggleDebugPanel(_mnuToggleDebug.Checked);
            view.DropDownItems.Add(_mnuToggleDebug);

            _mnuOverlayBounds = new ToolStripMenuItem("Overlay: Bounding Boxes") { CheckOnClick = true };
            _mnuOverlayBounds.CheckedChanged += (s, e) =>
            {
                if (_mnuOverlayBounds.Checked) _mnuOverlayHeatmap.Checked = false;
                _canvas.OverlayMode = _mnuOverlayBounds.Checked
                    ? DebugOverlayMode.BoundingBoxes
                    : DebugOverlayMode.None;
                SetStatus(_mnuOverlayBounds.Checked ? "Bounding box overlay enabled" : "Overlay disabled");
            };
            view.DropDownItems.Add(_mnuOverlayBounds);

            _mnuOverlayHeatmap = new ToolStripMenuItem("Overlay: Overdraw Heatmap") { CheckOnClick = true };
            _mnuOverlayHeatmap.CheckedChanged += (s, e) =>
            {
                if (_mnuOverlayHeatmap.Checked) _mnuOverlayBounds.Checked = false;
                _canvas.OverlayMode = _mnuOverlayHeatmap.Checked
                    ? DebugOverlayMode.OverdrawHeatmap
                    : DebugOverlayMode.None;
                SetStatus(_mnuOverlayHeatmap.Checked ? "Overdraw heatmap overlay enabled" : "Overlay disabled");
            };
            view.DropDownItems.Add(_mnuOverlayHeatmap);

            _mnuSizeWarnings = new ToolStripMenuItem("Overlay: Texture Size Warnings") { CheckOnClick = true };
            _mnuSizeWarnings.CheckedChanged += (s, e) =>
            {
                _canvas.ShowSizeWarnings = _mnuSizeWarnings.Checked;
                if (_mnuSizeWarnings.Checked) RefreshDebugStats();
                SetStatus(_mnuSizeWarnings.Checked ? "Texture size warnings enabled" : "Size warnings disabled");
            };
            view.DropDownItems.Add(_mnuSizeWarnings);

            view.DropDownItems.Add("Show VRAM Budget…", null, (s, e) => ShowVramBudgetDialog());
            view.DropDownItems.Add(new ToolStripSeparator());
            view.DropDownItems.Add("Pop Out Code Editor\tCtrl+E", null, (s, e) => ToggleCodePopout());

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
                RefreshLayerList(); if (!_codeBoxDirty) RefreshCode();
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

            // Layer list at bottom — header panel with label + Show All button
            var layerHeader = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = 24,
                BackColor = Color.FromArgb(45, 45, 48),
            };
            var lblLayers = new Label
            {
                Text      = "LAYER ORDER (bottom → top)",
                Dock      = DockStyle.Fill,
                ForeColor = Color.FromArgb(140, 140, 140),
                Font      = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                Padding   = new Padding(2, 4, 0, 0),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            _btnShowAll = new Button
            {
                Text      = "👁 Show All",
                Dock      = DockStyle.Right,
                Width     = 85,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 110, 60),
                ForeColor = Color.White,
                Font      = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                Visible   = false,
                Cursor    = Cursors.Hand,
            };
            _btnShowAll.FlatAppearance.BorderSize = 0;
            _btnShowAll.Click += (s, e) => RestoreFullView();
            layerHeader.Controls.Add(lblLayers);
            layerHeader.Controls.Add(_btnShowAll);
            _lstLayers = new ListBox
            {
                Dock          = DockStyle.Bottom,
                Height        = 100,
                BackColor     = Color.FromArgb(30, 30, 30),
                ForeColor     = Color.FromArgb(215, 215, 215),
                BorderStyle   = BorderStyle.None,
                Font          = new Font("Segoe UI", 8.5f),
                SelectionMode = SelectionMode.MultiExtended,
            };
            var layerTooltip = new ToolTip();
            layerTooltip.SetToolTip(_lstLayers,
                "Layer order (bottom → top)\n" +
                "⊘ = hidden  |  [REF] = reference layout  |  ⚠ = game data  |  · = untracked\n" +
                "// variable = code variable name\n" +
                "Double-click to jump to code definition");
            _lstLayers.SelectedIndexChanged += OnLayerListSelectionChanged;
            _lstLayers.MouseDoubleClick  += OnLayerListDoubleClick;
            _lstLayers.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    int idx = _lstLayers.IndexFromPoint(e.Location);
                    if (idx >= 0 && !_lstLayers.SelectedIndices.Contains(idx))
                    {
                        // Right-clicked on an unselected item — select only that one
                        _lstLayers.SelectedIndex = idx;
                    }
                    // If right-clicked on an already-selected item, keep multi-select
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

            // ── Runtime Data Indicator ─────────────────────────────────────────
            _lblRuntimeData = new Label
            {
                Text      = "\u26A0 Game data — edit position/color/size, not text",
                Width     = 230,
                Height    = 36,
                ForeColor = Color.FromArgb(255, 200, 80),
                Font      = new Font("Segoe UI", 7.5f, FontStyle.Italic),
                BackColor = Color.FromArgb(60, 50, 20),
                Padding   = new Padding(4, 2, 4, 2),
                Visible   = false,
            };
            flow.Controls.Add(_lblRuntimeData);

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
            outer.Controls.Add(layerHeader);
            outer.Controls.Add(header);
            return outer;
        }

        // ── Code output panel ─────────────────────────────────────────────────────
        private void OnExecCodeClick(object sender, EventArgs e)
        {
            // Stop any running animation — single-shot execute replaces it
            _animPlayer?.Stop();
            UpdateAnimButtonStates();

            if (_layout == null) { SetStatus("No layout loaded."); return; }

            // When a one-way live source owns the canvas, don't overwrite it.
            // If we have captured row data, re-render from that instead.
            if (IsOneWayStreaming)
            {
                if (_layout?.CapturedRows?.Count > 0)
                    AutoExecuteWithCapturedRows(null, _layout.CapturedRows.Count);
                else
                    SetStatus("Pause the live stream to execute code manually.");
                return;
            }

            string code = _codeBox.Text;
            if (string.IsNullOrWhiteSpace(code)) { SetStatus("Code panel is empty."); return; }

            // Track if user had edited the code (e.g., inserted a template).
            // We'll preserve their code instead of regenerating from captured sprites.
            bool preserveUserCode = _codeBoxDirty;
            string originalCodeText = preserveUserCode ? code : null;

            // Clear any stale OriginalSourceCode when user executes from the code box.
            // Templates with expressions (like "thermX") can't round-trip properly —
            // the parser may return sprites with default values that don't match the code.
            // Starting fresh prevents mixing template code with generated literal code.
            if (_codeBoxDirty)
                _layout.OriginalSourceCode = null;

            // Use stored call expression, or auto-detect from the code
            string call = _execCallBox.Text.Trim();

            // Virtual switch-case methods (e.g. RenderHeader) cannot be compiled
            // as direct calls — redirect to isolate mode which handles them.
            if (!string.IsNullOrWhiteSpace(call) && IsVirtualSwitchCaseCall(call))
            {
                IsolateCallSprites(call);
                return;
            }

            if (string.IsNullOrWhiteSpace(call))
            {
                call = CodeExecutor.DetectCallExpression(code);
                if (call == null)
                {
                    // Fallback: if the code contains bare frame.Add(new MySprite patterns
                    // (e.g. snapshot output from a Torch plugin), parse sprites directly
                    // instead of requiring a method wrapper.
                    if (code.Contains("new MySprite"))
                    {
                        var parsed = Services.CodeParser.Parse(code);
                        if (parsed.Count > 0)
                        {
                            PushUndo();

                            // SNAPSHOT PRESERVATION: If snapshot exists, merge positions with parsed code
                            bool hadImportSnapshot = _lastLiveFrame != null && _lastLiveFrame.Count > 0;
                            if (hadImportSnapshot)
                            {
                                var mergeResult = SnapshotMerger.Merge(parsed, _lastLiveFrame, applyColors: false);
                                foreach (var sp in parsed)
                                {
                                    if (sp.ImportBaseline != null)
                                        sp.ImportBaseline = sp.CloneValues();
                                }
                            }

                            _layout.Sprites.Clear();
                            _layout.Sprites.AddRange(parsed);

                            // Note: We intentionally do NOT set OriginalSourceCode here.
                            // The parser may have returned sprites with default values
                            // if the code uses expressions (like "thermX" in templates).
                            // Clearing it at the start of OnExecCodeClick handles this.

                            _canvas.CanvasLayout = _layout;
                            _canvas.SelectedSprite = parsed.Count > 0 ? parsed[0] : null;
                            RefreshLayerList();
                            RefreshCode();  // Will be skipped if _codeBoxDirty (template code)
                            _execResultLabel.Text      = "✔ " + parsed.Count + " sprites [Import]";
                            _execResultLabel.ForeColor = Color.FromArgb(80, 220, 100);
                            SetStatus($"Imported {parsed.Count} sprite(s) from pasted code" + (hadImportSnapshot ? " (snapshot positions preserved)" : ""));
                            return;
                        }
                    }

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

            CodeExecutor.AnimationContext execCtx;
            var result = CodeExecutor.ExecuteWithInit(code, call, _layout?.CapturedRows, out execCtx);
            if (!result.Success)
            {
                execCtx?.Dispose();
                _execResultLabel.Text      = "✗ Error";
                _execResultLabel.ForeColor = Color.FromArgb(220, 80, 80);
                AppendConsoleError(result.Error);
                MessageBox.Show(result.Error, "Execution Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Show Echo output in Console tab
            AppendConsoleOutput(result.OutputLines);

            string typeTag = result.ScriptType == ScriptType.ProgrammableBlock ? " [PB]"
                           : result.ScriptType == ScriptType.ModSurface        ? " [Mod]"
                           : result.ScriptType == ScriptType.PulsarPlugin      ? " [Pulsar]"
                           : result.ScriptType == ScriptType.TorchPlugin       ? " [Torch]"
                           : "";
            _execResultLabel.Text      = "✔ " + result.Sprites.Count + typeTag;
            _execResultLabel.ForeColor = Color.FromArgb(80, 220, 100);

            PushUndo();

            // SNAPSHOT PRESERVATION: If there's a captured snapshot (_lastLiveFrame),
            // merge its positions with the execution results so snapshot edits survive Execute Code
            bool hadSnapshot = _lastLiveFrame != null && _lastLiveFrame.Count > 0;
            if (hadSnapshot)
            {
                var mergeResult = SnapshotMerger.Merge(result.Sprites, _lastLiveFrame, applyColors: false);
                // Update execution results to use merged positions
                foreach (var sp in result.Sprites)
                {
                    if (sp.ImportBaseline != null)
                        sp.ImportBaseline = sp.CloneValues();
                }
            }

            // Tag text sprites that originate from runtime game data
            TagSnapshotSprites(result.Sprites);

            // When source-tracked sprites exist, merge execution results as a
            // snapshot so round-trip editing and click-to-jump continue to work.
            // Skip merge for Pulsar/Mod/Torch scripts — their sprites come from runtime
            // surface.DrawFrame() calls and don't match CodeParser's static analysis,
            // causing all execution sprites to be added as orphans (doubling).
            // ALSO skip when we just applied snapshot positions (hadSnapshot=true)
            // because the second merge would overwrite the snapshot positions.
            bool useFullReplacement = result.ScriptType == ScriptType.PulsarPlugin
                                   || result.ScriptType == ScriptType.ModSurface
                                   || result.ScriptType == ScriptType.TorchPlugin
                                   || hadSnapshot;

            if (!useFullReplacement && _layout.OriginalSourceCode != null)
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
                    if (!preserveUserCode)
                        RefreshCode();
                    SetStatus($"Executed — {mergeResult.Summary}");
                    RefreshDebugStats();
                    AutoInspectVariablesAfterExecution(execCtx);
                    return;
                }
            }

            // No source tracking — full replacement
            _layout.Sprites.Clear();
            foreach (var sp in result.Sprites)
            {
                // Mark sprites as coming from execution so they're not treated as "new"
                sp.IsFromExecution = true;
                _layout.Sprites.Add(sp);
            }

            // ── Determine script type BEFORE source tracking so we can set
            //    OriginalSourceCode for Pulsar/Mod (needed by the tracking section). ──
            bool isPulsarOrMod = result.ScriptType == ScriptType.PulsarPlugin
                              || result.ScriptType == ScriptType.ModSurface
                              || result.ScriptType == ScriptType.TorchPlugin;

            if (isPulsarOrMod)
            {
                // Pulsar/Mod/Torch: set OriginalSourceCode so source tracking works
                // and PatchOriginalSource can do surgical round-trip patching later.
                _layout.OriginalSourceCode = code;
                _layout.IsPulsarOrModLayout = true;
                ClearCodeDirty();
            }
            else if (result.ScriptType == ScriptType.ProgrammableBlock)
            {
                // PB scripts: preserve the user's code so RefreshCode doesn't regenerate
                // from scratch (which destroys expressions, animation data, PB structure).
                _layout.OriginalSourceCode = code;
                _layout.IsPulsarOrModLayout = false;
                ClearCodeDirty();
            }
            else
            {
                // For other script types (templates, etc.) without source tracking,
                // clear OriginalSourceCode to prevent round-trip patching issues.
                _layout.OriginalSourceCode = null;
                _layout.IsPulsarOrModLayout = false;
            }

            // ══════════════════════════════════════════════════════════════════════════
            // ESTABLISH SOURCE TRACKING: Execution sprites have runtime values but no
            // SourceStart/SourceEnd/ImportBaseline, so PatchOriginalSource can't
            // round-trip property edits (text, color, etc.) back to code.
            // Strategy 1: Match by type+content (works for literal texture names, etc.)
            // Strategy 2: Match by SourceLineNumber (works for expression-based text)
            // Strategy 3: Index-based matching (PB/Pulsar/Mod fallback when counts match)
            // ══════════════════════════════════════════════════════════════════════════
            if (_layout.OriginalSourceCode != null)
            {
                var parsedSprites = CodeParser.Parse(_layout.OriginalSourceCode);
                System.Diagnostics.Debug.WriteLine($"[ExecuteCode] OriginalSourceCode length={_layout.OriginalSourceCode.Length}, parsed {parsedSprites.Count} sprites, execution has {_layout.Sprites.Count} sprites");
                if (parsedSprites.Count > 0)
                {
                    // Sort by SourceStart so matching is in source order
                    parsedSprites.Sort((a, b) =>
                    {
                        if (a.SourceStart < 0 && b.SourceStart < 0) return 0;
                        if (a.SourceStart < 0) return 1;
                        if (b.SourceStart < 0) return -1;
                        return a.SourceStart.CompareTo(b.SourceStart);
                    });

                    // ── Strategy 1: Type+Content matching ────────────────────────
                    var pool = new Dictionary<string, Queue<SpriteEntry>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var ps in parsedSprites)
                    {
                        string key = ps.Type == SpriteEntryType.Text
                            ? "TEXT|" + (ps.Text ?? "")
                            : "TEXTURE|" + (ps.SpriteName ?? "");
                        if (!pool.ContainsKey(key))
                            pool[key] = new Queue<SpriteEntry>();
                        pool[key].Enqueue(ps);
                    }

                    int tracked = 0;
                    int unmatched = 0;
                    foreach (var sp in _layout.Sprites)
                    {
                        string key = sp.Type == SpriteEntryType.Text
                            ? "TEXT|" + (sp.Text ?? "")
                            : "TEXTURE|" + (sp.SpriteName ?? "");

                        if (pool.TryGetValue(key, out var queue) && queue.Count > 0)
                        {
                            var parsed = queue.Dequeue();
                            sp.SourceStart = parsed.SourceStart;
                            sp.SourceEnd = parsed.SourceEnd;
                            sp.ImportBaseline = sp.CloneValues();
                            tracked++;
                        }
                        else
                        {
                            unmatched++;
                        }
                    }

                    System.Diagnostics.Debug.WriteLine($"[ExecuteCode] Strategy 1 (type+content): {tracked} matched, {unmatched} unmatched out of {_layout.Sprites.Count} sprites");

                    // ── Strategy 2: SourceLineNumber matching (fallback) ─────────
                    // Handles expression-based text sprites where runtime text differs
                    // from code literals (e.g. code has $"Temp: {val}°C", runtime has
                    // "Temp: 22.5°C"). Uses the reliable SourceLineNumber from Phase 8
                    // instrumentation to locate the parsed sprite at that line.
                    if (unmatched > 0)
                    {
                        // Build a line-number → parsed sprite lookup from remaining unmatched parsed sprites
                        // (those still in the pool queues are already consumed; use direct line computation)
                        string src = _layout.OriginalSourceCode;
                        int lineNumberFallback = 0;
                        foreach (var sp in _layout.Sprites)
                        {
                            if (sp.SourceStart >= 0) continue; // already matched
                            if (sp.SourceLineNumber <= 0) continue; // no line info

                            // Find the parsed sprite whose SourceStart falls on sp.SourceLineNumber
                            int targetLine = sp.SourceLineNumber;
                            SpriteEntry bestMatch = null;
                            foreach (var ps in parsedSprites)
                            {
                                if (ps.SourceStart < 0) continue;
                                // Compute line number of this parsed sprite
                                int psLine = 1;
                                for (int ci = 0; ci < ps.SourceStart && ci < src.Length; ci++)
                                    if (src[ci] == '\n') psLine++;

                                if (psLine == targetLine && ps.Type == sp.Type)
                                {
                                    bestMatch = ps;
                                    break;
                                }
                            }

                            if (bestMatch != null)
                            {
                                sp.SourceStart = bestMatch.SourceStart;
                                sp.SourceEnd = bestMatch.SourceEnd;
                                sp.ImportBaseline = sp.CloneValues();
                                lineNumberFallback++;
                            }
                        }

                        if (lineNumberFallback > 0)
                            System.Diagnostics.Debug.WriteLine($"[ExecuteCode] Strategy 2 (SourceLineNumber): {lineNumberFallback} additional sprites matched");
                    }

                    // ── Strategy 3: Index-based matching (all-or-nothing fallback) ──
                    // When type+content and line-number matching leave sprites untracked
                    // AND parsed/execution counts match with aligned types, use positional
                    // index: 1st parsed → 1st execution, etc. Works for interpolated strings
                    // in PB/Pulsar/Mod where runtime text differs from code expressions.
                    bool hasUntracked = false;
                    foreach (var sp in _layout.Sprites)
                        if (sp.SourceStart < 0) { hasUntracked = true; break; }

                    if (hasUntracked && parsedSprites.Count == _layout.Sprites.Count)
                    {
                        bool typesMatch = true;
                        for (int i = 0; i < parsedSprites.Count; i++)
                        {
                            if (parsedSprites[i].Type != _layout.Sprites[i].Type)
                            {
                                typesMatch = false;
                                break;
                            }
                        }

                        if (typesMatch)
                        {
                            for (int i = 0; i < parsedSprites.Count; i++)
                            {
                                _layout.Sprites[i].SourceStart = parsedSprites[i].SourceStart;
                                _layout.Sprites[i].SourceEnd = parsedSprites[i].SourceEnd;
                                _layout.Sprites[i].ImportBaseline = _layout.Sprites[i].CloneValues();
                            }
                            System.Diagnostics.Debug.WriteLine($"[ExecuteCode] Strategy 3 (index-based): {parsedSprites.Count} sprites matched by position");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[ExecuteCode] Strategy 3 (index-based): type mismatch, skipping");
                        }
                    }
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[ExecuteCode] OriginalSourceCode is NULL — skipping source tracking");
            }

            // Suppress RefreshCode calls from OnSelectionChanged during execution.
            // Without this, expression-based PB code is destroyed: OnSelectionChanged
            // fires RefreshCode → PatchOriginalSource fails for expression text →
            // GenerateRoundTrip replaces expressions with literal evaluated values.
            _executingCode = true;
            try
            {
            _canvas.SelectedSprite = result.Sprites.Count > 0 ? result.Sprites[0] : null;
            _canvas.Invalidate();
            RefreshLayerList();

            // DIAGNOSTIC: Log sprite method attribution for code jump debugging
            {
                int withMethod = 0, withIndex = 0, total = _layout.Sprites.Count;
                foreach (var sp in _layout.Sprites)
                {
                    if (!string.IsNullOrEmpty(sp.SourceMethodName)) withMethod++;
                    if (sp.SourceMethodIndex >= 0) withIndex++;
                }
                System.Diagnostics.Debug.WriteLine($"[ExecuteCode] Sprite method attribution: {withMethod}/{total} have SourceMethodName, {withIndex}/{total} have SourceMethodIndex");
                // Log first 50 sprites for debugging off-by-one navigation
                int logLimit = Math.Min(total, 50);
                for (int di = 0; di < logLimit; di++)
                {
                    var dsp = _layout.Sprites[di];
                    System.Diagnostics.Debug.WriteLine($"[ExecuteCode]   [{di}] {dsp.DisplayName}: Method='{dsp.SourceMethodName ?? "(null)"}' Idx={dsp.SourceMethodIndex} SourceStart={dsp.SourceStart}");
                }
                if (total > logLimit)
                    System.Diagnostics.Debug.WriteLine($"[ExecuteCode]   ... and {total - logLimit} more sprites");
            }
            } // try
            finally { _executingCode = false; }

            // Don't regenerate code for Pulsar/Mod scripts - keep original code intact.
            // For PB scripts with OriginalSourceCode set, also skip — RefreshCode would
            // destroy expression-based code (interpolated strings, ternary colors) by
            // falling through PatchOriginalSource → GenerateRoundTrip → literal replacement.
            // The original user code is already displayed correctly at this point.
            if (!isPulsarOrMod && _layout.OriginalSourceCode == null)
                RefreshCode();

            SetStatus($"Executed — {result.Sprites.Count} sprite(s) captured.");
            RefreshDebugStats();

            // ══════════════════════════════════════════════════════════════════════════
            // AUTOMATIC VARIABLE INSPECTION: Show instance fields after execution
            // Reuses the already-compiled context to avoid a second Roslyn compilation.
            // ══════════════════════════════════════════════════════════════════════════
            AutoInspectVariablesAfterExecution(execCtx);

            // SpriteMappingBuilder removed: the instrumentation pipeline (SetCurrentMethod + RecordSpriteMethod)
            // now tags each sprite with SourceMethodName/SourceMethodIndex at execution time, making the
            // expensive re-compile-and-re-run approach redundant. Isolation fallback uses SourceMethodName.
        }

        // ── Variable Inspector Test ───────────────────────────────────────────────

        /// <summary>
        /// Tests the variable inspector MVP by compiling the current code as an animation
        /// and displaying all instance fields using reflection.
        /// 
        /// USAGE:
        /// 1. Paste/write code with class-level fields (e.g., int counter; float angle;)
        /// 2. Click "🔍 Inspect Variables" in the code panel toolbar
        /// 3. See all instance fields and their initial values after constructor runs
        /// 
        /// EXAMPLE CODE TO TEST:
        /// <code>
        /// int counter = 0;
        /// float angle = 0f;
        /// string[] items = new string[] { "foo", "bar", "baz" };
        /// 
        /// public void DrawHUD(IMyTextSurface surface) {
        ///     counter++;
        ///     angle += 0.1f;
        ///     // ... sprite drawing code ...
        /// }
        /// </code>
        /// </summary>
        private void TestVariableInspector()
        {
            string code = _codeBox.Text;
            if (string.IsNullOrWhiteSpace(code))
            {
                MessageBox.Show("No code to inspect.", "Variable Inspector", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string call = _execCallBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(call) && _detectedMethods != null && _detectedMethods.Count > 0)
            {
                call = _detectedMethods[0].CallExpression;
            }

            // Use AnimationPlayer to compile and initialize the script
            using (var animPlayer = new AnimationPlayer(this))
            {
                string prepError = animPlayer.Prepare(code, call, _layout?.CapturedRows);
                if (prepError != null)
                {
                    MessageBox.Show(prepError, "Variable Inspector - Compile Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Inspect all instance fields
                var fields = animPlayer.InspectFields();
                if (fields == null || fields.Count == 0)
                {
                    MessageBox.Show("No instance fields found.", "Variable Inspector", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // Populate ListView
                _lstVariables.Items.Clear();
                foreach (var kv in fields.OrderBy(x => x.Key))
                {
                    string value = FormatFieldValue(kv.Value);
                    var item = new ListViewItem(new[] { kv.Key, value });
                    item.ForeColor = Color.FromArgb(160, 220, 255);
                    _lstVariables.Items.Add(item);
                }

                // Format the output for MessageBox
                var output = new System.Text.StringBuilder();
                output.AppendLine($"Script Type: {animPlayer.ScriptType}");
                output.AppendLine($"Instance Fields ({fields.Count}):\n");

                foreach (var kv in fields.OrderBy(x => x.Key))
                {
                    string value = FormatFieldValue(kv.Value);
                    output.AppendLine($"{kv.Key} = {value}");
                }

                // Show in a scrollable message box + switch to Variables tab
                MessageBox.Show(output.ToString(), "Variable Inspector", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        /// <summary>
        /// Formats a field value for display in the variable inspector.
        /// </summary>
        private string FormatFieldValue(object value)
        {
            if (value == null)
                return "(null)";

            if (value is string str)
                return $"\"{str}\"";

            if (value is System.Collections.IEnumerable enumerable && !(value is string))
            {
                var items = new List<string>();
                int count = 0;
                foreach (var item in enumerable)
                {
                    if (count >= 5)
                    {
                        items.Add("...");
                        break;
                    }
                    items.Add(item?.ToString() ?? "(null)");
                    count++;
                }
                return $"[{string.Join(", ", items)}] (count: {count}{(count >= 5 ? "+" : "")})";
            }

            return value.ToString();
        }

        /// <summary>
        /// Gets the display color for a variable based on its type.
        /// VIBRANT colors for visibility on dark backgrounds.
        /// </summary>
        private Color GetColorForType(object value)
        {
            if (value == null)
                return Color.FromArgb(160, 160, 160); // Brighter gray for null

            var type = value.GetType();

            // Numeric types - Bright blue shades
            if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
                type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte))
                return Color.FromArgb(80, 160, 255); // Vivid blue for integers

            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                return Color.FromArgb(0, 220, 255); // Bright cyan for floats

            // String - Bright green
            if (type == typeof(string))
                return Color.FromArgb(80, 255, 120); // Vivid green

            // Boolean - Bright orange
            if (type == typeof(bool))
                return Color.FromArgb(255, 160, 60); // Vivid orange

            // VRageMath types - Bright purple/magenta
            if (type.Name.Contains("Vector") || type.Name.Contains("Matrix"))
                return Color.FromArgb(220, 100, 255); // Vivid purple for vectors/matrices

            if (type.Name.Contains("Color"))
                return Color.FromArgb(255, 220, 60); // Bright yellow for colors

            // Collections - Aqua
            if (value is System.Collections.IEnumerable && !(value is string))
                return Color.FromArgb(100, 200, 255); // Aqua for arrays/lists

            // Default for objects
            return Color.FromArgb(140, 200, 255); // Default light blue
        }

        /// <summary>
        /// Brightens a color to indicate a changed value during animation.
        /// </summary>
        private Color BrightenColor(Color baseColor)
        {
            // Increase RGB values by 30% and add yellow tint for visibility
            int r = Math.Min(255, baseColor.R + (int)((255 - baseColor.R) * 0.4f) + 20);
            int g = Math.Min(255, baseColor.G + (int)((255 - baseColor.G) * 0.4f) + 20);
            int b = Math.Min(255, baseColor.B + (int)((255 - baseColor.B) * 0.2f)); // Less blue boost
            return Color.FromArgb(r, g, b);
        }

        /// <summary>
        /// Automatically inspects variables after Execute Code completes successfully.
        /// Populates the Variables tab and writes to Debug output.
        /// When <paramref name="ctx"/> is non-null the already-compiled context is
        /// adopted (zero-cost), avoiding a second Roslyn compilation.  Falls back
        /// to the legacy compile path when ctx is null.
        /// </summary>
        private void AutoInspectVariablesAfterExecution(CodeExecutor.AnimationContext ctx)
        {
            try
            {
                // Dispose previous AnimationPlayer only if it's NOT the same as _animPlayer
                if (_lastAnimPlayer != null && _lastAnimPlayer != _animPlayer)
                {
                    _lastAnimPlayer.Dispose();
                }

                if (ctx != null)
                {
                    _lastAnimPlayer = new AnimationPlayer(this);
                    _lastAnimPlayer.AdoptContext(ctx);
                }
                else
                {
                    _lastAnimPlayer = null;
                    return;
                }

                var fields = _lastAnimPlayer.InspectFields();
                if (fields == null || fields.Count == 0)
                    return;

                // Populate ListView
                _lstVariables.Items.Clear();
                foreach (var kv in fields.OrderBy(x => x.Key))
                {
                    if (!ShouldShowField(kv.Key))
                        continue; // Skip internal fields

                    string value = FormatFieldValue(kv.Value);
                    var item = new ListViewItem(new[] { kv.Key, value, "" });
                    item.ForeColor = GetColorForType(kv.Value); // Type-based color coding
                    _lstVariables.Items.Add(item);
                }

                // Update tab title (no frame number for initial Execute)
                if (_tabVariables != null)
                {
                    _tabVariables.Text = "Variables";
                }

                // Write to Debug output for reference
                System.Diagnostics.Debug.WriteLine("\n========== VARIABLE INSPECTION ==========");
                System.Diagnostics.Debug.WriteLine($"Script Type: {_lastAnimPlayer.ScriptType}");
                System.Diagnostics.Debug.WriteLine($"Instance Fields ({fields.Count}):");

                foreach (var kv in fields.OrderBy(x => x.Key))
                {
                    string value = FormatFieldValue(kv.Value);
                    System.Diagnostics.Debug.WriteLine($"  {kv.Key} = {value}");
                }

                System.Diagnostics.Debug.WriteLine("=========================================\n");

                // Evaluate watch expressions with initial field values
                EvaluateWatches(_lastAnimPlayer);
            }
            catch (Exception ex)
            {
                // Silent failure - don't interrupt successful execution
                System.Diagnostics.Debug.WriteLine($"[AutoInspect] Error: {ex.Message}");
                if (_lastAnimPlayer != null && _lastAnimPlayer != _animPlayer)
                {
                    _lastAnimPlayer.Dispose();
                }
                _lastAnimPlayer = null;
            }
        }

        /// <summary>
        /// Determines if a field should be shown based on the "Show internal fields" checkbox.
        /// </summary>
        private bool ShouldShowField(string fieldName)
        {
            if (_chkShowInternalFields?.Checked == true)
                return true; // Show all fields

            // Hide fields starting with _ (private convention: _stubSurface, _timer)
            if (fieldName.StartsWith("_"))
                return false;

            // Hide compiler-generated fields (e.g., <>f__AnonymousType)
            if (fieldName.StartsWith("<"))
                return false;

            return true; // Show user-defined fields
        }

        /// <summary>
        /// Updates the Variables tab during animation playback to show live field values.
        /// </summary>
        private void UpdateVariablesDuringAnimation(int tick)
        {
            // If we have a dedicated animation player (not _lastAnimPlayer from Execute),
            // use it for inspection. Otherwise fall back to _lastAnimPlayer.
            AnimationPlayer inspectPlayer = _animPlayer ?? _lastAnimPlayer;

            if (inspectPlayer == null)
                return;

            try
            {
                var fields = inspectPlayer.InspectFields();
                if (fields == null || fields.Count == 0)
                    return;

                // Track previous values for change highlighting
                var previousValues = new Dictionary<string, string>();
                foreach (ListViewItem item in _lstVariables.Items)
                {
                    previousValues[item.Text] = item.SubItems[1].Text;
                }

                // Update ListView WITHOUT clearing (prevents flashing)
                _lstVariables.BeginUpdate();

                var orderedFields = fields.OrderBy(x => x.Key).Where(kv => ShouldShowField(kv.Key)).ToList();

                // If field count changed, rebuild the list
                if (_lstVariables.Items.Count != orderedFields.Count)
                {
                    _lstVariables.Items.Clear();
                    foreach (var kv in orderedFields)
                    {
                        string value = FormatFieldValue(kv.Value);
                        var item = new ListViewItem(new[] { kv.Key, value, "" });
                        item.ForeColor = GetColorForType(kv.Value);
                        _lstVariables.Items.Add(item);
                    }
                }
                else
                {
                    // Update existing items in-place (no flashing)
                    for (int i = 0; i < orderedFields.Count; i++)
                    {
                        var kv = orderedFields[i];
                        string value = FormatFieldValue(kv.Value);
                        var item = _lstVariables.Items[i];

                        // Update value if changed
                        if (item.SubItems[1].Text != value)
                        {
                            item.SubItems[1].Text = value;
                        }

                        // Get type-based color, brighten if value changed from previous tick
                        Color baseColor = GetColorForType(kv.Value);
                        if (previousValues.TryGetValue(kv.Key, out string oldValue) && oldValue != value)
                        {
                            item.ForeColor = BrightenColor(baseColor); // Brighten to show change
                        }
                        else
                        {
                            item.ForeColor = baseColor; // Normal type-based color
                        }
                    }
                }

                _lstVariables.EndUpdate();

                // Update tab title with tick number
                if (_tabVariables != null)
                {
                    _tabVariables.Text = $"Variables (Tick {tick})";
                }

                // Evaluate watch expressions each tick
                EvaluateWatches(inspectPlayer);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateVariables] Error: {ex.Message}");
            }
        }

        // ── Timeline scrubber ───────────────────────────────────────────────────

        /// <summary>
        /// Called each animation frame to keep the scrubber range and position
        /// in sync with the tick history.
        /// </summary>
        private void UpdateTimelineScrubber(int currentTick)
        {
            AnimationPlayer player = _animPlayer ?? _lastAnimPlayer;
            if (player == null || _timelineScrubber == null) return;

            var history = player.TickHistory;
            if (history == null || history.Count == 0)
            {
                _timelineBar.Visible = false;
                return;
            }

            _timelineBar.Visible = true;
            _timelineScrubber.Minimum = history.MinTick;
            _timelineScrubber.Maximum = Math.Max(history.MinTick, history.MaxTick);

            // Track live unless user is scrubbing
            if (!_isScrubbing)
            {
                _timelineScrubber.Value = Math.Min(currentTick, _timelineScrubber.Maximum);
                _lblTimelineTick.Text = $"Tick {currentTick} / {history.MaxTick}";
            }
        }

        /// <summary>
        /// Handles the user dragging the timeline scrubber to a historical tick.
        /// Populates the Variables tab from the stored snapshot.
        /// </summary>
        private void OnTimelineScrub(object sender, EventArgs e)
        {
            if (_timelineScrubber == null) return;

            int scrubTick = _timelineScrubber.Value;
            _isScrubbing = true;
            _lblTimelineTick.Text = $"⏪ Tick {scrubTick}";

            AnimationPlayer player = _animPlayer ?? _lastAnimPlayer;
            if (player == null) return;

            var history = player.TickHistory;
            if (history == null || history.Count == 0) return;

            var snapshot = history.GetSnapshot(scrubTick);
            if (snapshot == null) return;

            // Populate _lstVariables from the historical snapshot
            _lstVariables.BeginUpdate();
            var orderedFields = new List<KeyValuePair<string, object>>();
            foreach (var kv in snapshot)
            {
                if (ShouldShowField(kv.Key))
                    orderedFields.Add(kv);
            }
            orderedFields.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));

            if (_lstVariables.Items.Count != orderedFields.Count)
            {
                _lstVariables.Items.Clear();
                foreach (var kv in orderedFields)
                {
                    string value = FormatFieldValue(kv.Value);
                    var item = new ListViewItem(new[] { kv.Key, value, "" });
                    item.ForeColor = GetColorForType(kv.Value);
                    _lstVariables.Items.Add(item);
                }
            }
            else
            {
                for (int i = 0; i < orderedFields.Count; i++)
                {
                    var kv = orderedFields[i];
                    string value = FormatFieldValue(kv.Value);
                    var item = _lstVariables.Items[i];
                    if (item.Text != kv.Key) item.Text = kv.Key;
                    if (item.SubItems[1].Text != value) item.SubItems[1].Text = value;
                    item.ForeColor = GetColorForType(kv.Value);
                }
            }
            _lstVariables.EndUpdate();

            if (_tabVariables != null)
                _tabVariables.Text = $"Variables (Tick {scrubTick})";
        }

        /// <summary>
        /// Resets the scrubbing flag so the scrubber tracks live ticks again.
        /// Called when animation resumes or a new frame arrives after the user
        /// stops dragging.
        /// </summary>
        private void ResetScrubbing()
        {
            _isScrubbing = false;
        }

        // ── Snapshot Comparison Bookmarks ────────────────────────────────────────

        private void OnBookmarkAClick(object sender, EventArgs e)
        {
            AnimationPlayer player = _animPlayer ?? _lastAnimPlayer;
            if (player == null) return;

            int tick = _isScrubbing ? _timelineScrubber.Value : player.CurrentTick;
            player.SetBookmarkA(tick);
            UpdateBookmarkLabel(player);
        }

        private void OnBookmarkBClick(object sender, EventArgs e)
        {
            AnimationPlayer player = _animPlayer ?? _lastAnimPlayer;
            if (player == null) return;

            int tick = _isScrubbing ? _timelineScrubber.Value : player.CurrentTick;
            player.SetBookmarkB(tick);
            UpdateBookmarkLabel(player);
        }

        private void OnCompareSnapshotsClick(object sender, EventArgs e)
        {
            AnimationPlayer player = _animPlayer ?? _lastAnimPlayer;
            if (player == null) return;

            var spriteHistory = player.SpriteHistory;
            if (spriteHistory == null) return;

            int tickA = spriteHistory.BookmarkTickA;
            int tickB = spriteHistory.BookmarkTickB;
            if (tickA < 0 || tickB < 0) return;

            var beforeSnap = spriteHistory.GetSnapshot(tickA);
            var afterSnap  = spriteHistory.GetSnapshot(tickB);

            var changes = Services.SnapshotComparer.Compare(beforeSnap, afterSnap);
            ShowSpriteComparisonDiff(changes, tickA, tickB,
                beforeSnap?.Length ?? 0, afterSnap?.Length ?? 0);

            SetStatus($"Compared tick {tickA} vs {tickB}: {changes.Count} change(s).");
        }

        private void UpdateBookmarkLabel(AnimationPlayer player)
        {
            if (_lblBookmarks == null) return;

            var history = player?.SpriteHistory;
            int a = history?.BookmarkTickA ?? -1;
            int b = history?.BookmarkTickB ?? -1;

            string aText = a >= 0 ? $"A=Tick {a}" : "A=—";
            string bText = b >= 0 ? $"B=Tick {b}" : "B=—";
            _lblBookmarks.Text = $"{aText}  |  {bText}";

            _btnCompareSnapshots.Enabled = a >= 0 && b >= 0;
        }

        /// <summary>
        /// Formats sprite comparison results into the Diff tab.
        /// Left panel shows "Before" sprite states, right panel shows "After".
        /// </summary>
        private void ShowSpriteComparisonDiff(
            List<Services.SpriteChange> changes,
            int tickA, int tickB,
            int totalBefore, int totalAfter)
        {
            if (_rtbDiffBefore == null || _rtbDiffAfter == null) return;

            _rtbDiffBefore.SuspendLayout();
            _rtbDiffAfter.SuspendLayout();
            _rtbDiffBefore.Clear();
            _rtbDiffAfter.Clear();

            var colorMoved     = Color.FromArgb(25, 35, 60);    // dark blue
            var colorRecolored = Color.FromArgb(40, 25, 50);    // dark purple
            var colorResized   = Color.FromArgb(50, 35, 15);    // dark orange
            var colorAdded     = Color.FromArgb(25, 50, 25);    // dark green
            var colorRemoved   = Color.FromArgb(60, 30, 30);    // dark red
            var colorDefault   = Color.FromArgb(18, 20, 30);

            var fgMoved     = Color.FromArgb(120, 170, 255);
            var fgRecolored = Color.FromArgb(200, 140, 255);
            var fgResized   = Color.FromArgb(255, 180, 100);
            var fgAdded     = Color.FromArgb(140, 255, 140);
            var fgRemoved   = Color.FromArgb(255, 140, 140);
            var fgDefault   = Color.FromArgb(100, 110, 140);
            var fgHeader    = Color.FromArgb(180, 200, 240);

            // Headers
            AppendDiffLine(_rtbDiffBefore, $"  ── Before: Tick {tickA} ({totalBefore} sprites) ──", colorDefault, fgHeader);
            AppendDiffLine(_rtbDiffAfter,  $"  ── After:  Tick {tickB} ({totalAfter} sprites) ──",  colorDefault, fgHeader);

            if (changes.Count == 0)
            {
                AppendDiffLine(_rtbDiffBefore, "  No visual changes detected.", colorDefault, fgDefault);
                AppendDiffLine(_rtbDiffAfter,  "  No visual changes detected.", colorDefault, fgDefault);
            }
            else
            {
                foreach (var c in changes)
                {
                    Color bgBefore, bgAfter, fgBefore, fgAfter;

                    if ((c.Kind & Services.SpriteChangeKind.Added) != 0)
                    {
                        bgBefore = colorDefault; bgAfter = colorAdded;
                        fgBefore = fgDefault;    fgAfter = fgAdded;
                        AppendDiffLine(_rtbDiffBefore, $"  [{c.Index}] (not present)", bgBefore, fgBefore);
                        AppendDiffLine(_rtbDiffAfter,  $"  [{c.Index}] + {FormatSpriteSnap(c.After.Value)}", bgAfter, fgAfter);
                    }
                    else if ((c.Kind & Services.SpriteChangeKind.Removed) != 0)
                    {
                        bgBefore = colorRemoved; bgAfter = colorDefault;
                        fgBefore = fgRemoved;    fgAfter = fgDefault;
                        AppendDiffLine(_rtbDiffBefore, $"  [{c.Index}] - {FormatSpriteSnap(c.Before.Value)}", bgBefore, fgBefore);
                        AppendDiffLine(_rtbDiffAfter,  $"  [{c.Index}] (removed)", bgAfter, fgAfter);
                    }
                    else
                    {
                        // Modified — pick color based on primary change type
                        if ((c.Kind & Services.SpriteChangeKind.Moved) != 0)
                        { bgBefore = colorMoved; bgAfter = colorMoved; fgBefore = fgMoved; fgAfter = fgMoved; }
                        else if ((c.Kind & Services.SpriteChangeKind.Recolored) != 0)
                        { bgBefore = colorRecolored; bgAfter = colorRecolored; fgBefore = fgRecolored; fgAfter = fgRecolored; }
                        else if ((c.Kind & Services.SpriteChangeKind.Resized) != 0)
                        { bgBefore = colorResized; bgAfter = colorResized; fgBefore = fgResized; fgAfter = fgResized; }
                        else
                        { bgBefore = colorDefault; bgAfter = colorDefault; fgBefore = fgDefault; fgAfter = fgDefault; }

                        AppendDiffLine(_rtbDiffBefore, $"  [{c.Index}] {FormatSpriteSnap(c.Before.Value)}", bgBefore, fgBefore);
                        AppendDiffLine(_rtbDiffAfter,  $"  [{c.Index}] {FormatSpriteSnap(c.After.Value)}  ← {c.Summary}", bgAfter, fgAfter);
                    }
                }
            }

            _rtbDiffBefore.SelectionStart = 0;
            _rtbDiffAfter.SelectionStart  = 0;
            _rtbDiffBefore.ResumeLayout();
            _rtbDiffAfter.ResumeLayout();

            _tabDiff.Text = changes.Count > 0 ? $"Diff ({changes.Count})" : "Diff";
        }

        private static string FormatSpriteSnap(Models.SpriteSnapshotEntry sp)
        {
            string name = sp.Type == Models.SpriteEntryType.Text
                ? $"TEXT \"{(sp.Text?.Length > 15 ? sp.Text.Substring(0, 12) + "..." : sp.Text)}\""
                : sp.SpriteName ?? "(unnamed)";
            return $"{name}  pos=({sp.X:F1},{sp.Y:F1})  size=({sp.Width:F1}×{sp.Height:F1})  color=({sp.ColorR},{sp.ColorG},{sp.ColorB},{sp.ColorA})";
        }

        // ── Variables ListView owner-draw (sparklines) ──────────────────────────

        private void LstVariables_DrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            e.DrawDefault = true;
        }

        private void LstVariables_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            // Columns 0 (Field) and 1 (Value): default text drawing
            if (e.ColumnIndex < 2)
            {
                // Use item forecolor for proper highlighting support
                var textColor = e.Item.ForeColor;
                using (var brush = new System.Drawing.SolidBrush(textColor))
                {
                    var sf = new System.Drawing.StringFormat
                    {
                        Alignment     = StringAlignment.Near,
                        LineAlignment = StringAlignment.Center,
                        Trimming      = StringTrimming.EllipsisCharacter,
                        FormatFlags   = StringFormatFlags.NoWrap,
                    };
                    var rect = e.Bounds;
                    rect.X += 2;
                    rect.Width -= 4;
                    e.Graphics.DrawString(e.SubItem.Text, _lstVariables.Font, brush, rect, sf);
                }
                return;
            }

            // Column 2 (Trend): draw sparkline for numeric fields
            AnimationPlayer player = _animPlayer ?? _lastAnimPlayer;
            var history = player?.TickHistory;
            if (history == null || history.Count < 2)
            {
                e.DrawDefault = false;
                return;
            }

            string fieldName = e.Item.Text;
            int[] ticks;
            float[] values;
            history.GetNumericSeries(fieldName, out ticks, out values);

            // Check if this field has any real numeric data
            bool hasData = false;
            for (int i = 0; i < values.Length; i++)
            {
                if (!float.IsNaN(values[i])) { hasData = true; break; }
            }

            if (!hasData) return;

            // Draw sparkline
            var bounds = e.Bounds;
            int pad = 2;
            int left = bounds.X + pad;
            int top = bounds.Y + pad;
            int width = bounds.Width - pad * 2;
            int height = bounds.Height - pad * 2;
            if (width < 8 || height < 4) return;

            // Find min/max for scaling
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < values.Length; i++)
            {
                if (float.IsNaN(values[i])) continue;
                if (values[i] < min) min = values[i];
                if (values[i] > max) max = values[i];
            }

            float range = max - min;
            if (range < 0.0001f) range = 1f; // flat line

            // Build points
            var points = new List<System.Drawing.Point>();
            for (int i = 0; i < values.Length; i++)
            {
                if (float.IsNaN(values[i])) continue;
                int px = left + (int)((float)i / Math.Max(1, values.Length - 1) * width);
                int py = top + height - (int)((values[i] - min) / range * height);
                points.Add(new System.Drawing.Point(px, py));
            }

            if (points.Count >= 2)
            {
                using (var pen = new System.Drawing.Pen(Color.FromArgb(100, 200, 255), 1f))
                {
                    e.Graphics.DrawLines(pen, points.ToArray());
                }

                // Draw current value dot (last point)
                var last = points[points.Count - 1];
                using (var dotBrush = new System.Drawing.SolidBrush(Color.FromArgb(255, 220, 80)))
                {
                    e.Graphics.FillEllipse(dotBrush, last.X - 2, last.Y - 2, 4, 4);
                }
            }
        }

        // ── Sprite-to-Variable linking ──────────────────────────────────────────

        /// <summary>
        /// Scans a source code region for identifiers that match known runner
        /// field names.  Returns the set of field names referenced in the region.
        /// </summary>
        private static HashSet<string> GetLinkedFieldNames(
            string sourceCode, int start, int end, ICollection<string> knownFields)
        {
            var linked = new HashSet<string>();
            if (string.IsNullOrEmpty(sourceCode) || start < 0 || end <= start)
                return linked;

            start = Math.Max(0, start);
            end = Math.Min(sourceCode.Length, end);
            string region = sourceCode.Substring(start, end - start);

            foreach (var field in knownFields)
            {
                if (string.IsNullOrEmpty(field)) continue;

                int idx = 0;
                while (idx < region.Length)
                {
                    int pos = region.IndexOf(field, idx, StringComparison.Ordinal);
                    if (pos < 0) break;

                    // Ensure it's a whole-word match (not part of a longer identifier)
                    bool leftOk = pos == 0 || !char.IsLetterOrDigit(region[pos - 1]) && region[pos - 1] != '_';
                    int afterEnd = pos + field.Length;
                    bool rightOk = afterEnd >= region.Length || !char.IsLetterOrDigit(region[afterEnd]) && region[afterEnd] != '_';

                    if (leftOk && rightOk)
                    {
                        linked.Add(field);
                        break; // found once is enough
                    }
                    idx = pos + 1;
                }
            }

            return linked;
        }

        /// <summary>
        /// Highlights rows in the Variables tab that correspond to fields
        /// referenced in the selected sprite's source code region.
        /// Resets all rows to normal when nothing is selected or no links found.
        /// </summary>
        private void HighlightLinkedVariables(SpriteEntry sprite)
        {
            if (_lstVariables == null || _lstVariables.Items.Count == 0) return;

            AnimationPlayer player = _animPlayer ?? _lastAnimPlayer;
            HashSet<string> linked = null;

            if (sprite != null && player != null)
            {
                string sourceCode = _layout?.OriginalSourceCode ?? _codeBox?.Text;
                var fields = player.InspectFields();

                if (fields != null && fields.Count > 0 && !string.IsNullOrEmpty(sourceCode))
                {
                    // Primary: use SourceStart/SourceEnd character offsets
                    if (sprite.SourceStart >= 0 && sprite.SourceEnd > sprite.SourceStart)
                    {
                        linked = GetLinkedFieldNames(sourceCode, sprite.SourceStart, sprite.SourceEnd, fields.Keys);
                    }

                    // Fallback: use SourceCodeSnippet if available
                    if ((linked == null || linked.Count == 0) && !string.IsNullOrEmpty(sprite.SourceCodeSnippet))
                    {
                        linked = GetLinkedFieldNames(sprite.SourceCodeSnippet, 0, sprite.SourceCodeSnippet.Length, fields.Keys);
                    }

                    // Fallback: if sprite has a SourceMethodName, scan that entire method body
                    if ((linked == null || linked.Count == 0) && !string.IsNullOrEmpty(sprite.SourceMethodName))
                    {
                        int mStart = sourceCode.IndexOf(sprite.SourceMethodName, StringComparison.Ordinal);
                        if (mStart >= 0)
                        {
                            // Find the method body (from method name to next closing brace block)
                            int braceStart = sourceCode.IndexOf('{', mStart);
                            if (braceStart >= 0)
                            {
                                int depth = 1;
                                int scan = braceStart + 1;
                                while (scan < sourceCode.Length && depth > 0)
                                {
                                    if (sourceCode[scan] == '{') depth++;
                                    else if (sourceCode[scan] == '}') depth--;
                                    scan++;
                                }
                                linked = GetLinkedFieldNames(sourceCode, braceStart, scan, fields.Keys);
                            }
                        }
                    }
                }
            }

            // Apply highlighting
            bool hasLinks = linked != null && linked.Count > 0;
            Color linkedColor = Color.FromArgb(255, 220, 80);     // Bright gold for linked fields
            Color dimColor = Color.FromArgb(80, 90, 100);          // Dimmed for unrelated fields

            _lstVariables.BeginUpdate();
            foreach (ListViewItem item in _lstVariables.Items)
            {
                if (hasLinks)
                {
                    item.ForeColor = linked.Contains(item.Text) ? linkedColor : dimColor;
                }
                else
                {
                    // No links — restore type-based color
                    var fields = (player ?? _lastAnimPlayer)?.InspectFields();
                    object value = null;
                    fields?.TryGetValue(item.Text, out value);
                    item.ForeColor = GetColorForType(value);
                }
            }
            _lstVariables.EndUpdate();
        }

        // ── Variable editing at runtime ─────────────────────────────────────────

        /// <summary>
        /// Edits the currently selected variable in the Variables list via a
        /// simple input dialog, then applies the new value to the live runner
        /// instance using reflection.
        /// </summary>
        private void EditSelectedVariable()
        {
            if (_lstVariables.SelectedItems.Count == 0) return;

            AnimationPlayer player = _animPlayer ?? _lastAnimPlayer;
            if (player == null)
            {
                SetStatus("No active session — run or animate code first.");
                return;
            }

            var selected = _lstVariables.SelectedItems[0];
            string fieldName = selected.Text;
            string currentValue = selected.SubItems[1].Text;

            // Look up the field type for display
            string typeName = "";
            var fieldTypes = player.InspectFieldTypes();
            if (fieldTypes != null && fieldTypes.TryGetValue(fieldName, out Type ft))
                typeName = ft.Name;

            string newValue = ShowEditVariableDialog(fieldName, currentValue, typeName);
            if (newValue == null) return; // cancelled

            string error = player.SetFieldValue(fieldName, newValue);
            if (error != null)
            {
                MessageBox.Show(error, "Edit Variable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Refresh display immediately
            UpdateVariablesDuringAnimation(_animPlayer?.CurrentTick ?? 0);
            SetStatus($"Set {fieldName} = {newValue}");
        }

        /// <summary>
        /// Shows a dark-themed input dialog for editing a variable value.
        /// Returns the new value string, or null if cancelled.
        /// </summary>
        private string ShowEditVariableDialog(string fieldName, string currentValue, string typeName)
        {
            using (var dlg = new Form())
            {
                dlg.Text = "Edit Variable";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.Size = new Size(380, 180);
                dlg.BackColor = Color.FromArgb(30, 30, 38);
                dlg.ForeColor = Color.FromArgb(220, 220, 220);

                var lblField = new Label
                {
                    Text = $"{fieldName}  ({typeName})",
                    Location = new Point(12, 12),
                    Size = new Size(350, 20),
                    ForeColor = Color.FromArgb(130, 190, 255),
                    Font = new Font("Consolas", 10f, FontStyle.Bold),
                };

                var txtValue = new TextBox
                {
                    Text = currentValue.TrimStart('"').TrimEnd('"'),
                    Location = new Point(12, 40),
                    Size = new Size(340, 24),
                    BackColor = Color.FromArgb(18, 18, 24),
                    ForeColor = Color.FromArgb(220, 255, 220),
                    Font = new Font("Consolas", 10f),
                    BorderStyle = BorderStyle.FixedSingle,
                };
                txtValue.SelectAll();

                var btnOk = new Button
                {
                    Text = "Apply",
                    DialogResult = DialogResult.OK,
                    Location = new Point(190, 100),
                    Size = new Size(75, 28),
                    BackColor = Color.FromArgb(40, 80, 50),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                };

                var btnCancel = new Button
                {
                    Text = "Cancel",
                    DialogResult = DialogResult.Cancel,
                    Location = new Point(275, 100),
                    Size = new Size(75, 28),
                    BackColor = Color.FromArgb(60, 40, 40),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                };

                dlg.AcceptButton = btnOk;
                dlg.CancelButton = btnCancel;
                dlg.Controls.AddRange(new Control[] { lblField, txtValue, btnOk, btnCancel });

                return dlg.ShowDialog(this) == DialogResult.OK ? txtValue.Text : null;
            }
        }


        /// <summary>
        /// Starts a focused animation that immediately pauses at tick 1 for step-through debugging.
        /// Used by the "Execute & Break" context menu on detected methods.
        /// </summary>
        private void StartFocusedAnimationAndBreak(string call)
        {
            if (_layout == null || string.IsNullOrWhiteSpace(call)) return;

            string code = _layout?.OriginalSourceCode ?? _codeBox.Text;
            if (string.IsNullOrWhiteSpace(code)) return;

            // Stop any existing animation
            if (_animPlayer != null && _animPlayer.IsPlaying)
            {
                _animPlayer.Stop();
            }

            // Use the same setup as StartFocusedAnimation for focus/isolation
            // but after Prepare, do StepForward (single frame) instead of Play
            DetectedMethodInfo methodInfo = _detectedMethods?.FirstOrDefault(m => m.CallExpression == call);
            bool isVirtual = methodInfo != null && methodInfo.Kind == MethodKind.SwitchCase;

            List<SnapshotRowData> execRows = _layout?.CapturedRows;
            string execCall = call;

            if (isVirtual)
            {
                var filteredRows = execRows?
                    .Where(r => string.Equals(r.Kind, methodInfo.CaseName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (filteredRows == null || filteredRows.Count == 0)
                {
                    filteredRows = new List<SnapshotRowData>
                    {
                        new SnapshotRowData
                        {
                            Kind = methodInfo.CaseName,
                            Text = methodInfo.CaseName,
                            StatText = "1,000",
                            TextColorR = 255, TextColorG = 255, TextColorB = 255, TextColorA = 255,
                        }
                    };
                }

                var result = CodeExecutor.ExecuteWithInit(code, null, filteredRows);
                if (result.Success && result.Sprites.Count > 0)
                {
                    _animFocusSprites = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var sp in result.Sprites)
                        _animFocusSprites.Add(BuildFocusSpriteKey(sp));
                    _animFocusCall = call;
                }

                if (_isolatedCallSprites != null && _isolatedCallSprites.Count > 0)
                { execCall = call; execRows = filteredRows; }
                else
                { execCall = null; execRows = _layout?.CapturedRows; }
            }
            else
            {
                var result = CodeExecutor.ExecuteWithInit(code, call, _layout?.CapturedRows);
                if (result.Success && result.Sprites.Count > 0)
                {
                    _animFocusSprites = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var sp in result.Sprites)
                        _animFocusSprites.Add(BuildFocusSpriteKey(sp));
                    _animFocusCall = call;
                }

                if (_isolatedCallSprites != null && _isolatedCallSprites.Count > 0)
                    execCall = call;
                else
                    execCall = null;
                execRows = _layout?.CapturedRows;
            }

            EnsureAnimPlayer();
            PushUndo();
            CaptureAnimPositionSnapshot();

            string error = _animPlayer.Prepare(code, execCall, execRows);
            if (error != null)
            {
                MessageBox.Show(error, "Animation Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _animPositionSnapshot = null;
                _animFocusCall = null;
                _animFocusSprites = null;
                UpdateAnimButtonStates();
                return;
            }

            // Execute single frame and stay paused — ready for step-through
            _animPlayer.StepForward();
            UpdateAnimButtonStates();
            SetStatus($"⏸ Paused at tick 1 — step with ⏭ | {call}");
        }

        private Panel BuildCodePanel()
        {
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 20, 20), Padding = new Padding(3) };

            var toolbar = new FlowLayoutPanel
            {
                Dock          = DockStyle.Top,
                Height        = 36,
                AutoSize      = true,
                BackColor     = Color.FromArgb(30, 30, 30),
                FlowDirection = FlowDirection.LeftToRight,
                Padding       = new Padding(4, 4, 4, 0),
            };

            _lblCodeTitle = new Label { Text = "Generated C# Code", Width = 150, Height = 26, ForeColor = Color.FromArgb(150, 200, 255), TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };

            _lblCodeMode = new Label
            {
                Text      = "▸ In-Game (PB)",
                Width     = 110,
                Height    = 26,
                ForeColor = Color.FromArgb(140, 210, 140),
                TextAlign = ContentAlignment.MiddleLeft,
                Font      = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            };

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
            btnRefresh.Click += (s, e) => RefreshCode(writeBack: true, force: true);  // User explicitly wants fresh code + write back to project

            var btnResetSource = DarkButton("Reset Source", Color.FromArgb(120, 60, 0));
            btnResetSource.Size = new Size(95, 26);
            btnResetSource.Click += (s, e) =>
            {
                if (_layout != null) _layout.OriginalSourceCode = null;
                RefreshCode(force: true);  // User explicitly wants to reset
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
            _cmbCodeStyle.Items.AddRange(new object[] { "In-Game (PB)", "Mod", "Plugin / Torch", "Pulsar" });
            _cmbCodeStyle.SelectedIndex = 0;
            _cmbCodeStyle.SelectedIndexChanged += (s, e) =>
            {
                UpdateCodeModeLabel();
                RefreshCode();
            };

            _btnCaptureSnapshot = DarkButton("📷 Capture Snapshot", Color.FromArgb(140, 90, 0));
            _btnCaptureSnapshot.Size = new Size(140, 26);
            _btnCaptureSnapshot.Visible = false;
            _btnCaptureSnapshot.Click += (s, e) => ApplyLiveSnapshot();

            _btnApplyCode = DarkButton("📥 Apply Code", Color.FromArgb(160, 100, 0));
            _btnApplyCode.Size = new Size(110, 26);
            _btnApplyCode.Visible = false;
            _btnApplyCode.Click += (s, e) => ApplyCodeFromPanel();

            var btnPaste = DarkButton("📋 Paste Code", Color.FromArgb(100, 60, 140));
            btnPaste.Size = new Size(110, 26);
            btnPaste.Click += (s, e) => ShowPasteLayoutDialog();

            var btnInspect = DarkButton("🔍 Inspect Variables", Color.FromArgb(60, 120, 140));
            btnInspect.Size = new Size(130, 26);
            btnInspect.Click += (s, e) => TestVariableInspector();

            toolbar.Controls.Add(_lblCodeTitle);
            toolbar.Controls.Add(_lblCodeMode);
            toolbar.Controls.Add(btnPaste);
            toolbar.Controls.Add(btnCopy);
            toolbar.Controls.Add(btnRefresh);
            toolbar.Controls.Add(btnResetSource);
            toolbar.Controls.Add(_cmbCodeStyle);
            toolbar.Controls.Add(_btnApplyCode);
            toolbar.Controls.Add(_btnCaptureSnapshot);
            toolbar.Controls.Add(btnInspect);

            _btnPopOut = DarkButton("⬈ Pop Out", Color.FromArgb(80, 80, 100));
            _btnPopOut.Size = new Size(80, 26);
            _btnPopOut.Click += (s, e) => ToggleCodePopout();
            toolbar.Controls.Add(_btnPopOut);

            _codeBox = new RichTextBox
            {
                Dock          = DockStyle.Fill,
                BackColor     = Color.FromArgb(14, 14, 14),
                ForeColor     = Color.FromArgb(212, 212, 212),
                Font          = new Font("Consolas", 9f),
                ReadOnly      = false,
                BorderStyle   = BorderStyle.None,
                ScrollBars    = RichTextBoxScrollBars.Both,
                WordWrap      = false,
                HideSelection = false,
                AcceptsTab    = true,
            };
            // ── Syntax-highlight debounce timer ───────────────────────────────
            _syntaxTimer = new System.Windows.Forms.Timer { Interval = 900 };
            _syntaxTimer.Tick += (s, e) =>
            {
                _syntaxTimer.Stop();
                if (_highlightInProgress || _codeBox == null) return;

                // Full RichTextBox token colouring is expensive on very large scripts.
                // Keep auto-highlight for normal code size and skip oversized buffers.
                if (_codeBox.TextLength > LiveHighlightMaxChars) return;

                string current = _codeBox.Text;
                if (string.Equals(_lastHighlightedCode, current, StringComparison.Ordinal)) return;

                try
                {
                    _highlightInProgress = true;
                    SyntaxHighlighter.Highlight(_codeBox);
                    _lastHighlightedCode = _codeBox.Text;
                }
                finally
                {
                    _highlightInProgress = false;
                }
            };

            _codeBox.TextChanged += (s, e) =>
            {
                if (_suppressCodeBoxEvents) return;
                _codeBoxDirty = true;
                _lblCodeTitle.Text = "✏ Code (edited)";
                _lblCodeTitle.ForeColor = Color.FromArgb(255, 200, 80);
                if (_btnApplyCode != null) _btnApplyCode.Visible = true;
                _autoComplete?.OnTextChanged();
                // Restart the debounce so we only highlight after typing pauses
                _syntaxTimer.Stop();
                _syntaxTimer.Start();
            };
            _codeBox.LostFocus += (s, e) => _autoComplete?.Hide();

            // ── Code editor right-click context menu ──
            var ctxCode = new ContextMenuStrip();
            ctxCode.BackColor = Color.FromArgb(30, 30, 30);
            ctxCode.ForeColor = Color.White;
            ctxCode.Renderer  = new ToolStripProfessionalRenderer(new DarkColorTable());

            var mnuSelectAll = new ToolStripMenuItem("Select All", null, (s2, e2) => _codeBox.SelectAll());
            mnuSelectAll.ShortcutKeyDisplayString = "Ctrl+A";
            ctxCode.Items.Add(mnuSelectAll);
            ctxCode.Items.Add(new ToolStripSeparator());

            var mnuCut  = new ToolStripMenuItem("Cut",   null, (s2, e2) =>
            {
                string sel = _codeBox.SelectedText;
                if (!string.IsNullOrEmpty(sel))
                {
                    string norm = sel.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
                    Clipboard.SetText(norm);
                    _codeBox.SelectedText = "";
                }
            });
            mnuCut.ShortcutKeyDisplayString = "Ctrl+X";
            var mnuCopy = new ToolStripMenuItem("Copy",  null, (s2, e2) =>
            {
                string sel = _codeBox.SelectedText;
                if (string.IsNullOrEmpty(sel)) sel = _codeBox.Text;
                if (!string.IsNullOrEmpty(sel))
                {
                    string norm = sel.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
                    Clipboard.SetText(norm);
                }
            });
            mnuCopy.ShortcutKeyDisplayString = "Ctrl+C";
            var mnuPaste = new ToolStripMenuItem("Paste", null, (s2, e2) => _codeBox.Paste());
            mnuPaste.ShortcutKeyDisplayString = "Ctrl+V";
            ctxCode.Items.Add(mnuCut);
            ctxCode.Items.Add(mnuCopy);
            ctxCode.Items.Add(mnuPaste);

            ctxCode.Items.Add(new ToolStripSeparator());

            var mnuIndent = new ToolStripMenuItem("Set Indentation");
            mnuIndent.DropDownItems.Add(new ToolStripMenuItem("2 Spaces", null, (s2, e2) => ReindentCodeBox(2, false)));
            mnuIndent.DropDownItems.Add(new ToolStripMenuItem("4 Spaces", null, (s2, e2) => ReindentCodeBox(4, false)));
            mnuIndent.DropDownItems.Add(new ToolStripMenuItem("Tab",      null, (s2, e2) => ReindentCodeBox(0, true)));
            ctxCode.Items.Add(mnuIndent);

            ctxCode.Items.Add(new ToolStripSeparator());
            var mnuIndentSel  = new ToolStripMenuItem("Indent Selection",  null, (s2, e2) => CodeBoxIndentSelection());
            mnuIndentSel.ShortcutKeyDisplayString = "Tab";
            var mnuOutdentSel = new ToolStripMenuItem("Outdent Selection", null, (s2, e2) => CodeBoxOutdentSelection());
            mnuOutdentSel.ShortcutKeyDisplayString = "Shift+Tab";
            ctxCode.Items.Add(mnuIndentSel);
            ctxCode.Items.Add(mnuOutdentSel);

            ctxCode.Opening += (s2, e2) =>
            {
                bool hasSel = _codeBox.SelectionLength > 0;
                mnuCut.Enabled  = hasSel;
                mnuCopy.Enabled = hasSel;
                mnuPaste.Enabled = Clipboard.ContainsText();
            };
            _codeBox.ContextMenuStrip = ctxCode;

            var lineGutter = new Controls.LineNumberGutter(_codeBox);
            panel.Controls.Add(_codeBox);
            panel.Controls.Add(lineGutter);
            panel.Controls.Add(toolbar);
            _autoComplete = new CodeAutoComplete(_codeBox);
            _codePanel = panel;

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

            // ── Animation playback bar (above exec bar) ──────────────────────────
            _animBar = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = 30,
                BackColor = Color.FromArgb(18, 28, 18),
            };

            _btnAnimPlay = DarkButton("▶ Play", Color.FromArgb(20, 100, 40));
            _btnAnimPlay.Dock  = DockStyle.Left;
            _btnAnimPlay.Width = 65;
            _btnAnimPlay.Click += OnAnimPlayClick;

            _btnAnimPause = DarkButton("⏸", Color.FromArgb(100, 100, 20));
            _btnAnimPause.Dock    = DockStyle.Left;
            _btnAnimPause.Width   = 36;
            _btnAnimPause.Enabled = false;
            _btnAnimPause.Click  += OnAnimPauseClick;

            _btnAnimStop = DarkButton("⏹", Color.FromArgb(120, 30, 30));
            _btnAnimStop.Dock    = DockStyle.Left;
            _btnAnimStop.Width   = 36;
            _btnAnimStop.Enabled = false;
            _btnAnimStop.Click  += OnAnimStopClick;

            _btnAnimStep = DarkButton("⏭", Color.FromArgb(60, 60, 120));
            _btnAnimStep.Dock  = DockStyle.Left;
            _btnAnimStep.Width = 36;
            _btnAnimStep.Click += OnAnimStepClick;

            _lblAnimTick = new Label
            {
                Text      = "Animation",
                Dock      = DockStyle.Fill,
                ForeColor = Color.FromArgb(130, 200, 130),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding   = new Padding(8, 0, 0, 0),
            };

            _animBar.Controls.Add(_lblAnimTick);
            _animBar.Controls.Add(_btnAnimStep);
            _animBar.Controls.Add(_btnAnimStop);
            _animBar.Controls.Add(_btnAnimPause);
            _animBar.Controls.Add(_btnAnimPlay);
            panel.Controls.Add(_animBar);

            // ── Timeline scrubber bar (below animation bar) ───────────────────────
            _timelineBar = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = 28,
                BackColor = Color.FromArgb(18, 22, 32),
                Visible   = false, // shown once history has data
            };

            _lblTimelineTick = new Label
            {
                Text      = "⏪ History",
                Dock      = DockStyle.Right,
                Width     = 120,
                ForeColor = Color.FromArgb(180, 180, 255),
                TextAlign = ContentAlignment.MiddleRight,
                Padding   = new Padding(0, 0, 6, 0),
                Font      = new Font("Consolas", 8f),
            };

            _timelineScrubber = new TrackBar
            {
                Dock      = DockStyle.Fill,
                Minimum   = 0,
                Maximum   = 1,
                Value     = 0,
                TickStyle = TickStyle.None,
                BackColor = Color.FromArgb(18, 22, 32),
                AutoSize  = false,
                Height    = 28,
            };
            _timelineScrubber.Scroll += OnTimelineScrub;

            _timelineBar.Controls.Add(_timelineScrubber);
            _timelineBar.Controls.Add(_lblTimelineTick);
            panel.Controls.Add(_timelineBar);

            // ── Snapshot comparison bar (below timeline) ──────────────────────────
            var snapBar = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = 26,
                BackColor = Color.FromArgb(22, 22, 38),
                Visible   = false,
            };
            // Link visibility to timeline bar
            _timelineBar.VisibleChanged += (s, ev) => snapBar.Visible = _timelineBar.Visible;

            _btnBookmarkA = DarkButton("📌A", Color.FromArgb(40, 70, 110));
            _btnBookmarkA.Dock  = DockStyle.Left;
            _btnBookmarkA.Width = 44;
            _btnBookmarkA.Click += OnBookmarkAClick;

            _btnBookmarkB = DarkButton("📌B", Color.FromArgb(40, 70, 110));
            _btnBookmarkB.Dock  = DockStyle.Left;
            _btnBookmarkB.Width = 44;
            _btnBookmarkB.Click += OnBookmarkBClick;

            _btnCompareSnapshots = DarkButton("🔍 Compare", Color.FromArgb(60, 50, 90));
            _btnCompareSnapshots.Dock  = DockStyle.Left;
            _btnCompareSnapshots.Width = 80;
            _btnCompareSnapshots.Enabled = false;
            _btnCompareSnapshots.Click += OnCompareSnapshotsClick;

            _lblBookmarks = new Label
            {
                Text      = "Bookmark two ticks to compare",
                Dock      = DockStyle.Fill,
                ForeColor = Color.FromArgb(120, 140, 180),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding   = new Padding(6, 0, 0, 0),
                Font      = new Font("Consolas", 7.5f),
            };

            snapBar.Controls.Add(_lblBookmarks);
            snapBar.Controls.Add(_btnCompareSnapshots);
            snapBar.Controls.Add(_btnBookmarkB);
            snapBar.Controls.Add(_btnBookmarkA);
            panel.Controls.Add(snapBar);

            // ── Splitter for resizing the tabs section ────────────────────────────────
            var splitter = new Splitter
            {
                Dock        = DockStyle.Bottom,
                Height      = 8,  // Bigger for easier grabbing
                BackColor   = Color.FromArgb(100, 110, 140),  // Bright blue-gray
                BorderStyle = BorderStyle.FixedSingle,  // Add border to make it obvious
                Cursor      = Cursors.HSplit,
                MinExtra    = 100,  // Min space for code box above
                MinSize     = 60,   // Min size for tabs below
            };
            panel.Controls.Add(splitter);  // Add after animBar so it's between anim and tabs

            // ── Bottom tabs (Detected Methods + Variables) ────────────────────────────
            var tabControl = new TabControl
            {
                Dock        = DockStyle.Bottom,
                Height      = 150,  // Increased from 90 to accommodate checkbox + variables
                BackColor   = Color.FromArgb(22, 26, 38),
                ForeColor   = Color.FromArgb(130, 180, 230),
            };

            // ── Tab 1: Detected Methods ───────────────────────────────────────────────
            var tabMethods = new TabPage("Detected Methods");
            tabMethods.BackColor = Color.FromArgb(22, 26, 38);

            _lstDetectedCalls = new ListBox
            {
                Dock        = DockStyle.Fill,
                BackColor   = Color.FromArgb(22, 26, 38),
                ForeColor   = Color.FromArgb(160, 220, 255),
                BorderStyle = BorderStyle.None,
                Font        = new Font("Consolas", 9f),
            };
            var callsTooltip = new ToolTip();
            callsTooltip.SetToolTip(_lstDetectedCalls,
                "Auto-detected rendering methods in your code\n" +
                "Single-click to populate call box\n" +
                "Double-click to execute and show sprites\n" +
                "Right-click for Execute & Isolate and Focused Animation");
            _lstDetectedCalls.SelectedIndexChanged += (s, e) =>
            {
                if (_lstDetectedCalls.SelectedItem != null)
                    _execCallBox.Text = _lstDetectedCalls.SelectedItem.ToString();
            };

            // Double-click → execute the selected call and isolate its sprites
            _lstDetectedCalls.MouseDoubleClick += (s, e) =>
            {
                if (_lstDetectedCalls.SelectedItem == null) return;
                string call = _lstDetectedCalls.SelectedItem.ToString();
                _execCallBox.Text = call;
                IsolateCallSprites(call);
            };

            // Right-click → context menu with focused-animation option
            var ctxCalls = new ContextMenuStrip();
            ctxCalls.BackColor = Color.FromArgb(30, 30, 30);
            ctxCalls.ForeColor = Color.White;
            ctxCalls.Renderer  = new ToolStripProfessionalRenderer(
                new DarkColorTable());

            // Right-click should select the item under the cursor first
            _lstDetectedCalls.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                int idx = _lstDetectedCalls.IndexFromPoint(e.Location);
                if (idx >= 0) _lstDetectedCalls.SelectedIndex = idx;
            };

            var mnuExecIsolate = new ToolStripMenuItem("▶ Execute & Isolate");
            mnuExecIsolate.Click += (s2, e2) =>
            {
                if (_lstDetectedCalls.SelectedItem == null) return;
                string call = _lstDetectedCalls.SelectedItem.ToString();
                _execCallBox.Text = call;
                IsolateCallSprites(call);
            };
            ctxCalls.Items.Add(mnuExecIsolate);

            // "Build Sprite Map" removed: instrumentation pipeline (SetCurrentMethod + RecordSpriteMethod)
            // now handles method attribution at execution time. No need to re-run methods in isolation.

            var mnuFocusAnim = new ToolStripMenuItem("▶ Start Focused Animation");
            mnuFocusAnim.Click += (s2, e2) =>
            {
                if (_lstDetectedCalls.SelectedItem == null) return;
                string call = _lstDetectedCalls.SelectedItem.ToString();
                _execCallBox.Text = call;
                StartFocusedAnimation(call);
            };
            ctxCalls.Items.Add(mnuFocusAnim);

            var mnuExecBreak = new ToolStripMenuItem("⏸ Execute & Break");
            mnuExecBreak.Click += (s2, e2) =>
            {
                if (_lstDetectedCalls.SelectedItem == null) return;
                string call = _lstDetectedCalls.SelectedItem.ToString();
                _execCallBox.Text = call;
                StartFocusedAnimationAndBreak(call);
            };
            ctxCalls.Items.Add(mnuExecBreak);

            var mnuJump = new ToolStripMenuItem("↗ Jump to Definition");
            mnuJump.Click += (s2, e2) =>
            {
                if (_lstDetectedCalls.SelectedItem == null) return;
                string call = _lstDetectedCalls.SelectedItem.ToString();
                _execCallBox.Text = call;
                JumpToMethodDefinition(call);
            };
            ctxCalls.Items.Add(mnuJump);

            _lstDetectedCalls.ContextMenuStrip = ctxCalls;

            tabMethods.Controls.Add(_lstDetectedCalls);
            tabControl.TabPages.Add(tabMethods);

            // ── Tab 2: Variables ───────────────────────────────────────────────────────
            _tabVariables = new TabPage("Variables");
            _tabVariables.BackColor = Color.FromArgb(22, 26, 38);

            _lstVariables = new ListView
            {
                Dock          = DockStyle.Fill,
                BackColor     = Color.FromArgb(22, 26, 38),
                ForeColor     = Color.FromArgb(160, 220, 255),
                BorderStyle   = BorderStyle.None,
                Font          = new Font("Consolas", 9f),
                View          = View.Details,
                FullRowSelect = true,
                GridLines     = true,
                OwnerDraw     = true,
            };
            // Enable double buffering to eliminate flicker during fast owner-draw updates (sparklines)
            typeof(ListView).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(_lstVariables, true, null);
            _lstVariables.Columns.Add("Field", 120);
            _lstVariables.Columns.Add("Value", 170);
            _lstVariables.Columns.Add("Trend", 110);
            _lstVariables.DrawColumnHeader += LstVariables_DrawColumnHeader;
            _lstVariables.DrawSubItem      += LstVariables_DrawSubItem;

            var varsTooltip = new ToolTip();
            varsTooltip.SetToolTip(_lstVariables,
                "Instance fields from last Execute Code\n" +
                "Updated automatically after execution\n" +
                "Right-click to copy field name or value");

            // ── Context menu for Variables ─────────────────────────────────────────────
            var ctxVariables = new ContextMenuStrip();
            ctxVariables.BackColor = Color.FromArgb(30, 30, 30);
            ctxVariables.ForeColor = Color.White;
            ctxVariables.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());

            var mnuCopyValue = new ToolStripMenuItem("Copy Value");
            mnuCopyValue.Click += (s2, e2) =>
            {
                if (_lstVariables.SelectedItems.Count == 0) return;
                var item = _lstVariables.SelectedItems[0];
                string value = item.SubItems[1].Text;  // Second column = value
                if (!string.IsNullOrEmpty(value))
                {
                    Clipboard.SetText(value);
                    SetStatus($"Copied: {value}");
                }
            };

            var mnuCopyFieldName = new ToolStripMenuItem("Copy Field Name");
            mnuCopyFieldName.Click += (s2, e2) =>
            {
                if (_lstVariables.SelectedItems.Count == 0) return;
                var item = _lstVariables.SelectedItems[0];
                string fieldName = item.Text;  // First column = field name
                if (!string.IsNullOrEmpty(fieldName))
                {
                    Clipboard.SetText(fieldName);
                    SetStatus($"Copied: {fieldName}");
                }
            };

            var mnuEditValue = new ToolStripMenuItem("✏️ Edit Value");
            mnuEditValue.Click += (s2, e2) =>
            {
                if (_lstVariables.SelectedItems.Count == 0) return;
                EditSelectedVariable();
            };

            ctxVariables.Items.Add(mnuEditValue);
            ctxVariables.Items.Add(new ToolStripSeparator());
            ctxVariables.Items.Add(mnuCopyValue);
            ctxVariables.Items.Add(mnuCopyFieldName);
            _lstVariables.ContextMenuStrip = ctxVariables;

            // Double-click a variable to edit its value at runtime
            _lstVariables.DoubleClick += (s2, e2) =>
            {
                if (_lstVariables.SelectedItems.Count == 0) return;
                EditSelectedVariable();
            };

            // ── Checkbox: Show internal fields ─────────────────────────────────────
            _chkShowInternalFields = new CheckBox
            {
                Text      = "Show internal fields",
                Dock      = DockStyle.Top,
                Height    = 22,
                BackColor = Color.FromArgb(22, 26, 38),
                ForeColor = Color.FromArgb(130, 180, 230),
                Checked   = false, // Default: hide internal fields
                Padding   = new Padding(6, 2, 0, 0),
            };
            var chkTooltip = new ToolTip();
            chkTooltip.SetToolTip(_chkShowInternalFields,
                "Show internal fields (_stubSurface, compiler-generated fields, etc.)\n" +
                "Uncheck to see only your variables (counter, angle, items, etc.)");
            _chkShowInternalFields.CheckedChanged += (s, e) =>
            {
                // Refresh variables list when checkbox changes
                if (_lastAnimPlayer != null || _animPlayer != null)
                {
                    UpdateVariablesDuringAnimation(_animPlayer?.CurrentTick ?? 0);
                }
            };

            _tabVariables.Controls.Add(_lstVariables);
            _tabVariables.Controls.Add(_chkShowInternalFields);
            tabControl.TabPages.Add(_tabVariables);

            // ── Tab 3: Watch Expressions ──────────────────────────────────────────────
            _tabWatch = new TabPage("Watch");
            _tabWatch.BackColor = Color.FromArgb(22, 26, 38);

            _lstWatch = new ListView
            {
                Dock          = DockStyle.Fill,
                BackColor     = Color.FromArgb(22, 26, 38),
                ForeColor     = Color.FromArgb(160, 220, 255),
                BorderStyle   = BorderStyle.None,
                Font          = new Font("Consolas", 9f),
                View          = View.Details,
                FullRowSelect = true,
                GridLines     = true,
            };
            _lstWatch.Columns.Add("Expression", 180);
            _lstWatch.Columns.Add("Value", 180);
            _lstWatch.Columns.Add("Type", 80);

            // ── Input bar: TextBox + Add + Remove buttons ─────────────────────────────
            var watchInputPanel = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 26,
                BackColor = Color.FromArgb(22, 26, 38),
                Padding   = new Padding(2, 2, 2, 2),
            };

            _txtWatchExpr = new TextBox
            {
                Dock      = DockStyle.Fill,
                BackColor = Color.FromArgb(35, 40, 55),
                ForeColor = Color.FromArgb(220, 220, 220),
                Font      = new Font("Consolas", 9f),
                BorderStyle = BorderStyle.FixedSingle,
            };
            var watchExprTooltip = new ToolTip();
            watchExprTooltip.SetToolTip(_txtWatchExpr,
                "Type a C# expression to watch (e.g., counter % 10, angle > 180)\n" +
                "Press Enter or click + to add\n" +
                "Uses your script's field names as variables");
            _txtWatchExpr.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    AddWatchExpression(_txtWatchExpr.Text);
                    _txtWatchExpr.Clear();
                }
            };

            var btnAddWatch = new Button
            {
                Text      = "+",
                Dock      = DockStyle.Right,
                Width     = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 120, 80),
                ForeColor = Color.White,
                Font      = new Font("Consolas", 10f, FontStyle.Bold),
            };
            btnAddWatch.FlatAppearance.BorderSize = 0;
            btnAddWatch.Click += (s, e) =>
            {
                AddWatchExpression(_txtWatchExpr.Text);
                _txtWatchExpr.Clear();
            };

            var btnRemoveWatch = new Button
            {
                Text      = "−",
                Dock      = DockStyle.Right,
                Width     = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(140, 50, 50),
                ForeColor = Color.White,
                Font      = new Font("Consolas", 10f, FontStyle.Bold),
            };
            btnRemoveWatch.FlatAppearance.BorderSize = 0;
            btnRemoveWatch.Click += (s, e) => RemoveSelectedWatch();

            watchInputPanel.Controls.Add(_txtWatchExpr);
            watchInputPanel.Controls.Add(btnRemoveWatch);
            watchInputPanel.Controls.Add(btnAddWatch);

            // ── Context menu for Watch ────────────────────────────────────────────────
            var ctxWatch = new ContextMenuStrip();
            ctxWatch.BackColor = Color.FromArgb(30, 30, 30);
            ctxWatch.ForeColor = Color.White;
            ctxWatch.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());

            var mnuCopyWatchValue = new ToolStripMenuItem("Copy Value");
            mnuCopyWatchValue.Click += (s2, e2) =>
            {
                if (_lstWatch.SelectedItems.Count == 0) return;
                string val = _lstWatch.SelectedItems[0].SubItems[1].Text;
                if (!string.IsNullOrEmpty(val))
                {
                    Clipboard.SetText(val);
                    SetStatus($"Copied: {val}");
                }
            };

            var mnuCopyWatchExpr = new ToolStripMenuItem("Copy Expression");
            mnuCopyWatchExpr.Click += (s2, e2) =>
            {
                if (_lstWatch.SelectedItems.Count == 0) return;
                string expr = _lstWatch.SelectedItems[0].Text;
                if (!string.IsNullOrEmpty(expr))
                {
                    Clipboard.SetText(expr);
                    SetStatus($"Copied: {expr}");
                }
            };

            var mnuRemoveWatch = new ToolStripMenuItem("Remove Watch");
            mnuRemoveWatch.Click += (s2, e2) => RemoveSelectedWatch();

            ctxWatch.Items.Add(mnuCopyWatchValue);
            ctxWatch.Items.Add(mnuCopyWatchExpr);
            ctxWatch.Items.Add(new ToolStripSeparator());
            ctxWatch.Items.Add(mnuRemoveWatch);
            _lstWatch.ContextMenuStrip = ctxWatch;

            // ── Break condition bar ───────────────────────────────────────────────────
            var breakPanel = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 26,
                BackColor = Color.FromArgb(28, 22, 22),
                Padding   = new Padding(2, 2, 2, 2),
            };

            var lblBreakPrefix = new Label
            {
                Text      = "Break:",
                Dock      = DockStyle.Left,
                Width     = 42,
                ForeColor = Color.FromArgb(255, 140, 100),
                TextAlign = ContentAlignment.MiddleLeft,
                Font      = new Font("Segoe UI", 8f, FontStyle.Bold),
            };

            _txtBreakCondition = new TextBox
            {
                Dock        = DockStyle.Fill,
                BackColor   = Color.FromArgb(40, 30, 30),
                ForeColor   = Color.FromArgb(255, 200, 180),
                Font        = new Font("Consolas", 9f),
                BorderStyle = BorderStyle.FixedSingle,
            };
            var breakTooltip = new ToolTip();
            breakTooltip.SetToolTip(_txtBreakCondition,
                "Pause animation when this C# expression becomes true\n" +
                "e.g., counter > 50, _radarAngle > 360, items.Count == 0\n" +
                "Press Enter or click Set to activate");
            _txtBreakCondition.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    SetBreakCondition(_txtBreakCondition.Text);
                }
            };

            var btnSetBreak = new Button
            {
                Text      = "Set",
                Dock      = DockStyle.Right,
                Width     = 36,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(140, 60, 30),
                ForeColor = Color.White,
                Font      = new Font("Segoe UI", 8f, FontStyle.Bold),
            };
            btnSetBreak.FlatAppearance.BorderSize = 0;
            btnSetBreak.Click += (s, e) => SetBreakCondition(_txtBreakCondition.Text);

            var btnClearBreak = new Button
            {
                Text      = "✕",
                Dock      = DockStyle.Right,
                Width     = 26,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(80, 30, 30),
                ForeColor = Color.FromArgb(200, 200, 200),
                Font      = new Font("Segoe UI", 9f),
            };
            btnClearBreak.FlatAppearance.BorderSize = 0;
            btnClearBreak.Click += (s, e) => ClearBreakCondition();

            _lblBreakStatus = new Label
            {
                Text      = "",
                Dock      = DockStyle.Right,
                Width     = 120,
                ForeColor = Color.FromArgb(120, 120, 120),
                TextAlign = ContentAlignment.MiddleRight,
                Font      = new Font("Consolas", 8f),
            };

            breakPanel.Controls.Add(_txtBreakCondition);
            breakPanel.Controls.Add(_lblBreakStatus);
            breakPanel.Controls.Add(btnClearBreak);
            breakPanel.Controls.Add(btnSetBreak);
            breakPanel.Controls.Add(lblBreakPrefix);

            _tabWatch.Controls.Add(_lstWatch);
            _tabWatch.Controls.Add(breakPanel);
            _tabWatch.Controls.Add(watchInputPanel);
            tabControl.TabPages.Add(_tabWatch);

            // ── Tab 4: Console (Echo output log) ──────────────────────────────────────
            _tabConsole = new TabPage("Console");
            _tabConsole.BackColor = Color.FromArgb(22, 26, 38);

            _rtbConsole = new RichTextBox
            {
                Dock       = DockStyle.Fill,
                BackColor  = Color.FromArgb(18, 20, 30),
                ForeColor  = Color.FromArgb(180, 220, 255),
                Font       = new Font("Consolas", 9f),
                ReadOnly   = true,
                BorderStyle = BorderStyle.None,
                WordWrap   = false,
            };

            var consoleBtnPanel = new Panel
            {
                Dock   = DockStyle.Top,
                Height = 26,
                BackColor = Color.FromArgb(30, 34, 48),
            };

            var btnClearConsole = new Button
            {
                Text      = "🗑 Clear",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(50, 55, 75),
                ForeColor = Color.FromArgb(180, 200, 230),
                Font      = new Font("Segoe UI", 8f),
                Height    = 22,
                Width     = 70,
                Location  = new Point(4, 2),
                Cursor    = Cursors.Hand,
            };
            btnClearConsole.FlatAppearance.BorderColor = Color.FromArgb(70, 80, 110);
            btnClearConsole.Click += (s, e) => { _rtbConsole.Clear(); };

            consoleBtnPanel.Controls.Add(btnClearConsole);
            _tabConsole.Controls.Add(_rtbConsole);
            _tabConsole.Controls.Add(consoleBtnPanel);
            tabControl.TabPages.Add(_tabConsole);

            // ── Tab 5: Diff (side-by-side patched code view) ──────────────────────────
            _tabDiff = new TabPage("Diff");
            _tabDiff.BackColor = Color.FromArgb(22, 26, 38);

            var diffSplit = new SplitContainer
            {
                Dock             = DockStyle.Fill,
                Orientation      = Orientation.Vertical,
                BackColor        = Color.FromArgb(30, 34, 48),
                SplitterDistance = 300,
                SplitterWidth    = 4,
            };

            // Left panel: Before
            var lblBefore = new Label
            {
                Text      = "Before",
                Dock      = DockStyle.Top,
                Height    = 18,
                ForeColor = Color.FromArgb(140, 160, 200),
                BackColor = Color.FromArgb(30, 34, 48),
                Font      = new Font("Segoe UI", 8f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
            };
            _rtbDiffBefore = new RichTextBox
            {
                Dock        = DockStyle.Fill,
                BackColor   = Color.FromArgb(18, 20, 30),
                ForeColor   = Color.FromArgb(170, 185, 210),
                Font        = new Font("Consolas", 9f),
                ReadOnly    = true,
                BorderStyle = BorderStyle.None,
                WordWrap    = false,
            };
            diffSplit.Panel1.Controls.Add(_rtbDiffBefore);
            diffSplit.Panel1.Controls.Add(lblBefore);

            // Right panel: After
            var lblAfter = new Label
            {
                Text      = "After",
                Dock      = DockStyle.Top,
                Height    = 18,
                ForeColor = Color.FromArgb(140, 160, 200),
                BackColor = Color.FromArgb(30, 34, 48),
                Font      = new Font("Segoe UI", 8f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
            };
            _rtbDiffAfter = new RichTextBox
            {
                Dock        = DockStyle.Fill,
                BackColor   = Color.FromArgb(18, 20, 30),
                ForeColor   = Color.FromArgb(170, 185, 210),
                Font        = new Font("Consolas", 9f),
                ReadOnly    = true,
                BorderStyle = BorderStyle.None,
                WordWrap    = false,
            };
            diffSplit.Panel2.Controls.Add(_rtbDiffAfter);
            diffSplit.Panel2.Controls.Add(lblAfter);

            var diffBtnPanel = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 26,
                BackColor = Color.FromArgb(30, 34, 48),
            };
            var btnClearDiff = new Button
            {
                Text      = "🗑 Clear",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(50, 55, 75),
                ForeColor = Color.FromArgb(180, 200, 230),
                Font      = new Font("Segoe UI", 8f),
                Height    = 22,
                Width     = 70,
                Location  = new Point(4, 2),
                Cursor    = Cursors.Hand,
            };
            btnClearDiff.FlatAppearance.BorderColor = Color.FromArgb(70, 80, 110);
            btnClearDiff.Click += (s, e) => { _rtbDiffBefore.Clear(); _rtbDiffAfter.Clear(); _tabDiff.Text = "Diff"; };
            diffBtnPanel.Controls.Add(btnClearDiff);

            _tabDiff.Controls.Add(diffSplit);
            _tabDiff.Controls.Add(diffBtnPanel);
            tabControl.TabPages.Add(_tabDiff);

            panel.Controls.Add(tabControl);

            return panel;
        }

        // ── Diff view helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Computes a line-by-line diff between the original and patched code and
        /// displays it in the Before/After RichTextBoxes on the Diff tab.
        /// Changed lines are highlighted: removed = red tint, added = green tint,
        /// unchanged = default. The tab title shows the count of changed lines.
        /// </summary>
        private void ShowPatchDiff(string original, string patched)
        {
            if (_rtbDiffBefore == null || _rtbDiffAfter == null) return;
            if (original == null || patched == null) return;
            if (original == patched) return; // No changes

            var oldLines = original.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var newLines = patched.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            // Simple LCS-based diff — compute edit script
            var diff = ComputeLineDiff(oldLines, newLines);

            _rtbDiffBefore.SuspendLayout();
            _rtbDiffAfter.SuspendLayout();
            _rtbDiffBefore.Clear();
            _rtbDiffAfter.Clear();

            var colorRemoved  = Color.FromArgb(60, 30, 30);   // dark red
            var colorAdded    = Color.FromArgb(25, 50, 25);    // dark green
            var colorDefault  = Color.FromArgb(18, 20, 30);    // normal bg
            var fgRemoved     = Color.FromArgb(255, 140, 140); // bright red text
            var fgAdded       = Color.FromArgb(140, 255, 140); // bright green text
            var fgDefault     = Color.FromArgb(170, 185, 210); // normal text
            var fgContext     = Color.FromArgb(100, 110, 140); // dimmed context

            int changedCount = 0;

            foreach (var entry in diff)
            {
                switch (entry.Type)
                {
                    case DiffLineType.Unchanged:
                        AppendDiffLine(_rtbDiffBefore, "  " + entry.OldLine, colorDefault, fgContext);
                        AppendDiffLine(_rtbDiffAfter,  "  " + entry.NewLine, colorDefault, fgContext);
                        break;
                    case DiffLineType.Removed:
                        AppendDiffLine(_rtbDiffBefore, "- " + entry.OldLine, colorRemoved, fgRemoved);
                        AppendDiffLine(_rtbDiffAfter,  "", colorDefault, fgContext); // blank placeholder
                        changedCount++;
                        break;
                    case DiffLineType.Added:
                        AppendDiffLine(_rtbDiffBefore, "", colorDefault, fgContext); // blank placeholder
                        AppendDiffLine(_rtbDiffAfter,  "+ " + entry.NewLine, colorAdded, fgAdded);
                        changedCount++;
                        break;
                    case DiffLineType.Modified:
                        AppendDiffLine(_rtbDiffBefore, "~ " + entry.OldLine, colorRemoved, fgRemoved);
                        AppendDiffLine(_rtbDiffAfter,  "~ " + entry.NewLine, colorAdded, fgAdded);
                        changedCount++;
                        break;
                }
            }

            _rtbDiffBefore.SelectionStart = 0;
            _rtbDiffAfter.SelectionStart = 0;
            _rtbDiffBefore.ResumeLayout();
            _rtbDiffAfter.ResumeLayout();

            _tabDiff.Text = changedCount > 0 ? $"Diff ({changedCount})" : "Diff";
        }

        private enum DiffLineType { Unchanged, Removed, Added, Modified }

        private struct DiffEntry
        {
            public DiffLineType Type;
            public string OldLine;
            public string NewLine;
        }

        /// <summary>
        /// Simple line-level diff using longest common subsequence.
        /// Produces a list of entries showing unchanged, removed, added, or modified lines.
        /// For patched code the changes are typically scattered single-line edits
        /// (color literals, position values), so this approach works well.
        /// </summary>
        private static List<DiffEntry> ComputeLineDiff(string[] oldLines, string[] newLines)
        {
            int n = oldLines.Length;
            int m = newLines.Length;

            // LCS table
            var lcs = new int[n + 1, m + 1];
            for (int i = 1; i <= n; i++)
                for (int j = 1; j <= m; j++)
                    lcs[i, j] = oldLines[i - 1] == newLines[j - 1]
                        ? lcs[i - 1, j - 1] + 1
                        : Math.Max(lcs[i - 1, j], lcs[i, j - 1]);

            // Backtrack to produce diff entries
            var result = new List<DiffEntry>();
            int oi = n, ni = m;
            while (oi > 0 || ni > 0)
            {
                if (oi > 0 && ni > 0 && oldLines[oi - 1] == newLines[ni - 1])
                {
                    result.Add(new DiffEntry { Type = DiffLineType.Unchanged, OldLine = oldLines[oi - 1], NewLine = newLines[ni - 1] });
                    oi--; ni--;
                }
                else if (oi > 0 && ni > 0 &&
                         lcs[oi - 1, ni - 1] >= lcs[oi - 1, ni] &&
                         lcs[oi - 1, ni - 1] >= lcs[oi, ni - 1])
                {
                    // Both sides changed at same position — show as modified
                    result.Add(new DiffEntry { Type = DiffLineType.Modified, OldLine = oldLines[oi - 1], NewLine = newLines[ni - 1] });
                    oi--; ni--;
                }
                else if (ni > 0 && (oi == 0 || lcs[oi, ni - 1] >= lcs[oi - 1, ni]))
                {
                    result.Add(new DiffEntry { Type = DiffLineType.Added, NewLine = newLines[ni - 1] });
                    ni--;
                }
                else
                {
                    result.Add(new DiffEntry { Type = DiffLineType.Removed, OldLine = oldLines[oi - 1] });
                    oi--;
                }
            }

            result.Reverse();
            return result;
        }

        private static void AppendDiffLine(RichTextBox rtb, string text, Color bgColor, Color fgColor)
        {
            int start = rtb.TextLength;
            rtb.AppendText(text + "\n");
            rtb.Select(start, rtb.TextLength - start);
            rtb.SelectionBackColor = bgColor;
            rtb.SelectionColor = fgColor;
            rtb.SelectionLength = 0;
        }

        // ── Console output helpers ────────────────────────────────────────────────

        /// <summary>
        /// Appends output lines (Echo calls) to the Console tab.
        /// Lines are color-coded: Echo in cyan, errors in red.
        /// </summary>
        private const int MaxConsoleLines = 2000;
        private const int ConsoleTrimTarget = 1500;

        private void AppendConsoleOutput(List<string> lines, int tick = -1)
        {
            if (_rtbConsole == null || lines == null || lines.Count == 0) return;

            // Suppress all painting during the batch update
            SendMessage(_rtbConsole.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                foreach (string line in lines)
                {
                    string prefix = tick >= 0 ? $"[T{tick}] " : "";
                    _rtbConsole.SelectionStart = _rtbConsole.TextLength;
                    _rtbConsole.SelectionLength = 0;
                    _rtbConsole.SelectionColor = Color.FromArgb(100, 200, 255);
                    _rtbConsole.AppendText(prefix + line + "\n");
                }

                // Trim old lines to prevent unbounded growth
                if (_rtbConsole.Lines.Length > MaxConsoleLines)
                {
                    int removeUpTo = _rtbConsole.Lines.Length - ConsoleTrimTarget;
                    int charIdx = _rtbConsole.GetFirstCharIndexFromLine(removeUpTo);
                    if (charIdx > 0)
                    {
                        _rtbConsole.Select(0, charIdx);
                        _rtbConsole.SelectedText = "";
                    }
                }
            }
            finally
            {
                // Re-enable painting and force a single repaint
                SendMessage(_rtbConsole.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                _rtbConsole.Invalidate();
            }

            // Auto-scroll to bottom
            _rtbConsole.SelectionStart = _rtbConsole.TextLength;
            _rtbConsole.ScrollToCaret();
        }

        /// <summary>
        /// Appends an error line to the Console tab in red.
        /// </summary>
        private void AppendConsoleError(string message, int tick = -1)
        {
            if (_rtbConsole == null || string.IsNullOrEmpty(message)) return;

            string prefix = tick >= 0 ? $"[T{tick}] " : "";
            _rtbConsole.SelectionStart = _rtbConsole.TextLength;
            _rtbConsole.SelectionLength = 0;
            _rtbConsole.SelectionColor = Color.FromArgb(255, 100, 100); // red
            _rtbConsole.AppendText(prefix + "❌ " + message + "\n");

            // Auto-scroll to bottom
            _rtbConsole.SelectionStart = _rtbConsole.TextLength;
            _rtbConsole.ScrollToCaret();
        }

        // ── Code heatmap helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Applies warm-color background highlighting to the code editor based on
        /// per-method execution timing.  Maps method names to their source line ranges,
        /// then paints each line's background from green (fast) → yellow → orange → red (slow).
        /// </summary>
        private void ApplyCodeHeatmap(Dictionary<string, double> timings)
        {
            if (_codeBox == null || timings == null || timings.Count == 0 || !_heatmapEnabled)
            {
                ClearCodeHeatmap();
                return;
            }

            // Avoid redundant re-paints when timings haven't changed meaningfully
            if (_lastHeatmapTimings != null && _lastHeatmapTimings.Count == timings.Count)
            {
                bool same = true;
                foreach (var kv in timings)
                {
                    if (!_lastHeatmapTimings.TryGetValue(kv.Key, out double prev) ||
                        Math.Abs(prev - kv.Value) > 0.01)
                    { same = false; break; }
                }
                if (same) return;
            }
            _lastHeatmapTimings = new Dictionary<string, double>(timings);

            string code = _codeBox.Text;
            if (string.IsNullOrEmpty(code)) return;

            // Build method name → (charStart, charEnd) map from source
            var methodRanges = new Dictionary<string, (int start, int end)>(StringComparer.Ordinal);
            var rxMethod = new System.Text.RegularExpressions.Regex(
                @"(?:private|public|internal|protected)[ \t]+(?:static[ \t]+)?(?:void|[\w<>\[\], \t]+)[ \t]+(\w+)\s*\([^)]*\)\s*\{");

            foreach (System.Text.RegularExpressions.Match m in rxMethod.Matches(code))
            {
                string name = m.Groups[1].Value;
                if (!timings.ContainsKey(name)) continue;

                int bodyStart = m.Index + m.Length;
                int depth = 1;
                int bodyEnd = bodyStart;
                for (int i = bodyStart; i < code.Length && depth > 0; i++)
                {
                    if (code[i] == '{') depth++;
                    else if (code[i] == '}') { depth--; if (depth == 0) bodyEnd = i + 1; }
                }
                methodRanges[name] = (m.Index, bodyEnd);
            }

            if (methodRanges.Count == 0)
            {
                ClearCodeHeatmap();
                return;
            }

            // Save scroll and cursor position
            int savedSel = _codeBox.SelectionStart;
            int savedLen = _codeBox.SelectionLength;
            var scrollPos = GetScrollPos(_codeBox);

            _codeBox.SuspendLayout();

            // First, clear all backgrounds to default
            _codeBox.SelectAll();
            _codeBox.SelectionBackColor = _codeBox.BackColor;

            // Apply heatmap colors per method region (absolute thresholds)
            foreach (var kv in methodRanges)
            {
                if (!timings.TryGetValue(kv.Key, out double ms)) continue;

                Color bg = HeatmapColor(ms);

                int start = kv.Value.start;
                int end = Math.Min(kv.Value.end, code.Length);
                if (end > start)
                {
                    _codeBox.Select(start, end - start);
                    _codeBox.SelectionBackColor = bg;
                }
            }

            // Restore scroll and cursor
            _codeBox.Select(savedSel, savedLen);
            SetScrollPos(_codeBox, scrollPos);
            _codeBox.ResumeLayout();
        }

        /// <summary>Clears heatmap highlighting from the code editor.</summary>
        private void ClearCodeHeatmap()
        {
            if (_codeBox == null || _lastHeatmapTimings == null) return;
            _lastHeatmapTimings = null;

            int savedSel = _codeBox.SelectionStart;
            int savedLen = _codeBox.SelectionLength;
            var scrollPos = GetScrollPos(_codeBox);

            _codeBox.SuspendLayout();
            _codeBox.SelectAll();
            _codeBox.SelectionBackColor = _codeBox.BackColor;
            _codeBox.Select(savedSel, savedLen);
            SetScrollPos(_codeBox, scrollPos);
            _codeBox.ResumeLayout();
        }

        /// <summary>
        /// Maps an absolute execution time (ms) to a heatmap color.
        /// 0–0.5 ms = dark green, 0.5–2 ms = yellow-green, 2–8 ms = orange, >8 ms = red.
        /// Colors are kept dark/muted so white text remains readable.
        /// </summary>
        private static Color HeatmapColor(double ms)
        {
            // Map absolute ms to a 0–1 heat value via fixed thresholds
            double t;
            if (ms < 0.5)
                t = 0.25 * (ms / 0.5);                               // 0.0–0.25  (dark green)
            else if (ms < 2.0)
                t = 0.25 + 0.25 * ((ms - 0.5) / 1.5);               // 0.25–0.5  (yellow-green)
            else if (ms < 8.0)
                t = 0.5 + 0.25 * ((ms - 2.0) / 6.0);                // 0.5–0.75  (orange)
            else
                t = 0.75 + 0.25 * Math.Min(1.0, (ms - 8.0) / 12.0); // 0.75–1.0 (dark red)

            t = Math.Max(0, Math.Min(1, t));
            int r, g, b;
            if (t < 0.5)
            {
                // Green → Yellow
                double f = t * 2.0;
                r = (int)(30 + 50 * f);   // 30 → 80
                g = (int)(60 + 20 * f);   // 60 → 80
                b = 30;
            }
            else
            {
                // Yellow → Red
                double f = (t - 0.5) * 2.0;
                r = (int)(80 + 50 * f);   // 80 → 130
                g = (int)(80 - 50 * f);   // 80 → 30
                b = 30;
            }
            return Color.FromArgb(r, g, b);
        }

        // RichTextBox scroll position helpers (WinAPI)
        private const int WM_USER = 0x0400;
        private const int EM_GETSCROLLPOS = WM_USER + 221;
        private const int EM_SETSCROLLPOS = WM_USER + 222;
        private const int WM_SETREDRAW = 0x000B;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref Point lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private static Point GetScrollPos(RichTextBox rtb)
        {
            var pt = new Point();
            SendMessage(rtb.Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref pt);
            return pt;
        }

        private static void SetScrollPos(RichTextBox rtb, Point pos)
        {
            SendMessage(rtb.Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref pos);
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
            UpdateActionBarVisibility();
            _canvas.CanvasLayout = _layout;
            RefreshLayerList();
            ClearCodeDirty();
            RefreshCode();
            UpdateTitle();
            SetStatus("New layout — surface 512 × 512. Double-click a sprite in the palette or use Add buttons.");
            RefreshDebugStats();
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
            var selected = GetSelectedSprites();
            if (selected.Count == 0 && _canvas.SelectedSprite != null)
                selected.Add(_canvas.SelectedSprite);
            if (selected.Count == 0) return;

            PushUndo();
            InvalidateOriginalSourceIfSet();
            foreach (var sp in selected)
                _layout.Sprites.Remove(sp);
            _canvas.SelectedSprite = null;
            RefreshLayerList();
            ClearCodeDirty();
            RefreshCode();
            SetStatus(selected.Count == 1 ? "Deleted sprite" : $"Deleted {selected.Count} sprites");
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
            System.Diagnostics.Debug.WriteLine($"[OnSelectionChanged] sprite='{sp?.DisplayName ?? "(null)"}' _updatingProps={_updatingProps}");
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

                        // Show runtime-data warning when the selected sprite was populated by game data
                        if (_lblRuntimeData != null)
                            _lblRuntimeData.Visible = sp.IsSnapshotData;
                    }
                    else
                    {
                        if (_lblRuntimeData != null)
                            _lblRuntimeData.Visible = false;
                    }

                        // Sync layer list selection (uses _layerListSprites which reflects sort order + isolation filtering)
                        if (sp != null && _layout != null && _layerListSprites != null)
                        {
                            int idx = _layerListSprites.IndexOf(sp);
                            _lstLayers.SelectedIndex = (idx >= 0 && idx < _lstLayers.Items.Count) ? idx : -1;
                        }
                        else
                        {
                            _lstLayers.SelectedIndex = -1;
                        }
            }
            finally { _updatingProps = false; }

            RefreshExpressionColors();
            // Skip RefreshCode during execution — the execution handler manages code display.
            // Without this guard, expression-based PB code (e.g. $"Temp: {temperature:F1}°C")
            // gets destroyed: PatchOriginalSource fails for expression text → GenerateRoundTrip
            // replaces expressions with literal evaluated values.
            if (!_executingCode)
                RefreshCode();  // Protected by _codeBoxDirty check inside RefreshCode()
            HighlightLinkedVariables(_canvas.SelectedSprite);
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

            // During a drag the code box is intentionally frozen to avoid constant
            // regeneration.  OnDragCompleted will refresh it once the drag ends.
            if (_canvas.IsDragging)
            {
                _lblCodeTitle.Text      = "⟳ dragging…";
                _lblCodeTitle.ForeColor = Color.FromArgb(160, 160, 160);
                UpdateStatus();
                return;
            }

            RefreshCode(writeBack: true);  // Protected by _codeBoxDirty check inside RefreshCode()
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
            ClearCodeDirty();
            RefreshCode(writeBack: true);
        }

        private void OnAlphaChanged(object sender, EventArgs e)
        {
            _lblAlpha.Text = _trackAlpha.Value.ToString();
            if (_updatingProps || _canvas?.SelectedSprite == null) return;
            PushUndo();
            _canvas.SelectedSprite.ColorA = _trackAlpha.Value;
            _canvas.Invalidate();
            ClearCodeDirty();
            RefreshCode(writeBack: true);
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
                ClearCodeDirty();
                RefreshCode(writeBack: true);
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
        /// Returns true when the call expression refers to a virtual switch-case
        /// method (e.g. RenderHeader) that has no real method body in the user's
        /// source code.  These expressions are UI-only and must not be passed to
        /// the compiler.
        /// </summary>
        private bool IsVirtualSwitchCaseCall(string callExpression)
        {
            if (_detectedMethods == null || string.IsNullOrWhiteSpace(callExpression))
                return false;
            var info = _detectedMethods.FirstOrDefault(m => m.CallExpression == callExpression);
            return info != null && info.Kind == MethodKind.SwitchCase;
        }

        /// <summary>
        /// Re-executes the current OriginalSourceCode to refresh sprite values on the canvas.
        /// Used after expression color edits.
        /// </summary>
        private void TryReExecuteCode()
        {
            if (_layout?.OriginalSourceCode == null) return;
            if (IsOneWayStreaming) return;

            var callExpr = _execCallBox?.Text?.Trim();
            if (string.IsNullOrEmpty(callExpr)) callExpr = null;

            // Virtual switch-case methods (e.g. RenderHeader) have no real method
            // body — pass null so the compiler auto-detects the real render method.
            if (callExpr != null && IsVirtualSwitchCaseCall(callExpr))
                callExpr = null;

            try
            {
                var result = CodeExecutor.Execute(_layout.OriginalSourceCode, callExpr, capturedRows: _layout?.CapturedRows);
                if (result.Success && result.Sprites.Count > 0)
                {
                    TagSnapshotSprites(result.Sprites);

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
                    RefreshCode(writeBack: true);
                    RefreshExpressionColors();

                    // Re-sync the colour swatch and alpha slider for the selected sprite
                    // so the properties panel immediately reflects the updated colour values.
                    var sel = _canvas.SelectedSprite;
                    if (sel != null)
                    {
                        _updatingProps = true;
                        try
                        {
                            _colorPreview.BackColor = sel.Color;
                            _trackAlpha.Value       = Math.Max(0, Math.Min(255, sel.ColorA));
                            _lblAlpha.Text          = sel.ColorA.ToString();
                        }
                        finally { _updatingProps = false; }
                    }
                }
            }
            catch
            {
                // Execution failed — just refresh the code display
                RefreshCode();
            }
        }

        private SpriteEntry SpriteFromLayerIndex(int listIdx)
        {
            if (_layout == null || listIdx < 0) return null;
            // Use _layerListSprites which already reflects sort order + isolation filtering
            if (_layerListSprites != null)
                return listIdx < _layerListSprites.Count ? _layerListSprites[listIdx] : null;
            return listIdx < _layout.Sprites.Count ? _layout.Sprites[listIdx] : null;
        }

        /// <summary>
        /// Returns all sprites currently selected (from layer list or canvas multi-select).
        /// </summary>
        private List<SpriteEntry> GetSelectedSprites()
        {
            var list = new List<SpriteEntry>();
            if (_layout == null) return list;

            // Include canvas multi-select (Shift+click on canvas)
            if (_canvas?.SelectedSprites != null && _canvas.SelectedSprites.Count > 0)
            {
                foreach (var sp in _canvas.SelectedSprites)
                    if (!list.Contains(sp)) list.Add(sp);
            }

            // Include layer list multi-select
            foreach (int idx in _lstLayers.SelectedIndices)
            {
                var sp = SpriteFromLayerIndex(idx);
                if (sp != null && !list.Contains(sp)) list.Add(sp);
            }
            return list;
        }

        private void OnLayerListSelectionChanged(object sender, EventArgs e)
        {
            if (_updatingProps || _layout == null)
            {
                System.Diagnostics.Debug.WriteLine($"[OnLayerListSelectionChanged] SKIPPED — _updatingProps={_updatingProps}, _layout={((_layout == null) ? "null" : "ok")}");
                return;
            }
            // In multi-select mode, set canvas to the focused item (last clicked)
            // SelectedIndex still returns the most recently toggled item
            int idx = _lstLayers.SelectedIndex;
            var sprite = SpriteFromLayerIndex(idx);
            System.Diagnostics.Debug.WriteLine($"[OnLayerListSelectionChanged] idx={idx}, sprite='{sprite?.DisplayName ?? "(null)"}', _layerListSprites={((_layerListSprites == null) ? "null" : _layerListSprites.Count.ToString())}");
            if (sprite != null)
                _canvas.SelectedSprite = sprite;
        }

        private void OnLayerListDoubleClick(object sender, MouseEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[OnLayerListDoubleClick] Event triggered");

            if (_layout == null || _codeBox.TextLength == 0)
            {
                System.Diagnostics.Debug.WriteLine("[OnLayerListDoubleClick] No layout or no code - aborting");
                return;
            }

            // Get the sprite from the clicked list item
            int listIdx = _lstLayers.IndexFromPoint(e.Location);
            System.Diagnostics.Debug.WriteLine($"[OnLayerListDoubleClick] List index: {listIdx}");

            var sprite = SpriteFromLayerIndex(listIdx);
            if (sprite == null)
            {
                System.Diagnostics.Debug.WriteLine("[OnLayerListDoubleClick] No sprite at clicked position");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[OnLayerListDoubleClick] Navigating to sprite: {sprite.DisplayName}");
            System.Diagnostics.Debug.WriteLine($"[OnLayerListDoubleClick] Text='{sprite.Text}' SpriteName='{sprite.SpriteName}' SourceStart={sprite.SourceStart} Method='{sprite.SourceMethodName}' Idx={sprite.SourceMethodIndex}");

            // VALIDATION: Show what code line SourceStart points to
            if (sprite.SourceStart >= 0 && sprite.SourceStart < _codeBox.TextLength)
            {
                string cText = _codeBox.Text;
                int vLineStart = cText.LastIndexOf('\n', sprite.SourceStart) + 1;
                int vLineEnd = cText.IndexOf('\n', sprite.SourceStart);
                if (vLineEnd < 0) vLineEnd = cText.Length;
                string vLine = cText.Substring(vLineStart, Math.Min(100, vLineEnd - vLineStart)).Trim();
                int lineNum = 1;
                for (int ci = 0; ci < sprite.SourceStart; ci++)
                    if (cText[ci] == '\n') lineNum++;
                System.Diagnostics.Debug.WriteLine($"[OnLayerListDoubleClick] SourceStart {sprite.SourceStart} → line {lineNum}: \"{vLine}\"");
            }

            // Use the NEW navigation system - parses current code with Roslyn EVERY TIME
            // No stale tracking, no approximations, no fallbacks - just EXACT navigation
            bool success = CodeNavigationService.NavigateToSprite(sprite, _codeBox);

            if (!success)
            {
                System.Diagnostics.Debug.WriteLine($"[OnLayerListDoubleClick] ✗ Navigation failed for sprite '{sprite.DisplayName}'");
                SetStatus($"Could not find source code for '{sprite.DisplayName}'");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[OnLayerListDoubleClick] ✓ Navigation succeeded");

                // Defer focus to codeBox so the ListBox doesn't reclaim it
                // after the double-click handler returns.
                BeginInvoke(new Action(() =>
                {
                    _codeBox.Focus();
                    _codeBox.ScrollToCaret();
                }));
            }
        }

        /// <summary>
        /// Scrolls the code editor to the definition of the method referenced by
        /// <paramref name="callExpression"/> and briefly highlights the signature line.
        /// </summary>
        private void JumpToMethodDefinition(string callExpression)
        {
            if (_codeBox == null || string.IsNullOrWhiteSpace(callExpression)) return;

            // First, ensure we're displaying source code that contains the method definition
            string searchCode = _codeBox.Text.Replace("\r", "");
            int offset = CodeExecutor.FindMethodDefinitionOffset(searchCode, callExpression);

            // If not found and we have OriginalSourceCode, switch to show it
            if (offset < 0 && _layout?.OriginalSourceCode != null)
            {
                SetCodeText(_layout.OriginalSourceCode);
                searchCode = _codeBox.Text.Replace("\r", "");
                offset = CodeExecutor.FindMethodDefinitionOffset(searchCode, callExpression);
            }

            if (offset < 0)
            {
                SetStatus($"Could not find definition of '{callExpression}' in code.");
                return;
            }

            // Find the start of the line containing the method definition
            int lineStart = searchCode.LastIndexOf('\n', Math.Max(0, offset - 1));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;

            // Find the end of the method signature (opening brace or line end)
            int sigEnd = searchCode.IndexOf('{', offset);
            if (sigEnd < 0) sigEnd = searchCode.IndexOf('\n', offset);
            if (sigEnd < 0) sigEnd = searchCode.Length;

            // Calculate selection length
            int selectionLength = sigEnd - lineStart;

            _suppressCodeBoxEvents = true;
            try
            {
                _codeBox.Focus();
                _codeBox.Select(lineStart, selectionLength);
                _codeBox.ScrollToCaret();
                SetStatus($"Found: {callExpression}");
            }
            finally
            {
                _suppressCodeBoxEvents = false;
            }
        }

        // ── Refresh helpers ───────────────────────────────────────────────────────
        private void RefreshLayerList()
        {
            _updatingProps = true;
            try
            {
                // Preserve multi-selection state before clearing
                var prevSelected = GetSelectedSprites();

                _lstLayers.Items.Clear();
                if (_layout == null) return;

                // CRITICAL FIX: Clear SpriteName for ALL text sprites before displaying
                // to ensure DisplayName shows text content, not texture sprite names.
                // This handles sprites loaded from old .splcd files or cached before the fix.
                foreach (var sprite in _layout.Sprites)
                {
                    if (sprite.Type == SpriteEntryType.Text)
                        sprite.SpriteName = null;
                }

                // Safety: if isolation is active but HighlightedSprites was
                // cleared by another path, re-sync from _isolatedCallSprites.
                var highlighted = _canvas.HighlightedSprites;
                if (highlighted == null && _isolatedCallSprites != null && _isolatedCallSprites.Count > 0)
                {
                    _canvas.HighlightedSprites = _isolatedCallSprites;
                    highlighted = _isolatedCallSprites;
                }

                // Sort by SourceStart so layer list matches code order.
                // CRITICAL: Use LINQ OrderBy (stable sort) instead of List.Sort (unstable).
                // When many sprites have SourceStart == -1 (dynamic/loop-generated),
                // unstable sort randomizes their order. Stable sort preserves the
                // original execution order as a sensible fallback.
                var sortedSprites = _layout.Sprites
                    .Select((s, i) => new { Sprite = s, OrigIdx = i })
                    .OrderBy(x => x.Sprite.SourceStart < 0 ? int.MaxValue : x.Sprite.SourceStart)
                    .ThenBy(x => x.OrigIdx)
                    .Select(x => x.Sprite)
                    .ToList();

                int trackedCount = 0;
                foreach (var s in sortedSprites)
                    if (s.SourceStart >= 0) trackedCount++;
                System.Diagnostics.Debug.WriteLine($"[RefreshLayerList] {trackedCount}/{sortedSprites.Count} sprites have SourceStart, total in layout: {_layout.Sprites.Count}");

                // Build a mapping from list index to sprite for re-selection
                var listSprites = new List<SpriteEntry>();
                foreach (var s in sortedSprites)
                {
                    // During isolation mode, hide dimmed (non-highlighted) sprites from the list
                    if (highlighted != null && !highlighted.Contains(s))
                        continue;

                    string prefix = s.IsHidden ? "⊘ "
                        : s.IsReferenceLayout ? "[REF] "
                        : s.IsSnapshotData ? "\u26A0 "
                        : s.SourceStart < 0 ? "· "
                        : "";

                    // Animation group indicator
                    if (!string.IsNullOrEmpty(s.AnimationGroupId))
                        prefix += s.KeyframeAnimation != null ? "⟐ " : "⟡ ";

                    // Append variable name annotation when present (shows code structure)
                    string suffix = !string.IsNullOrEmpty(s.VariableName) 
                        ? $"  // {s.VariableName}"
                        : "";

                    _lstLayers.Items.Add(prefix + s.DisplayName + suffix);
                    listSprites.Add(s);
                }

                _layerListSprites = listSprites;

                // Restore multi-selection
                if (prevSelected.Count > 0)
                {
                    for (int i = 0; i < listSprites.Count; i++)
                    {
                        if (prevSelected.Contains(listSprites[i]))
                            _lstLayers.SetSelected(i, true);
                    }
                }
                else
                {
                    // Fall back to canvas selection
                    var sel = _canvas.SelectedSprite;
                    if (sel != null)
                    {
                        int listIdx = listSprites.IndexOf(sel);
                                    if (listIdx >= 0)
                                        _lstLayers.SetSelected(listIdx, true);
                                    }
                                }

                                // Show the "Show All" button when any layers are hidden or isolation is active
                                if (_btnShowAll != null)
                                {
                                    bool hasIsolation = _isolatedCallSprites != null && _isolatedCallSprites.Count > 0;
                                    bool hasHiddenLayers = false;
                                    foreach (var sp in _layout.Sprites)
                                    {
                                        if (sp.IsHidden) { hasHiddenLayers = true; break; }
                                    }
                                    _btnShowAll.Visible = hasIsolation || hasHiddenLayers;
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
                    case 3: return CodeStyle.Pulsar;
                    default: return CodeStyle.InGame;
                }
            }
        }

        /// <summary>
        /// Refreshes the mode-indicator label next to the code title so the user can
        /// always see which of the three output formats is currently active.
        /// </summary>
        private void UpdateCodeModeLabel()
        {
            if (_lblCodeMode == null || _cmbCodeStyle == null) return;
            switch (_cmbCodeStyle.SelectedIndex)
            {
                case 1:  _lblCodeMode.Text = "▸ Mod";            _lblCodeMode.ForeColor = Color.FromArgb(180, 220, 140); break;
                case 2:  _lblCodeMode.Text = "▸ Plugin / Torch"; _lblCodeMode.ForeColor = Color.FromArgb(220, 180, 100); break;
                case 3:  _lblCodeMode.Text = "▸ Pulsar";         _lblCodeMode.ForeColor = Color.FromArgb(100, 180, 255); break;
                default: _lblCodeMode.Text = "▸ In-Game (PB)";   _lblCodeMode.ForeColor = Color.FromArgb(140, 210, 140); break;
            }
        }

        /// <summary>
        /// Auto-switches the code style dropdown based on the detected script type
        /// of <paramref name="code"/>.  If the type cannot be determined (LcdHelper),
        /// the dropdown is left unchanged.
        /// </summary>
        private void AutoSwitchCodeStyle(string code)
        {
            if (_cmbCodeStyle == null || string.IsNullOrWhiteSpace(code)) return;
            var detectedType = CodeExecutor.DetectScriptType(code);
            int targetIndex;
            switch (detectedType)
            {
                case ScriptType.ProgrammableBlock: targetIndex = 0; break;
                case ScriptType.TorchPlugin:       targetIndex = 2; break; // "Plugin / Torch"
                case ScriptType.ModSurface:
                    // Distinguish between generic Mod (index 1) and Torch/Plugin (index 2)
                    if (code.Contains("#if TORCH") ||
                        code.Contains("#endif // TORCH") ||
                        code.Contains("class RenderContext") ||
                        code.Contains("struct LcdSpriteRow") ||
                        (code.Contains("RenderContext ctx") && code.Contains("LcdSpriteRow")))
                    {
                        targetIndex = 2; // "Plugin / Torch"
                    }
                    else
                    {
                        targetIndex = 1; // "Mod"
                    }
                    break;
                case ScriptType.PulsarPlugin:      targetIndex = 3; break;
                default: return; // LcdHelper — leave dropdown unchanged
            }
            if (_cmbCodeStyle.SelectedIndex != targetIndex)
                _cmbCodeStyle.SelectedIndex = targetIndex;
        }

        private bool IsActivelyStreaming =>
            (_pipeListener != null && _pipeListener.IsListening && !_pipeListener.IsPaused)
            || (_fileWatcher != null && _fileWatcher.IsListening && !_fileWatcher.IsPaused)
            || (_clipboardTimer != null);

        /// <summary>
        /// Regenerates the code panel from the current layout.
        /// IMPORTANT: By default, this respects _codeBoxDirty — if the user has manually
        /// edited the code (templates, animations, etc.), it will NOT overwrite their edits.
        /// Use force=true only when you explicitly want to overwrite user edits.
        /// </summary>
        private void RefreshCode(bool writeBack = false, bool force = false)
        {
            if (_layout == null) { SetCodeText(""); RefreshDetectedCalls(); return; }

            // CRITICAL: Respect user's manual code edits unless force=true.
            // This prevents templates, animation snippets, and other manual edits
            // from being overwritten by canvas interactions or other operations.
            if (_codeBoxDirty && !force)
                return;

            // FAST PATH: For Pulsar/Mod layouts, skip code regeneration for non-editing
            // calls (animation playback, selection changes, etc.) but allow through when:
            //  - force=true  (Generate Code button)
            //  - writeBack=true AND source-tracked sprites exist (property edits)
            //  - new user-added sprites need inserting
            if (_layout.IsPulsarOrModLayout && !force)
            {
                bool hasNewSprites = false;
                bool hasSourceTracking = false;
                foreach (var sp in _layout.Sprites)
                {
                    if (sp.SourceStart < 0 && !sp.IsFromExecution)
                    {
                        hasNewSprites = true;
                        break;
                    }
                    if (sp.SourceStart >= 0 && sp.ImportBaseline != null)
                        hasSourceTracking = true;
                }

                // Allow through for property edits (writeBack) when source tracking
                // is active, or when new sprites need inserting.
                if (!hasNewSprites && !(hasSourceTracking && writeBack))
                {
                    return;
                }
            }

            // Clear the "user has manually edited" state — any action that calls
            // RefreshCode wants the code panel to reflect the current layout.
            ClearCodeDirty();

            // In round-trip mode keep the Apply Code button visible so the user
            // can re-import the patched code to sync canvas sprites at any time.
            if (_btnApplyCode != null && _layout.OriginalSourceCode != null)
                _btnApplyCode.Visible = true;

            // While a one-way live source (pipe or clipboard) is actively
            // streaming, the layout contains runtime-expanded sprites that
            // don't match the original source structure.  Freeze the code
            // panel so the user keeps seeing their compact source code.
            // The file watcher is excluded — it operates bidirectionally so
            // we need to regenerate code and write it back to the file.
            if (IsOneWayStreaming) return;

            // During animation playback, _layout.Sprites are replaced with
            // fresh execution sprites every frame — they have no source tracking
            // (SourceStart=-1, no ImportBaseline).  PatchOriginalSource fails
            // for these, and GenerateRoundTrip replaces expression-based code
            // (interpolated strings, ternary colors) with literal evaluated values.
            // Freeze the code panel during animation; OnAnimStopped restores
            // the pre-animation sprites with full source tracking.
            if (_animPlayer != null && _animPlayer.IsPlaying)
                return;

            // Try round-trip: splice updated sprites back into the original pasted code
            if (_layout.OriginalSourceCode != null)
            {
                // Capture original for diff view before any patching
                string prePatched = _layout.OriginalSourceCode;

                // Per-sprite patching (dynamic code — loops, switch/case, expressions)
                // PatchOriginalSource handles text, color, position, size updates via ImportBaseline diffs
                string patched = CodeGenerator.PatchOriginalSource(_layout);
                if (patched != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[RefreshCode] PatchOriginalSource succeeded, writeBack={writeBack}");

                    // Only use Roslyn if there are actually NEW sprites to insert
                    // (not from execution, and not already tracked)
                    bool hasNewSprites = _layout.Sprites.Any(sp => sp.SourceStart < 0 && !sp.IsFromExecution);
                    if (hasNewSprites)
                    {
                        System.Diagnostics.Debug.WriteLine($"[RefreshCode] Found {_layout.Sprites.Count(sp => sp.SourceStart < 0 && !sp.IsFromExecution)} new sprites to insert");
                        // Use Roslyn for proper syntax-aware insertion
                        string listVar = RoslynCodeMerger.DetectListVariable(patched);
                        var roslynResult = RoslynCodeMerger.InsertSprites(patched, _layout.Sprites, listVar);
                        if (roslynResult.Success && roslynResult.SpritesInserted > 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"[RefreshCode] Roslyn inserted {roslynResult.SpritesInserted} sprites");
                            patched = roslynResult.Code;
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[RefreshCode] Roslyn insertion failed: {roslynResult.Error}, using fallback");
                            patched = CodeGenerator.InsertNewSpritesIntoSource(_layout, patched); // Fallback
                        }
                    }

                    // Show diff if the code actually changed
                    if (patched != prePatched)
                        ShowPatchDiff(prePatched, patched);

                    // Update OriginalSourceCode so future patches work against the new baseline
                    _layout.OriginalSourceCode = patched;

                    // Re-establish source tracking: SourceStart/SourceEnd may have shifted
                    // if the patch changed code length, and ImportBaseline must reflect the
                    // current values so the NEXT edit diffs correctly.
                    RefreshSourceTracking();

                    SetCodeText(patched);
                    RefreshDetectedCalls();
                    if (writeBack)
                    {
                        System.Diagnostics.Debug.WriteLine($"[RefreshCode] Writing back to watched file");
                        WriteBackToWatchedFile(patched);
                    }
                    return;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[RefreshCode] PatchOriginalSource returned null");
                }

                // Region-based replacement (static layouts with literal positions)
                string roundTrip = CodeGenerator.GenerateRoundTrip(_layout);
                if (roundTrip != null)
                {
                    // Only use Roslyn if there are actually NEW sprites to insert
                    bool hasNewSprites = _layout.Sprites.Any(sp => sp.SourceStart < 0 && !sp.IsFromExecution);
                    if (hasNewSprites)
                    {
                        string listVar = RoslynCodeMerger.DetectListVariable(roundTrip);
                        var roslynResult = RoslynCodeMerger.InsertSprites(roundTrip, _layout.Sprites, listVar);
                        if (roslynResult.Success && roslynResult.SpritesInserted > 0)
                            roundTrip = roslynResult.Code;
                        else
                            roundTrip = CodeGenerator.InsertNewSpritesIntoSource(_layout, roundTrip); // Fallback
                    }

                    // Show diff if the code actually changed
                    if (roundTrip != prePatched)
                        ShowPatchDiff(prePatched, roundTrip);

                    _layout.OriginalSourceCode = roundTrip;

                    SetCodeText(roundTrip);
                    RefreshDetectedCalls();
                    if (writeBack) WriteBackToWatchedFile(roundTrip);
                    return;
                }

                // Both round-trip paths failed.  If no sprites have source tracking
                // (e.g. Pulsar/Mod execution, live stream) we still want to INSERT
                // new sprites into the code while preserving the original structure.
                bool hasTracking = false;
                foreach (var sp in _layout.Sprites)
                    if (sp.SourceStart >= 0 && sp.ImportBaseline != null) { hasTracking = true; break; }

                if (!hasTracking)
                {
                    // Check if there are any truly NEW sprites that need inserting
                    // (SourceStart < 0 AND not from execution)
                    bool hasNewSprites = false;
                    foreach (var sp in _layout.Sprites)
                        if (sp.SourceStart < 0 && !sp.IsFromExecution) { hasNewSprites = true; break; }

                    if (hasNewSprites)
                    {
                        // For Pulsar/Mod layouts: use Roslyn to insert new sprites into the original code
                        // This properly parses the syntax tree and inserts after the last frame.Add()
                        string listVar = RoslynCodeMerger.DetectListVariable(_layout.OriginalSourceCode);
                        var mergeResult = RoslynCodeMerger.InsertSprites(_layout.OriginalSourceCode, _layout.Sprites, listVar);

                        if (mergeResult.Success && mergeResult.SpritesInserted > 0)
                        {
                            _layout.OriginalSourceCode = mergeResult.Code;
                            SetCodeText(mergeResult.Code);
                            RefreshDetectedCalls();
                            if (writeBack) WriteBackToWatchedFile(mergeResult.Code);
                            SetStatus($"Inserted {mergeResult.SpritesInserted} sprite(s) via Roslyn");
                            return;
                        }
                    }

                    // No new sprites or Roslyn merge succeeded with no changes
                    // Just show the original code without parsing
                    SetCodeText(_layout.OriginalSourceCode);
                    RefreshDetectedCalls();
                    return;
                }
            }

            // Full regeneration fallback - NEVER for Pulsar/Mod layouts.
            // Their dynamic code (expressions, loops, conditionals) cannot be
            // regenerated from runtime sprites — always preserve the original.
            if (_layout.IsPulsarOrModLayout)
            {
                // Keep the original code — full regeneration would destroy it.
                // If we got here, PatchOriginalSource and GenerateRoundTrip both failed;
                // the original code is still the safest thing to show.
                if (_layout.OriginalSourceCode != null)
                    SetCodeText(_layout.OriginalSourceCode);
                RefreshDetectedCalls();
                return;
            }

            SetCodeText(CodeGenerator.Generate(_layout, SelectedCodeStyle));
            RefreshDetectedCalls();
        }

        /// <summary>
        /// Re-establishes SourceStart/SourceEnd/ImportBaseline on layout sprites
        /// after PatchOriginalSource changes the code.  This ensures subsequent
        /// property edits diff against the correct baseline and correct offsets.
        /// Uses the same 3-strategy matching as the execution handler:
        ///   Strategy 1: Type+content pool matching (exact text/sprite name)
        ///   Strategy 2: SourceLineNumber fallback (for expression-based text)
        ///   Strategy 3: Index-based all-or-nothing (when counts match)
        /// </summary>
        private void RefreshSourceTracking()
        {
            if (_layout?.OriginalSourceCode == null) return;

            var parsedSprites = CodeParser.Parse(_layout.OriginalSourceCode);
            if (parsedSprites.Count == 0) return;

            // Sort parsed sprites by SourceStart so matching is in source order
            parsedSprites.Sort((a, b) =>
            {
                if (a.SourceStart < 0 && b.SourceStart < 0) return 0;
                if (a.SourceStart < 0) return 1;
                if (b.SourceStart < 0) return -1;
                return a.SourceStart.CompareTo(b.SourceStart);
            });

            // ── Strategy 1: Type+content pool matching ──
            var pool = new Dictionary<string, Queue<SpriteEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var ps in parsedSprites)
            {
                string key = ps.Type == SpriteEntryType.Text
                    ? "TEXT|" + (ps.Text ?? "")
                    : "TEXTURE|" + (ps.SpriteName ?? "");
                if (!pool.ContainsKey(key))
                    pool[key] = new Queue<SpriteEntry>();
                pool[key].Enqueue(ps);
            }

            // Match layout sprites to parsed sprites and transfer updated offsets.
            // IMPORTANT: Clear SourceLineNumber when updating SourceStart — the execution-time
            // line number is stale after code patching (adding/removing sprites shifts lines).
            // Navigation will use the fresh SourceStart (Strategy 0a) instead.
            int unmatched = 0;
            foreach (var sp in _layout.Sprites)
            {
                string key = sp.Type == SpriteEntryType.Text
                    ? "TEXT|" + (sp.Text ?? "")
                    : "TEXTURE|" + (sp.SpriteName ?? "");

                if (pool.TryGetValue(key, out var queue) && queue.Count > 0)
                {
                    var parsed = queue.Dequeue();
                    sp.SourceStart = parsed.SourceStart;
                    sp.SourceEnd = parsed.SourceEnd;
                    sp.SourceLineNumber = -1; // stale after code modification — let SourceStart navigate
                    sp.ImportBaseline = sp.CloneValues();
                }
                else
                {
                    unmatched++;
                }
            }

            // ── Strategy 2: SourceLineNumber fallback ──
            // Handles expression-based text sprites where runtime text differs from
            // code literals (e.g. code has $"Temp: {val}°C", runtime has "Temp: 22.5°C").
            if (unmatched > 0)
            {
                string src = _layout.OriginalSourceCode;
                int lineNumberFallback = 0;
                foreach (var sp in _layout.Sprites)
                {
                    if (sp.SourceStart >= 0) continue; // already matched
                    if (sp.SourceLineNumber <= 0) continue; // no line info

                    int targetLine = sp.SourceLineNumber;
                    SpriteEntry bestMatch = null;
                    foreach (var ps in parsedSprites)
                    {
                        if (ps.SourceStart < 0) continue;
                        int psLine = 1;
                        for (int ci = 0; ci < ps.SourceStart && ci < src.Length; ci++)
                            if (src[ci] == '\n') psLine++;

                        if (psLine == targetLine && ps.Type == sp.Type)
                        {
                            bestMatch = ps;
                            break;
                        }
                    }

                    if (bestMatch != null)
                    {
                        sp.SourceStart = bestMatch.SourceStart;
                        sp.SourceEnd = bestMatch.SourceEnd;
                        sp.SourceLineNumber = -1; // stale after code modification
                        sp.ImportBaseline = sp.CloneValues();
                        lineNumberFallback++;
                    }
                }

                if (lineNumberFallback > 0)
                    System.Diagnostics.Debug.WriteLine($"[RefreshSourceTracking] Strategy 2 (SourceLineNumber): {lineNumberFallback} additional sprites matched");
            }

            // ── Strategy 3: Index-based all-or-nothing fallback ──
            // When type+content and line-number matching leave sprites untracked
            // AND parsed/execution counts match with aligned types, use positional index.
            bool hasUntracked = false;
            foreach (var sp in _layout.Sprites)
                if (sp.SourceStart < 0) { hasUntracked = true; break; }

            if (hasUntracked && parsedSprites.Count == _layout.Sprites.Count)
            {
                bool typesMatch = true;
                for (int i = 0; i < parsedSprites.Count; i++)
                {
                    if (parsedSprites[i].Type != _layout.Sprites[i].Type)
                    {
                        typesMatch = false;
                        break;
                    }
                }

                if (typesMatch)
                {
                    for (int i = 0; i < parsedSprites.Count; i++)
                    {
                        _layout.Sprites[i].SourceStart = parsedSprites[i].SourceStart;
                        _layout.Sprites[i].SourceEnd = parsedSprites[i].SourceEnd;
                        _layout.Sprites[i].SourceLineNumber = -1; // stale after code modification
                        _layout.Sprites[i].ImportBaseline = _layout.Sprites[i].CloneValues();
                    }
                    System.Diagnostics.Debug.WriteLine($"[RefreshSourceTracking] Strategy 3 (index-based): {parsedSprites.Count} sprites matched by position");
                }
            }
        }

        /// <summary>
        /// Sets <see cref="_codeBox"/> text without triggering the dirty flag.
        /// If the text hasn't changed, preserves the current scroll position and selection.
        /// RichTextBox normalises \n to \r\n internally, so we compare with
        /// normalised line endings to avoid resetting the control on every call
        /// (which would lose scroll position and selection).
        /// </summary>
        private void SetCodeText(string text)
        {
            _suppressCodeBoxEvents = true;
            // Normalise to \n for comparison — RichTextBox.Text always returns \r\n
            string normNew  = text.Replace("\r\n", "\n").Replace("\r", "\n");
            string normCur  = _codeBox.Text.Replace("\r\n", "\n").Replace("\r", "\n");
            if (normCur != normNew)
            {
                _codeBox.Text = text;
                _lastHighlightedCode = null;
                if (_codeBox.TextLength <= InitialHighlightMaxChars)
                {
                    SyntaxHighlighter.Highlight(_codeBox);
                    _lastHighlightedCode = _codeBox.Text;
                }
            }
            _suppressCodeBoxEvents = false;
        }

        /// <summary>
        /// Re-indents all lines in <see cref="_codeBox"/> using the specified style.
        /// Scans the entire text to detect the original indent unit, then converts
        /// every line's leading whitespace to the chosen style at the correct depth.
        /// </summary>
        private void ReindentCodeBox(int spaces, bool useTabs)
        {
            if (_codeBox == null || string.IsNullOrEmpty(_codeBox.Text)) return;

            string unit = useTabs ? "\t" : new string(' ', spaces);
            var lines = _codeBox.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            // ── Pass 1: detect the original indent unit ──
            // Measure the leading-space count of every indented line and find the
            // smallest non-zero value that appears.  That is the most likely unit.
            int detectedUnit = 4; // default fallback
            int minSpaces = int.MaxValue;
            foreach (string line in lines)
            {
                if (line.Length == 0 || line.TrimStart(' ', '\t').Length == 0) continue;

                int spaceCount = 0;
                bool hasTabs = false;
                for (int c = 0; c < line.Length; c++)
                {
                    if (line[c] == ' ') spaceCount++;
                    else if (line[c] == '\t') { hasTabs = true; break; }
                    else break;
                }

                // If the file already uses tabs, each tab = 1 level → unit is irrelevant
                if (hasTabs) { detectedUnit = 1; minSpaces = 1; break; }

                if (spaceCount > 0 && spaceCount < minSpaces)
                    minSpaces = spaceCount;
            }
            if (minSpaces != int.MaxValue && minSpaces > 0)
                detectedUnit = minSpaces;

            // ── Pass 2: re-indent each line ──
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int depth = 0;
                int j = 0;
                while (j < line.Length)
                {
                    if (line[j] == '\t') { depth++; j++; }
                    else if (line[j] == ' ')
                    {
                        int start = j;
                        while (j < line.Length && line[j] == ' ') j++;
                        depth += (j - start) / detectedUnit;
                    }
                    else break;
                }

                string content = line.TrimStart(' ', '\t');
                if (i > 0) sb.AppendLine();
                if (content.Length > 0)
                {
                    for (int k = 0; k < depth; k++) sb.Append(unit);
                }
                sb.Append(content);
            }

            int pos = _codeBox.SelectionStart;
            _codeBox.Text = sb.ToString();
            _codeBox.SelectionStart = Math.Min(pos, _codeBox.TextLength);
            SetStatus($"Indentation set to {(useTabs ? "tabs" : spaces + " spaces")}");
        }

        /// <summary>
        /// Smart auto-indent: analyzes brace structure and applies correct indentation
        /// to each line based on nesting depth. Select code and press Tab to reformat.
        /// If nothing is selected, inserts 4 spaces at the caret.
        /// </summary>
        private void CodeBoxIndentSelection()
        {
            const string indent = "    ";

            // No selection = quick 4-space insert at cursor
            if (_codeBox.SelectionLength == 0)
            {
                _codeBox.SelectedText = indent;
                return;
            }

            string fullText = _codeBox.Text;
            int selStart = _codeBox.SelectionStart;
            int selEnd = selStart + _codeBox.SelectionLength;

            // Expand to full lines
            int lineStart = selStart;
            while (lineStart > 0 && fullText[lineStart - 1] != '\n')
                lineStart--;

            // Calculate starting brace depth by counting { and } from document start
            int depth = 0;
            bool inString = false;
            bool inChar = false;
            bool inLineComment = false;
            bool inBlockComment = false;
            for (int i = 0; i < lineStart && i < fullText.Length; i++)
            {
                char c = fullText[i];
                char next = (i + 1 < fullText.Length) ? fullText[i + 1] : '\0';
                char prev = (i > 0) ? fullText[i - 1] : '\0';

                // Track comments and strings to avoid counting braces inside them
                if (inLineComment)
                {
                    if (c == '\n') inLineComment = false;
                    continue;
                }
                if (inBlockComment)
                {
                    if (c == '*' && next == '/') { inBlockComment = false; i++; }
                    continue;
                }
                if (c == '/' && next == '/') { inLineComment = true; i++; continue; }
                if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }

                if (!inChar && c == '"' && prev != '\\') inString = !inString;
                if (!inString && c == '\'' && prev != '\\') inChar = !inChar;
                if (inString || inChar) continue;

                if (c == '{') depth++;
                else if (c == '}') depth = Math.Max(0, depth - 1);
            }

            // Select the full lines
            _codeBox.SelectionStart = lineStart;
            _codeBox.SelectionLength = selEnd - lineStart;
            string block = _codeBox.SelectedText;
            string[] lines = block.Split('\n');

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string content = line.TrimStart(' ', '\t').TrimEnd('\r');

                // Empty lines stay empty (preserve blank lines)
                if (string.IsNullOrEmpty(content))
                {
                    if (i > 0) sb.Append('\n');
                    continue;
                }

                // If line starts with }, decrease depth BEFORE indenting
                if (content.StartsWith("}") || content.StartsWith(")"))
                    depth = Math.Max(0, depth - 1);

                // Apply indentation
                if (i > 0) sb.Append('\n');
                for (int d = 0; d < depth; d++)
                    sb.Append(indent);
                sb.Append(content);

                // Preserve trailing \r if original line had it
                if (line.EndsWith("\r"))
                    sb.Append('\r');

                // Count braces in content to adjust depth for NEXT line
                // (skip the leading brace we already handled)
                int startIdx = (content.StartsWith("}") || content.StartsWith(")")) ? 1 : 0;
                bool lineInString = false;
                bool lineInChar = false;
                bool lineInComment = false;
                for (int ci = startIdx; ci < content.Length; ci++)
                {
                    char c = content[ci];
                    char cnext = (ci + 1 < content.Length) ? content[ci + 1] : '\0';
                    char cprev = (ci > 0) ? content[ci - 1] : '\0';

                    if (lineInComment) continue; // rest of line is comment
                    if (c == '/' && cnext == '/') { lineInComment = true; continue; }

                    if (!lineInChar && c == '"' && cprev != '\\') lineInString = !lineInString;
                    if (!lineInString && c == '\'' && cprev != '\\') lineInChar = !lineInChar;
                    if (lineInString || lineInChar) continue;

                    if (c == '{') depth++;
                    else if (c == '}') depth = Math.Max(0, depth - 1);
                }
            }

            string result = sb.ToString();
            _codeBox.SelectedText = result;
            _codeBox.SelectionStart = lineStart;
            _codeBox.SelectionLength = result.Length;
            SetStatus($"Auto-indented {lines.Length} line(s)");
        }

        /// <summary>
        /// Removes up to 4 leading spaces (or one tab) from every selected line.
        /// When nothing is selected, outdents the current line.
        /// </summary>
        private void CodeBoxOutdentSelection()
        {
            string text = _codeBox.Text;
            if (string.IsNullOrEmpty(text)) return;

            int selStart = _codeBox.SelectionStart;
            int selEnd = selStart + _codeBox.SelectionLength;

            // Expand to full lines (handles both no-selection and partial selection)
            int lineStart = selStart;
            while (lineStart > 0 && text[lineStart - 1] != '\n')
                lineStart--;

            // If no selection, expand selEnd to end of current line
            if (selEnd == selStart)
            {
                while (selEnd < text.Length && text[selEnd] != '\n' && text[selEnd] != '\r')
                    selEnd++;
            }

            _codeBox.SelectionStart = lineStart;
            _codeBox.SelectionLength = selEnd - lineStart;
            string block = _codeBox.SelectedText;
            string[] lines = block.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int removed = 0;
                int j = 0;
                while (j < line.Length && removed < 4)
                {
                    if (line[j] == ' ') { removed++; j++; }
                    else if (line[j] == '\t') { removed = 4; j++; }
                    else break;
                }
                lines[i] = line.Substring(j);
            }

            string result = string.Join("\n", lines);
            _codeBox.SelectedText = result;
            _codeBox.SelectionStart = lineStart;
            _codeBox.SelectionLength = result.Length;
        }

        /// <summary>
        /// Inserts a newline that matches the indentation of the current line.
        /// Also adds an extra indent level when the line ends with '{'.
        /// </summary>
        private void CodeBoxAutoIndentNewline()
        {
            string text = _codeBox.Text;
            int caret = _codeBox.SelectionStart;

            // Find start of current line
            int lineStart = caret;
            while (lineStart > 0 && text[lineStart - 1] != '\n')
                lineStart--;

            // Extract leading whitespace from the current line
            int ws = lineStart;
            while (ws < caret && (text[ws] == ' ' || text[ws] == '\t'))
                ws++;
            string lineIndent = text.Substring(lineStart, ws - lineStart);

            // If the non-whitespace portion before the caret ends with '{', add an extra level
            string beforeCaret = text.Substring(ws, caret - ws).TrimEnd();
            if (beforeCaret.EndsWith("{"))
                lineIndent += "    ";

            _codeBox.SelectedText = "\n" + lineIndent;
        }

        /// <summary>
        /// Clears the dirty flag and restores the code panel header to its default state.
        /// </summary>
        private void ClearCodeDirty()
        {
            _codeBoxDirty = false;
            _lblCodeTitle.Text = "Generated C# Code";
            _lblCodeTitle.ForeColor = Color.FromArgb(150, 200, 255);
            if (_btnApplyCode != null && (_layout == null || _layout.OriginalSourceCode == null))
                _btnApplyCode.Visible = false;
        }

        /// <summary>
        /// Clears <see cref="LcdLayout.OriginalSourceCode"/> when a structural canvas
        /// edit (add / delete / duplicate) would break offset-based round-trip patching.
        /// Shows a one-time status hint so the user understands why the code resets.
        /// </summary>
        private void InvalidateOriginalSourceIfSet()
        {
            if (_layout?.OriginalSourceCode == null) return;
            _layout.OriginalSourceCode = null;
            SetStatus("Round-trip source cleared — canvas structure change replaced pasted code. Use 'Reset Source' if needed.");
        }

        /// <summary>
        /// Validates and clamps property values on a set of parsed sprites.
        /// Returns a list of human-readable correction descriptions.
        /// </summary>
        private static List<string> ValidateAndFixSprites(List<SpriteEntry> sprites)
        {
            var warnings = new List<string>();
            foreach (var sp in sprites)
            {
                if (float.IsNaN(sp.X) || float.IsInfinity(sp.X))
                { warnings.Add($"X invalid on '{sp.DisplayName}' → 0"); sp.X = 0f; }
                if (float.IsNaN(sp.Y) || float.IsInfinity(sp.Y))
                { warnings.Add($"Y invalid on '{sp.DisplayName}' → 0"); sp.Y = 0f; }
                if (sp.Width <= 0f || float.IsNaN(sp.Width) || float.IsInfinity(sp.Width))
                { warnings.Add($"Width invalid on '{sp.DisplayName}' → 10"); sp.Width = 10f; }
                if (sp.Height <= 0f || float.IsNaN(sp.Height) || float.IsInfinity(sp.Height))
                { warnings.Add($"Height invalid on '{sp.DisplayName}' → 10"); sp.Height = 10f; }
                if (sp.ColorA < 0 || sp.ColorA > 255)
{ warnings.Add($"Alpha out of range on '{sp.DisplayName}' → clamped"); sp.ColorA = sp.ColorA < 0 ? 0 : 255; }
                if (sp.Type == SpriteEntryType.Text && (sp.Scale <= 0f || float.IsNaN(sp.Scale) || float.IsInfinity(sp.Scale)))
                { warnings.Add($"Scale invalid on '{sp.DisplayName}' → 1"); sp.Scale = 1f; }
            }
            return warnings;
        }

        /// <summary>
        /// Parses the manually edited code in <see cref="_codeBox"/> and imports
        /// the resulting sprites onto the canvas, replacing the current layout.
        /// </summary>
        private void ApplyCodeFromPanel()
        {
            if (_layout == null) { SetStatus("No layout loaded."); return; }

            string sourceCode = _codeBox.Text;
            if (string.IsNullOrWhiteSpace(sourceCode))
            {
                SetStatus("Code panel is empty — nothing to apply.");
                return;
            }

            // Auto-detect script type from code markers
            AutoSwitchCodeStyle(sourceCode);

            var sprites = CodeParser.Parse(sourceCode);
            if (sprites.Count == 0)
            {
                MessageBox.Show(
                    "No MySprite definitions found in the code panel.\n\n"
                    + "Supported patterns:\n"
                    + "  new MySprite { Type = SpriteType.TEXTURE, ... }\n"
                    + "  new MySprite(SpriteType.TEXTURE, \"data\", ...)\n"
                    + "  MySprite.CreateText(\"text\", ...)\n"
                    + "  sprite.Type = SpriteType.TEXTURE; sprite.Data = ...;",
                    "Nothing Found", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Validate and clamp any out-of-range values produced by the parser
            var warnings = ValidateAndFixSprites(sprites);

            // Per-sprite source tracking for round-trip patching
            var contextCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var sprite in sprites)
            {
                string ctx = sprite.SourceStart >= 0
                    ? CodeGenerator.DetectSpriteContext(sourceCode, sprite.SourceStart)
                    : null;

                string typeHint = sprite.Type == SpriteEntryType.Text
                    ? "Text"
                    : sprite.SpriteName ?? "Texture";

                string label = ctx != null ? $"{ctx}: {typeHint}" : typeHint;

                if (!contextCounts.TryGetValue(label, out int count))
                    count = 0;
                contextCounts[label] = count + 1;
                if (count > 0)
                    label += $".{count + 1}";

                sprite.ImportLabel = label;
                sprite.ImportBaseline = sprite.CloneValues();
            }

            PushUndo();

            // Sort sprites by SourceStart so layout order matches source order.
            // The parser returns sprites in pass order (Pass 1, 2, 3...), not
            // source order.  When code mixes new MySprite() and MySprite.CreateText(),
            // the ordinal-based code jump (Strategy 4) would select the wrong sprite.
            sprites.Sort((a, b) =>
            {
                if (a.SourceStart < 0 && b.SourceStart < 0) return 0;
                if (a.SourceStart < 0) return 1;
                if (b.SourceStart < 0) return -1;
                return a.SourceStart.CompareTo(b.SourceStart);
            });

            _layout.Sprites.Clear();
            foreach (var sprite in sprites)
                _layout.Sprites.Add(sprite);

            _layout.OriginalSourceCode = sourceCode;

            // Tag text sprites whose content originates from runtime game data
            TagSnapshotSprites(sprites);

            _canvas.SelectedSprite = sprites.Count > 0 ? sprites[0] : null;
            _canvas.Invalidate();
            RefreshLayerList();
            ClearCodeDirty();
            RefreshCode();
            RefreshDebugStats();
            if (warnings.Count > 0)
                SetStatus($"Applied {sprites.Count} sprite(s) — {warnings.Count} value(s) corrected: {string.Join("; ", warnings)}");
            else
                SetStatus($"Applied {sprites.Count} sprite(s) from the code panel.");
        }

        /// <summary>
        /// Tags sprites whose text content originates from captured runtime data
        /// (e.g. item names, quantities) so the UI can warn users not to edit text.
        /// Sources checked (in order): CapturedRows, detected-call LcdSpriteRow
        /// initializers, and finally a blanket check for LcdSpriteRow code patterns.
        /// </summary>
        private void TagSnapshotSprites(List<SpriteEntry> sprites)
        {
            var runtimeTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Source 1: Captured runtime rows from a snapshot
            var rows = _layout?.CapturedRows;
            if (rows != null)
            {
                foreach (var r in rows)
                {
                    if (!string.IsNullOrEmpty(r.Text))     runtimeTexts.Add(r.Text);
                    if (!string.IsNullOrEmpty(r.StatText))  runtimeTexts.Add(r.StatText);
                }
            }

            // Source 2: Text literals embedded in detected call expressions
            //   e.g. RenderHeader(default, new LcdSpriteRow { Text = "Inventory", ... })
            if (_lstDetectedCalls != null)
            {
                foreach (var item in _lstDetectedCalls.Items)
                {
                    string call = item.ToString();
                    if (!call.Contains("LcdSpriteRow")) continue;
                    ExtractQuotedValues(call, "Text = \"", runtimeTexts);
                    ExtractQuotedValues(call, "StatText = \"", runtimeTexts);
                }
            }

            // If we matched specific runtime text values, tag only those sprites
            if (runtimeTexts.Count > 0)
            {
                foreach (var sp in sprites)
                {
                    if (sp.Type != SpriteEntryType.Text) continue;
                    if (string.IsNullOrEmpty(sp.Text)) continue;

                    if (runtimeTexts.Contains(sp.Text))
                    {
                        sp.IsSnapshotData  = true;
                        sp.RuntimeDataNote = "Text set by game data at runtime \u2014 edit position, color, and size instead";
                    }
                }
            }
            else
            {
                // Fallback: if the code uses LcdSpriteRow-based rendering, tag ALL
                // text sprites — all text content comes from row data that varies at runtime
                string code = _codeBox?.Text;
                if (code != null && code.Contains("LcdSpriteRow"))
                {
                    foreach (var sp in sprites)
                    {
                        if (sp.Type != SpriteEntryType.Text) continue;
                        if (string.IsNullOrEmpty(sp.Text)) continue;
                        sp.IsSnapshotData  = true;
                        sp.RuntimeDataNote = "Text set by game data at runtime \u2014 edit position, color, and size instead";
                    }
                }
            }

            // Un-tag sprites whose text is hard-coded as a literal in a
            // MySprite.CreateText("text",...) call — those are user-authored
            // constants, not runtime row data.
            string src = _layout?.OriginalSourceCode ?? _codeBox?.Text;
            if (src != null)
            {
                var codeLiterals = new HashSet<string>(StringComparer.Ordinal);
                int p = 0;
                while (p < src.Length)
                {
                    int idx = src.IndexOf("CreateText(\"", p, StringComparison.Ordinal);
                    if (idx < 0) break;
                    idx += "CreateText(\"".Length;
                    int end = src.IndexOf('"', idx);
                    if (end > idx) codeLiterals.Add(src.Substring(idx, end - idx));
                    p = (end > idx) ? end + 1 : idx + 1;
                }

                foreach (var sp in sprites)
                {
                    if (!sp.IsSnapshotData) continue;
                    if (sp.Type != SpriteEntryType.Text) continue;
                    if (string.IsNullOrEmpty(sp.Text)) continue;
                    if (codeLiterals.Contains(sp.Text))
                    {
                        sp.IsSnapshotData  = false;
                        sp.RuntimeDataNote = null;
                    }
                }
            }
        }

        /// <summary>
        /// Extracts quoted string values following a given prefix pattern.
        /// e.g. prefix="Text = \"" finds the value in: Text = "Iron Ingot"
        /// </summary>
        private static void ExtractQuotedValues(string source, string prefix, HashSet<string> results)
        {
            int pos = 0;
            while (pos < source.Length)
            {
                int start = source.IndexOf(prefix, pos, StringComparison.Ordinal);
                if (start < 0) break;
                start += prefix.Length;
                int end = source.IndexOf('"', start);
                if (end < 0) break;
                string val = source.Substring(start, end - start);
                if (val.Length > 0) results.Add(val);
                pos = end + 1;
            }
        }

        private void RefreshDetectedCalls()
        {
            if (_lstDetectedCalls == null) return;

            // DIAGNOSTIC: Check what's in the code box and what gets detected
            string codeText = _codeBox.Text ?? "";
            System.Diagnostics.Debug.WriteLine($"[RefreshDetectedCalls] Code length: {codeText.Length}");

            var calls = CodeExecutor.DetectAllCallExpressions(codeText, _layout?.CapturedRows);
            _detectedMethods = CodeExecutor.GetDetectedMethodsWithMetadata(codeText, calls, _layout?.CapturedRows);

            System.Diagnostics.Debug.WriteLine($"[RefreshDetectedCalls] Detected {calls.Count} calls:");
            foreach (var c in calls)
                System.Diagnostics.Debug.WriteLine($"  - {c}");

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


    }
}
