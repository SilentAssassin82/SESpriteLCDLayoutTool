using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Listens on a named pipe for live LCD snapshot frames streamed from a
    /// Torch/SE plugin.  Each frame is length-prefixed (4-byte LE int32 +
    /// UTF-8 payload) and contains serialised MySprite C# code that can be
    /// fed directly to <see cref="CodeParser.Parse"/>.
    ///
    /// Threading: the listener runs on a background thread and raises events
    /// on that thread.  Callers must marshal to the UI thread if needed.
    /// </summary>
    public sealed class LivePipeListener : IDisposable
    {
        public const string PipeName = "SELcdSnapshot";

        /// <summary>Raised on background thread with the raw C# frame text.</summary>
        public event Action<string> FrameReceived;

        /// <summary>Raised when a plugin connects to the pipe.</summary>
        public event Action Connected;

        /// <summary>Raised when the plugin disconnects (timeout or manual stop).</summary>
        public event Action Disconnected;

        private Thread _thread;
        private volatile bool _stopping;
        private NamedPipeServerStream _currentPipe;
        private readonly object _pipeLock = new object();

        public bool IsListening { get; private set; }
        public bool IsConnected { get; private set; }

        /// <summary>
        /// When true the listener still reads frames (keeping the pipe flowing)
        /// but does not raise <see cref="FrameReceived"/>.
        /// </summary>
        public bool IsPaused { get; set; }

        public void Start()
        {
            if (IsListening) return;
            _stopping = false;
            _thread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "LcdPipeListener",
            };
            _thread.Start();
            IsListening = true;
        }

        public void Stop()
        {
            _stopping = true;
            // Dispose the current pipe to unblock WaitForConnection / Read
            lock (_pipeLock)
            {
                _currentPipe?.Dispose();
                _currentPipe = null;
            }
            IsListening = false;
            IsConnected = false;
        }

        public void Dispose()
        {
            Stop();
        }

        // ── Background thread ────────────────────────────────────────────────

        private void ListenLoop()
        {
            while (!_stopping)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        1, // single client at a time
                        PipeTransmissionMode.Byte);

                    lock (_pipeLock) { _currentPipe = server; }

                    server.WaitForConnection();
                    if (_stopping) break;

                    IsConnected = true;
                    Connected?.Invoke();

                    // Read length-prefixed frames until disconnect
                    var header = new byte[4];
                    while (!_stopping && server.IsConnected)
                    {
                        int read = ReadExact(server, header, 4);
                        if (read < 4) break; // disconnected

                        int length = BitConverter.ToInt32(header, 0);
                        if (length <= 0 || length > 2_000_000) break; // sanity

                        var data = new byte[length];
                        read = ReadExact(server, data, length);
                        if (read < length) break; // disconnected

                        if (!IsPaused)
                        {
                            string frame = Encoding.UTF8.GetString(data);
                            FrameReceived?.Invoke(frame);
                        }
                    }
                }
                catch (ObjectDisposedException) when (_stopping) { break; }
                catch (IOException) when (_stopping) { break; }
                catch { /* pipe error — loop back to re-listen */ }
                finally
                {
                    IsConnected = false;
                    lock (_pipeLock) { _currentPipe = null; }
                    try { server?.Dispose(); } catch { }
                    if (!_stopping)
                        Disconnected?.Invoke();
                }
            }
            IsListening = false;
        }

        private static int ReadExact(Stream s, byte[] buf, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buf, total, count - total);
                if (n == 0) break; // end of stream
                total += n;
            }
            return total;
        }
    }
}
