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

        /// <summary>
        /// Fixed fallback interval in ms when the script does not set
        /// <c>Runtime.UpdateFrequency</c>.  Default is 166 ms (≈ Update10).
        /// </summary>
        public int FallbackIntervalMs { get; set; } = 166;

        // ── Internal state ─────────────────────────────────────────────────────
        private CodeExecutor.AnimationContext _ctx;
        private Timer _timer;
        private readonly Control _syncControl;
        private DateTime _lastFrameTime;
        private int _currentUpdateType = 32; // UpdateType.Update10
        private bool _frameInProgress;

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
        public string Prepare(string userCode, string callExpression = null)
        {
            Stop();

            try
            {
                _ctx = CodeExecutor.CompileForAnimation(userCode, callExpression);
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
            if (wasPlaying) PlaybackStopped?.Invoke();
        }

        /// <summary>Executes a single frame (synchronously) and stays paused.</summary>
        public void StepForward()
        {
            if (_ctx == null) return;

            IsPlaying = true;
            IsPaused = true;
            _timer.Stop();

            var now = DateTime.UtcNow;
            double elapsed = (now - _lastFrameTime).TotalSeconds;
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

            FrameRendered?.Invoke(result.Sprites, CurrentTick);
            UpdateTimerInterval();
        }

        // ── Timer callback ─────────────────────────────────────────────────────

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

            FrameRendered?.Invoke(result.Sprites, tick);
            UpdateTimerInterval();
        }

        // ── Update-frequency → timer interval ─────────────────────────────────

        private void UpdateTimerInterval()
        {
            if (_ctx == null) return;

            int freq = CodeExecutor.GetUpdateFrequency(_ctx);

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
                    _timer.Interval = FallbackIntervalMs;
                    _currentUpdateType = 32;
                }
            }
            else
            {
                _timer.Interval = FallbackIntervalMs;
                _currentUpdateType = 32; // Default UpdateType.Update10
            }
        }

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
