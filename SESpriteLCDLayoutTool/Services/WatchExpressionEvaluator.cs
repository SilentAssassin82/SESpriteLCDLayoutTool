using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using SESpriteLCDLayoutTool.Models;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Compiles and evaluates user-defined watch expressions against the live fields
    /// of a running animation script. Each expression is compiled once into a delegate
    /// and invoked each tick with fresh field values.
    /// </summary>
    public static class WatchExpressionEvaluator
    {
        /// <summary>
        /// Compiles a watch expression against a set of known field names and types.
        /// Generates a static wrapper method that casts elements from an object[] into
        /// typed locals, evaluates the expression, and returns the boxed result.
        /// </summary>
        /// <param name="watch">The watch expression to compile. On success, sets CompiledEval and CompiledFieldNames.</param>
        /// <param name="fieldNames">Ordered field names from the runner instance.</param>
        /// <param name="fieldTypes">Ordered field types corresponding to fieldNames.</param>
        public static void Compile(WatchExpression watch, string[] fieldNames, Type[] fieldTypes)
        {
            if (watch == null) return;

            watch.CompiledEval = null;
            watch.CompiledFieldNames = null;
            watch.Error = null;

            if (string.IsNullOrWhiteSpace(watch.Expression))
            {
                watch.Error = "Empty expression";
                return;
            }

            try
            {
                string source = BuildWrapperSource(watch.Expression, fieldNames, fieldTypes);
                Assembly asm = CodeExecutor.Compile(source);

                Type evalType = asm.GetType("__WatchEval");
                if (evalType == null)
                {
                    watch.Error = "Internal error: compiled type not found";
                    return;
                }

                MethodInfo evalMethod = evalType.GetMethod("Eval", BindingFlags.Public | BindingFlags.Static);
                if (evalMethod == null)
                {
                    watch.Error = "Internal error: Eval method not found";
                    return;
                }

                watch.CompiledEval = (Func<object[], object>)Delegate.CreateDelegate(
                    typeof(Func<object[], object>), evalMethod);
                watch.CompiledFieldNames = (string[])fieldNames.Clone();
            }
            catch (Exception ex)
            {
                // Extract just the compiler error messages, strip internal details
                string msg = ex.Message;
                int errIdx = msg.IndexOf("error CS", StringComparison.Ordinal);
                if (errIdx >= 0)
                    msg = msg.Substring(errIdx);

                // Trim to first error line for compact display
                int nl = msg.IndexOf('\n');
                if (nl > 0)
                    msg = msg.Substring(0, nl).Trim();

                watch.Error = msg;
            }
        }

        /// <summary>
        /// Evaluates a compiled watch expression with the given field values.
        /// Updates LastValue, LastTypeName, and Error on the watch.
        /// </summary>
        /// <param name="watch">The watch expression (must be compiled).</param>
        /// <param name="fieldValues">Field values in the same order as CompiledFieldNames.</param>
        public static void Evaluate(WatchExpression watch, object[] fieldValues)
        {
            if (watch == null || !watch.IsCompiled)
                return;

            try
            {
                object result = watch.CompiledEval(fieldValues);
                watch.LastValue = FormatValue(result);
                watch.LastTypeName = result?.GetType().Name ?? "null";
                watch.Error = null;
            }
            catch (Exception ex)
            {
                watch.LastValue = null;
                watch.LastTypeName = null;
                watch.Error = $"Runtime: {ex.InnerException?.Message ?? ex.Message}";
            }
        }

        /// <summary>
        /// Compiles and immediately evaluates a watch expression. Convenience method
        /// for one-shot evaluation (e.g., after Execute Code).
        /// </summary>
        public static void CompileAndEvaluate(WatchExpression watch,
            string[] fieldNames, Type[] fieldTypes, object[] fieldValues)
        {
            Compile(watch, fieldNames, fieldTypes);
            if (watch.IsCompiled)
                Evaluate(watch, fieldValues);
        }

        /// <summary>
        /// Checks whether a watch needs recompilation because the field set changed.
        /// </summary>
        public static bool NeedsRecompile(WatchExpression watch, string[] currentFieldNames)
        {
            if (!watch.IsCompiled || watch.CompiledFieldNames == null)
                return true;
            if (watch.CompiledFieldNames.Length != currentFieldNames.Length)
                return true;
            for (int i = 0; i < currentFieldNames.Length; i++)
            {
                if (watch.CompiledFieldNames[i] != currentFieldNames[i])
                    return true;
            }
            return false;
        }

        // ── Internal helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Builds the C# source for a wrapper class that evaluates the user expression.
        /// The generated code looks like:
        /// <code>
        /// public static class __WatchEval {
        ///     public static object Eval(object[] __f) {
        ///         int counter = (int)__f[0];
        ///         float angle = (float)__f[1];
        ///         // ...
        ///         return (object)(counter % 10);
        ///     }
        /// }
        /// </code>
        /// </summary>
        private static string BuildWrapperSource(string expression, string[] fieldNames, Type[] fieldTypes)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine();
            sb.AppendLine("public static class __WatchEval {");
            sb.AppendLine("    public static object Eval(object[] __f) {");

            // Generate typed local variables from the field array
            for (int i = 0; i < fieldNames.Length; i++)
            {
                string name = SanitizeIdentifier(fieldNames[i]);
                string typeName = GetFriendlyTypeName(fieldTypes[i]);

                // Use dynamic for types that are hard to reference (game types, etc.)
                // For basic types, use proper casts for better IntelliSense in expressions
                if (IsBasicType(fieldTypes[i]))
                {
                    sb.AppendLine($"        {typeName} {name} = __f[{i}] != null ? ({typeName})__f[{i}] : default({typeName});");
                }
                else
                {
                    // Use object for complex types - user can still call .ToString(), etc.
                    sb.AppendLine($"        object {name} = __f[{i}];");
                }
            }

            sb.AppendLine();
            sb.AppendLine($"        return (object)({expression});");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private static string SanitizeIdentifier(string name)
        {
            // Strip leading underscores for cleaner access
            // but keep the original if stripping would create a conflict or empty name
            if (name.StartsWith("_") && name.Length > 1)
            {
                // Don't strip — user needs to reference the actual field name
                // (they type _counter to access _counter)
            }

            // Replace invalid chars with underscore
            var sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsLetterOrDigit(c) || c == '_')
                    sb.Append(c);
                else
                    sb.Append('_');
            }

            string result = sb.ToString();

            // Prefix with _ if starts with digit
            if (result.Length > 0 && char.IsDigit(result[0]))
                result = "_" + result;

            return string.IsNullOrEmpty(result) ? "_field" : result;
        }

        private static bool IsBasicType(Type t)
        {
            if (t == null) return false;
            return t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ||
                   t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte) ||
                   t == typeof(float) || t == typeof(double) || t == typeof(decimal) ||
                   t == typeof(bool) || t == typeof(char) || t == typeof(string);
        }

        private static string GetFriendlyTypeName(Type t)
        {
            if (t == null) return "object";
            if (t == typeof(int)) return "int";
            if (t == typeof(long)) return "long";
            if (t == typeof(short)) return "short";
            if (t == typeof(byte)) return "byte";
            if (t == typeof(uint)) return "uint";
            if (t == typeof(ulong)) return "ulong";
            if (t == typeof(ushort)) return "ushort";
            if (t == typeof(sbyte)) return "sbyte";
            if (t == typeof(float)) return "float";
            if (t == typeof(double)) return "double";
            if (t == typeof(decimal)) return "decimal";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(char)) return "char";
            if (t == typeof(string)) return "string";
            return "object";
        }

        private static string FormatValue(object value)
        {
            if (value == null) return "(null)";
            if (value is string s) return $"\"{s}\"";
            if (value is bool b) return b ? "true" : "false";
            if (value is float f) return f.ToString("G6");
            if (value is double d) return d.ToString("G6");
            return value.ToString();
        }
    }
}
