namespace SESpriteLCDLayoutTool.Services.CodeInjection
{
    /// <summary>
    /// Compile-time / runtime feature gates for the code-injector refactor.
    ///
    /// As of step 6, the keyframe-group and simple-animation merge paths are
    /// permanently routed through the new <see cref="ICodeInjector"/>. Their
    /// per-caller toggles are gone; the only remaining gate is
    /// <see cref="ShadowCompare"/>, kept around for diagnosing future caller
    /// migrations.
    /// </summary>
    public static class CodeInjectionFeatureFlags
    {
        /// <summary>
        /// When true, callers that opt in run the new <see cref="ICodeInjector"/>
        /// pipeline alongside legacy fallback logic and log a structured diff.
        /// Used for parity validation only — does not change returned values.
        /// </summary>
        public static bool ShadowCompare { get; set; } = false;
    }
}
