using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SESpriteLCDLayoutTool.Services.CodeInjection
{
    /// <summary>
    /// Phase 2 instrumentation. When <see cref="CodeInjectionFeatureFlags.ShadowCompare"/>
    /// is enabled, callers route through this runner to execute BOTH the legacy
    /// engine and the new <see cref="ICodeInjector"/>, log a structured diff, and
    /// return the LEGACY output unchanged. Zero behaviour change in production.
    ///
    /// The point is to gather real-world parity evidence on actual user layouts
    /// before any caller is migrated behind a UseNewInjectorFor* flag.
    /// </summary>
    public static class ShadowCompareRunner
    {
        /// <summary>
        /// Optional sink — defaults to writing under
        /// <c>%LOCALAPPDATA%\SESpriteLCDLayoutTool\shadow-compare\</c>.
        /// Tests can replace this with an in-memory delegate.
        /// </summary>
        public static Action<string, string> LogSink { get; set; } = DefaultFileSink;

        /// <summary>
        /// Run the new injector alongside the legacy result. Returns the legacy
        /// output unchanged; emits a diff log entry whenever the two disagree.
        /// </summary>
        /// <param name="callerTag">Short identifier for the caller (e.g. "KeyframeGroups").</param>
        /// <param name="originalSource">Pre-mutation source text.</param>
        /// <param name="plan">The structured plan describing what the legacy engine just did.</param>
        /// <param name="legacyOutput">The output the legacy engine actually produced.</param>
        /// <returns><paramref name="legacyOutput"/>, always.</returns>
        public static string CompareAndReturnLegacy(
            string callerTag,
            string originalSource,
            InjectionPlan plan,
            string legacyOutput)
        {
            if (!CodeInjectionFeatureFlags.ShadowCompare)
                return legacyOutput;

            if (string.IsNullOrEmpty(callerTag)) callerTag = "Unknown";
            if (originalSource == null) originalSource = string.Empty;
            if (legacyOutput == null) legacyOutput = string.Empty;
            if (plan == null) plan = new InjectionPlan();

            InjectionResult newResult;
            try
            {
                newResult = new RoslynCodeInjector().Apply(originalSource, plan);
            }
            catch (Exception ex)
            {
                SafeLog(callerTag, "INJECTOR-THREW", ex.ToString());
                return legacyOutput;
            }

            string newOutput = newResult.RewrittenSource ?? string.Empty;

            if (string.Equals(newOutput, legacyOutput, StringComparison.Ordinal))
            {
                SafeLog(callerTag, "MATCH", BuildSummary(plan, newResult));
                return legacyOutput;
            }

            var sb = new StringBuilder();
            sb.AppendLine(BuildSummary(plan, newResult));
            sb.AppendLine("--- legacy");
            sb.AppendLine("+++ new");
            sb.AppendLine(BuildLineDiff(legacyOutput, newOutput, contextLines: 2, maxHunks: 50));
            SafeLog(callerTag, "MISMATCH", sb.ToString());

            return legacyOutput;
        }

        // ─── Logging ─────────────────────────────────────────────────────────

        private static void SafeLog(string callerTag, string verdict, string body)
        {
            try
            {
                var sink = LogSink;
                if (sink == null) return;
                sink(callerTag + ":" + verdict, body ?? string.Empty);
            }
            catch
            {
                // Shadow-compare must never break the host. Swallow.
            }
        }

        private static void DefaultFileSink(string header, string body)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SESpriteLCDLayoutTool", "shadow-compare");
            Directory.CreateDirectory(dir);

            string file = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd") + ".log");
            string line =
                "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + header +
                Environment.NewLine + body + Environment.NewLine + new string('-', 60) +
                Environment.NewLine;
            File.AppendAllText(file, line, Encoding.UTF8);
        }

        // ─── Summary + diff helpers ──────────────────────────────────────────

        private static string BuildSummary(InjectionPlan plan, InjectionResult result)
        {
            int ops = plan.Fields.Count + plan.Methods.Count + plan.RenderBlocks.Count + plan.SpriteAdds.Count;
            int applied = result.Diff?.Count(d => d.Applied) ?? 0;
            int warns = result.Warnings?.Count ?? 0;
            return "ops=" + ops + " applied=" + applied + " warnings=" + warns +
                   (result.Success ? "" : " ERROR=" + result.Error);
        }

        /// <summary>
        /// Tiny line-level diff. Not a full LCS — just emits hunks where the two
        /// inputs disagree, with a small fixed context window. Sufficient for
        /// human-eyeball parity review during Phase 2.
        /// </summary>
        private static string BuildLineDiff(string left, string right, int contextLines, int maxHunks)
        {
            string[] a = left.Split('\n');
            string[] b = right.Split('\n');

            var sb = new StringBuilder();
            int i = 0, j = 0, hunks = 0;
            int max = Math.Max(a.Length, b.Length);

            while (i < max && j < max && hunks < maxHunks)
            {
                if (i < a.Length && j < b.Length && a[i] == b[j])
                {
                    i++; j++;
                    continue;
                }

                int hunkStartA = Math.Max(0, i - contextLines);
                int hunkStartB = Math.Max(0, j - contextLines);

                for (int k = hunkStartA; k < i && k < a.Length; k++)
                    sb.Append("  ").AppendLine(a[k].TrimEnd('\r'));

                while (i < a.Length && (j >= b.Length || a[i] != b[j]))
                {
                    sb.Append("- ").AppendLine(a[i].TrimEnd('\r'));
                    i++;
                    if (i - hunkStartA > 200) break; // safety
                }
                while (j < b.Length && (i >= a.Length || a.ElementAtOrDefault(i) != b[j]))
                {
                    sb.Append("+ ").AppendLine(b[j].TrimEnd('\r'));
                    j++;
                    if (j - hunkStartB > 200) break;
                }

                hunks++;
            }

            if (hunks >= maxHunks) sb.AppendLine("... (diff truncated)");
            return sb.ToString();
        }
    }
}
