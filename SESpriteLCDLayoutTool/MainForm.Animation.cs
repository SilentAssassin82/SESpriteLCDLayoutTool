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
        // ── Animation playback handlers ───────────────────────────────────────────

        /// <summary>
        /// Starts (or restarts) animation in focused mode.
        /// - ISOLATION MODE (when _isolatedCallSprites is active): Runs ONLY the isolated method
        /// - FOCUSED MODE (normal): Runs full scene but dims other methods' sprites
        /// </summary>
        private void StartFocusedAnimation(string call)
        {
            if (_layout == null || string.IsNullOrWhiteSpace(call)) return;

            string code = _layout?.OriginalSourceCode ?? _codeBox.Text;
            if (string.IsNullOrWhiteSpace(code)) return;

            // PB scripts always call Main() directly — focused animation
            // would compile twice (ExecuteWithInit + Prepare) for no benefit.
            // Fall through to the normal Prepare path by clearing focus state
            // and preparing directly.
            if (CodeExecutor.DetectScriptType(code) == ScriptType.ProgrammableBlock)
            {
                _animFocusCall = null;
                _animFocusSprites = null;
                _canvas.HighlightedSprites = null;

                if (_animPlayer != null && _animPlayer.IsPlaying)
                    return; // already running, nothing to re-focus

                EnsureAnimPlayer();
                PushUndo();
                CaptureAnimPositionSnapshot();
                _rtbConsole?.Clear();

                string pbError = _animPlayer.Prepare(code, null, _layout?.CapturedRows);
                if (pbError != null)
                {
                    MessageBox.Show(pbError, "Animation Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _animPositionSnapshot = null;
                    UpdateAnimButtonStates();
                    return;
                }

                _animPlayer.Play();
                UpdateAnimButtonStates();
                SetStatus("Animation playing…");
                return;
            }

            // Check if this is a virtual method (switch-case)
            DetectedMethodInfo methodInfo = _detectedMethods?.FirstOrDefault(m => m.CallExpression == call);
            bool isVirtual = methodInfo != null && methodInfo.Kind == MethodKind.SwitchCase;

            // For virtual methods, we execute with filtered capturedRows (only the
            // target case Kind) to identify which sprites belong to this case.
            // For real methods, we execute the specific call expression directly.
            List<SnapshotRowData> execRows = _layout?.CapturedRows;
            string execCall = call;

            if (isVirtual)
            {
                // Filter captured rows to the target case kind
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

                // Execute full pipeline with filtered rows to build focus set
                var result = CodeExecutor.ExecuteWithInit(code, null, filteredRows);
                if (!result.Success || result.Sprites.Count == 0)
                {
                    // Could not build focus set — fall back to unfocused animation
                    _animFocusSprites = null;
                    _animFocusCall = null;
                    execCall = null;
                    execRows = _layout?.CapturedRows;
                }
                else
                {
                    // Build focus set from filtered execution
                    _animFocusSprites = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var sp in result.Sprites)
                    {
                        string key = BuildFocusSpriteKey(sp);
                        _animFocusSprites.Add(key);
                    }
                    _animFocusCall = call;

                    // ISOLATION MODE: If isolation is active, run ONLY this method (not full scene)
                    // Otherwise run full scene with all captured rows for normal focused mode
                    if (_isolatedCallSprites != null && _isolatedCallSprites.Count > 0)
                    {
                        // Keep execCall pointing to this specific method - don't run full orchestrator
                        execCall = call;
                        execRows = filteredRows;
                    }
                    else
                    {
                        // Normal focused mode: run full scene (null call) with all captured rows
                        execCall = null;
                        execRows = _layout?.CapturedRows;
                    }
                }
            }
            else
            {
                // Real methods: execute the specific call to build focus set
                var result = CodeExecutor.ExecuteWithInit(code, call, _layout?.CapturedRows);
                if (!result.Success || result.Sprites.Count == 0)
                {
                    // Could not build focus set — fall back to unfocused animation
                    _animFocusSprites = null;
                    _animFocusCall = null;
                    execCall = null;
                    execRows = _layout?.CapturedRows;
                }
                else
                {
                    // Build focus set using type + name + approximate position to uniquely
                    // identify sprites from this method even when other methods use the
                    // same sprite names (e.g., Circle, SquareSimple).
                    _animFocusSprites = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var sp in result.Sprites)
                    {
                        string key = BuildFocusSpriteKey(sp);
                        _animFocusSprites.Add(key);
                    }
                    _animFocusCall = call;

                    // ISOLATION MODE: If isolation is active, run ONLY this method (not full scene)
                    // Otherwise run full scene for normal focused mode (dim others, show all)
                    if (_isolatedCallSprites != null && _isolatedCallSprites.Count > 0)
                    {
                        // Keep execCall pointing to this specific method - don't run full orchestrator
                        // This ensures animation runs ONLY the isolated method
                        execCall = call;
                    }
                    else
                    {
                        // Normal focused mode: run the full scene (null call) so the orchestrator
                        // calculates correct positions; _animFocusSprites will highlight
                        // only sprites belonging to this focused method.
                        execCall = null;
                    }
                    execRows = _layout?.CapturedRows;
                }
            }

            // If animation is already running, just apply the focus — the next
            // OnAnimFrame will pick up _animFocusSprites and dim accordingly.
            if (_animPlayer != null && _animPlayer.IsPlaying)
            {
                SetStatus($"Focused on: {call}");
                return;
            }

            // Start animation — always run full scene so orchestrator calculates
            // correct positions; _animFocusSprites highlights only the focused method.
            EnsureAnimPlayer();
            PushUndo();
            CaptureAnimPositionSnapshot();
            _rtbConsole?.Clear();

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

            _animPlayer.Play();
            UpdateAnimButtonStates();
            SetStatus(_animFocusCall != null
                ? $"Animation playing — focused on: {_animFocusCall}"
                : "Animation playing");
        }

        private void OnAnimPlayClick(object sender, EventArgs e)
        {
            // Resume if paused
            if (_animPlayer != null && _animPlayer.IsPaused)
            {
                ResetScrubbing();
                _animPlayer.Play();
                UpdateAnimButtonStates();
                SetStatus("Animation resumed.");
                return;
            }

            // If a call is currently isolated (via Execute & Isolate), carry
            // that isolation into animation mode so only those sprites are shown.
            // DON'T call RestoreFullView() here - we want to keep _isolatedCallSprites
            // active so OnAnimFrame can filter the animation frames.
            if (_isolatedCallSprites != null && _isolatedCallSprites.Count > 0)
            {
                string call = _execCallBox.Text.Trim();
                if (!string.IsNullOrEmpty(call))
                {
                    // Skip focused-animation path for PB scripts — same reasoning
                    // as the non-isolated check below (PB always calls Main directly).
                    string isoCode = _layout?.OriginalSourceCode ?? _codeBox.Text;
                    if (CodeExecutor.DetectScriptType(isoCode) != ScriptType.ProgrammableBlock)
                    {
                        // Keep isolation active - OnAnimFrame will filter sprites using the mapping
                        StartFocusedAnimation(call);
                        return;
                    }
                }
            }

            // Starting from scratch clears any focused mode
            _animFocusCall = null;
            _animFocusSprites = null;
            _canvas.HighlightedSprites = null;

            // Clear selection once up-front so OnAnimFrame never triggers
            // OnSelectionChanged (the null guard in OnAnimFrame is the main
            // protection, but this avoids even the first-frame cost).
            if (_canvas.SelectedSprite != null)
                _canvas.SelectedSprite = null;

            // Prepare + play from scratch
            string code = _layout?.OriginalSourceCode ?? _codeBox.Text;
            if (string.IsNullOrWhiteSpace(code)) { SetStatus("No code to animate."); return; }

            // If a specific call is selected in the call box, animate
            // just that method instead of the full scene.
            // Skip for PB scripts — BuildPbAnimationSource always calls Main()
            // directly, so the focused-animation path is unnecessary and can
            // interfere with PB animation playback (e.g. auto-detected
            // "Main("", UpdateType.None)" would divert through ExecuteWithInit).
            {
                string selectedCall = _execCallBox.Text.Trim();
                if (!string.IsNullOrEmpty(selectedCall) &&
                    CodeExecutor.DetectScriptType(code) != ScriptType.ProgrammableBlock)
                {
                    StartFocusedAnimation(selectedCall);
                    return;
                }
            }

            EnsureAnimPlayer();
            PushUndo();
            CaptureAnimPositionSnapshot();
            _rtbConsole?.Clear();

            // Pass null so CompileForAnimation auto-detects ALL rendering
            // methods and calls them every frame — showing the full scene.
            string error = _animPlayer.Prepare(code, null, _layout?.CapturedRows);
            if (error != null)
            {
                MessageBox.Show(error, "Animation Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _animPositionSnapshot = null;
                UpdateAnimButtonStates();
                return;
            }

            _animPlayer.Play();
            UpdateAnimButtonStates();
            SetStatus("Animation playing…");
        }

        private void OnAnimPauseClick(object sender, EventArgs e)
        {
            if (_animPlayer == null) return;
            if (_animPlayer.IsPaused)
                _animPlayer.Play();
            else
                _animPlayer.Pause();
            UpdateAnimButtonStates();
        }

        private void OnAnimStopClick(object sender, EventArgs e)
        {
            _animPlayer?.Stop();
            _animFocusCall = null;
            _canvas.HighlightedSprites = null;
            UpdateAnimButtonStates();
        }

        private void OnAnimStepClick(object sender, EventArgs e)
        {
            // If not yet prepared, prepare first
            if (_animPlayer == null || !_animPlayer.IsPlaying)
            {
                // Detect script type once — PB scripts always call Main() directly,
                // so the focused-animation path (extra compile to build focus set) is unnecessary.
                string stepCode = _layout?.OriginalSourceCode ?? _codeBox.Text;
                bool isPbStep = !string.IsNullOrWhiteSpace(stepCode)
                    && CodeExecutor.DetectScriptType(stepCode) == ScriptType.ProgrammableBlock;

                // If a call is currently isolated, carry that into animation
                // Keep _isolatedCallSprites active so OnAnimFrame can filter frames
                if (!isPbStep && _isolatedCallSprites != null && _isolatedCallSprites.Count > 0)
                {
                    string isoCall = _execCallBox.Text.Trim();
                    if (!string.IsNullOrEmpty(isoCall))
                    {
                        // Keep isolation active - OnAnimFrame will filter sprites using the mapping
                        StartFocusedAnimation(isoCall);
                        // Now step the freshly started animation
                        if (_animPlayer != null && _animPlayer.IsPlaying)
                            _animPlayer.StepForward();
                        UpdateAnimButtonStates();
                        return;
                    }
                }

                // If a specific call is selected, animate just that method
                // Skip for PB scripts — same reasoning as OnAnimPlayClick:
                // PB always calls Main() directly, focused animation is unnecessary
                // and causes a double-compile (ExecuteWithInit + Prepare).
                if (!isPbStep)
                {
                    string selectedCall = _execCallBox.Text.Trim();
                    if (!string.IsNullOrEmpty(selectedCall))
                    {
                        StartFocusedAnimation(selectedCall);
                        if (_animPlayer != null && _animPlayer.IsPlaying)
                            _animPlayer.StepForward();
                        UpdateAnimButtonStates();
                        return;
                    }
                }

                string code = stepCode;
                if (string.IsNullOrWhiteSpace(code)) { SetStatus("No code to animate."); return; }

                EnsureAnimPlayer();
                PushUndo();
                CaptureAnimPositionSnapshot();

                // Pass null so CompileForAnimation auto-detects ALL rendering
                // methods and calls them every frame — showing the full scene.
                string error = _animPlayer.Prepare(code, null, _layout?.CapturedRows);
                if (error != null)
                {
                    MessageBox.Show(error, "Animation Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _animPositionSnapshot = null;
                    UpdateAnimButtonStates();
                    return;
                }
            }

            _animPlayer.StepForward();
            UpdateAnimButtonStates();
        }

        private void OnAnimFrame(List<SpriteEntry> sprites, int tick)
        {
            // Suppress the syntax-highlight debounce timer while the animation is running.
            // The timer only fires when the text changes, but holding it stopped during
            // playback avoids any accidental Roslyn parse triggered by other code paths.
            _syntaxTimer?.Stop();

            _layout.Sprites.Clear();

            // Isolation mode: when a specific method is isolated (via Execute & Isolate),
            // filter animation frames to show ONLY sprites that belong to that method.
            // Primary: SourceMethodName set by instrumentation pipeline at runtime.
            // Fallback: SpriteMapping position-independent matching (if available).
            List<SpriteEntry> frameSprites = sprites;
            float yOffset = 0f;
            if (_isolatedCallSprites != null && _isolatedCallSprites.Count > 0)
            {
                string targetMethodName = CodeExecutor.ExtractMethodName(_execCallBox.Text.Trim());
                if (!string.IsNullOrEmpty(targetMethodName))
                {
                    var filtered = new List<SpriteEntry>();
                    foreach (var sp in sprites)
                    {
                        // Primary: SourceMethodName set by instrumentation (SetCurrentMethod + RecordSpriteMethod)
                        if (!string.IsNullOrEmpty(sp.SourceMethodName) &&
                            string.Equals(sp.SourceMethodName, targetMethodName, StringComparison.Ordinal))
                        {
                            filtered.Add(sp);
                        }
                        // Fallback: SpriteMapping position-independent matching
                        else if (_layout?.SpriteMapping != null && _layout.SpriteMapping.HasData &&
                                 _layout.SpriteMapping.SpritesBelongsToMethodByName(sp, targetMethodName))
                        {
                            filtered.Add(sp);
                        }
                    }
                    if (filtered.Count > 0)
                        frameSprites = filtered;

                    // Apply Y offset from sprite mapping if available
                    if (_layout?.SpriteMapping != null &&
                        _layout.SpriteMapping.MethodYOffsets.TryGetValue(targetMethodName, out float methodOffset))
                    {
                        yOffset = methodOffset;
                        System.Diagnostics.Debug.WriteLine($"[OnAnimFrame] Isolated animation '{targetMethodName}': yOffset={yOffset:F1}, sprites={frameSprites.Count}");
                    }
                }
            }

            // Show executor sprites directly — the compiled code produces sprites
            // with correct positions (via orchestrator like BuildSprites, or individual
            // render methods).  No merge with snapshot positions needed; the pre-play
            // layout is saved and restored when animation stops.
            foreach (var sp in frameSprites)
            {
                // Apply Y offset for isolated animations to position them correctly
                if (yOffset != 0f)
                {
                    float oldY = sp.Y;
                    sp.Y += yOffset;
                    if (frameSprites.Count < 5) // Only log for small sprite counts to avoid spam
                        System.Diagnostics.Debug.WriteLine($"  Sprite '{sp.SpriteName ?? sp.Text}': Y {oldY:F1} → {sp.Y:F1}");
                }
                _layout.Sprites.Add(sp);
            }

            // Focused animation mode: dim sprites not belonging to the focused call
            if (_animFocusCall != null && _animFocusSprites != null && _animFocusSprites.Count > 0)
            {
                var highlighted = new HashSet<SpriteEntry>();
                foreach (var sp in _layout.Sprites)
                {
                    string key = BuildFocusSpriteKey(sp);
                    if (_animFocusSprites.Contains(key))
                        highlighted.Add(sp);
                }
                _canvas.HighlightedSprites = highlighted.Count > 0 ? highlighted : null;
            }

            if (_canvas.SelectedSprite != null)
                _canvas.SelectedSprite = null;
            _canvas.Invalidate();

            string typeTag = _animPlayer?.ScriptType == ScriptType.ProgrammableBlock ? "PB"
                           : _animPlayer?.ScriptType == ScriptType.ModSurface        ? "Mod"
                           : _animPlayer?.ScriptType == ScriptType.PulsarPlugin      ? "Pulsar"
                           : _animPlayer?.ScriptType == ScriptType.TorchPlugin       ? "Torch"
                           : "LCD";
            double ms = _animPlayer?.LastFrameMs ?? 0;
            string focusTag = _animFocusCall != null ? " 🔍" : "";
            string heatTag = "";
            if (_animPlayer?.LastMethodTimings != null && _animPlayer.LastMethodTimings.Count > 0)
            {
                string slowest = null; double slowMs = 0;
                foreach (var kv in _animPlayer.LastMethodTimings)
                    if (kv.Value > slowMs) { slowMs = kv.Value; slowest = kv.Key; }
                if (slowest != null)
                    heatTag = $"  🔥{slowest}:{slowMs:F2}ms";
            }
            _lblAnimTick.Text = $"{typeTag}  Tick: {tick}  ({ms:F1} ms){focusTag}{heatTag}";

            // Update Variables tab with current field values during animation.
            // Throttled to ~4 Hz so reflection + ListView updates don't stall the UI thread
            // on every tick (especially costly for complex Pulsar/Torch plugin scenes).
            if (!_isScrubbing &&
                (DateTime.UtcNow - _lastVariablesUpdateTime).TotalMilliseconds >= 250)
            {
                _lastVariablesUpdateTime = DateTime.UtcNow;
                UpdateVariablesDuringAnimation(tick);
            }

            // Keep timeline scrubber in sync
            UpdateTimelineScrubber(tick);

            // Append Echo output to Console tab
            if (_animPlayer?.LastOutputLines != null && _animPlayer.LastOutputLines.Count > 0)
                AppendConsoleOutput(_animPlayer.LastOutputLines, tick);

            // Apply code heatmap from per-method timing data
            if (_animPlayer?.LastMethodTimings != null && _animPlayer.LastMethodTimings.Count > 0)
                ApplyCodeHeatmap(_animPlayer.LastMethodTimings);

            // Check conditional breakpoint — pause animation if condition is true
            if (_breakCondition != null && _animPlayer != null && !_animPlayer.IsPaused)
            {
                if (CheckBreakCondition(_animPlayer))
                {
                    _animPlayer.Pause();
                    UpdateAnimButtonStates();
                    _lblAnimTick.Text += "  ⏸ BREAK";
                    SetStatus($"⏸ Break condition hit at tick {tick}: {_breakCondition.Expression}");
                }
            }
        }

        private void OnAnimError(string error)
        {
            SetStatus("Animation error: " + error);
            AppendConsoleError(error, _animPlayer?.CurrentTick ?? -1);
            UpdateAnimButtonStates();
            MessageBox.Show(error, "Animation Error",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>
        /// Builds a key for a sprite based on type and name/text.
        /// Used for focused animation to identify which sprites belong to the focused method.
        /// </summary>
        private static string BuildFocusSpriteKey(SpriteEntry sp)
        {
            // Use type prefix + name only (no position) because:
            // - Focus set is built from isolated execution (default positions)
            // - Animation runs full orchestrator (calculated positions)
            // - Position-based keys would never match
            string prefix = sp.Type == SpriteEntryType.Text ? "T:" : "X:";
            string name = sp.Type == SpriteEntryType.Text ? sp.Text : sp.SpriteName;
            return $"{prefix}{name}";
        }

        /// <summary>
        /// Transfers position/size/color/rotation from executed sprites to matched layout sprites.
        /// Matches by Type + Data (SpriteName or Text). Ensures Execute &amp; Isolate shows
        /// sprites at correct runtime positions even when animation hasn't been run first.
        /// </summary>
        private static void TransferExecutionPositions(HashSet<SpriteEntry> layoutSprites, List<SpriteEntry> execSprites)
        {
            if (layoutSprites == null || layoutSprites.Count == 0 || execSprites == null || execSprites.Count == 0)
                return;

            var usedExec = new HashSet<int>();
            foreach (var layoutSp in layoutSprites)
            {
                for (int ei = 0; ei < execSprites.Count; ei++)
                {
                    if (usedExec.Contains(ei)) continue;
                    var execSp = execSprites[ei];
                    bool typeMatch = layoutSp.Type == execSp.Type
                        && ((layoutSp.Type == SpriteEntryType.Text && layoutSp.Text == execSp.Text)
                         || (layoutSp.Type == SpriteEntryType.Texture && layoutSp.SpriteName == execSp.SpriteName));
                    if (typeMatch)
                    {
                        layoutSp.X = execSp.X;
                        layoutSp.Y = execSp.Y;
                        layoutSp.Width = execSp.Width;
                        layoutSp.Height = execSp.Height;
                        layoutSp.Color = execSp.Color;
                        layoutSp.Rotation = execSp.Rotation;
                        if (layoutSp.Type == SpriteEntryType.Text)
                            layoutSp.Scale = execSp.Scale;
                        usedExec.Add(ei);
                        break;
                    }
                }
            }
        }

        private void OnAnimStopped()
        {
            // Reset per-frame throttle timestamps so the first inspection/scrub
            // after stopping is always immediate.
            _lastVariablesUpdateTime = DateTime.MinValue;
            _lastHeatmapPaintTime    = DateTime.MinValue;

            // Clear focused animation state
            _animFocusCall = null;
            _animFocusSprites = null;
            _canvas.HighlightedSprites = null;

            // Restore pre-animation layout so the canvas returns to its
            // editable state with correct source-tracking and positions.
            if (_animPositionSnapshot != null)
            {
                _layout.Sprites.Clear();
                foreach (var sp in _animPositionSnapshot)
                    _layout.Sprites.Add(sp);
                _animPositionSnapshot = null;
                _canvas.CanvasLayout = _layout;
                RefreshLayerList();
            }

            _lblAnimTick.Text = "Animation";
            UpdateAnimButtonStates();
            SetStatus("Animation stopped.");

            // Clear code heatmap when animation stops
            ClearCodeHeatmap();

            // Keep timeline scrubber visible for post-mortem analysis
            // (history is preserved in the player)
            _isScrubbing = false;
            if (_timelineBar != null)
            {
                AnimationPlayer player = _animPlayer ?? _lastAnimPlayer;
                var history = player?.TickHistory;
                _timelineBar.Visible = history != null && history.Count > 0;
                if (_timelineBar.Visible)
                    SetStatus("Animation stopped.  Use timeline slider to scrub history.");
            }

            // Force garbage collection to immediately reclaim the thousands of
            // temporary SpriteEntry objects created during animation frames.
            // Without this, GC pressure accumulates and causes lingering UI lag.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
        }

        private void EnsureAnimPlayer()
        {
            if (_animPlayer != null) return;
            _animPlayer = new AnimationPlayer(this);
            _animPlayer.FrameRendered  += OnAnimFrame;
            _animPlayer.ErrorOccurred  += OnAnimError;
            _animPlayer.PlaybackStopped += OnAnimStopped;

            // Share with variable inspector for live updates (don't dispose - same instance!)
            _lastAnimPlayer = _animPlayer;
        }

        /// <summary>
        /// Saves the current layout sprites as a position reference for animation.
        /// If the layout has non-reference sprites with valid positions, they are
        /// cloned so the animation can merge frame output with these positions.
        /// </summary>
        private void CaptureAnimPositionSnapshot()
        {
            _animPositionSnapshot = null;
            if (_layout == null || _layout.Sprites.Count == 0) return;

            var snapshot = new List<SpriteEntry>();
            foreach (var sp in _layout.Sprites)
            {
                if (sp.IsReferenceLayout) continue;
                snapshot.Add(sp.CloneValues());
            }

            if (snapshot.Count > 0)
                _animPositionSnapshot = snapshot;
        }

        private void UpdateAnimButtonStates()
        {
            bool playing = _animPlayer?.IsPlaying == true && !(_animPlayer?.IsPaused == true);
            bool paused  = _animPlayer?.IsPaused == true;
            bool active  = _animPlayer?.IsPlaying == true;

            _btnAnimPlay.Enabled  = !playing;
            _btnAnimPause.Enabled = playing || paused;
            _btnAnimStop.Enabled  = active;
            _btnAnimStep.Enabled  = !playing;

            _btnAnimPause.Text = paused ? "▶" : "⏸";
        }

        // ── Animation snippet helpers ─────────────────────────────────────────

        /// <summary>
        /// Tries to locate the enclosing .Add(new MySprite { … }); block for
        /// <paramref name="sprite"/> inside <see cref="_codeBox"/> text.
        /// Returns true and sets <paramref name="blockStart"/>/<paramref name="blockLength"/>
        /// when found; false otherwise (caller should fall back to cursor insertion).
        /// </summary>
        private bool TryFindSpriteBlockInCodeBox(SpriteEntry sprite, out int blockStart, out int blockLength)
        {
            blockStart = 0;
            blockLength = 0;
            if (_codeBox == null || _layout == null) return false;

            string code = _codeBox.Text;
            if (string.IsNullOrEmpty(code)) return false;

            // Data value to search for in code
            string dataValue = sprite.Type == SpriteEntryType.Text ? sprite.Text : sprite.SpriteName;
            if (string.IsNullOrEmpty(dataValue)) return false;
            string escaped = dataValue.Replace("\\", "\\\\").Replace("\"", "\\\"");
            string dataLiteral = "\"" + escaped + "\"";

            // Count which occurrence of this Data value this sprite is among layout sprites
            int targetOcc = 0;
            foreach (var sp in _layout.Sprites)
            {
                if (sp == sprite) break;
                string d = sp.Type == SpriteEntryType.Text ? sp.Text : sp.SpriteName;
                if (d == dataValue) targetOcc++;
            }

            // Find the nth occurrence of the Data literal within a new MySprite block
            int occ = 0;
            int nmsPos = -1;
            int search = 0;
            while (search < code.Length)
            {
                int dPos = code.IndexOf(dataLiteral, search, StringComparison.Ordinal);
                if (dPos < 0) break;

                // Check it's inside a "new MySprite" block by looking backward
                int lookStart = Math.Max(0, dPos - 500);
                int nms = code.LastIndexOf("new MySprite", dPos, dPos - lookStart, StringComparison.Ordinal);
                if (nms >= 0)
                {
                    if (occ == targetOcc) { nmsPos = nms; break; }
                    occ++;
                }
                search = dPos + dataLiteral.Length;
            }

            if (nmsPos < 0) return false;

            // ── Expand backward to include .Add( wrapper ──
            blockStart = nmsPos;
            int lookBackLen = Math.Min(nmsPos, 300);
            if (lookBackLen > 0)
            {
                string before = code.Substring(nmsPos - lookBackLen, lookBackLen);
                int addIdx = before.LastIndexOf(".Add(", StringComparison.Ordinal);
                if (addIdx >= 0)
                {
                    int addAbs = nmsPos - lookBackLen + addIdx;
                    int ls = code.LastIndexOf('\n', addAbs);
                    blockStart = (ls < 0) ? 0 : ls + 1;
                }
            }

            // Include preceding comment lines (// …)
            while (blockStart > 0)
            {
                int sf = blockStart - 2;
                if (sf < 0) break;
                int nlBefore = code.LastIndexOf('\n', sf);
                int prevLineStart = (nlBefore < 0) ? 0 : nlBefore + 1;
                if (prevLineStart >= blockStart) break;
                string prevLine = code.Substring(prevLineStart, blockStart - prevLineStart)
                                      .TrimEnd('\r', '\n');
                if (prevLine.TrimStart().StartsWith("//"))
                    blockStart = prevLineStart;
                else
                    break;
            }

            // ── Expand forward past closing }); ──
            // Find the { after "new MySprite"
            int braceOpen = -1;
            for (int j = nmsPos + 12; j < code.Length && j < nmsPos + 60; j++)
            {
                char c = code[j];
                if (c == '{') { braceOpen = j; break; }
                if (c == '(') break; // constructor syntax — not handled here
                if (!char.IsWhiteSpace(c)) break;
            }
            if (braceOpen < 0) return false;

            int depth = 1;
            int ci = braceOpen + 1;
            while (ci < code.Length && depth > 0)
            {
                if (code[ci] == '{') depth++;
                else if (code[ci] == '}') depth--;
                ci++;
            }
            if (depth != 0) return false;

            // ci is now past the closing }.  Look for );
            int end = ci;
            while (end < code.Length && (code[end] == ' ' || code[end] == '\t')) end++;
            if (end < code.Length && code[end] == ')') end++;
            if (end < code.Length && code[end] == ';') end++;
            if (end < code.Length && code[end] == '\r') end++;
            if (end < code.Length && code[end] == '\n') end++;

            blockLength = end - blockStart;
            return blockLength > 0;
        }

        // ── Copy / Paste keyframe animation ────────────────────────────────────

        private void CopySelectedAnimation()
        {
            var sprite = _canvas.SelectedSprite;
            if (sprite == null) return;

            // If sprite is a group follower, grab the leader's animation
            var src = sprite.KeyframeAnimation;
            if (src == null && !string.IsNullOrEmpty(sprite.AnimationGroupId))
            {
                var leader = FindGroupLeader(sprite.AnimationGroupId);
                src = leader?.KeyframeAnimation;
            }
            src = src ?? AnimationSnippetGenerator.TryParseKeyframed(_codeBox?.Text, sprite.AnimationIndex);
            if (src == null) { SetStatus("No animation to copy"); return; }

            // Deep-clone so edits to the copy don't mutate the original
            _copiedAnimation = new KeyframeAnimationParams
            {
                ListVarName  = src.ListVarName,
                Loop         = src.Loop,
                TargetScript = src.TargetScript,
                Keyframes    = src.Keyframes.Select(k => new Keyframe
                {
                    Tick        = k.Tick,
                    X           = k.X,
                    Y           = k.Y,
                    Width       = k.Width,
                    Height      = k.Height,
                    ColorR      = k.ColorR,
                    ColorG      = k.ColorG,
                    ColorB      = k.ColorB,
                    ColorA      = k.ColorA,
                    Rotation    = k.Rotation,
                    Scale       = k.Scale,
                    EasingToNext = k.EasingToNext,
                }).ToList(),
            };

            SetStatus($"Copied animation ({_copiedAnimation.Keyframes.Count} keyframes)");
        }

        private void PasteAnimationToSelected()
        {
            var sprite = _canvas.SelectedSprite;
            if (sprite == null || _copiedAnimation == null) return;

            PushUndo();

            // Deep-clone again so each paste is independent
            sprite.KeyframeAnimation = new KeyframeAnimationParams
            {
                ListVarName  = _copiedAnimation.ListVarName,
                Loop         = _copiedAnimation.Loop,
                TargetScript = _copiedAnimation.TargetScript,
                Keyframes    = _copiedAnimation.Keyframes.Select(k => new Keyframe
                {
                    Tick        = k.Tick,
                    X           = k.X,
                    Y           = k.Y,
                    Width       = k.Width,
                    Height      = k.Height,
                    ColorR      = k.ColorR,
                    ColorG      = k.ColorG,
                    ColorB      = k.ColorB,
                    ColorA      = k.ColorA,
                    Rotation    = k.Rotation,
                    Scale       = k.Scale,
                    EasingToNext = k.EasingToNext,
                }).ToList(),
            };

            SetStatus($"Pasted animation ({sprite.KeyframeAnimation.Keyframes.Count} keyframes) to '{sprite.DisplayName}'");

            // Auto-generate animation code so canvas playback works immediately
            MergeAnimationCodeIntoPanel(sprite);
        }

        // ── Animation groups ─────────────────────────────────────────────────────

        /// <summary>Finds the group leader (sprite with KeyframeAnimation) for a given group ID.</summary>
        private SpriteEntry FindGroupLeader(string groupId)
        {
            if (string.IsNullOrEmpty(groupId) || _layout == null) return null;
            return _layout.Sprites.FirstOrDefault(s => s.AnimationGroupId == groupId && s.KeyframeAnimation != null);
        }

        /// <summary>Gets all sprites in a group (including leader).</summary>
        private List<SpriteEntry> GetGroupMembers(string groupId)
        {
            if (string.IsNullOrEmpty(groupId) || _layout == null) return new List<SpriteEntry>();
            return _layout.Sprites.Where(s => s.AnimationGroupId == groupId).ToList();
        }

        /// <summary>Gets follower sprites in a group (excluding leader).</summary>
        private List<SpriteEntry> GetGroupFollowers(string groupId)
        {
            if (string.IsNullOrEmpty(groupId) || _layout == null) return new List<SpriteEntry>();
            return _layout.Sprites.Where(s => s.AnimationGroupId == groupId && s.KeyframeAnimation == null).ToList();
        }

        private void CreateAnimationGroup()
        {
            var sprite = _canvas.SelectedSprite;
            if (sprite == null) return;

            // Need animation data to be a leader
            var anim = sprite.KeyframeAnimation
                    ?? AnimationSnippetGenerator.TryParseKeyframed(_codeBox?.Text, sprite.AnimationIndex);
            if (anim == null) { SetStatus("Sprite has no keyframe animation to share"); return; }

            PushUndo();
            sprite.KeyframeAnimation = anim;
            sprite.AnimationGroupId = Guid.NewGuid().ToString("N").Substring(0, 8);

            RefreshLayerList();
            SetStatus($"Created animation group '{sprite.AnimationGroupId}' — right-click other sprites to join");
        }

        private void JoinAnimationGroup(string groupId)
        {
            var sprite = _canvas.SelectedSprite;
            if (sprite == null || string.IsNullOrEmpty(groupId)) return;

            // Warn if the joining sprite has its own keyframe animation
            if (sprite.KeyframeAnimation != null)
            {
                var answer = MessageBox.Show(
                    $"\"{sprite.DisplayName}\" has its own keyframe animation which will be replaced by " +
                    "the group leader's animation.\n\n" +
                    "Other effects (e.g. Color Cycle) will be kept.\n\n" +
                    "To combine keyframes, edit them directly in the Keyframe Editor instead.\n\n" +
                    "Join the group anyway?",
                    "Keyframe Animation Will Be Replaced",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) return;
            }

            PushUndo();
            sprite.AnimationGroupId = groupId;
            // Followers don't own animation data — they reference the leader's
            sprite.KeyframeAnimation = null;
            // Remove any standalone KeyframeEffect (group will supply GroupFollowerEffect)
            sprite.AnimationEffects?.RemoveAll(fx => fx.EffectType == AnimationEffectType.Keyframed);

            var leader = FindGroupLeader(groupId);
            int memberCount = GetGroupMembers(groupId).Count;
            RefreshLayerList();
            SetStatus($"Joined animation group '{groupId}' ({memberCount} sprites, leader: {leader?.DisplayName ?? "?"})");

            // Regenerate animation code to include the new follower
            if (leader != null)
                MergeAnimationCodeIntoPanel(leader);
        }

        private void LeaveAnimationGroup()
        {
            var sprite = _canvas.SelectedSprite;
            if (sprite == null || string.IsNullOrEmpty(sprite.AnimationGroupId)) return;

            PushUndo();
            string oldGroup = sprite.AnimationGroupId;

            // If this sprite is the leader, promote a follower before leaving
            if (sprite.KeyframeAnimation != null)
                PromoteGroupFollowerIfNeeded(sprite);

            sprite.AnimationGroupId = null;

            // If only one member remains, dissolve the group
            var remaining = GetGroupMembers(oldGroup);
            if (remaining.Count == 1)
            {
                var lastMember = remaining[0];
                lastMember.AnimationGroupId = null;
                SetStatus("Animation group dissolved (last member removed)");
                // Regenerate solo animation code for the last remaining member
                if (lastMember.KeyframeAnimation != null)
                    MergeAnimationCodeIntoPanel(lastMember);
            }
            else if (remaining.Count > 0)
            {
                SetStatus($"Left animation group ({remaining.Count} remaining)");
                // Regenerate group code without the departed member
                var newLeader = FindGroupLeader(oldGroup);
                if (newLeader != null)
                    MergeAnimationCodeIntoPanel(newLeader);
            }
            else
            {
                SetStatus("Left animation group");
            }

            RefreshLayerList();
        }

        /// <summary>
        /// When a leader leaves, this preserves the animation on the group by
        /// promoting a follower to leader with a cloned copy of the animation.
        /// Must be called BEFORE clearing the leader's group ID.
        /// </summary>
        private void PromoteGroupFollowerIfNeeded(SpriteEntry departingLeader)
        {
            if (departingLeader?.KeyframeAnimation == null) return;
            string gid = departingLeader.AnimationGroupId;
            if (string.IsNullOrEmpty(gid)) return;

            var followers = GetGroupFollowers(gid);
            if (followers.Count == 0) return;

            // Deep-clone animation to the first follower
            var src = departingLeader.KeyframeAnimation;
            followers[0].KeyframeAnimation = new KeyframeAnimationParams
            {
                ListVarName  = src.ListVarName,
                Loop         = src.Loop,
                TargetScript = src.TargetScript,
                Keyframes    = src.Keyframes.Select(k => new Keyframe
                {
                    Tick         = k.Tick,
                    X            = k.X,
                    Y            = k.Y,
                    Width        = k.Width,
                    Height       = k.Height,
                    ColorR       = k.ColorR,
                    ColorG       = k.ColorG,
                    ColorB       = k.ColorB,
                    ColorA       = k.ColorA,
                    Rotation     = k.Rotation,
                    Scale        = k.Scale,
                    EasingToNext = k.EasingToNext,
                }).ToList(),
            };
        }

        /// <summary>
        /// Generates animation code for the given sprite (or its group) and merges
        /// <summary>
        /// Detects which animation index (1, 2, 3, ...) a sprite uses by scanning its
        /// frame.Add block in the existing code for interpolation variable references
        /// (e.g. 'arot' → index 1, 'arot2' → index 2).
        /// Returns 0 if no animation reference is found (sprite is not yet animated in code).
        /// </summary>
        private int DetectSpriteAnimationIndex(string code, SpriteEntry sprite)
        {
            if (string.IsNullOrEmpty(code)) return 0;

            string block = null;

            if (sprite.SourceStart >= 0 && sprite.SourceStart < code.Length)
            {
                int end = sprite.SourceEnd > sprite.SourceStart ? sprite.SourceEnd : code.Length;
                end = Math.Min(end, code.Length);
                block = code.Substring(sprite.SourceStart, end - sprite.SourceStart);
            }
            else
            {
                // PB/runtime sprites have SourceStart = -1.
                // Fall back: find the frame.Add block whose Data matches this sprite's name/text.
                string dataName = sprite.Type == SpriteEntryType.Text ? sprite.Text : sprite.SpriteName;
                if (!string.IsNullOrEmpty(dataName))
                {
                    // Find all frame.Add(new MySprite blocks and match by Data = "..."
                    var addRx = new System.Text.RegularExpressions.Regex(
                        @"frame\w*\.Add\(new\s+MySprite\s*\{[^}]*\}", System.Text.RegularExpressions.RegexOptions.Singleline);
                    foreach (System.Text.RegularExpressions.Match addMatch in addRx.Matches(code))
                    {
                        string addBlock = addMatch.Value;
                        // Check if Data = "dataName" appears in this block
                        if (addBlock.Contains("\"" + dataName + "\""))
                        {
                            block = addBlock;
                            break;
                        }
                    }
                }
            }

            if (block == null) return 0;

            // Look for interpolation variables: arot, ax, ay, aw, ah, ascl, ar, ag, ab, aa
            // Unsuffixed = index 1. Suffixed (arot2, ax3, etc.) = that number.
            var rx = new System.Text.RegularExpressions.Regex(@"\b(?:arot|ax|ay|aw|ah|ascl|ar|ag|ab|aa)(\d*)(?:_interp)?\b");
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(block))
            {
                string suffix = m.Groups[1].Value;
                return string.IsNullOrEmpty(suffix) ? 1 : int.Parse(suffix);
            }
            return 0;
        }

        /// <summary>
        /// After code execution, auto-labels duplicate sprites (e.g. "SemiCircle #1", "SemiCircle #2")
        /// and assigns AnimationIndex by matching each sprite to its ordinal frame.Add block in the code.
        /// Unique sprite names get no label suffix. Only sets UserLabel if it's currently empty.
        /// </summary>
        private void AutoLabelAndDetectAnimationIndices(string code)
        {
            if (string.IsNullOrEmpty(code) || _layout?.Sprites == null) return;

            // Group sprites by their display key (SpriteName for textures, Text for text sprites)
            var groups = new Dictionary<string, List<SpriteEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var sp in _layout.Sprites)
            {
                string key = sp.Type == SpriteEntryType.Text ? sp.Text : sp.SpriteName;
                if (string.IsNullOrEmpty(key)) continue;
                if (!groups.ContainsKey(key)) groups[key] = new List<SpriteEntry>();
                groups[key].Add(sp);
            }

            // For each group, find matching frame.Add blocks in code order and assign labels + animation indices
            var addRx = new System.Text.RegularExpressions.Regex(
                @"frame\w*\.Add\(new\s+MySprite\s*\{[^}]*\}",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            var interpRx = new System.Text.RegularExpressions.Regex(@"\b(?:arot|ax|ay|aw|ah|ascl|ar|ag|ab|aa)(\d*)(?:_interp)?\b");

            foreach (var kvp in groups)
            {
                string dataName = kvp.Key;
                var sprites = kvp.Value;
                bool hasDuplicates = sprites.Count > 1;

                // Find all frame.Add blocks matching this sprite name, in code order
                var matchingBlocks = new List<string>();
                foreach (System.Text.RegularExpressions.Match addMatch in addRx.Matches(code))
                {
                    if (addMatch.Value.Contains("\"" + dataName + "\""))
                        matchingBlocks.Add(addMatch.Value);
                }

                for (int i = 0; i < sprites.Count; i++)
                {
                    var sp = sprites[i];

                    // Auto-label duplicates: "SemiCircle #1", "SemiCircle #2"
                    if (string.IsNullOrEmpty(sp.UserLabel) && hasDuplicates)
                        sp.UserLabel = $"{dataName} #{i + 1}";

                    // Detect animation index from the matching code block (by ordinal)
                    if (sp.AnimationIndex == 0 && i < matchingBlocks.Count)
                    {
                        foreach (System.Text.RegularExpressions.Match m in interpRx.Matches(matchingBlocks[i]))
                        {
                            string suffix = m.Groups[1].Value;
                            sp.AnimationIndex = string.IsNullOrEmpty(suffix) ? 1 : int.Parse(suffix);
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Generates a keyframed animation snippet for the given sprite and merges
        /// it into the code panel using a 3-tier strategy: block replace → array merge → append.
        /// Now tries the Roslyn-based injector first for clean structural injection.
        /// </summary>
        private void PrepareSpriteForKeyframeInjection(SpriteEntry sprite)
        {
            if (sprite?.KeyframeAnimation == null) return;

            var kfEffect = new KeyframeEffect
            {
                Keyframes = sprite.KeyframeAnimation.Keyframes,
                Loop = sprite.KeyframeAnimation.Loop,
            };
            AddOrReplaceEffect(sprite, kfEffect);

            string leaderGroupId = sprite.AnimationGroupId;
            if (!string.IsNullOrEmpty(leaderGroupId) && _layout != null)
            {
                var kf0 = sprite.KeyframeAnimation.Keyframes.Count > 0
                    ? sprite.KeyframeAnimation.Keyframes[0] : null;
                float leaderBaseX = kf0?.X ?? sprite.X;
                float leaderBaseY = kf0?.Y ?? sprite.Y;
                float leaderBaseW = kf0?.Width ?? sprite.Width;
                float leaderBaseH = kf0?.Height ?? sprite.Height;

                var followers = _layout.Sprites
                    .Where(s => s.AnimationGroupId == leaderGroupId && s != sprite)
                    .ToList();

                foreach (var f in followers)
                {
                    var followerFx = new GroupFollowerEffect
                    {
                        LeaderEffect = kfEffect,
                        LeaderSuffix = "",
                        LeaderBaseX = leaderBaseX,
                        LeaderBaseY = leaderBaseY,
                        LeaderBaseW = leaderBaseW,
                        LeaderBaseH = leaderBaseH,
                        FollowerBaseX = f.X,
                        FollowerBaseY = f.Y,
                        FollowerBaseW = f.Width,
                        FollowerBaseH = f.Height,
                    };
                    AddOrReplaceEffect(f, followerFx);
                }
            }
        }

        private void ApplyAnimationCodeUpdate(string existing, string newCode, string status)
        {
            if (existing == null || newCode == null) return;

            SetCodeText(newCode);
            _codeBoxDirty = true;
            ShowPatchDiff(existing, newCode);
            if (_layout != null)
                _layout.OriginalSourceCode = _codeBox.Text;
            WriteBackToWatchedFile(_codeBox.Text);
            SetStatus(status);
        }

        private void MergeAnimationsIntoPanel(IEnumerable<SpriteEntry> sprites)
        {
            if (_codeBox == null || sprites == null) return;

            var uniqueSprites = sprites
                .Where(s => s?.KeyframeAnimation != null)
                .GroupBy(s => s.Id)
                .Select(g => g.First())
                .ToList();

            if (uniqueSprites.Count == 0) return;

            foreach (var sprite in uniqueSprites)
                PrepareSpriteForKeyframeInjection(sprite);

            string existing = _codeBox.Text;
            bool existingIsComplete = existing.IndexOf("public void Main(", StringComparison.Ordinal) >= 0
                                   || existing.IndexOf("class Program", StringComparison.Ordinal) >= 0;
            bool hasLegacyKeyframeBlock = existing.IndexOf(AnimationSnippetGenerator.FooterMarker, StringComparison.Ordinal) >= 0;

            if (existingIsComplete && !hasLegacyKeyframeBlock)
            {
                var allSprites = _layout?.Sprites?.ToList() ?? new List<SpriteEntry>();
                var injResult = RoslynAnimationInjector.InjectAnimations(existing, allSprites);
                if (injResult.Success && injResult.Code != existing)
                {
                    ApplyAnimationCodeUpdate(existing, injResult.Code,
                        $"✅ Updated {uniqueSprites.Count} animation(s) in code panel");
                    return;
                }
            }

            foreach (var sprite in uniqueSprites)
                MergeAnimationCodeIntoPanel(sprite);

            SetStatus($"✅ Updated {uniqueSprites.Count} animation(s) in code panel");
        }

        private void MergeAnimationCodeIntoPanel(SpriteEntry sprite)
        {
            if (_codeBox == null || sprite?.KeyframeAnimation == null) return;

            // ── Roslyn injector path (preferred) ──
            PrepareSpriteForKeyframeInjection(sprite);

            string existing = _codeBox.Text;

            // A method-body snippet from CodeGenerator.Generate() is NOT a complete program.
            // Roslyn would place fields at file scope and miss Main() for tick insertion.
            // Only use Roslyn injection when the existing code is a complete PB program.
            bool existingIsComplete = existing.IndexOf("public void Main(", StringComparison.Ordinal) >= 0
                                   || existing.IndexOf("class Program", StringComparison.Ordinal) >= 0;
            bool hasLegacyKeyframeBlock = existing.IndexOf(AnimationSnippetGenerator.FooterMarker, StringComparison.Ordinal) >= 0;

            // Pass all sprites so ordinal matching works for duplicates
            var allSprites = _layout?.Sprites?.ToList() ?? new List<SpriteEntry>();

            // Try Roslyn injection (only for complete programs)
            var injResult = RoslynAnimationInjector.InjectAnimations(existing, allSprites);
            if (injResult.Success && injResult.Code != existing && existingIsComplete && !hasLegacyKeyframeBlock)
            {
                ApplyAnimationCodeUpdate(existing, injResult.Code,
                    $"✅ Keyframe animation injected ({injResult.SpritesAnimated} sprite(s))");
                return;
            }

            // ── Legacy merge path (fallback) ──
            System.Diagnostics.Debug.WriteLine(
                $"[MergeAnimationCodeIntoPanel] Roslyn injector {(injResult.Success ? "produced no changes" : "failed: " + injResult.Error)}, falling back to legacy merge");

            var kp = sprite.KeyframeAnimation;
            string groupId = sprite.AnimationGroupId;

            // Compute SourceLineNumber from SourceStart if missing
            // (PB execution sprites have SourceStart but not SourceLineNumber)
            if (sprite.SourceLineNumber <= 0 && sprite.SourceStart >= 0 && _codeBox?.Text != null)
            {
                string src = _codeBox.Text;
                int line = 1;
                for (int ci = 0; ci < sprite.SourceStart && ci < src.Length; ci++)
                    if (src[ci] == '\n') line++;
                sprite.SourceLineNumber = line;
            }

            // ── Determine correct animation index for this sprite ──
            MultiAnimationRegistry.Reset();
            string existingCode = _codeBox.Text ?? "";

            if (sprite.AnimationIndex > 0 && sprite.SourceLineNumber > 0)
            {
                // Sprite already has a stored index from a prior edit — reuse it.
                MultiAnimationRegistry.ReserveExistingIndices(existingCode);
                MultiAnimationRegistry.RegisterAnimationIndex(sprite.SourceLineNumber, sprite.AnimationIndex);
            }
            else if (sprite.SourceLineNumber > 0)
            {
                // First edit: detect which animation (if any) this sprite already uses in code.
                // Check if the sprite's frame.Add block references an interpolation variable
                // tied to an existing kfTick declaration (e.g. 'arot', 'arot2').
                int detectedIndex = DetectSpriteAnimationIndex(existingCode, sprite);
                if (detectedIndex > 0)
                {
                    MultiAnimationRegistry.ReserveExistingIndices(existingCode);
                    MultiAnimationRegistry.RegisterAnimationIndex(sprite.SourceLineNumber, detectedIndex);
                    sprite.AnimationIndex = detectedIndex;
                }
                else
                {
                    // New animation — reserve existing indices so we get the next available suffix.
                    MultiAnimationRegistry.ReserveExistingIndices(existingCode);
                }
            }

            // Generate snippet code (for merging into existing code)
            // Generate COMPLETE compilable program (for standalone / Tier 3)
            string snippetCode;
            string completeCode;

            if (!string.IsNullOrEmpty(groupId))
            {
                var followers = GetGroupFollowers(groupId);
                if (followers.Count > 0)
                {
                    snippetCode = AnimationSnippetGenerator.GenerateKeyframedGroup(sprite, kp, followers);
                    completeCode = AnimationSnippetGenerator.GenerateKeyframedGroupComplete(sprite, kp, followers);
                }
                else
                {
                    snippetCode = AnimationSnippetGenerator.GenerateKeyframed(sprite, kp);
                    completeCode = AnimationSnippetGenerator.GenerateKeyframedComplete(sprite, kp);
                }
            }
            else
            {
                snippetCode = AnimationSnippetGenerator.GenerateKeyframed(sprite, kp);
                completeCode = AnimationSnippetGenerator.GenerateKeyframedComplete(sprite, kp);
            }

            if (string.IsNullOrEmpty(snippetCode)) return;

            // Store the animation index on the sprite for future re-edits
            if (sprite.SourceLineNumber > 0)
                sprite.AnimationIndex = MultiAnimationRegistry.GetAnimationIndex(sprite.SourceLineNumber);

            string newCode = null;
            string finalStatus = "✅ Animation code updated";
            // 'existing' already declared above (Roslyn injector path)

            System.Diagnostics.Debug.WriteLine($"[MergeAnim] START sprite={sprite.SpriteName}, sourceLine={sprite.SourceLineNumber}, existingLen={existing?.Length ?? 0}, snippetLen={snippetCode?.Length ?? 0}, completeLen={completeCode?.Length ?? 0}");

            // Tier 1: targeted block replace (prefer SourceLine marker; optional unique name fallback)
            string targetName = sprite.Type == SpriteEntryType.Text ? sprite.Text : sprite.SpriteName;
            int sourceLine = sprite.SourceLineNumber > 0 ? sprite.SourceLineNumber : -1;

            bool found = false;
            int blockStart = 0;
            int blockLength = 0;

            // Preferred: exact SourceLine marker inside a keyframed block.
            if (sourceLine > 0)
            {
                string marker = "// SourceLine: " + sourceLine;
                int markerIdx = existing.IndexOf(marker, StringComparison.Ordinal);
                if (markerIdx >= 0)
                {
                    int h1 = existing.LastIndexOf("// ─── Keyframe Animation:", markerIdx, StringComparison.Ordinal);
                    int h2 = existing.LastIndexOf("// ─── Animation Group:", markerIdx, StringComparison.Ordinal);
                    int headerIdx = Math.Max(h1, h2);
                    if (headerIdx >= 0)
                    {
                        int lineStart = headerIdx;
                        while (lineStart > 0 && existing[lineStart - 1] != '\n') lineStart--;

                        int footerIdx = existing.IndexOf(AnimationSnippetGenerator.FooterMarker, markerIdx, StringComparison.Ordinal);
                        if (footerIdx >= 0)
                        {
                            int endIdx = footerIdx + AnimationSnippetGenerator.FooterMarker.Length;
                            if (endIdx < existing.Length && existing[endIdx] == '\r') endIdx++;
                            if (endIdx < existing.Length && existing[endIdx] == '\n') endIdx++;
                            blockStart = lineStart;
                            blockLength = endIdx - lineStart;
                            found = blockLength > 0;
                        }
                    }
                }
            }

            // Optional fallback: unique header-name match (only when exactly one match exists).
            if (!found && !string.IsNullOrEmpty(targetName))
            {
                string escName = Regex.Escape(targetName);
                var rx = new Regex("//\\s*─+\\s*(?:Keyframe Animation|Animation Group):\\s*\"" + escName + "\"", RegexOptions.CultureInvariant);
                var matches = rx.Matches(existing);
                if (matches.Count == 1)
                {
                    int headerIdx = matches[0].Index;
                    int lineStart = headerIdx;
                    while (lineStart > 0 && existing[lineStart - 1] != '\n') lineStart--;

                    int footerIdx = existing.IndexOf(AnimationSnippetGenerator.FooterMarker, headerIdx, StringComparison.Ordinal);
                    if (footerIdx >= 0)
                    {
                        int endIdx = footerIdx + AnimationSnippetGenerator.FooterMarker.Length;
                        if (endIdx < existing.Length && existing[endIdx] == '\r') endIdx++;
                        if (endIdx < existing.Length && existing[endIdx] == '\n') endIdx++;
                        blockStart = lineStart;
                        blockLength = endIdx - lineStart;
                        found = blockLength > 0;
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine($"[MergeAnim] Tier1 found={found}, blockStart={blockStart}, blockLen={blockLength}");
            if (found)
            {
                string blockContent = existing.Substring(blockStart, blockLength);
                bool hasFooter = blockContent.Contains(AnimationSnippetGenerator.FooterMarker);

                // If this is a complete program with multiple independent animations
                // (e.g. kfTick AND kfTick2), a full block replace would wipe the other
                // animations. Skip Tier 1 and let Case B (array-value merge) handle it.
                bool hasMultipleAnimations = false;
                if (hasFooter)
                {
                    var rxSuffixes = System.Text.RegularExpressions.Regex.Matches(
                        blockContent, @"(?:int\[\]|int\s*\[\s*\])\s+kfTick(\d*)\s*=");
                    if (rxSuffixes.Count > 1)
                    {
                        hasMultipleAnimations = true;
                        found = false; // Reset so the else block (Case B/C) executes
                        System.Diagnostics.Debug.WriteLine($"[MergeAnim] Tier1 SKIPPED: block has {rxSuffixes.Count} animation suffixes, falling through to Case B");
                    }
                }

                if (!hasMultipleAnimations)
                {
                    // If the existing block has a footer marker, replace with complete program;
                    // otherwise replace with snippet (preserving surrounding code structure)
                    string replacement = hasFooter ? completeCode : snippetCode;
                    newCode = existing.Substring(0, blockStart)
                            + replacement
                            + existing.Substring(blockStart + blockLength);
                }
            }
            else
            {
                // If named keyframed blocks already exist but none matched this sprite,
                // append a new block instead of mutating unrelated animations.
                int anyStart, anyLength;
                bool hasAnyNamedBlock = AnimationSnippetGenerator.FindKeyframedBlockRange(existing, out anyStart, out anyLength);

                // Also guard legacy/manual keyframed code that may not have our generated headers.
                bool hasAnyKeyframeArrays = existing.IndexOf("kfTick", StringComparison.Ordinal) >= 0
                                          && existing.IndexOf("kfEase", StringComparison.Ordinal) >= 0;

                if (hasAnyNamedBlock || hasAnyKeyframeArrays)
                {
                    // Detect if existing code is a complete program (has Main method or footer marker).
                    // Complete programs use different variable names than snippets, so we must
                    // use completeCode replacement rather than snippet-level array merge.
                    bool isCompleteProgram = existing.IndexOf("public void Main(", StringComparison.Ordinal) >= 0
                                         || existing.IndexOf(AnimationSnippetGenerator.FooterMarker, StringComparison.Ordinal) >= 0;

                    if (isCompleteProgram && !string.IsNullOrEmpty(completeCode))
                    {
                        // Three distinct cases for complete programs:
                        //
                        // Case A: GROUP animation (leader + followers).
                        //   The code structure must change (new sprite blocks, delta-based
                        //   interpolation). Array-level merge can't add new frame.Add() blocks,
                        //   so we must regenerate the complete program.
                        //
                        // Case B: SINGLE sprite update (sprite exists in code, no followers).
                        //   Only keyframe array values and counts need updating. Use
                        //   MergeKeyframedIntoCode which swaps values inside { } and
                        //   updates loop counts, preserving all existing variable names.
                        //
                        // Case C: NEW independent animation (sprite not in existing code).
                        //   Inject new arrays as fields and new interpolation + sprite
                        //   block into Main(), keeping all existing code intact.

                        bool hasFollowers = !string.IsNullOrEmpty(groupId)
                            && GetGroupFollowers(groupId).Count > 0;

                        // Check if THIS sprite's animation arrays already exist in the code.
                        // We can't just check the sprite name (it may appear as a static sprite).
                        // Instead, check if the snippet's tick array (e.g. "kfTick2") is declared in existing code.
                        string snippetTickArr = AnimationSnippetGenerator.ExtractTickArrayName(snippetCode);
                        bool animationExistsInCode = !string.IsNullOrEmpty(snippetTickArr)
                            && Regex.IsMatch(existing, @"(?:int\[\]|int\s*\[\s*\])\s+" + Regex.Escape(snippetTickArr) + @"\s*=");

                        System.Diagnostics.Debug.WriteLine($"[MergeAnim] hasFollowers={hasFollowers}, snippetTickArr={snippetTickArr ?? "null"}, animationExistsInCode={animationExistsInCode}, groupId={groupId ?? "null"}, sourceLine={sprite.SourceLineNumber}");

                        string merged = null;

                        if (hasFollowers)
                        {
                            // Case A: group — regenerate entire program with all members
                            System.Diagnostics.Debug.WriteLine("[MergeAnim] → Case A: group replace");
                            newCode = completeCode;
                            finalStatus = "✅ Animation group program generated";
                        }
                        else if (animationExistsInCode)
                        {
                            // Case B: update existing single animation's array values
                            System.Diagnostics.Debug.WriteLine("[MergeAnim] → Case B: array value merge");
                            merged = AnimationSnippetGenerator.MergeKeyframedIntoCode(existing, snippetCode);
                            System.Diagnostics.Debug.WriteLine($"[MergeAnim] MergeKeyframedIntoCode returned {(merged != null ? "success" : "null")}");
                            if (merged != null)
                            {
                                newCode = merged;
                                finalStatus = "✅ Animation arrays updated in existing program";
                            }
                        }

                        if (newCode == null)
                        {
                            // Case C: new independent animation — inject into program
                            System.Diagnostics.Debug.WriteLine("[MergeAnim] → Case C: inject new animation");
                            merged = AnimationSnippetGenerator.MergeSnippetIntoCompleteProgram(existing, snippetCode, sprite.SpriteName ?? sprite.Text);
                            if (merged != null)
                            {
                                newCode = merged;
                                finalStatus = "✅ New animation merged into existing program";
                            }
                            else
                            {
                                // Case D: universal merge engine fallback
                                merged = AnimationMergeEngine.MergeSnippetIntoExistingCode(existing, snippetCode, sprite.SpriteName ?? sprite.Text);
                                if (merged != null)
                                {
                                    newCode = merged;
                                    finalStatus = "✅ Animation merged via universal engine";
                                }
                                else
                                {
                                    newCode = existing;
                                    finalStatus = "Could not safely merge animation into existing program; original code preserved.";
                                }
                            }
                        }
                    }
                    else
                    {
                        // Try smart array-level merge when blocks or arrays exist (snippet-to-snippet).
                        string merged = AnimationSnippetGenerator.MergeKeyframedIntoCode(existing, snippetCode);
                        if (merged != null)
                        {
                            newCode = merged;
                            finalStatus = "✅ Animation arrays merged into existing code";
                        }
                        else
                        {
                            // Merge failed — try universal engine
                            string uniMerged = AnimationMergeEngine.MergeSnippetIntoExistingCode(existing, snippetCode, sprite.SpriteName ?? sprite.Text);
                            if (uniMerged != null)
                            {
                                newCode = uniMerged;
                                finalStatus = "✅ Animation merged via universal engine";
                            }
                            else
                            {
                                newCode = existing;
                                finalStatus = "Could not safely merge this animation into existing keyframe blocks; original code preserved.";
                            }
                        }
                    }
                }
                else
                {
                    // No existing keyframe content detected at all:
                    // try smart merge first for hand-written compatible snippets,
                    // then use completeCode when panel is empty or holds only a
                    // generated method-body snippet (not a complete PB program).
                    string merged = AnimationSnippetGenerator.MergeKeyframedIntoCode(existing, snippetCode);
                    if (merged != null)
                        newCode = merged;
                    else
                        newCode = (string.IsNullOrWhiteSpace(existing) || !existingIsComplete)
                            ? completeCode
                            : existing.TrimEnd() + Environment.NewLine + Environment.NewLine + snippetCode.TrimStart();
                }
            }

            // If Tier 1 was skipped due to multiple animations but the else block
            // didn't run (because `if (found)` was already entered), fall through
            // to Case B/C merge logic here.
            if (newCode == null)
            {
                bool isCompleteProgram = existing.IndexOf("public void Main(", StringComparison.Ordinal) >= 0
                                      || existing.IndexOf(AnimationSnippetGenerator.FooterMarker, StringComparison.Ordinal) >= 0;

                if (isCompleteProgram && !string.IsNullOrEmpty(completeCode))
                {
                    string snippetTickArr = AnimationSnippetGenerator.ExtractTickArrayName(snippetCode);
                    bool animationExistsInCode = !string.IsNullOrEmpty(snippetTickArr)
                        && Regex.IsMatch(existing, @"(?:int\[\]|int\s*\[\s*\])\s+" + Regex.Escape(snippetTickArr) + @"\s*=");

                    System.Diagnostics.Debug.WriteLine($"[MergeAnim] Fallthrough: snippetTickArr={snippetTickArr ?? "null"}, animationExistsInCode={animationExistsInCode}");

                    if (animationExistsInCode)
                    {
                        // Case B: update existing animation's array values
                        string merged = AnimationSnippetGenerator.MergeKeyframedIntoCode(existing, snippetCode);
                        if (merged != null)
                        {
                            newCode = merged;
                            finalStatus = "✅ Animation arrays updated in existing program";
                        }
                    }

                    if (newCode == null)
                    {
                        // Case C: inject new animation into program
                        string merged = AnimationSnippetGenerator.MergeSnippetIntoCompleteProgram(existing, snippetCode, sprite.SpriteName ?? sprite.Text);
                        if (merged != null)
                        {
                            newCode = merged;
                            finalStatus = "✅ New animation merged into existing program";
                        }
                        else
                        {
                            // Universal engine fallback
                            string uniMerged = AnimationMergeEngine.MergeSnippetIntoExistingCode(existing, snippetCode, sprite.SpriteName ?? sprite.Text);
                            if (uniMerged != null)
                            {
                                newCode = uniMerged;
                                finalStatus = "✅ Animation merged via universal engine";
                            }
                            else
                            {
                                newCode = existing;
                                finalStatus = "Could not safely merge animation; original code preserved.";
                            }
                        }
                    }
                }
                else
                {
                    // Non-program fallback
                    string merged = AnimationSnippetGenerator.MergeKeyframedIntoCode(existing, snippetCode);
                    if (merged == null)
                        merged = AnimationMergeEngine.MergeSnippetIntoExistingCode(existing, snippetCode, sprite.SpriteName ?? sprite.Text);
                    newCode = merged ?? existing;
                }
            }

            // SetCodeText handles suppression, highlighting, and dirty-flag reset
            // in one place — avoids the grey-text bug caused by bypassing the
            // TextChanged handler while _suppressCodeBoxEvents is true.
            ApplyAnimationCodeUpdate(existing, newCode, finalStatus);
        }

        // ── Keyframe animation dialog ──────────────────────────────────────────

        private void ShowKeyframeAnimationDialog(bool editExisting = false)
        {
            var sprite = _canvas.SelectedSprite;
            if (sprite == null) { SetStatus("Select a sprite first"); return; }

            // If this sprite is a group follower, redirect to the leader
            SpriteEntry editTarget = sprite;
            if (editExisting && !string.IsNullOrEmpty(sprite.AnimationGroupId) && sprite.KeyframeAnimation == null)
            {
                var leader = FindGroupLeader(sprite.AnimationGroupId);
                if (leader != null)
                {
                    editTarget = leader;
                    SetStatus($"Editing group leader '{leader.DisplayName}' animation");
                }
            }

            // If editing existing but no in-memory data, try parsing from the code panel
            if (editExisting && editTarget.KeyframeAnimation == null)
            {
                // Detect animation index from code if not yet known
                if (editTarget.AnimationIndex == 0 && !string.IsNullOrEmpty(_codeBox?.Text))
                {
                    int detected = DetectSpriteAnimationIndex(_codeBox.Text, editTarget);
                    if (detected > 0)
                        editTarget.AnimationIndex = detected;
                }
                var parsed = AnimationSnippetGenerator.TryParseKeyframed(_codeBox?.Text, editTarget.AnimationIndex);
                if (parsed != null)
                {
                    editTarget.KeyframeAnimation = parsed;
                    SetStatus("Recovered keyframe data from code");
                }
                else
                {
                    SetStatus("No existing animation on this sprite");
                    return;
                }
            }

            // Close any previously open snippet dialog
            if (_snippetDialog != null && !_snippetDialog.IsDisposed)
            {
                _snippetDialog.Close();
                _snippetDialog.Dispose();
                _snippetDialog = null;
            }

            // ── Detect target script type from code style dropdown ──
            var target = MapCodeStyleToTarget();

            // Open the visual keyframe editor.
            // For group followers (sprite != editTarget): pass the originally selected
            // sprite so the dialog title and preview show that sprite's appearance,
            // but supply the leader's existing params so the shared animation is edited.
            var dlg = new KeyframeEditorDialog(sprite, target,
                editExisting ? editTarget.KeyframeAnimation : null, _textureCache);

            // The constructor set sprite.KeyframeAnimation = _params.  If the
            // selected sprite is a follower (not the leader) that side-effect would
            // promote it to leader and break the group — clear it back immediately.
            if (sprite != editTarget)
                sprite.KeyframeAnimation = null;

            _snippetDialog = dlg;

            var capturedTarget = editTarget;

            dlg.CodeUpdateRequested += newCode =>
            {
                if (_codeBox == null || string.IsNullOrEmpty(newCode)) return;
                // MergeAnimationCodeIntoPanel regenerates code from sprite.KeyframeAnimation
                // which was already updated by the dialog. Use it directly:
                MergeAnimationCodeIntoPanel(capturedTarget);
                SetStatus("✅ Animation code updated in code panel");
            };
            dlg.FormClosed += (s2, e2) =>
            {
                if (_snippetDialog == dlg) _snippetDialog = null;
            };
            dlg.Show(this);
        }


        /// <summary>Helper for keyframe dialog float parameter fields.</summary>
        private static void AddKfParamFloat(TableLayoutPanel panel, ref int row,
            string label, float initial, Action<float> onChange)
        {
            var lbl = new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(200, 200, 200),
            };
            var nud = new NumericUpDown
            {
                Dock = DockStyle.Fill,
                Minimum = -9999,
                Maximum = 9999,
                DecimalPlaces = 1,
                Increment = 1m,
                Value = (decimal)Math.Max(-9999, Math.Min(9999, initial)),
                BackColor = Color.FromArgb(50, 50, 55),
                ForeColor = Color.FromArgb(220, 220, 220),
            };
            nud.ValueChanged += (s, e) => onChange((float)nud.Value);
            panel.Controls.Add(lbl, 0, row);
            panel.Controls.Add(nud, 1, row);
            row++;
        }

        /// <summary>Helper for keyframe dialog int parameter fields.</summary>
        private static void AddKfParamInt(TableLayoutPanel panel, ref int row,
            string label, int initial, int min, int max, Action<int> onChange)
        {
            var lbl = new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(200, 200, 200),
            };
            var nud = new NumericUpDown
            {
                Dock = DockStyle.Fill,
                Minimum = min,
                Maximum = max,
                DecimalPlaces = 0,
                Increment = 1,
                Value = Math.Max(min, Math.Min(max, initial)),
                BackColor = Color.FromArgb(50, 50, 55),
                ForeColor = Color.FromArgb(220, 220, 220),
            };
            nud.ValueChanged += (s, e) => onChange((int)nud.Value);
            panel.Controls.Add(lbl, 0, row);
            panel.Controls.Add(nud, 1, row);
            row++;
        }

        // ── Multi-sprite timeline ─────────────────────────────────────────────

        private MultiSpriteTimelineDialog _multiTimelineDlg;

        private int RecoverKeyframedAnimationsFromCode()
        {
            if (_layout?.Sprites == null || string.IsNullOrEmpty(_codeBox?.Text)) return 0;

            AutoLabelAndDetectAnimationIndices(_codeBox.Text);

            int recovered = 0;
            foreach (var sprite in _layout.Sprites)
            {
                if (sprite == null || sprite.KeyframeAnimation != null) continue;

                if (sprite.AnimationIndex == 0)
                {
                    int detected = DetectSpriteAnimationIndex(_codeBox.Text, sprite);
                    if (detected > 0)
                        sprite.AnimationIndex = detected;
                }

                if (sprite.AnimationIndex == 0) continue;

                var parsed = AnimationSnippetGenerator.TryParseKeyframed(_codeBox.Text, sprite.AnimationIndex);
                if (parsed == null) continue;

                sprite.KeyframeAnimation = parsed;
                recovered++;
            }

            return recovered;
        }

        internal void OpenMultiSpriteTimeline()
        {
            int recovered = RecoverKeyframedAnimationsFromCode();
            var animated = _layout?.Sprites?.Where(s => s.KeyframeAnimation != null).ToList();
            if (animated == null || animated.Count == 0)
            {
                SetStatus("No animated sprites found — right-click a sprite → Edit Animation… to create one");
                return;
            }

            if (_multiTimelineDlg != null && !_multiTimelineDlg.IsDisposed)
            {
                _multiTimelineDlg.BringToFront();
                return;
            }

            var dlg = new MultiSpriteTimelineDialog(_layout.Sprites, MapCodeStyleToTarget());

            dlg.UpdateCodeRequested += sprites =>
            {
                MergeAnimationsIntoPanel(sprites);
            };

            dlg.FormClosed += (s, e) => _multiTimelineDlg = null;
            _multiTimelineDlg = dlg;
            dlg.Show(this);
            SetStatus(recovered > 0
                ? $"Multi-sprite timeline opened — recovered {recovered} animation(s) from code"
                : $"Multi-sprite timeline opened — {animated.Count} animated sprite(s)");
        }

        // ── Animation snippet dialog ──────────────────────────────────────────
        private Form _snippetDialog;

        /// <summary>Maps the _cmbCodeStyle dropdown selection to a TargetScriptType.</summary>
        private TargetScriptType MapCodeStyleToTarget()
        {
            switch (_cmbCodeStyle.SelectedIndex)
            {
                case 0:  return TargetScriptType.ProgrammableBlock;  // In-Game (PB)
                case 1:  return TargetScriptType.Mod;
                case 2:  return TargetScriptType.Plugin;             // Plugin / Torch
                case 3:  return TargetScriptType.Pulsar;
                default: return TargetScriptType.LcdHelper;
            }
        }

        private void ShowAnimationSnippetDialog(AnimationType animType)
        {
            var sprite = _canvas.SelectedSprite;
            if (sprite == null) { SetStatus("Select a sprite first"); return; }

            // Close any previously open snippet dialog
            if (_snippetDialog != null && !_snippetDialog.IsDisposed)
            {
                var old = _snippetDialog;
                _snippetDialog = null;
                old.Close();
                old.Dispose();
            }

            var p = new AnimationSnippetGenerator.AnimationParams();

            // ── Detect target script type from code style dropdown ──
            var target = MapCodeStyleToTarget();
            p.TargetScript = target;
            bool isPbOrPlugin = target != TargetScriptType.LcdHelper;
            if (isPbOrPlugin) p.ListVarName = "frame";

            // ── If the code panel already has a matching simple animation block,
            //    parse it back and pre-populate p so the user sees their current settings ──
            if (_codeBox != null && !string.IsNullOrEmpty(_codeBox.Text))
            {
                AnimationType parsedType;
                var parsedParams = AnimationSnippetGenerator.TryParseSimpleAnim(_codeBox.Text, out parsedType);
                if (parsedParams != null && parsedType == animType)
                {
                    p = parsedParams;
                    // Keep target in sync with dropdown (user may have changed it)
                    p.TargetScript = target;
                }
            }

            string targetLabel = target == TargetScriptType.LcdHelper ? "LCD Helper"
                : target == TargetScriptType.ProgrammableBlock ? "PB"
                : target == TargetScriptType.Mod ? "Mod"
                : target == TargetScriptType.Plugin ? "Plugin"
                : "Pulsar";

            var dlg = new Form();
            _snippetDialog = dlg;

            dlg.Text = $"Add Animation — {animType} [{targetLabel}]";
            dlg.Size = new Size(640, 560);
            dlg.MinimumSize = new Size(500, 400);
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.BackColor = Color.FromArgb(30, 30, 30);
            dlg.ForeColor = Color.FromArgb(220, 220, 220);
            dlg.Font = new Font("Segoe UI", 9f);
            dlg.FormClosed += (s2, e2) =>
            {
                if (_snippetDialog == dlg) _snippetDialog = null;
                dlg.Dispose();
            };

            // ── Header label ──
            var lblHeader = new Label
            {
                Dock = DockStyle.Top,
                Height = 36,
                Padding = new Padding(8, 8, 8, 0),
                Text = $"Animation: {animType}  |  Sprite: \"{sprite.DisplayName}\"  |  Target: {targetLabel}",
                ForeColor = Color.FromArgb(180, 200, 255),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            };

            // ── Parameter panel ──
            var pnlParams = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                Padding = new Padding(8, 4, 8, 4),
            };
            pnlParams.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            pnlParams.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // ── Code preview ──
            var txtCode = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BackColor = Color.FromArgb(20, 20, 20),
                ForeColor = Color.FromArgb(200, 220, 200),
                Font = new Font("Consolas", 9.5f),
                MaxLength = 0,
            };

            // Refresh code preview helper
            Action refreshPreview = () =>
            {
                txtCode.Text = AnimationSnippetGenerator.Generate(sprite, animType, p);
            };

            // ── Build parameter controls based on animation type ──
            int row = 0;

            // List variable name selector (shared across all types)
            int listVarDefault = isPbOrPlugin ? 1 : 0;
            AddParamCombo(pnlParams, ref row, "List variable:",
                new[] { "sprites", "frame" }, listVarDefault,
                v => { p.ListVarName = v == 0 ? "sprites" : "frame"; refreshPreview(); });

            switch (animType)
            {
                case AnimationType.Rotate:
                    AddParamFloat(pnlParams, ref row, "Speed (rad/tick):", p.RotateSpeed,
                        v => { p.RotateSpeed = v; refreshPreview(); });
                    AddParamCheckbox(pnlParams, ref row, "Clockwise:", p.Clockwise,
                        v => { p.Clockwise = v; refreshPreview(); });
                    break;

                case AnimationType.Oscillate:
                    AddParamCombo(pnlParams, ref row, "Axis:",
                        new[] { "X", "Y", "Both" }, (int)p.Axis,
                        v => { p.Axis = (OscillateAxis)v; refreshPreview(); });
                    AddParamFloat(pnlParams, ref row, "Amplitude (px):", p.OscillateAmplitude,
                        v => { p.OscillateAmplitude = v; refreshPreview(); });
                    AddParamFloat(pnlParams, ref row, "Speed:", p.OscillateSpeed,
                        v => { p.OscillateSpeed = v; refreshPreview(); });
                    break;

                case AnimationType.Pulse:
                    AddParamFloat(pnlParams, ref row, "Amplitude (±):", p.PulseAmplitude,
                        v => { p.PulseAmplitude = v; refreshPreview(); });
                    AddParamFloat(pnlParams, ref row, "Speed:", p.PulseSpeed,
                        v => { p.PulseSpeed = v; refreshPreview(); });
                    break;

                case AnimationType.Fade:
                    AddParamInt(pnlParams, ref row, "Min Alpha:", p.FadeMinAlpha, 0, 255,
                        v => { p.FadeMinAlpha = v; refreshPreview(); });
                    AddParamInt(pnlParams, ref row, "Max Alpha:", p.FadeMaxAlpha, 0, 255,
                        v => { p.FadeMaxAlpha = v; refreshPreview(); });
                    AddParamFloat(pnlParams, ref row, "Speed:", p.FadeSpeed,
                        v => { p.FadeSpeed = v; refreshPreview(); });
                    break;

                case AnimationType.Blink:
                    AddParamInt(pnlParams, ref row, "On (ticks):", p.BlinkOnTicks, 1, 600,
                        v => { p.BlinkOnTicks = v; refreshPreview(); });
                    AddParamInt(pnlParams, ref row, "Off (ticks):", p.BlinkOffTicks, 1, 600,
                        v => { p.BlinkOffTicks = v; refreshPreview(); });
                    break;

                case AnimationType.ColorCycle:
                    AddParamFloat(pnlParams, ref row, "Speed (°/tick):", p.CycleSpeed,
                        v => { p.CycleSpeed = v; refreshPreview(); });
                    AddParamFloat(pnlParams, ref row, "Brightness:", p.CycleBrightness,
                        v => { p.CycleBrightness = v; refreshPreview(); });
                    break;
            }

            // ── Bottom toolbar ──
            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 38,
                FlowDirection = FlowDirection.RightToLeft,
                BackColor = Color.FromArgb(30, 30, 30),
                Padding = new Padding(4, 4, 8, 4),
            };

            var btnClose = DarkButton("Close", Color.FromArgb(70, 70, 70));
            btnClose.Width = 80;
            btnClose.Click += (s, e) => dlg.Close();

            var btnCopy = DarkButton("\uD83D\uDCCB Copy to Clipboard", Color.FromArgb(0, 100, 180));
            btnCopy.Width = 180;
            btnCopy.Click += (s, e) =>
            {
                Clipboard.SetText(txtCode.Text);
                SetStatus("Animation snippet copied to clipboard");
            };

            var btnApply = DarkButton("▶ Apply to Code", Color.FromArgb(0, 130, 80));
            btnApply.Width = 150;
            btnApply.Click += (s, e) =>
            {
                if (_codeBox == null) return;

                string existing = _codeBox.Text;

                // Build the animation effect from dialog parameters
                IAnimationEffect effect = BuildEffectFromParams(animType, p);
                if (effect == null) return;

                // Add or replace this effect type on the sprite
                AddOrReplaceEffect(sprite, effect);

                // Pass all sprites so ordinal matching works for duplicates
                var allSprites = _layout?.Sprites?.ToList() ?? new List<SpriteEntry>();

                // Roslyn needs a complete PB program. If the panel is empty or only
                // holds a generated method-body snippet, build the complete program first.
                bool simpleExistingIsComplete = existing.IndexOf("public void Main(", StringComparison.Ordinal) >= 0
                                            || existing.IndexOf("class Program", StringComparison.Ordinal) >= 0;
                bool simpleHasLegacyKeyframeBlock = existing.IndexOf(AnimationSnippetGenerator.FooterMarker, StringComparison.Ordinal) >= 0;
                string codeToInject = existing;
                if (string.IsNullOrWhiteSpace(existing) || !simpleExistingIsComplete)
                {
                    codeToInject = AnimationSnippetGenerator.GenerateSimpleComplete(sprite, animType, p);
                }

                // Legacy keyframe blocks already contain keyframe vars/compute code.
                // When applying simple effects (e.g. color cycle), exclude keyframe/group
                // effects from Roslyn injection to avoid duplicating kfTick/kfEase vars and
                // segment locals in Main().
                IEnumerable<SpriteEntry> injectionSprites = allSprites;
                if (simpleHasLegacyKeyframeBlock)
                {
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

                // Inject all animations via Roslyn
                var result = RoslynAnimationInjector.InjectAnimations(codeToInject, injectionSprites);
                string newCode;

                if (result.Success)
                {
                    newCode = result.Code;
                    SetStatus($"Animation applied ({result.SpritesAnimated} sprite(s) animated)");
                }
                else
                {
                    // Fallback: generate complete standalone program
                    if (string.IsNullOrWhiteSpace(existing) || !simpleExistingIsComplete)
                    {
                        newCode = AnimationSnippetGenerator.GenerateSimpleComplete(sprite, animType, p);
                        SetStatus("Animation applied (standalone program)");
                    }
                    else
                    {
                        SetStatus($"Could not inject animation: {result.Error}");
                        return;
                    }
                }

                SetCodeText(newCode);
                _codeBoxDirty = true;   // Protect animation code from RefreshCode overwrite
                ShowPatchDiff(existing, newCode);

                if (_layout != null)
                    _layout.OriginalSourceCode = _codeBox.Text;

                WriteBackToWatchedFile(_codeBox.Text);

                dlg.Close();
            };

            toolbar.Controls.Add(btnClose);
            toolbar.Controls.Add(btnCopy);
            toolbar.Controls.Add(btnApply);

            // ── Separator label ──
            var lblCodeHeader = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                Text = "   Code Preview:",
                ForeColor = Color.FromArgb(140, 160, 180),
                Font = new Font("Segoe UI", 8.5f),
                Padding = new Padding(8, 6, 0, 0),
            };

            // ── Assemble layout (add in reverse dock order) ──
            dlg.Controls.Add(txtCode);        // Fill
            dlg.Controls.Add(lblCodeHeader);   // Top (below params)
            dlg.Controls.Add(pnlParams);       // Top (below header)
            dlg.Controls.Add(lblHeader);       // Top
            dlg.Controls.Add(toolbar);          // Bottom

            // Initial preview
            refreshPreview();

            dlg.Show(this);
        }

        // ── Roslyn injector helpers ──────────────────────────────────────────────

        /// <summary>
        /// Builds an <see cref="IAnimationEffect"/> from the snippet dialog parameters.
        /// </summary>
        private static IAnimationEffect BuildEffectFromParams(AnimationType animType, AnimationSnippetGenerator.AnimationParams p)
        {
            switch (animType)
            {
                case AnimationType.Rotate:
                    return new RotateEffect { Speed = p.RotateSpeed, Clockwise = p.Clockwise };
                case AnimationType.Oscillate:
                    return new OscillateEffect
                    {
                        OscAxis = (OscillateEffect.Axis)(int)p.Axis,
                        Amplitude = p.OscillateAmplitude,
                        Speed = p.OscillateSpeed,
                    };
                case AnimationType.Pulse:
                    return new PulseEffect { Amplitude = p.PulseAmplitude, Speed = p.PulseSpeed };
                case AnimationType.Fade:
                    return new FadeEffect { MinAlpha = p.FadeMinAlpha, MaxAlpha = p.FadeMaxAlpha, Speed = p.FadeSpeed };
                case AnimationType.Blink:
                    return new BlinkEffect { OnTicks = p.BlinkOnTicks, OffTicks = p.BlinkOffTicks };
                case AnimationType.ColorCycle:
                    return new ColorCycleEffect { Speed = p.CycleSpeed, Brightness = p.CycleBrightness };
                default:
                    return null;
            }
        }

        /// <summary>
        /// Adds an effect to a sprite, replacing any existing effect of the same type.
        /// This allows stacking different effects (Rotate + ColorCycle) while
        /// updating existing ones (re-applying Rotate replaces the old Rotate).
        /// </summary>
        private static void AddOrReplaceEffect(SpriteEntry sprite, IAnimationEffect effect)
        {
            if (sprite.AnimationEffects == null)
                sprite.AnimationEffects = new List<IAnimationEffect>();

            // Remove any existing effect of the same type
            sprite.AnimationEffects.RemoveAll(fx => fx.EffectType == effect.EffectType);
            sprite.AnimationEffects.Add(effect);
        }

        // ── Dialog parameter helpers ─────────────────────────────────────────────

        private static void AddParamFloat(TableLayoutPanel panel, ref int row,
            string label, float initial, Action<float> onChange)
        {
            var lbl = new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(200, 200, 200),
            };

            var nud = new NumericUpDown
            {
                Dock = DockStyle.Fill,
                Minimum = -9999,
                Maximum = 9999,
                DecimalPlaces = 3,
                Increment = 0.01m,
                Value = (decimal)initial,
                BackColor = Color.FromArgb(50, 50, 55),
                ForeColor = Color.FromArgb(220, 220, 220),
            };
            nud.ValueChanged += (s, e) => onChange((float)nud.Value);
            nud.KeyDown += SuppressEnterBeep;

            panel.Controls.Add(lbl, 0, row);
            panel.Controls.Add(nud, 1, row);
            row++;
        }

        private static void AddParamInt(TableLayoutPanel panel, ref int row,
            string label, int initial, int min, int max, Action<int> onChange)
        {
            var lbl = new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(200, 200, 200),
            };

            var nud = new NumericUpDown
            {
                Dock = DockStyle.Fill,
                Minimum = min,
                Maximum = max,
                DecimalPlaces = 0,
                Increment = 1,
                Value = initial,
                BackColor = Color.FromArgb(50, 50, 55),
                ForeColor = Color.FromArgb(220, 220, 220),
            };
            nud.ValueChanged += (s, e) => onChange((int)nud.Value);
            nud.KeyDown += SuppressEnterBeep;

            panel.Controls.Add(lbl, 0, row);
            panel.Controls.Add(nud, 1, row);
            row++;
        }

        private static void AddParamCheckbox(TableLayoutPanel panel, ref int row,
            string label, bool initial, Action<bool> onChange)
        {
            var lbl = new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(200, 200, 200),
            };

            var chk = new CheckBox
            {
                Dock = DockStyle.Fill,
                Checked = initial,
                ForeColor = Color.FromArgb(220, 220, 220),
            };
            chk.CheckedChanged += (s, e) => onChange(chk.Checked);

            panel.Controls.Add(lbl, 0, row);
            panel.Controls.Add(chk, 1, row);
            row++;
        }

        private static void AddParamCombo(TableLayoutPanel panel, ref int row,
            string label, string[] items, int selectedIndex, Action<int> onChange)
        {
            var lbl = new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(200, 200, 200),
            };

            var cmb = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(50, 50, 55),
                ForeColor = Color.FromArgb(220, 220, 220),
            };
            cmb.Items.AddRange(items);
            cmb.SelectedIndex = selectedIndex;
            cmb.SelectedIndexChanged += (s, e) => onChange(cmb.SelectedIndex);

            panel.Controls.Add(lbl, 0, row);
            panel.Controls.Add(cmb, 1, row);
            row++;
        }

    }
}
