using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SESpriteLCDLayoutTool.Services.CodeInjection;

namespace SESpriteLCDLayoutTool.Tests
{
    /// <summary>
    /// Phase 2 instrumentation tests. <see cref="ShadowCompareRunner"/> must:
    ///   - Be a no-op when the flag is OFF (returns legacy unchanged, no logging).
    ///   - Always return the legacy output, even on injector mismatch or throw.
    ///   - Log MATCH when outputs agree, MISMATCH when they don't.
    ///   - Never throw, regardless of input.
    /// </summary>
    [TestClass]
    public class ShadowCompareRunnerTests
    {
        private List<string> _headers;
        private List<string> _bodies;

        [TestInitialize]
        public void Setup()
        {
            _headers = new List<string>();
            _bodies = new List<string>();
            ShadowCompareRunner.LogSink = (h, b) => { _headers.Add(h); _bodies.Add(b); };
            CodeInjectionFeatureFlags.ShadowCompare = false;
        }

        [TestCleanup]
        public void Teardown()
        {
            CodeInjectionFeatureFlags.ShadowCompare = false;
            ShadowCompareRunner.LogSink = null;
        }

        [TestMethod]
        public void FlagOff_ReturnsLegacy_AndDoesNotLog()
        {
            string legacy = "anything";
            string result = ShadowCompareRunner.CompareAndReturnLegacy(
                "Test", "src", new InjectionPlan(), legacy);

            Assert.AreEqual(legacy, result);
            Assert.AreEqual(0, _headers.Count);
        }

        [TestMethod]
        public void FlagOn_EmptyPlan_LogsMatch()
        {
            CodeInjectionFeatureFlags.ShadowCompare = true;
            const string src = "public class P { }\n";

            string result = ShadowCompareRunner.CompareAndReturnLegacy(
                "Test", src, new InjectionPlan(), src);

            Assert.AreEqual(src, result);
            Assert.AreEqual(1, _headers.Count);
            StringAssert.Contains(_headers[0], "MATCH");
        }

        [TestMethod]
        public void FlagOn_LegacyDiffersFromInjector_LogsMismatch_StillReturnsLegacy()
        {
            CodeInjectionFeatureFlags.ShadowCompare = true;
            const string src = "public class P { }\n";
            string fakeLegacy = "public class P { /* legacy did something */ }\n";

            string result = ShadowCompareRunner.CompareAndReturnLegacy(
                "Test", src, new InjectionPlan(), fakeLegacy);

            Assert.AreEqual(fakeLegacy, result, "Runner must always return legacy output.");
            Assert.AreEqual(1, _headers.Count);
            StringAssert.Contains(_headers[0], "MISMATCH");
            StringAssert.Contains(_bodies[0], "--- legacy");
            StringAssert.Contains(_bodies[0], "+++ new");
        }

        [TestMethod]
        public void FlagOn_NullInputs_DoNotThrow()
        {
            CodeInjectionFeatureFlags.ShadowCompare = true;

            string result = ShadowCompareRunner.CompareAndReturnLegacy(
                null, null, null, null);

            Assert.AreEqual(string.Empty, result ?? string.Empty);
        }

        [TestMethod]
        public void FlagOn_LogSinkThrows_RunnerSwallows()
        {
            CodeInjectionFeatureFlags.ShadowCompare = true;
            ShadowCompareRunner.LogSink = (h, b) => { throw new System.InvalidOperationException("boom"); };

            string legacy = "x";
            // Must not propagate.
            string result = ShadowCompareRunner.CompareAndReturnLegacy(
                "Test", "x", new InjectionPlan(), legacy);

            Assert.AreEqual(legacy, result);
        }
    }
}
