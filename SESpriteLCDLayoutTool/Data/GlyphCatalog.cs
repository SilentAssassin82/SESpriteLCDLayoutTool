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

                // ── Standard Unicode – confirmed tintable in Monospace ─────────────────────────
                // Source: SE_LCD_GLYPH_REFERENCE.md (in-game cyan/white tint test)

                new GlyphCategory
                {
                    Name = "Density Ramp – Monospace (tintable)",
                    FontHint = "Monospace",
                    Glyphs = new[]
                    {
                        G('\u2591', "\u2591  Light Shade 25%",    "Monospace", "U+2591", true),
                        G('\u2592', "\u2592  Medium Shade 50%",   "Monospace", "U+2592", true),
                        G('\u2593', "\u2593  Dark Shade 75%",     "Monospace", "U+2593", true),
                        G('\u2588', "\u2588  Full Block 100%",    "Monospace", "U+2588", true),
                        G('\u25A0', "\u25A0  Black Square ~82%",  "Monospace", "U+25A0", true),
                        G('\u263B', "\u263B  Black Smiley ~65%",  "Monospace", "U+263B", true),
                    }
                },

                new GlyphCategory
                {
                    Name = "Box Drawing \u2013 Light (tintable)",
                    FontHint = "Monospace",
                    Glyphs = new[]
                    {
                        G('\u2500', "\u2500  Light Horizontal",     "Monospace", "U+2500", true),
                        G('\u2502', "\u2502  Light Vertical",       "Monospace", "U+2502", true),
                        G('\u250C', "\u250C  Top-Left Corner",      "Monospace", "U+250C", true),
                        G('\u2510', "\u2510  Top-Right Corner",     "Monospace", "U+2510", true),
                        G('\u2514', "\u2514  Bottom-Left Corner",   "Monospace", "U+2514", true),
                        G('\u2518', "\u2518  Bottom-Right Corner",  "Monospace", "U+2518", true),
                        G('\u251C', "\u251C  T-Left",               "Monospace", "U+251C", true),
                        G('\u2524', "\u2524  T-Right",              "Monospace", "U+2524", true),
                        G('\u252C', "\u252C  T-Top",                "Monospace", "U+252C", true),
                        G('\u2534', "\u2534  T-Bottom",             "Monospace", "U+2534", true),
                        G('\u253C', "\u253C  Cross",                "Monospace", "U+253C", true),
                    }
                },

                new GlyphCategory
                {
                    Name = "Box Drawing \u2013 Double & Mixed (tintable)",
                    FontHint = "Monospace",
                    Glyphs = new[]
                    {
                        G('\u2551', "\u2551  Double Vertical",                    "Monospace", "U+2551", true),
                        G('\u2552', "\u2552  Down Single & Right Double",         "Monospace", "U+2552", true),
                        G('\u2553', "\u2553  Down Double & Right Single",         "Monospace", "U+2553", true),
                        G('\u2554', "\u2554  Double Down & Right",                "Monospace", "U+2554", true),
                        G('\u2555', "\u2555  Down Single & Left Double",          "Monospace", "U+2555", true),
                        G('\u2556', "\u2556  Down Double & Left Single",          "Monospace", "U+2556", true),
                        G('\u2557', "\u2557  Double Down & Left",                 "Monospace", "U+2557", true),
                        G('\u2558', "\u2558  Up Single & Right Double",           "Monospace", "U+2558", true),
                        G('\u2559', "\u2559  Up Double & Right Single",           "Monospace", "U+2559", true),
                        G('\u255A', "\u255A  Double Up & Right",                  "Monospace", "U+255A", true),
                        G('\u255B', "\u255B  Up Single & Left Double",            "Monospace", "U+255B", true),
                        G('\u255C', "\u255C  Up Double & Left Single",            "Monospace", "U+255C", true),
                        G('\u255D', "\u255D  Double Up & Left",                   "Monospace", "U+255D", true),
                        G('\u255E', "\u255E  Vert Single & Right Double",         "Monospace", "U+255E", true),
                        G('\u255F', "\u255F  Vert Double & Right Single",         "Monospace", "U+255F", true),
                        G('\u2560', "\u2560  Double Vert & Right",                "Monospace", "U+2560", true),
                        G('\u2561', "\u2561  Vert Single & Left Double",          "Monospace", "U+2561", true),
                        G('\u2562', "\u2562  Vert Double & Left Single",          "Monospace", "U+2562", true),
                        G('\u2563', "\u2563  Double Vert & Left",                 "Monospace", "U+2563", true),
                        G('\u2564', "\u2564  Down Single & Horiz Double",         "Monospace", "U+2564", true),
                        G('\u2565', "\u2565  Down Double & Horiz Single",         "Monospace", "U+2565", true),
                        G('\u2566', "\u2566  Double Down & Horizontal",           "Monospace", "U+2566", true),
                        G('\u2567', "\u2567  Up Single & Horiz Double",           "Monospace", "U+2567", true),
                        G('\u2568', "\u2568  Up Double & Horiz Single",           "Monospace", "U+2568", true),
                        G('\u2569', "\u2569  Double Up & Horizontal",             "Monospace", "U+2569", true),
                        G('\u256A', "\u256A  Vert Single & Horiz Double",         "Monospace", "U+256A", true),
                        G('\u256B', "\u256B  Vert Double & Horiz Single",         "Monospace", "U+256B", true),
                        G('\u256C', "\u256C  Double Vert & Horizontal",           "Monospace", "U+256C", true),
                    }
                },

                new GlyphCategory
                {
                    Name = "Block Elements (tintable)",
                    FontHint = "Monospace",
                    Glyphs = new[]
                    {
                        G('\u2580', "\u2580  Upper Half Block",       "Monospace", "U+2580 \u2014 50%",   true),
                        G('\u2581', "\u2581  Lower 1/8 Block",        "Monospace", "U+2581 \u2014 12.5%", true),
                        G('\u2584', "\u2584  Lower Half Block",       "Monospace", "U+2584 \u2014 50%",   true),
                        G('\u258C', "\u258C  Left Half Block",        "Monospace", "U+258C \u2014 50%",   true),
                        G('\u2590', "\u2590  Right Half Block",       "Monospace", "U+2590 \u2014 50%",   true),
                    }
                },

                new GlyphCategory
                {
                    Name = "Geometric Shapes (tintable)",
                    FontHint = "Monospace",
                    Glyphs = new[]
                    {
                        G('\u25AA', "\u25AA  Black Small Square",     "Monospace", "U+25AA", true),
                        G('\u25AB', "\u25AB  White Small Square",     "Monospace", "U+25AB", true),
                        G('\u25AC', "\u25AC  Black Rectangle",        "Monospace", "U+25AC", true),
                        G('\u25B2', "\u25B2  Black Up Triangle",      "Monospace", "U+25B2", true),
                        G('\u25BA', "\u25BA  Black Right Pointer",    "Monospace", "U+25BA", true),
                        G('\u25BC', "\u25BC  Black Down Triangle",    "Monospace", "U+25BC", true),
                        G('\u25C4', "\u25C4  Black Left Pointer",     "Monospace", "U+25C4", true),
                        G('\u25CA', "\u25CA  Lozenge",                "Monospace", "U+25CA", true),
                        G('\u25CB', "\u25CB  White Circle",           "Monospace", "U+25CB", true),
                        G('\u25CF', "\u25CF  Black Circle",           "Monospace", "U+25CF", true),
                    }
                },

                new GlyphCategory
                {
                    Name = "Arrows (tintable)",
                    FontHint = "Monospace",
                    Glyphs = new[]
                    {
                        G('\u2190', "\u2190  Leftwards",           "Monospace", "U+2190", true),
                        G('\u2191', "\u2191  Upwards",             "Monospace", "U+2191", true),
                        G('\u2192', "\u2192  Rightwards",          "Monospace", "U+2192", true),
                        G('\u2193', "\u2193  Downwards",           "Monospace", "U+2193", true),
                        G('\u2194', "\u2194  Left Right",          "Monospace", "U+2194", true),
                        G('\u2195', "\u2195  Up Down",             "Monospace", "U+2195", true),
                        G('\u21A8', "\u21A8  Up Down with Base",   "Monospace", "U+21A8", true),
                    }
                },

                new GlyphCategory
                {
                    Name = "Math & Technical (tintable)",
                    FontHint = "Monospace",
                    Glyphs = new[]
                    {
                        G('\u2202', "\u2202  Partial Differential",  "Monospace", "U+2202", true),
                        G('\u2205', "\u2205  Empty Set",             "Monospace", "U+2205", true),
                        G('\u2206', "\u2206  Increment (Delta)",     "Monospace", "U+2206", true),
                        G('\u2208', "\u2208  Element Of",            "Monospace", "U+2208", true),
                        G('\u220F', "\u220F  N-Ary Product",         "Monospace", "U+220F", true),
                        G('\u2211', "\u2211  N-Ary Summation",       "Monospace", "U+2211", true),
                        G('\u2215', "\u2215  Division Slash",        "Monospace", "U+2215", true),
                        G('\u221A', "\u221A  Square Root",           "Monospace", "U+221A", true),
                        G('\u221E', "\u221E  Infinity",              "Monospace", "U+221E", true),
                        G('\u221F', "\u221F  Right Angle",           "Monospace", "U+221F", true),
                        G('\u2229', "\u2229  Intersection",          "Monospace", "U+2229", true),
                        G('\u222B', "\u222B  Integral",              "Monospace", "U+222B", true),
                        G('\u2248', "\u2248  Almost Equal To",       "Monospace", "U+2248", true),
                        G('\u2260', "\u2260  Not Equal To",          "Monospace", "U+2260", true),
                        G('\u2264', "\u2264  Less-Than or Equal",    "Monospace", "U+2264", true),
                        G('\u2265', "\u2265  Greater-Than or Equal", "Monospace", "U+2265", true),
                        G('\u2302', "\u2302  House",                 "Monospace", "U+2302", true),
                        G('\u2310', "\u2310  Reversed Not Sign",     "Monospace", "U+2310", true),
                        G('\u2320', "\u2320  Top Half Integral",     "Monospace", "U+2320", true),
                        G('\u2321', "\u2321  Bottom Half Integral",  "Monospace", "U+2321", true),
                        G('\u2713', "\u2713  Check Mark",            "Monospace", "U+2713", true),
                    }
                },

                new GlyphCategory
                {
                    Name = "Misc Symbols (tintable)",
                    FontHint = "Monospace",
                    Glyphs = new[]
                    {
                        G('\u263A', "\u263A  White Smiley",         "Monospace", "U+263A", true),
                        G('\u263C', "\u263C  White Sun",            "Monospace", "U+263C", true),
                        G('\u2640', "\u2640  Female Sign",          "Monospace", "U+2640", true),
                        G('\u2642', "\u2642  Male Sign",            "Monospace", "U+2642", true),
                        G('\u2660', "\u2660  Black Spade",          "Monospace", "U+2660", true),
                        G('\u2663', "\u2663  Black Club",           "Monospace", "U+2663", true),
                        G('\u2665', "\u2665  Black Heart",          "Monospace", "U+2665", true),
                        G('\u2666', "\u2666  Black Diamond",        "Monospace", "U+2666", true),
                        G('\u266A', "\u266A  Eighth Note",          "Monospace", "U+266A", true),
                        G('\u266B', "\u266B  Beamed Eighth Notes",  "Monospace", "U+266B", true),
                    }
                },

                new GlyphCategory
                {
                    Name = "Vulgar Fractions (tintable)",
                    FontHint = "Monospace",
                    Glyphs = new[]
                    {
                        G('\u2150', "\u2150  1/7",  "Monospace", "U+2150", true),
                        G('\u2151', "\u2151  1/9",  "Monospace", "U+2151", true),
                        G('\u2153', "\u2153  1/3",  "Monospace", "U+2153", true),
                        G('\u2154', "\u2154  2/3",  "Monospace", "U+2154", true),
                        G('\u2155', "\u2155  1/5",  "Monospace", "U+2155", true),
                        G('\u2156', "\u2156  2/5",  "Monospace", "U+2156", true),
                        G('\u2157', "\u2157  3/5",  "Monospace", "U+2157", true),
                        G('\u2158', "\u2158  4/5",  "Monospace", "U+2158", true),
                        G('\u2159', "\u2159  1/6",  "Monospace", "U+2159", true),
                        G('\u215A', "\u215A  5/6",  "Monospace", "U+215A", true),
                        G('\u215B', "\u215B  1/8",  "Monospace", "U+215B", true),
                        G('\u215C', "\u215C  3/8",  "Monospace", "U+215C", true),
                        G('\u215D', "\u215D  5/8",  "Monospace", "U+215D", true),
                        G('\u215E', "\u215E  7/8",  "Monospace", "U+215E", true),
                    }
                },

                new GlyphCategory
                {
                    Name = "Letterlike & Currency (tintable)",
                    FontHint = "Monospace",
                    Glyphs = new[]
                    {
                        G('\u03A9', "\u03A9  Omega",           "Monospace", "U+03A9", true),
                        G('\u2105', "\u2105  Care Of",          "Monospace", "U+2105", true),
                        G('\u2113', "\u2113  Script Small L",   "Monospace", "U+2113", true),
                        G('\u2116', "\u2116  Numero Sign",      "Monospace", "U+2116", true),
                        G('\u2122', "\u2122  Trade Mark",       "Monospace", "U+2122", true),
                        G('\u212E', "\u212E  Estimated Sign",   "Monospace", "U+212E", true),
                        G('\u20A3', "\u20A3  Franc Sign",       "Monospace", "U+20A3", true),
                        G('\u20A4', "\u20A4  Lira Sign",        "Monospace", "U+20A4", true),
                        G('\u20A7', "\u20A7  Peseta Sign",      "Monospace", "U+20A7", true),
                        G('\u20AA', "\u20AA  Shekel Sign",      "Monospace", "U+20AA", true),
                        G('\u20AC', "\u20AC  Euro Sign",        "Monospace", "U+20AC", true),
                    }
                },

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
                    Name = "Color Swatches – Monospace (E100–E2FF, in-game only, NOT tintable)",
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

        private static GlyphEntry G(char ch, string label, string font, string notes = null, bool tintable = false) =>
            new GlyphEntry { Character = ch, Label = label, FontHint = font, Notes = notes, Tintable = tintable };

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
            var list = new List<GlyphEntry>(named.Length + 2);
            list.Add(new GlyphEntry
            {
                Character = '\0',
                Label     = "\u26A0 Swatches render as colored squares in SE only \u2014 designer shows fallback font",
                FontHint  = "Monospace",
                Notes     = "info",
            });
            foreach (var (r3, g3, b3, nm) in named)
            {
                int cp = 0xE100 + (r3 << 6) + (g3 << 3) + b3;
                list.Add(new GlyphEntry
                {
                    Character = (char)cp,
                    Label     = $"{nm}  U+{cp:X4}  ({r3*36},{g3*36},{b3*36})  (in-game only)",
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

        // ── Tintable codepoint lookup ─────────────────────────────────────────────

        private static HashSet<int> _tintableCodepoints;

        /// <summary>
        /// Returns the set of codepoints confirmed tintable via in-game testing.
        /// Built once from the catalog entries that have Tintable == true.
        /// </summary>
        public static HashSet<int> GetVerifiedTintableCodepoints()
        {
            if (_tintableCodepoints != null) return _tintableCodepoints;
            var set = new HashSet<int>();
            foreach (var cat in Categories)
            {
                if (cat.Glyphs == null) continue;
                foreach (var g in cat.Glyphs)
                {
                    if (g.Tintable && g.Character != '\0')
                        set.Add(g.Character);
                }
            }
            _tintableCodepoints = set;
            return set;
        }
    }
}
