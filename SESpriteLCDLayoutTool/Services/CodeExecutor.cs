using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using SESpriteLCDLayoutTool.Models;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Compiles and executes user LCD C# code against lightweight SE type stubs,
    /// producing a <see cref="SpriteEntry"/> list equivalent to a live snapshot
    /// — but without Space Engineers running.
    ///
    /// The executor uses <see cref="Microsoft.CSharp.CSharpCodeProvider"/> which
    /// is built into .NET Framework 4.8 and requires no extra NuGet packages.
    /// The compiled assembly is loaded in-process; user code that throws or loops
    /// infinitely is guarded by a 5-second background-thread timeout.
    /// </summary>
    public static class CodeExecutor
    {
        // ── Result type ───────────────────────────────────────────────────────

        public sealed class ExecutionResult
        {
            public List<SpriteEntry> Sprites { get; set; }
            public string Error { get; set; }
            public bool Success { get { return Error == null; } }
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Scans <paramref name="userCode"/> for methods whose first parameter is
        /// <c>List&lt;MySprite&gt;</c> and returns a suggested call expression such
        /// as <c>"RenderPanel(sprites, 512f, 10f, 1f)"</c> with guessed defaults.
        /// Returns <c>null</c> if nothing suitable is found.
        /// </summary>
        public static string DetectCallExpression(string userCode)
        {
            if (string.IsNullOrWhiteSpace(userCode)) return null;

            // Find void MethodName(List<MySprite> anyName, optional params…)
            var pattern = new Regex(
                @"(?:private|public|internal|protected)?\s*(?:static\s+)?void\s+(\w+)\s*\(\s*List\s*<\s*MySprite\s*>\s+\w+([^)]*)\)",
                RegexOptions.Compiled);

            foreach (Match m in pattern.Matches(userCode))
            {
                string methodName = m.Groups[1].Value;
                string restParams = m.Groups[2].Value.Trim();
                string call = BuildCallExpression(methodName, restParams);
                if (call != null) return call;
            }
            return null;
        }

        /// <summary>
        /// Compiles <paramref name="userCode"/> with SE type stubs and executes
        /// <paramref name="callExpression"/> (a C# statement such as
        /// <c>"RenderPanel(sprites, 512f, 10f, 1f)"</c>).  Returns the resulting
        /// sprite list or an error description.  Execution is capped at 5 seconds.
        /// </summary>
        public static ExecutionResult Execute(string userCode, string callExpression, int tick = 0)
        {
            if (string.IsNullOrWhiteSpace(userCode))
                return Fail("No code provided.");
            if (string.IsNullOrWhiteSpace(callExpression))
                return Fail("No call expression provided.");

            try
            {
                bool hadClassWrapper;
                string source = BuildSource(userCode, callExpression, tick, out hadClassWrapper);
                Assembly asm = Compile(source);
                return RunAndConvert(asm);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        // ── Source construction ───────────────────────────────────────────────

        private static string BuildSource(string userCode, string callExpression,
                                          int tick, out bool hadClassWrapper)
        {
            // Extract user's using-directives; strip class/namespace wrapper; remove constructors
            string[] userUsings;
            string stripped = ExtractUsings(userCode, out userUsings);
            string className;
            stripped = StripClassWrapper(stripped, out hadClassWrapper, out className);
            if (hadClassWrapper && !string.IsNullOrEmpty(className))
                stripped = StripConstructors(stripped, className);

            // Ensure call expression ends with a semicolon
            string callLine = callExpression.TrimEnd();
            if (!callLine.EndsWith(";")) callLine += ";";

            var sb = new StringBuilder();

            // ── Top-level imports ─────────────────────────────────────────────
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Globalization;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Text;");
            sb.AppendLine("using System.Threading;");
            sb.AppendLine();

            // ── SE type stubs ─────────────────────────────────────────────────
            sb.Append(StubsSource);
            sb.AppendLine();

            // ── Runner class ──────────────────────────────────────────────────
            var knownUsings = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "VRage.Game.GUI.TextPanel", "VRageMath", "Sandbox.ModAPI.Ingame",
                "System", "System.Collections.Generic", "System.Globalization",
                "System.Linq", "System.Text", "System.Threading"
            };
            sb.AppendLine("namespace SELcdExec {");
            sb.AppendLine("    using VRage.Game.GUI.TextPanel;");
            sb.AppendLine("    using VRageMath;");
            sb.AppendLine("    using Sandbox.ModAPI.Ingame;");
            foreach (string u in userUsings)
                if (!knownUsings.Contains(u))
                    sb.AppendLine("    using " + u + ";");
            sb.AppendLine();
            sb.AppendLine("    public class LcdRunner {");

            // Default LCD-script fields — only inject when no class body was extracted
            // (avoids duplicate-field compile errors if the user's class already declares them)
            if (!hadClassWrapper)
            {
                sb.AppendLine("        public int _tick = " + tick + ";");
                sb.AppendLine("        public float _h2 = 0.5f;");
                sb.AppendLine("        public float _o2 = 0.8f;");
                sb.AppendLine("        public float _power = 0.75f;");
                sb.AppendLine("        public bool _alert = false;");
                sb.AppendLine("        public string _status = \"ONLINE\";");
                sb.AppendLine("        public float _cargo = 0.5f;");
                sb.AppendLine("        public int _count = 0;");
                sb.AppendLine();
            }
            else
            {
                // SE PB inherited members so stripped class body can access them
                sb.AppendLine("        public IMyRuntime Runtime = null;");
                sb.AppendLine("        public IMyProgrammableBlock Me = null;");
                sb.AppendLine("        public IMyGridTerminalSystem GridTerminalSystem = null;");
                sb.AppendLine("        public string Storage = string.Empty;");
                sb.AppendLine("        public virtual void Save() { }");
                sb.AppendLine();
            }

            // ── User methods ──────────────────────────────────────────────────
            sb.AppendLine(stripped);
            sb.AppendLine();

            // ── Common SE no-op stubs so code that calls Echo/SaveCustomData compiles ──
            sb.AppendLine("        public void Echo(string text) { }");
            sb.AppendLine("        public void SaveCustomData(string d) { }");
            sb.AppendLine();

            // ── Executor entry point ──────────────────────────────────────────
            // Returns sprites as string[][] so we don't need deep struct reflection.
            // Columns: type, data, posX, posY, sizeW, sizeH, R, G, B, A, font, rotOrScale, align
            sb.AppendLine("        public string[][] RunAllData() {");
            sb.AppendLine("            var sprites = new List<MySprite>();");
            sb.AppendLine("            " + callLine);
            sb.AppendLine("            var result = new string[sprites.Count][];");
            sb.AppendLine("            for (int i = 0; i < sprites.Count; i++) {");
            sb.AppendLine("                var sp = sprites[i];");
            sb.AppendLine("                var spPos = sp.Position;");
            sb.AppendLine("                var spSz  = sp.Size;");
            sb.AppendLine("                var spCol = sp.Color;");
            sb.AppendLine("                result[i] = new string[] {");
            sb.AppendLine("                    sp.Type == SpriteType.TEXT ? \"TEXT\" : \"TEXTURE\",");
            sb.AppendLine("                    sp.Data ?? \"\",");
            sb.AppendLine("                    spPos.HasValue ? spPos.Value.X.ToString(CultureInfo.InvariantCulture) : \"\",");
            sb.AppendLine("                    spPos.HasValue ? spPos.Value.Y.ToString(CultureInfo.InvariantCulture) : \"\",");
            sb.AppendLine("                    spSz.HasValue  ? spSz.Value.X.ToString(CultureInfo.InvariantCulture)  : \"\",");
            sb.AppendLine("                    spSz.HasValue  ? spSz.Value.Y.ToString(CultureInfo.InvariantCulture)  : \"\",");
            sb.AppendLine("                    spCol.HasValue ? spCol.Value.R.ToString() : \"255\",");
            sb.AppendLine("                    spCol.HasValue ? spCol.Value.G.ToString() : \"255\",");
            sb.AppendLine("                    spCol.HasValue ? spCol.Value.B.ToString() : \"255\",");
            sb.AppendLine("                    spCol.HasValue ? spCol.Value.A.ToString() : \"255\",");
            sb.AppendLine("                    sp.FontId ?? \"White\",");
            sb.AppendLine("                    sp.RotationOrScale.ToString(CultureInfo.InvariantCulture),");
            sb.AppendLine("                    ((int)sp.Alignment).ToString()");
            sb.AppendLine("                };");
            sb.AppendLine("            }");
            sb.AppendLine("            return result;");
            sb.AppendLine("        }");

            sb.AppendLine("    }"); // class LcdRunner
            sb.AppendLine("}");     // namespace SELcdExec

            return sb.ToString();
        }

        // ── Compilation ───────────────────────────────────────────────────────

        // Cached path to the VS Roslyn csc.exe (discovered once via vswhere.exe)
        private static string _cachedCscPath;
        private static bool _cscSearched;

        private static Assembly Compile(string source)
        {
            if (!_cscSearched)
            {
                _cachedCscPath = FindRoslynCsc();
                _cscSearched = true;
            }

            if (_cachedCscPath != null)
                return CompileWithRoslyn(source, _cachedCscPath);

            throw new InvalidOperationException(
                "Could not locate the Roslyn C# compiler (csc.exe).\n"
                + "Please ensure Visual Studio 2019 or later is installed.");
        }

        /// <summary>
        /// Locates the Roslyn csc.exe from the VS installation via vswhere.exe.
        /// Returns null if VS is not found.
        /// </summary>
        private static string FindRoslynCsc()
        {
            try
            {
                string vswhere = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Microsoft Visual Studio", "Installer", "vswhere.exe");

                if (!File.Exists(vswhere)) return null;

                var psi = new ProcessStartInfo(vswhere, "-latest -property installationPath")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                };

                string vsPath;
                using (var proc = Process.Start(psi))
                {
                    vsPath = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(5000);
                }

                if (string.IsNullOrWhiteSpace(vsPath)) return null;

                string csc = Path.Combine(vsPath, "MSBuild", "Current", "Bin", "Roslyn", "csc.exe");
                return File.Exists(csc) ? csc : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Compiles <paramref name="source"/> using the VS Roslyn csc.exe, loads the
        /// resulting assembly from bytes (so the temp file can be deleted), and returns it.
        /// </summary>
        private static Assembly CompileWithRoslyn(string source, string cscPath)
        {
            // Reference the standard .NET Framework assemblies from the runtime directory
            string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            string refs = string.Join(" ",
                "/reference:\"" + Path.Combine(runtimeDir, "System.dll") + "\"",
                "/reference:\"" + Path.Combine(runtimeDir, "System.Core.dll") + "\"");

            string tempSrc = Path.ChangeExtension(Path.GetTempFileName(), ".cs");
            string tempDll = Path.ChangeExtension(Path.GetTempFileName(), ".dll");
            try
            {
                File.WriteAllText(tempSrc, source, Encoding.UTF8);

                string args = string.Format(
                    "/target:library /optimize+ /langversion:7.3 /nologo {0} /out:\"{1}\" \"{2}\"",
                    refs, tempDll, tempSrc);

                var psi = new ProcessStartInfo(cscPath, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                string output;
                int exitCode;
                using (var proc = Process.Start(psi))
                {
                    output = proc.StandardOutput.ReadToEnd()
                           + proc.StandardError.ReadToEnd();
                    proc.WaitForExit(30000);
                    exitCode = proc.ExitCode;
                }

                if (exitCode != 0)
                {
                    // Strip temp file path from error messages for readability
                    output = output.Replace(tempSrc, "<user code>");
                    throw new InvalidOperationException("Compilation errors:\n" + output.TrimEnd());
                }

                // Load as byte array so we can delete the temp DLL immediately
                byte[] dllBytes = File.ReadAllBytes(tempDll);
                return Assembly.Load(dllBytes);
            }
            finally
            {
                try { File.Delete(tempSrc); } catch { }
                try { File.Delete(tempDll); } catch { }
            }
        }

        // ── Execution + conversion ─────────────────────────────────────────────

        private static ExecutionResult RunAndConvert(Assembly asm)
        {
            Type runnerType = asm.GetType("SELcdExec.LcdRunner");
            if (runnerType == null)
                return Fail("Internal error: LcdRunner type not found.");

            object runner = Activator.CreateInstance(runnerType);
            MethodInfo runAll = runnerType.GetMethod("RunAllData");
            if (runAll == null)
                return Fail("Internal error: RunAllData method not found.");

            // Execute on a background thread with a 5-second timeout guard
            string[][] rawData = null;
            Exception runEx = null;

            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    rawData = (string[][])runAll.Invoke(runner, null);
                }
                catch (Exception ex)
                {
                    runEx = ex.InnerException ?? ex;
                }
            });
            thread.IsBackground = true;
            thread.Start();

            if (!thread.Join(5000))
            {
                thread.Abort();
                return Fail("Code execution timed out after 5 s — possible infinite loop.");
            }

            if (runEx != null)
                return Fail("Runtime error: " + runEx.Message);
            if (rawData == null)
                return Fail("No sprite data returned.");

            return new ExecutionResult { Sprites = ConvertFromMatrix(rawData) };
        }

        private static List<SpriteEntry> ConvertFromMatrix(string[][] matrix)
        {
            var sprites = new List<SpriteEntry>();
            foreach (string[] row in matrix)
            {
                if (row == null || row.Length < 13) continue;
                try
                {
                    bool isText = row[0] == "TEXT";

                    var entry = new SpriteEntry
                    {
                        Type = isText ? SpriteEntryType.Text : SpriteEntryType.Texture,
                        SourceStart = -1, // orphan — no static parse source tracking
                    };

                    if (isText)
                        entry.Text = row[1];
                    else
                        entry.SpriteName = string.IsNullOrEmpty(row[1]) ? "SquareSimple" : row[1];

                    if (!string.IsNullOrEmpty(row[2]))
                        entry.X = float.Parse(row[2], CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(row[3]))
                        entry.Y = float.Parse(row[3], CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(row[4]))
                        entry.Width = float.Parse(row[4], CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(row[5]))
                        entry.Height = float.Parse(row[5], CultureInfo.InvariantCulture);

                    entry.ColorR = int.Parse(row[6]);
                    entry.ColorG = int.Parse(row[7]);
                    entry.ColorB = int.Parse(row[8]);
                    entry.ColorA = int.Parse(row[9]);

                    entry.FontId = string.IsNullOrEmpty(row[10]) ? "White" : row[10];

                    float rotOrScale = float.Parse(row[11], CultureInfo.InvariantCulture);
                    if (isText)
                    {
                        entry.Scale = rotOrScale;
                        entry.Rotation = 0f;
                    }
                    else
                    {
                        entry.Rotation = rotOrScale;
                        entry.Scale = 1f;
                    }

                    int alignVal = int.Parse(row[12]);
                    entry.Alignment = alignVal == 1 ? SpriteTextAlignment.Center
                                    : alignVal == 2 ? SpriteTextAlignment.Right
                                    : SpriteTextAlignment.Left;

                    sprites.Add(entry);
                }
                catch { /* skip malformed row */ }
            }
            return sprites;
        }

        // ── Call-expression helpers ───────────────────────────────────────────

        private static string BuildCallExpression(string methodName, string restParams)
        {
            if (string.IsNullOrWhiteSpace(restParams))
                return methodName + "(sprites)";

            var args = new List<string> { "sprites" };

            // Split on commas — simple split is fine (we won't see generics here)
            foreach (string param in restParams.Split(','))
            {
                string p = param.Trim();
                if (string.IsNullOrWhiteSpace(p)) continue;

                string[] parts = p.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                string typeName  = parts[0].ToLowerInvariant();
                string paramName = parts[parts.Length - 1].ToLowerInvariant().TrimStart('_');
                args.Add(GuessDefaultArg(typeName, paramName));
            }

            return methodName + "(" + string.Join(", ", args) + ")";
        }

        private static string GuessDefaultArg(string typeName, string paramName)
        {
            if (typeName == "float" || typeName == "double")
            {
                if (paramName == "w" || paramName.Contains("width"))  return "512f";
                if (paramName == "h" || paramName.Contains("height")) return "512f";
                if (paramName.Contains("pad"))                         return "10f";
                if (paramName == "top" || paramName == "y")            return "10f";
                if (paramName == "sc"  || paramName.Contains("scale")
                                       || paramName.Contains("zoom"))  return "1f";
                return "0f";
            }
            if (typeName == "int")    return "0";
            if (typeName == "bool")   return "false";
            if (typeName == "string") return "\"\"";
            return "default";
        }

        // ── Source pre-processing ─────────────────────────────────────────────

        private static string ExtractUsings(string code, out string[] extractedUsings)
        {
            var usings = new List<string>();
            foreach (Match m in Regex.Matches(code,
                @"^\s*using\s+([\w\.]+)\s*;\s*\r?\n?", RegexOptions.Multiline))
                usings.Add(m.Groups[1].Value);
            extractedUsings = usings.ToArray();
            return Regex.Replace(code,
                @"^\s*using\s+[\w\.]+\s*;\s*\r?\n?", "", RegexOptions.Multiline);
        }

        private static string StripConstructors(string body, string className)
        {
            if (string.IsNullOrEmpty(className)) return body;
            var ctorRegex = new Regex(
                @"(?:public|private|protected|internal)\s+" + Regex.Escape(className)
                + @"\s*\([^)]*\)(?:\s*:[^{]+)?\s*\{",
                RegexOptions.Singleline);
            var result = new StringBuilder();
            int pos = 0;
            Match m = ctorRegex.Match(body);
            while (m.Success)
            {
                result.Append(body, pos, m.Index - pos);
                int depth = 1;
                int scan = m.Index + m.Length;
                while (scan < body.Length && depth > 0)
                {
                    if (body[scan] == '{') depth++;
                    else if (body[scan] == '}') depth--;
                    scan++;
                }
                pos = scan;
                m = ctorRegex.Match(body, pos);
            }
            result.Append(body, pos, body.Length - pos);
            return result.ToString();
        }

        /// <summary>
        /// If <paramref name="code"/> contains a class definition, extracts its body
        /// (the content between the outermost braces) so the methods can be injected
        /// directly into <c>LcdRunner</c>.  Sets <paramref name="hadWrapper"/> to true
        /// and <paramref name="className"/> to the class name when a wrapper was stripped.
        /// </summary>
        private static string StripClassWrapper(string code, out bool hadWrapper, out string className)
        {
            var classMatch = Regex.Match(code,
                @"(?:public|private|internal|protected)?\s*" +
                @"(?:static|sealed|abstract|partial)?\s*" +
                @"class\s+(\w+)[\w\s:,<>]*\{",
                RegexOptions.Singleline);

            if (!classMatch.Success)
            {
                hadWrapper = false;
                className = null;
                return code;
            }

            className = classMatch.Groups[1].Value;
            int bodyStart = classMatch.Index + classMatch.Length;
            int depth = 1;
            int pos = bodyStart;
            while (pos < code.Length && depth > 0)
            {
                if (code[pos] == '{') depth++;
                else if (code[pos] == '}') depth--;
                pos++;
            }

            hadWrapper = true;
            return code.Substring(bodyStart, pos - 1 - bodyStart);
        }

        // ── Misc helpers ──────────────────────────────────────────────────────

        private static ExecutionResult Fail(string error)
        {
            return new ExecutionResult { Error = error };
        }

        // ── SE Type Stubs ─────────────────────────────────────────────────────
        // These mirror the minimal subset of the Space Engineers API used by
        // LCD rendering code.  They are compiled together with the user's code.
        // Each stub lives in its real SE namespace so that user code that has
        //   using VRageMath;
        //   using VRage.Game.GUI.TextPanel;
        //   using Sandbox.ModAPI.Ingame;
        // works without modification.

        private const string StubsSource = @"
namespace VRage.Game.GUI.TextPanel
{
    public enum SpriteType { TEXTURE = 0, TEXT = 1, CLIP_RECT = 2 }
    public enum ContentType { NONE = 0, TEXT_AND_IMAGE = 1, SCRIPT = 2 }
}

namespace VRageMath
{
    using System;

    public struct Vector2
    {
        public float X, Y;
        public Vector2(float x, float y) { X = x; Y = y; }
        public static Vector2 operator+(Vector2 a, Vector2 b) { return new Vector2(a.X+b.X, a.Y+b.Y); }
        public static Vector2 operator-(Vector2 a, Vector2 b) { return new Vector2(a.X-b.X, a.Y-b.Y); }
        public static Vector2 operator*(Vector2 a, float f)   { return new Vector2(a.X*f,   a.Y*f);   }
        public static Vector2 operator*(float f,   Vector2 a) { return new Vector2(a.X*f,   a.Y*f);   }
        public static Vector2 operator/(Vector2 a, float f)   { return new Vector2(a.X/f,   a.Y/f);   }
        public static Vector2 operator-(Vector2 a)            { return new Vector2(-a.X,    -a.Y);     }
        public static bool operator==(Vector2 a, Vector2 b)   { return a.X==b.X && a.Y==b.Y; }
        public static bool operator!=(Vector2 a, Vector2 b)   { return !(a==b); }
        public override bool Equals(object obj) { return obj is Vector2 && this==(Vector2)obj; }
        public override int GetHashCode() { return X.GetHashCode() ^ Y.GetHashCode(); }
        public float Length() { return (float)Math.Sqrt(X*X+Y*Y); }
        public float LengthSquared() { return X*X+Y*Y; }
        public static Vector2 Zero { get { return new Vector2(0,0); } }
        public static Vector2 One  { get { return new Vector2(1,1); } }
        public override string ToString() { return X+"",""+Y; }
    }

    public struct Color
    {
        public byte R, G, B, A;
        public Color(int r, int g, int b)        { R=(byte)r; G=(byte)g; B=(byte)b; A=255;     }
        public Color(int r, int g, int b, int a) { R=(byte)r; G=(byte)g; B=(byte)b; A=(byte)a; }
        public static Color White       { get { return new Color(255,255,255); } }
        public static Color Black       { get { return new Color(0,0,0);       } }
        public static Color Red         { get { return new Color(255,0,0);     } }
        public static Color Green       { get { return new Color(0,255,0);     } }
        public static Color Blue        { get { return new Color(0,0,255);     } }
        public static Color Transparent { get { return new Color(0,0,0,0);    } }
    }

    public enum TextAlignment { LEFT = 0, CENTER = 1, RIGHT = 2 }
}

namespace Sandbox.ModAPI.Ingame
{
    using System;
    using System.Collections.Generic;
    using VRage.Game.GUI.TextPanel;
    using VRageMath;

    [Flags] public enum UpdateType { None = 0, Once = 128, Update1 = 16, Update10 = 32, Update100 = 64, Terminal = 256, Trigger = 512 }
    [Flags] public enum UpdateFrequency { None = 0, Update1 = 1, Update10 = 2, Update100 = 4, Once = 8 }

    public interface IMyRuntime
    {
        UpdateFrequency UpdateFrequency { get; set; }
        double TimeSinceLastRun { get; }
        double LastRunTimeMs { get; }
        int MaxInstructionCount { get; }
    }

    public interface IMyTerminalBlock
    {
        string CustomName { get; set; }
        string CustomData { get; set; }
        bool IsWorking { get; }
        bool IsFunctional { get; }
        long EntityId { get; }
    }

    public interface IMyTextSurface
    {
        ContentType ContentType { get; set; }
        Color FontColor { get; set; }
        Color BackgroundColor { get; set; }
        float FontSize { get; set; }
        string Font { get; set; }
        float TextPadding { get; set; }
        Vector2 SurfaceSize { get; }
        Vector2 TextureSize { get; }
        void WriteText(string text, bool append = false);
        string ReadText();
        MySpriteDrawFrame DrawFrame();
    }

    public struct MySpriteDrawFrame : IDisposable
    {
        public void Add(MySprite sprite) { }
        public void AddRange(IEnumerable<MySprite> sprites) { }
        public void Dispose() { }
    }

    public interface IMyProgrammableBlock : IMyTerminalBlock
    {
        IMyTextSurface GetSurface(int index);
        int SurfaceCount { get; }
    }

    public interface IMyGridTerminalSystem
    {
        void GetBlocksOfType<T>(List<T> blocks, Func<T, bool> collect = null) where T : class;
        IMyTerminalBlock GetBlockWithId(long id);
    }

    public class MyGridProgram
    {
        public IMyRuntime Runtime { get; protected set; }
        public IMyProgrammableBlock Me { get; protected set; }
        public IMyGridTerminalSystem GridTerminalSystem { get; protected set; }
        public string Storage { get; set; }
        public void Echo(string text) { }
        protected virtual void Save() { }
    }

    public struct MySprite
    {
        public SpriteType    Type;
        public string        Data;
        public Vector2?      Position;
        public Vector2?      Size;
        public Color?        Color;
        public string        FontId;
        public float         RotationOrScale;
        public TextAlignment Alignment;

        public MySprite(SpriteType type, string data, Vector2 position, Vector2 size, Color color)
        {
            Type=type; Data=data; Position=position; Size=size; Color=color;
            FontId=null; RotationOrScale=0f; Alignment=TextAlignment.LEFT;
        }

        public static MySprite CreateText(string text, string font, Color color,
                                          float scale, TextAlignment alignment)
        {
            MySprite sp = new MySprite();
            sp.Type=SpriteType.TEXT; sp.Data=text; sp.FontId=font;
            sp.Color=color; sp.RotationOrScale=scale; sp.Alignment=alignment;
            return sp;
        }
    }
}
";
    }
}
