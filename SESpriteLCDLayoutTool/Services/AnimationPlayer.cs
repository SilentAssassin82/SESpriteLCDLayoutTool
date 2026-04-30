using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;
using SESpriteLCDLayoutTool.Models;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Manages animated playback of user scripts by compiling once
    /// and executing the entry point repeatedly on a timer, preserving instance
    /// state (fields, counters, Storage) across ticks.
    /// </summary>
    public sealed class AnimationPlayer : IDisposable
    {
        // ── Events (fired on UI thread) ────────────────────────────────────────
        /// <summary>Fired after each frame.  Args: captured sprites, tick number.</summary>
        public event Action<List<SpriteEntry>, int> FrameRendered;
        /// <summary>Fired when a frame execution fails.</summary>
        public event Action<string> ErrorOccurred;
        /// <summary>Fired when playback stops (manually or due to error).</summary>
        public event Action PlaybackStopped;

        // ── Properties ─────────────────────────────────────────────────────────
        public bool IsPlaying { get; private set; }
        public bool IsPaused { get; private set; }
        public int CurrentTick { get; private set; }

        /// <summary>The script type of the currently prepared animation.</summary>
        public ScriptType ScriptType { get; private set; }

        /// <summary>Execution time of the last frame in milliseconds.</summary>
        public double LastFrameMs { get; private set; }

        /// <summary>Echo/output lines captured from the most recent frame execution.</summary>
        public List<string> LastOutputLines { get; private set; }

        /// <summary>Per-method timing data from the most recent frame. Key = method name, Value = elapsed ms.</summary>
        public Dictionary<string, double> LastMethodTimings { get; private set; }

        /// <summary>
        /// Fixed fallback interval in ms when the script does not set
        /// <c>Runtime.UpdateFrequency</c>.  Default is 166 ms (≈ Update10).
        /// </summary>
        public int FallbackIntervalMs { get; set; } = 166;

        /// <summary>
        /// When true, the player skips the per-tick history snapshot (field
        /// reflection + sprite copy) and the FrameRendered side-effects in the
        /// async timer path are minimised.  Used by the GIF exporter to keep
        /// frame capture as fast as possible.
        /// </summary>
        public bool FastCaptureMode { get; set; }

        // ── Internal state ─────────────────────────────────────────────────────
        private CodeExecutor.AnimationContext _ctx;
        private Timer _timer;
        private readonly Control _syncControl;
        private DateTime _lastFrameTime;
        private int _currentUpdateType = 32; // UpdateType.Update10
        private bool _frameInProgress;
        private System.Reflection.FieldInfo[] _cachedFields;
        private DateTime _lastSnapshotTime;

        // ── Tick history for timeline scrubber
        private TickHistoryBuffer _tickHistory = new TickHistoryBuffer(500);

        /// <summary>
        /// Ring buffer of per-tick field snapshots.  The timeline scrubber
        /// reads this to show historical variable values.
        /// </summary>
        public TickHistoryBuffer TickHistory => _tickHistory;

        // ── Sprite history for snapshot comparison ────────────────────────────
        private SpriteHistoryBuffer _spriteHistory = new SpriteHistoryBuffer(500);

        /// <summary>
        /// Ring buffer of per-tick sprite snapshots.  The Snapshot Comparison
        /// feature reads this to diff sprite states between bookmarked ticks.
        /// </summary>
        public SpriteHistoryBuffer SpriteHistory => _spriteHistory;

        // ── Constructor ────────────────────────────────────────────────────────
        /// <param name="syncControl">
        /// A control on the UI thread used for <see cref="Control.BeginInvoke"/>
        /// when marshalling frame results back from background threads.
        /// </param>
        public AnimationPlayer(Control syncControl)
        {
            _syncControl = syncControl ?? throw new ArgumentNullException(nameof(syncControl));
            _timer = new Timer();
            _timer.Interval = FallbackIntervalMs;
            _timer.Tick += OnTimerTick;
        }

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Compiles and initialises the script for animated playback.
        /// Returns <c>null</c> on success, or an error message on failure.
        /// </summary>
        /// <param name="callExpression">
        /// Required for LCD Helper and Mod/Surface scripts (e.g. <c>DrawHUD(surface)</c>).
        /// Ignored for Programmable Block scripts.
        /// </param>
        /// <param name="capturedRows">
        /// Optional runtime snapshot data (from live capture or in-game file).
        /// Passed to render methods so they can display real game values.
        /// </param>
        public string Prepare(string userCode, string callExpression = null, List<SnapshotRowData> capturedRows = null)
        {
            Stop();
            _tickHistory.Clear();
            _spriteHistory.Clear();

            try
            {
                _ctx = CodeExecutor.CompileForAnimation(userCode, callExpression, capturedRows);
            }
            catch (Exception ex)
            {
                return "Compilation failed:\n" + ex.Message;
            }

            try
            {
                CodeExecutor.InitAnimation(_ctx);
            }
            catch (Exception ex)
            {
                _ctx?.Dispose();
                _ctx = null;
                return "Initialization failed:\n" + ex.Message;
            }

            ScriptType = _ctx.ScriptType;
            CurrentTick = 0;
            UpdateTimerInterval();
            return null;
        }

        /// <summary>
        /// Adopts a pre-compiled and already-initialised <see cref="CodeExecutor.AnimationContext"/>
        /// so the player can inspect fields and run additional frames without a second compilation.
        /// The player takes ownership of the context and will dispose it on <see cref="Stop"/>.
        /// </summary>
        public void AdoptContext(CodeExecutor.AnimationContext ctx)
        {
            Stop();
            _tickHistory.Clear();
            _spriteHistory.Clear();

            _ctx = ctx;
            ScriptType = ctx.ScriptType;
            CurrentTick = 0;
            UpdateTimerInterval();
        }

        /// <summary>Starts or resumes playback.</summary>
        public void Play()
        {
            if (_ctx == null) return;

            IsPaused = false;
            IsPlaying = true;
            _lastFrameTime = DateTime.UtcNow;
            _timer.Start();
        }

        /// <summary>Pauses playback (state is preserved).</summary>
        public void Pause()
        {
            _timer.Stop();
            IsPaused = true;
        }

        /// <summary>Stops playback and discards the compiled session.</summary>
        public void Stop()
        {
            _timer.Stop();
            _frameInProgress = false;
            bool wasPlaying = IsPlaying;
            IsPlaying = false;
            IsPaused = false;
            CurrentTick = 0;
            _ctx?.Dispose();
            _ctx = null;
            _cachedFields = null;
            // Note: do NOT clear _tickHistory here — it's needed for
            // post-mortem scrubbing after animation stops.
            if (wasPlaying) PlaybackStopped?.Invoke();
        }

        /// <summary>Executes a single frame (synchronously) and stays paused.</summary>
        public void StepForward()
        {
            StepForward(0.0);
        }

        /// <summary>
        /// Executes a single frame (synchronously) and stays paused.  When
        /// <paramref name="forcedElapsedSeconds"/> is greater than zero, that
        /// value is passed to the script in place of the wall-clock delta — used
        /// by GIF capture so the GIF's encoded frame rate matches the in-script
        /// time progression regardless of how fast frames are actually rendered.
        /// </summary>
        public void StepForward(double forcedElapsedSeconds)
        {
            if (_ctx == null) return;

            IsPlaying = true;
            IsPaused = true;
            _timer.Stop();

            var now = DateTime.UtcNow;
            double elapsed;
            if (forcedElapsedSeconds > 0.0)
            {
                elapsed = forcedElapsedSeconds;
            }
            else
            {
                elapsed = _lastFrameTime == default(DateTime)
                    ? 1.0 / 60.0   // First frame: assume ~60fps
                    : (now - _lastFrameTime).TotalSeconds;
            }
            _lastFrameTime = now;
            CurrentTick++;

            var sw = Stopwatch.StartNew();
            var result = CodeExecutor.RunAnimationFrame(_ctx, _currentUpdateType, CurrentTick, elapsed);
            sw.Stop();
            LastFrameMs = sw.Elapsed.TotalMilliseconds;

            if (!result.Success)
            {
                Stop();
                ErrorOccurred?.Invoke(result.Error);
                return;
            }

            LastOutputLines = result.OutputLines;
            LastMethodTimings = result.MethodTimings;
            if (!FastCaptureMode)
                RecordTickSnapshot(CurrentTick, result.Sprites);
            FrameRendered?.Invoke(result.Sprites, CurrentTick);
            UpdateTimerInterval();
        }

        // ── Timer callback

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (_frameInProgress || _ctx == null) return;
            _frameInProgress = true;

            var now = DateTime.UtcNow;
            double elapsed = (now - _lastFrameTime).TotalSeconds;
            _lastFrameTime = now;
            int tick = ++CurrentTick;
            int updateType = _currentUpdateType;
            var ctx = _ctx; // capture for closure

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                CodeExecutor.ExecutionResult result;
                var sw = Stopwatch.StartNew();
                try
                {
                    result = CodeExecutor.RunAnimationFrame(ctx, updateType, tick, elapsed);
                }
                catch (Exception ex)
                {
                    result = new CodeExecutor.ExecutionResult { Error = ex.Message };
                }
                sw.Stop();
                double frameMs = sw.Elapsed.TotalMilliseconds;

                try
                {
                    _syncControl.BeginInvoke(new Action(() => HandleFrameResult(result, tick, frameMs)));
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            });
        }

        private void HandleFrameResult(CodeExecutor.ExecutionResult result, int tick, double frameMs)
        {
            _frameInProgress = false;
            if (_ctx == null || !IsPlaying) return;

            LastFrameMs = frameMs;

            if (!result.Success)
            {
                Stop();
                ErrorOccurred?.Invoke(result.Error);
                return;
            }

            LastOutputLines = result.OutputLines;
            LastMethodTimings = result.MethodTimings;

            // Throttle history recording to ~10 Hz to reduce per-frame overhead
            var now = DateTime.UtcNow;
            if ((now - _lastSnapshotTime).TotalMilliseconds >= 100)
            {
                _lastSnapshotTime = now;
                RecordTickSnapshot(tick, result.Sprites);
            }

            FrameRendered?.Invoke(result.Sprites, tick);
            UpdateTimerInterval();
        }

        // ── Update-frequency

        private void UpdateTimerInterval()
        {
            if (_ctx == null) return;

            int freq = CodeExecutor.GetUpdateFrequency(_ctx);

            // Plugin / Mod / Torch scripts almost never set Runtime.UpdateFrequency
            // (it is a Programmable-Block concept), so falling through to the PB
            // 166 ms Update10 default produces a sluggish ~6 fps that doesn't reflect
            // how the script actually runs in-game.  Pick a brisker default for those
            // script types so playback feels responsive while staying readable for
            // watch / variable inspection.
            int pluginFallback =
                _ctx.ScriptType == ScriptType.PulsarPlugin ||
                _ctx.ScriptType == ScriptType.ModSurface   ||
                _ctx.ScriptType == ScriptType.TorchPlugin
                    ? 60   // ≈ 16 fps — smooth-enough preview without overwhelming the UI
                    : FallbackIntervalMs;

            if (freq != 0)
            {
                // UpdateFrequency flags: None=0, Update1=1, Update10=2, Update100=4, Once=8
                if ((freq & 1) != 0)        // Update1
                {
                    _timer.Interval = 33;    // cap at ~30 fps
                    _currentUpdateType = 16; // UpdateType.Update1
                }
                else if ((freq & 2) != 0)   // Update10
                {
                    _timer.Interval = 166;
                    _currentUpdateType = 32; // UpdateType.Update10
                }
                else if ((freq & 4) != 0)   // Update100
                {
                    _timer.Interval = 1666;
                    _currentUpdateType = 64; // UpdateType.Update100
                }
                else if ((freq & 8) != 0)   // Once
                {
                    _timer.Interval = 33;
                    _currentUpdateType = 128; // UpdateType.Once
                }
                else
                {
                    _timer.Interval = pluginFallback;
                    _currentUpdateType = 32;
                }
            }
            else
            {
                _timer.Interval = pluginFallback;
                _currentUpdateType = 32; // Default UpdateType.Update10
            }
        }

        /// <summary>
        /// Snapshots all runner fields and sprite states into the history buffers.
        /// Called automatically after each successful frame.
        /// </summary>
        private void RecordTickSnapshot(int tick, List<SpriteEntry> sprites)
        {
            var fields = InspectFields();
            if (fields != null)
                _tickHistory.Record(tick, fields);
            if (sprites != null)
                _spriteHistory.Record(tick, sprites);
        }

        // ── Variable Inspector ─────────────────────────────────────────────

        /// <summary>
        /// Inspects all instance fields of the compiled runner class using reflection.
        /// Returns null if no animation is prepared.
        /// </summary>
        public Dictionary<string, object> InspectFields()
        {
            if (_ctx?.Runner == null)
                return null;

            var result = new Dictionary<string, object>();
            if (_cachedFields == null)
            {
                _cachedFields = _ctx.Runner.GetType()
                    .GetFields(System.Reflection.BindingFlags.Public | 
                              System.Reflection.BindingFlags.NonPublic | 
                              System.Reflection.BindingFlags.Instance);
            }
            var fields = _cachedFields;

            foreach (var field in fields)
            {
                try
                {
                    var value = field.GetValue(_ctx.Runner);
                    result[field.Name] = value;
                }
                catch (Exception ex)
                {
                    result[field.Name] = $"(error: {ex.Message})";
                }
            }

            return result;
        }

        /// <summary>
        /// Returns the field names and their declared types from the runner instance.
        /// Used by watch expression compilation to generate properly-typed local variables.
        /// Returns null if no animation is prepared.
        /// </summary>
        public Dictionary<string, Type> InspectFieldTypes()
        {
            if (_ctx?.Runner == null)
                return null;

            var result = new Dictionary<string, Type>();
            if (_cachedFields == null)
            {
                _cachedFields = _ctx.Runner.GetType()
                    .GetFields(System.Reflection.BindingFlags.Public |
                              System.Reflection.BindingFlags.NonPublic |
                              System.Reflection.BindingFlags.Instance);
            }
            var fields = _cachedFields;

            foreach (var field in fields)
            {
                result[field.Name] = field.FieldType;
            }

            return result;
        }

        // ── Variable Editor ────────────────────────────────────────────────

        /// <summary>
        /// Sets an instance field on the live runner via reflection.
        /// Converts <paramref name="newValue"/> from string to the field's declared type.
        /// Returns null on success, or an error message on failure.
        /// </summary>
        public string SetFieldValue(string fieldName, string newValue)
        {
            if (_ctx?.Runner == null)
                return "No active animation session.";

            var field = _ctx.Runner.GetType()
                .GetField(fieldName,
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);

            if (field == null)
                return $"Field '{fieldName}' not found.";

            try
            {
                object converted = ConvertToFieldType(newValue, field.FieldType);
                field.SetValue(_ctx.Runner, converted);
                return null; // success
            }
            catch (Exception ex)
            {
                return $"Cannot set '{fieldName}': {ex.Message}";
            }
        }

        /// <summary>
        /// Converts a user-entered string to the target field type.
        /// Handles common primitives, nullable wrappers, and enums.
        /// </summary>
        private static object ConvertToFieldType(string text, Type targetType)
        {
            if (text == null || text.Equals("(null)", StringComparison.OrdinalIgnoreCase))
            {
                if (!targetType.IsValueType || Nullable.GetUnderlyingType(targetType) != null)
                    return null;
                throw new InvalidCastException($"Cannot assign null to value type {targetType.Name}.");
            }

            // Unwrap Nullable<T>
            Type underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

            // Boolean: accept true/false/1/0
            if (underlying == typeof(bool))
            {
                if (text == "1") return true;
                if (text == "0") return false;
                return bool.Parse(text);
            }

            // Enum
            if (underlying.IsEnum)
                return Enum.Parse(underlying, text, ignoreCase: true);

            // All other primitives + string via Convert
            return Convert.ChangeType(text, underlying, System.Globalization.CultureInfo.InvariantCulture);
        }

        // ── Snapshot Comparison Bookmarks ──────────────────────────────────────

        /// <summary>
        /// Bookmarks the current tick (or the scrubber tick) as snapshot A for comparison.
        /// </summary>
        public void SetBookmarkA(int tick) => _spriteHistory.SetBookmarkA(tick);

        /// <summary>
        /// Bookmarks the current tick (or the scrubber tick) as snapshot B for comparison.
        /// </summary>
        public void SetBookmarkB(int tick) => _spriteHistory.SetBookmarkB(tick);

        // ── Dispose ────────────────────────────────────────────────────────────

        public void Dispose()
        {
            Stop();
            if (_timer != null)
            {
                _timer.Tick -= OnTimerTick;
                _timer.Dispose();
                _timer = null;
            }
        }
    }
}
