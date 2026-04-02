using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using SESpriteLCDLayoutTool.Data;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Glyph metrics parsed from an SE font XML element.
    /// </summary>
    public struct GlyphMetrics
    {
        public int Code;       // Unicode codepoint
        public int BitmapId;   // index into the bitmap atlas list
        public int OriginX;
        public int OriginY;
        public int Width;
        public int Height;
        public int AdvanceWidth;
        public int LeftSideBearing;
        public bool ForceWhite; // true = alpha-mask (tintable)
    }

    /// <summary>
    /// Loads SE bitmap font atlases (FontDataPA.xml + DDS sheets) and provides
    /// cropped glyph bitmaps keyed by (fontName, codepoint).
    /// </summary>
    public sealed class SeFontAtlas : IDisposable
    {
        // fontName → (codepoint → cropped Bitmap)
        private readonly Dictionary<string, Dictionary<int, Bitmap>> _glyphCache
            = new Dictionary<string, Dictionary<int, Bitmap>>(StringComparer.OrdinalIgnoreCase);

        // fontName → (codepoint → metrics)
        private readonly Dictionary<string, Dictionary<int, GlyphMetrics>> _metrics
            = new Dictionary<string, Dictionary<int, GlyphMetrics>>(StringComparer.OrdinalIgnoreCase);

        // fontName → atlas Bitmap[]
        private readonly Dictionary<string, Bitmap[]> _atlases
            = new Dictionary<string, Bitmap[]>(StringComparer.OrdinalIgnoreCase);

        public int LoadedGlyphCount { get; private set; }

        /// <summary>
        /// Loads font data from the SE Content directory.
        /// Parses FontDataPA.xml for each font folder (white, monospace)
        /// and loads the DDS atlas bitmaps.
        /// </summary>
        public string LoadFromContent(string gameContentPath)
        {
            Dispose();
            if (string.IsNullOrWhiteSpace(gameContentPath) || !Directory.Exists(gameContentPath))
                return "Font content directory not found.";

            string fontsDir = Path.Combine(gameContentPath, "Fonts");
            if (!Directory.Exists(fontsDir))
                return "Fonts directory not found.";

            int totalGlyphs = 0;
            int totalFonts = 0;

            // Load each known font folder
            foreach (string fontName in new[] { "white", "monospace" })
            {
                string fontDir = Path.Combine(fontsDir, fontName);
                string xmlPath = Path.Combine(fontDir, "FontDataPA.xml");
                if (!File.Exists(xmlPath)) continue;

                try
                {
                    int count = LoadFont(fontName, fontDir, xmlPath);
                    totalGlyphs += count;
                    totalFonts++;
                }
                catch { /* skip malformed font files */ }
            }

            LoadedGlyphCount = totalGlyphs;
            return $"Loaded {totalGlyphs} font glyphs from {totalFonts} font(s).";
        }

        /// <summary>
        /// Returns the cropped glyph bitmap for the given font and codepoint,
        /// or null if not available. The bitmap is white alpha-mask for forcewhite
        /// glyphs, or pre-coloured for baked glyphs.
        /// </summary>
        public Bitmap GetGlyph(string fontName, int codepoint)
        {
            // Map SE font names to folder names
            string key = MapFontName(fontName);

            if (_glyphCache.TryGetValue(key, out var cache))
            {
                if (cache.TryGetValue(codepoint, out Bitmap bmp))
                    return bmp;

                // Lazy crop: metrics exist but bitmap not yet cropped
                if (_metrics.TryGetValue(key, out var metricsMap) &&
                    metricsMap.TryGetValue(codepoint, out GlyphMetrics gm) &&
                    _atlases.TryGetValue(key, out Bitmap[] atlases) &&
                    gm.BitmapId < atlases.Length && atlases[gm.BitmapId] != null)
                {
                    bmp = CropGlyph(atlases[gm.BitmapId], gm);
                    if (bmp != null)
                        cache[codepoint] = bmp;
                    return bmp;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the glyph metrics for the given font and codepoint, or null.
        /// </summary>
        public GlyphMetrics? GetMetrics(string fontName, int codepoint)
        {
            string key = MapFontName(fontName);
            if (_metrics.TryGetValue(key, out var metricsMap) &&
                metricsMap.TryGetValue(codepoint, out GlyphMetrics gm))
                return gm;
            return null;
        }

        public void Dispose()
        {
            foreach (var cache in _glyphCache.Values)
                foreach (var bmp in cache.Values)
                    bmp?.Dispose();
            _glyphCache.Clear();

            foreach (var atlases in _atlases.Values)
                foreach (var bmp in atlases)
                    bmp?.Dispose();
            _atlases.Clear();

            _metrics.Clear();
            LoadedGlyphCount = 0;
        }

        // ── Internal ──────────────────────────────────────────────────────────────

        private static string MapFontName(string fontName)
        {
            if (fontName == null) return "white";
            switch (fontName.ToLowerInvariant())
            {
                case "monospace": return "monospace";

                // All SE colour fonts share the same "white" glyph atlas;
                // tinting is applied at render time by the game / our canvas.
                case "white":
                case "red":
                case "green":
                case "blue":
                case "darkblue":
                case "urlfont":
                    return "white";

                default: return "white"; // unknown font — best-effort
            }
        }

        private int LoadFont(string fontName, string fontDir, string xmlPath)
        {
            XDocument doc;
            using (var stream = File.OpenRead(xmlPath))
                doc = XDocument.Load(stream);

            var root = doc.Root;
            if (root == null) return 0;

            // SE font XMLs use a default namespace (http://xna.microsoft.com/bitmapfont).
            // XDocument requires namespace-qualified element lookups.
            XNamespace ns = root.GetDefaultNamespace();

            // Parse bitmap atlas references
            var bitmapPaths = new List<string>();
            var bitmapsEl = root.Element(ns + "bitmaps");
            if (bitmapsEl != null)
            {
                foreach (var bEl in bitmapsEl.Elements(ns + "bitmap"))
                {
                    string name = bEl.Attribute("name")?.Value;
                    if (name != null)
                        bitmapPaths.Add(Path.Combine(fontDir, name));
                    else
                        bitmapPaths.Add(null);
                }
            }

            // Load atlas DDS files
            var atlases = new Bitmap[bitmapPaths.Count];
            for (int i = 0; i < bitmapPaths.Count; i++)
            {
                if (bitmapPaths[i] != null && File.Exists(bitmapPaths[i]))
                    atlases[i] = DdsLoader.Load(bitmapPaths[i]);
            }
            _atlases[fontName] = atlases;

            // Parse glyph elements
            var metricsMap = new Dictionary<int, GlyphMetrics>();
            var glyphsEl = root.Element(ns + "glyphs");
            if (glyphsEl != null)
            {
                foreach (var gEl in glyphsEl.Elements(ns + "glyph"))
                {
                    var gm = ParseGlyph(gEl);
                    if (gm.HasValue)
                        metricsMap[gm.Value.Code] = gm.Value;
                }
            }

            // Determine ForceWhite by analysing actual atlas pixel data.
            // Grayscale pixels (R≈G≈B) = white alpha-mask → tintable.
            // Coloured pixels = baked RGBA → not tintable.
            AnalyzeGlyphTints(atlases, metricsMap);

            _metrics[fontName] = metricsMap;

            // Initialise empty glyph cache (lazy crop on first access)
            _glyphCache[fontName] = new Dictionary<int, Bitmap>();

            return metricsMap.Count;
        }

        /// <summary>
        /// Analyses each glyph's actual atlas pixels to decide ForceWhite.
        /// Grayscale pixels (R ≈ G ≈ B within <paramref name="tolerance"/>)
        /// indicate a white alpha-mask that the game tints at render time.
        /// Any pixel with visible colour saturation means the glyph is baked
        /// RGBA and should NOT be tinted.
        /// </summary>
        private static void AnalyzeGlyphTints(Bitmap[] atlases, Dictionary<int, GlyphMetrics> metricsMap)
        {
            // Pre-extract raw pixel data for each atlas sheet so we lock once
            // per sheet rather than once per glyph.
            var sheetPixels = new byte[atlases.Length][];
            var sheetStrides = new int[atlases.Length];
            var sheetWidths = new int[atlases.Length];
            var sheetHeights = new int[atlases.Length];

            for (int i = 0; i < atlases.Length; i++)
            {
                Bitmap bmp = atlases[i];
                if (bmp == null) continue;

                var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
                BitmapData bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int byteCount = bd.Stride * bd.Height;
                    sheetPixels[i] = new byte[byteCount];
                    Marshal.Copy(bd.Scan0, sheetPixels[i], 0, byteCount);
                    sheetStrides[i] = bd.Stride;
                    sheetWidths[i] = bmp.Width;
                    sheetHeights[i] = bmp.Height;
                }
                finally
                {
                    bmp.UnlockBits(bd);
                }
            }

            // Analyse every glyph
            var codes = new List<int>(metricsMap.Keys);
            foreach (int code in codes)
            {
                var gm = metricsMap[code];
                int sid = gm.BitmapId;

                if (sid < 0 || sid >= atlases.Length || sheetPixels[sid] == null)
                {
                    // No atlas available — keep XML flag as-is
                    continue;
                }

                gm.ForceWhite = IsGlyphTintable(
                    sheetPixels[sid], sheetStrides[sid],
                    sheetWidths[sid], sheetHeights[sid], gm);
                metricsMap[code] = gm;
            }
        }

        /// <summary>
        /// Returns <c>true</c> if every visible pixel in the glyph region is
        /// approximately grayscale (max channel − min channel ≤ tolerance),
        /// meaning the glyph is a white alpha-mask that the game tints.
        /// </summary>
        private static bool IsGlyphTintable(byte[] pixels, int stride,
            int atlasW, int atlasH, GlyphMetrics gm, int tolerance = 20)
        {
            int x0 = Math.Max(0, gm.OriginX);
            int y0 = Math.Max(0, gm.OriginY);
            int x1 = Math.Min(x0 + gm.Width, atlasW);
            int y1 = Math.Min(y0 + gm.Height, atlasH);
            if (x1 <= x0 || y1 <= y0) return true; // empty → default tintable

            // Format32bppArgb memory layout per pixel: B G R A
            for (int y = y0; y < y1; y++)
            {
                int rowOffset = y * stride;
                for (int x = x0; x < x1; x++)
                {
                    int px = rowOffset + x * 4;
                    int a = pixels[px + 3];
                    if (a < 8) continue; // nearly transparent — skip

                    int b = pixels[px];
                    int g = pixels[px + 1];
                    int r = pixels[px + 2];

                    int max = r > g ? (r > b ? r : b) : (g > b ? g : b);
                    int min = r < g ? (r < b ? r : b) : (g < b ? g : b);

                    if (max - min > tolerance)
                        return false; // colour saturation detected → baked RGBA
                }
            }

            return true; // all visible pixels are grayscale → tintable
        }

        private static GlyphMetrics? ParseGlyph(XElement el)
        {
            string codeStr = el.Attribute("code")?.Value;
            if (codeStr == null) return null;

            if (!int.TryParse(codeStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                return null;

            string originStr = el.Attribute("origin")?.Value; // "x,y"
            string sizeStr = el.Attribute("size")?.Value;     // "wxh"
            if (originStr == null || sizeStr == null) return null;

            var originParts = originStr.Split(',');
            var sizeParts = sizeStr.Split('x');
            if (originParts.Length != 2 || sizeParts.Length != 2) return null;

            if (!int.TryParse(originParts[0], out int ox) ||
                !int.TryParse(originParts[1], out int oy) ||
                !int.TryParse(sizeParts[0], out int w) ||
                !int.TryParse(sizeParts[1], out int h))
                return null;

            int.TryParse(el.Attribute("bm")?.Value, out int bmId);
            int.TryParse(el.Attribute("aw")?.Value, out int aw);
            int.TryParse(el.Attribute("lsb")?.Value, out int lsb);
            bool forceWhite = string.Equals(el.Attribute("forcewhite")?.Value, "true",
                                            StringComparison.OrdinalIgnoreCase);

            return new GlyphMetrics
            {
                Code = code,
                BitmapId = bmId,
                OriginX = ox,
                OriginY = oy,
                Width = w,
                Height = h,
                AdvanceWidth = aw,
                LeftSideBearing = lsb,
                ForceWhite = forceWhite,
            };
        }

        private static Bitmap CropGlyph(Bitmap atlas, GlyphMetrics gm)
        {
            if (gm.Width <= 0 || gm.Height <= 0) return null;

            // Clamp to atlas bounds
            int x = Math.Max(0, gm.OriginX);
            int y = Math.Max(0, gm.OriginY);
            int w = Math.Min(gm.Width, atlas.Width - x);
            int h = Math.Min(gm.Height, atlas.Height - y);
            if (w <= 0 || h <= 0) return null;

            var cropRect = new Rectangle(x, y, w, h);
            var glyph = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(glyph))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(atlas, new Rectangle(0, 0, w, h), cropRect, GraphicsUnit.Pixel);
            }
            return glyph;
        }
    }
}
