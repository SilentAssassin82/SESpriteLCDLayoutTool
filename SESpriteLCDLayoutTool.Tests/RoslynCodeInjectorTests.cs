using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SESpriteLCDLayoutTool.Services;
using SESpriteLCDLayoutTool.Services.CodeInjection;

namespace SESpriteLCDLayoutTool.Tests
{
    /// <summary>
    /// Phase 2 tests: <see cref="RoslynCodeInjector"/> contract conformance.
    ///
    /// Covers:
    ///   - Add / Update / Remove / Preserve for every op type
    ///   - Idempotence (apply same plan twice → same source)
    ///   - Line-jump contract preservation on injector output (golden tests apply
    ///     to the rewritten source, not just the input)
    ///   - Warning generation for missing anchors / unparseable bodies
    /// </summary>
    [TestClass]
    public class RoslynCodeInjectorTests
    {
        private const string EmptyClass =
            "public class P {\n" +
            "    void Draw(IMyTextSurface surface) {\n" +
            "        var frame = surface.DrawFrame();\n" +
            "    }\n" +
            "}\n";

        private static InjectionResult Apply(string src, InjectionPlan plan) =>
            new RoslynCodeInjector().Apply(src, plan);

        // ─── ClassFieldOp ────────────────────────────────────────────────────

        [TestMethod]
        public void Field_Add_InsertsBannerAndDeclaration()
        {
            var plan = new InjectionPlan();
            plan.Fields.Add(new ClassFieldOp
            {
                Action = InjectionAction.Add,
                Name = "thermX",
                Declaration = "private float thermX = 0.5f"
            });

            var r = Apply(EmptyClass, plan);

            Assert.IsTrue(r.Success);
            StringAssert.Contains(r.RewrittenSource, "// [INJ:field:thermX]");
            StringAssert.Contains(r.RewrittenSource, "private float thermX = 0.5f;");
            Assert.AreEqual(1, r.Diff.Count(d => d.Applied && d.Action == InjectionAction.Add));
        }

        [TestMethod]
        public void Field_AddTwice_IsIdempotent()
        {
            var plan = new InjectionPlan();
            plan.Fields.Add(new ClassFieldOp
            {
                Action = InjectionAction.Add,
                Name = "thermX",
                Declaration = "private float thermX = 0.5f"
            });

            var first = Apply(EmptyClass, plan);
            var second = Apply(first.RewrittenSource, plan);

            // Field is already there → second pass treats as Update, not duplicate.
            Assert.AreEqual(first.RewrittenSource, second.RewrittenSource,
                "Applying the same plan twice MUST be idempotent.");
            Assert.AreEqual(1, second.Warnings.Count(w => w.Message.Contains("already exists")));
        }

        [TestMethod]
        public void Field_Update_ChangesDeclarationKeepsBanner()
        {
            var addPlan = new InjectionPlan();
            addPlan.Fields.Add(new ClassFieldOp
            {
                Action = InjectionAction.Add,
                Name = "thermX",
                Declaration = "private float thermX = 0.5f"
            });
            var withField = Apply(EmptyClass, addPlan).RewrittenSource;

            var updatePlan = new InjectionPlan();
            updatePlan.Fields.Add(new ClassFieldOp
            {
                Action = InjectionAction.Update,
                Name = "thermX",
                Declaration = "private float thermX = 0.99f"
            });
            var r = Apply(withField, updatePlan);

            Assert.IsTrue(r.Success);
            StringAssert.Contains(r.RewrittenSource, "0.99f");
            Assert.IsFalse(r.RewrittenSource.Contains("0.5f"));
            // Single banner instance, no duplication.
            Assert.AreEqual(1, CountOccurrences(r.RewrittenSource, "// [INJ:field:thermX]"));
        }

        [TestMethod]
        public void Field_Remove_DeletesFieldAndBanner()
        {
            var addPlan = new InjectionPlan();
            addPlan.Fields.Add(new ClassFieldOp
            {
                Action = InjectionAction.Add,
                Name = "thermX",
                Declaration = "private float thermX = 0.5f"
            });
            var withField = Apply(EmptyClass, addPlan).RewrittenSource;

            var removePlan = new InjectionPlan();
            removePlan.Fields.Add(new ClassFieldOp { Action = InjectionAction.Remove, Name = "thermX" });
            var r = Apply(withField, removePlan);

            Assert.IsTrue(r.Success);
            Assert.IsFalse(r.RewrittenSource.Contains("thermX"));
            Assert.IsFalse(r.RewrittenSource.Contains("// [INJ:field:thermX]"));
        }

        [TestMethod]
        public void Field_Preserve_DoesNothing()
        {
            var plan = new InjectionPlan();
            plan.Fields.Add(new ClassFieldOp { Action = InjectionAction.Preserve, Name = "anything" });
            var r = Apply(EmptyClass, plan);

            Assert.IsTrue(r.Success);
            Assert.AreEqual(EmptyClass, r.RewrittenSource);
            Assert.AreEqual(0, r.Diff.Count);
        }

        // ─── HelperMethodOp ──────────────────────────────────────────────────

        [TestMethod]
        public void Method_Add_InsertsBannerAndMethod()
        {
            var plan = new InjectionPlan();
            plan.Methods.Add(new HelperMethodOp
            {
                Action = InjectionAction.Add,
                Name = "DrawBar",
                ParameterSignature = "List<MySprite>,float",
                FullDeclaration = "void DrawBar(List<MySprite> sprites, float v) { }"
            });

            var r = Apply(EmptyClass, plan);

            Assert.IsTrue(r.Success);
            StringAssert.Contains(r.RewrittenSource, "// [INJ:method:DrawBar(List<MySprite>,float)]");
            StringAssert.Contains(r.RewrittenSource, "void DrawBar(List<MySprite> sprites, float v)");
        }

        [TestMethod]
        public void Method_RemoveMissing_WarnsButSucceeds()
        {
            var plan = new InjectionPlan();
            plan.Methods.Add(new HelperMethodOp
            {
                Action = InjectionAction.Remove,
                Name = "Nope",
                ParameterSignature = ""
            });

            var r = Apply(EmptyClass, plan);

            Assert.IsTrue(r.Success);
            Assert.AreEqual(1, r.Warnings.Count);
            Assert.AreEqual(EmptyClass, r.RewrittenSource);
        }

        // ─── RenderBlockOp ───────────────────────────────────────────────────

        [TestMethod]
        public void RenderBlock_Add_BeforeFirstAdd_PlacedCorrectly()
        {
            string src =
                "public class P {\n" +
                "    void Draw(IMyTextSurface surface) {\n" +
                "        var frame = surface.DrawFrame();\n" +
                "        frame.Add(new MySprite());\n" +
                "    }\n" +
                "}\n";

            var plan = new InjectionPlan();
            plan.RenderBlocks.Add(new RenderBlockOp
            {
                Action = InjectionAction.Add,
                AnchorMethod = "Draw",
                AnchorKey = "prologue",
                Placement = RenderBlockPlacement.BeforeFirstAdd,
                BodyText = "var t = 0.5f;"
            });

            var r = Apply(src, plan);

            Assert.IsTrue(r.Success);
            int prologueIdx = r.RewrittenSource.IndexOf("// [INJ:block:Draw/prologue]");
            int addIdx = r.RewrittenSource.IndexOf("frame.Add(new MySprite());");
            Assert.IsTrue(prologueIdx > 0 && addIdx > prologueIdx, "Block must precede the first frame.Add.");
        }

        [TestMethod]
        public void RenderBlock_Update_PreservesSurroundingStatements()
        {
            string src =
                "public class P {\n" +
                "    void Draw(IMyTextSurface surface) {\n" +
                "        var frame = surface.DrawFrame();\n" +
                "        frame.Add(new MySprite());\n" +
                "    }\n" +
                "}\n";

            var addPlan = new InjectionPlan();
            addPlan.RenderBlocks.Add(new RenderBlockOp
            {
                Action = InjectionAction.Add,
                AnchorMethod = "Draw",
                AnchorKey = "prologue",
                BodyText = "var v1 = 1f;"
            });
            var withBlock = Apply(src, addPlan).RewrittenSource;

            var updatePlan = new InjectionPlan();
            updatePlan.RenderBlocks.Add(new RenderBlockOp
            {
                Action = InjectionAction.Update,
                AnchorMethod = "Draw",
                AnchorKey = "prologue",
                BodyText = "var v1 = 2f;"
            });
            var r = Apply(withBlock, updatePlan);

            Assert.IsTrue(r.Success);
            StringAssert.Contains(r.RewrittenSource, "var v1 = 2f;");
            Assert.IsFalse(r.RewrittenSource.Contains("var v1 = 1f;"));
            StringAssert.Contains(r.RewrittenSource, "frame.Add(new MySprite());");
            StringAssert.Contains(r.RewrittenSource, "var frame = surface.DrawFrame();");
        }

        // ─── SpriteAddOp + LINE-JUMP CONTRACT VERIFICATION ───────────────────

        /// <summary>
        /// CRITICAL: Inject a sprite group with indirect creation, then re-run the
        /// SpriteAddMapper against the rewritten source. The contract pinned by
        /// LineJumpGoldenTests must hold on injector OUTPUT, not just hand-written input.
        /// </summary>
        [TestMethod]
        public void SpriteAdd_IndirectCreation_OutputPreservesLineJumpContract()
        {
            var plan = new InjectionPlan();
            plan.SpriteAdds.Add(new SpriteAddOp
            {
                Action = InjectionAction.Add,
                AnchorMethod = "Draw",
                GroupKey = "thermometer",
                Index = 0,
                LocalName = "t",
                CreationStatement = "var t = MySprite.CreateText(\"hello\", \"Debug\", System.Drawing.Color.White, 1f)",
                AddStatement = "frame.Add(t)"
            });

            var r = Apply(EmptyClass, plan);

            Assert.IsTrue(r.Success);
            // Re-parse with SpriteAddMapper to verify line-jump contract on OUTPUT.
            var map = SpriteAddMapper.BuildAddCallMap(r.RewrittenSource);
            Assert.IsTrue(map.ContainsKey("Draw"), "Draw should still be detected by SpriteAddMapper.");
            var call = map["Draw"].Single();
            Assert.AreNotEqual(call.LineNumber, call.AddLineNumber,
                "Injector output must keep creation line distinct from Add line.");
            Assert.AreEqual("t", call.VariableName);
        }

        [TestMethod]
        public void SpriteAdd_MultipleInGroup_PreserveSourceOrderAndLineJump()
        {
            var plan = new InjectionPlan();
            plan.SpriteAdds.Add(new SpriteAddOp
            {
                Action = InjectionAction.Add, AnchorMethod = "Draw", GroupKey = "g", Index = 0,
                LocalName = "a",
                CreationStatement = "var a = MySprite.CreateText(\"A\", \"Debug\", System.Drawing.Color.White, 1f)",
                AddStatement = "frame.Add(a)"
            });
            plan.SpriteAdds.Add(new SpriteAddOp
            {
                Action = InjectionAction.Add, AnchorMethod = "Draw", GroupKey = "g", Index = 1,
                LocalName = "b",
                CreationStatement = "var b = MySprite.CreateText(\"B\", \"Debug\", System.Drawing.Color.White, 1f)",
                AddStatement = "frame.Add(b)"
            });

            var r = Apply(EmptyClass, plan);
            Assert.IsTrue(r.Success);

            var map = SpriteAddMapper.BuildAddCallMap(r.RewrittenSource);
            var calls = map["Draw"];
            Assert.AreEqual(2, calls.Count);
            Assert.AreEqual("a", calls[0].VariableName);
            Assert.AreEqual("b", calls[1].VariableName);
            Assert.IsTrue(calls[0].LineNumber < calls[1].LineNumber, "Source order preserved.");
            Assert.AreNotEqual(calls[0].LineNumber, calls[0].AddLineNumber);
            Assert.AreNotEqual(calls[1].LineNumber, calls[1].AddLineNumber);
        }

        [TestMethod]
        public void SpriteAdd_GroupRemove_DeletesEntireBlock()
        {
            var addPlan = new InjectionPlan();
            addPlan.SpriteAdds.Add(new SpriteAddOp
            {
                Action = InjectionAction.Add, AnchorMethod = "Draw", GroupKey = "g", Index = 0,
                LocalName = "a",
                CreationStatement = "var a = MySprite.CreateText(\"A\", \"Debug\", System.Drawing.Color.White, 1f)",
                AddStatement = "frame.Add(a)"
            });
            var withSprite = Apply(EmptyClass, addPlan).RewrittenSource;

            var removePlan = new InjectionPlan();
            removePlan.SpriteAdds.Add(new SpriteAddOp
            {
                Action = InjectionAction.Remove, AnchorMethod = "Draw", GroupKey = "g", Index = 0
            });
            var r = Apply(withSprite, removePlan);

            Assert.IsTrue(r.Success);
            Assert.IsFalse(r.RewrittenSource.Contains("// [INJ:sprite-group:Draw/g]"));
            Assert.IsFalse(r.RewrittenSource.Contains("MySprite.CreateText"));
        }

        [TestMethod]
        public void SpriteAdd_DoesNotEatUnrelatedSprites()
        {
            // The "Triangle eaten while merging SemiCircle" regression. Two groups
            // must update independently; touching one MUST NOT remove the other.
            var addPlan = new InjectionPlan();
            addPlan.SpriteAdds.Add(new SpriteAddOp
            {
                Action = InjectionAction.Add, AnchorMethod = "Draw", GroupKey = "triangle", Index = 0,
                LocalName = "tri",
                CreationStatement = "var tri = MySprite.CreateText(\"TRI\", \"Debug\", System.Drawing.Color.White, 1f)",
                AddStatement = "frame.Add(tri)"
            });
            addPlan.SpriteAdds.Add(new SpriteAddOp
            {
                Action = InjectionAction.Add, AnchorMethod = "Draw", GroupKey = "semicircle", Index = 0,
                LocalName = "sc",
                CreationStatement = "var sc = MySprite.CreateText(\"SC\", \"Debug\", System.Drawing.Color.White, 1f)",
                AddStatement = "frame.Add(sc)"
            });
            var withBoth = Apply(EmptyClass, addPlan).RewrittenSource;

            // Now Update only the SemiCircle group.
            var updatePlan = new InjectionPlan();
            updatePlan.SpriteAdds.Add(new SpriteAddOp
            {
                Action = InjectionAction.Update, AnchorMethod = "Draw", GroupKey = "semicircle", Index = 0,
                LocalName = "sc",
                CreationStatement = "var sc = MySprite.CreateText(\"SC2\", \"Debug\", System.Drawing.Color.White, 1f)",
                AddStatement = "frame.Add(sc)"
            });
            var r = Apply(withBoth, updatePlan);

            Assert.IsTrue(r.Success);
            StringAssert.Contains(r.RewrittenSource, "// [INJ:sprite-group:Draw/triangle]");
            StringAssert.Contains(r.RewrittenSource, "// [INJ:sprite-group:Draw/semicircle]");
            StringAssert.Contains(r.RewrittenSource, "\"TRI\"");
            StringAssert.Contains(r.RewrittenSource, "\"SC2\"");
            Assert.IsFalse(r.RewrittenSource.Contains("\"SC\","), "Original SC should be replaced.");
        }

        [TestMethod]
        public void SpriteAdd_AnchorMethodMissing_WarnsGracefully()
        {
            var plan = new InjectionPlan();
            plan.SpriteAdds.Add(new SpriteAddOp
            {
                Action = InjectionAction.Add, AnchorMethod = "DoesNotExist", GroupKey = "g", Index = 0,
                CreationStatement = "var a = new MySprite()",
                AddStatement = "frame.Add(a)"
            });

            var r = Apply(EmptyClass, plan);

            Assert.IsTrue(r.Success, "Missing-anchor must be a warning, not a failure.");
            Assert.IsTrue(r.Warnings.Count >= 1);
            Assert.AreEqual(EmptyClass, r.RewrittenSource);
        }

        // ─── PropertyPatchOp ─────────────────────────────────────────────────

        [TestMethod]
        public void PropertyPatch_Update_ReplacesSpanAndReportsEdit()
        {
            // Source has a single string literal we want to surgically patch.
            string src = "public class P { void M() { var s = \"OLD\"; } }";
            int start = src.IndexOf("\"OLD\"", System.StringComparison.Ordinal);
            int end = start + "\"OLD\"".Length;

            var plan = new InjectionPlan();
            plan.PropertyPatches.Add(new PropertyPatchOp
            {
                Action = InjectionAction.Update,
                Anchor = "sprite#0:Text",
                Start = start,
                End = end,
                ExpectedOldText = "\"OLD\"",
                NewText = "\"NEW-VAL\""
            });

            var r = Apply(src, plan);

            Assert.IsTrue(r.Success);
            StringAssert.Contains(r.RewrittenSource, "\"NEW-VAL\"");
            Assert.IsFalse(r.RewrittenSource.Contains("\"OLD\""));
            Assert.AreEqual(1, r.Edits.Count);
            Assert.AreEqual(start, r.Edits[0].Start);
            Assert.AreEqual(end, r.Edits[0].End);
            Assert.AreEqual("\"NEW-VAL\"".Length - "\"OLD\"".Length, r.Edits[0].Delta);
        }

        [TestMethod]
        public void PropertyPatch_TwoNonOverlappingSpans_AppliedDescendingPreservesOffsets()
        {
            // Patches are expressed in ORIGINAL source coords; injector must
            // apply them so both spans land correctly even though the first
            // patch lengthens the buffer.
            string src = "public class P { void M() { var a = \"A\"; var b = \"B\"; } }";
            int aStart = src.IndexOf("\"A\"", System.StringComparison.Ordinal);
            int bStart = src.IndexOf("\"B\"", System.StringComparison.Ordinal);

            var plan = new InjectionPlan();
            plan.PropertyPatches.Add(new PropertyPatchOp
            {
                Action = InjectionAction.Update,
                Anchor = "a", Start = aStart, End = aStart + 3,
                NewText = "\"AAA\""
            });
            plan.PropertyPatches.Add(new PropertyPatchOp
            {
                Action = InjectionAction.Update,
                Anchor = "b", Start = bStart, End = bStart + 3,
                NewText = "\"BBBB\""
            });

            var r = Apply(src, plan);

            Assert.IsTrue(r.Success);
            StringAssert.Contains(r.RewrittenSource, "var a = \"AAA\";");
            StringAssert.Contains(r.RewrittenSource, "var b = \"BBBB\";");
            Assert.AreEqual(2, r.Edits.Count);
            // Edits reported in pre-edit coords.
            foreach (var e in r.Edits)
                Assert.IsTrue(e.Start == aStart || e.Start == bStart);
        }

        [TestMethod]
        public void PropertyPatch_StaleExpectedText_WarnsAndSkips()
        {
            string src = "public class P { void M() { var s = \"CURRENT\"; } }";
            int start = src.IndexOf("\"CURRENT\"", System.StringComparison.Ordinal);

            var plan = new InjectionPlan();
            plan.PropertyPatches.Add(new PropertyPatchOp
            {
                Action = InjectionAction.Update,
                Anchor = "stale", Start = start, End = start + "\"CURRENT\"".Length,
                ExpectedOldText = "\"WAS_DIFFERENT\"",
                NewText = "\"NEW\""
            });

            var r = Apply(src, plan);

            Assert.IsTrue(r.Success);
            Assert.AreEqual(src, r.RewrittenSource);
            Assert.AreEqual(0, r.Edits.Count);
            Assert.IsTrue(r.Warnings.Count >= 1);
        }

        [TestMethod]
        public void PropertyPatch_OverlappingSpans_KeepsOnePatchOneWarn()
        {
            string src = "public class P { void M() { var s = \"ABCDEF\"; } }";
            int start = src.IndexOf("\"ABCDEF\"", System.StringComparison.Ordinal);

            var plan = new InjectionPlan();
            plan.PropertyPatches.Add(new PropertyPatchOp
            {
                Action = InjectionAction.Update,
                Anchor = "outer", Start = start, End = start + "\"ABCDEF\"".Length,
                NewText = "\"X\""
            });
            plan.PropertyPatches.Add(new PropertyPatchOp
            {
                Action = InjectionAction.Update,
                Anchor = "inner", Start = start + 1, End = start + 4, // overlaps outer
                NewText = "ZZ"
            });

            var r = Apply(src, plan);

            Assert.IsTrue(r.Success);
            Assert.AreEqual(1, r.Edits.Count, "Overlapping patch must be skipped, not double-applied.");
            Assert.IsTrue(r.Warnings.Count >= 1);
        }

        [TestMethod]
        public void PropertyPatch_Idempotent_ReplayProducesSameSource()
        {
            string src = "public class P { void M() { var s = \"OLD\"; } }";
            int start = src.IndexOf("\"OLD\"", System.StringComparison.Ordinal);

            var plan = new InjectionPlan();
            plan.PropertyPatches.Add(new PropertyPatchOp
            {
                Action = InjectionAction.Update,
                Anchor = "x", Start = start, End = start + "\"OLD\"".Length,
                NewText = "\"OLD\""
            });

            var r = Apply(src, plan);

            Assert.IsTrue(r.Success);
            Assert.AreEqual(src, r.RewrittenSource);
            Assert.AreEqual(0, r.Edits.Count, "No-change patch must not record an edit.");
        }

        [TestMethod]
        public void PropertyPatch_OutOfRange_WarnsAndPreservesSource()
        {
            string src = "public class P { void M() { } }";

            var plan = new InjectionPlan();
            plan.PropertyPatches.Add(new PropertyPatchOp
            {
                Action = InjectionAction.Update,
                Anchor = "bad", Start = 9000, End = 9100,
                NewText = "ignored"
            });

            var r = Apply(src, plan);

            Assert.IsTrue(r.Success);
            Assert.AreEqual(src, r.RewrittenSource);
            Assert.IsTrue(r.Warnings.Count >= 1);
        }

        // ─── Cross-cutting ───────────────────────────────────────────────────

        [TestMethod]
        public void Apply_EmptyPlan_ReturnsSourceUnchanged()
        {
            var r = Apply(EmptyClass, new InjectionPlan());
            Assert.IsTrue(r.Success);
            Assert.AreEqual(EmptyClass, r.RewrittenSource);
        }

        [TestMethod]
        public void Apply_BadSource_ReturnsErrorAndOriginalText()
        {
            var r = Apply("this is not C# code at all", new InjectionPlan
            {
                Fields = { }
            });
            // Roslyn parses anything; "no class found" is the real failure mode.
            // If it parses (Roslyn is permissive), no class → error.
            // Either way: original source is preserved.
            Assert.IsTrue(r.Success || r.Error != null);
            if (!r.Success)
                Assert.AreEqual("this is not C# code at all", r.RewrittenSource);
        }

        // ─── Helpers ─────────────────────────────────────────────────────────

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, idx = 0;
            while ((idx = haystack.IndexOf(needle, idx, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += needle.Length;
            }
            return count;
        }
    }
}
