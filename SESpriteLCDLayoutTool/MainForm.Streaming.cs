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
        // ── Live LCD Stream ───────────────────────────────────────────────────────

        private void ToggleLiveListening()
        {
            if (_pipeListener != null && _pipeListener.IsListening)
            {
                _pipeListener.Stop();
                _pipeListener.Dispose();
                _pipeListener = null;
                _mnuListenToggle.Text = "Start Live Listening";
                _mnuPauseToggle.Enabled = _fileWatcher != null && _fileWatcher.IsListening;
                if (!_mnuPauseToggle.Enabled)
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
            if (_fileWatcher != null && _fileWatcher.IsListening && !_fileWatchBidirectional)
            {
                StopFileWatcher();
                SetStatus("LCD output file watching stopped");
                return;
            }

            // If a script sync is active, stop it first — only one file watcher at a time
            if (_fileWatcher != null && _fileWatcher.IsListening)
                StopFileWatcher();

            using (var dlg = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Select LCD output file to watch (!lcd watch)",
                Filter = "All files (*.*)|*.*|Text files (*.txt)|*.txt",
                CheckFileExists = false, // file may not exist yet
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                _fileWatchBidirectional = false;
                _fileWatcher = new LiveFileWatcher();
                _fileWatcher.FrameReceived += OnLiveFrameReceived;
                _fileWatcher.Connected += () => BeginInvoke((Action)(() =>
                {
                    SetStatus($"Watching LCD output: {_fileWatcher?.FilePath}");
                    _mnuPauseToggle.Enabled = true;
                }));
                _fileWatcher.Disconnected += () => BeginInvoke((Action)(() =>
                {
                    SetStatus("LCD output file watching stopped");
                    if (_pipeListener == null || !_pipeListener.IsListening)
                    {
                        _mnuPauseToggle.Enabled = false;
                        _mnuPauseToggle.Text = "Pause Live Stream";
                    }
                }));
                _fileWatcher.Start(dlg.FileName);
                _mnuFileWatchToggle.Text = "Stop Watching LCD File";
                _mnuPauseToggle.Enabled = true;
                SetStatus($"Watching LCD output: {dlg.FileName}");
            }
        }

        private void ToggleScriptWatching()
        {
            if (_fileWatcher != null && _fileWatcher.IsListening && _fileWatchBidirectional)
            {
                StopFileWatcher();
                SetStatus("Script sync stopped");
                return;
            }

            // If a snapshot watcher is active, stop it first — only one file watcher at a time
            if (_fileWatcher != null && _fileWatcher.IsListening)
                StopFileWatcher();

            using (var dlg = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Select script file to sync with VS Code / external editor",
                Filter = "C# files (*.cs)|*.cs|All files (*.*)|*.*",
                CheckFileExists = false, // file may not exist yet
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                _fileWatchBidirectional = true;
                _fileWatcher = new LiveFileWatcher();
                _fileWatcher.FrameReceived += OnFileWatcherFrameReceived;
                _fileWatcher.Connected += () => BeginInvoke((Action)(() =>
                {
                    SetStatus($"Script sync: {_fileWatcher?.FilePath}");
                    _mnuPauseToggle.Enabled = true;
                }));
                _fileWatcher.Disconnected += () => BeginInvoke((Action)(() =>
                {
                    SetStatus("Script sync stopped");
                    if (_pipeListener == null || !_pipeListener.IsListening)
                    {
                        _mnuPauseToggle.Enabled = false;
                        _mnuPauseToggle.Text = "Pause Live Stream";
                    }
                }));
                _fileWatcher.Start(dlg.FileName);
                _mnuScriptWatchToggle.Text = "Stop Script Sync";
                _mnuPauseToggle.Enabled = true;
                SetStatus($"Script sync: {dlg.FileName}  — edits in either direction are synced");
            }
        }

        /// <summary>
        /// Centralised stop for the file watcher regardless of mode.
        /// Cleans up timers, resets menu labels, and restores streaming state.
        /// </summary>
        private void StopFileWatcher()
        {
            if (_fileWatcher == null) return;

            // Flush any pending write-back before stopping
            if (_writeBackTimer != null)
            {
                _writeBackTimer.Stop();
                _pendingWriteBack = null;
            }

            _fileWatcher.Stop();
            _fileWatcher.Dispose();
            _fileWatcher = null;

            // Reset both menu labels to their default state
            _mnuFileWatchToggle.Text  = "Watch LCD Output File…";
            _mnuScriptWatchToggle.Text = "Sync Script File (VS Code)…";

            _fileWatchBidirectional = false;
            _lastWriteBackHash = 0;
            _mnuPauseToggle.Enabled = _pipeListener != null && _pipeListener.IsListening;
            _liveUndoPushed = false;
            RestoreCodeSpritesIfStreamingEnded();
            RefreshCode();
            UpdateSnapshotButtonState();
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

        // ── Bidirectional file watcher ────────────────────────────────────────────

        /// <summary>
        /// Returns true when a one-way live source (pipe, clipboard, or LCD output
        /// file watcher) is actively streaming.  The bidirectional script sync is
        /// excluded because the user's edits are written back to the file, so the
        /// code panel must keep regenerating.
        /// </summary>
        private bool IsOneWayStreaming =>
            (_pipeListener != null && _pipeListener.IsListening && !_pipeListener.IsPaused)
            || (_clipboardTimer != null)
            || (_fileWatcher != null && _fileWatcher.IsListening && !_fileWatcher.IsPaused && !_fileWatchBidirectional);

        /// <summary>
        /// Handles inbound frames from the file watcher.  Unlike the generic
        /// <see cref="OnLiveFrameReceived"/> handler used by pipe/clipboard, this
        /// sets up per-sprite source tracking so that round-trip patching
        /// (<see cref="CodeGenerator.PatchOriginalSource"/>) works correctly
        /// and edits can be written back to the file.
        /// </summary>
        private void OnFileWatcherFrameReceived(string frame)
        {
            BeginInvoke((Action)(() =>
            {
                string priorSource = _layout?.OriginalSourceCode ?? _codeBox?.Text;

                // Normalise line endings to \r\n so OriginalSourceCode, SourceStart/
                // SourceEnd offsets, and _codeBox.Text are always in sync.  VS Code
                // and other editors may save with bare \n which creates mismatches.
                frame = frame.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

                var sprites = CodeParser.Parse(frame);
                if (sprites.Count == 0) return;

                if (_layout == null)
                {
                    _layout = new LcdLayout();
                    _canvas.CanvasLayout = _layout;
                }

                if (!_liveUndoPushed)
                {
                    PushUndo();
                    _liveUndoPushed = true;
                }

                // Set up per-sprite source tracking for round-trip patching
                var contextCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var sprite in sprites)
                {
                    string ctx = sprite.SourceStart >= 0
                        ? CodeGenerator.DetectSpriteContext(frame, sprite.SourceStart)
                        : null;

                    string typeHint = sprite.Type == SpriteEntryType.Text
                        ? SpriteTypeHint(sprite)
                        : sprite.SpriteName ?? "Texture";

                    string label = ctx != null ? $"{ctx}: {typeHint}" : typeHint;

                    if (!contextCounts.TryGetValue(label, out int count))
                        count = 0;
                    contextCounts[label] = count + 1;
                    if (count > 0)
                        label += $".{count + 1}";

                    sprite.ImportLabel    = label;
                    sprite.ImportBaseline = sprite.CloneValues();
                }

                // SNAPSHOT PRESERVATION: If there's a captured snapshot, merge its positions
                // with the new file sprites so snapshot edits survive file sync updates
                bool hadSnapshot = _lastLiveFrame != null && _lastLiveFrame.Count > 0;
                if (hadSnapshot)
                {
                    var mergeResult = SnapshotMerger.Merge(sprites, _lastLiveFrame, applyColors: false);
                    // Update baselines to preserve the merged positions
                    foreach (var sp in sprites)
                    {
                        if (sp.ImportBaseline != null)
                            sp.ImportBaseline = sp.CloneValues();
                    }
                }

                // Sort sprites by SourceStart so layout order matches source order.
                // The parser returns sprites in pass order (Pass 1, 2, 3...), not
                // source order.  When code mixes new MySprite() and MySprite.CreateText(),
                // the ordinal-based code jump (Strategy 4) would select the wrong sprite
                // if we don't sort by source position.  Sprites without SourceStart
                // (value -1) are placed at the end.
                sprites.Sort((a, b) =>
                {
                    if (a.SourceStart < 0 && b.SourceStart < 0) return 0;
                    if (a.SourceStart < 0) return 1;
                    if (b.SourceStart < 0) return -1;
                    return a.SourceStart.CompareTo(b.SourceStart);
                });

                _layout.Sprites.Clear();
                _layout.Sprites.AddRange(sprites);
                _layout.OriginalSourceCode = frame;
                _layout.IsPulsarOrModLayout = false;  // Fresh import has source tracking

                _canvas.CanvasLayout = _layout;
                _canvas.SelectedSprite = sprites.Count > 0 ? sprites[0] : null;
                RefreshLayerList();

                // Auto-switch code style dropdown based on detected script type
                AutoSwitchCodeStyle(frame);

                // Show the file's code directly — no regeneration needed since
                // baselines equal current values (nothing has been modified yet).
                SetCodeText(frame);
                RefreshDetectedCalls();

                if (!string.IsNullOrEmpty(priorSource) && !string.Equals(priorSource, frame, StringComparison.Ordinal))
                    ShowPatchDiff(priorSource, frame);

                // Don't overwrite _lastLiveFrame here - preserve the snapshot!
                // _lastLiveFrame is only updated when "Capture Snapshot" button is clicked

                SetStatus($"File sync: {sprites.Count} sprite(s) imported" +
                    (hadSnapshot ? " (snapshot positions preserved)" : ""));
            }));
        }

        /// <summary>
        /// Schedules a debounced write of <paramref name="code"/> to the watched
        /// file.  Rapid modifications (e.g. holding an arrow key to nudge) are
        /// coalesced so only the final state is written after a short delay.
        /// </summary>
        private void WriteBackToWatchedFile(string code)
        {
            if (_fileWatcher == null || !_fileWatcher.IsListening || _fileWatcher.IsPaused)
                return;
            if (string.IsNullOrWhiteSpace(code)) return;

            // Skip if the code content hasn't changed since the last write-back.
            // This prevents unnecessary file writes during selection changes,
            // code jumping (double-click layer), and other non-editing actions
            // that trigger RefreshCode() without altering the generated code.
            int hash = code.Replace("\r\n", "\n").Replace("\r", "\n").GetHashCode();
            if (hash == _lastWriteBackHash) return;
            _lastWriteBackHash = hash;

            _pendingWriteBack = code;

            if (_writeBackTimer == null)
            {
                _writeBackTimer = new System.Windows.Forms.Timer { Interval = 300 };
                _writeBackTimer.Tick += OnWriteBackTick;
            }

            // Restart the debounce window
            _writeBackTimer.Stop();
            _writeBackTimer.Start();
        }

        private void OnWriteBackTick(object sender, EventArgs e)
        {
            _writeBackTimer.Stop();
            string code = _pendingWriteBack;
            _pendingWriteBack = null;
            if (code != null)
                _fileWatcher?.WriteBack(code);
        }

        /// <summary>
        /// Called when all streaming sources have stopped.  Restores the canvas to
        /// code-tracked sprites so editing and click-to-jump work correctly.
        /// If the user paused before stopping, the code sprites are already on canvas —
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
            UpdateActionBarVisibility();
        }

        /// <summary>
        /// Legacy placeholder — action buttons are now in the main toolbar
        /// with individual Visible management; no separate bar to toggle.
        /// </summary>
        private void UpdateActionBarVisibility()
        {
            // Buttons manage their own Visible state individually.
        }

        /// <summary>
        /// Extracts sprites from a switch-case block by parsing the case body using CodeParser.
        /// Returns null if the case block cannot be found or contains no sprites.
        /// </summary>
        private List<SpriteEntry> ExtractCaseBlockSprites(string code, string caseName)
        {
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(caseName))
                return null;

            // Find the case block
            // Pattern: case EnumType.CaseName: or case CaseName:
            var rxCase = new System.Text.RegularExpressions.Regex(
                @"case\s+(?:[\w\.]+\.)?" + System.Text.RegularExpressions.Regex.Escape(caseName) + @"\s*:",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            var match = rxCase.Match(code);
            if (!match.Success)
                return null;

            // Find the case block body (from ':' to 'break;' or next 'case' or '}')
            int bodyStart = match.Index + match.Length;
            int bodyEnd = FindCaseBlockEnd(code, bodyStart);
            if (bodyEnd <= bodyStart)
                return null;

            string caseBody = code.Substring(bodyStart, bodyEnd - bodyStart);

            // Parse sprites from the case body
            try
            {
                var sprites = CodeParser.Parse(caseBody);
                return sprites;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExtractCaseBlockSprites] Failed to parse case {caseName}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Finds the end of a case block starting from the position after 'case X:'.
        /// Returns the position of the 'break;' statement, next 'case', or closing '}'.
        /// </summary>
        private int FindCaseBlockEnd(string code, int start)
        {
            int braceDepth = 0;
            for (int i = start; i < code.Length - 1; i++)
            {
                char c = code[i];
                char next = code[i + 1];

                if (c == '{') braceDepth++;
                else if (c == '}')
                {
                    if (braceDepth == 0)
                        return i; // End of switch block
                    braceDepth--;
                }
                else if (braceDepth == 0)
                {
                    // Look for 'break;', 'return', or 'case'
                    if (code.Substring(i).StartsWith("break;"))
                        return i + 6; // Include 'break;'
                    if (code.Substring(i).StartsWith("return"))
                        return i;
                    if (code.Substring(i).StartsWith("case "))
                        return i;
                }
            }
            return code.Length;
        }

        /// <summary>
        /// Executes a single detected call, then highlights only its sprites on the
        /// canvas while dimming the rest of the full frame.  The user can edit the
        /// isolated sprites and click "Show All" to restore the complete view.
        /// </summary>
        private void IsolateCallSprites(string call)
        {
            if (_layout == null || string.IsNullOrWhiteSpace(call)) return;

            // Removed duplicate variable declaration
            // Retained correct variable declaration
            string code = _layout?.OriginalSourceCode ?? _codeBox.Text;
            if (string.IsNullOrWhiteSpace(code)) return;

            // When a one-way live source owns the canvas, allow identification
            // (highlighting) but don't modify the sprite data.
            bool highlightOnly = IsOneWayStreaming;

            // Save the full frame so we can restore it later.
            // On first call, snapshot the original sprites. On subsequent
            // calls (switching between isolated methods), restore the original
            // set first to remove orphans from the previous isolation.
            if (_fullFrameSprites == null)
            {
                _fullFrameSprites = new List<SpriteEntry>();
                foreach (var sp in _layout.Sprites)
                    _fullFrameSprites.Add(sp);
            }
            else
            {
                // Restore original sprite list before re-isolating
                _layout.Sprites.Clear();
                foreach (var sp in _fullFrameSprites)
                    _layout.Sprites.Add(sp);
            }

            // Check if this is a virtual method (switch-case)
            DetectedMethodInfo methodInfo = _detectedMethods?.FirstOrDefault(m => m.CallExpression == call);
            if (methodInfo != null && methodInfo.Kind == MethodKind.SwitchCase)
            {
                // Virtual methods: execute the full rendering pipeline with
                // capturedRows filtered to only the target case kind.  This
                // resolves dynamic data (row.Text, row.IconSprite, etc.) that
                // static CodeParser.Parse cannot handle.
                List<SpriteEntry> caseSprites = null;

                var filteredRows = _layout?.CapturedRows?
                    .Where(r => string.Equals(r.Kind, methodInfo.CaseName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // If no captured rows match (or none exist), create a synthetic
                // sample row so BuildSampleEnqueueCode can still feed the switch.
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

                var caseResult = CodeExecutor.ExecuteWithInit(code, null, filteredRows);
                if (caseResult.Success && caseResult.Sprites.Count > 0)
                {
                    caseSprites = caseResult.Sprites;

                    // Tag sprites immediately with source method name during execution
                    // (we know which case block created them)
                    // Extract method name from CallExpression: "RenderHeader(...)" → "RenderHeader"
                    string methodName = methodInfo.CallExpression;
                    int caseParenIdx = methodName?.IndexOf('(') ?? -1;
                    if (caseParenIdx > 0)
                        methodName = methodName.Substring(0, caseParenIdx).Trim();

                    if (!string.IsNullOrEmpty(methodName))
                    {
                        foreach (var sprite in caseSprites)
                        {
                            if (string.IsNullOrEmpty(sprite.SourceMethodName))
                            {
                                sprite.SourceMethodName = methodName;
                                System.Diagnostics.Debug.WriteLine($"[IsolateCall] Tagged sprite '{sprite.DisplayName}' with method '{methodName}'");
                            }
                        }
                    }
                }
                else
                {
                    // Fall back to static extraction if execution fails
                    caseSprites = ExtractCaseBlockSprites(code, methodInfo.CaseName);
                }

                if (caseSprites == null || caseSprites.Count == 0)
                {
                    SetStatus($"Could not extract sprites from case block '{methodInfo.CaseName}'");
                    return;
                }

                _execResultLabel.Text      = $"✔ {caseSprites.Count} (case block)";
                _execResultLabel.ForeColor = Color.FromArgb(80, 220, 100);

                System.Diagnostics.Debug.WriteLine($"[IsolateCall-Matching] Starting sprite matching for case '{methodInfo.CaseName}'");
                System.Diagnostics.Debug.WriteLine($"[IsolateCall-Matching] Executed sprites: {caseSprites.Count}");
                System.Diagnostics.Debug.WriteLine($"[IsolateCall-Matching] Layout sprites: {_layout.Sprites.Count}");
                foreach (var sp in caseSprites)
                {
                    System.Diagnostics.Debug.WriteLine($"[IsolateCall-Matching]   Exec sprite: {sp.DisplayName} | Type: {sp.Type} | Method: {sp.SourceMethodName}");
                }

                PushUndo();

                // Build highlight set by matching executed sprites to existing layout sprites.
                // Keep ALL sprites in the layout — only dim the non-matched ones.
                // Two-pass: first match only sprites whose ImportLabel belongs to
                // this case block (e.g. "Footer: Text" for case Footer), then fall
                // back to unrestricted matching for orphan execution sprites.
                _isolatedCallSprites = new HashSet<SpriteEntry>();
                var usedExec = new HashSet<int>();
                string casePrefix = methodInfo.CaseName + ":";

                // Pass 1: match layout sprites that belong to this case block
                System.Diagnostics.Debug.WriteLine($"[IsolateCall-Matching] Pass 1: Matching sprites with ImportLabel prefix '{casePrefix}'");
                foreach (var layoutSp in _layout.Sprites)
                {
                    if (layoutSp.ImportLabel == null ||
                        !layoutSp.ImportLabel.StartsWith(casePrefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    for (int ei = 0; ei < caseSprites.Count; ei++)
                    {
                        if (usedExec.Contains(ei)) continue;
                        var execSp = caseSprites[ei];
                        bool typeMatch = layoutSp.Type == execSp.Type
                            && ((layoutSp.Type == SpriteEntryType.Text && layoutSp.Text == execSp.Text)
                             || (layoutSp.Type == SpriteEntryType.Texture && layoutSp.SpriteName == execSp.SpriteName));
                        if (typeMatch)
                        {
                            _isolatedCallSprites.Add(layoutSp);
                            System.Diagnostics.Debug.WriteLine($"[IsolateCall-Matching]   Pass 1 MATCH: Layout '{layoutSp.DisplayName}' <- Exec '{execSp.DisplayName}'");

                            // Transfer SourceMethodName from executed sprite to layout sprite
                            // so navigation continues to work after Execute to Isolate
                            if (!string.IsNullOrEmpty(execSp.SourceMethodName) &&
                                string.IsNullOrEmpty(layoutSp.SourceMethodName))
                            {
                                layoutSp.SourceMethodName = execSp.SourceMethodName;
                            }

                            usedExec.Add(ei);
                            break;
                        }
                    }
                }

                // Pass 2: match remaining execution sprites against any layout sprite
                // (handles orphans from previous executions that lack ImportLabel)
                foreach (var layoutSp in _layout.Sprites)
                {
                    if (_isolatedCallSprites.Contains(layoutSp)) continue;
                    for (int ei = 0; ei < caseSprites.Count; ei++)
                    {
                        if (usedExec.Contains(ei)) continue;
                        var execSp = caseSprites[ei];
                        bool typeMatch = layoutSp.Type == execSp.Type
                            && ((layoutSp.Type == SpriteEntryType.Text && layoutSp.Text == execSp.Text)
                             || (layoutSp.Type == SpriteEntryType.Texture && layoutSp.SpriteName == execSp.SpriteName));
                        if (typeMatch)
                        {
                            _isolatedCallSprites.Add(layoutSp);

                            // Transfer SourceMethodName from executed sprite to layout sprite
                            if (!string.IsNullOrEmpty(execSp.SourceMethodName) &&
                                string.IsNullOrEmpty(layoutSp.SourceMethodName))
                            {
                                layoutSp.SourceMethodName = execSp.SourceMethodName;
                            }

                            usedExec.Add(ei);
                            break;
                        }
                    }
                }

                // Add any unmatched executed sprites (orphans) to the layout
                System.Diagnostics.Debug.WriteLine($"[IsolateCall-Matching] Processing {caseSprites.Count - usedExec.Count} orphan sprites (highlightOnly={highlightOnly})");
                if (!highlightOnly)
                {
                    for (int ei = 0; ei < caseSprites.Count; ei++)
                    {
                        if (usedExec.Contains(ei)) continue;
                        var orphan = caseSprites[ei];
                        System.Diagnostics.Debug.WriteLine($"[IsolateCall-Matching]   ORPHAN (adding to layout): '{orphan.DisplayName}' Type={orphan.Type}");
                        orphan.SourceStart = -1;
                        orphan.SourceEnd   = -1;
                        _layout.Sprites.Add(orphan);
                        _isolatedCallSprites.Add(orphan);
                    }

                    // If no matches at all, add all case sprites directly
                    if (_isolatedCallSprites.Count == 0)
                    {
                        foreach (var sp in caseSprites)
                        {
                            _layout.Sprites.Add(sp);
                            _isolatedCallSprites.Add(sp);
                        }
                    }
                }

                // Transfer positions from executed case sprites to matched layout sprites
                TransferExecutionPositions(_isolatedCallSprites, caseSprites);

                _canvas.HighlightedSprites = _isolatedCallSprites;
                _canvas.Invalidate();
                RefreshLayerList();
                SpriteEntry firstIso = null;
                foreach (var sp in _isolatedCallSprites) { firstIso = sp; break; }
                _canvas.SelectedSprite = firstIso;
                if (_btnShowAll != null) _btnShowAll.Visible = true;
                UpdateActionBarVisibility();
                SetStatus($"Isolated {_isolatedCallSprites.Count} sprites from case {methodInfo.CaseName}. Click Show All to restore.");
                return;
            }

            var result = CodeExecutor.ExecuteWithInit(code, call, _layout?.CapturedRows);
            if (!result.Success)
            {
                _execResultLabel.Text      = "✗ Error";
                _execResultLabel.ForeColor = Color.FromArgb(220, 80, 80);
                ShowAnimationErrorWithDiagnostics(result.Error, "Execution Error");
                return;
            }

            if (result.Sprites.Count == 0)
            {
                SetStatus($"Call returned 0 sprites — nothing to isolate.");
                return;
            }

            ClearEditorDiagnosticsAfterSuccessfulRun();

            TagSnapshotSprites(result.Sprites);

            _execResultLabel.Text      = $"✔ {result.Sprites.Count} (isolated)";
            _execResultLabel.ForeColor = Color.FromArgb(80, 220, 100);

            PushUndo();

            // Extract method name from call expression for mapping-based matching
            string targetMethodName = null;
            int parenIdx = call.IndexOf('(');
            if (parenIdx > 0)
            {
                targetMethodName = call.Substring(0, parenIdx).Trim();
                int eqIdx = targetMethodName.LastIndexOf('=');
                if (eqIdx >= 0)
                    targetMethodName = targetMethodName.Substring(eqIdx + 1).Trim();
            }

            // Tag sprites immediately with source method name during execution
            // (we know which method created them — no need to wait for Build Sprite Map)
            if (!string.IsNullOrEmpty(targetMethodName))
            {
                foreach (var sprite in result.Sprites)
                {
                    if (string.IsNullOrEmpty(sprite.SourceMethodName))
                    {
                        sprite.SourceMethodName = targetMethodName;
                        System.Diagnostics.Debug.WriteLine($"[IsolateCall] Tagged sprite '{sprite.DisplayName}' with method '{targetMethodName}'");
                    }
                }
            }

            // Cold-start: when layout sprites have no SourceMethodName tags
            // (no prior animation run), run a full execution to tag them.
            // If the layout is completely empty (typical for TorchPlugin on
            // first use), populate it from the full execution results — this
            // automates the manual "run 1 tick" step the user had to do.
            if (!string.IsNullOrEmpty(targetMethodName) &&
                !_layout.Sprites.Any(sp => !string.IsNullOrEmpty(sp.SourceMethodName)))
            {
                try
                {
                    var fullResult = CodeExecutor.ExecuteWithInit(code, null, _layout?.CapturedRows);
                    if (fullResult.Success && fullResult.Sprites.Count > 0)
                    {
                        // Count non-reference sprites in the layout
                        int nonRefCount = 0;
                        foreach (var sp in _layout.Sprites)
                            if (!sp.IsReferenceLayout) nonRefCount++;

                        if (nonRefCount == 0)
                        {
                            // Layout is empty — populate from full execution
                            foreach (var sp in fullResult.Sprites)
                                _layout.Sprites.Add(sp);

                            // Update snapshot so "Show All" can restore correctly
                            _fullFrameSprites = new List<SpriteEntry>();
                            foreach (var sp in _layout.Sprites)
                                _fullFrameSprites.Add(sp);

                            System.Diagnostics.Debug.WriteLine(
                                $"[IsolateCall] Cold-populated layout with {fullResult.Sprites.Count} sprites from full execution");
                        }
                        else
                        {
                            // Layout has sprites but no method tags — tag them
                            var usedFull = new HashSet<int>();
                            foreach (var layoutSp in _layout.Sprites)
                            {
                                for (int fi = 0; fi < fullResult.Sprites.Count; fi++)
                                {
                                    if (usedFull.Contains(fi)) continue;
                                    var fullSp = fullResult.Sprites[fi];
                                    bool typeMatch = layoutSp.Type == fullSp.Type
                                        && ((layoutSp.Type == SpriteEntryType.Text && layoutSp.Text == fullSp.Text)
                                         || (layoutSp.Type == SpriteEntryType.Texture && layoutSp.SpriteName == fullSp.SpriteName));
                                    if (typeMatch)
                                    {
                                        if (!string.IsNullOrEmpty(fullSp.SourceMethodName))
                                            layoutSp.SourceMethodName = fullSp.SourceMethodName;
                                        if (fullSp.SourceMethodIndex >= 0)
                                            layoutSp.SourceMethodIndex = fullSp.SourceMethodIndex;
                                        usedFull.Add(fi);
                                        break;
                                    }
                                }
                            }
                            System.Diagnostics.Debug.WriteLine(
                                $"[IsolateCall] Cold-tagged {usedFull.Count}/{fullResult.Sprites.Count} layout sprites with method names");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[IsolateCall] Cold-tag full execution failed: {ex.Message}");
                }
            }

            // For Pulsar/Mod/Torch scripts, runtime sprites from surface.DrawFrame()
            // don't match CodeParser's static analysis — use sprite mapping when available,
            // otherwise match by Type+Data and keep ALL sprites in the layout.
            if (result.ScriptType == ScriptType.PulsarPlugin ||
                result.ScriptType == ScriptType.ModSurface ||
                result.ScriptType == ScriptType.TorchPlugin)
            {
                _isolatedCallSprites = new HashSet<SpriteEntry>();

                // Prefer SpriteMapping when available (built via "Build Sprite Map")
                bool usedMapping = false;
                if (_layout?.SpriteMapping != null && _layout.SpriteMapping.HasData && !string.IsNullOrEmpty(targetMethodName))
                {
                    foreach (var layoutSp in _layout.Sprites)
                    {
                        if (_layout.SpriteMapping.SpritesBelongsToMethod(layoutSp, targetMethodName))
                        {
                            _isolatedCallSprites.Add(layoutSp);
                            usedMapping = true;
                        }
                    }
                }

                // Fallback to SourceMethodName matching
                if (!usedMapping && !string.IsNullOrEmpty(targetMethodName))
                {
                    bool hasMethodTags = _layout.Sprites.Any(sp => !string.IsNullOrEmpty(sp.SourceMethodName));
                    if (hasMethodTags)
                    {
                        foreach (var layoutSp in _layout.Sprites)
                        {
                            if (string.Equals(layoutSp.SourceMethodName, targetMethodName, StringComparison.Ordinal))
                            {
                                _isolatedCallSprites.Add(layoutSp);
                                usedMapping = true;
                            }
                        }
                    }
                }

                // Final fallback: Type+Data matching
                if (!usedMapping)
                {
                    var usedExec = new HashSet<int>();
                    foreach (var layoutSp in _layout.Sprites)
                    {
                        for (int ei = 0; ei < result.Sprites.Count; ei++)
                        {
                            if (usedExec.Contains(ei)) continue;
                            var execSp = result.Sprites[ei];
                            bool typeMatch = layoutSp.Type == execSp.Type
                                && ((layoutSp.Type == SpriteEntryType.Text && layoutSp.Text == execSp.Text)
                                 || (layoutSp.Type == SpriteEntryType.Texture && layoutSp.SpriteName == execSp.SpriteName));
                            if (typeMatch)
                            {
                                _isolatedCallSprites.Add(layoutSp);

                                // Transfer SourceMethodName from executed sprite to layout sprite
                                if (!string.IsNullOrEmpty(execSp.SourceMethodName) &&
                                    string.IsNullOrEmpty(layoutSp.SourceMethodName))
                                {
                                    layoutSp.SourceMethodName = execSp.SourceMethodName;
                                }

                                usedExec.Add(ei);
                                break;
                            }
                        }
                    }

                    // Add orphan executed sprites not matched to existing layout
                    if (!highlightOnly)
                    {
                        for (int ei = 0; ei < result.Sprites.Count; ei++)
                        {
                            if (usedExec.Contains(ei)) continue;
                            var orphan = result.Sprites[ei];
                            orphan.SourceStart = -1;
                            orphan.SourceEnd   = -1;
                            _layout.Sprites.Add(orphan);
                            _isolatedCallSprites.Add(orphan);
                        }

                        // If no matches at all, add all executed sprites directly
                        if (_isolatedCallSprites.Count == 0)
                        {
                            foreach (var sp in result.Sprites)
                            {
                                _layout.Sprites.Add(sp);
                                _isolatedCallSprites.Add(sp);
                            }
                        }
                    }
                }

                // Transfer positions from executed sprites to matched layout sprites.
                // Without this, layout sprites retain their original positions from
                // static code parsing — only correct after a full animation run.
                TransferExecutionPositions(_isolatedCallSprites, result.Sprites);

                _canvas.HighlightedSprites = _isolatedCallSprites;
                _canvas.Invalidate();
                RefreshLayerList();
                SpriteEntry firstIso = null;
                foreach (var sp in _isolatedCallSprites) { firstIso = sp; break; }
                _canvas.SelectedSprite = firstIso;
                if (_btnShowAll != null) _btnShowAll.Visible = true;
                UpdateActionBarVisibility();
                string matchMethod = usedMapping ? " (via mapping)" : "";
                SetStatus($"Isolated: {call} — {_isolatedCallSprites.Count} sprite(s){matchMethod}. Click Show All to restore.");
                return;
            }

            // Match executed sprites to existing layout sprites and build
            // the highlight set.  When streaming, skip Merge and orphan adds
            // so the live frame data is not modified.
            _isolatedCallSprites = new HashSet<SpriteEntry>();

            // targetMethodName was already extracted above for Pulsar/Mod handling

            if (highlightOnly)
            {
                // First priority: SpriteMapping (built via "Build Sprite Map")
                bool usedMapping = false;
                if (_layout?.SpriteMapping != null && _layout.SpriteMapping.HasData && !string.IsNullOrEmpty(targetMethodName))
                {
                    foreach (var layoutSp in _layout.Sprites)
                    {
                        if (_layout.SpriteMapping.SpritesBelongsToMethod(layoutSp, targetMethodName))
                        {
                            _isolatedCallSprites.Add(layoutSp);
                            usedMapping = true;
                        }
                    }
                }

                // Second priority: SourceMethodName from per-sprite tags
                if (!usedMapping)
                {
                    bool hasMethodTags = _layout.Sprites.Any(sp => !string.IsNullOrEmpty(sp.SourceMethodName));
                    if (hasMethodTags && !string.IsNullOrEmpty(targetMethodName))
                    {
                        foreach (var layoutSp in _layout.Sprites)
                        {
                            if (string.Equals(layoutSp.SourceMethodName, targetMethodName, StringComparison.Ordinal))
                                _isolatedCallSprites.Add(layoutSp);
                        }
                    }
                    else
                    {
                        // Fallback: Type+data matching only — no position/colour transfer.
                        var usedExec2 = new HashSet<int>();
                        foreach (var layoutSp in _layout.Sprites)
                        {
                            for (int ei = 0; ei < result.Sprites.Count; ei++)
                            {
                                if (usedExec2.Contains(ei)) continue;
                                var execSp = result.Sprites[ei];
                                bool typeMatch = layoutSp.Type == execSp.Type
                                    && ((layoutSp.Type == SpriteEntryType.Text && layoutSp.Text == execSp.Text)
                                     || (layoutSp.Type == SpriteEntryType.Texture && layoutSp.SpriteName == execSp.SpriteName));
                                if (typeMatch)
                                {
                                    _isolatedCallSprites.Add(layoutSp);

                                    // Transfer SourceMethodName from executed sprite to layout sprite
                                    if (!string.IsNullOrEmpty(execSp.SourceMethodName) &&
                                        string.IsNullOrEmpty(layoutSp.SourceMethodName))
                                    {
                                        layoutSp.SourceMethodName = execSp.SourceMethodName;
                                    }

                                    usedExec2.Add(ei);
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                var nonRef = new List<SpriteEntry>();
                foreach (var sp in _layout.Sprites)
                    if (!sp.IsReferenceLayout) nonRef.Add(sp);

                // Identify target sprites FIRST, then transfer positions only
                // to matched sprites.  Do NOT merge partial execution results
                // into all layout sprites — that corrupts non-target positions
                // when isolating a single method's output cold (no prior animation).

                // First priority: SpriteMapping (built via "Build Sprite Map")
                bool usedMapping = false;
                if (_layout?.SpriteMapping != null && _layout.SpriteMapping.HasData && !string.IsNullOrEmpty(targetMethodName))
                {
                    foreach (var layoutSp in nonRef)
                    {
                        if (_layout.SpriteMapping.SpritesBelongsToMethod(layoutSp, targetMethodName))
                        {
                            _isolatedCallSprites.Add(layoutSp);
                            usedMapping = true;
                        }
                    }
                }

                // Second priority: SourceMethodName from per-sprite tags
                if (!usedMapping)
                {
                    bool hasMethodTags = nonRef.Any(sp => !string.IsNullOrEmpty(sp.SourceMethodName));
                    if (hasMethodTags && !string.IsNullOrEmpty(targetMethodName))
                    {
                        foreach (var sp in nonRef)
                        {
                            if (string.Equals(sp.SourceMethodName, targetMethodName, StringComparison.Ordinal))
                                _isolatedCallSprites.Add(sp);
                        }
                    }
                    else
                    {
                        // Fallback: Type+name matching only
                        var usedExec = new HashSet<int>();
                        foreach (var sp in nonRef)
                        {
                            for (int ei = 0; ei < result.Sprites.Count; ei++)
                            {
                                if (usedExec.Contains(ei)) continue;
                                var execSp = result.Sprites[ei];
                                bool typeMatch = (sp.Type == SpriteEntryType.Text && execSp.Type == SpriteEntryType.Text
                                                    && sp.Text == execSp.Text)
                                               || (sp.Type == SpriteEntryType.Texture && execSp.Type == SpriteEntryType.Texture
                                                    && sp.SpriteName == execSp.SpriteName);
                                if (typeMatch)
                                {
                                    _isolatedCallSprites.Add(sp);

                                    // Transfer source tracking from executed sprite to layout sprite
                                    if (!string.IsNullOrEmpty(execSp.SourceMethodName) &&
                                        string.IsNullOrEmpty(sp.SourceMethodName))
                                    {
                                        sp.SourceMethodName = execSp.SourceMethodName;
                                        sp.SourceMethodIndex = execSp.SourceMethodIndex;
                                    }

                                    usedExec.Add(ei);
                                    break;
                                }
                            }
                        }

                        // Add orphan executed sprites not matched to existing layout
                        for (int ei = 0; ei < result.Sprites.Count; ei++)
                        {
                            if (usedExec.Contains(ei)) continue;
                            var orphan = result.Sprites[ei];
                            orphan.SourceStart = -1;
                            orphan.SourceEnd   = -1;
                            _layout.Sprites.Add(orphan);
                            _isolatedCallSprites.Add(orphan);
                        }
                    }
                }

                // Transfer runtime positions only to the matched isolated sprites,
                // leaving all other layout sprites at their original positions.
                TransferExecutionPositions(_isolatedCallSprites, result.Sprites);
            }

            // If no matches found, highlight all executed sprites directly
            if (_isolatedCallSprites.Count == 0)
            {
                foreach (var sp in result.Sprites)
                    _isolatedCallSprites.Add(sp);
            }

            // Hide all sprites that are NOT in the isolation set
            foreach (var sp in _layout.Sprites)
            {
                sp.IsHidden = !_isolatedCallSprites.Contains(sp);
            }

            _canvas.HighlightedSprites = _isolatedCallSprites;
            _canvas.Invalidate();
            RefreshLayerList();
            SpriteEntry firstIsolated = null;
            foreach (var sp in _isolatedCallSprites) { firstIsolated = sp; break; }
            _canvas.SelectedSprite = firstIsolated;
            if (_btnShowAll != null) _btnShowAll.Visible = true;
            UpdateActionBarVisibility();
            SetStatus($"Isolated: {call} — {_isolatedCallSprites.Count} sprite(s). Edit, then click Show All.");
        }

        /// <summary>
        /// Builds an element-to-sprite mapping by running the animation for N frames.
        /// This captures all sprite states across the animation lifecycle and enables
        /// accurate Execute &amp; Isolate filtering.
        /// </summary>
        private async void BuildSpriteMappingAsync()
        {
            string code = _codeBox?.Text;
            if (string.IsNullOrWhiteSpace(code))
            {
                SetStatus("Code panel is empty. Nothing to map.");
                return;
            }

            SetStatus("Building sprite map (running 30 frames)...");
            _execResultLabel.Text = "Mapping…";
            _execResultLabel.ForeColor = Color.FromArgb(200, 180, 60);

            SpriteMappingBuilder.BuildResult buildResult = null;
            await System.Threading.Tasks.Task.Run(() =>
            {
                buildResult = SpriteMappingBuilder.BuildMapping(
                    code,
                    callExpression: null,  // Auto-detect all methods
                    frameCount: 30,
                    capturedRows: _layout?.CapturedRows);
            });

            if (buildResult == null || !buildResult.Success)
            {
                _execResultLabel.Text = "✗ Map failed";
                _execResultLabel.ForeColor = Color.FromArgb(220, 80, 80);
                SetStatus($"Failed to build sprite map: {buildResult?.Error ?? "Unknown error"}");
                return;
            }

            // Store the mapping in the layout
            if (_layout != null)
            {
                _layout.SpriteMapping = buildResult.Mapping;

                // Also propagate SourceMethodName to layout sprites based on the mapping
                // This updates existing sprites so isolation works immediately
                foreach (var sprite in _layout.Sprites)
                {
                    var methods = buildResult.Mapping.GetMethodsForSprite(sprite);
                    var methodList = methods.ToList();
                    if (methodList.Count == 1)
                    {
                        // Unambiguous ownership
                        sprite.SourceMethodName = methodList[0];
                    }
                    else if (methodList.Count > 1)
                    {
                        // Multiple methods produce this sprite - choose the MOST SPECIFIC one
                        // Prefer specific render methods (RenderOscilloscope) over orchestrators (BuildSprites, Render)
                        // Strategy: Prefer methods that start with "Render" and are NOT just "Render" itself
                        string chosen = null;

                        // Priority 1: Specific render methods (RenderOscilloscope, RenderTitle, etc.)
                        foreach (var method in methodList)
                        {
                            if (method.StartsWith("Render", StringComparison.Ordinal) &&
                                method.Length > 6 &&
                                method != "Render")
                            {
                                chosen = method;
                                break;
                            }
                        }

                        // Priority 2: Helper methods (DrawGauge, etc.)
                        if (chosen == null)
                        {
                            foreach (var method in methodList)
                            {
                                if (method != "BuildSprites" && method != "Render")
                                {
                                    chosen = method;
                                    break;
                                }
                            }
                        }

                        // Priority 3: Fallback to first (BuildSprites or Render)
                        if (chosen == null && methodList.Count > 0)
                        {
                            chosen = methodList[0];
                        }

                        sprite.SourceMethodName = chosen;
                        System.Diagnostics.Debug.WriteLine($"[BuildSpriteMap] Sprite '{sprite.DisplayName}' belongs to {methodList.Count} methods: [{string.Join(", ", methodList)}] → chose '{chosen}'");
                    }
                }
            }

            int methodCount = buildResult.Mapping.MethodToSprites.Count;
            int uniqueSprites = buildResult.Mapping.MethodToSprites.Values
                .SelectMany(s => s).Distinct().Count();

            _execResultLabel.Text = $"✔ Map: {methodCount} methods";
            _execResultLabel.ForeColor = Color.FromArgb(80, 220, 100);
            SetStatus($"Sprite map built: {methodCount} methods, {uniqueSprites} unique sprites across {buildResult.FramesExecuted} frames. Isolation will now use this map.");

            // Refresh layer list to show updated method names
            RefreshLayerList();
        }

        /// <summary>
        /// Restores the full frame view after an isolated call edit session.
        /// Merges any edits back and removes the dimming.
        /// </summary>
        private void RestoreFullView()
        {
            _canvas.HighlightedSprites = null;
            _isolatedCallSprites = null;

            if (_fullFrameSprites != null && _layout != null)
            {
                // Restore the original sprite list — remove any orphan sprites
                // that were added during isolation and restore the original set.
                _layout.Sprites.Clear();
                foreach (var sp in _fullFrameSprites)
                    _layout.Sprites.Add(sp);
                _fullFrameSprites = null;
            }

            // Unhide all layers when restoring full view
            if (_layout != null)
                foreach (var sp in _layout.Sprites)
                    sp.IsHidden = false;

            if (_btnShowAll != null) _btnShowAll.Visible = false;
            UpdateActionBarVisibility();
            _canvas.Invalidate();
            RefreshLayerList();
            SetStatus("Full view restored.");
        }

        /// <summary>
        /// Opens the Template Gallery dialog where users can browse and insert pre-built sprite patterns.
        /// </summary>
        private void ShowTemplateGallery()
        {
            float surfW = _layout?.SurfaceWidth ?? 512f;
            float surfH = _layout?.SurfaceHeight ?? 512f;
            int targetIdx = _cmbCodeStyle?.SelectedIndex ?? 0;

            using (var dlg = new TemplateGalleryDialog(surfW, surfH, targetIdx))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (string.IsNullOrEmpty(dlg.GeneratedCode)) return;

                // Insert the generated code at the cursor in the code editor
                if (_codeBox != null)
                {
                    _codeBox.Focus();
                    _suppressCodeBoxEvents = true;
                    try
                    {
                        _codeBox.SelectedText = dlg.GeneratedCode;
                    }
                    finally
                    {
                        _suppressCodeBoxEvents = false;
                    }

                    _codeBoxDirty = true;
                    _lblCodeTitle.Text = "✏ Code (edited)";
                    _lblCodeTitle.ForeColor = Color.FromArgb(255, 200, 80);
                    if (_btnApplyCode != null) _btnApplyCode.Visible = true;

                    SetStatus("Template inserted — click 'Apply Code' or '▶ Execute Code' to see it on canvas.");
                }
            }
        }

        /// <summary>Hides or shows the selected sprite layer(s).</summary>
        private void ToggleSelectedLayerVisibility(bool hide)
        {
            var selected = GetSelectedSprites();
            if (selected.Count == 0)
            {
                var sel = _canvas.SelectedSprite;
                if (sel != null) selected.Add(sel);
            }
            if (selected.Count == 0 || _layout == null) return;

            foreach (var sp in selected)
                sp.IsHidden = hide;
            if (hide)
                _canvas.SelectedSprite = null;
            _canvas.Invalidate();
            RefreshLayerList();
            SetStatus(hide
                ? (selected.Count == 1 ? $"Layer hidden: {selected[0].DisplayName}" : $"{selected.Count} layers hidden")
                : (selected.Count == 1 ? $"Layer shown: {selected[0].DisplayName}" : $"{selected.Count} layers shown"));
        }

        /// <summary>Locks or unlocks the selected sprite layer(s).</summary>
        private void ToggleSelectedLayerLock(bool lockIt)
        {
            var selected = GetSelectedSprites();
            if (selected.Count == 0)
            {
                var sel = _canvas.SelectedSprite;
                if (sel != null) selected.Add(sel);
            }
            if (selected.Count == 0 || _layout == null) return;

            PushUndo();
            foreach (var sp in selected)
                sp.IsLocked = lockIt;
            _canvas.Invalidate();
            RefreshLayerList();
            SetStatus(lockIt
                ? (selected.Count == 1 ? $"Layer locked: {selected[0].DisplayName}" : $"{selected.Count} layers locked")
                : (selected.Count == 1 ? $"Layer unlocked: {selected[0].DisplayName}" : $"{selected.Count} layers unlocked"));
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

        /// <summary>Hides all sprite layers drawn above (on top of) the selected sprite.</summary>
        private void HideLayersAbove()
        {
            var sel = _canvas.SelectedSprite;
            if (sel == null || _layout == null) return;

            int idx = _layout.Sprites.IndexOf(sel);
            if (idx < 0) return;

            int count = 0;
            for (int i = idx + 1; i < _layout.Sprites.Count; i++)
            {
                if (!_layout.Sprites[i].IsHidden)
                {
                    _layout.Sprites[i].IsHidden = true;
                    count++;
                }
            }

            if (count > 0)
            {
                _canvas.Invalidate();
                RefreshLayerList();
                SetStatus($"{count} layer(s) above hidden — use Show All Layers to restore.");
            }
        }

        private void OnLiveFrameReceived(string frame)
        {
            // Called on background/timer thread — always marshal to UI thread
            BeginInvoke((Action)(() =>
            {
                var sprites = CodeParser.Parse(frame);

                // Extract snapshot tag if present
                string frameTag = CodeParser.ParseSnapshotTag(frame);

                // Always parse @ROW data — even when the file has no MySprite blocks.
                // This ensures CapturedRows is populated from @ROW-only files so the
                // executor can replay switch-case render methods with real game data.
                var frameRows = CodeParser.ParseSnapshotRows(frame);

                // Parse debug variables streamed by SnapshotDebug() calls in the plugin
                var debugVars = CodeParser.ParseDebugVars(frame);
                if (debugVars.Count > 0)
                {
                    _liveDebugVars = debugVars;
                    UpdateLiveDebugVariables(debugVars, frameTag);
                }

                // Create a default layout if none is loaded so live frames always land somewhere
                if (_layout == null)
                {
                    _layout = new LcdLayout();
                    _canvas.CanvasLayout = _layout;
                }

                // Store captured rows before the early-return check
                bool rowsUpdated = false;
                if (frameRows.Count > 0)
                {
                    _layout.CapturedRows = frameRows;
                    rowsUpdated = true;
                }

                // When the file has @ROW data but no sprites, and source code is loaded,
                // auto-execute the code with the captured rows to produce a rendered frame.
                if (sprites.Count == 0)
                {
                    if (rowsUpdated && _layout.OriginalSourceCode != null)
                    {
                        AutoExecuteWithCapturedRows(frameTag, frameRows.Count);
                    }
                    return;
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

                // Always save the live frame so Execute Code can merge
                // snapshot positions even when no source code is loaded yet.
                _lastLiveFrame = sprites;

                // Always replace for a correct full-resolution visual.
                // (The merge into code sprites happens in ToggleLivePause.)
                _layout.Sprites.Clear();
                _layout.Sprites.AddRange(sprites);
                _canvas.CanvasLayout = _layout;
                RefreshLayerList();

                // Refresh detected calls when rows changed so the call list
                // reflects real game data instead of hardcoded sample placeholders.
                if (rowsUpdated)
                    RefreshDetectedCalls();

                SetStatus(frameTag != null
                    ? $"Live frame [{frameTag}]: {sprites.Count} sprites{(debugVars.Count > 0 ? $", {debugVars.Count} vars" : "")}"
                    : $"Live frame: {sprites.Count} sprites{(debugVars.Count > 0 ? $", {debugVars.Count} vars" : "")}");
            }));
        }

        /// <summary>
        /// Populates the Variables tab with debug variables received from a live
        /// stream frame.  Follows the same pattern as
        /// <see cref="UpdateVariablesDuringAnimation"/> — in-place updates to
        /// avoid flashing, change highlighting, and type-based colour coding.
        /// </summary>
        private void UpdateLiveDebugVariables(List<Models.DebugVariable> vars, string tag)
        {
            if (vars == null || vars.Count == 0) return;

            // Track previous values for change highlighting
            var previousValues = new Dictionary<string, string>();
            foreach (ListViewItem existing in _lstVariables.Items)
            {
                previousValues[existing.Text] = existing.SubItems[1].Text;
            }

            _lstVariables.BeginUpdate();

            var ordered = vars.OrderBy(v => v.Name).ToList();

            if (_lstVariables.Items.Count != ordered.Count)
            {
                _lstVariables.Items.Clear();
                foreach (var dv in ordered)
                {
                    string value = FormatFieldValue(dv.TypedValue);
                    var item = new ListViewItem(new[] { dv.Name, value, dv.TypeName });
                    item.ForeColor = GetColorForType(dv.TypedValue);
                    _lstVariables.Items.Add(item);
                }
            }
            else
            {
                for (int i = 0; i < ordered.Count; i++)
                {
                    var dv = ordered[i];
                    string value = FormatFieldValue(dv.TypedValue);
                    var item = _lstVariables.Items[i];

                    if (item.Text != dv.Name)
                        item.Text = dv.Name;
                    if (item.SubItems[1].Text != value)
                        item.SubItems[1].Text = value;
                    if (item.SubItems.Count > 2)
                        item.SubItems[2].Text = dv.TypeName;

                    Color baseColor = GetColorForType(dv.TypedValue);
                    if (previousValues.TryGetValue(dv.Name, out string oldValue) && oldValue != value)
                        item.ForeColor = BrightenColor(baseColor);
                    else
                        item.ForeColor = baseColor;
                }
            }

            _lstVariables.EndUpdate();

            if (_tabVariables != null)
            {
                _tabVariables.Text = tag != null
                    ? $"Variables [{tag}] ({vars.Count})"
                    : $"Variables (Live: {vars.Count})";
            }
        }

        /// <summary>
        /// Called when the watched file has @ROW data but no MySprite blocks.
        /// Re-executes the loaded source code with the updated CapturedRows so
        /// the switch-case render methods produce a proper rendering.
        /// </summary>
        private void AutoExecuteWithCapturedRows(string frameTag, int rowCount)
        {
            string code = _layout?.OriginalSourceCode;
            if (string.IsNullOrWhiteSpace(code)) return;

            if (!_liveUndoPushed)
            {
                PushUndo();
                _liveUndoPushed = true;
            }

            // Detect a suitable call expression from the source code
            string call = _execCallBox?.Text?.Trim();
            if (string.IsNullOrEmpty(call) || (call != null && IsVirtualSwitchCaseCall(call)))
                call = CodeExecutor.DetectCallExpression(code);

            try
            {
                var result = CodeExecutor.ExecuteWithInit(code, call, _layout.CapturedRows);
                if (!result.Success || result.Sprites.Count == 0) return;

                TagSnapshotSprites(result.Sprites);

                // Check whether we should merge into existing source-tracked sprites
                // or do a full replacement
                var editable = new List<SpriteEntry>();
                foreach (var sp in _layout.Sprites)
                    if (!sp.IsReferenceLayout && sp.SourceStart >= 0 && sp.ImportBaseline != null)
                        editable.Add(sp);

                if (editable.Count > 0)
                {
                    var mergeResult = SnapshotMerger.Merge(editable, result.Sprites, applyColors: true);

                    foreach (var sp in editable)
                    {
                        if (sp.ImportBaseline != null)
                            sp.ImportBaseline = sp.CloneValues();
                    }

                    foreach (var orphan in mergeResult.UnmatchedSnapshots)
                    {
                        orphan.SourceStart = -1;
                        orphan.SourceEnd   = -1;
                        _layout.Sprites.Add(orphan);
                    }
                }
                else
                {
                    // No source tracking — full replacement with executed sprites
                    _layout.Sprites.Clear();
                    _layout.Sprites.AddRange(result.Sprites);
                }

                _lastLiveFrame = result.Sprites;
                _canvas.CanvasLayout = _layout;
                RefreshLayerList();
                RefreshDetectedCalls();

                string tagInfo = frameTag != null ? $" [{frameTag}]" : "";
                SetStatus($"@ROW update{tagInfo}: {rowCount} rows → {result.Sprites.Count} sprites");
            }
            catch
            {
                // Execution failed — just show the row count
                RefreshDetectedCalls();
                string tagInfo = frameTag != null ? $" [{frameTag}]" : "";
                SetStatus($"@ROW update{tagInfo}: {rowCount} rows captured (execute code to render)");
            }
        }

        private void SetStatus(string msg) => _statusLabel.Text = msg;

        private void UpdateStatus()
        {
            if (_layout == null) return;
            var sp = _canvas.SelectedSprite;
            string sel = sp != null ? $" | Selected: {sp.DisplayName}  ({sp.X:F0}, {sp.Y:F0})  {sp.Width:F0}×{sp.Height:F0}" : "";
            _statusLabel.Text = $"Surface: {_layout.SurfaceWidth}×{_layout.SurfaceHeight}  Sprites: {_layout.Sprites.Count}{sel}";
            RefreshDebugStats();
        }

    }
}
