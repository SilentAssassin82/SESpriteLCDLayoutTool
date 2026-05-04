using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Writes an animated GIF from a sequence of <see cref="Bitmap"/> frames using
    /// SixLabors.ImageSharp.
    ///
    /// Each frame is quantised with the Wu algorithm (per-frame 256-colour palette)
    /// and dithered with Floyd–Steinberg error diffusion. This gives dramatically
    /// better quality than GDI+'s built-in GIF encoder, which uses a fixed 8-bit
    /// halftone palette + ordered dither and produces the "salt and pepper" grain
    /// the previous implementation suffered from.
    ///
    /// API surface is unchanged: construct with stream/width/height, set FrameDelayCs
    /// and LoopCount, call AddFrame(Bitmap) for each frame, then Dispose() to flush.
    /// </summary>
    public sealed class GifExporter : IDisposable
    {
        private readonly Stream _output;
        private readonly int _width;
        private readonly int _height;
        private Image<Rgba32> _gif;
        private bool _disposed;

        /// <summary>
        /// Frame delay, in hundredths of a second (1 = 10 ms, 10 = 100 ms).
        /// GIF spec uses 1/100 s units. Values below 2 are usually clamped by viewers.
        /// </summary>
        public int FrameDelayCs { get; set; } = 5;

        /// <summary>0 = loop forever, otherwise loop count.</summary>
        public int LoopCount { get; set; } = 0;

        public GifExporter(Stream output, int width, int height)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (width  < 1) width  = 1;
            if (height < 1) height = 1;
            _output = output;
            _width  = width;
            _height = height;
        }

        /// <summary>Adds a frame. The bitmap is not retained; safe to dispose after this call.</summary>
        public void AddFrame(Bitmap frame)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GifExporter));
            if (frame == null) throw new ArgumentNullException(nameof(frame));

            // Resize / convert to 32bpp ARGB at the canvas size if needed.
            Bitmap rgb;
            bool ownsRgb = false;
            if (frame.Width != _width || frame.Height != _height
                || frame.PixelFormat != PixelFormat.Format32bppArgb)
            {
                rgb = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
                ownsRgb = true;
                using (var g = Graphics.FromImage(rgb))
                {
                    g.Clear(System.Drawing.Color.Black);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode   = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.DrawImage(frame, 0, 0, _width, _height);
                }
            }
            else
            {
                rgb = frame;
            }

            Image<Rgba32> isFrame = BitmapToImageSharp(rgb);
            if (ownsRgb) rgb.Dispose();

            int delayCs = Math.Max(2, FrameDelayCs);

            if (_gif == null)
            {
                // First frame becomes the root; tag animation- and frame-level metadata.
                _gif = isFrame;
                var gifMeta = _gif.Metadata.GetGifMetadata();
                gifMeta.RepeatCount = (ushort)Math.Max(0, LoopCount);
                gifMeta.ColorTableMode = GifColorTableMode.Local; // per-frame palette

                var rootMeta = _gif.Frames.RootFrame.Metadata.GetGifMetadata();
                rootMeta.FrameDelay = delayCs;
                rootMeta.DisposalMethod = GifDisposalMethod.RestoreToBackground;
            }
            else
            {
                // AddFrame clones the frame; tag the cloned frame's metadata so the delay sticks.
                var added = _gif.Frames.AddFrame(isFrame.Frames.RootFrame);
                var addedMeta = added.Metadata.GetGifMetadata();
                addedMeta.FrameDelay = delayCs;
                addedMeta.DisposalMethod = GifDisposalMethod.RestoreToBackground;
                isFrame.Dispose();
            }
        }

        /// <summary>Copies a 32bpp ARGB GDI+ bitmap into a freshly allocated ImageSharp Rgba32 image.</summary>
        private static Image<Rgba32> BitmapToImageSharp(Bitmap bmp)
        {
            int w = bmp.Width;
            int h = bmp.Height;

            BitmapData data = bmp.LockBits(
                new System.Drawing.Rectangle(0, 0, w, h),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            try
            {
                int stride = data.Stride;
                int bytes = stride * h;
                byte[] buf = new byte[bytes];
                Marshal.Copy(data.Scan0, buf, 0, bytes);

                var img = new Image<Rgba32>(w, h);
                // GDI+ Format32bppArgb is BGRA in memory; ImageSharp Rgba32 is RGBA.
                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    var span = img.Frames.RootFrame.PixelBuffer.DangerousGetRowSpan(y);
                    for (int x = 0; x < w; x++)
                    {
                        int i = row + x * 4;
                        byte b = buf[i];
                        byte g = buf[i + 1];
                        byte r = buf[i + 2];
                        byte a = buf[i + 3];
                        span[x] = new Rgba32(r, g, b, a);
                    }
                }
                return img;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        /// <summary>Encodes the assembled animation and writes it to the output stream.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_gif != null)
            {
                var encoder = new GifEncoder
                {
                    Quantizer = new WuQuantizer(new QuantizerOptions
                    {
                        Dither = KnownDitherings.FloydSteinberg,
                        MaxColors = 256
                    }),
                    ColorTableMode = GifColorTableMode.Local
                };
                _gif.SaveAsGif(_output, encoder);
                _gif.Dispose();
                _gif = null;
            }

            _output.Flush();
        }
    }
}
