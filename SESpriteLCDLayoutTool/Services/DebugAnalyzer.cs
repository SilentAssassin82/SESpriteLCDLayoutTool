using System;
using System.Collections.Generic;
using System.Drawing;
using SESpriteLCDLayoutTool.Models;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Provides static analysis of an LCD layout for debugging: sprite stats,
    /// estimated game thread load, texture memory budget, overdraw, and
    /// per-sprite size warnings.
    /// </summary>
    internal static class DebugAnalyzer
    {
        // ── Load rating thresholds (sprite count) ────────────────────────────────
        private const int LoadLight    = 60;
        private const int LoadModerate = 120;
        // Above LoadModerate = Heavy

        /// <summary>Summary statistics for a layout.</summary>
        public sealed class LayoutStats
        {
            public int TotalSprites;
            public int TextureSprites;
            public int TextSprites;
            public int UniqueTextures;
            public int EstimatedDrawCalls;

            /// <summary>Rough ms/frame estimate for the game thread LCD render pass.</summary>
            public float EstimatedMsPerFrame;

            /// <summary>"Light", "Moderate", or "Heavy".</summary>
            public string LoadRating;

            /// <summary>Unicode traffic-light indicator.</summary>
            public string LoadIndicator;
        }

        /// <summary>VRAM information for a single texture.</summary>
        public sealed class TextureMemoryEntry
        {
            public string SpriteName;
            public int OriginalWidth;
            public int OriginalHeight;
            /// <summary>Estimated GPU memory in bytes (W × H × 4).</summary>
            public long VramBytes;
        }

        /// <summary>Aggregated texture memory report.</summary>
        public sealed class TextureMemoryReport
        {
            public List<TextureMemoryEntry> Entries = new List<TextureMemoryEntry>();
            public long TotalBytes;
        }

        /// <summary>Per-sprite warning when rendered much smaller than source texture.</summary>
        public sealed class SizeWarning
        {
            public SpriteEntry Sprite;
            public int TextureWidth;
            public int TextureHeight;
            public float RenderedWidth;
            public float RenderedHeight;
            public float WasteRatio; // textureArea / renderedArea
        }

        // ── Main analysis ────────────────────────────────────────────────────────

        /// <summary>
        /// Computes layout-wide statistics: sprite counts, unique textures,
        /// estimated draw calls, and a game-thread load rating.
        /// </summary>
        public static LayoutStats Analyze(LcdLayout layout)
        {
            if (layout == null) return new LayoutStats();

            var stats = new LayoutStats();
            var uniqueTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string lastTexture = null;
            int drawCalls = 0;

            foreach (var sp in layout.Sprites)
            {
                if (sp.IsHidden) continue;
                stats.TotalSprites++;

                if (sp.Type == SpriteEntryType.Text)
                {
                    stats.TextSprites++;
                    drawCalls++; // text always causes a draw call
                    lastTexture = null; // texture batching broken by text
                }
                else
                {
                    stats.TextureSprites++;
                    string key = sp.SpriteName ?? "";
                    uniqueTextures.Add(key);
                    // SE batches consecutive texture sprites with the same texture
                    if (!string.Equals(key, lastTexture, StringComparison.OrdinalIgnoreCase))
                        drawCalls++;
                    lastTexture = key;
                }
            }

            stats.UniqueTextures = uniqueTextures.Count;
            stats.EstimatedDrawCalls = drawCalls;

            // Rough cost model: base 0.1ms + 0.03ms per sprite + 0.05ms per text sprite
            // (text measurement is more expensive than texture blits in SE)
            stats.EstimatedMsPerFrame = 0.1f
                + stats.TotalSprites * 0.03f
                + stats.TextSprites * 0.05f;

            if (stats.TotalSprites <= LoadLight)
            {
                stats.LoadRating = "Light";
                stats.LoadIndicator = "\U0001F7E2"; // green circle
            }
            else if (stats.TotalSprites <= LoadModerate)
            {
                stats.LoadRating = "Moderate";
                stats.LoadIndicator = "\U0001F7E1"; // yellow circle
            }
            else
            {
                stats.LoadRating = "Heavy";
                stats.LoadIndicator = "\U0001F534"; // red circle
            }

            return stats;
        }

        // ── Texture memory budget ────────────────────────────────────────────────

        /// <summary>
        /// Estimates VRAM usage for all unique textures referenced by visible sprites.
        /// Uses original (pre-downscale) texture dimensions from the SBC-mapped source.
        /// Falls back to cached bitmap dimensions when originals are unknown.
        /// </summary>
        public static TextureMemoryReport AnalyzeTextureMemory(
            LcdLayout layout, SpriteTextureCache textureCache)
        {
            var report = new TextureMemoryReport();
            if (layout == null) return report;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sp in layout.Sprites)
            {
                if (sp.IsHidden || sp.Type != SpriteEntryType.Texture) continue;
                string name = sp.SpriteName ?? "";
                if (!seen.Add(name)) continue;

                // Try to get the original texture size from the cache
                int w = 0, h = 0;
                if (textureCache != null)
                {
                    var originalSize = textureCache.GetOriginalSize(name);
                    if (originalSize.HasValue)
                    {
                        w = originalSize.Value.Width;
                        h = originalSize.Value.Height;
                    }
                    else
                    {
                        // Fallback: use cached (downscaled) bitmap size
                        var bmp = textureCache.GetTexture(name);
                        if (bmp != null)
                        {
                            w = bmp.Width;
                            h = bmp.Height;
                        }
                    }
                }

                if (w == 0 || h == 0) continue;

                long vram = (long)w * h * 4; // BGRA 32-bit
                report.Entries.Add(new TextureMemoryEntry
                {
                    SpriteName = name,
                    OriginalWidth = w,
                    OriginalHeight = h,
                    VramBytes = vram,
                });
                report.TotalBytes += vram;
            }

            return report;
        }

        // ── Per-sprite size warnings ─────────────────────────────────────────────

        /// <summary>
        /// Returns warnings for sprites whose rendered size is dramatically smaller
        /// than the source texture (waste ratio > 4×).
        /// </summary>
        public static List<SizeWarning> AnalyzeSizeWarnings(
            LcdLayout layout, SpriteTextureCache textureCache)
        {
            var warnings = new List<SizeWarning>();
            if (layout == null || textureCache == null) return warnings;

            foreach (var sp in layout.Sprites)
            {
                if (sp.IsHidden || sp.Type != SpriteEntryType.Texture) continue;

                int tw = 0, th = 0;
                var originalSize = textureCache.GetOriginalSize(sp.SpriteName ?? "");
                if (originalSize.HasValue)
                {
                    tw = originalSize.Value.Width;
                    th = originalSize.Value.Height;
                }
                else
                {
                    var bmp = textureCache.GetTexture(sp.SpriteName ?? "");
                    if (bmp != null) { tw = bmp.Width; th = bmp.Height; }
                }

                if (tw == 0 || th == 0) continue;

                float renderedArea = sp.Width * sp.Height;
                float textureArea = tw * th;
                if (renderedArea <= 0) continue;

                float ratio = textureArea / renderedArea;
                if (ratio >= 4f)
                {
                    warnings.Add(new SizeWarning
                    {
                        Sprite = sp,
                        TextureWidth = tw,
                        TextureHeight = th,
                        RenderedWidth = sp.Width,
                        RenderedHeight = sp.Height,
                        WasteRatio = ratio,
                    });
                }
            }

            return warnings;
        }

        // ── Overdraw computation ─────────────────────────────────────────────────

        /// <summary>
        /// Computes a low-resolution overdraw map for the layout surface.
        /// Each cell stores the number of sprites whose bounding box covers that cell.
        /// Resolution is clamped to avoid excessive computation.
        /// </summary>
        /// <param name="cellSize">Size of each analysis cell in surface pixels.</param>
        /// <returns>2D array [cols, rows] of overlap counts, or null if no layout.</returns>
        public static int[,] ComputeOverdrawMap(LcdLayout layout, int cellSize = 8)
        {
            if (layout == null) return null;

            int cols = Math.Max(1, (int)Math.Ceiling((float)layout.SurfaceWidth / cellSize));
            int rows = Math.Max(1, (int)Math.Ceiling((float)layout.SurfaceHeight / cellSize));

            // Cap resolution to avoid huge allocations
            if (cols > 256) { cellSize = (int)Math.Ceiling((float)layout.SurfaceWidth / 256); cols = 256; }
            if (rows > 256) { cellSize = (int)Math.Ceiling((float)layout.SurfaceHeight / 256); rows = 256; }

            var map = new int[cols, rows];

            foreach (var sp in layout.Sprites)
            {
                if (sp.IsHidden) continue;

                // Compute sprite bounding box in surface coords
                float left, top, right, bottom;
                if (sp.Type == SpriteEntryType.Text)
                {
                    // Text: Position is top-edge, alignment shifts X
                    switch (sp.Alignment)
                    {
                        case SpriteTextAlignment.Right:
                            left = sp.X - sp.Width;
                            right = sp.X;
                            break;
                        case SpriteTextAlignment.Center:
                            left = sp.X - sp.Width / 2f;
                            right = sp.X + sp.Width / 2f;
                            break;
                        default: // Left
                            left = sp.X;
                            right = sp.X + sp.Width;
                            break;
                    }
                    top = sp.Y;
                    bottom = sp.Y + sp.Height;
                }
                else
                {
                    // Texture: Position is center
                    float hw = sp.Width / 2f;
                    float hh = sp.Height / 2f;
                    left = sp.X - hw;
                    top = sp.Y - hh;
                    right = sp.X + hw;
                    bottom = sp.Y + hh;
                }

                int c0 = Math.Max(0, (int)(left / cellSize));
                int c1 = Math.Min(cols - 1, (int)(right / cellSize));
                int r0 = Math.Max(0, (int)(top / cellSize));
                int r1 = Math.Min(rows - 1, (int)(bottom / cellSize));

                for (int c = c0; c <= c1; c++)
                    for (int r = r0; r <= r1; r++)
                        map[c, r]++;
            }

            return map;
        }

        // ── Formatting helpers ───────────────────────────────────────────────────

        /// <summary>Formats a byte count as a human-readable string (KB / MB).</summary>
        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }

        /// <summary>
        /// Builds a one-line summary for the status bar.
        /// </summary>
        public static string BuildStatusSummary(LayoutStats stats)
        {
            if (stats.TotalSprites == 0) return "No sprites";
            return $"{stats.LoadIndicator} {stats.TotalSprites} sprites  |  "
                 + $"{stats.TextureSprites} tex / {stats.TextSprites} text  |  "
                 + $"{stats.UniqueTextures} unique tex  |  "
                 + $"~{stats.EstimatedDrawCalls} draw calls  |  "
                 + $"~{stats.EstimatedMsPerFrame:F1} ms  [{stats.LoadRating}]";
        }
    }
}
