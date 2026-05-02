using Microsoft.VisualStudio.TestTools.UnitTesting;
using SESpriteLCDLayoutTool.Models;
using SESpriteLCDLayoutTool.Services.CodeInjection;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool.Tests
{
    /// <summary>
    /// Slice 1 tests for <see cref="RoundTripPatchPlanner"/>: validate that the
    /// planner emits correct PropertyPatchOps for in-chunk text/spriteName/font
    /// edits and routes everything else to the Uncovered list.
    /// </summary>
    [TestClass]
    public class RoundTripPatchPlannerTests
    {
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

        private static SpriteEntry MakeTexture(string spriteName, int start, int end, string font = "White")
        {
            var sp = new SpriteEntry
            {
                Type = SpriteEntryType.Texture,
                SpriteName = spriteName,
                FontId = font,
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

        // ─── Text ────────────────────────────────────────────────────────────

        [TestMethod]
        public void TextEdit_InChunkLiteral_EmitsPropertyPatchOp()
        {
            string src = "var t = MySprite.CreateText(\"OLD\", \"White\", Color.White, 1f);";
            int start = 0;
            int end = src.Length;
            var sp = MakeText("OLD", start, end);
            sp.Text = "NEW"; // user edit on canvas

            var report = RoundTripPatchPlanner.BuildPlan(MakeLayout(src, sp));

            Assert.AreEqual(1, report.Plan.PropertyPatches.Count);
            var op = report.Plan.PropertyPatches[0];
            Assert.AreEqual(InjectionAction.Update, op.Action);
            Assert.AreEqual("\"OLD\"", op.ExpectedOldText);
            Assert.AreEqual("\"NEW\"", op.NewText);
            Assert.AreEqual(src.IndexOf("\"OLD\""), op.Start);
            Assert.AreEqual(src.IndexOf("\"OLD\"") + 5, op.End);
            Assert.IsTrue(report.FullyCovered);
        }

        [TestMethod]
        public void TextEdit_AppliedThroughInjector_ProducesExpectedSource()
        {
            string src = "class P { void M() { var t = MySprite.CreateText(\"OLD\", \"White\", Color.White, 1f); } }";
            int chunkStart = src.IndexOf("var t");
            int chunkEnd = src.IndexOf(";", chunkStart) + 1;
            var sp = MakeText("OLD", chunkStart, chunkEnd);
            sp.Text = "HELLO";

            var report = RoundTripPatchPlanner.BuildPlan(MakeLayout(src, sp));
            var injectorResult = new RoslynCodeInjector().Apply(src, report.Plan);

            Assert.IsTrue(injectorResult.Success);
            StringAssert.Contains(injectorResult.RewrittenSource, "\"HELLO\"");
            Assert.IsFalse(injectorResult.RewrittenSource.Contains("\"OLD\""));
            Assert.AreEqual(1, injectorResult.Edits.Count);
            // Edit reported in pre-edit coords, allowing caller to shift SourceEnd.
            Assert.AreEqual("\"HELLO\"".Length - "\"OLD\"".Length, injectorResult.Edits[0].Delta);
        }

        // ─── SpriteName / FontId ─────────────────────────────────────────────

        [TestMethod]
        public void SpriteNameEdit_InChunkLiteral_EmitsPatchOp()
        {
            string src = "var s = MySprite.CreateTextureSprite(\"SquareSimple\", Vector2.Zero, Vector2.One, Color.White);";
            var sp = MakeTexture("SquareSimple", 0, src.Length);
            sp.SpriteName = "Circle";

            var report = RoundTripPatchPlanner.BuildPlan(MakeLayout(src, sp));

            Assert.AreEqual(1, report.Plan.PropertyPatches.Count);
            Assert.AreEqual("\"SquareSimple\"", report.Plan.PropertyPatches[0].ExpectedOldText);
            Assert.AreEqual("\"Circle\"", report.Plan.PropertyPatches[0].NewText);
        }

        [TestMethod]
        public void FontIdEdit_OnTextSprite_EmitsPatchOp()
        {
            string src = "var t = MySprite.CreateText(\"hi\", \"White\", Color.White, 1f);";
            var sp = MakeText("hi", 0, src.Length, font: "White");
            sp.FontId = "Monospace";

            var report = RoundTripPatchPlanner.BuildPlan(MakeLayout(src, sp));

            Assert.AreEqual(1, report.Plan.PropertyPatches.Count);
            Assert.AreEqual("\"White\"", report.Plan.PropertyPatches[0].ExpectedOldText);
            Assert.AreEqual("\"Monospace\"", report.Plan.PropertyPatches[0].NewText);
        }

        // ─── Coverage / fallback ─────────────────────────────────────────────

        [TestMethod]
        public void NoChange_ProducesEmptyPlanAndEmptyUncovered()
        {
            string src = "var t = MySprite.CreateText(\"hi\", \"White\", Color.White, 1f);";
            var sp = MakeText("hi", 0, src.Length);

            var report = RoundTripPatchPlanner.BuildPlan(MakeLayout(src, sp));

            Assert.AreEqual(0, report.Plan.PropertyPatches.Count);
            Assert.AreEqual(0, report.Uncovered.Count);
            Assert.IsTrue(report.FullyCovered);
        }

        [TestMethod]
        public void BaselineOnlySprite_RoutedToUncovered()
        {
            string src = "DrawGauge(s, \"POWER\", 0.5f);";
            var sp = new SpriteEntry
            {
                Type = SpriteEntryType.Text,
                Text = "POWER",
                FontId = "White",
                SourceStart = -1,
                SourceEnd = -1
            };
            sp.ImportBaseline = sp.CloneValues();
            sp.Text = "ENERGY";

            var report = RoundTripPatchPlanner.BuildPlan(MakeLayout(src, sp));

            Assert.AreEqual(0, report.Plan.PropertyPatches.Count);
            Assert.AreEqual(1, report.Uncovered.Count);
            Assert.AreEqual("Text", report.Uncovered[0].Property);
            Assert.IsFalse(report.FullyCovered);
        }

        [TestMethod]
        public void InterpolatedLiteral_NotMatchedExactly_RoutedToUncovered()
        {
            string src = "var t = MySprite.CreateText($\"COUNT={n}\", \"White\", Color.White, 1f);";
            // Baseline runtime value would be e.g. "COUNT=5" — no exact "..." match in chunk.
            var sp = new SpriteEntry
            {
                Type = SpriteEntryType.Text,
                Text = "COUNT=5",
                FontId = "White",
                SourceStart = 0,
                SourceEnd = src.Length
            };
            sp.ImportBaseline = sp.CloneValues();
            sp.Text = "COUNT=9";

            var report = RoundTripPatchPlanner.BuildPlan(MakeLayout(src, sp));

            Assert.AreEqual(0, report.Plan.PropertyPatches.Count);
            Assert.IsTrue(report.Uncovered.Count >= 1);
        }

        [TestMethod]
        public void AmbiguousLiteralInChunk_RoutedToUncovered()
        {
            // Same baseline string appears twice inside the chunk.
            string src = "var t = MySprite.CreateText(\"X\", \"X\", Color.White, 1f);";
            var sp = MakeText("X", 0, src.Length, font: "X");
            sp.Text = "Y";

            var report = RoundTripPatchPlanner.BuildPlan(MakeLayout(src, sp));

            // Text change is ambiguous → uncovered.
            // FontId is unchanged (still "X") → no op, no entry.
            Assert.AreEqual(0, report.Plan.PropertyPatches.Count);
            Assert.IsTrue(report.Uncovered.Count >= 1);
        }

        [TestMethod]
        public void ColorEdit_BaselineNotInChunk_RoutedToUncovered()
        {
            // Baseline color (5,5,5) does not appear as either a constructor
            // literal or a named-color literal in the chunk, so the planner
            // must route the edit to Uncovered.
            string src = "var t = MySprite.CreateText(\"hi\", \"White\", colorExpr, 1f);";
            var sp = MakeText("hi", 0, src.Length);
            sp.ColorR = 5; sp.ColorG = 5; sp.ColorB = 5; sp.ColorA = 255;
            sp.ImportBaseline = sp.CloneValues();
            sp.ColorR = 10; sp.ColorG = 20; sp.ColorB = 30;

            var report = RoundTripPatchPlanner.BuildPlan(MakeLayout(src, sp));

            Assert.AreEqual(0, report.Plan.PropertyPatches.Count);
            Assert.IsTrue(report.Uncovered.Count >= 1);
            Assert.IsFalse(report.FullyCovered);
        }

        [TestMethod]
        public void ColorEdit_NamedColorInChunk_EmitsPatchOp()
        {
            // Baseline is Color.White (255,255,255,255); user changes the color
            // to (10,20,30,255). Planner must replace `Color.White` with
            // `new Color(10, 20, 30)` exactly once.
            string src = "var t = MySprite.CreateText(\"hi\", \"White\", Color.White, 1f);";
            var sp = MakeText("hi", 0, src.Length);
            sp.ColorR = 255; sp.ColorG = 255; sp.ColorB = 255; sp.ColorA = 255;
            sp.ImportBaseline = sp.CloneValues();
            sp.ColorR = 10; sp.ColorG = 20; sp.ColorB = 30;

            var report = RoundTripPatchPlanner.BuildPlan(MakeLayout(src, sp));

            Assert.AreEqual(1, report.Plan.PropertyPatches.Count);
            var op = report.Plan.PropertyPatches[0];
            Assert.AreEqual("Color.White", op.ExpectedOldText);
            Assert.AreEqual("new Color(10, 20, 30)", op.NewText);
            Assert.AreEqual(1, report.Covered.Count);
            Assert.AreEqual("Color", report.Covered[0].Property);
            Assert.AreEqual(10, report.Covered[0].NewColorR);
        }

        [TestMethod]
        public void ColorEdit_ConstructorInChunk_EmitsPatchOp()
        {
            // Baseline is `new Color(10, 20, 30)`; user changes to (40,50,60,200).
            string src = "var t = MySprite.CreateText(\"hi\", \"White\", new Color(10, 20, 30), 1f);";
            var sp = MakeText("hi", 0, src.Length);
            sp.ColorR = 10; sp.ColorG = 20; sp.ColorB = 30; sp.ColorA = 255;
            sp.ImportBaseline = sp.CloneValues();
            sp.ColorR = 40; sp.ColorG = 50; sp.ColorB = 60; sp.ColorA = 200;

            var report = RoundTripPatchPlanner.BuildPlan(MakeLayout(src, sp));

            Assert.AreEqual(1, report.Plan.PropertyPatches.Count);
            var op = report.Plan.PropertyPatches[0];
            Assert.AreEqual("new Color(10, 20, 30)", op.ExpectedOldText);
            Assert.AreEqual("new Color(40, 50, 60, 200)", op.NewText);
        }

        [TestMethod]
        public void ColorEdit_ConstructorWithAlphaInChunk_EmitsPatchOp()
        {
            // Baseline matches the constructor regex with the alpha branch.
            string src = "var t = MySprite.CreateText(\"hi\", \"White\", new Color(10, 20, 30, 128), 1f);";
            var sp = MakeText("hi", 0, src.Length);
            sp.ColorR = 10; sp.ColorG = 20; sp.ColorB = 30; sp.ColorA = 128;
            sp.ImportBaseline = sp.CloneValues();
            sp.ColorR = 10; sp.ColorG = 20; sp.ColorB = 30; sp.ColorA = 255;

            var report = RoundTripPatchPlanner.BuildPlan(MakeLayout(src, sp));

            Assert.AreEqual(1, report.Plan.PropertyPatches.Count);
            var op = report.Plan.PropertyPatches[0];
            Assert.AreEqual("new Color(10, 20, 30, 128)", op.ExpectedOldText);
            Assert.AreEqual("new Color(10, 20, 30)", op.NewText);
        }

        [TestMethod]
        public void ColorEdit_ExpressionColor_RoutedToUncovered()
        {
            // Baseline color value comes from a runtime expression — the planner
            // can't find a constructor or named-color literal in the chunk.
            string src = "var t = MySprite.CreateText(\"hi\", \"White\", colorVar, 1f);";
            var sp = MakeText("hi", 0, src.Length);
            sp.ColorR = 80; sp.ColorG = 80; sp.ColorB = 80; sp.ColorA = 255;
            sp.ImportBaseline = sp.CloneValues();
            sp.ColorR = 90;

            var report = RoundTripPatchPlanner.BuildPlan(MakeLayout(src, sp));

            Assert.AreEqual(0, report.Plan.PropertyPatches.Count);
            Assert.IsTrue(report.Uncovered.Count >= 1);
        }

        [TestMethod]
        public void ValueWithEmbeddedQuote_RoutedToUncovered()
        {
            string src = "var t = MySprite.CreateText(\"OLD\", \"White\", Color.White, 1f);";
            var sp = MakeText("OLD", 0, src.Length);
            sp.Text = "has \"quote\"";

            var report = RoundTripPatchPlanner.BuildPlan(MakeLayout(src, sp));

            Assert.AreEqual(0, report.Plan.PropertyPatches.Count);
            Assert.IsTrue(report.Uncovered.Count >= 1);
        }

        [TestMethod]
        public void TwoSprites_OneCoveredOneUncovered_ReportSplits()
        {
            string src =
                "class P { void M() {\n" +
                "  var a = MySprite.CreateText(\"A\", \"White\", Color.White, 1f);\n" +
                "  var b = MySprite.CreateText($\"B={x}\", \"White\", Color.White, 1f);\n" +
                "} }";
            int aStart = src.IndexOf("var a");
            int aEnd = src.IndexOf(";", aStart) + 1;
            int bStart = src.IndexOf("var b");
            int bEnd = src.IndexOf(";", bStart) + 1;

            var a = MakeText("A", aStart, aEnd);
            a.Text = "AA";
            var b = new SpriteEntry
            {
                Type = SpriteEntryType.Text, Text = "B=1", FontId = "White",
                SourceStart = bStart, SourceEnd = bEnd
            };
            b.ImportBaseline = b.CloneValues();
            b.Text = "B=2";

            var report = RoundTripPatchPlanner.BuildPlan(MakeLayout(src, a, b));

            Assert.AreEqual(1, report.Plan.PropertyPatches.Count);
            Assert.AreEqual("\"A\"", report.Plan.PropertyPatches[0].ExpectedOldText);
            Assert.AreEqual("\"AA\"", report.Plan.PropertyPatches[0].NewText);
            Assert.IsTrue(report.Uncovered.Count >= 1);
        }

        // ─── Position / Size / Rotation / Scale ──────────────────────────────

        [TestMethod]
        public void PositionEdit_LiteralVector2_EmitsPatchOp()
        {
            string src = "var t = new MySprite { Position = new Vector2(100.0f, 200.0f) };";
            var sp = MakeText("hi", 0, src.Length);
            sp.X = 100f; sp.Y = 200f;
            sp.ImportBaseline = sp.CloneValues();
            sp.X = 150f; sp.Y = 250f;

            var report = RoundTripPatchPlanner.BuildPlan(MakeLayout(src, sp));

            Assert.AreEqual(1, report.Plan.PropertyPatches.Count);
            var op = report.Plan.PropertyPatches[0];
            Assert.AreEqual("100.0f, 200.0f", op.ExpectedOldText);
            Assert.AreEqual("150.0f, 250.0f", op.NewText);
            Assert.AreEqual(1, report.Covered.Count);
            Assert.AreEqual("Position", report.Covered[0].Property);
            Assert.AreEqual(150f, report.Covered[0].NewFloatX);
            Assert.AreEqual(250f, report.Covered[0].NewFloatY);
        }

        [TestMethod]
        public void PositionEdit_ExpressionVector2_RoutedToUncovered()
        {
            // Position uses an expression rather than a literal Vector2 → planner can't cover it.
            string src = "var t = new MySprite { Position = posExpr };";
            var sp = MakeText("hi", 0, src.Length);
            sp.X = 10f; sp.Y = 20f;
            sp.ImportBaseline = sp.CloneValues();
            sp.X = 11f; sp.Y = 22f;

            var report = RoundTripPatchPlanner.BuildPlan(MakeLayout(src, sp));

            Assert.AreEqual(0, report.Plan.PropertyPatches.Count);
            Assert.IsTrue(report.Uncovered.Count >= 1);
        }

        [TestMethod]
        public void SizeEdit_LiteralVector2OnTexture_EmitsPatchOp()
        {
            string src = "var s = new MySprite { Size = new Vector2(64.0f, 32.0f) };";
            var sp = MakeTexture("Square", 0, src.Length);
            sp.Width = 64f; sp.Height = 32f;
            sp.ImportBaseline = sp.CloneValues();
            sp.Width = 128f; sp.Height = 96f;

            var report = RoundTripPatchPlanner.BuildPlan(MakeLayout(src, sp));

            Assert.AreEqual(1, report.Plan.PropertyPatches.Count);
            Assert.AreEqual("64.0f, 32.0f", report.Plan.PropertyPatches[0].ExpectedOldText);
            Assert.AreEqual("128.0f, 96.0f", report.Plan.PropertyPatches[0].NewText);
        }

        [TestMethod]
        public void RotationEdit_LiteralFloatOnTexture_EmitsPatchOp()
        {
            string src = "var s = new MySprite { RotationOrScale = 0.5000f, Foo = 1 };";
            var sp = MakeTexture("Square", 0, src.Length);
            sp.Rotation = 0.5f;
            sp.ImportBaseline = sp.CloneValues();
            sp.Rotation = 1.25f;

            var report = RoundTripPatchPlanner.BuildPlan(MakeLayout(src, sp));

            Assert.AreEqual(1, report.Plan.PropertyPatches.Count);
            var op = report.Plan.PropertyPatches[0];
            Assert.AreEqual("0.5000f", op.ExpectedOldText);
            Assert.AreEqual("1.2500f", op.NewText);
            Assert.AreEqual("Rotation", report.Covered[0].Property);
            Assert.AreEqual(1.25f, report.Covered[0].NewFloatScalar);
        }

        [TestMethod]
        public void ScaleEdit_LiteralFloatOnText_EmitsPatchOp_TwoDecimalFormat()
        {
            string src = "var t = new MySprite { RotationOrScale = 1.00f, Foo = 1 };";
            var sp = MakeText("hi", 0, src.Length);
            sp.Scale = 1.0f;
            sp.ImportBaseline = sp.CloneValues();
            sp.Scale = 1.5f;

            var report = RoundTripPatchPlanner.BuildPlan(MakeLayout(src, sp));

            Assert.AreEqual(1, report.Plan.PropertyPatches.Count);
            var op = report.Plan.PropertyPatches[0];
            Assert.AreEqual("1.00f", op.ExpectedOldText);
            Assert.AreEqual("1.50f", op.NewText);
            Assert.AreEqual("Scale", report.Covered[0].Property);
        }

        [TestMethod]
        public void RotationEdit_ExpressionFloat_RoutedToUncovered()
        {
            string src = "var s = new MySprite { RotationOrScale = angleExpr };";
            var sp = MakeTexture("Square", 0, src.Length);
            sp.Rotation = 0.5f;
            sp.ImportBaseline = sp.CloneValues();
            sp.Rotation = 1.5f;

            var report = RoundTripPatchPlanner.BuildPlan(MakeLayout(src, sp));

            Assert.AreEqual(0, report.Plan.PropertyPatches.Count);
            Assert.IsTrue(report.Uncovered.Count >= 1);
        }
    }
}
