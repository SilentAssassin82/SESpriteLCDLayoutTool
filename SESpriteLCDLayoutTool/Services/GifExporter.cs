using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Writes an animated GIF89a file from a sequence of <see cref="Bitmap"/> frames.
    ///
    /// Implementation strategy: we let GDI+ encode each frame as a single-frame GIF
    /// (so it does the palette quantisation and LZW compression for us), then we
    /// stitch the frames together into one animated GIF89a stream. Each frame is
    /// written with its own Local Color Table + Graphic Control Extension so the
    /// quantised colours stay accurate across frames. A NETSCAPE 2.0 application
    /// extension is added so the animation loops indefinitely.
    /// </summary>
    public sealed class GifExporter : IDisposable
    {
        private readonly BinaryWriter _bw;
        private readonly int _width;
        private readonly int _height;
        private bool _headerWritten;
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
            _bw     = new BinaryWriter(output);
            _width  = width;
            _height = height;
        }

        /// <summary>Adds a frame. The bitmap is not retained; safe to dispose after this call.</summary>
        public void AddFrame(Bitmap frame)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GifExporter));
            if (frame == null) throw new ArgumentNullException(nameof(frame));

            if (!_headerWritten)
            {
                WriteFileHeader();
                _headerWritten = true;
            }

            // Resize to canvas size if needed.
            Bitmap rgb;
            bool ownsRgb = false;
            if (frame.Width != _width || frame.Height != _height
                || frame.PixelFormat != PixelFormat.Format32bppArgb)
            {
                rgb = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
                ownsRgb = true;
                using (var g = Graphics.FromImage(rgb))
                {
                    g.Clear(Color.Black);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode   = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.DrawImage(frame, 0, 0, _width, _height);
                }
            }
            else
            {
                rgb = frame;
            }

            // Encode the frame as a single-frame GIF via GDI+ so it handles
            // palette quantisation and LZW compression for us, then we splice
            // it into the animated stream below.
            byte[] singleFrame;
            using (var ms = new MemoryStream())
            {
                rgb.Save(ms, ImageFormat.Gif);
                singleFrame = ms.ToArray();
            }
            if (ownsRgb) rgb.Dispose();

            ParseSingleFrameGif(singleFrame, out byte[] palette, out int paletteEntries,
                                out byte[] lzwBlock, out int frameW, out int frameH);

            if (palette == null || lzwBlock == null)
                throw new InvalidDataException("GDI+ produced an unexpected GIF stream.");

            WriteGraphicControlExtension(FrameDelayCs);
            WriteImageDescriptor(frameW, frameH, paletteEntries);
            _bw.Write(palette);
            _bw.Write(lzwBlock);
        }

        private void WriteFileHeader()
        {
            // Magic
            _bw.Write(new[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' });

            // Logical Screen Descriptor — no global colour table; each frame brings its own LCT.
            _bw.Write((ushort)_width);
            _bw.Write((ushort)_height);
            _bw.Write((byte)0x00); // packed: no GCT, colour resolution / sort / size irrelevant
            _bw.Write((byte)0x00); // background colour index
            _bw.Write((byte)0x00); // pixel aspect ratio

            // NETSCAPE 2.0 application extension — looping
            _bw.Write((byte)0x21);             // extension introducer
            _bw.Write((byte)0xFF);             // application extension label
            _bw.Write((byte)0x0B);             // block size
            _bw.Write(new[] {
                (byte)'N',(byte)'E',(byte)'T',(byte)'S',(byte)'C',
                (byte)'A',(byte)'P',(byte)'E',(byte)'2',(byte)'.',(byte)'0' });
            _bw.Write((byte)0x03);             // sub-block size
            _bw.Write((byte)0x01);             // loop sub-block id
            _bw.Write((ushort)LoopCount);      // 0 = infinite
            _bw.Write((byte)0x00);             // block terminator
        }

        private void WriteGraphicControlExtension(int delayCs)
        {
            _bw.Write((byte)0x21);             // extension introducer
            _bw.Write((byte)0xF9);             // graphic control label
            _bw.Write((byte)0x04);             // block size
            _bw.Write((byte)0x00);             // packed: no transparency, no user input, disposal=none
            _bw.Write((ushort)Math.Max(0, delayCs));
            _bw.Write((byte)0x00);             // transparent colour index
            _bw.Write((byte)0x00);             // block terminator
        }

        private void WriteImageDescriptor(int w, int h, int paletteEntries)
        {
            // packed: bit7=LCT flag, bit6=interlace, bit5=sort, bits 0..2 = size of LCT (n where 2^(n+1) entries)
            int n = 0;
            int entries = 2;
            while (entries < paletteEntries) { entries <<= 1; n++; }
            byte packed = (byte)(0x80 | (n & 0x07));

            _bw.Write((byte)0x2C);             // image separator
            _bw.Write((ushort)0);               // left
            _bw.Write((ushort)0);               // top
            _bw.Write((ushort)w);
            _bw.Write((ushort)h);
            _bw.Write(packed);
        }

        /// <summary>
        /// Parses a single-frame GIF produced by GDI+ and extracts:
        ///   - the active colour table (global if present, else local),
        ///   - the LZW image data block (min-code-size byte + sub-blocks + 0x00 terminator),
        ///   - frame width/height.
        /// </summary>
        private static void ParseSingleFrameGif(byte[] data,
                                                out byte[] palette, out int paletteEntries,
                                                out byte[] lzwBlock,
                                                out int width, out int height)
        {
            palette = null;
            paletteEntries = 0;
            lzwBlock = null;
            width = 0;
            height = 0;

            int p = 0;
            if (data.Length < 13) return;

            // Header "GIF87a" / "GIF89a"
            p += 6;

            // Logical Screen Descriptor
            int lsdW    = data[p] | (data[p + 1] << 8); p += 2;
            int lsdH    = data[p] | (data[p + 1] << 8); p += 2;
            byte packed = data[p++];
            p += 2; // bg index + aspect
            bool hasGct = (packed & 0x80) != 0;
            int gctEntries = 1 << ((packed & 0x07) + 1);
            if (hasGct)
            {
                palette = new byte[gctEntries * 3];
                Array.Copy(data, p, palette, 0, palette.Length);
                p += palette.Length;
                paletteEntries = gctEntries;
            }

            width = lsdW;
            height = lsdH;

            while (p < data.Length)
            {
                byte block = data[p++];

                if (block == 0x21) // extension — skip entirely
                {
                    p++; // label
                    while (p < data.Length)
                    {
                        byte sz = data[p++];
                        if (sz == 0) break;
                        p += sz;
                    }
                }
                else if (block == 0x2C) // image descriptor
                {
                    int left = data[p] | (data[p + 1] << 8); p += 2;
                    int top  = data[p] | (data[p + 1] << 8); p += 2;
                    int iw   = data[p] | (data[p + 1] << 8); p += 2;
                    int ih   = data[p] | (data[p + 1] << 8); p += 2;
                    byte ipacked = data[p++];
                    bool hasLct = (ipacked & 0x80) != 0;
                    int lctEntries = 1 << ((ipacked & 0x07) + 1);
                    if (hasLct)
                    {
                        palette = new byte[lctEntries * 3];
                        Array.Copy(data, p, palette, 0, palette.Length);
                        p += palette.Length;
                        paletteEntries = lctEntries;
                    }

                    width = iw > 0 ? iw : width;
                    height = ih > 0 ? ih : height;

                    int lzwStart = p;
                    p++; // min code size byte
                    while (p < data.Length)
                    {
                        byte sz = data[p++];
                        if (sz == 0) break;
                        p += sz;
                    }
                    int lzwEnd = p;
                    lzwBlock = new byte[lzwEnd - lzwStart];
                    Array.Copy(data, lzwStart, lzwBlock, 0, lzwBlock.Length);
                    _ = left; _ = top;
                    return;
                }
                else if (block == 0x3B) // trailer
                {
                    return;
                }
                else
                {
                    // Unknown — bail out
                    return;
                }
            }
        }

        /// <summary>Writes the GIF trailer and closes the stream writer.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_headerWritten)
                _bw.Write((byte)0x3B); // trailer
            _bw.Flush();
            _bw.Dispose();
        }
    }
}
