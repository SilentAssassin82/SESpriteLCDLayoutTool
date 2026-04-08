using System;

namespace SESpriteLCDLayoutTool.Models
{
    /// <summary>
    /// Represents a detected callable method or switch-case block in LCD rendering code.
    /// Stores both the call expression and source code position for jump-to-code navigation.
    /// </summary>
    public class DetectedMethodInfo
    {
        /// <summary>
        /// The call expression to execute (e.g., "RenderHeader(default, default)").
        /// </summary>
        public string CallExpression { get; set; }

        /// <summary>
        /// Character position of the method definition or case block in the source code.
        /// For real methods, this is the start of the method signature.
        /// For switch cases, this is the position of the "case" keyword.
        /// -1 if position tracking is unavailable.
        /// </summary>
        public int SourcePosition { get; set; }

        /// <summary>
        /// Whether this represents a real method or a virtual method extracted from a switch case.
        /// </summary>
        public MethodKind Kind { get; set; }

        /// <summary>
        /// For switch-case methods, the name of the case (e.g., "Header", "Separator").
        /// For real methods, null or empty.
        /// </summary>
        public string CaseName { get; set; }

        /// <summary>
        /// Whether this method can be executed directly via the built-in executor.
        /// Both real methods and switch-case virtual methods are executable —
        /// virtual methods execute the full pipeline with filtered captured rows.
        /// </summary>
        public bool IsExecutable => true;

        public DetectedMethodInfo()
        {
            SourcePosition = -1;
            Kind = MethodKind.RealMethod;
        }

        public override string ToString() => CallExpression ?? "";
    }

    public enum MethodKind
    {
        /// <summary>
        /// A real method detected via signature matching (List&lt;MySprite&gt;, IMyTextSurface, etc.).
        /// </summary>
        RealMethod,

        /// <summary>
        /// A virtual method extracted from a switch statement case block.
        /// Represents inline rendering logic that can be jumped to but not executed.
        /// </summary>
        SwitchCase,
    }
}
