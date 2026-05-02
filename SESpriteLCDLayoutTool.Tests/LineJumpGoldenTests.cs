using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool.Tests
{
    /// <summary>
    /// Phase 1.5 golden regression test.
    ///
    /// CONTRACT (from .github/copilot-instructions.md):
    ///   "Code navigation from clicking a sprite in the layer list MUST land on the
    ///    sprite CREATION line (e.g. var t = MySprite.CreateText(...) or new MySprite(...))
    ///    — NOT on the .Add() line. The .Add() line has no editable sprite data.
    ///    This applies to ALL plugin types."
    ///
    /// These tests pin the behaviour of <see cref="SpriteSourceMapper"/> and
    /// <see cref="SpriteAddMapper"/> BEFORE the code-injector refactor, so any
    /// regression in line-jump targeting will fail loudly during/after migration.
    ///
    /// They are intentionally parser-only — no compilation, no UI, no Roslyn
    /// emit — so they run fast and stay reliable.
    /// </summary>
    [TestClass]
    public class LineJumpGoldenTests
    {
        // ─────────────────────────────────────────────────────────────────────
        // SpriteAddMapper: the primary navigation table for runtime sprites
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Indirect creation via local variable: var t = MySprite.CreateText(...); frame.Add(t);
        /// Creation line MUST be the `var t = ...` line, NOT the frame.Add line.
        /// This is THE canonical regression that the contract calls out by name.
        /// </summary>
        [TestMethod]
        public void AddMapper_IndirectCreateText_PointsAtCreationLineNotAddLine()
        {
            // Lines:                                                                   (1-based)
            //  1  using VRage.Game.GUI.TextPanel;
            //  2
            //  3  public class P {
            //  4      void Draw(IMyTextSurface surface) {
            //  5          var frame = surface.DrawFrame();
            //  6          var t = MySprite.CreateText("hello", "Debug", System.Drawing.Color.White, 1f);
            //  7          frame.Add(t);
            //  8      }
            //  9  }
            string code =
                "using VRage.Game.GUI.TextPanel;\n" +
                "\n" +
                "public class P {\n" +
                "    void Draw(IMyTextSurface surface) {\n" +
                "        var frame = surface.DrawFrame();\n" +
                "        var t = MySprite.CreateText(\"hello\", \"Debug\", System.Drawing.Color.White, 1f);\n" +
                "        frame.Add(t);\n" +
                "    }\n" +
                "}\n";

            var map = SpriteAddMapper.BuildAddCallMap(code);

            Assert.IsTrue(map.ContainsKey("Draw"), "Draw should be mapped");
            var calls = map["Draw"];
            Assert.AreEqual(1, calls.Count, "Expected exactly one Add call");

            var call = calls[0];
            Assert.AreEqual(6, call.LineNumber,
                "Creation line MUST point at the 'var t = MySprite.CreateText(...)' line (6), not the .Add line.");
            Assert.AreEqual(7, call.AddLineNumber,
                "AddLineNumber should still record the actual frame.Add(t) line (7) for reference.");
            Assert.AreNotEqual(call.LineNumber, call.AddLineNumber,
                "Indirect-creation sprites must distinguish creation line from add line.");
            Assert.AreEqual("t", call.VariableName, "Indirect Add should record the variable name.");
        }

        /// <summary>
        /// Indirect creation via `new MySprite(...)` stored in a local.
        /// </summary>
        [TestMethod]
        public void AddMapper_IndirectNewMySprite_PointsAtCreationLineNotAddLine()
        {
            //  1  public class P {
            //  2      void Draw(IMyTextSurface surface) {
            //  3          var frame = surface.DrawFrame();
            //  4          var s = new MySprite(SpriteType.TEXTURE, "SquareSimple", new Vector2(10,10));
            //  5          frame.Add(s);
            //  6      }
            //  7  }
            string code =
                "public class P {\n" +
                "    void Draw(IMyTextSurface surface) {\n" +
                "        var frame = surface.DrawFrame();\n" +
                "        var s = new MySprite(SpriteType.TEXTURE, \"SquareSimple\", new Vector2(10,10));\n" +
                "        frame.Add(s);\n" +
                "    }\n" +
                "}\n";

            var map = SpriteAddMapper.BuildAddCallMap(code);

            Assert.IsTrue(map.ContainsKey("Draw"));
            var call = map["Draw"].Single();
            Assert.AreEqual(4, call.LineNumber, "Creation line should be the `var s = new MySprite(...)` line.");
            Assert.AreEqual(5, call.AddLineNumber, "AddLineNumber should be the frame.Add(s) line.");
            Assert.AreEqual("s", call.VariableName);
        }

        /// <summary>
        /// Direct inline creation: frame.Add(new MySprite(...)).
        /// In this case creation == add (same line) and that is correct;
        /// the assertion is that LineNumber lands on the line the user wants to edit.
        /// </summary>
        [TestMethod]
        public void AddMapper_DirectInlineNew_LandsOnAddLine()
        {
            //  1  public class P {
            //  2      void Draw(IMyTextSurface surface) {
            //  3          var frame = surface.DrawFrame();
            //  4          frame.Add(new MySprite(SpriteType.TEXTURE, "Circle", new Vector2(0,0)));
            //  5      }
            //  6  }
            string code =
                "public class P {\n" +
                "    void Draw(IMyTextSurface surface) {\n" +
                "        var frame = surface.DrawFrame();\n" +
                "        frame.Add(new MySprite(SpriteType.TEXTURE, \"Circle\", new Vector2(0,0)));\n" +
                "    }\n" +
                "}\n";

            var map = SpriteAddMapper.BuildAddCallMap(code);
            var call = map["Draw"].Single();

            Assert.AreEqual(4, call.LineNumber);
            Assert.AreEqual(4, call.AddLineNumber);
            Assert.IsNull(call.VariableName, "Direct creations must not record a variable name.");
        }

        /// <summary>
        /// List&lt;MySprite&gt; render-method pattern (PB-style helper).
        /// Indirect: var t = ...; sprites.Add(t); — creation line, not Add line.
        /// </summary>
        [TestMethod]
        public void AddMapper_ListMySpritePattern_IndirectCreation_PointsAtCreationLine()
        {
            //  1  public class P {
            //  2      void DrawBar(List<MySprite> sprites, float v) {
            //  3          var bar = new MySprite(SpriteType.TEXTURE, "SquareSimple", new Vector2(0,0));
            //  4          sprites.Add(bar);
            //  5      }
            //  6  }
            string code =
                "public class P {\n" +
                "    void DrawBar(List<MySprite> sprites, float v) {\n" +
                "        var bar = new MySprite(SpriteType.TEXTURE, \"SquareSimple\", new Vector2(0,0));\n" +
                "        sprites.Add(bar);\n" +
                "    }\n" +
                "}\n";

            var map = SpriteAddMapper.BuildAddCallMap(code);

            Assert.IsTrue(map.ContainsKey("DrawBar"), "DrawBar (List<MySprite> param) should be mapped");
            var call = map["DrawBar"].Single();
            Assert.AreEqual(3, call.LineNumber, "Creation line must be the `var bar = new MySprite(...)` line.");
            Assert.AreEqual(4, call.AddLineNumber);
            Assert.AreEqual("bar", call.VariableName);
        }

        /// <summary>
        /// Multiple Add calls in source order: each must independently resolve to its own creation line.
        /// </summary>
        [TestMethod]
        public void AddMapper_MultipleAdds_PreserveCreationOrderAndLines()
        {
            //  1  public class P {
            //  2      void Draw(IMyTextSurface surface) {
            //  3          var frame = surface.DrawFrame();
            //  4          var a = MySprite.CreateText("A", "Debug", System.Drawing.Color.White, 1f);
            //  5          var b = MySprite.CreateText("B", "Debug", System.Drawing.Color.White, 1f);
            //  6          frame.Add(a);
            //  7          frame.Add(b);
            //  8      }
            //  9  }
            string code =
                "public class P {\n" +
                "    void Draw(IMyTextSurface surface) {\n" +
                "        var frame = surface.DrawFrame();\n" +
                "        var a = MySprite.CreateText(\"A\", \"Debug\", System.Drawing.Color.White, 1f);\n" +
                "        var b = MySprite.CreateText(\"B\", \"Debug\", System.Drawing.Color.White, 1f);\n" +
                "        frame.Add(a);\n" +
                "        frame.Add(b);\n" +
                "    }\n" +
                "}\n";

            var map = SpriteAddMapper.BuildAddCallMap(code);
            var calls = map["Draw"];

            Assert.AreEqual(2, calls.Count);
            Assert.AreEqual(4, calls[0].LineNumber, "First sprite creation is on line 4.");
            Assert.AreEqual(6, calls[0].AddLineNumber);
            Assert.AreEqual("a", calls[0].VariableName);
            Assert.AreEqual(5, calls[1].LineNumber, "Second sprite creation is on line 5.");
            Assert.AreEqual(7, calls[1].AddLineNumber);
            Assert.AreEqual("b", calls[1].VariableName);
        }

        /// <summary>
        /// Switch/case render pattern (e.g. IML's switch(row.RowKind)).
        /// Each case block is exposed as a virtual "Render{CaseName}" method
        /// so navigation works for sprites produced from that case.
        /// </summary>
        [TestMethod]
        public void AddMapper_SwitchCaseRender_ProducesVirtualRenderMethods()
        {
            //  1  public class P {
            //  2      void Render() {
            //  3          var sprites = new List<MySprite>();
            //  4          var row = new LcdSpriteRow();
            //  5          switch (row.RowKind) {
            //  6              case LcdSpriteRow.Kind.Header:
            //  7                  var h = MySprite.CreateText("HDR", "Debug", System.Drawing.Color.White, 1f);
            //  8                  sprites.Add(h);
            //  9                  break;
            // 10              case LcdSpriteRow.Kind.Bar:
            // 11                  var b = new MySprite(SpriteType.TEXTURE, "SquareSimple", new Vector2(0,0));
            // 12                  sprites.Add(b);
            // 13                  break;
            // 14          }
            // 15      }
            // 16  }
            string code =
                "public class P {\n" +
                "    void Render() {\n" +
                "        var sprites = new List<MySprite>();\n" +
                "        var row = new LcdSpriteRow();\n" +
                "        switch (row.RowKind) {\n" +
                "            case LcdSpriteRow.Kind.Header:\n" +
                "                var h = MySprite.CreateText(\"HDR\", \"Debug\", System.Drawing.Color.White, 1f);\n" +
                "                sprites.Add(h);\n" +
                "                break;\n" +
                "            case LcdSpriteRow.Kind.Bar:\n" +
                "                var b = new MySprite(SpriteType.TEXTURE, \"SquareSimple\", new Vector2(0,0));\n" +
                "                sprites.Add(b);\n" +
                "                break;\n" +
                "        }\n" +
                "    }\n" +
                "}\n";

            var map = SpriteAddMapper.BuildAddCallMap(code);

            Assert.IsTrue(map.ContainsKey("RenderHeader"),
                "Switch-case extraction must produce virtual method 'RenderHeader' for case Kind.Header.");
            Assert.IsTrue(map.ContainsKey("RenderBar"),
                "Switch-case extraction must produce virtual method 'RenderBar' for case Kind.Bar.");

            var hdr = map["RenderHeader"].Single();
            Assert.AreEqual(7, hdr.LineNumber, "RenderHeader sprite must point at the `var h = ...` creation line (7).");
            Assert.AreEqual(8, hdr.AddLineNumber);

            var bar = map["RenderBar"].Single();
            Assert.AreEqual(11, bar.LineNumber, "RenderBar sprite must point at the `var b = new MySprite(...)` creation line (11).");
            Assert.AreEqual(12, bar.AddLineNumber);
        }

        // ─────────────────────────────────────────────────────────────────────
        // SpriteSourceMapper: Roslyn-based sibling used by per-sprite round-trip
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// SpriteSourceMapper records the SYNTACTIC location of each sprite-producing
        /// expression. For an indirect Add call the recorded creation site IS the
        /// .Add invocation (because that is the syntax node), but its CodeSnippet
        /// must capture the full call so downstream consumers can disambiguate.
        ///
        /// This test pins that semantics so it can be compared against the new
        /// injector's plan-builder during migration.
        /// </summary>
        [TestMethod]
        public void SourceMapper_IndexesAndOrdersAreStable()
        {
            string code =
                "public class P {\n" +
                "    void Draw(IMyTextSurface surface) {\n" +
                "        var frame = surface.DrawFrame();\n" +
                "        var a = MySprite.CreateText(\"A\", \"Debug\", System.Drawing.Color.White, 1f);\n" +
                "        var b = MySprite.CreateText(\"B\", \"Debug\", System.Drawing.Color.White, 1f);\n" +
                "        frame.Add(a);\n" +
                "        frame.Add(b);\n" +
                "    }\n" +
                "}\n";

            var map = SpriteSourceMapper.MapSpriteCreationSites(code);

            Assert.IsTrue(map.ContainsKey("Draw"));
            var locs = map["Draw"];
            Assert.IsTrue(locs.Count >= 2, "Expected at least the two Add invocations to be mapped.");

            // CreationIndex must be 0,1,2,... in source order with no gaps.
            for (int i = 0; i < locs.Count; i++)
                Assert.AreEqual(i, locs[i].CreationIndex, "CreationIndex must be a 0-based dense source-order sequence.");

            // Locations must be sorted by SpanStart (source order).
            for (int i = 1; i < locs.Count; i++)
                Assert.IsTrue(locs[i].SpanStart > locs[i - 1].SpanStart, "Locations must be in source order.");
        }

        /// <summary>
        /// Switch-case sprite mapping by case name. RenderHeader / RenderBar virtual
        /// methods derive from this; the mapper must locate the right case block
        /// regardless of qualified vs unqualified case labels.
        /// </summary>
        [TestMethod]
        public void SourceMapper_MapCaseBlock_HandlesQualifiedAndUnqualifiedLabels()
        {
            string code =
                "public class P {\n" +
                "    void Render() {\n" +
                "        var sprites = new List<MySprite>();\n" +
                "        switch (row.RowKind) {\n" +
                "            case LcdSpriteRow.Kind.Header:\n" +
                "                var h = MySprite.CreateText(\"HDR\", \"Debug\", System.Drawing.Color.White, 1f);\n" +
                "                sprites.Add(h);\n" +
                "                break;\n" +
                "            case Bar:\n" +
                "                sprites.Add(new MySprite(SpriteType.TEXTURE, \"SquareSimple\", new Vector2(0,0)));\n" +
                "                break;\n" +
                "        }\n" +
                "    }\n" +
                "}\n";

            var hdr = SpriteSourceMapper.MapCaseBlockSpriteCreations(code, "Header");
            var bar = SpriteSourceMapper.MapCaseBlockSpriteCreations(code, "Bar");

            Assert.IsNotNull(hdr, "Qualified label `case LcdSpriteRow.Kind.Header:` must be resolvable by name 'Header'.");
            Assert.IsTrue(hdr.Count >= 1);
            Assert.AreEqual("RenderHeader", hdr[0].MethodName);

            Assert.IsNotNull(bar, "Unqualified label `case Bar:` must be resolvable by name 'Bar'.");
            Assert.IsTrue(bar.Count >= 1);
            Assert.AreEqual("RenderBar", bar[0].MethodName);
        }
    }
}
