using Microsoft.VisualStudio.TestTools.UnitTesting;
using SESpriteLCDLayoutTool.Models;
using SESpriteLCDLayoutTool.Services;
using SESpriteLCDLayoutTool.Services.CodeInjection;

namespace SESpriteLCDLayoutTool.Tests
{
    /// <summary>
    /// Wire-in tests for <see cref="CodeGenerator.PatchOriginalSource"/> +
    /// <see cref="RoundTripPatchPlanner"/>.  Validates that in-chunk literal
    /// edits are routed through the central injector while uncovered cases
    /// (interpolated literals, escape-required values, baseline-only sprites)
    /// continue to flow through the legacy string-literal fallback.
    /// </summary>
    [TestClass]
    public class PatchOriginalSourceWireInTests
    {
        // ?? helpers ??????????????????????????????????????????????????????????

        private static SpriteEntry MakeText(string text, int start, int end, string font = "White")
        {
            var sp = new SpriteEntry
            {
                Type = SpriteEntryType.Text,
                Text = text,
                FontId = font,
                SourceStart = start,
                SourceEnd = end
            };
            sp.ImportBaseline = sp.CloneValues();
            return sp;
        }

        private static SpriteEntry MakeTexture(string spriteName, int start, int end)
        {
            var sp = new SpriteEntry
            {
                Type = SpriteEntryType.Texture,
                SpriteName = spriteName,
                FontId = "White",
                SourceStart = start,
                SourceEnd = end
            };
            sp.ImportBaseline = sp.CloneValues();
            return sp;
        }

        private static LcdLayout MakeLayout(string source, params SpriteEntry[] sprites)
        {
            var layout = new LcdLayout { OriginalSourceCode = source };
            foreach (var sp in sprites) layout.Sprites.Add(sp);
            return layout;
        }

        // ── coverage parity (off vs on) ──────────────────────────────────────

        [TestMethod]
        public void TextEdit_ProducesPatchedOutput()
        {
            string src = "var t = MySprite.CreateText(\"OLD\", \"White\", Color.White, 1f);";
            var sp = MakeText("OLD", 0, src.Length);
            sp.Text = "NEW";
            var layout = MakeLayout(src, sp);

            string patched = CodeGenerator.PatchOriginalSource(layout);

            Assert.IsNotNull(patched);
            StringAssert.Contains(patched, "\"NEW\"");
            Assert.IsFalse(patched.Contains("\"OLD\""));
        }

        [TestMethod]
        public void TextEdit_ShiftsSourceOffsetsForLaterSprites()
        {
            // Two non-overlapping chunks; edit in first chunk grows the buffer
            // by 4 chars, second chunk's offsets must shift by +4.
            string src =
                "var a = MySprite.CreateText(\"OLD\", \"White\", Color.White, 1f);\n" +
                "var b = MySprite.CreateText(\"KEEP\", \"White\", Color.White, 1f);\n";

            int firstStart = 0;
            int firstEnd = src.IndexOf(";\n") + 1;
            int secondStart = firstEnd + 1;
            int secondEnd = src.Length - 1;

            var sp1 = MakeText("OLD", firstStart, firstEnd);
            sp1.Text = "OLDXXXX"; // +4 chars after replace

            var sp2 = MakeText("KEEP", secondStart, secondEnd);

            string patched = CodeGenerator.PatchOriginalSource(MakeLayout(src, sp1, sp2));

            Assert.IsNotNull(patched);
            StringAssert.Contains(patched, "\"OLDXXXX\"");
            Assert.AreEqual(1, CodeGenerator.LastInjectorEditCount, "pre-pass edits");
            // sp2 offsets must point at its (still-intact) chunk in the new buffer.
            Assert.AreEqual(secondStart + 4, sp2.SourceStart);
            Assert.AreEqual(secondEnd + 4, sp2.SourceEnd);
            string sp2Chunk = patched.Substring(sp2.SourceStart, sp2.SourceEnd - sp2.SourceStart);
            StringAssert.Contains(sp2Chunk, "\"KEEP\"");
        }

        [TestMethod]
        public void TextEdit_AdvancesBaselineSoStringFallbackDoesNotDoublePatch()
        {
            string src = "var t = MySprite.CreateText(\"OLD\", \"White\", Color.White, 1f);";
            var sp = MakeText("OLD", 0, src.Length);
            sp.Text = "NEW";

            string patched = CodeGenerator.PatchOriginalSource(MakeLayout(src, sp));

            Assert.IsNotNull(patched);
            // After the planner pre-pass advances the baseline, the string-
            // literal fallback MUST report zero unpatched diffs for the
            // covered property (otherwise we'd see a NoteUnpatched bump).
            Assert.AreEqual(0, CodeGenerator.LastUnpatchedChangeCount,
                "Covered text edit should not appear as unpatched after pre-pass.");
        }

        [TestMethod]
        public void SpriteNameEdit_PatchesAndShiftsOffsets()
        {
            string src = "MySprite s = MySprite.CreateSprite(\"OLD\", new Vector2(0,0), new Vector2(64,64));";
            var sp = MakeTexture("OLD", 0, src.Length);
            sp.SpriteName = "NEW";

            string patched = CodeGenerator.PatchOriginalSource(MakeLayout(src, sp));

            Assert.IsNotNull(patched);
            StringAssert.Contains(patched, "\"NEW\"");
            Assert.AreEqual(0, CodeGenerator.LastUnpatchedChangeCount);
        }

        // ── uncovered fallback ───────────────────────────────────────────────

        [TestMethod]
        public void ValueRequiringEscaping_RoutesToUncovered()
        {
            // The planner is escape-free in slice 1: any value containing a
            // quote/backslash/newline/tab is routed to Uncovered so the
            // string-literal fallback can handle the escape sequencing.
            // Result: the injector pre-pass reports zero edits and the
            // fallback owns the patch.
            string src = "var t = MySprite.CreateText(\"OLD\", \"White\", Color.White, 1f);";
            var sp = MakeText("OLD", 0, src.Length);
            sp.Text = "NEW\"WITH\"QUOTES";

            string patched = CodeGenerator.PatchOriginalSource(MakeLayout(src, sp));

            Assert.IsNotNull(patched);
            Assert.AreEqual(0, CodeGenerator.LastInjectorEditCount,
                "value requiring literal escaping must be Uncovered by the planner");
        }

        [TestMethod]
        public void NoChange_ReturnsBufferUnchanged()
        {
            string src = "var t = MySprite.CreateText(\"SAME\", \"White\", Color.White, 1f);";
            var sp = MakeText("SAME", 0, src.Length);

            string patched = CodeGenerator.PatchOriginalSource(MakeLayout(src, sp));

            Assert.AreEqual(src, patched);
            Assert.AreEqual(0, CodeGenerator.LastUnpatchedChangeCount);
        }

        [TestMethod]
        public void TwoCoveredEdits_BothApplied_OffsetsConsistent()
        {
            string src =
                "var a = MySprite.CreateText(\"AA\", \"White\", Color.White, 1f);\n" +
                "var b = MySprite.CreateText(\"BB\", \"White\", Color.White, 1f);\n";

            int firstEnd = src.IndexOf(";\n") + 1;
            int secondStart = firstEnd + 1;

            var sp1 = MakeText("AA", 0, firstEnd);
            sp1.Text = "AAA";   // +1 char
            var sp2 = MakeText("BB", secondStart, src.Length - 1);
            sp2.Text = "BBBB";  // +2 chars

            string patched = CodeGenerator.PatchOriginalSource(MakeLayout(src, sp1, sp2));

            Assert.IsNotNull(patched);
            StringAssert.Contains(patched, "\"AAA\"");
            StringAssert.Contains(patched, "\"BBBB\"");
            // sp2 offsets shifted by sp1's +1 delta (descending-edit application
            // means sp2's own delta does NOT shift sp2 itself).
            Assert.AreEqual(secondStart + 1, sp2.SourceStart);
            string sp2Chunk = patched.Substring(sp2.SourceStart, sp2.SourceEnd - sp2.SourceStart);
            StringAssert.Contains(sp2Chunk, "\"BBBB\"");
        }

        // ?? Color slice ??????????????????????????????????????????????????????

        [TestMethod]
        public void ColorEdit_NamedColor_PatchedAndBaselineAdvanced()
        {
            string src = "var t = MySprite.CreateText(\"hi\", \"White\", Color.White, 1f);";
            var sp = MakeText("hi", 0, src.Length);
            sp.ColorR = 255; sp.ColorG = 255; sp.ColorB = 255; sp.ColorA = 255;
            sp.ImportBaseline = sp.CloneValues();
            sp.ColorR = 10; sp.ColorG = 20; sp.ColorB = 30;

            string patched = CodeGenerator.PatchOriginalSource(MakeLayout(src, sp));

            Assert.IsNotNull(patched);
            StringAssert.Contains(patched, "new Color(10, 20, 30)");
            Assert.IsFalse(patched.Contains("Color.White"),
                "named-color literal must be replaced by the constructor form");
            Assert.AreEqual(1, CodeGenerator.LastInjectorEditCount);
            // Baseline advanced so the string-literal fallback would see no Color diff.
            Assert.AreEqual(10, sp.ImportBaseline.ColorR);
            Assert.AreEqual(20, sp.ImportBaseline.ColorG);
            Assert.AreEqual(30, sp.ImportBaseline.ColorB);
        }

        [TestMethod]
        public void ColorEdit_ConstructorForm_PatchedAndOffsetsShift()
        {
            string src =
                "var a = MySprite.CreateText(\"AA\", \"White\", new Color(10, 20, 30), 1f);\n" +
                "var b = MySprite.CreateText(\"BB\", \"White\", Color.White, 1f);\n";

            int firstEnd = src.IndexOf(";\n") + 1;
            int secondStart = firstEnd + 1;
            int secondEnd = src.Length - 1;

            var sp1 = MakeText("AA", 0, firstEnd);
            sp1.ColorR = 10; sp1.ColorG = 20; sp1.ColorB = 30; sp1.ColorA = 255;
            sp1.ImportBaseline = sp1.CloneValues();
            // Edit color on sp1: `new Color(10, 20, 30)` ? `new Color(40, 50, 60, 200)` (+5 chars)
            sp1.ColorR = 40; sp1.ColorG = 50; sp1.ColorB = 60; sp1.ColorA = 200;

            var sp2 = MakeText("BB", secondStart, secondEnd);

            string patched = CodeGenerator.PatchOriginalSource(MakeLayout(src, sp1, sp2));

            Assert.IsNotNull(patched);
            StringAssert.Contains(patched, "new Color(40, 50, 60, 200)");
            Assert.AreEqual(1, CodeGenerator.LastInjectorEditCount);

            // Old text was 21 chars: `new Color(10, 20, 30)`
            // New text is 26 chars: `new Color(40, 50, 60, 200)` ? delta = +5
            Assert.AreEqual(secondStart + 5, sp2.SourceStart);
            Assert.AreEqual(secondEnd + 5, sp2.SourceEnd);
            string sp2Chunk = patched.Substring(sp2.SourceStart, sp2.SourceEnd - sp2.SourceStart);
            StringAssert.Contains(sp2Chunk, "\"BB\"");
        }

        // ?? Position / Size / Rotation / Scale slice ?????????????????????????

        [TestMethod]
        public void PositionEdit_PatchesLiteralVector2()
        {
            string src = "var t = new MySprite { Position = new Vector2(100.0f, 200.0f), Foo = 1 };";

            var sp = MakeText("hi", 0, src.Length);
            sp.X = 100f; sp.Y = 200f;
            sp.ImportBaseline = sp.CloneValues();
            sp.X = 150f; sp.Y = 250f;

            string patched = CodeGenerator.PatchOriginalSource(MakeLayout(src, sp));

            StringAssert.Contains(patched, "new Vector2(150.0f, 250.0f)");
            Assert.AreEqual(1, CodeGenerator.LastInjectorEditCount);
            Assert.AreEqual(150f, sp.ImportBaseline.X);
            Assert.AreEqual(250f, sp.ImportBaseline.Y);
        }

        [TestMethod]
        public void SizeEdit_PatchesLiteralVector2()
        {
            string src = "var s = new MySprite { Size = new Vector2(64.0f, 32.0f), Foo = 1 };";

            var sp = MakeTexture("Square", 0, src.Length);
            sp.Width = 64f; sp.Height = 32f;
            sp.ImportBaseline = sp.CloneValues();
            sp.Width = 128f; sp.Height = 96f;

            string patched = CodeGenerator.PatchOriginalSource(MakeLayout(src, sp));

            StringAssert.Contains(patched, "new Vector2(128.0f, 96.0f)");
            Assert.AreEqual(1, CodeGenerator.LastInjectorEditCount);
        }

        [TestMethod]
        public void RotationEdit_PatchesLiteralFloat()
        {
            string src = "var s = new MySprite { RotationOrScale = 0.5000f, Foo = 1 };";

            var sp = MakeTexture("Square", 0, src.Length);
            sp.Rotation = 0.5f;
            sp.ImportBaseline = sp.CloneValues();
            sp.Rotation = 1.25f;

            string patched = CodeGenerator.PatchOriginalSource(MakeLayout(src, sp));

            StringAssert.Contains(patched, "RotationOrScale = 1.2500f");
            Assert.AreEqual(1, CodeGenerator.LastInjectorEditCount);
            Assert.AreEqual(1.25f, sp.ImportBaseline.Rotation);
        }

        [TestMethod]
        public void ScaleEdit_PatchesLiteralFloat()
        {
            string src = "var t = new MySprite { RotationOrScale = 1.00f, Foo = 1 };";

            var sp = MakeText("hi", 0, src.Length);
            sp.Scale = 1.0f;
            sp.ImportBaseline = sp.CloneValues();
            sp.Scale = 1.5f;

            string patched = CodeGenerator.PatchOriginalSource(MakeLayout(src, sp));

            StringAssert.Contains(patched, "RotationOrScale = 1.50f");
            Assert.AreEqual(1, CodeGenerator.LastInjectorEditCount);
            Assert.AreEqual(1.5f, sp.ImportBaseline.Scale);
        }

        [TestMethod]
        public void PositionEdit_ShiftsLaterSpriteOffsets()
        {
            // sp1's literal Position grows by +3 chars (10.0f ? 1000.0f);
            // sp2's offsets must shift by exactly +3.
            string src =
                "var a = new MySprite { Position = new Vector2(10.0f, 20.0f) };\n" +
                "var b = MySprite.CreateText(\"BB\", \"White\", Color.White, 1f);\n";

            int firstEnd = src.IndexOf(";\n") + 1;
            int secondStart = firstEnd + 1;
            int secondEnd = src.Length - 1;

            var sp1 = MakeText("hi", 0, firstEnd);
            sp1.X = 10f; sp1.Y = 20f;
            sp1.ImportBaseline = sp1.CloneValues();
            sp1.X = 1000f; sp1.Y = 20f;

            var sp2 = MakeText("BB", secondStart, secondEnd);

            string patched = CodeGenerator.PatchOriginalSource(MakeLayout(src, sp1, sp2));

            // "10.0f, 20.0f" (12) ? "1000.0f, 20.0f" (14) ? delta = +2
            Assert.AreEqual(secondStart + 2, sp2.SourceStart);
            Assert.AreEqual(secondEnd + 2, sp2.SourceEnd);
            string sp2Chunk = patched.Substring(sp2.SourceStart, sp2.SourceEnd - sp2.SourceStart);
            StringAssert.Contains(sp2Chunk, "\"BB\"");
        }
    }
}
