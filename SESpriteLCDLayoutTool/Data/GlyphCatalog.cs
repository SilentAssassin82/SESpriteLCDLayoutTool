using System.Collections.Generic;

namespace SESpriteLCDLayoutTool.Data
{
    // ── Glyph model ───────────────────────────────────────────────────────────────
    public struct GlyphEntry
    {
        public char   Character { get; set; }
        public string Label     { get; set; }   // display name in the palette
        public string FontHint  { get; set; }   // "White" or "Monospace"
        public bool   Tintable  { get; set; }   // responds to FontColor
        public string Notes     { get; set; }
    }

    public class GlyphCategory
    {
        public string      Name     { get; set; }
        public string      FontHint { get; set; }
        public GlyphEntry[] Glyphs  { get; set; }
    }

    // ── Catalog ───────────────────────────────────────────────────────────────────
    /// <summary>
    /// Confirmed Space Engineers LCD font glyphs, cross-referenced against
    /// glyph_reference.txt produced by SEGlyphScanner from the game's font XMLs.
    ///
    /// Sources:
    ///   • G:\SteamLibrary\...\SpaceEngineers\Content\Fonts\white\glyph_reference.txt
    ///   • SEGlyphScanner / SELcdSymbols.cs (SilentAssassin82)
    ///   • In-game tintability tests documented in SE_LCD_GLYPH_REFERENCE.md
    ///
    /// Key rules:
    ///   • "White" font: default LCD font; E001-E053 are forcewhite (alpha-mask) → tintable
    ///   • "Monospace" font: E040-E23F are baked-RGBA swatches (NOT tintable)
    ///   • E050/E051/E052 render DIFFERENTLY per font:
    ///       White    = PlayStation stick icons
    ///       Monospace = left-chevron arrows  (DrainIcon / FillIcon)
    ///   • Spacers (E070-E078) are invisible; advance-width only; Monospace only
    /// </summary>
    public static class GlyphCatalog
    {
        public static readonly GlyphCategory[] Categories;

        static GlyphCatalog()
        {
            Categories = new[]
            {
                // ── E001–E021  Xbox HUD icons — in both White and Monospace ─────────────────
                // White: forcewhite alpha-mask → fully tintable via FontColor
                // Monospace: compact variants, NOT tintable
                // Confirmed in-game: all E001-E021 tintable in White
                MakeRange("HUD Icons – White & Mono (E001–E021, White tintable)", "White",
                          0xE001, 0x21, tintable: true),

                // ── E022–E027  Xbox extras — White font only ──────────────────────────────────
                MakeRange("HUD Icons – White only (E022–E027, tintable)", "White",
                          0xE022, 0x06, tintable: true),

                // ── E030–E033  PS HUD icons — in both fonts ───────────────────────────────────
                // E033: confirmed tintable in Monospace as well (stipple density)
                MakeRange("PS HUD Icons (E030–E033, tintable)", "White",
                          0xE030, 0x04, tintable: true),

                // ── E034–E053  PS HUD / status / chevrons — both fonts ────────────────────────
                // White: tintable (alpha-mask). Monospace: pre-coloured status arrows (baked).
                // E049, E051, E052: confirmed tintable in Monospace (chevrons).
                // CRITICAL: E050 = DrainIcon (Mono: <), E051 = FillIcon (Mono: < variant)
                MakeRange("PS / Status / Chevrons (E034–E053, use White for tinting)", "White",
                          0xE034, 0x20, tintable: true),

                // ── E054–E058  Monospace-only extra arrows ────────────────────────────────────
                MakeRange("Extra Arrows – Monospace only (E054–E058)", "Monospace",
                          0xE054, 0x05, tintable: false),

                // ── Spacers (E070–E078) Monospace ONLY ────────────────────────────────────────
                new GlyphCategory
                {
                    Name = "Spacers – Monospace (E070–E078, invisible)",
                    FontHint = "Monospace",
                    Glyphs = new[]
                    {
                        G('\uE070', "Spacer   0 px (zero-width)", "Monospace", "No-op; advance = 0 px"),
                        G('\uE071', "Spacer   1 px",              "Monospace"),
                        G('\uE072', "Spacer   3 px",              "Monospace"),
                        G('\uE073', "Spacer   7 px",              "Monospace"),
                        G('\uE074', "Spacer  15 px",              "Monospace"),
                        G('\uE075', "Spacer  31 px",              "Monospace"),
                        G('\uE076', "Spacer  63 px",              "Monospace"),
                        G('\uE077', "Spacer 127 px",              "Monospace"),
                        G('\uE078', "Spacer 255 px",              "Monospace"),
                    }
                },

                // ── Color swatches (E100–E2FF) Monospace ONLY ────────────────────────────────
                // 512 baked-RGBA 3-bit-per-channel swatches; formula:
                //   codepoint = 0xE100 + (R₃ << 6) + (G₃ << 3) + B₃
                // Use ColorSwatch(r, g, b) from SELcdSymbols for the right character.
                // We list a representative selection here; use the swatch picker for the full set.
                new GlyphCategory
                {
                    Name = "Color Swatches – Monospace (E100–E2FF, baked, NOT tintable)",
                    FontHint = "Monospace",
                    Glyphs = BuildSwatchSamples(),
                },
            };
        }

        // ── Helpers ───────────────────────────────────────────────────────────────
        private static GlyphCategory MakeRange(string name, string font, int first, int count, bool tintable)
        {
            var list = new List<GlyphEntry>(count);
            for (int i = 0; i < count; i++)
            {
                int cp = first + i;
                string special = SpecialLabel(cp);
                list.Add(new GlyphEntry
                {
                    Character = (char)cp,
                    Label     = special ?? $"U+{cp:X4}",
                    FontHint  = font,
                    Tintable  = tintable,
                    Notes     = special != null ? $"U+{cp:X4}" : null,
                });
            }
            return new GlyphCategory { Name = name, FontHint = font, Glyphs = list.ToArray() };
        }

        private static GlyphEntry G(char ch, string label, string font, string notes = null) =>
            new GlyphEntry { Character = ch, Label = label, FontHint = font, Notes = notes };

        /// <summary>
        /// Named labels for glyphs confirmed in SELcdSymbols.cs.
        /// Returns null for unnamed glyphs.
        /// </summary>
        private static string SpecialLabel(int cp)
        {
            switch (cp)
            {
                case 0xE050: return "DrainIcon – U+E050  (Mono: left-chevron <)";
                case 0xE051: return "FillIcon  – U+E051  (Mono: left-chevron variant)";
                default: return null;
            }
        }

        /// <summary>
        /// Returns a sample of named color swatches for the palette.
        /// Full formula: codepoint = 0xE100 + (R3 &lt;&lt; 6) + (G3 &lt;&lt; 3) + B3  (0–7 per channel)
        /// </summary>
        private static GlyphEntry[] BuildSwatchSamples()
        {
            // Named 3-bit colour corners + common tones
            var named = new (int r3, int g3, int b3, string name)[]
            {
                (0,0,0,"Black"),  (7,7,7,"White"),
                (7,0,0,"Red"),    (0,7,0,"Green"),  (0,0,7,"Blue"),
                (7,7,0,"Yellow"), (0,7,7,"Cyan"),   (7,0,7,"Magenta"),
                (4,4,4,"Gray"),   (2,2,2,"Dark Gray"),
                (7,3,0,"Orange"), (0,3,7,"Sky Blue"),
                (5,0,5,"Purple"), (3,7,0,"Lime"),
            };
            var list = new List<GlyphEntry>(named.Length + 1);
            foreach (var (r3, g3, b3, nm) in named)
            {
                int cp = 0xE100 + (r3 << 6) + (g3 << 3) + b3;
                list.Add(new GlyphEntry
                {
                    Character = (char)cp,
                    Label     = $"{nm}  U+{cp:X4}  ({r3*36},{g3*36},{b3*36})",
                    FontHint  = "Monospace",
                    Tintable  = false,
                    Notes     = $"Baked RGBA swatch. R={r3}/7 G={g3}/7 B={b3}/7",
                });
            }
            list.Add(new GlyphEntry
            {
                Character = '\0',
                Label     = "▸ All 512 swatches: E100 + (R₃<<6) + (G₃<<3) + B₃",
                FontHint  = "Monospace",
                Notes     = "info",
            });
            return list.ToArray();
        }
    }
}
