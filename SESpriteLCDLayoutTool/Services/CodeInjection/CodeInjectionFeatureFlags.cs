namespace SESpriteLCDLayoutTool.Services.CodeInjection
{
    /// <summary>
    /// Compile-time / runtime feature gates for the code-injector refactor.
    ///
    /// Phase 1: all flags default OFF — the new pipeline is dormant code only.
    /// Phase 2: enable <see cref="ShadowCompare"/> to log side-by-side diffs of
    ///          new-injector output vs legacy engines without applying them.
    /// Phase 3+: flip <see cref="UseNewInjectorForKeyframeGroups"/> etc. as each
    ///          legacy caller is migrated.
    ///
    /// Keep this file the SOLE source of truth for migration toggles.
    /// </summary>
    public static class CodeInjectionFeatureFlags
    {
        /// <summary>
        /// When true, new-pipeline callers run the new <see cref="ICodeInjector"/>
        /// AND the legacy engine, log a structured diff, then return the LEGACY
        /// output. Used during Phase 2 to validate parity with zero behaviour change.
        /// </summary>
        public static bool ShadowCompare { get; set; } = false;

        /// <summary>Phase 3 toggle: keyframe-group injection (first migration target).</summary>
        public static bool UseNewInjectorForKeyframeGroups { get; set; } = false;

        /// <summary>Phase 3+ toggle: animation snippet injection.</summary>
        public static bool UseNewInjectorForAnimationSnippets { get; set; } = false;

        /// <summary>Phase 3+ toggle: template gallery injection.</summary>
        public static bool UseNewInjectorForTemplates { get; set; } = false;

        /// <summary>Phase 3+ toggle: round-trip merging of edited sprites back into source.</summary>
        public static bool UseNewInjectorForRoundTripMerge { get; set; } = false;
    }
}
