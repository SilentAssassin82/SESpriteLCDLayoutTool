using Microsoft.VisualStudio.TestTools.UnitTesting;
using SESpriteLCDLayoutTool.Services;
using SESpriteLCDLayoutTool.Services.CodeInjection;

namespace SESpriteLCDLayoutTool.Tests
{
    /// <summary>
    /// Step 6 wire-in tests. The new <see cref="ICodeInjector"/> pipeline is
    /// the permanent primary path for <see cref="AnimationSnippetGenerator.MergeSimpleAnimIntoCode"/>.
    /// These tests assert the production wrapper produces the expected merged
    /// output and falls back to legacy without throwing on null/garbage inputs.
    /// Byte-level legacy parity is covered by <c>MergeSimpleAnimShadowParityTests</c>.
    /// </summary>
    [TestClass]
    public class MergeSimpleAnimWireInTests
    {
        [TestInitialize]
        public void Setup()
        {
            CodeInjectionFeatureFlags.ShadowCompare = false;
        }

        [TestCleanup]
        public void Teardown()
        {
            CodeInjectionFeatureFlags.ShadowCompare = false;
        }

        private const string ExistingRotate =
            "// ─── Animation: Rotate \"\" [LcdHelper] ───\n" +
            "int _tick = 0;\n" +
            "void Render(List<MySprite> sprites) {\n" +
            "    _tick++;\n" +
            "    sprites.Add(new MySprite {\n" +
            "        Type = SpriteType.TEXTURE,\n" +
            "        Data = \"Circle\",\n" +
            "        RotationOrScale = _tick * 0.05f,\n" +
            "    });\n" +
            "}\n";

        private const string NewRotateSnippet =
            "// ─── Animation: Rotate \"\" [LcdHelper] ───\n" +
            "int _tick = 0;\n" +
            "_tick++;\n" +
            "sprites.Add(new MySprite {\n" +
            "    Type = SpriteType.TEXTURE,\n" +
            "    Data = \"Circle\",\n" +
            "    RotationOrScale = _tick * 0.10f,\n" +
            "});\n";

        private const string ExistingOscillate =
            "// ─── Animation: Oscillate \"\" [LcdHelper] ───\n" +
            "int _tick = 0;\n" +
            "void Render(List<MySprite> sprites) {\n" +
            "    _tick++;\n" +
            "    float oscOffset = (float)System.Math.Sin(_tick * 0.05f) * 10f;\n" +
            "    sprites.Add(new MySprite {\n" +
            "        Type = SpriteType.TEXTURE,\n" +
            "        Data = \"Circle\",\n" +
            "        Position = new Vector2(0f + oscOffset, 0f),\n" +
            "    });\n" +
            "}\n";

        private const string NewOscillateSnippet =
            "// ─── Animation: Oscillate \"\" [LcdHelper] ───\n" +
            "int _tick = 0;\n" +
            "_tick++;\n" +
            "float oscOffset = (float)System.Math.Sin(_tick * 0.10f) * 25f;\n" +
            "sprites.Add(new MySprite {\n" +
            "    Type = SpriteType.TEXTURE,\n" +
            "    Data = \"Circle\",\n" +
            "    Position = new Vector2(0f + oscOffset, 0f),\n" +
            "});\n";

        [TestMethod]
        public void Wired_AddBlockOnly_ProducesExpectedOutput()
        {
            string result = AnimationSnippetGenerator.MergeSimpleAnimIntoCode(
                ExistingRotate, NewRotateSnippet);

            Assert.IsNotNull(result);
            StringAssert.Contains(result, "_tick * 0.10f");
        }

        [TestMethod]
        public void Wired_VarLineAndAddBlock_ProducesExpectedOutput()
        {
            string result = AnimationSnippetGenerator.MergeSimpleAnimIntoCode(
                ExistingOscillate, NewOscillateSnippet);

            Assert.IsNotNull(result);
            StringAssert.Contains(result, "* 0.10f) * 25f");
        }

        [TestMethod]
        public void Wired_NullInputs_FallsBackToLegacyWithoutThrowing()
        {
            string r1 = AnimationSnippetGenerator.MergeSimpleAnimIntoCode(null, NewRotateSnippet);
            string r2 = AnimationSnippetGenerator.MergeSimpleAnimIntoCode(ExistingRotate, null);

            Assert.IsNull(r1);
            Assert.IsNull(r2);
        }

        [TestMethod]
        public void Wired_DifferentAnimType_FallsBackToLegacyNull()
        {
            string result = AnimationSnippetGenerator.MergeSimpleAnimIntoCode(
                ExistingRotate, NewOscillateSnippet);

            Assert.IsNull(result, "When legacy core returns null, the wrapper must too.");
        }

        [TestMethod]
        public void Wired_Idempotent_RunningTwiceProducesSameOutput()
        {
            string first = AnimationSnippetGenerator.MergeSimpleAnimIntoCode(
                ExistingOscillate, NewOscillateSnippet);

            string second = AnimationSnippetGenerator.MergeSimpleAnimIntoCode(
                first, NewOscillateSnippet);

            Assert.IsNotNull(first);
            Assert.IsNull(second,
                "Re-applying the same merge produces no further change (legacy returns null).");
        }
    }
}
