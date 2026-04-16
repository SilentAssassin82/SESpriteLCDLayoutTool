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

            // Update Variables tab with current field values during animation
            if (!_isScrubbing)
                UpdateVariablesDuringAnimation(tick);

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
            src = src ?? AnimationSnippetGenerator.TryParseKeyframed(_codeBox?.Text);
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
                    ?? AnimationSnippetGenerator.TryParseKeyframed(_codeBox?.Text);
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

            PushUndo();
            sprite.AnimationGroupId = groupId;
            // Followers don't own animation data — they reference the leader's
            sprite.KeyframeAnimation = null;

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
        /// it into the code panel using a 3-tier strategy: block replace → array merge → append.
        /// </summary>
        private void MergeAnimationCodeIntoPanel(SpriteEntry sprite)
        {
            if (_codeBox == null || sprite?.KeyframeAnimation == null) return;

            var kp = sprite.KeyframeAnimation;
            string groupId = sprite.AnimationGroupId;

            // Generate snippet code (for merging into existing code)
            string snippetCode;
            // Generate COMPLETE compilable program (for standalone / Tier 3)
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

            string existing = _codeBox.Text;
            string newCode = null;

            // Tier 1: Exact block replace (handles both snippet-only and complete program blocks)
            if (AnimationSnippetGenerator.FindKeyframedBlockRange(existing,
                    out int blockStart, out int blockLength))
            {
                // If the existing block has a footer marker, replace with complete program;
                // otherwise replace with snippet (preserving surrounding code structure)
                string replacement = existing.Substring(blockStart, blockLength)
                    .Contains(AnimationSnippetGenerator.FooterMarker)
                    ? completeCode
                    : snippetCode;
                newCode = existing.Substring(0, blockStart)
                        + replacement
                        + existing.Substring(blockStart + blockLength);
            }
            else
            {
                // Tier 2: Smart array-level merge (existing code has kfTick arrays)
                string merged = AnimationSnippetGenerator.MergeKeyframedIntoCode(existing, snippetCode);
                // Tier 3: No existing animation code — use complete compilable program
                newCode = merged ?? completeCode;
            }

            // SetCodeText handles suppression, highlighting, and dirty-flag reset
            // in one place — avoids the grey-text bug caused by bypassing the
            // TextChanged handler while _suppressCodeBoxEvents is true.
            SetCodeText(newCode);
            _codeBoxDirty = false;

            // Sync OriginalSourceCode so the Play button uses the updated code
            if (_layout != null)
                _layout.OriginalSourceCode = _codeBox.Text;

            // Write the updated code back to the watched file (script sync) so
            // the external diff/editor sees the change immediately.
            WriteBackToWatchedFile(_codeBox.Text);
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
                var parsed = AnimationSnippetGenerator.TryParseKeyframed(_codeBox?.Text);
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

            // Open the visual keyframe editor (pass existing params if editing)
            var dlg = new KeyframeEditorDialog(editTarget, target,
                editExisting ? editTarget.KeyframeAnimation : null, _textureCache);
            _snippetDialog = dlg;

            var capturedTarget = editTarget;

            dlg.CodeUpdateRequested += newCode =>
            {
                if (_codeBox == null || string.IsNullOrEmpty(newCode)) return;
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
                _snippetDialog.Close();
                _snippetDialog.Dispose();
                _snippetDialog = null;
            }

            var p = new AnimationSnippetGenerator.AnimationParams();

            // ── Detect target script type from code style dropdown ──
            var target = MapCodeStyleToTarget();
            p.TargetScript = target;
            bool isPbOrPlugin = target != TargetScriptType.LcdHelper;
            if (isPbOrPlugin) p.ListVarName = "frame";

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

            // Auto-find: check whether we can locate the sprite's block in the code editor
            int autoStart, autoLen;
            bool canReplace = TryFindSpriteBlockInCodeBox(sprite, out autoStart, out autoLen);

            var btnInsert = DarkButton(
                canReplace ? "📥 Replace in Code" : "📥 Insert at Cursor",
                Color.FromArgb(0, 130, 80));
            btnInsert.Width = canReplace ? 170 : 160;
            btnInsert.Click += (s, e) =>
            {
                if (_codeBox == null) return;

                _codeBox.Focus();

                // Try auto-selecting the sprite's existing code block
                int bs, bl;
                if (TryFindSpriteBlockInCodeBox(sprite, out bs, out bl))
                {
                    _codeBox.SelectionStart = bs;
                    _codeBox.SelectionLength = bl;
                }

                // Replace the selection (or insert at cursor if nothing was selected)
                _suppressCodeBoxEvents = true;
                try { _codeBox.SelectedText = txtCode.Text; }
                finally { _suppressCodeBoxEvents = false; }

                SetStatus(bl > 0
                    ? "Animation snippet replaced sprite code"
                    : "Animation snippet inserted at cursor");
            };

            toolbar.Controls.Add(btnClose);
            toolbar.Controls.Add(btnCopy);
            toolbar.Controls.Add(btnInsert);

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
