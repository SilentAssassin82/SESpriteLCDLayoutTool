using Microsoft.VisualStudio.TestTools.UnitTesting;
using SESpriteLCDLayoutTool.Services;
using SESpriteLCDLayoutTool.Services.CodeInjection;

namespace SESpriteLCDLayoutTool.Tests
{
    /// <summary>
    /// Step 6 wire-in tests. The new <see cref="ICodeInjector"/> pipeline is
    /// the permanent primary path for <see cref="AnimationSnippetGenerator.MergeKeyframedIntoCode"/>.
    /// These tests assert the production wrapper produces the expected merged
    /// output and falls back to legacy without throwing on null/garbage inputs.
    /// Byte-level legacy parity is covered by <c>MergeKeyframedShadowParityTests</c>.
    /// </summary>
    [TestClass]
    public class MergeKeyframedWireInTests
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

        private const string ExistingProgram =
            "public class P\n" +
            "{\n" +
            "    int _tick = 0;\n" +
            "    int[] kfTick = { 0, 30, 60 };\n" +
            "    float[] kfRot = { 0f, 1.5f, 3f };\n" +
            "    void M()\n" +
            "    {\n" +
            "        _tick = _tick % 60;\n" +
            "    }\n" +
            "}\n";

        [TestMethod]
        public void Wired_ValueSwap_ProducesExpectedOutput()
        {
            const string snippet =
                "int _tick = 0;\n" +
                "int[] kfTick = { 0, 45, 60 };\n" +
                "float[] kfRot = { 0f, 2.25f, 3f };\n" +
                "_tick = _tick % 60;\n";

            string result = AnimationSnippetGenerator.MergeKeyframedIntoCode(ExistingProgram, snippet);

            Assert.IsNotNull(result);
            StringAssert.Contains(result, "{ 0, 45, 60 }");
            StringAssert.Contains(result, "{ 0f, 2.25f, 3f }");
        }

        [TestMethod]
        public void Wired_LengthChange_RewritesArraysAndModulo()
        {
            const string snippet =
                "int _tick = 0;\n" +
                "int[] kfTick = { 0, 30, 60, 90 };\n" +
                "float[] kfRot = { 0f, 1f, 2f, 3f };\n" +
                "_tick = _tick % 90;\n";

            string result = AnimationSnippetGenerator.MergeKeyframedIntoCode(ExistingProgram, snippet);

            Assert.IsNotNull(result);
            StringAssert.Contains(result, "{ 0, 30, 60, 90 }");
            StringAssert.Contains(result, "% 90");
        }

        [TestMethod]
        public void Wired_NullInputs_FallsBackToLegacyWithoutThrowing()
        {
            string r1 = AnimationSnippetGenerator.MergeKeyframedIntoCode(null, "int[] kfTick = { 0, 1 };");
            string r2 = AnimationSnippetGenerator.MergeKeyframedIntoCode(ExistingProgram, null);

            Assert.IsNull(r1);
            Assert.IsNull(r2);
        }

        [TestMethod]
        public void Wired_GarbageInjectorInput_FallsBackToLegacy()
        {
            string result = AnimationSnippetGenerator.MergeKeyframedIntoCode(
                ExistingProgram, "// no arrays here");

            Assert.IsNull(result, "When legacy core returns null, the wrapper must too.");
        }

        [TestMethod]
        public void Wired_Idempotent_RunningTwiceProducesSameOutput()
        {
            const string snippet =
                "int _tick = 0;\n" +
                "int[] kfTick = { 0, 45, 60 };\n" +
                "float[] kfRot = { 0f, 2.25f, 3f };\n" +
                "_tick = _tick % 60;\n";

            string first = AnimationSnippetGenerator.MergeKeyframedIntoCode(ExistingProgram, snippet);
            string second = AnimationSnippetGenerator.MergeKeyframedIntoCode(first, snippet);

            Assert.AreEqual(first, second,
                "Re-applying the same merge must be idempotent.");
        }
    }
}
