using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// DDS texture loader — decodes BC1 (DXT1), BC3 (DXT5), BC7 (BPTC)
    /// and uncompressed 32-bit BGRA/RGBA into <see cref="Bitmap"/>.
    /// BC7 partition/fixup/weight tables sourced from the D3D specification
    /// (Microsoft DirectXTex, MIT-licensed).
    /// Unsupported formats return null so the caller can fall back gracefully.
    /// </summary>
    internal static class DdsLoader
    {
        private const uint DdsMagic = 0x20534444; // "DDS "
        private const uint DDPF_FOURCC = 0x04;
        private const uint DDPF_RGB    = 0x40;

        private static readonly uint FCC_DXT1 = FourCC('D','X','T','1');
        private static readonly uint FCC_DXT3 = FourCC('D','X','T','3');
        private static readonly uint FCC_DXT5 = FourCC('D','X','T','5');
        private static readonly uint FCC_DX10 = FourCC('D','X','1','0');

        private static uint FourCC(char a, char b, char c, char d) =>
            (uint)a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);

        public static Bitmap Load(string path)
        {
            return Load(path, out _);
        }

        /// <summary>
        /// Loads a DDS texture and provides a human-readable error reason on failure.
        /// </summary>
        public static Bitmap Load(string path, out string error)
        {
            error = null;
            if (!File.Exists(path)) { error = "File not found"; return null; }
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (data.Length < 128) { error = $"File too small ({data.Length} bytes)"; return null; }
                if (BitConverter.ToUInt32(data, 0) != DdsMagic) { error = "Invalid DDS magic number"; return null; }

                int height = (int)BitConverter.ToUInt32(data, 12);
                int width  = (int)BitConverter.ToUInt32(data, 16);
                uint pfFlags  = BitConverter.ToUInt32(data, 80);
                uint pfFourCC = BitConverter.ToUInt32(data, 84);
                uint pfBits   = BitConverter.ToUInt32(data, 88);
                uint pfR = BitConverter.ToUInt32(data, 92);
                uint pfG = BitConverter.ToUInt32(data, 96);
                uint pfB = BitConverter.ToUInt32(data, 100);
                uint pfA = BitConverter.ToUInt32(data, 104);

                if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
                {
                    error = $"Invalid dimensions ({width}×{height})";
                    return null;
                }

                int off = 128;
                byte[] pixels;

                if ((pfFlags & DDPF_FOURCC) != 0)
                {
                    if (pfFourCC == FCC_DX10)
                    {
                        if (data.Length < 148) { error = "DX10 extended header truncated"; return null; }
                        uint dxgi = BitConverter.ToUInt32(data, 128);
                        off = 148;
                        if (dxgi == 98 || dxgi == 99)      pixels = DecompressBC7(data, off, width, height);
                        else if (dxgi == 71 || dxgi == 72)  pixels = DecompressBC1(data, off, width, height);
                        else if (dxgi == 77 || dxgi == 78)  pixels = DecompressBC3(data, off, width, height);
                        else if (dxgi == 74 || dxgi == 75)  pixels = DecompressBC3(data, off, width, height);
                        else { error = $"Unsupported DXGI format ({dxgi})"; return null; }
                    }
                    else if (pfFourCC == FCC_DXT1) pixels = DecompressBC1(data, off, width, height);
                    else if (pfFourCC == FCC_DXT5) pixels = DecompressBC3(data, off, width, height);
                    else if (pfFourCC == FCC_DXT3) pixels = DecompressBC3(data, off, width, height);
                    else
                    {
                        char c0 = (char)(pfFourCC & 0xFF);
                        char c1 = (char)((pfFourCC >> 8) & 0xFF);
                        char c2 = (char)((pfFourCC >> 16) & 0xFF);
                        char c3 = (char)((pfFourCC >> 24) & 0xFF);
                        error = $"Unsupported FourCC '{c0}{c1}{c2}{c3}'";
                        return null;
                    }
                }
                else if ((pfFlags & DDPF_RGB) != 0 && pfBits == 32)
                {
                    pixels = DecodeRaw32(data, off, width, height, pfR, pfG, pfB, pfA);
                }
                else
                {
                    error = $"Unsupported pixel format (flags=0x{pfFlags:X}, bits={pfBits})";
                    return null;
                }

                if (pixels == null) { error = "Pixel decompression failed (data truncated?)"; return null; }
                return MakeBitmap(pixels, width, height);
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  BC1 (DXT1)
        // ══════════════════════════════════════════════════════════════════════════
        private static byte[] DecompressBC1(byte[] d, int off, int w, int h)
        {
            int bw = (w + 3) / 4, bh = (h + 3) / 4;
            if (off + bw * bh * 8 > d.Length) return null;
            var px = new byte[w * h * 4];
            int p = off;
            for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                ushort c0 = BitConverter.ToUInt16(d, p);
                ushort c1 = BitConverter.ToUInt16(d, p + 2);
                uint idx = BitConverter.ToUInt32(d, p + 4);
                p += 8;
                Rgb565(c0, out byte r0, out byte g0, out byte b0);
                Rgb565(c1, out byte r1, out byte g1, out byte b1);
                var pal = new byte[16];
                pal[0]=b0; pal[1]=g0; pal[2]=r0; pal[3]=255;
                pal[4]=b1; pal[5]=g1; pal[6]=r1; pal[7]=255;
                if (c0 > c1) {
                    pal[8] =(byte)((2*b0+b1)/3); pal[9] =(byte)((2*g0+g1)/3); pal[10]=(byte)((2*r0+r1)/3); pal[11]=255;
                    pal[12]=(byte)((b0+2*b1)/3); pal[13]=(byte)((g0+2*g1)/3); pal[14]=(byte)((r0+2*r1)/3); pal[15]=255;
                } else {
                    pal[8] =(byte)((b0+b1)/2); pal[9] =(byte)((g0+g1)/2); pal[10]=(byte)((r0+r1)/2); pal[11]=255;
                    pal[12]=0; pal[13]=0; pal[14]=0; pal[15]=0;
                }
                WriteBlock(px, w, h, bx, by, pal, idx);
            }
            return px;
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  BC3 (DXT5) — also used for BC2/DXT3 (close enough for preview alpha)
        // ══════════════════════════════════════════════════════════════════════════
        private static byte[] DecompressBC3(byte[] d, int off, int w, int h)
        {
            int bw = (w + 3) / 4, bh = (h + 3) / 4;
            if (off + bw * bh * 16 > d.Length) return null;
            var px = new byte[w * h * 4];
            int p = off;
            for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                byte a0 = d[p], a1 = d[p + 1];
                ulong ab = 0;
                for (int i = 2; i < 8; i++) ab |= (ulong)d[p + i] << (8 * (i - 2));
                var ap = new byte[8];
                ap[0] = a0; ap[1] = a1;
                if (a0 > a1) { for (int i = 1; i <= 6; i++) ap[i+1] = (byte)(((7-i)*a0+i*a1)/7); }
                else { for (int i = 1; i <= 4; i++) ap[i+1] = (byte)(((5-i)*a0+i*a1)/5); ap[6]=0; ap[7]=255; }

                ushort c0 = BitConverter.ToUInt16(d, p+8);
                ushort c1 = BitConverter.ToUInt16(d, p+10);
                uint idx = BitConverter.ToUInt32(d, p+12);
                p += 16;
                Rgb565(c0, out byte r0, out byte g0, out byte b0);
                Rgb565(c1, out byte r1, out byte g1, out byte b1);
                var cp = new byte[12];
                cp[0]=b0;cp[1]=g0;cp[2]=r0; cp[3]=b1;cp[4]=g1;cp[5]=r1;
                cp[6]=(byte)((2*b0+b1)/3);cp[7]=(byte)((2*g0+g1)/3);cp[8]=(byte)((2*r0+r1)/3);
                cp[9]=(byte)((b0+2*b1)/3);cp[10]=(byte)((g0+2*g1)/3);cp[11]=(byte)((r0+2*r1)/3);

                for (int py = 0; py < 4; py++)
                {
                    int y = by*4+py; if (y >= h) break;
                    for (int px2 = 0; px2 < 4; px2++)
                    {
                        int x = bx*4+px2; if (x >= w) continue;
                        int ci = (int)((idx >> (2*(py*4+px2))) & 3);
                        int ai = (int)((ab  >> (3*(py*4+px2))) & 7);
                        int dst = (y*w+x)*4;
                        px[dst]=cp[ci*3]; px[dst+1]=cp[ci*3+1]; px[dst+2]=cp[ci*3+2]; px[dst+3]=ap[ai];
                    }
                }
            }
            return px;
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  Uncompressed 32-bit
        // ══════════════════════════════════════════════════════════════════════════
        private static byte[] DecodeRaw32(byte[] d, int off, int w, int h, uint rM, uint gM, uint bM, uint aM)
        {
            if (off + w * h * 4 > d.Length) return null;
            var px = new byte[w * h * 4];
            int rS = Shift(rM), rB = Bits(rM);
            int gS = Shift(gM), gB = Bits(gM);
            int bS = Shift(bM), bB = Bits(bM);
            int aS = Shift(aM), aB = Bits(aM);
            for (int i = 0; i < w * h; i++)
            {
                uint v = BitConverter.ToUInt32(d, off + i * 4);
                int dst = i * 4;
                px[dst + 2] = Expand(v, rS, rB);
                px[dst + 1] = Expand(v, gS, gB);
                px[dst]     = Expand(v, bS, bB);
                px[dst + 3] = aM != 0 ? Expand(v, aS, aB) : (byte)255;
            }
            return px;
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  BC7 (BPTC) — full decoder for all 8 modes
        // ══════════════════════════════════════════════════════════════════════════
        private static byte[] DecompressBC7(byte[] d, int off, int w, int h)
        {
            int bw = (w + 3) / 4, bh = (h + 3) / 4;
            if (off + bw * bh * 16 > d.Length) return null;
            var px = new byte[w * h * 4];
            var blk = new byte[16];
            var rgba = new byte[64];

            int pos = off;
            for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                Buffer.BlockCopy(d, pos, blk, 0, 16);
                pos += 16;
                DecodeBC7Block(blk, rgba);
                for (int py = 0; py < 4; py++)
                {
                    int y = by * 4 + py; if (y >= h) break;
                    for (int px2 = 0; px2 < 4; px2++)
                    {
                        int x = bx * 4 + px2; if (x >= w) continue;
                        int si = (py * 4 + px2) * 4;
                        int di = (y * w + x) * 4;
                        px[di] = rgba[si]; px[di+1] = rgba[si+1]; px[di+2] = rgba[si+2]; px[di+3] = rgba[si+3];
                    }
                }
            }
            return px;
        }

        private static void DecodeBC7Block(byte[] blk, byte[] outBgra)
        {
            int mode = -1;
            for (int i = 0; i < 8; i++) { if ((blk[0] & (1 << i)) != 0) { mode = i; break; } }
            if (mode < 0) { Array.Clear(outBgra, 0, 64); return; }

            int bp = mode + 1;
            int ns  = MNS[mode], pb = MPB[mode], rb = MRB[mode], isb = MISB[mode];
            int cb  = MCB[mode], ab = MAB[mode], epb = MEPB[mode], spb = MSPB[mode];
            int ib  = MIB[mode], ib2 = MIB2[mode];
            int ne  = ns * 2;

            int partIdx = (int)RdBits(blk, ref bp, pb);
            int rot     = (int)RdBits(blk, ref bp, rb);
            int idxSel  = (int)RdBits(blk, ref bp, isb);

            // Endpoints [endpoint][channel: R=0 G=1 B=2 A=3]
            var ep = new int[ne, 4];
            for (int ch = 0; ch < 3; ch++)
                for (int e = 0; e < ne; e++)
                    ep[e, ch] = (int)RdBits(blk, ref bp, cb);
            for (int e = 0; e < ne; e++)
                ep[e, 3] = ab > 0 ? (int)RdBits(blk, ref bp, ab) : 255;

            // P-bits
            int cbx = cb, abx = ab;
            if (epb > 0)
            {
                for (int e = 0; e < ne; e++)
                {
                    int pv = (int)RdBits(blk, ref bp, 1);
                    for (int ch = 0; ch < 3; ch++) ep[e, ch] = (ep[e, ch] << 1) | pv;
                    if (ab > 0) ep[e, 3] = (ep[e, 3] << 1) | pv;
                }
                cbx++; if (ab > 0) abx++;
            }
            else if (spb > 0)
            {
                for (int s = 0; s < ns; s++)
                {
                    int pv = (int)RdBits(blk, ref bp, 1);
                    for (int j = 0; j < 2; j++)
                    {
                        int e = s * 2 + j;
                        for (int ch = 0; ch < 3; ch++) ep[e, ch] = (ep[e, ch] << 1) | pv;
                        if (ab > 0) ep[e, 3] = (ep[e, 3] << 1) | pv;
                    }
                }
                cbx++; if (ab > 0) abx++;
            }

            // Expand to 8-bit
            for (int e = 0; e < ne; e++)
            {
                for (int ch = 0; ch < 3; ch++) ep[e, ch] = Exp8(ep[e, ch], cbx);
                ep[e, 3] = ab > 0 ? Exp8(ep[e, 3], abx) : 255;
            }

            // Primary indices
            var ci = new int[16];
            for (int i = 0; i < 16; i++)
            {
                bool anchor = IsAnchor(ns, partIdx, i);
                ci[i] = (int)RdBits(blk, ref bp, anchor ? ib - 1 : ib);
            }

            // Secondary indices (modes 4 & 5)
            var ci2 = new int[16];
            if (ib2 > 0)
            {
                for (int i = 0; i < 16; i++)
                    ci2[i] = (int)RdBits(blk, ref bp, i == 0 ? ib2 - 1 : ib2);
            }

            var wt  = Weights[ib];
            var wt2 = ib2 > 0 ? Weights[ib2] : null;

            for (int i = 0; i < 16; i++)
            {
                int sub = Subset(ns, partIdx, i);
                int e0 = sub * 2, e1 = sub * 2 + 1;
                int r, g, b, a;

                if (ib2 == 0)
                {
                    int ww = wt[ci[i]];
                    r = Lerp(ep[e0,0], ep[e1,0], ww);
                    g = Lerp(ep[e0,1], ep[e1,1], ww);
                    b = Lerp(ep[e0,2], ep[e1,2], ww);
                    a = Lerp(ep[e0,3], ep[e1,3], ww);
                }
                else
                {
                    int cI, aI; int[] cW, aW;
                    if (idxSel == 0) { cI = ci[i]; cW = wt; aI = ci2[i]; aW = wt2; }
                    else             { cI = ci2[i]; cW = wt2; aI = ci[i]; aW = wt; }
                    r = Lerp(ep[e0,0], ep[e1,0], cW[cI]);
                    g = Lerp(ep[e0,1], ep[e1,1], cW[cI]);
                    b = Lerp(ep[e0,2], ep[e1,2], cW[cI]);
                    a = Lerp(ep[e0,3], ep[e1,3], aW[aI]);
                }

                // Channel rotation (modes 4 & 5)
                switch (rot)
                {
                    case 1: { int t = a; a = r; r = t; break; }
                    case 2: { int t = a; a = g; g = t; break; }
                    case 3: { int t = a; a = b; b = t; break; }
                }

                outBgra[i*4]   = (byte)b;
                outBgra[i*4+1] = (byte)g;
                outBgra[i*4+2] = (byte)r;
                outBgra[i*4+3] = (byte)a;
            }
        }

        // ── BC7 bit reader ────────────────────────────────────────────────────────
        private static uint RdBits(byte[] blk, ref int bp, int n)
        {
            uint v = 0;
            for (int i = 0; i < n; i++)
            {
                v |= (uint)((blk[bp >> 3] >> (bp & 7)) & 1) << i;
                bp++;
            }
            return v;
        }

        private static int Lerp(int e0, int e1, int w) => ((64 - w) * e0 + w * e1 + 32) >> 6;

        private static int Exp8(int v, int bits)
        {
            if (bits >= 8) return v & 255;
            if (bits == 0) return 255;
            v <<= (8 - bits);
            v |= v >> bits;
            return v & 255;
        }

        private static int Subset(int ns, int partIdx, int pixel)
        {
            if (ns == 1) return 0;
            if (ns == 2) return P2[partIdx, pixel];
            return P3[partIdx, pixel];
        }

        private static bool IsAnchor(int ns, int partIdx, int pixel)
        {
            if (pixel == 0) return true; // subset 0 anchor is always pixel 0
            if (ns == 2) return pixel == Fix2[partIdx];
            if (ns == 3) return pixel == Fix3[partIdx] || pixel == Fix3b[partIdx];
            return false;
        }

        // ── BC1/BC3 helpers ───────────────────────────────────────────────────────
        private static void Rgb565(ushort c, out byte r, out byte g, out byte b)
        {
            r = (byte)((c >> 11) * 255 / 31);
            g = (byte)(((c >> 5) & 63) * 255 / 63);
            b = (byte)((c & 31) * 255 / 31);
        }

        private static void WriteBlock(byte[] px, int w, int h, int bx, int by, byte[] pal, uint idx)
        {
            for (int py = 0; py < 4; py++)
            {
                int y = by * 4 + py; if (y >= h) break;
                for (int px2 = 0; px2 < 4; px2++)
                {
                    int x = bx * 4 + px2; if (x >= w) continue;
                    int ci = (int)((idx >> (2 * (py * 4 + px2))) & 3);
                    int dst = (y * w + x) * 4;
                    px[dst] = pal[ci*4]; px[dst+1] = pal[ci*4+1]; px[dst+2] = pal[ci*4+2]; px[dst+3] = pal[ci*4+3];
                }
            }
        }

        private static int Shift(uint m) { if (m==0)return 0; int s=0; while((m&1)==0){m>>=1;s++;} return s; }
        private static int Bits(uint m) { if (m==0)return 0; while((m&1)==0)m>>=1; int n=0; while((m&1)!=0){m>>=1;n++;} return n; }
        private static byte Expand(uint p, int s, int b) { if(b==0)return 255; uint v=(p>>s)&((1u<<b)-1); return(byte)(v*255/((1u<<b)-1)); }

        private static Bitmap MakeBitmap(byte[] bgra, int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                if (bd.Stride == w * 4) Marshal.Copy(bgra, 0, bd.Scan0, bgra.Length);
                else for (int y = 0; y < h; y++) Marshal.Copy(bgra, y*w*4, bd.Scan0 + y*bd.Stride, w*4);
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  BC7 tables — from D3D specification (Microsoft DirectXTex, MIT license)
        // ══════════════════════════════════════════════════════════════════════════

        // Mode:                   0  1  2  3  4  5  6  7
        static readonly int[] MNS  = {3, 2, 3, 2, 1, 1, 1, 2}; // num subsets
        static readonly int[] MPB  = {4, 6, 6, 6, 0, 0, 0, 6}; // partition bits
        static readonly int[] MRB  = {0, 0, 0, 0, 2, 2, 0, 0}; // rotation bits
        static readonly int[] MISB = {0, 0, 0, 0, 1, 0, 0, 0}; // index sel bit
        static readonly int[] MCB  = {4, 6, 5, 7, 5, 7, 7, 5}; // color bits
        static readonly int[] MAB  = {0, 0, 0, 0, 6, 8, 7, 5}; // alpha bits
        static readonly int[] MEPB = {1, 0, 0, 1, 0, 0, 1, 1}; // endpoint P-bit
        static readonly int[] MSPB = {0, 1, 0, 0, 0, 0, 0, 0}; // shared P-bit
        static readonly int[] MIB  = {3, 3, 2, 2, 2, 2, 4, 2}; // index bits
        static readonly int[] MIB2 = {0, 0, 0, 0, 3, 2, 0, 0}; // secondary idx

        static readonly int[][] Weights = {
            null, null,
            new[]{ 0, 21, 43, 64 },
            new[]{ 0, 9, 18, 27, 37, 46, 55, 64 },
            new[]{ 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 },
        };

        // 2-subset fixup index (subset 1 anchor pixel) for each of 64 partitions
        static readonly byte[] Fix2 = {
            15,15,15,15,15,15,15,15, 15,15,15,15,15,15,15,15,
            15, 2, 8, 2, 2, 8, 8,15,  2, 8, 2, 2, 8, 8, 2, 2,
            15,15, 6, 8, 2, 8,15,15,  2, 8, 2, 2, 2,15,15, 6,
             6, 2, 6, 8,15,15, 2, 2, 15,15,15,15,15, 2, 2,15,
        };

        // 3-subset fixup indices for subset 1 and subset 2
        static readonly byte[] Fix3 = {
             3, 3,15,15, 8, 3,15,15,  8, 8, 6, 6, 6, 5, 3, 3,
             3, 3, 8,15, 3, 3, 6,10,  5, 8, 8, 6, 8, 5,15,15,
             8,15, 3, 5, 6,10, 8,15, 15, 3,15, 5,15,15,15,15,
             3,15, 5, 5, 5, 8, 5,10,  5,10, 8,13,15,12, 3, 3,
        };

        // The g_aFixUp table stores {subset0_anchor=0, subset1_anchor, subset2_anchor}.
        // For IsAnchor with 3 subsets, we check Fix3 (subset 1) and Fix3b (subset 2).
        static readonly byte[] Fix3b = {
            15, 8, 8, 3,15,15, 3, 8, 15,15,15,15,15,15,15, 8,
            15, 8,15, 3,15, 8,15, 8,  3,15, 6,10,15,15,10, 8,
            15, 3,15,10,10, 8, 9,10,  6,15, 8,15, 3, 6, 6, 8,
            15, 3,15,15,15,15,15,15, 15,15,15,15, 3,15,15, 8,
        };

        // ── Partition tables (from DirectXTex BC6HBC7.cpp, MIT license) ────────
        // P2[partition, pixel] → subset (0 or 1) for 2-subset modes
        static readonly byte[,] P2 = {
            {0,0,1,1,0,0,1,1,0,0,1,1,0,0,1,1},{0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1},
            {0,1,1,1,0,1,1,1,0,1,1,1,0,1,1,1},{0,0,0,1,0,0,1,1,0,0,1,1,0,1,1,1},
            {0,0,0,0,0,0,0,1,0,0,0,1,0,0,1,1},{0,0,1,1,0,1,1,1,0,1,1,1,1,1,1,1},
            {0,0,0,1,0,0,1,1,0,1,1,1,1,1,1,1},{0,0,0,0,0,0,0,1,0,0,1,1,0,1,1,1},
            {0,0,0,0,0,0,0,0,0,0,0,1,0,0,1,1},{0,0,1,1,0,1,1,1,1,1,1,1,1,1,1,1},
            {0,0,0,0,0,0,0,1,0,1,1,1,1,1,1,1},{0,0,0,0,0,0,0,0,0,0,0,1,0,1,1,1},
            {0,0,0,1,0,1,1,1,1,1,1,1,1,1,1,1},{0,0,0,0,0,0,0,0,1,1,1,1,1,1,1,1},
            {0,0,0,0,1,1,1,1,1,1,1,1,1,1,1,1},{0,0,0,0,0,0,0,0,0,0,0,0,1,1,1,1},
            {0,0,0,0,1,0,0,0,1,1,1,0,1,1,1,1},{0,1,1,1,0,0,0,0,0,0,0,0,1,1,1,0},
            {0,0,0,0,0,0,0,0,1,0,0,0,1,1,1,0},{0,1,1,1,0,0,1,1,0,0,0,0,0,0,0,0},
            {0,0,1,1,0,0,0,1,0,0,0,0,0,0,0,0},{0,0,0,0,1,0,0,0,1,1,0,0,1,1,1,0},
            {0,0,0,0,0,0,0,0,1,0,0,0,1,1,0,0},{0,1,1,1,0,0,1,1,0,0,1,1,0,0,0,0},
            {0,0,1,1,0,0,0,1,0,0,0,1,0,0,0,0},{0,0,0,0,0,0,0,1,0,1,1,0,0,1,1,0},
            {0,0,0,0,0,0,0,0,1,1,0,0,1,1,0,0},{0,0,0,1,0,0,1,1,1,1,0,0,1,0,0,0},
            {0,0,0,0,1,1,1,1,0,0,0,0,0,0,0,0},{0,0,0,0,0,0,0,1,1,1,0,0,0,0,0,0},
            {0,0,0,1,0,0,0,1,1,0,0,0,1,0,0,0},{0,0,0,0,0,0,0,0,1,0,0,1,1,0,0,1},
            {0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1},{0,0,0,0,1,1,1,1,0,0,0,0,1,1,1,1},
            {0,1,0,1,1,0,1,0,0,1,0,1,1,0,1,0},{0,0,1,1,0,0,1,1,1,1,0,0,1,1,0,0},
            {0,0,1,1,1,1,0,0,0,0,1,1,1,1,0,0},{0,1,0,1,0,1,0,1,1,0,1,0,1,0,1,0},
            {0,1,1,0,0,1,1,0,1,0,0,1,1,0,0,1},{0,1,0,1,1,0,1,0,1,0,1,0,0,1,0,1},
            {0,1,1,1,0,0,1,1,1,1,0,0,1,1,1,0},{0,0,0,1,0,0,1,1,1,1,0,0,1,0,0,0},
            {0,0,1,1,0,0,1,0,0,1,0,0,1,1,0,0},{0,0,1,1,1,0,0,1,1,0,0,1,1,1,0,0},
            {0,1,1,0,1,0,0,1,1,0,0,1,0,1,1,0},{0,0,1,1,1,1,0,0,1,1,0,0,0,0,1,1},
            {0,1,1,0,0,1,1,0,1,0,0,1,1,0,0,1},{0,0,0,0,0,1,1,0,0,1,1,0,0,0,0,0},
            {0,1,0,0,1,1,1,0,0,1,0,0,0,0,0,0},{0,0,1,0,0,1,1,1,0,0,1,0,0,0,0,0},
            {0,0,0,0,0,0,1,0,0,1,1,1,0,0,1,0},{0,0,0,0,0,1,0,0,1,1,1,0,0,1,0,0},
            {0,1,1,0,1,1,0,0,1,0,0,1,0,0,1,1},{0,0,1,1,0,1,1,0,1,1,0,0,1,0,0,1},
            {0,1,1,0,0,0,1,1,1,0,0,1,1,1,0,0},{0,0,1,1,1,0,0,1,1,1,0,0,0,0,1,1},
            {0,1,1,0,1,1,0,0,1,1,0,0,1,0,0,1},{0,1,1,0,0,0,1,1,0,0,1,1,1,0,0,1},
            {0,1,1,1,1,1,1,0,1,0,0,0,0,0,0,1},{0,0,0,1,1,0,0,0,1,1,1,0,0,1,1,1},
            {0,0,0,0,1,1,1,1,0,0,1,1,0,0,1,1},{0,0,1,1,0,0,1,1,1,1,1,1,0,0,0,0},
            {0,0,1,0,0,0,1,0,1,1,1,0,1,1,1,0},{0,1,0,0,0,1,0,0,0,1,1,1,0,1,1,1},
        };

        // P3[partition, pixel] → subset (0, 1, or 2) for 3-subset modes
        static readonly byte[,] P3 = {
            {0,0,1,1,0,0,1,1,0,2,2,1,2,2,2,2},{0,0,0,1,0,0,1,1,2,2,1,1,2,2,2,1},
            {0,0,0,0,2,0,0,1,2,2,1,1,2,2,1,1},{0,2,2,2,0,0,2,2,0,0,1,1,0,1,1,1},
            {0,0,0,0,0,0,0,0,1,1,2,2,1,1,2,2},{0,0,1,1,0,0,1,1,0,0,2,2,0,0,2,2},
            {0,0,2,2,0,0,2,2,1,1,1,1,1,1,1,1},{0,0,1,1,0,0,1,1,2,2,1,1,2,2,1,1},
            {0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2},{0,0,0,0,1,1,1,1,1,1,1,1,2,2,2,2},
            {0,0,0,0,1,1,1,1,2,2,2,2,2,2,2,2},{0,0,1,2,0,0,1,2,0,0,1,2,0,0,1,2},
            {0,1,1,2,0,1,1,2,0,1,1,2,0,1,1,2},{0,1,2,2,0,1,2,2,0,1,2,2,0,1,2,2},
            {0,0,1,1,0,1,1,2,1,1,2,2,1,2,2,2},{0,0,1,1,2,0,0,1,2,2,0,0,2,2,2,0},
            {0,0,0,1,0,0,1,1,0,1,1,2,1,1,2,2},{0,1,1,1,0,0,1,1,2,0,0,1,2,2,0,0},
            {0,0,0,0,1,1,2,2,1,1,2,2,1,1,2,2},{0,0,2,2,0,0,2,2,0,0,2,2,1,1,1,1},
            {0,1,1,1,0,1,1,1,0,2,2,2,0,2,2,2},{0,0,0,1,0,0,0,1,2,2,2,1,2,2,2,1},
            {0,0,0,0,0,0,1,1,0,1,2,2,0,1,2,2},{0,0,0,0,1,1,0,0,2,2,1,0,2,2,1,0},
            {0,1,2,2,0,1,2,2,0,0,1,1,0,0,0,0},{0,0,1,2,0,0,1,2,1,1,2,2,2,2,2,2},
            {0,1,1,0,1,2,2,1,1,2,2,1,0,1,1,0},{0,0,0,0,0,1,1,0,1,2,2,1,1,2,2,1},
            {0,0,2,2,1,1,0,2,1,1,0,2,0,0,2,2},{0,1,1,0,0,1,1,0,2,0,0,2,2,2,2,2},
            {0,0,1,1,0,1,2,2,0,1,2,2,0,0,1,1},{0,0,0,0,2,0,0,0,2,2,1,1,2,2,2,1},
            {0,0,0,0,0,0,0,2,1,1,2,2,1,2,2,2},{0,2,2,2,0,0,2,2,0,0,1,2,0,0,1,1},
            {0,0,1,1,0,0,1,2,0,0,2,2,0,2,2,2},{0,1,2,0,0,1,2,0,0,1,2,0,0,1,2,0},
            {0,0,0,0,1,1,1,1,2,2,2,2,0,0,0,0},{0,1,2,0,1,2,0,1,2,0,1,2,0,1,2,0},
            {0,1,2,0,2,0,1,2,1,2,0,1,0,1,2,0},{0,0,1,1,2,2,0,0,1,1,2,2,0,0,1,1},
            {0,0,1,1,1,1,2,2,2,2,0,0,0,0,1,1},{0,1,0,1,0,1,0,1,2,2,2,2,2,2,2,2},
            {0,0,0,0,0,0,0,0,2,1,2,1,2,1,2,1},{0,0,2,2,1,1,2,2,0,0,2,2,1,1,2,2},
            {0,0,2,2,0,0,1,1,0,0,2,2,0,0,1,1},{0,2,2,0,1,2,2,1,0,2,2,0,1,2,2,1},
            {0,1,0,1,2,2,2,2,2,2,2,2,0,1,0,1},{0,0,0,0,2,1,2,1,2,1,2,1,2,1,2,1},
            {0,1,0,1,0,1,0,1,0,1,0,1,2,2,2,2},{0,2,2,2,0,1,1,1,0,2,2,2,0,1,1,1},
            {0,0,0,2,1,1,1,2,0,0,0,2,1,1,1,2},{0,0,0,0,2,1,1,2,2,1,1,2,2,1,1,2},
            {0,2,2,2,0,1,1,1,0,1,1,1,0,2,2,2},{0,0,0,2,1,1,1,2,1,1,1,2,0,0,0,2},
            {0,1,1,0,0,1,1,0,0,1,1,0,2,2,2,2},{0,0,0,0,0,0,0,0,2,1,1,2,2,1,1,2},
            {0,1,1,0,0,1,1,0,2,2,2,2,2,2,2,2},{0,0,2,2,0,0,1,1,0,0,1,1,0,0,2,2},
            {0,0,2,2,1,1,2,2,1,1,2,2,0,0,2,2},{0,0,0,0,0,0,0,0,0,0,0,0,2,1,1,2},
            {0,0,0,2,0,0,0,1,0,0,0,2,0,0,0,1},{0,2,2,2,1,2,2,2,0,2,2,2,1,2,2,2},
            {0,1,0,1,2,2,2,2,2,2,2,2,2,2,2,2},{0,1,1,1,2,0,1,1,2,2,0,1,2,2,2,0},
        };
    }
}
