using System;
using System.IO;
using System.Threading;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Watches a snapshot file on disk and raises <see cref="FrameReceived"/>
    /// whenever it changes.  This provides a file-based live stream path for
    /// Torch plugins that write snapshots via <c>File.WriteAllText</c>.
    ///
    /// The API mirrors <see cref="LivePipeListener"/> so the same
    /// <c>OnLiveFrameReceived</c> handler can be reused.
    ///
    /// Threading: events fire on a ThreadPool thread; callers must marshal
    /// to the UI thread.
    /// </summary>
    public sealed class LiveFileWatcher : IDisposable
    {
        /// <summary>Raised with the file contents whenever the snapshot file changes.</summary>
        public event Action<string> FrameReceived;

        /// <summary>Raised when watching starts successfully.</summary>
        public event Action Connected;

        /// <summary>Raised when watching is stopped.</summary>
        public event Action Disconnected;

        /// <summary>
        /// When true the watcher still detects changes but does not raise
        /// <see cref="FrameReceived"/>.
        /// </summary>
        public bool IsPaused { get; set; }

        public bool IsListening { get; private set; }

        /// <summary>Full path of the file being watched.</summary>
        public string FilePath { get; private set; }

        private FileSystemWatcher _watcher;
        private Timer _throttle;
        private bool _throttlePending;
        private readonly object _lock = new object();

        // Throttle interval — guarantees one read every N ms even during
        // continuous writes (debounce would starve under rapid writes).
        private const int ThrottleMs = 50;

        /// <summary>
        /// Starts watching <paramref name="filePath"/> for changes.
        /// If the file already exists, its current contents are sent as
        /// the first frame immediately.
        /// </summary>
        public void Start(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentNullException(nameof(filePath));

            Stop();

            FilePath = Path.GetFullPath(filePath);
            string dir  = Path.GetDirectoryName(FilePath);
            string name = Path.GetFileName(FilePath);

            _watcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite
                             | NotifyFilters.Size
                             | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };

            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;

            IsListening = true;
            Connected?.Invoke();

            // If the file already exists, send its contents as the first frame
            if (File.Exists(FilePath))
                ScheduleRead();
        }

        public void Stop()
        {
            lock (_lock)
            {
                _throttle?.Dispose();
                _throttle = null;
                _throttlePending = false;
            }

            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnFileEvent;
                _watcher.Created -= OnFileEvent;
                _watcher.Dispose();
                _watcher = null;
            }

            if (IsListening)
            {
                IsListening = false;
                FilePath = null;
                Disconnected?.Invoke();
            }
        }

        public void Dispose() => Stop();

        // ── Internal ────────────────────────────────────────────────────────

        private void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            ScheduleRead();
        }

        /// <summary>
        /// Schedules a read after <see cref="ThrottleMs"/>.  If a read is
        /// already scheduled the event is ignored — this guarantees a steady
        /// read cadence even under continuous writes (no starvation).
        /// </summary>
        private void ScheduleRead()
        {
            lock (_lock)
            {
                if (_throttlePending) return;
                _throttlePending = true;

                if (_throttle == null)
                    _throttle = new Timer(ReadFile, null, ThrottleMs, Timeout.Infinite);
                else
                    _throttle.Change(ThrottleMs, Timeout.Infinite);
            }
        }

        private void ReadFile(object state)
        {
            lock (_lock) { _throttlePending = false; }

            if (!IsListening) return;
            if (IsPaused) return;

            try
            {
                // Use FileShare.ReadWrite so we don't block the writing process
                string contents;
                using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                    contents = sr.ReadToEnd();

                if (!string.IsNullOrWhiteSpace(contents))
                    FrameReceived?.Invoke(contents);
            }
            catch (IOException)
            {
                // File still being written — next event will retry
            }
            catch (UnauthorizedAccessException)
            {
                // Permissions issue — silently skip
            }
        }
    }
}
