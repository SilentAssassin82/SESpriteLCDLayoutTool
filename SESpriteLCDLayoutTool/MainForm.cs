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
        private LcdCanvas    _canvas;
        private CanvasRuler  _rulerH;   // horizontal ruler along top of canvas
        private CanvasRuler  _rulerV;   // vertical ruler along left of canvas
        private Panel        _rulerCorner; // 20×20 dead corner where rulers meet

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
        private TextBox       _txtLabel;
        private Label         _lblRuntimeData;
        private ListBox       _lstLayers;
        private List<SpriteEntry> _layerListSprites = new List<SpriteEntry>();

        // ── Code panel ────────────────────────────────────────────────────────────
        private Controls.ScintillaCodeBox _codeBox;
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
        private bool        _syncingDiffScroll;

        // ── Code heatmap / profiling ──────────────────────────────────────────
        private Dictionary<string, double> _lastHeatmapTimings;
        private bool _heatmapEnabled = true;
        private DateTime _lastHeatmapPaintTime;

        // ── Syntax highlighting ───────────────────────────────────────────────
        private System.Windows.Forms.Timer _syntaxTimer;
        private string _lastHighlightedCode;
        private bool _highlightInProgress;
        private int _semanticHighlightGeneration;
        private enum CodeDiagnosticsMode { None, Live, Compile }
        private CodeDiagnosticsMode _codeDiagnosticsMode = CodeDiagnosticsMode.None;
        private string _compileDiagnosticsCodeSnapshot;
        private const int SyntaxHighlightDebounceMs = 500;
        private ToolTip _codeDiagTooltip;
        private Controls.DiagnosticOverlay _diagnosticOverlay;
        private string _lastCodeDiagTooltipText;
        private int _lastCodeDiagTooltipChar = -1;
        private ToolTip _layerListTooltip;
        private System.Windows.Forms.Timer _layerListTooltipTimer;
        private string _lastLayerTooltipText;
        private int _lastLayerTooltipIndex = -1;
        private Point _lastLayerTooltipLocation;
        private const int LiveHighlightMaxChars = 120000;
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
        private DateTime _lastVariablesUpdateTime;
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
        private readonly Services.CodeUndoManager _codeUndo = new Services.CodeUndoManager();
        private bool  _executingCode;  // true while OnExecCodeClick is running — suppresses RefreshCode from OnSelectionChanged
        private Label _lblCodeTitle;
        private Label _lblCodeMode;
        private Button _btnApplyCode;
        private CodeAutoComplete _autoComplete;
        private Controls.CodeFindReplaceBar _findReplaceBar;

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

            // Throttle: at most once per 300 ms during playback to avoid killing the UI thread.
            var now = DateTime.UtcNow;
            bool throttled = (now - _lastHeatmapPaintTime).TotalMilliseconds < 300;

            // Avoid redundant re-paints when timings haven't changed meaningfully.
            // Use a coarser threshold (5 % relative OR 0.5 ms absolute) because
            // Pulsar/Torch timing numbers fluctuate slightly every frame; the old
            // 0.01 ms threshold caused a full repaint on almost every tick.
            if (_lastHeatmapTimings != null && _lastHeatmapTimings.Count == timings.Count)
            {
                bool same = true;
                foreach (var kv in timings)
                {
                    if (!_lastHeatmapTimings.TryGetValue(kv.Key, out double prev))
                    { same = false; break; }
                    double delta = Math.Abs(prev - kv.Value);
                    if (delta > Math.Max(0.5, prev * 0.05))
                    { same = false; break; }
                }
                if (same || throttled) return;
            }
            else if (throttled)
            {
                return;
            }

            _lastHeatmapPaintTime = now;
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

            // Suppress events and batch visual updates to avoid flash
            _suppressCodeBoxEvents = true;
            try
            {
                _codeBox.ClearBackColors();

                // Compute timing range for relative scaling
                double minMs = double.MaxValue, maxMs = double.MinValue;
                foreach (var kv in methodRanges)
                {
                    if (!timings.TryGetValue(kv.Key, out double v)) continue;
                    if (v < minMs) minMs = v;
                    if (v > maxMs) maxMs = v;
                }
                double range = maxMs - minMs;

                foreach (var kv in methodRanges)
                {
                    if (!timings.TryGetValue(kv.Key, out double ms)) continue;
                    // Normalise to 0–1 within the observed range
                    double t = range > 1e-9 ? (ms - minMs) / range : 0.0;
                    var color = HeatmapColor(t);
                    _codeBox.SetRangeBackColor(kv.Value.start, kv.Value.end - kv.Value.start, color);
                }
            }
            finally
            {
                _suppressCodeBoxEvents = false;
            }
        }

        /// <summary>Clears heatmap highlighting from the code editor.</summary>
        private void ClearCodeHeatmap()
        {
            if (_codeBox == null || _lastHeatmapTimings == null) return;
            _lastHeatmapTimings = null;
            _codeBox.ClearBackColors();
        }

        /// <summary>
        /// Maps a normalised heat value (0 = fastest, 1 = slowest) to a heatmap color.
        /// Green → yellow → orange → red.
        /// </summary>
        private static Color HeatmapColor(double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            int r, g, b;
            if (t < 0.5)
            {
                // Green → Yellow
                double f = t * 2.0;
                r = (int)(40 + 100 * f);   // 40 → 140
                g = (int)(120 + 20 * f);   // 120 → 140
                b = 40;
            }
            else
            {
                // Yellow → Red
                double f = (t - 0.5) * 2.0;
                r = (int)(140 + 80 * f);   // 140 → 220
                g = (int)(140 - 90 * f);   // 140 → 50
                b = 40;
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

        private void SyncDiffScroll(RichTextBox source, RichTextBox target)
        {
            if (_syncingDiffScroll || source == null || target == null) return;

            try
            {
                _syncingDiffScroll = true;
                var pos = GetScrollPos(source);
                SetScrollPos(target, pos);
            }
            finally
            {
                _syncingDiffScroll = false;
            }
        }

        private void EnsureDiffSplitCentered(SplitContainer diffSplit)
        {
            if (diffSplit == null || diffSplit.Width <= 0) return;

            int minPane = 140;
            int available = diffSplit.ClientSize.Width - diffSplit.SplitterWidth;
            if (available <= 0) return;

            int centered = available / 2;
            int min = minPane;
            int max = Math.Max(minPane, available - minPane);
            diffSplit.SplitterDistance = Math.Max(min, Math.Min(max, centered));
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

            string priorSource = _layout?.OriginalSourceCode ?? _codeBox?.Text;

            PushUndo();

            bool sourcePatched = false;
            string patchedSource;
            if (TryRemoveSelectedSpritesFromSource(selected, out patchedSource))
            {
                _layout.OriginalSourceCode = patchedSource;
                SetCodeText(patchedSource);
                _codeBoxDirty = true;
                _lblCodeTitle.Text = "✏ Code (edited)";
                _lblCodeTitle.ForeColor = Color.FromArgb(255, 200, 80);
                WriteBackToWatchedFile(patchedSource);
                sourcePatched = true;
            }
            else
            {
                InvalidateOriginalSourceIfSet();
            }

            foreach (var sp in selected)
                _layout.Sprites.Remove(sp);
            _canvas.SelectedSprite = null;
            RefreshLayerList();

            if (sourcePatched)
            {
                CleanupInjectedAnimationAfterDeletion();
                RefreshSourceTracking();
            }
            else
            {
                ClearCodeDirty();
                RefreshCode();
            }

            string finalSource = _layout?.OriginalSourceCode ?? _codeBox?.Text;
            if (!string.IsNullOrEmpty(priorSource) &&
                !string.IsNullOrEmpty(finalSource) &&
                !string.Equals(priorSource, finalSource, StringComparison.Ordinal))
            {
                ShowPatchDiff(priorSource, finalSource);
            }

            SetStatus(selected.Count == 1 ? "Deleted sprite" : $"Deleted {selected.Count} sprites");
        }

        /// <summary>
        /// Removes selected tracked sprite blocks directly from OriginalSourceCode.
        /// Returns false when any selected sprite lacks reliable source offsets.
        /// </summary>
        private bool TryRemoveSelectedSpritesFromSource(List<SpriteEntry> selected, out string patched)
        {
            patched = _layout?.OriginalSourceCode;
            if (string.IsNullOrEmpty(patched) || selected == null || selected.Count == 0)
                return false;

            var ranges = new List<Tuple<int, int>>();
            foreach (var sp in selected)
            {
                if (sp == null || sp.SourceStart < 0 || sp.SourceEnd <= sp.SourceStart || sp.SourceEnd > patched.Length)
                    return false;

                int start = sp.SourceStart;
                int end = sp.SourceEnd;

                // If SourceStart points into the argument of a call (e.g. "new MySprite{…}" inside
                // "frame.Add(new MySprite{…})"), expand backwards to include the "frame.Add(" prefix
                // plus its leading indentation so we don't leave an orphan "frame.Add();" behind.
                {
                    int scan = start - 1;
                    while (scan >= 0 && (patched[scan] == ' ' || patched[scan] == '\t'))
                        scan--;
                    if (scan >= 0 && patched[scan] == '(')
                    {
                        // Include the whole line (indentation + call up to and including '(')
                        int lineBegin = scan;
                        while (lineBegin > 0 && patched[lineBegin - 1] != '\n')
                            lineBegin--;
                        start = lineBegin;
                    }
                }

                // Expand end forwards over ");"-suffix left by the object-initializer pattern,
                // then consume ';' and any trailing whitespace on the same line.
                {
                    int scan = end;
                    while (scan < patched.Length && (patched[scan] == ' ' || patched[scan] == '\t'))
                        scan++;
                    if (scan < patched.Length && patched[scan] == ')')
                    {
                        scan++; // consume ')'
                        while (scan < patched.Length && (patched[scan] == ' ' || patched[scan] == '\t'))
                            scan++;
                        if (scan < patched.Length && patched[scan] == ';')
                            scan++; // consume ';'
                        end = scan;
                    }
                }

                // Remove trailing newline(s) so we don't leave ragged blank lines.
                while (end < patched.Length && (patched[end] == '\r' || patched[end] == '\n'))
                    end++;

                ranges.Add(Tuple.Create(start, end));
            }

            // Apply removals from back to front to keep offsets valid.
            ranges.Sort((a, b) => b.Item1.CompareTo(a.Item1));
            foreach (var r in ranges)
            {
                if (r.Item1 < 0 || r.Item2 > patched.Length || r.Item2 <= r.Item1)
                    return false;
                patched = patched.Substring(0, r.Item1) + patched.Substring(r.Item2);
            }

            return true;
        }

        /// <summary>
        /// Rebuilds Roslyn-injected animation regions from the remaining sprites after deletion.
        /// This removes orphan injected vars/compute blocks while preserving shared ones.
        /// </summary>
        private void CleanupInjectedAnimationAfterDeletion()
        {
            if (_layout?.OriginalSourceCode == null)
            {
                System.Diagnostics.Debug.WriteLine("[DeleteCleanup] Skipped: OriginalSourceCode is null.");
                return;
            }

            string existing = _layout.OriginalSourceCode;
            var allSprites = _layout.Sprites?.ToList() ?? new List<SpriteEntry>();
            bool hasLegacyKeyframeBlock = existing.IndexOf(AnimationSnippetGenerator.FooterMarker, StringComparison.Ordinal) >= 0;

            IEnumerable<SpriteEntry> injectionSprites = allSprites;
            if (hasLegacyKeyframeBlock)
            {
                // Keep full sprite order for duplicate-name targeting; strip only keyframe/group
                // effects so legacy keyframe snippets aren't duplicated by Roslyn reinjection.
                var filtered = new List<SpriteEntry>();
                foreach (var sp in allSprites)
                {
                    var fx = (sp.AnimationEffects ?? new List<IAnimationEffect>())
                        .Where(a => !(a is KeyframeEffect) && !(a is GroupFollowerEffect))
                        .ToList();

                    filtered.Add(new SpriteEntry
                    {
                        Type = sp.Type,
                        SpriteName = sp.SpriteName,
                        Text = sp.Text,
                        X = sp.X,
                        Y = sp.Y,
                        Width = sp.Width,
                        Height = sp.Height,
                        ColorR = sp.ColorR,
                        ColorG = sp.ColorG,
                        ColorB = sp.ColorB,
                        ColorA = sp.ColorA,
                        Rotation = sp.Rotation,
                        SourceLineNumber = sp.SourceLineNumber,
                        SourceStart = sp.SourceStart,
                        SourceEnd = sp.SourceEnd,
                        AnimationEffects = fx,
                    });
                }

                injectionSprites = filtered;
            }

            var inj = RoslynAnimationInjector.InjectAnimations(existing, injectionSprites);
            if (!inj.Success)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DeleteCleanup] Skipped: InjectAnimations failed. Error='{inj.Error ?? "(none)"}'.");
                return;
            }
            if (inj.Code == existing)
            {
                System.Diagnostics.Debug.WriteLine("[DeleteCleanup] No changes: reinjection produced identical code.");
                return;
            }

            _layout.OriginalSourceCode = inj.Code;
            SetCodeText(inj.Code);
            _codeBoxDirty = true;
            _lblCodeTitle.Text = "✏ Code (edited)";
            _lblCodeTitle.ForeColor = Color.FromArgb(255, 200, 80);
            WriteBackToWatchedFile(inj.Code);

            System.Diagnostics.Debug.WriteLine(
                $"[DeleteCleanup] Applied: rewritten code after deletion. AnimatedSprites={inj.SpritesAnimated}.");
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
                    _txtLabel.Text = sp.UserLabel ?? "";
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

            sp.UserLabel = string.IsNullOrWhiteSpace(_txtLabel.Text) ? null : _txtLabel.Text.Trim();
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

            HideLayerListTooltip();

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
            HideLayerListTooltip();
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

        private void OnLayerListMouseMove(object sender, MouseEventArgs e)
        {
            if (_lstLayers == null || _layerListTooltip == null)
                return;

            int idx = _lstLayers.IndexFromPoint(e.Location);
            if (idx < 0)
            {
                HideLayerListTooltip();
                return;
            }

            Rectangle itemBounds;
            try
            {
                itemBounds = _lstLayers.GetItemRectangle(idx);
            }
            catch
            {
                HideLayerListTooltip();
                return;
            }

            if (!itemBounds.Contains(e.Location))
            {
                HideLayerListTooltip();
                return;
            }

            string tip = BuildLayerTooltip(SpriteFromLayerIndex(idx));
            if (string.IsNullOrWhiteSpace(tip))
            {
                HideLayerListTooltip();
                return;
            }

            if (idx == _lastLayerTooltipIndex &&
                string.Equals(tip, _lastLayerTooltipText, StringComparison.Ordinal))
                return;

            _lastLayerTooltipIndex = idx;
            _lastLayerTooltipText = tip;
            _lastLayerTooltipLocation = e.Location;

            if (_layerListTooltipTimer != null)
            {
                _layerListTooltipTimer.Stop();
                _layerListTooltipTimer.Start();
            }
        }

        private void OnLayerListTooltipTimerTick(object sender, EventArgs e)
        {
            if (_layerListTooltipTimer != null)
                _layerListTooltipTimer.Stop();

            if (_lstLayers == null || _layerListTooltip == null || string.IsNullOrWhiteSpace(_lastLayerTooltipText))
                return;

            if (_lastLayerTooltipIndex < 0 || _lastLayerTooltipIndex >= _lstLayers.Items.Count)
                return;

            _layerListTooltip.SetToolTip(_lstLayers, _lastLayerTooltipText);
            _layerListTooltip.Show(_lastLayerTooltipText, _lstLayers, _lastLayerTooltipLocation.X + 18, _lastLayerTooltipLocation.Y + 18, 7000);
        }

        private void HideLayerListTooltip()
        {
            if (_layerListTooltip == null || _lstLayers == null)
                return;

            _layerListTooltipTimer?.Stop();

            _layerListTooltip.Hide(_lstLayers);
            _layerListTooltip.SetToolTip(_lstLayers, string.Empty);
            _lastLayerTooltipText = null;
            _lastLayerTooltipIndex = -1;
            _lastLayerTooltipLocation = Point.Empty;
        }

        private string BuildLayerTooltip(SpriteEntry sprite)
        {
            if (sprite == null)
                return null;

            var lines = new List<string>();
            lines.Add(sprite.DisplayName);

            var badges = new List<string>();
            if (sprite.IsHidden) badges.Add("Hidden");
            if (sprite.IsLocked) badges.Add("Locked");
            if (sprite.IsReferenceLayout) badges.Add("Reference");
            if (sprite.IsSnapshotData) badges.Add("Game data");
            if (sprite.SourceStart < 0) badges.Add("Untracked");
            if (sprite.KeyframeAnimation != null) badges.Add($"Animated ({sprite.KeyframeAnimation.Keyframes.Count} keyframes)");
            if (!string.IsNullOrEmpty(sprite.AnimationGroupId)) badges.Add($"Group: {sprite.AnimationGroupId}");
            if (badges.Count > 0)
                lines.Add(string.Join(" | ", badges));

            string source = CodeNavigationService.GetSourceLocationDescription(sprite);
            if (!string.IsNullOrWhiteSpace(source))
                lines.Add(source);

            if (!string.IsNullOrWhiteSpace(sprite.VariableName))
                lines.Add("Variable: " + sprite.VariableName);

            if (sprite.Type == SpriteEntryType.Text)
            {
                if (!string.IsNullOrWhiteSpace(sprite.Text))
                    lines.Add("Text: " + TrimTooltipText(sprite.Text, 120));
            }
            else if (!string.IsNullOrWhiteSpace(sprite.SpriteName))
            {
                lines.Add("Sprite: " + sprite.SpriteName);
            }

            if (!string.IsNullOrWhiteSpace(sprite.RuntimeDataNote))
                lines.Add("Data: " + TrimTooltipText(sprite.RuntimeDataNote, 120));

            if (!string.IsNullOrWhiteSpace(sprite.SourceCodeSnippet))
                lines.Add("Code: " + TrimTooltipText(sprite.SourceCodeSnippet, 180));

            return string.Join(Environment.NewLine, lines.Where(l => !string.IsNullOrWhiteSpace(l)));
        }

        private static string TrimTooltipText(string text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            string normalized = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (normalized.Length <= maxLength)
                return normalized;

            return normalized.Substring(0, Math.Max(0, maxLength - 3)).TrimEnd() + "...";
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
                HideLayerListTooltip();

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

                    string prefix = s.IsHidden ? "\u29B8 " : "\u25CE ";

                    if (s.IsReferenceLayout)
                        prefix += "[REF] ";
                    else if (s.IsSnapshotData)
                        prefix += "\u26A0 ";
                    else if (s.SourceStart < 0)
                        prefix += "\u00B7 ";

                    // Lock indicator
                    if (s.IsLocked)
                        prefix += "🔒 ";

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

            // CRITICAL FIX: Respect user's manual code edits unless force=true.
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

                    // Re-establish source tracking: SourceStart/SourceEnd/ImportBaseline must reflect the
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

            // ── Strategy 1: Type+content matching ──
            // Text and texture sprites are matched by their exact content
            var contentPool = new Dictionary<string, Queue<SpriteEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var ps in parsedSprites)
            {
                string key = (ps.Type == SpriteEntryType.Text ? "TEXT|" : "TEXTURE|") + (ps.SpriteName ?? ps.Text ?? "");
                if (!contentPool.ContainsKey(key))
                    contentPool[key] = new Queue<SpriteEntry>();
                contentPool[key].Enqueue(ps);
            }

            int unmatched = 0;
            foreach (var sp in _layout.Sprites)
            {
                string key = (sp.Type == SpriteEntryType.Text ? "TEXT|" : "TEXTURE|") + (sp.SpriteName ?? sp.Text ?? "");
                if (contentPool.TryGetValue(key, out var queue) && queue.Count > 0)
                {
                    var parsed = queue.Dequeue();
                    sp.SourceStart = parsed.SourceStart;
                    sp.SourceEnd = parsed.SourceEnd;
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
            int savedSel = _codeBox.SelectionStart;
            int savedLen = _codeBox.SelectionLength;
            int savedFirstLine = _codeBox.FirstVisibleLine;

            string normNew  = text.Replace("\r\n", "\n").Replace("\r", "\n");
            string normCur  = _codeBox.Text.Replace("\r\n", "\n").Replace("\r", "\n");
            if (normCur != normNew)
            {
                _codeBox.Text = text;
                _lastHighlightedCode = null;
                if (_codeBox.TextLength <= InitialHighlightMaxChars)
                {
                    _suppressCodeBoxEvents = true;
                    try
                    {
                        SyntaxHighlighter.Highlight(_codeBox);
                    }
                    finally
                    {
                        _suppressCodeBoxEvents = false;
                    }
                    _lastHighlightedCode = _codeBox.Text;
                    _diagnosticOverlay?.InvalidateEditor();
                }
                // Seed the custom undo stack with the new content
                _codeUndo.Clear();
                _codeUndo.Push(_codeBox.Text, 0);
            }

            if (_codeBox.TextLength > 0)
            {
                int ss = Math.Max(0, Math.Min(savedSel, _codeBox.TextLength));
                int maxLen = _codeBox.TextLength - ss;
                int sl = Math.Max(0, Math.Min(savedLen, maxLen));
                _codeBox.Select(ss, sl);
            }
            _codeBox.FirstVisibleLine = savedFirstLine;

            _suppressCodeBoxEvents = false;
        }

        private void UpdateCodeDiagnosticTooltip(Point mousePoint)
        {
            if (_codeBox == null || _codeDiagTooltip == null || _codeBox.TextLength == 0)
            {
                HideCodeDiagnosticTooltip();
                return;
            }

            int charIndex = _codeBox.GetCharIndexFromPosition(mousePoint);
            if (charIndex < 0 || charIndex >= _codeBox.TextLength)
            {
                HideCodeDiagnosticTooltip();
                return;
            }

            if (!SyntaxHighlighter.TryGetDiagnosticTooltip(_codeBox, charIndex, out string tip))
            {
                HideCodeDiagnosticTooltip();
                return;
            }

            if (charIndex == _lastCodeDiagTooltipChar &&
                string.Equals(tip, _lastCodeDiagTooltipText, StringComparison.Ordinal))
                return;

            _lastCodeDiagTooltipChar = charIndex;
            _lastCodeDiagTooltipText = tip;
            // Use Show() with explicit position — SetToolTip() doesn't reliably
            // display on RichEdit50W controls.
            var screen = _codeBox.PointToScreen(mousePoint);
            var client = _codeBox.Parent.PointToClient(screen);
            _codeDiagTooltip.Show(tip, _codeBox, mousePoint.X, mousePoint.Y - 20, 10000);
        }

        private void HideCodeDiagnosticTooltip()
        {
            if (_codeDiagTooltip == null || _codeBox == null) return;
            _codeDiagTooltip.Hide(_codeBox);
            _lastCodeDiagTooltipText = null;
            _lastCodeDiagTooltipChar = -1;
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
                    for (int d = 0; d < depth; d++) sb.Append(unit);
                }
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

            int pos = _codeBox.SelectionStart;
            _codeBox.Text = sb.ToString();
            _codeBox.SelectionStart = Math.Min(pos, _codeBox.TextLength);
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

            string priorSource = _layout.OriginalSourceCode ?? _codeBox.Text;

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
            if (!string.IsNullOrEmpty(priorSource) &&
                !string.Equals(priorSource, sourceCode, StringComparison.Ordinal))
            {
                ShowPatchDiff(priorSource, sourceCode);
            }
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
                    if (end < idx) break;
                    string val = src.Substring(idx, end - idx);
                    if (val.Length > 0) codeLiterals.Add(val);
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
                if (end < start) break;
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
