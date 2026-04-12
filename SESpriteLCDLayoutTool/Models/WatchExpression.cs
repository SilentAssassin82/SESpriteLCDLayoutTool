using System;

namespace SESpriteLCDLayoutTool.Models
{
    /// <summary>
    /// Represents a user-defined watch expression that gets evaluated each animation tick.
    /// The expression is compiled once via Roslyn and invoked repeatedly with fresh field values.
    /// </summary>
    public class WatchExpression
    {
        /// <summary>
        /// The raw C# expression text entered by the user (e.g., "counter % 10", "angle > 180").
        /// </summary>
        public string Expression { get; set; }

        /// <summary>
        /// The compiled evaluation delegate. Takes an object[] of field values (same order as
        /// the field snapshot used during compilation) and returns the boxed result.
        /// Null if compilation failed or hasn't been attempted yet.
        /// </summary>
        public Func<object[], object> CompiledEval { get; set; }

        /// <summary>
        /// The last successfully evaluated result, formatted as a display string.
        /// </summary>
        public string LastValue { get; set; }

        /// <summary>
        /// The type name of the last evaluated result (e.g., "Int32", "Boolean", "String").
        /// </summary>
        public string LastTypeName { get; set; }

        /// <summary>
        /// If compilation or evaluation failed, contains the error message.
        /// Null/empty when the expression is valid.
        /// </summary>
        public string Error { get; set; }

        /// <summary>
        /// Whether this expression compiled successfully and is ready for evaluation.
        /// </summary>
        public bool IsCompiled => CompiledEval != null;

        /// <summary>
        /// The field names (in order) that were used when this expression was compiled.
        /// If the field set changes, the expression needs recompilation.
        /// </summary>
        public string[] CompiledFieldNames { get; set; }
    }
}
