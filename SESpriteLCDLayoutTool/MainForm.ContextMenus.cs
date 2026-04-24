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
        // ── Keyboard shortcuts ────────────────────────────────────────────────────
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Let text-editing keys pass through when a text control is focused
            if ((_codeBox != null && _codeBox.Focused) ||
                (_execCallBox != null && _execCallBox.Focused))
            {
                // Autocomplete popup intercepts Up/Down/Enter/Tab/Escape
                if (_autoComplete != null && _autoComplete.IsActive && _autoComplete.HandleKey(keyData))
                    return true;

                // Code-editor–specific keys (only when the code box itself is focused)
                if (_codeBox != null && _codeBox.Focused)
                {
                    Keys keyCode = keyData & Keys.KeyCode;
                    Keys modifiers = keyData & Keys.Modifiers;

                    // Find / Find+Replace in code editor
                    if (keyCode == Keys.F && modifiers == Keys.Control)
                    {
                        _findReplaceBar?.ShowFind();
                        return true;
                    }
                    if (keyCode == Keys.H && modifiers == Keys.Control)
                    {
                        _findReplaceBar?.ShowFindReplace();
                        return true;
                    }

                    // Kick the syntax highlight timer on undo/redo so colouring updates
                    if ((keyCode == Keys.Z || keyCode == Keys.Y) && modifiers == Keys.Control)
                    {
                        _lastHighlightedCode = null;
                        _syntaxTimer.Stop();
                        _syntaxTimer.Start();
                        // Let Scintilla handle the actual undo/redo
                        return base.ProcessCmdKey(ref msg, keyData);
                    }
                }

                switch (keyData)
                {
                    case Keys.Control | Keys.S:
                    case Keys.Control | Keys.N:
                    case Keys.Control | Keys.E:
                    case Keys.Control | Keys.T:
                        break; // fall through to global handler
                    default:
                        return base.ProcessCmdKey(ref msg, keyData);
                }
            }

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
                    PushUndo(); _canvas.NudgeSelected(0, -1); if (!_codeBoxDirty) RefreshCode();
                    return true;
                case Keys.Down:
                    PushUndo(); _canvas.NudgeSelected(0, 1); if (!_codeBoxDirty) RefreshCode();
                    return true;
                case Keys.Left:
                    PushUndo(); _canvas.NudgeSelected(-1, 0); if (!_codeBoxDirty) RefreshCode();
                    return true;
                case Keys.Right:
                    PushUndo(); _canvas.NudgeSelected(1, 0); if (!_codeBoxDirty) RefreshCode();
                    return true;
                case Keys.Shift | Keys.Up:
                    PushUndo(); _canvas.NudgeSelected(0, -10); if (!_codeBoxDirty) RefreshCode();
                    return true;
                case Keys.Shift | Keys.Down:
                    PushUndo(); _canvas.NudgeSelected(0, 10); if (!_codeBoxDirty) RefreshCode();
                    return true;
                case Keys.Shift | Keys.Left:
                    PushUndo(); _canvas.NudgeSelected(-10, 0); if (!_codeBoxDirty) RefreshCode();
                    return true;
                case Keys.Shift | Keys.Right:
                    PushUndo(); _canvas.NudgeSelected(10, 0); if (!_codeBoxDirty) RefreshCode();
                    return true;

                // Layer order
                case Keys.Control | Keys.OemCloseBrackets:
                    PushUndo(); _canvas.MoveSelectedUp(); RefreshLayerList(); if (!_codeBoxDirty) RefreshCode();
                    return true;
                case Keys.Control | Keys.OemOpenBrackets:
                    PushUndo(); _canvas.MoveSelectedDown(); RefreshLayerList(); if (!_codeBoxDirty) RefreshCode();
                    return true;

                // Snap-to-grid toggle
                case Keys.Control | Keys.G:
                    _canvas.SnapToGrid = !_canvas.SnapToGrid;
                    SetStatus(_canvas.SnapToGrid ? "Snap to grid enabled" : "Snap to grid disabled");
                    return true;

                // Select all
                case Keys.Control | Keys.A:
                    _canvas.SelectAll();
                    RefreshLayerList();
                    SetStatus($"Selected all {_canvas.SelectedSprites.Count} sprites");
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
                case Keys.Control | Keys.Shift | Keys.S:
                    ExportScript();
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

                // Template gallery
                case Keys.Control | Keys.T:
                    ShowTemplateGallery();
                    return true;

                // Pop out / dock code editor
                case Keys.Control | Keys.E:
                    ToggleCodePopout();
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
            ClearCodeDirty();
            RefreshCode(writeBack: true);
            RefreshDebugStats();
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
            var selected = GetSelectedSprites();
            if (selected.Count == 0 && _canvas.SelectedSprite != null)
                selected.Add(_canvas.SelectedSprite);
            if (selected.Count == 0) { SetStatus("Nothing selected to duplicate."); return; }

            PushUndo();
            SpriteEntry lastDup = null;
            foreach (var src in selected)
            {
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
                lastDup = dup;
            }
            if (lastDup != null) _canvas.SelectedSprite = lastDup;
            RefreshLayerList();
            RefreshCode();
            SetStatus(selected.Count == 1 ? "Duplicated sprite" : $"Duplicated {selected.Count} sprites");
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
            ClearCodeDirty();
            RefreshCode();
            SetStatus("Stretched to surface");
        }

        private void AlignSelected(AlignMode mode)
        {
            var sel = GetSelectedSprites();
            if (sel.Count < 2) { SetStatus("Select 2 or more sprites to align."); return; }
            PushUndo();
            _canvas.AlignSelection(mode);
            ClearCodeDirty();
            RefreshCode();
            string label = mode.ToString();
            SetStatus($"Aligned {sel.Count} sprites: {label}");
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

            // ── Align submenu ──
            var alignMenu = new ToolStripMenuItem("Align / Distribute")
            {
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.FromArgb(220, 220, 220),
            };
            alignMenu.DropDown.BackColor = Color.FromArgb(45, 45, 48);
            alignMenu.DropDown.ForeColor = Color.FromArgb(220, 220, 220);
            if (alignMenu.DropDown is ToolStripDropDownMenu alignDd) alignDd.Renderer = new DarkMenuRenderer();
            alignMenu.DropDownItems.Add("Align Left Edges",      null, (s, e) => AlignSelected(AlignMode.Left));
            alignMenu.DropDownItems.Add("Align Right Edges",     null, (s, e) => AlignSelected(AlignMode.Right));
            alignMenu.DropDownItems.Add("Align Top Edges",       null, (s, e) => AlignSelected(AlignMode.Top));
            alignMenu.DropDownItems.Add("Align Bottom Edges",    null, (s, e) => AlignSelected(AlignMode.Bottom));
            alignMenu.DropDownItems.Add(new ToolStripSeparator());
            alignMenu.DropDownItems.Add("Center Horizontally",   null, (s, e) => AlignSelected(AlignMode.CenterH));
            alignMenu.DropDownItems.Add("Center Vertically",     null, (s, e) => AlignSelected(AlignMode.CenterV));
            alignMenu.DropDownItems.Add(new ToolStripSeparator());
            alignMenu.DropDownItems.Add("Space Evenly (H)",      null, (s, e) => AlignSelected(AlignMode.SpaceH));
            alignMenu.DropDownItems.Add("Space Evenly (V)",      null, (s, e) => AlignSelected(AlignMode.SpaceV));
            ctx.Items.Add(alignMenu);

            ctx.Items.Add(new ToolStripSeparator());

            // Hide Selected (works with Shift+click multi-select)
            var hideItem = ctx.Items.Add("Hide Selected",     null, (s, e) => ToggleSelectedLayerVisibility(true));
            ctx.Items.Add("Show All Layers",  null, (s, e) => ShowAllLayers());
            ctx.Items.Add(new ToolStripSeparator());

            var lockItem = ctx.Items.Add("Lock Selected", null, (s, e) => ToggleSelectedLayerLock(true));
            var unlockItem = ctx.Items.Add("Unlock Selected", null, (s, e) => ToggleSelectedLayerLock(false));
            ctx.Items.Add(new ToolStripSeparator());



            // ── Add Animation submenu ──
            var animMenu = new ToolStripMenuItem("Add Animation…")
            {
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.FromArgb(220, 220, 220),
            };
            animMenu.DropDown.BackColor = Color.FromArgb(45, 45, 48);
            animMenu.DropDown.ForeColor = Color.FromArgb(220, 220, 220);
            if (animMenu.DropDown is ToolStripDropDownMenu dd) dd.Renderer = new DarkMenuRenderer();
            animMenu.DropDownItems.Add("Rotate…",           null, (s, e) => ShowAnimationSnippetDialog(AnimationType.Rotate));
            animMenu.DropDownItems.Add("Oscillate…",        null, (s, e) => ShowAnimationSnippetDialog(AnimationType.Oscillate));
            animMenu.DropDownItems.Add("Pulse (Scale)…",    null, (s, e) => ShowAnimationSnippetDialog(AnimationType.Pulse));
            animMenu.DropDownItems.Add("Fade…",             null, (s, e) => ShowAnimationSnippetDialog(AnimationType.Fade));
            animMenu.DropDownItems.Add("Blink…",            null, (s, e) => ShowAnimationSnippetDialog(AnimationType.Blink));
            animMenu.DropDownItems.Add("Color Cycle…",      null, (s, e) => ShowAnimationSnippetDialog(AnimationType.ColorCycle));
            animMenu.DropDownItems.Add(new ToolStripSeparator());
            animMenu.DropDownItems.Add("Keyframe Animation…", null, (s, e) => ShowKeyframeAnimationDialog());
            ctx.Items.Add(animMenu);

            var editAnimItem = ctx.Items.Add("Edit Animation…", null, (s, e) => ShowKeyframeAnimationDialog(editExisting: true));
            var copyAnimItem  = ctx.Items.Add("Copy Animation",  null, (s, e) => CopySelectedAnimation());
            var pasteAnimItem = ctx.Items.Add("Paste Animation", null, (s, e) => PasteAnimationToSelected());
            var createGroupItem = ctx.Items.Add("Create Animation Group", null, (s, e) => CreateAnimationGroup());
            var joinGroupMenu = new ToolStripMenuItem("Join Animation Group")
            {
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.FromArgb(220, 220, 220),
            };
            joinGroupMenu.DropDown.BackColor = Color.FromArgb(45, 45, 48);
            joinGroupMenu.DropDown.ForeColor = Color.FromArgb(220, 220, 220);
            if (joinGroupMenu.DropDown is ToolStripDropDownMenu jdd) jdd.Renderer = new DarkMenuRenderer();
            ctx.Items.Add(joinGroupMenu);
            var leaveGroupItem = ctx.Items.Add("Leave Animation Group", null, (s, e) => LeaveAnimationGroup());

            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add("Layer Up\tCtrl+]",         null, (s, e) => { PushUndo(); _canvas.MoveSelectedUp();   RefreshLayerList(); if (!_codeBoxDirty) RefreshCode(); });
            ctx.Items.Add("Layer Down\tCtrl+[",       null, (s, e) => { PushUndo(); _canvas.MoveSelectedDown(); RefreshLayerList(); if (!_codeBoxDirty) RefreshCode(); });
            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add("Reset View\tCtrl+0",       null, (s, e) => _canvas.ResetView());

            ctx.Opening += (s, e) =>
            {
                var sel = _canvas.SelectedSprite;
                animMenu.Enabled = sel != null;
                // Detect animation index from code if not yet known
                if (sel != null && sel.AnimationIndex == 0 && !string.IsNullOrEmpty(_codeBox?.Text))
                {
                    int detected = DetectSpriteAnimationIndex(_codeBox.Text, sel);
                    if (detected > 0) sel.AnimationIndex = detected;
                }
                // Show "Edit Animation" if sprite has in-memory data OR code panel has keyframe arrays
                bool hasAnim = sel?.KeyframeAnimation != null
                            || AnimationSnippetGenerator.TryParseKeyframed(_codeBox?.Text, sel?.AnimationIndex ?? 0) != null;
                // Also check if sprite is in a group (follower can edit via leader)
                bool inGroup = !string.IsNullOrEmpty(sel?.AnimationGroupId);
                bool isLeader = inGroup && sel?.KeyframeAnimation != null;
                editAnimItem.Visible  = sel != null && (hasAnim || inGroup);
                copyAnimItem.Visible  = sel != null && (hasAnim || inGroup);
                pasteAnimItem.Visible = sel != null && _copiedAnimation != null;

                // Group items
                createGroupItem.Visible = sel != null && hasAnim && !inGroup;
                leaveGroupItem.Visible  = sel != null && inGroup;

                // Build "Join" submenu dynamically
                joinGroupMenu.DropDownItems.Clear();
                if (sel != null && !inGroup && _layout != null)
                {
                    var groups = _layout.Sprites
                        .Where(sp => !string.IsNullOrEmpty(sp.AnimationGroupId) && sp.KeyframeAnimation != null)
                        .GroupBy(sp => sp.AnimationGroupId)
                        .ToList();
                    foreach (var g in groups)
                    {
                        var leader = g.First();
                        int count = GetGroupMembers(g.Key).Count;
                        joinGroupMenu.DropDownItems.Add(
                            $"{leader.DisplayName} ({count} sprites)",
                            null,
                            (s2, e2) => JoinAnimationGroup(g.Key));
                    }
                }
                joinGroupMenu.Visible = sel != null && !inGroup && joinGroupMenu.DropDownItems.Count > 0;

                // Update "Hide Selected" label based on count
                var selected = GetSelectedSprites();
                hideItem.Text = selected.Count > 1 ? $"Hide Selected ({selected.Count})" : "Hide Selected";
                hideItem.Enabled = selected.Count > 0;

                // Lock/Unlock items
                bool locked = _canvas.SelectedSprite?.IsLocked ?? false;
                lockItem.Text    = selected.Count > 1 ? $"Lock Selected ({selected.Count})" : "Lock Selected";
                unlockItem.Text  = selected.Count > 1 ? $"Unlock Selected ({selected.Count})" : "Unlock Selected";
                lockItem.Visible = !locked || selected.Count > 1;
                unlockItem.Visible = locked || selected.Count > 1;

                // Align menu: only useful with 2+ sprites selected
                alignMenu.Enabled = selected.Count >= 2;
                alignMenu.Text = selected.Count >= 2 ? $"Align / Distribute ({selected.Count})" : "Align / Distribute";
            };

            return ctx;
        }

        // ── Layer list context menu ──────────────────────────────────────────────
        private ContextMenuStrip BuildLayerContextMenu()
        {
            var ctx = new ContextMenuStrip { BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(220, 220, 220) };
            ctx.Renderer = new DarkMenuRenderer();

            var moveUp   = ctx.Items.Add("Move Up",    null, (s, e) => { PushUndo(); _canvas.MoveSelectedUp();   RefreshLayerList(); if (!_codeBoxDirty) RefreshCode(); });
            var moveDown = ctx.Items.Add("Move Down",  null, (s, e) => { PushUndo(); _canvas.MoveSelectedDown(); RefreshLayerList(); if (!_codeBoxDirty) RefreshCode(); });
            ctx.Items.Add(new ToolStripSeparator());
            var dupItem  = ctx.Items.Add("Duplicate",  null, (s, e) => DuplicateSelected());
            var delItem  = ctx.Items.Add("Delete",     null, (s, e) => DeleteSelected());
            ctx.Items.Add(new ToolStripSeparator());
            var hideItem      = ctx.Items.Add("Hide Layer",         null, (s, e) => ToggleSelectedLayerVisibility(true));
            var showItem      = ctx.Items.Add("Show Layer",         null, (s, e) => ToggleSelectedLayerVisibility(false));
            var hideAboveItem = ctx.Items.Add("Hide Layers Above",  null, (s, e) => HideLayersAbove());
            var showAllItem   = ctx.Items.Add("Show All Layers",    null, (s, e) => ShowAllLayers());
            ctx.Items.Add(new ToolStripSeparator());
            var lockItem      = ctx.Items.Add("Lock Layer",         null, (s, e) => ToggleSelectedLayerLock(true));
            var unlockItem    = ctx.Items.Add("Unlock Layer",       null, (s, e) => ToggleSelectedLayerLock(false));
            ctx.Items.Add(new ToolStripSeparator());
            var editAnimItem  = ctx.Items.Add("Edit Animation…",    null, (s, e) => ShowKeyframeAnimationDialog(editExisting: true));
            var copyAnimItem  = ctx.Items.Add("Copy Animation",      null, (s, e) => CopySelectedAnimation());
            var pasteAnimItem = ctx.Items.Add("Paste Animation",     null, (s, e) => PasteAnimationToSelected());
            var createGroupItem2 = ctx.Items.Add("Create Animation Group", null, (s, e) => CreateAnimationGroup());
            var joinGroupMenu2 = new ToolStripMenuItem("Join Animation Group")
            {
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.FromArgb(220, 220, 220),
            };
            joinGroupMenu2.DropDown.BackColor = Color.FromArgb(45, 45, 48);
            joinGroupMenu2.DropDown.ForeColor = Color.FromArgb(220, 220, 220);
            if (joinGroupMenu2.DropDown is ToolStripDropDownMenu jdd2) jdd2.Renderer = new DarkMenuRenderer();
            ctx.Items.Add(joinGroupMenu2);
            var leaveGroupItem2 = ctx.Items.Add("Leave Animation Group", null, (s, e) => LeaveAnimationGroup());

            ctx.Opening += (s, e) =>
            {
                if (_layout == null || _canvas.SelectedSprite == null)
                {
                    e.Cancel = true;
                    return;
                }

                int selCount = _lstLayers.SelectedIndices.Count;
                bool multi = selCount > 1;

                // Move up/down only for single selection
                int idx = _layout.Sprites.IndexOf(_canvas.SelectedSprite);
                moveUp.Enabled   = !multi && idx < _layout.Sprites.Count - 1;
                moveDown.Enabled = !multi && idx > 0;

                // Update labels for multi-select
                dupItem.Text = multi ? $"Duplicate ({selCount} selected)" : "Duplicate";
                delItem.Text = multi ? $"Delete ({selCount} selected)"    : "Delete";

                bool hidden = _canvas.SelectedSprite.IsHidden;
                hideItem.Text    = multi ? $"Hide Layers ({selCount} selected)" : "Hide Layer";
                showItem.Text    = multi ? $"Show Layers ({selCount} selected)" : "Show Layer";
                hideItem.Visible = !hidden || multi;
                showItem.Visible = hidden  || multi;

                // Lock/Unlock items
                bool locked = _canvas.SelectedSprite.IsLocked;
                lockItem.Text    = multi ? $"Lock Layers ({selCount} selected)" : "Lock Layer";
                unlockItem.Text  = multi ? $"Unlock Layers ({selCount} selected)" : "Unlock Layer";
                lockItem.Visible = !locked || multi;
                unlockItem.Visible = locked || multi;

                // Hide Layers Above: only enabled when there are visible layers above
                bool hasVisibleAbove = false;
                for (int i = idx + 1; i < _layout.Sprites.Count; i++)
                    if (!_layout.Sprites[i].IsHidden) { hasVisibleAbove = true; break; }
                hideAboveItem.Enabled = hasVisibleAbove;

                bool anyHidden = false;
                foreach (var sp in _layout.Sprites)
                    if (sp.IsHidden) { anyHidden = true; break; }
                showAllItem.Enabled = anyHidden;

                // Edit Animation: visible if sprite has in-memory data OR code has keyframe arrays
                var selSprite = _canvas.SelectedSprite;
                // Detect animation index from code if not yet known
                if (selSprite != null && selSprite.AnimationIndex == 0 && !string.IsNullOrEmpty(_codeBox?.Text))
                {
                    int detected = DetectSpriteAnimationIndex(_codeBox.Text, selSprite);
                    if (detected > 0) selSprite.AnimationIndex = detected;
                }
                bool hasAnim = selSprite.KeyframeAnimation != null
                            || AnimationSnippetGenerator.TryParseKeyframed(_codeBox?.Text, selSprite.AnimationIndex) != null;
                bool inGroup = !string.IsNullOrEmpty(selSprite.AnimationGroupId);
                editAnimItem.Visible  = !multi && (hasAnim || inGroup);
                copyAnimItem.Visible  = !multi && (hasAnim || inGroup);
                pasteAnimItem.Visible = !multi && _copiedAnimation != null;

                // Group items
                createGroupItem2.Visible = !multi && hasAnim && !inGroup;
                leaveGroupItem2.Visible  = !multi && inGroup;

                // Build "Join" submenu dynamically
                joinGroupMenu2.DropDownItems.Clear();
                if (!multi && !inGroup && _layout != null)
                {
                    var groups = _layout.Sprites
                        .Where(sp => !string.IsNullOrEmpty(sp.AnimationGroupId) && sp.KeyframeAnimation != null)
                        .GroupBy(sp => sp.AnimationGroupId)
                        .ToList();
                    foreach (var g in groups)
                    {
                        var ldr = g.First();
                        int count = GetGroupMembers(g.Key).Count;
                        joinGroupMenu2.DropDownItems.Add(
                            $"{ldr.DisplayName} ({count} sprites)",
                            null,
                            (s2, e2) => JoinAnimationGroup(g.Key));
                    }
                }
                joinGroupMenu2.Visible = !multi && !inGroup && joinGroupMenu2.DropDownItems.Count > 0;
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
                UpdateImportLabel(sprite, SpriteTypeHint(sprite));
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
            this.SuspendLayout();
            // 
            // MainForm
            // 
            this.ClientSize = new System.Drawing.Size(583, 528);
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
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
            num1.KeyDown += SuppressEnterBeep;
            num2.KeyDown += SuppressEnterBeep;

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

        private static void SuppressEnterBeep(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private static float ClampF(float v, float min, float max) => v < min ? min : v > max ? max : v;

    }
}
