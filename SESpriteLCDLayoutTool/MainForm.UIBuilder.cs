using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;
using SESpriteLCDLayoutTool.Controls;
using SESpriteLCDLayoutTool.Data;
using SESpriteLCDLayoutTool.Models;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool
{
    public partial class MainForm
    {
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

            // Canvas + rulers container
            _canvas = new LcdCanvas { Dock = DockStyle.Fill };
            _canvas.SelectionChanged += OnSelectionChanged;
            _canvas.SpriteModified   += OnSpriteModified;
            _canvas.DragStarting     += (ss, ee) => PushUndo();
            _canvas.DragCompleted    += OnDragCompleted;
            _canvas.ContextMenuStrip  = BuildCanvasContextMenu();

            // Rulers
            _rulerH = new CanvasRuler(CanvasRuler.Orientation.Horizontal);
            _rulerV = new CanvasRuler(CanvasRuler.Orientation.Vertical);
            _rulerCorner = new Panel
            {
                Width     = CanvasRuler.Thickness,
                Height    = CanvasRuler.Thickness,
                Dock      = DockStyle.None,
                BackColor = Color.FromArgb(32, 32, 36),
            };

            // Panel that hosts canvas + rulers using TableLayoutPanel
            var canvasTable = new TableLayoutPanel
            {
                Dock        = DockStyle.Fill,
                ColumnCount = 2,
                RowCount    = 2,
                Padding     = new Padding(0),
                Margin      = new Padding(0),
                BackColor   = Color.FromArgb(28, 28, 28),
            };
            canvasTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, CanvasRuler.Thickness));
            canvasTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            canvasTable.RowStyles.Add(new RowStyle(SizeType.Absolute, CanvasRuler.Thickness));
            canvasTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            canvasTable.Controls.Add(_rulerCorner, 0, 0);
            canvasTable.Controls.Add(_rulerH, 1, 0);
            canvasTable.Controls.Add(_rulerV, 0, 1);
            canvasTable.Controls.Add(_canvas, 1, 1);

            // Wire ruler updates: mouse move fires hairline tracking + transform sync
            _canvas.SurfaceMouseMoved += (mx, my) =>
            {
                _rulerH.SetCursorPos(mx);
                _rulerV.SetCursorPos(my);
            };

            // Sync transform to rulers after every repaint (zoom/pan/resize)
            _canvas.Paint += (ss, ee) =>
            {
                if (_canvas.CanvasLayout == null) return;
                _canvas.GetCurrentTransform(out float sc, out PointF orig);
                _rulerH.SetTransform(sc, orig, _canvas.CanvasLayout.SurfaceWidth);
                _rulerV.SetTransform(sc, orig, _canvas.CanvasLayout.SurfaceHeight);
            };

            topSplit.Panel1.Controls.Add(canvasTable);

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

            var snapSpriteItem = new ToolStripMenuItem("Snap to Sprite Edges\tCtrl+Shift+G") { CheckOnClick = true };
            snapSpriteItem.CheckedChanged += (s, e) =>
            {
                _canvas.SnapToSprite = snapSpriteItem.Checked;
                SetStatus(snapSpriteItem.Checked ? "Snap to sprite edges enabled" : "Snap to sprite edges disabled");
            };
            view.DropDownItems.Add(snapSpriteItem);

            var rulersItem = new ToolStripMenuItem("Show Rulers") { CheckOnClick = true, Checked = true };
            rulersItem.CheckedChanged += (s, e) =>
            {
                bool show = rulersItem.Checked;
                if (_rulerH != null) _rulerH.Visible = show;
                if (_rulerV != null) _rulerV.Visible = show;
                if (_rulerCorner != null) _rulerCorner.Visible = show;
            };
            view.DropDownItems.Add(rulersItem);

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

            var animation = new ToolStripMenuItem("Animation");
            animation.DropDownItems.Add("Multi-Sprite Timeline…", null, (s, e) => OpenMultiSpriteTimeline());
            ms.Items.Add(animation);

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
                "Layer order (bottom \u2192 top)\n" +
                "\u25CE = visible  |  \u29B8 = hidden  |  [REF] = reference layout  |  \u26A0 = game data  |  \u00B7 = untracked\n" +
                "Left-click left edge to toggle visibility\n" +
                "// variable = code variable name\n" +
                "Double-click to jump to code definition");
            _lstLayers.SelectedIndexChanged += OnLayerListSelectionChanged;
            _lstLayers.MouseDoubleClick  += OnLayerListDoubleClick;
            _lstLayers.MouseDown += (s, e) =>
            {
                int idx = _lstLayers.IndexFromPoint(e.Location);

                // Left-click on icon gutter toggles visibility for that row
                if (e.Button == MouseButtons.Left && idx >= 0 && e.X <= 22)
                {
                    var sprite = SpriteFromLayerIndex(idx);
                    if (sprite != null)
                    {
                        PushUndo();
                        sprite.IsHidden = !sprite.IsHidden;
                        if (sprite.IsHidden && _canvas.SelectedSprite == sprite)
                            _canvas.SelectedSprite = null;
                        _canvas.Invalidate();
                        RefreshLayerList();
                        SetStatus(sprite.IsHidden
                            ? $"Layer hidden: {sprite.DisplayName}"
                            : $"Layer shown: {sprite.DisplayName}");
                    }
                    return;
                }

                if (e.Button == MouseButtons.Right)
                {
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
                Minimum       = -100,
                Maximum       = 100,
                DecimalPlaces = 4,
                Increment     = 0.0001M,
                BackColor     = Color.FromArgb(30, 30, 30),
                ForeColor     = Color.White,
            };
            _numRotScale.ValueChanged += OnPropChanged;
            _numRotScale.KeyDown += SuppressEnterBeep;
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

    }
}
