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
    /// <summary>Identifies the kind of SE script being compiled.</summary>
    public enum ScriptType
    {
        /// <summary>Loose render methods with <c>List&lt;MySprite&gt;</c> first parameter.</summary>
        LcdHelper,
        /// <summary>Programmable Block script — extends <c>MyGridProgram</c>, entry point is <c>Main()</c>.</summary>
        ProgrammableBlock,
        /// <summary>Mod / plugin code with methods that accept <c>IMyTextSurface</c>.</summary>
        ModSurface,
    }

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
            public ScriptType ScriptType { get; set; }
        }

        // ── Public API ────────────────────────────────────────────────────────

        // Regex patterns used by detection (compiled once)
        private static readonly Regex _rxPbClass = new Regex(
            @"class\s+\w+\s*:\s*MyGridProgram\b", RegexOptions.Compiled);
        private static readonly Regex _rxMainMethod = new Regex(
            @"(?:public\s+)?void\s+Main\s*\(\s*(?:string\s+\w+(?:\s*,\s*UpdateType\s+\w+)?)?\s*\)",
            RegexOptions.Compiled);
        private static readonly Regex _rxSurfaceMethod = new Regex(
            @"(?:private|public|internal|protected)?\s*(?:static\s+)?void\s+(\w+)\s*\([^)]*IMyTextSurface\s+\w+[^)]*\)",
            RegexOptions.Compiled);
        private static readonly Regex _rxSpriteListMethod = new Regex(
            @"(?:private|public|internal|protected)?\s*(?:static\s+)?void\s+(\w+)\s*\(\s*List\s*<\s*MySprite\s*>\s+\w+([^)]*)\)",
            RegexOptions.Compiled);

        /// <summary>
        /// Auto-detects whether <paramref name="userCode"/> is a Programmable Block script,
        /// a mod/plugin with IMyTextSurface methods, or a plain LCD helper with List&lt;MySprite&gt; methods.
        /// </summary>
        public static ScriptType DetectScriptType(string userCode)
        {
            if (string.IsNullOrWhiteSpace(userCode)) return ScriptType.LcdHelper;

            // PB detection: class extending MyGridProgram, or Main() with SE signature
            if (_rxPbClass.IsMatch(userCode) || _rxMainMethod.IsMatch(userCode))
                return ScriptType.ProgrammableBlock;

            // Mod/surface detection: methods accepting IMyTextSurface
            if (_rxSurfaceMethod.IsMatch(userCode))
                return ScriptType.ModSurface;

            return ScriptType.LcdHelper;
        }

        /// <summary>
        /// Scans <paramref name="userCode"/> for methods whose first parameter is
        /// <c>List&lt;MySprite&gt;</c> and returns a suggested call expression such
        /// as <c>"RenderPanel(sprites, 512f, 10f, 1f)"</c> with guessed defaults.
        /// Returns <c>null</c> if nothing suitable is found.
        /// </summary>
        public static string DetectCallExpression(string userCode)
        {
            var all = DetectAllCallExpressions(userCode);
            return all.Count > 0 ? all[0] : null;
        }

        /// <summary>
        /// Scans <paramref name="userCode"/> for ALL callable entry points based on the
        /// detected script type.  For LcdHelper scripts returns expressions like
        /// <c>"RenderPanel(sprites, 512f, 10f, 1f)"</c>.  For PB scripts returns
        /// <c>"Main(\"\", UpdateType.None)"</c>.  For mod surface scripts returns
        /// expressions like <c>"DrawHUD(surface)"</c>.
        /// </summary>
        public static List<string> DetectAllCallExpressions(string userCode)
        {
            var results = new List<string>();
            if (string.IsNullOrWhiteSpace(userCode)) return results;

            var scriptType = DetectScriptType(userCode);

            switch (scriptType)
            {
                case ScriptType.ProgrammableBlock:
                    DetectPbCalls(userCode, results);
                    break;

                case ScriptType.ModSurface:
                    DetectSurfaceCalls(userCode, results);
                    // Also check for any List<MySprite> helpers in the same file
                    DetectLcdHelperCalls(userCode, results);
                    break;

                default:
                    DetectLcdHelperCalls(userCode, results);
                    break;
            }

            return results;
        }

        private static void DetectPbCalls(string userCode, List<string> results)
        {
            // Match Main(string arg, UpdateType src) / Main(string arg) / Main()
            foreach (Match m in _rxMainMethod.Matches(userCode))
            {
                string sig = m.Value;
                if (sig.Contains("UpdateType"))
                    results.Add("Main(\"\", UpdateType.None)");
                else if (sig.Contains("string"))
                    results.Add("Main(\"\")");
                else
                    results.Add("Main()");
            }

            // If no Main detected but we know it's PB, add a default
            if (results.Count == 0)
                results.Add("Main(\"\", UpdateType.None)");
        }

        private static void DetectSurfaceCalls(string userCode, List<string> results)
        {
            foreach (Match m in _rxSurfaceMethod.Matches(userCode))
            {
                string methodName = m.Groups[1].Value;
                // Parse the full parameter list to build a call
                string fullParams = m.Value;
                int parenOpen = fullParams.IndexOf('(');
                int parenClose = fullParams.LastIndexOf(')');
                if (parenOpen < 0 || parenClose < 0) continue;

                string paramList = fullParams.Substring(parenOpen + 1, parenClose - parenOpen - 1);
                var args = new List<string>();

                foreach (string param in paramList.Split(','))
                {
                    string p = param.Trim();
                    if (string.IsNullOrWhiteSpace(p)) continue;

                    string[] parts = p.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;

                    string typeName = parts[0];
                    string paramName = parts[parts.Length - 1].ToLowerInvariant().TrimStart('_');

                    if (typeName == "IMyTextSurface")
                        args.Add("surface");
                    else
                        args.Add(GuessDefaultArg(typeName.ToLowerInvariant(), paramName));
                }

                results.Add(methodName + "(" + string.Join(", ", args) + ")");
            }
        }

        private static void DetectLcdHelperCalls(string userCode, List<string> results)
        {
            foreach (Match m in _rxSpriteListMethod.Matches(userCode))
            {
                string methodName = m.Groups[1].Value;
                string restParams = m.Groups[2].Value.Trim();
                string call = BuildCallExpression(methodName, restParams);
                if (call != null) results.Add(call);
            }
        }

        /// <summary>
        /// Compiles <paramref name="userCode"/> with SE type stubs and executes
        /// <paramref name="callExpression"/>.  For LCD helpers this is a method call
        /// like <c>"RenderPanel(sprites, 512f, 10f, 1f)"</c>.  For PB scripts it is
        /// <c>"Main(\"\", UpdateType.None)"</c>.  For mod scripts it is a surface
        /// method like <c>"DrawHUD(surface)"</c>.  Returns the resulting sprite list
        /// or an error description.  Execution is capped at 5 seconds.
        /// </summary>
        public static ExecutionResult Execute(string userCode, string callExpression, int tick = 0)
        {
            if (string.IsNullOrWhiteSpace(userCode))
                return Fail("No code provided.");
            if (string.IsNullOrWhiteSpace(callExpression))
                return Fail("No call expression provided.");

            var scriptType = DetectScriptType(userCode);

            try
            {
                bool hadClassWrapper;
                string source = BuildSource(userCode, callExpression, tick, scriptType, out hadClassWrapper);
                Assembly asm = Compile(source);
                var result = RunAndConvert(asm);
                result.ScriptType = scriptType;
                return result;
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        // ── Source construction ───────────────────────────────────────────────

        private static string BuildSource(string userCode, string callExpression,
                                          int tick, ScriptType scriptType, out bool hadClassWrapper)
        {
            switch (scriptType)
            {
                case ScriptType.ProgrammableBlock:
                    return BuildPbSource(userCode, callExpression, out hadClassWrapper);
                case ScriptType.ModSurface:
                    return BuildModSurfaceSource(userCode, callExpression, out hadClassWrapper);
                default:
                    return BuildLcdHelperSource(userCode, callExpression, tick, out hadClassWrapper);
            }
        }

        // ── Shared header/footer helpers ──────────────────────────────────────

        private static void AppendSharedHeader(StringBuilder sb, string[] userUsings)
        {
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Globalization;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Text;");
            sb.AppendLine("using System.Threading;");
            sb.AppendLine();
            sb.Append(StubsSource);
            sb.AppendLine();

            var knownUsings = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "VRage.Game.GUI.TextPanel", "VRageMath", "Sandbox.ModAPI.Ingame",
                "Sandbox.ModAPI", "System", "System.Collections.Generic",
                "System.Globalization", "System.Linq", "System.Text", "System.Threading"
            };
            sb.AppendLine("namespace SELcdExec {");
            sb.AppendLine("    using VRage.Game.GUI.TextPanel;");
            sb.AppendLine("    using VRageMath;");
            sb.AppendLine("    using Sandbox.ModAPI.Ingame;");
            foreach (string u in userUsings)
                if (!knownUsings.Contains(u))
                    sb.AppendLine("    using " + u + ";");
            sb.AppendLine();
        }

        private static void AppendSpriteSerialisation(StringBuilder sb, string listVar)
        {
            sb.AppendLine("            var result = new string[" + listVar + ".Count][];");
            sb.AppendLine("            for (int i = 0; i < " + listVar + ".Count; i++) {");
            sb.AppendLine("                var sp = " + listVar + "[i];");
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
        }

        // ── LCD Helper source (original path) ────────────────────────────────

        private static string BuildLcdHelperSource(string userCode, string callExpression,
                                                   int tick, out bool hadClassWrapper)
        {
            string[] userUsings;
            string stripped = ExtractUsings(userCode, out userUsings);
            string className;
            stripped = StripClassWrapper(stripped, out hadClassWrapper, out className);
            if (hadClassWrapper && !string.IsNullOrEmpty(className))
                stripped = StripConstructors(stripped, className);

            string callLine = callExpression.TrimEnd();
            if (!callLine.EndsWith(";")) callLine += ";";

            var sb = new StringBuilder();
            AppendSharedHeader(sb, userUsings);
            sb.AppendLine("    public class LcdRunner {");

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
                sb.AppendLine("        public IMyRuntime Runtime = new StubRuntime();");
                sb.AppendLine("        public IMyProgrammableBlock Me = new StubProgrammableBlock();");
                sb.AppendLine("        public IMyGridTerminalSystem GridTerminalSystem = new StubGridTerminalSystem();");
                sb.AppendLine("        public string Storage = string.Empty;");
                sb.AppendLine("        public virtual void Save() { }");
                sb.AppendLine();
            }

            sb.AppendLine(stripped);
            sb.AppendLine();
            sb.AppendLine("        public void Echo(string text) { }");
            sb.AppendLine("        public void SaveCustomData(string d) { }");
            sb.AppendLine();
            sb.AppendLine("        public string[][] RunAllData() {");
            sb.AppendLine("            var sprites = new List<MySprite>();");
            sb.AppendLine("            " + callLine);
            AppendSpriteSerialisation(sb, "sprites");
            sb.AppendLine("        }");
            sb.AppendLine("    }"); // class LcdRunner
            sb.AppendLine("}");     // namespace
            return sb.ToString();
        }

        // ── PB script source ─────────────────────────────────────────────────

        private static string BuildPbSource(string userCode, string callExpression,
                                            out bool hadClassWrapper)
        {
            string[] userUsings;
            string stripped = ExtractUsings(userCode, out userUsings);
            string className;
            stripped = StripClassWrapper(stripped, out hadClassWrapper, out className);

            // For PB scripts we KEEP constructors — they initialise UpdateFrequency, etc.
            // However we rename them to an Init() method that RunAllData calls.
            string ctorBody = "";
            if (hadClassWrapper && !string.IsNullOrEmpty(className))
            {
                ctorBody = ExtractConstructorBody(stripped, className);
                stripped = StripConstructors(stripped, className);
            }

            string callLine = callExpression.TrimEnd();
            if (!callLine.EndsWith(";")) callLine += ";";

            var sb = new StringBuilder();
            AppendSharedHeader(sb, userUsings);

            // The runner extends MyGridProgram so that PB code referencing
            // Runtime, Me, GridTerminalSystem, Echo, etc. compiles.
            sb.AppendLine("    public class LcdRunner : MyGridProgram {");
            sb.AppendLine();

            // Wire up functional stubs
            sb.AppendLine("        private void _InitStubs() {");
            sb.AppendLine("            var gts = new StubGridTerminalSystem();");
            sb.AppendLine("            var pb = new StubProgrammableBlock();");
            sb.AppendLine("            gts.RegisterBlock(pb);");
            sb.AppendLine("            Me = pb;");
            sb.AppendLine("            Runtime = new StubRuntime();");
            sb.AppendLine("            GridTerminalSystem = gts;");
            sb.AppendLine("            Storage = string.Empty;");
            sb.AppendLine("        }");
            sb.AppendLine();

            // User methods
            sb.AppendLine(stripped);
            sb.AppendLine();

            // Entry point
            sb.AppendLine("        public string[][] RunAllData() {");
            sb.AppendLine("            _InitStubs();");
            sb.AppendLine("            SpriteCollector.Reset();");
            if (!string.IsNullOrWhiteSpace(ctorBody))
            {
                sb.AppendLine("            // Constructor body (Program()):");
                sb.AppendLine("            {");
                sb.AppendLine("                " + ctorBody);
                sb.AppendLine("            }");
            }
            sb.AppendLine("            " + callLine);
            sb.AppendLine("            var sprites = SpriteCollector.Captured;");
            AppendSpriteSerialisation(sb, "sprites");
            sb.AppendLine("        }");
            sb.AppendLine("    }"); // class LcdRunner
            sb.AppendLine("}");     // namespace
            return sb.ToString();
        }

        // ── Mod / surface script source ──────────────────────────────────────

        private static string BuildModSurfaceSource(string userCode, string callExpression,
                                                    out bool hadClassWrapper)
        {
            string[] userUsings;
            string stripped = ExtractUsings(userCode, out userUsings);
            string className;
            stripped = StripClassWrapper(stripped, out hadClassWrapper, out className);
            if (hadClassWrapper && !string.IsNullOrEmpty(className))
                stripped = StripConstructors(stripped, className);

            // Rewrite the call expression: replace `surface` token with the local stub variable
            string callLine = callExpression.TrimEnd();
            if (!callLine.EndsWith(";")) callLine += ";";
            callLine = Regex.Replace(callLine, @"\bsurface\b", "_stubSurface");

            var sb = new StringBuilder();
            AppendSharedHeader(sb, userUsings);
            sb.AppendLine("    public class LcdRunner {");
            sb.AppendLine();

            if (hadClassWrapper)
            {
                sb.AppendLine("        public IMyRuntime Runtime = new StubRuntime();");
                sb.AppendLine("        public IMyProgrammableBlock Me = new StubProgrammableBlock();");
                sb.AppendLine("        public IMyGridTerminalSystem GridTerminalSystem = new StubGridTerminalSystem();");
                sb.AppendLine("        public string Storage = string.Empty;");
                sb.AppendLine("        public virtual void Save() { }");
                sb.AppendLine();
            }

            sb.AppendLine(stripped);
            sb.AppendLine();
            sb.AppendLine("        public void Echo(string text) { }");
            sb.AppendLine("        public void SaveCustomData(string d) { }");
            sb.AppendLine();
            sb.AppendLine("        public string[][] RunAllData() {");
            sb.AppendLine("            SpriteCollector.Reset();");
            sb.AppendLine("            var _stubSurface = new StubTextSurface(512f, 512f);");
            sb.AppendLine("            " + callLine);
            sb.AppendLine("            var sprites = SpriteCollector.Captured;");
            AppendSpriteSerialisation(sb, "sprites");
            sb.AppendLine("        }");
            sb.AppendLine("    }"); // class LcdRunner
            sb.AppendLine("}");     // namespace
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
        /// Extracts the body of the first constructor matching <paramref name="className"/>
        /// from <paramref name="body"/>.  Returns an empty string if no constructor is found.
        /// This is used by PB script compilation to inline the constructor logic into RunAllData.
        /// </summary>
        private static string ExtractConstructorBody(string body, string className)
        {
            if (string.IsNullOrEmpty(className)) return "";
            var ctorRegex = new Regex(
                @"(?:public|private|protected|internal)\s+" + Regex.Escape(className)
                + @"\s*\([^)]*\)(?:\s*:[^{]+)?\s*\{",
                RegexOptions.Singleline);
            Match m = ctorRegex.Match(body);
            if (!m.Success) return "";

            int depth = 1;
            int scan = m.Index + m.Length;
            while (scan < body.Length && depth > 0)
            {
                if (body[scan] == '{') depth++;
                else if (body[scan] == '}') depth--;
                scan++;
            }
            // Extract inner body (between outer { and })
            int bodyStart = m.Index + m.Length;
            int bodyEnd = scan - 1;
            if (bodyEnd <= bodyStart) return "";
            return body.Substring(bodyStart, bodyEnd - bodyStart).Trim();
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
// ── Global sprite collector — captures sprites added via MySpriteDrawFrame ──
namespace SELcdExec
{
    using System.Collections.Generic;
    using Sandbox.ModAPI.Ingame;

    public static class SpriteCollector
    {
        [System.ThreadStatic] public static List<MySprite> Captured;
        public static void Reset()
        {
            if (Captured == null) Captured = new List<MySprite>();
            else Captured.Clear();
        }
    }
}

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
        public Color(float r, float g, float b)  { R=(byte)(r*255); G=(byte)(g*255); B=(byte)(b*255); A=255; }
        public Color(float r, float g, float b, float a) { R=(byte)(r*255); G=(byte)(g*255); B=(byte)(b*255); A=(byte)(a*255); }
        public Color(Color c, float a) { R=c.R; G=c.G; B=c.B; A=(byte)(a*255); }
        public static Color White       { get { return new Color(255,255,255); } }
        public static Color Black       { get { return new Color(0,0,0);       } }
        public static Color Red         { get { return new Color(255,0,0);     } }
        public static Color Green       { get { return new Color(0,255,0);     } }
        public static Color Blue        { get { return new Color(0,0,255);     } }
        public static Color Yellow      { get { return new Color(255,255,0);   } }
        public static Color Cyan        { get { return new Color(0,255,255);   } }
        public static Color Magenta     { get { return new Color(255,0,255);   } }
        public static Color Gray        { get { return new Color(128,128,128); } }
        public static Color Orange      { get { return new Color(255,165,0);   } }
        public static Color Transparent { get { return new Color(0,0,0,0);    } }
        public static Color operator*(Color c, float f) { return new Color((int)(c.R*f),(int)(c.G*f),(int)(c.B*f),(int)c.A); }
    }

    public enum TextAlignment { LEFT = 0, CENTER = 1, RIGHT = 2 }

    // Minimal MathHelper stub used by some PB scripts
    public static class MathHelper
    {
        public const float Pi = 3.14159265f;
        public const float TwoPi = 6.28318530f;
        public const float PiOver2 = 1.57079632f;
        public const float PiOver4 = 0.78539816f;
        public static float Clamp(float v, float min, float max) { return v < min ? min : v > max ? max : v; }
        public static float Lerp(float a, float b, float t) { return a + (b - a) * t; }
        public static float ToRadians(float degrees) { return degrees * Pi / 180f; }
        public static float ToDegrees(float radians) { return radians * 180f / Pi; }
    }
}

namespace Sandbox.ModAPI.Ingame
{
    using System;
    using System.Collections.Generic;
    using VRage.Game.GUI.TextPanel;
    using VRageMath;

    [Flags] public enum UpdateType { None = 0, Once = 128, Update1 = 16, Update10 = 32, Update100 = 64, Terminal = 256, Trigger = 512 }
    [Flags] public enum UpdateFrequency { None = 0, Update1 = 1, Update10 = 2, Update100 = 4, Once = 8 }

    // ── Functional stubs ──────────────────────────────────────────────────

    public class StubRuntime : IMyRuntime
    {
        public UpdateFrequency UpdateFrequency { get; set; }
        public double TimeSinceLastRun { get { return 0.016; } }
        public double LastRunTimeMs { get { return 0.1; } }
        public int MaxInstructionCount { get { return 50000; } }
    }

    public class StubTextSurface : IMyTextSurface
    {
        private string _text = """";
        public ContentType ContentType { get; set; }
        public Color FontColor { get; set; }
        public Color BackgroundColor { get; set; }
        public float FontSize { get; set; }
        public string Font { get; set; }
        public float TextPadding { get; set; }
        public string Script { get; set; }
        public Vector2 SurfaceSize { get; set; }
        public Vector2 TextureSize { get; set; }
        public void WriteText(string text, bool append = false) { _text = append ? _text + text : text; }
        public string ReadText() { return _text; }
        public MySpriteDrawFrame DrawFrame() { return new MySpriteDrawFrame(); }

        public StubTextSurface() : this(512f, 512f) { }
        public StubTextSurface(float w, float h)
        {
            SurfaceSize = new Vector2(w, h);
            TextureSize = new Vector2(w, h);
            FontSize = 1f;
            Font = ""White"";
            FontColor = Color.White;
            BackgroundColor = Color.Black;
        }
    }

    public interface IMyTextSurfaceProvider
    {
        IMyTextSurface GetSurface(int index);
        int SurfaceCount { get; }
    }

    public class StubTerminalBlock : IMyTerminalBlock, IMyTextSurfaceProvider
    {
        private readonly StubTextSurface[] _surfaces;
        public string CustomName { get; set; }
        public string CustomData { get; set; }
        public bool IsWorking { get { return true; } }
        public bool IsFunctional { get { return true; } }
        public long EntityId { get; set; }
        public int SurfaceCount { get { return _surfaces.Length; } }
        public IMyTextSurface GetSurface(int index) { return _surfaces[index]; }

        public StubTerminalBlock(int surfaceCount)
        {
            _surfaces = new StubTextSurface[surfaceCount];
            for (int i = 0; i < surfaceCount; i++)
                _surfaces[i] = new StubTextSurface();
            CustomName = ""LCD Panel"";
            CustomData = """";
            EntityId = 1;
        }
    }

    public class StubProgrammableBlock : StubTerminalBlock, IMyProgrammableBlock
    {
        public StubProgrammableBlock() : base(2) { CustomName = ""Programmable Block""; }
    }

    public class StubGridTerminalSystem : IMyGridTerminalSystem
    {
        private readonly List<IMyTerminalBlock> _blocks = new List<IMyTerminalBlock>();

        public void RegisterBlock(IMyTerminalBlock block) { _blocks.Add(block); }

        public void GetBlocksOfType<T>(List<T> blocks, Func<T, bool> collect = null) where T : class
        {
            blocks.Clear();
            foreach (var b in _blocks)
            {
                T typed = b as T;
                if (typed != null && (collect == null || collect(typed)))
                    blocks.Add(typed);
            }
        }

        public IMyTerminalBlock GetBlockWithId(long id)
        {
            foreach (var b in _blocks) if (b.EntityId == id) return b;
            return null;
        }
    }

    // ── Interfaces ────────────────────────────────────────────────────────

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
        public void Add(MySprite sprite) { SELcdExec.SpriteCollector.Captured.Add(sprite); }
        public void AddRange(IEnumerable<MySprite> sprites)
        {
            foreach (var s in sprites) SELcdExec.SpriteCollector.Captured.Add(s);
        }
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
        public IMyRuntime Runtime { get; set; }
        public IMyProgrammableBlock Me { get; set; }
        public IMyGridTerminalSystem GridTerminalSystem { get; set; }
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

        public static MySprite CreateSprite(string sprite, Vector2 position, Vector2 size)
        {
            MySprite sp = new MySprite();
            sp.Type=SpriteType.TEXTURE; sp.Data=sprite; sp.Position=position; sp.Size=size;
            sp.Color=VRageMath.Color.White;
            return sp;
        }
    }
}
";
    }
}
