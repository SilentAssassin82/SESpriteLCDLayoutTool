namespace SESpriteLCDLayoutTool.Models
{
    /// <summary>
    /// Represents a single debug variable streamed from a plugin via the
    /// <c>// @DebugVar: name (type) = value</c> snapshot comment protocol.
    /// Parsed by <see cref="Services.CodeParser.ParseDebugVars"/> and displayed
    /// in the Variables tab when a live stream is active.
    /// </summary>
    public class DebugVariable
    {
        /// <summary>Variable / field name as declared in the plugin (e.g. "_itemCount").</summary>
        public string Name { get; set; }

        /// <summary>Short CLR type name (e.g. "Int32", "String", "Boolean", "Single").</summary>
        public string TypeName { get; set; }

        /// <summary>The raw string representation of the value, exactly as serialised.</summary>
        public string RawValue { get; set; }

        /// <summary>
        /// Returns a typed object parsed from <see cref="RawValue"/> when the type
        /// is a well-known primitive, otherwise returns the raw string.
        /// This allows <c>GetColorForType</c> and <c>FormatFieldValue</c> in MainForm
        /// to apply the same colour coding used for animation-inspected fields.
        /// </summary>
        public object TypedValue
        {
            get
            {
                if (string.IsNullOrEmpty(RawValue) || string.IsNullOrEmpty(TypeName))
                    return RawValue;

                switch (TypeName)
                {
                    case "Int32":
                        int i; return int.TryParse(RawValue, out i) ? (object)i : RawValue;
                    case "Int64":
                        long l; return long.TryParse(RawValue, out l) ? (object)l : RawValue;
                    case "Int16":
                        short sh; return short.TryParse(RawValue, out sh) ? (object)sh : RawValue;
                    case "Byte":
                        byte b; return byte.TryParse(RawValue, out b) ? (object)b : RawValue;
                    case "Single":
                        float f; return float.TryParse(RawValue,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out f) ? (object)f : RawValue;
                    case "Double":
                        double d; return double.TryParse(RawValue,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out d) ? (object)d : RawValue;
                    case "Boolean":
                        bool bv; return bool.TryParse(RawValue, out bv) ? (object)bv : RawValue;
                    case "String":
                        // Strip surrounding quotes if present
                        if (RawValue.Length >= 2 && RawValue[0] == '"' && RawValue[RawValue.Length - 1] == '"')
                            return RawValue.Substring(1, RawValue.Length - 2);
                        return RawValue;
                    default:
                        return RawValue;
                }
            }
        }
    }
}
