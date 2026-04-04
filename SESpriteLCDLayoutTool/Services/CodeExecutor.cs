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
            @"(?:private|public|internal|protected)?\s*(?:static\s+)?void\s+(\w+)\s*\([^)]*(?:IMyTextSurface|IMyTextPanel)\s+\w+[^)]*\)",
            RegexOptions.Compiled);
        private static readonly Regex _rxSpriteListMethod = new Regex(
            @"(?:private|public|internal|protected)?\s*(?:static\s+)?void\s+(\w+)\s*\(\s*List\s*<\s*MySprite\s*>\s+\w+([^)]*)\)",
            RegexOptions.Compiled);

        // Matches void methods with no params whose name indicates a state-update
        // method: Advance, Update, Tick, Simulate, Step, etc.
        // Excludes OnTick/OnUpdate/OnStep — these are timer/event wrappers that
        // typically call the direct methods, causing double-advance if both match.
        private static readonly Regex _rxStateUpdateMethod = new Regex(
            @"(?:private|public|internal|protected)?\s*(?:static\s+)?void\s+(Advance|Update|Tick|Simulate|Step|DoUpdate|ProcessTick)\s*\(\s*\)",
            RegexOptions.Compiled);

        // Matches methods that RETURN List<MySprite> — these are orchestrators
        // (e.g. BuildSprites(Vector2 surfaceSize)) that call render methods internally
        // with correct positions.  Preferred over individual render method calls.
        private static readonly Regex _rxSpriteReturnMethod = new Regex(
            @"(?:private|public|internal|protected)?\s*(?:static\s+)?List\s*<\s*MySprite\s*>\s+(\w+)\s*\(([^)]*)\)",
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
        /// detected script type.  For LcdHelper scripts, first checks for orchestrator
        /// methods that return <c>List&lt;MySprite&gt;</c> (e.g. <c>BuildSprites</c>).
        /// If found, these are preferred because they call render methods internally
        /// with correct positions.  Falls back to individual render method detection
        /// with guessed parameter defaults.
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
                    // List orchestrators first (e.g. BuildSprites), then individual
                    // render methods.  The user can select any single method to
                    // animate in isolation, or leave the default (all) for the full scene.
                    var returnCalls = DetectSpriteReturnCalls(userCode);
                    results.AddRange(returnCalls);
                    // Collect method names from orchestrators so we can skip duplicates
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string rc in returnCalls)
                    {
                        // Extract method name from "sprites = MethodName(...)"
                        int eq = rc.IndexOf('=');
                        if (eq >= 0)
                        {
                            string after = rc.Substring(eq + 1).Trim();
                            int paren = after.IndexOf('(');
                            if (paren > 0) seen.Add(after.Substring(0, paren).Trim());
                        }
                    }
                    // Add individual render methods that weren't already listed as orchestrators
                    var helperCalls = new List<string>();
                    DetectLcdHelperCalls(userCode, helperCalls);
                    foreach (string hc in helperCalls)
                    {
                        // Extract method name from "MethodName(sprites, ...)"
                        int paren = hc.IndexOf('(');
                        string name = paren > 0 ? hc.Substring(0, paren).Trim() : hc;
                        if (!seen.Contains(name))
                            results.Add(hc);
                    }
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
        /// Detects state-update methods (Advance, Update, Tick, etc.) that
        /// should be called before rendering methods each frame so animation state
        /// actually changes between frames.  Timer wrappers like OnTick are
        /// excluded to avoid double-advancing state.
        /// </summary>
        public static List<string> DetectStateUpdateCalls(string userCode)
        {
            var results = new List<string>();
            if (string.IsNullOrWhiteSpace(userCode)) return results;

            foreach (Match m in _rxStateUpdateMethod.Matches(userCode))
            {
                string methodName = m.Groups[1].Value;
                results.Add(methodName + "()");
            }
            return results;
        }

        /// <summary>
        /// Detects methods that RETURN <c>List&lt;MySprite&gt;</c> — these are
        /// orchestrators (e.g. <c>BuildSprites(Vector2 surfaceSize)</c>) that call
        /// render methods internally with correct positions.  When found, these
        /// should be preferred over calling individual render methods with guessed
        /// parameter defaults.  Returns call expressions like
        /// <c>"sprites = BuildSprites(new Vector2(512f, 512f))"</c>.
        /// </summary>
        public static List<string> DetectSpriteReturnCalls(string userCode)
        {
            var results = new List<string>();
            if (string.IsNullOrWhiteSpace(userCode)) return results;

            foreach (Match m in _rxSpriteReturnMethod.Matches(userCode))
            {
                string methodName = m.Groups[1].Value;
                string paramDecl = m.Groups[2].Value.Trim();
                string call = BuildReturnCallExpression(methodName, paramDecl);
                if (call != null) results.Add(call);
            }
            return results;
        }

        private static string BuildReturnCallExpression(string methodName, string paramDecl)
        {
            if (string.IsNullOrWhiteSpace(paramDecl))
                return "sprites = " + methodName + "()";

            var args = new List<string>();
            foreach (string param in paramDecl.Split(','))
            {
                string p = param.Trim();
                if (string.IsNullOrWhiteSpace(p)) continue;

                string[] parts = p.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                string typeName = parts[0];
                string paramName = parts[parts.Length - 1].ToLowerInvariant().TrimStart('_');

                if (typeName == "Vector2")
                    args.Add("new Vector2(512f, 512f)");
                else
                    args.Add(GuessDefaultArg(typeName.ToLowerInvariant(), paramName));
            }
            return "sprites = " + methodName + "(" + string.Join(", ", args) + ")";
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

        // ── Animation API ─────────────────────────────────────────────────────

        /// <summary>
        /// Holds the compiled assembly and live runner instance for multi-frame
        /// animation playback of any script type.
        /// </summary>
        public sealed class AnimationContext : IDisposable
        {
            internal Assembly CompiledAssembly;
            internal object Runner;
            internal Type RunnerType;
            internal MethodInfo InitMethod;
            internal MethodInfo FrameMethod;
            internal MethodInfo GetFreqMethod;
            internal MethodInfo SetElapsedMethod;
            public ScriptType ScriptType { get; internal set; }

            public void Dispose()
            {
                Runner = null;
                CompiledAssembly = null;
            }
        }

        /// <summary>
        /// Compiles a script for animated (multi-frame) playback.
        /// The returned <see cref="AnimationContext"/> keeps the runner instance alive
        /// so that fields, counters, and <c>Storage</c> persist across ticks.
        /// <para>For PB scripts, <paramref name="callExpression"/> may be null (auto-detects Main).
        /// For LCD Helper and Mod scripts, a call expression may be null to auto-detect
        /// all rendering methods and call them all each frame.</para>
        /// </summary>
        public static AnimationContext CompileForAnimation(string userCode, string callExpression = null)
        {
            var scriptType = DetectScriptType(userCode);

            // Auto-detect call expressions when none is specified (non-PB).
            // For LcdHelper scripts, prefer orchestrators (methods returning List<MySprite>)
            // which call individual render methods internally with correct positions.
            // Using both would duplicate sprites since the orchestrator already invokes them.
            if (string.IsNullOrWhiteSpace(callExpression) && scriptType != ScriptType.ProgrammableBlock)
            {
                List<string> animCalls;
                if (scriptType == ScriptType.LcdHelper)
                {
                    // Orchestrators first; fall back to individual render methods
                    animCalls = DetectSpriteReturnCalls(userCode);
                    if (animCalls.Count == 0)
                    {
                        animCalls = new List<string>();
                        DetectLcdHelperCalls(userCode, animCalls);
                    }
                }
                else
                {
                    animCalls = DetectAllCallExpressions(userCode);
                }
                if (animCalls.Count == 0)
                    throw new InvalidOperationException(
                        "No rendering methods detected in the script.\n"
                        + "Add a method that accepts IMyTextSurface or List<MySprite>,\n"
                        + "or enter a call expression manually in the '▶ Call:' box.");
                callExpression = string.Join("\n", animCalls);
            }

            // Detect state-update methods (Advance, Update, Tick, etc.) for non-PB scripts.
            // These are called each frame before rendering so animation state actually changes.
            var stateUpdateCalls = (scriptType != ScriptType.ProgrammableBlock)
                ? DetectStateUpdateCalls(userCode)
                : new List<string>();

            string source;
            switch (scriptType)
            {
                case ScriptType.ProgrammableBlock:
                    source = BuildPbAnimationSource(userCode);
                    break;
                case ScriptType.ModSurface:
                    source = BuildModSurfaceAnimationSource(userCode, callExpression, stateUpdateCalls);
                    break;
                default: // LcdHelper
                    source = BuildLcdHelperAnimationSource(userCode, callExpression, stateUpdateCalls);
                    break;
            }

            Assembly asm = Compile(source);

            Type runnerType = asm.GetType("SELcdExec.LcdRunner");
            if (runnerType == null)
                throw new InvalidOperationException("Internal error: LcdRunner type not found.");

            var ctx = new AnimationContext
            {
                CompiledAssembly = asm,
                RunnerType = runnerType,
                Runner = Activator.CreateInstance(runnerType),
                InitMethod = runnerType.GetMethod("AnimInit"),
                FrameMethod = runnerType.GetMethod("RunFrame"),
                GetFreqMethod = runnerType.GetMethod("GetUpdateFrequency"),      // null for non-PB
                SetElapsedMethod = runnerType.GetMethod("SetTimeSinceLastRun"),   // null for non-PB
                ScriptType = scriptType,
            };
            return ctx;
        }

        /// <summary>
        /// Calls the script's constructor body (Program()) on the runner instance.
        /// Must be called once after <see cref="CompileForAnimation"/> before any frames.
        /// Capped at 5 seconds.
        /// </summary>
        public static void InitAnimation(AnimationContext ctx)
        {
            Exception initEx = null;
            var thread = new System.Threading.Thread(() =>
            {
                try { ctx.InitMethod.Invoke(ctx.Runner, null); }
                catch (Exception ex) { initEx = ex.InnerException ?? ex; }
            });
            thread.IsBackground = true;
            thread.Start();
            if (!thread.Join(5000))
            {
                thread.Abort();
                throw new TimeoutException("Initialization timed out after 5 s.");
            }
            if (initEx != null) throw initEx;
        }

        /// <summary>
        /// Executes a single animation frame on a live runner, capturing
        /// the sprite output.  Instance state persists between calls.
        /// Capped at 2 seconds per frame.
        /// </summary>
        public static ExecutionResult RunAnimationFrame(
            AnimationContext ctx, int updateType, int tick, double elapsedSeconds)
        {
            if (ctx?.Runner == null)
                return Fail("Animation session is not active.");

            if (ctx.SetElapsedMethod != null)
                ctx.SetElapsedMethod.Invoke(ctx.Runner, new object[] { elapsedSeconds });

            string[][] rawData = null;
            Exception runEx = null;

            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    rawData = (string[][])ctx.FrameMethod.Invoke(
                        ctx.Runner, new object[] { updateType, tick });
                }
                catch (Exception ex)
                {
                    runEx = ex.InnerException ?? ex;
                }
            });
            thread.IsBackground = true;
            thread.Start();

            if (!thread.Join(2000))
            {
                thread.Abort();
                return Fail("Frame execution timed out after 2 s — possible infinite loop.");
            }

            if (runEx != null)
                return Fail("Runtime error: " + runEx.Message);
            if (rawData == null)
                return Fail("No sprite data returned.");

            return new ExecutionResult
            {
                Sprites = ConvertFromMatrix(rawData),
                ScriptType = ctx.ScriptType,
            };
        }

        /// <summary>
        /// Reads <c>Runtime.UpdateFrequency</c> from the live runner instance.
        /// Returns the raw flags as an <see cref="int"/>.
        /// </summary>
        public static int GetUpdateFrequency(AnimationContext ctx)
        {
            if (ctx?.GetFreqMethod == null) return 0;
            return (int)ctx.GetFreqMethod.Invoke(ctx.Runner, null);
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
                "Sandbox.ModAPI", "VRage", "VRage.Game.ModAPI.Ingame",
                "VRage.ModAPI", "VRage.Game.ModAPI", "VRage.Game.Components",
                "VRage.Utils", "Sandbox.Game.GameSystems",
                "System", "System.Collections.Generic",
                "System.Globalization", "System.Linq", "System.Text", "System.Threading"
            };
            sb.AppendLine("namespace SELcdExec {");
            sb.AppendLine("    using VRage;");
            sb.AppendLine("    using VRage.Game.GUI.TextPanel;");
            sb.AppendLine("    using VRage.Game.ModAPI.Ingame;");
            sb.AppendLine("    using VRage.ModAPI;");
            sb.AppendLine("    using VRage.Game.ModAPI;");
            sb.AppendLine("    using VRage.Game.Components;");
            sb.AppendLine("    using VRage.Utils;");
            sb.AppendLine("    using VRageMath;");
            sb.AppendLine("    using Sandbox.ModAPI.Ingame;");
            sb.AppendLine("    using Sandbox.Game.GameSystems;");
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

            // PB scripts may or may not have a class wrapper.  In SE's in-game
            // editor the code is bare (no class declaration) but still has a
            // Program() constructor.  Use "Program" as the class name when no
            // wrapper was found so constructors are still extracted and stripped.
            string ctorName = hadClassWrapper && !string.IsNullOrEmpty(className)
                ? className
                : "Program";

            string ctorBody = ExtractConstructorBody(stripped, ctorName);
            stripped = StripConstructors(stripped, ctorName);

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
            sb.AppendLine("            // Register a default LCD panel so GetBlockWithName finds a surface");
            sb.AppendLine("            var lcd = new StubTerminalBlock(1);");
            sb.AppendLine("            lcd.EntityId = 2;");
            sb.AppendLine("            gts.RegisterBlock(lcd);");
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

        // ── PB animation source (compile once, run many) ────────────────────

        private static string BuildPbAnimationSource(string userCode)
        {
            string[] userUsings;
            string stripped = ExtractUsings(userCode, out userUsings);
            bool hadClassWrapper;
            string className;
            stripped = StripClassWrapper(stripped, out hadClassWrapper, out className);

            string ctorName = hadClassWrapper && !string.IsNullOrEmpty(className)
                ? className
                : "Program";

            string ctorBody = ExtractConstructorBody(stripped, ctorName);
            stripped = StripConstructors(stripped, ctorName);
            stripped = StripReadonly(stripped);

            // Detect Main() signature to determine call in RunFrame
            string mainCall;
            Match mainMatch = _rxMainMethod.Match(userCode);
            if (mainMatch.Success && mainMatch.Value.Contains("UpdateType"))
                mainCall = "Main(\"\", (UpdateType)updateType);";
            else if (mainMatch.Success && mainMatch.Value.Contains("string"))
                mainCall = "Main(\"\");";
            else
                mainCall = "Main();";

            var sb = new StringBuilder();
            AppendSharedHeader(sb, userUsings);

            sb.AppendLine("    public class LcdRunner : MyGridProgram {");
            sb.AppendLine();

            // _InitStubs — identical to the regular PB path
            sb.AppendLine("        private void _InitStubs() {");
            sb.AppendLine("            var gts = new StubGridTerminalSystem();");
            sb.AppendLine("            var pb = new StubProgrammableBlock();");
            sb.AppendLine("            gts.RegisterBlock(pb);");
            sb.AppendLine("            var lcd = new StubTerminalBlock(1);");
            sb.AppendLine("            lcd.EntityId = 2;");
            sb.AppendLine("            gts.RegisterBlock(lcd);");
            sb.AppendLine("            Me = pb;");
            sb.AppendLine("            Runtime = new StubRuntime();");
            sb.AppendLine("            GridTerminalSystem = gts;");
            sb.AppendLine("            Storage = string.Empty;");
            sb.AppendLine("        }");
            sb.AppendLine();

            // User methods (including Main)
            sb.AppendLine(stripped);
            sb.AppendLine();

            // AnimInit — called once to set up stubs + run constructor
            sb.AppendLine("        public void AnimInit() {");
            sb.AppendLine("            _InitStubs();");
            sb.AppendLine("            SpriteCollector.Reset();");
            if (!string.IsNullOrWhiteSpace(ctorBody))
            {
                sb.AppendLine("            {");
                sb.AppendLine("                " + ctorBody);
                sb.AppendLine("            }");
            }
            sb.AppendLine("        }");
            sb.AppendLine();

            // RunFrame — called every tick; resets collector, calls Main, returns sprites
            sb.AppendLine("        public string[][] RunFrame(int updateType, int tick) {");
            sb.AppendLine("            SpriteCollector.Reset();");
            sb.AppendLine("            " + mainCall);
            sb.AppendLine("            var sprites = SpriteCollector.Captured;");
            AppendSpriteSerialisation(sb, "sprites");
            sb.AppendLine("        }");
            sb.AppendLine();

            // GetUpdateFrequency — returns Runtime.UpdateFrequency as int
            sb.AppendLine("        public int GetUpdateFrequency() {");
            sb.AppendLine("            return (int)Runtime.UpdateFrequency;");
            sb.AppendLine("        }");
            sb.AppendLine();

            // SetTimeSinceLastRun — allows the host to feed realistic elapsed time
            sb.AppendLine("        public void SetTimeSinceLastRun(double elapsed) {");
            sb.AppendLine("            ((StubRuntime)Runtime).TimeSinceLastRun = elapsed;");
            sb.AppendLine("        }");

            sb.AppendLine("    }"); // class LcdRunner
            sb.AppendLine("}");     // namespace
            return sb.ToString();
        }

        // ── LCD Helper animation source (compile once, run many) ────────────

        private static string BuildLcdHelperAnimationSource(string userCode, string callExpression,
            List<string> stateUpdateCalls = null)
        {
            string[] userUsings;
            string stripped = ExtractUsings(userCode, out userUsings);
            bool hadClassWrapper;
            string className;
            stripped = StripClassWrapper(stripped, out hadClassWrapper, out className);

            string ctorBody = "";
            if (hadClassWrapper && !string.IsNullOrEmpty(className))
            {
                ctorBody = ExtractConstructorBody(stripped, className);
                stripped = StripConstructors(stripped, className);
            }
            stripped = StripReadonly(stripped);

            // callExpression may contain multiple calls separated by newlines
            var callLines = new List<string>();
            foreach (string raw in callExpression.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string cl = raw.Trim();
                if (string.IsNullOrEmpty(cl)) continue;
                if (!cl.EndsWith(";")) cl += ";";
                callLines.Add(cl);
            }

            var sb = new StringBuilder();
            AppendSharedHeader(sb, userUsings);
            sb.AppendLine("    public class LcdRunner {");

            if (!hadClassWrapper)
            {
                sb.AppendLine("        public int _tick = 0;");
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

            // AnimInit
            sb.AppendLine("        public void AnimInit() {");
            if (!string.IsNullOrWhiteSpace(ctorBody))
            {
                sb.AppendLine("            {");
                sb.AppendLine("                " + ctorBody);
                sb.AppendLine("            }");
            }
            sb.AppendLine("        }");
            sb.AppendLine();

            // RunFrame — updates _tick (bare scripts), calls state-update methods, then
            // calls ALL rendering methods, and returns sprites
            sb.AppendLine("        public string[][] RunFrame(int updateType, int tick) {");
            if (!hadClassWrapper)
                sb.AppendLine("            _tick = tick;");
            // Call state-update methods (Advance, Update, Tick, etc.) before rendering
            if (stateUpdateCalls != null)
            {
                foreach (string upd in stateUpdateCalls)
                {
                    string updCall = upd.Trim();
                    if (!updCall.EndsWith(";")) updCall += ";";
                    sb.AppendLine("            " + updCall);
                }
            }
            sb.AppendLine("            var sprites = new List<MySprite>();");
            foreach (string cl in callLines)
                sb.AppendLine("            " + cl);
            AppendSpriteSerialisation(sb, "sprites");
            sb.AppendLine("        }");

            sb.AppendLine("    }"); // class LcdRunner
            sb.AppendLine("}");     // namespace
            return sb.ToString();
        }

        // ── Mod / surface animation source (compile once, run many) ─────────

        private static string BuildModSurfaceAnimationSource(string userCode, string callExpression,
            List<string> stateUpdateCalls = null)
        {
            string[] userUsings;
            string stripped = ExtractUsings(userCode, out userUsings);
            bool hadClassWrapper;
            string className;
            stripped = StripClassWrapper(stripped, out hadClassWrapper, out className);

            string ctorBody = "";
            if (hadClassWrapper && !string.IsNullOrEmpty(className))
            {
                ctorBody = ExtractConstructorBody(stripped, className);
                stripped = StripConstructors(stripped, className);
            }
            stripped = StripReadonly(stripped);

            // Rewrite `surface` → `_stubSurface` in the call expression(s)
            // callExpression may contain multiple calls separated by newlines
            var callLines = new List<string>();
            foreach (string raw in callExpression.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string cl = raw.Trim();
                if (string.IsNullOrEmpty(cl)) continue;
                if (!cl.EndsWith(";")) cl += ";";
                cl = Regex.Replace(cl, @"\bsurface\b", "_stubSurface");
                callLines.Add(cl);
            }

            var sb = new StringBuilder();
            AppendSharedHeader(sb, userUsings);
            sb.AppendLine("    public class LcdRunner {");
            sb.AppendLine();

            // Persistent surface field (survives across frames)
            sb.AppendLine("        private StubTextSurface _stubSurface = new StubTextSurface(512f, 512f);");
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

            // AnimInit
            sb.AppendLine("        public void AnimInit() {");
            if (!string.IsNullOrWhiteSpace(ctorBody))
            {
                sb.AppendLine("            {");
                sb.AppendLine("                " + ctorBody);
                sb.AppendLine("            }");
            }
            sb.AppendLine("        }");
            sb.AppendLine();

            // RunFrame — resets collector, calls state-update methods, then calls ALL
            // rendering methods, and returns captured sprites
            sb.AppendLine("        public string[][] RunFrame(int updateType, int tick) {");
            sb.AppendLine("            SpriteCollector.Reset();");
            // Call state-update methods (Advance, Update, Tick, etc.) before rendering
            if (stateUpdateCalls != null)
            {
                foreach (string upd in stateUpdateCalls)
                {
                    string updCall = upd.Trim();
                    if (!updCall.EndsWith(";")) updCall += ";";
                    sb.AppendLine("            " + updCall);
                }
            }
            foreach (string cl in callLines)
                sb.AppendLine("            " + cl);
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

        /// <summary>
        /// Removes the <c>readonly</c> modifier from field declarations so that
        /// constructor-body code moved into AnimInit() can still assign them.
        /// </summary>
        private static string StripReadonly(string code)
        {
            return Regex.Replace(code, @"\breadonly\s+", "");
        }

        private static string StripConstructors(string body, string className)
        {
            if (string.IsNullOrEmpty(className)) return body;
            var ctorRegex = new Regex(
                @"(?:(?:public|private|protected|internal)\s+)?" + Regex.Escape(className)
                + @"\s*\([^)]*\)(?:\s*:[^{]+)?\s*\{",
                RegexOptions.Singleline);
            var result = new StringBuilder();
            int pos = 0;
            Match m = ctorRegex.Match(body);
            while (m.Success)
            {
                result.Append(body, pos, m.Index - pos);
                int scan = ScanToMatchingBrace(body, m.Index + m.Length);
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
                @"(?:(?:public|private|protected|internal)\s+)?" + Regex.Escape(className)
                + @"\s*\([^)]*\)(?:\s*:[^{]+)?\s*\{",
                RegexOptions.Singleline);
            Match m = ctorRegex.Match(body);
            if (!m.Success) return "";

            int scan = ScanToMatchingBrace(body, m.Index + m.Length);
            // Extract inner body (between outer { and })
            int bodyStart = m.Index + m.Length;
            int bodyEnd = scan - 1;
            if (bodyEnd <= bodyStart) return "";
            // Strip bare return statements — they are valid in void constructors
            // but would cause CS0126 when inlined into RunAllData (string[][]).
            string ctorBody = body.Substring(bodyStart, bodyEnd - bodyStart).Trim();
            ctorBody = Regex.Replace(ctorBody, @"(?m)^\s*return\s*;\s*$", "").Trim();
            return ctorBody;
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
            int pos = ScanToMatchingBrace(code, bodyStart);

            hadWrapper = true;
            return code.Substring(bodyStart, pos - 1 - bodyStart);
        }

        // ── Brace-aware scanning ─────────────────────────────────────────────
        // The simple '{'/'}' counter used previously failed when braces appeared
        // inside string literals, character literals, or comments.  These helpers
        // skip over such constructs so that StripClassWrapper, StripConstructors,
        // and ExtractConstructorBody see only structural braces.

        /// <summary>
        /// Starting right after an opening '{', scans forward until the matching
        /// closing '}' is found, correctly skipping braces inside string literals,
        /// character literals, and comments.  Returns the index just past the
        /// closing '}'.  If no match is found, returns <c>code.Length</c>.
        /// </summary>
        private static int ScanToMatchingBrace(string code, int start)
        {
            int depth = 1;
            int i = start;
            while (i < code.Length && depth > 0)
            {
                char c = code[i];
                switch (c)
                {
                    case '/':
                        if (i + 1 < code.Length)
                        {
                            if (code[i + 1] == '/')
                            {
                                int nl = code.IndexOf('\n', i + 2);
                                i = nl < 0 ? code.Length : nl + 1;
                                continue;
                            }
                            if (code[i + 1] == '*')
                            {
                                int end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                                i = end < 0 ? code.Length : end + 2;
                                continue;
                            }
                        }
                        break;
                    case '@':
                        if (i + 1 < code.Length && code[i + 1] == '"')
                        {
                            i = SkipVerbatimString(code, i + 2);
                            continue;
                        }
                        if (i + 2 < code.Length && code[i + 1] == '$' && code[i + 2] == '"')
                        {
                            i = SkipVerbatimString(code, i + 3);
                            continue;
                        }
                        break;
                    case '$':
                        if (i + 1 < code.Length && code[i + 1] == '"')
                        {
                            i = SkipRegularString(code, i + 2);
                            continue;
                        }
                        if (i + 2 < code.Length && code[i + 1] == '@' && code[i + 2] == '"')
                        {
                            i = SkipVerbatimString(code, i + 3);
                            continue;
                        }
                        break;
                    case '"':
                        i = SkipRegularString(code, i + 1);
                        continue;
                    case '\'':
                        i++;
                        if (i < code.Length && code[i] == '\\') i++;
                        i++;
                        if (i < code.Length && code[i] == '\'') i++;
                        continue;
                    case '{':
                        depth++;
                        break;
                    case '}':
                        depth--;
                        break;
                }
                i++;
            }
            return i;
        }

        private static int SkipRegularString(string code, int i)
        {
            while (i < code.Length)
            {
                if (code[i] == '\\') { i += 2; continue; }
                if (code[i] == '"') return i + 1;
                i++;
            }
            return i;
        }

        private static int SkipVerbatimString(string code, int i)
        {
            while (i < code.Length)
            {
                if (code[i] == '"')
                {
                    if (i + 1 < code.Length && code[i + 1] == '"')
                        i += 2;
                    else
                        return i + 1;
                }
                else
                    i++;
            }
            return i;
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
        public static Color Lime        { get { return new Color(0,255,0);     } }
        public static Color DarkGray    { get { return new Color(64,64,64);    } }
        public static Color LightGray   { get { return new Color(192,192,192); } }
        public static Color Transparent { get { return new Color(0,0,0,0);    } }
        public static Color operator*(Color c, float f) { return new Color((int)(c.R*f),(int)(c.G*f),(int)(c.B*f),(int)c.A); }
    }

    public enum TextAlignment { LEFT = 0, CENTER = 1, RIGHT = 2 }

    public struct Vector3D
    {
        public double X, Y, Z;
        public Vector3D(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static Vector3D operator+(Vector3D a, Vector3D b) { return new Vector3D(a.X+b.X, a.Y+b.Y, a.Z+b.Z); }
        public static Vector3D operator-(Vector3D a, Vector3D b) { return new Vector3D(a.X-b.X, a.Y-b.Y, a.Z-b.Z); }
        public static Vector3D operator*(Vector3D a, double f)   { return new Vector3D(a.X*f,   a.Y*f,   a.Z*f);   }
        public static Vector3D operator*(double f, Vector3D a)   { return new Vector3D(a.X*f,   a.Y*f,   a.Z*f);   }
        public static Vector3D operator/(Vector3D a, double f)   { return new Vector3D(a.X/f,   a.Y/f,   a.Z/f);   }
        public static Vector3D operator-(Vector3D a)             { return new Vector3D(-a.X,    -a.Y,    -a.Z);     }
        public static bool operator==(Vector3D a, Vector3D b)    { return a.X==b.X && a.Y==b.Y && a.Z==b.Z; }
        public static bool operator!=(Vector3D a, Vector3D b)    { return !(a==b); }
        public override bool Equals(object obj) { return obj is Vector3D && this==(Vector3D)obj; }
        public override int GetHashCode() { return X.GetHashCode() ^ Y.GetHashCode() ^ Z.GetHashCode(); }
        public double Length() { return System.Math.Sqrt(X*X+Y*Y+Z*Z); }
        public double LengthSquared() { return X*X+Y*Y+Z*Z; }
        public static Vector3D Zero { get { return new Vector3D(0,0,0); } }
        public static Vector3D One  { get { return new Vector3D(1,1,1); } }
        public static Vector3D Normalize(Vector3D v) { double l = v.Length(); return l > 0 ? v / l : Zero; }
        public override string ToString() { return X+"",""+Y+"",""+Z; }
    }

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

namespace VRage
{
    public struct MyFixedPoint
    {
        private long _raw;

        public int RawValue { get { return (int)_raw; } }

        public static implicit operator MyFixedPoint(int v)    { var fp = new MyFixedPoint(); fp._raw = (long)v * 1000000; return fp; }
        public static implicit operator MyFixedPoint(float v)  { var fp = new MyFixedPoint(); fp._raw = (long)(v * 1000000); return fp; }
        public static implicit operator MyFixedPoint(double v) { var fp = new MyFixedPoint(); fp._raw = (long)(v * 1000000); return fp; }
        public static implicit operator float(MyFixedPoint fp)  { return fp._raw / 1000000f; }
        public static implicit operator double(MyFixedPoint fp) { return fp._raw / 1000000.0; }

        public static MyFixedPoint operator +(MyFixedPoint a, MyFixedPoint b) { var fp = new MyFixedPoint(); fp._raw = a._raw + b._raw; return fp; }
        public static MyFixedPoint operator -(MyFixedPoint a, MyFixedPoint b) { var fp = new MyFixedPoint(); fp._raw = a._raw - b._raw; return fp; }
        public static MyFixedPoint operator *(MyFixedPoint a, MyFixedPoint b) { var fp = new MyFixedPoint(); fp._raw = a._raw * b._raw / 1000000; return fp; }
        public static bool operator >(MyFixedPoint a, MyFixedPoint b)  { return a._raw > b._raw; }
        public static bool operator <(MyFixedPoint a, MyFixedPoint b)  { return a._raw < b._raw; }
        public static bool operator >=(MyFixedPoint a, MyFixedPoint b) { return a._raw >= b._raw; }
        public static bool operator <=(MyFixedPoint a, MyFixedPoint b) { return a._raw <= b._raw; }
        public static bool operator ==(MyFixedPoint a, MyFixedPoint b) { return a._raw == b._raw; }
        public static bool operator !=(MyFixedPoint a, MyFixedPoint b) { return a._raw != b._raw; }

        public override bool Equals(object obj) { return obj is MyFixedPoint && this == (MyFixedPoint)obj; }
        public override int GetHashCode() { return _raw.GetHashCode(); }
        public override string ToString() { return ((float)this).ToString(); }

        public int ToIntSafe() { return (int)(_raw / 1000000); }
        public static MyFixedPoint MaxValue { get { MyFixedPoint fp = new MyFixedPoint(); fp._raw = long.MaxValue; return fp; } }
        public static MyFixedPoint MinValue { get { MyFixedPoint fp = new MyFixedPoint(); fp._raw = long.MinValue; return fp; } }
    }
}

namespace VRage.Game.ModAPI.Ingame
{
    using VRage;

    public struct MyItemType
    {
        public string TypeId { get; private set; }
        public string SubtypeId { get; private set; }
        public MyItemType(string typeId, string subtypeId) { TypeId = typeId; SubtypeId = subtypeId; }
        public static MyItemType Parse(string str)
        {
            var parts = str.Split('/');
            return parts.Length == 2 ? new MyItemType(parts[0], parts[1]) : new MyItemType(str, """");
        }
        public static MyItemType MakeOre(string subtype)      { return new MyItemType(""MyObjectBuilder_Ore"", subtype); }
        public static MyItemType MakeIngot(string subtype)     { return new MyItemType(""MyObjectBuilder_Ingot"", subtype); }
        public static MyItemType MakeComponent(string subtype) { return new MyItemType(""MyObjectBuilder_Component"", subtype); }
        public static MyItemType MakeAmmo(string subtype)      { return new MyItemType(""MyObjectBuilder_AmmoMagazine"", subtype); }
        public static MyItemType MakeTool(string subtype)      { return new MyItemType(""MyObjectBuilder_PhysicalGunObject"", subtype); }
        public override string ToString() { return TypeId + ""/"" + SubtypeId; }
    }

    public struct MyInventoryItem
    {
        public MyItemType Type { get; set; }
        public MyFixedPoint Amount { get; set; }
    }

    public interface IMyInventory
    {
        MyFixedPoint CurrentVolume { get; }
        MyFixedPoint MaxVolume { get; }
        MyFixedPoint CurrentMass { get; }
        int ItemCount { get; }
        void GetItems(System.Collections.Generic.List<MyInventoryItem> items, System.Func<MyInventoryItem, bool> filter = null);
        MyInventoryItem? GetItemAt(int index);
        bool CanItemsBeAdded(MyFixedPoint amount, MyItemType type);
        bool ContainItems(MyFixedPoint amount, MyItemType type);
        MyFixedPoint GetItemAmount(MyItemType type);
    }

    public class StubInventory : IMyInventory
    {
        public MyFixedPoint CurrentVolume { get { return 0; } }
        public MyFixedPoint MaxVolume { get { return 1000; } }
        public MyFixedPoint CurrentMass { get { return 0; } }
        public int ItemCount { get { return 0; } }
        public void GetItems(System.Collections.Generic.List<MyInventoryItem> items, System.Func<MyInventoryItem, bool> filter = null) { items.Clear(); }
        public MyInventoryItem? GetItemAt(int index) { return null; }
        public bool CanItemsBeAdded(MyFixedPoint amount, MyItemType type) { return true; }
        public bool ContainItems(MyFixedPoint amount, MyItemType type) { return false; }
        public MyFixedPoint GetItemAmount(MyItemType type) { return 0; }
    }
}

namespace Sandbox.ModAPI.Ingame
{
    using System;
    using System.Collections.Generic;
    using VRage;
    using VRage.Game.GUI.TextPanel;
    using VRage.Game.ModAPI.Ingame;
    using VRageMath;

    [Flags] public enum UpdateType { None = 0, Once = 128, Update1 = 16, Update10 = 32, Update100 = 64, Terminal = 256, Trigger = 512 }
    [Flags] public enum UpdateFrequency { None = 0, Update1 = 1, Update10 = 2, Update100 = 4, Once = 8 }

    // ── Functional stubs ──────────────────────────────────────────────────

    public class StubRuntime : IMyRuntime
    {
        public UpdateFrequency UpdateFrequency { get; set; }
        private double _tsLastRun = 0.016;
        public double TimeSinceLastRun { get { return _tsLastRun; } set { _tsLastRun = value; } }
        public double LastRunTimeMs { get { return 0.1; } }
        public int MaxInstructionCount { get { return 50000; } }
    }

    public class StubTextSurface : IMyTextSurface, IMyTextPanel
    {
        private string _text = """";
        private readonly StubInventory _inv = new StubInventory();
        public ContentType ContentType { get; set; }
        public Color FontColor { get; set; }
        public Color BackgroundColor { get; set; }
        public Color ScriptBackgroundColor { get; set; }
        public Color ScriptForegroundColor { get; set; }
        public float FontSize { get; set; }
        public string Font { get; set; }
        public float TextPadding { get; set; }
        public string Script { get; set; }
        public Vector2 SurfaceSize { get; set; }
        public Vector2 TextureSize { get; set; }
        public void WriteText(string text, bool append = false) { _text = append ? _text + text : text; }
        public string ReadText() { return _text; }
        public MySpriteDrawFrame DrawFrame() { return new MySpriteDrawFrame(); }
        public void GetSprites(List<string> sprites) { sprites.Clear(); }

        // IMyFunctionalBlock / IMyTerminalBlock members
        public bool Enabled { get; set; }
        public string CustomName { get; set; }
        public string CustomData { get; set; }
        public string DetailedInfo { get; set; }
        public bool IsWorking { get { return true; } }
        public bool IsFunctional { get { return true; } }
        public long EntityId { get; set; }
        public IMyCubeGrid CubeGrid { get; set; }
        public ITerminalProperty GetProperty(string name) { return new StubTerminalProperty(name); }
        public ITerminalAction GetAction(string name) { return new StubTerminalAction(name); }
        public bool HasInventory { get { return false; } }
        public int InventoryCount { get { return 0; } }
        public IMyInventory GetInventory() { return _inv; }
        public IMyInventory GetInventory(int index) { return _inv; }
        public Vector3D GetPosition() { return Vector3D.Zero; }

        public StubTextSurface() : this(512f, 512f) { }
        public StubTextSurface(float w, float h)
        {
            SurfaceSize = new Vector2(w, h);
            TextureSize = new Vector2(w, h);
            FontSize = 1f;
            Font = ""White"";
            FontColor = Color.White;
            BackgroundColor = Color.Black;
            ScriptBackgroundColor = new Color(0, 88, 151);
            ScriptForegroundColor = Color.White;
            Enabled = true;
            CustomName = ""LCD Panel"";
            CustomData = """";
            DetailedInfo = """";
            EntityId = 2;
            CubeGrid = new StubCubeGrid();
        }
    }

    public interface IMyTextSurfaceProvider
    {
        IMyTextSurface GetSurface(int index);
        int SurfaceCount { get; }
    }

    public class StubTerminalBlock : IMyTerminalBlock, IMyTextSurfaceProvider, IMyFunctionalBlock
    {
        private readonly StubTextSurface[] _surfaces;
        private readonly StubInventory _inv = new StubInventory();
        public string CustomName { get; set; }
        public string CustomData { get; set; }
        public string DetailedInfo { get; set; }
        public bool IsWorking { get { return true; } }
        public bool IsFunctional { get { return true; } }
        public bool Enabled { get; set; }
        public long EntityId { get; set; }
        public IMyCubeGrid CubeGrid { get; set; }
        public VRageMath.Vector3D GetPosition() { return VRageMath.Vector3D.Zero; }
        public int SurfaceCount { get { return _surfaces.Length; } }
        public IMyTextSurface GetSurface(int index) { return _surfaces[index]; }
        public ITerminalProperty GetProperty(string name) { return new StubTerminalProperty(name); }
        public ITerminalAction GetAction(string name) { return new StubTerminalAction(name); }
        public bool HasInventory { get { return true; } }
        public int InventoryCount { get { return 1; } }
        public IMyInventory GetInventory() { return _inv; }
        public IMyInventory GetInventory(int index) { return _inv; }

        public StubTerminalBlock(int surfaceCount)
        {
            _surfaces = new StubTextSurface[surfaceCount];
            for (int i = 0; i < surfaceCount; i++)
                _surfaces[i] = new StubTextSurface();
            CustomName = ""LCD Panel"";
            CustomData = """";
            DetailedInfo = """";
            Enabled = true;
            EntityId = 1;
            CubeGrid = new StubCubeGrid();
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

        public IMyTerminalBlock GetBlockWithName(string name)
        {
            foreach (var b in _blocks)
                if (b.CustomName == name) return b;
            // Fallback: return first non-PB block so preview always works
            foreach (var b in _blocks)
                if (!(b is IMyProgrammableBlock)) return b;
            return _blocks.Count > 0 ? _blocks[0] : null;
        }

        public void SearchBlocksOfName(string name, List<IMyTerminalBlock> blocks, Func<IMyTerminalBlock, bool> collect = null)
        {
            blocks.Clear();
            foreach (var b in _blocks)
                if (b.CustomName.Contains(name) && (collect == null || collect(b)))
                    blocks.Add(b);
        }

        private readonly Dictionary<string, StubBlockGroup> _groups = new Dictionary<string, StubBlockGroup>();

        public IMyBlockGroup GetBlockGroupWithName(string name)
        {
            StubBlockGroup g;
            if (!_groups.TryGetValue(name, out g))
            {
                g = new StubBlockGroup(name);
                _groups[name] = g;
            }
            return g;
        }

        public void GetBlockGroups(List<IMyBlockGroup> groups, Func<IMyBlockGroup, bool> collect = null)
        {
            groups.Clear();
            foreach (var g in _groups.Values)
                if (collect == null || collect(g))
                    groups.Add(g);
        }
    }

    public class StubBlockGroup : IMyBlockGroup
    {
        public string Name { get; private set; }
        private readonly List<IMyTerminalBlock> _blocks = new List<IMyTerminalBlock>();

        public StubBlockGroup(string name) { Name = name; }

        public void GetBlocks(List<IMyTerminalBlock> blocks, Func<IMyTerminalBlock, bool> collect = null)
        {
            blocks.Clear();
            foreach (var b in _blocks)
                if (collect == null || collect(b))
                    blocks.Add(b);
        }

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
    }

    // ── Interfaces ────────────────────────────────────────────────────────

    public interface IMyRuntime
    {
        UpdateFrequency UpdateFrequency { get; set; }
        double TimeSinceLastRun { get; }
        double LastRunTimeMs { get; }
        int MaxInstructionCount { get; }
    }

    public interface ITerminalProperty
    {
        string Id { get; }
    }

    public class StubTerminalProperty : ITerminalProperty
    {
        public string Id { get; private set; }
        public StubTerminalProperty(string id) { Id = id; }
    }

    public interface ITerminalAction
    {
        string Id { get; }
        void Apply(IMyTerminalBlock block);
    }

    public class StubTerminalAction : ITerminalAction
    {
        public string Id { get; private set; }
        public StubTerminalAction(string id) { Id = id; }
        public void Apply(IMyTerminalBlock block) { }
    }

    public interface IMyCubeGrid
    {
        string CustomName { get; set; }
        string DisplayName { get; }
    }

    public class StubCubeGrid : IMyCubeGrid
    {
        public string CustomName { get; set; }
        public string DisplayName { get { return CustomName; } }
        public StubCubeGrid() { CustomName = ""My Grid""; }
    }

    public interface IMyTerminalBlock
    {
        string CustomName { get; set; }
        string CustomData { get; set; }
        string DetailedInfo { get; }
        bool IsWorking { get; }
        bool IsFunctional { get; }
        long EntityId { get; }
        IMyCubeGrid CubeGrid { get; }
        ITerminalProperty GetProperty(string name);
        ITerminalAction GetAction(string name);
        bool HasInventory { get; }
        int InventoryCount { get; }
        IMyInventory GetInventory();
        IMyInventory GetInventory(int index);
        VRageMath.Vector3D GetPosition();
    }

    public interface IMyFunctionalBlock : IMyTerminalBlock
    {
        bool Enabled { get; set; }
    }

    public interface IMyBlockGroup
    {
        string Name { get; }
        void GetBlocks(List<IMyTerminalBlock> blocks, Func<IMyTerminalBlock, bool> collect = null);
        void GetBlocksOfType<T>(List<T> blocks, Func<T, bool> collect = null) where T : class;
    }

    public interface IMyTextSurface
    {
        ContentType ContentType { get; set; }
        Color FontColor { get; set; }
        Color BackgroundColor { get; set; }
        Color ScriptBackgroundColor { get; set; }
        Color ScriptForegroundColor { get; set; }
        float FontSize { get; set; }
        string Font { get; set; }
        float TextPadding { get; set; }
        string Script { get; set; }
        Vector2 SurfaceSize { get; }
        Vector2 TextureSize { get; }
        void WriteText(string text, bool append = false);
        string ReadText();
        MySpriteDrawFrame DrawFrame();
        void GetSprites(List<string> sprites);
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
        IMyTerminalBlock GetBlockWithName(string name);
        void SearchBlocksOfName(string name, List<IMyTerminalBlock> blocks, Func<IMyTerminalBlock, bool> collect = null);
        IMyBlockGroup GetBlockGroupWithName(string name);
        void GetBlockGroups(List<IMyBlockGroup> groups, Func<IMyBlockGroup, bool> collect = null);
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

        public MySprite(SpriteType type, string data,
                       Vector2? position = null, Vector2? size = null, Color? color = null,
                       string fontId = null, TextAlignment alignment = TextAlignment.LEFT,
                       float rotationOrScale = 0f)
        {
            Type=type; Data=data; Position=position; Size=size; Color=color;
            FontId=fontId; RotationOrScale=rotationOrScale; Alignment=alignment;
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

    // ── Block interfaces ──────────────────────────────────────────────────

    public enum ChargeMode { Auto = 0, Recharge = 1, Discharge = 2 }
    public enum MyShipConnectorStatus { Unconnected = 0, Connectable = 1, Connected = 2 }
    public enum DoorStatus { Open = 0, Closed = 1, Opening = 2, Closing = 3 }
    public enum PistonStatus { Extended = 0, Retracted = 1, Extending = 2, Retracting = 3, Stopped = 4 }

    public interface IMyBatteryBlock : IMyFunctionalBlock
    {
        float CurrentStoredPower { get; }
        float MaxStoredPower { get; }
        float CurrentInput { get; }
        float CurrentOutput { get; }
        ChargeMode ChargeMode { get; set; }
        bool IsCharging { get; }
        bool HasCapacityRemaining { get; }
    }

    public interface IMyGasTank : IMyFunctionalBlock
    {
        double FilledRatio { get; }
        float Capacity { get; }
        bool AutoRefillBottles { get; set; }
        bool Stockpile { get; set; }
    }

    public interface IMyShipConnector : IMyFunctionalBlock
    {
        MyShipConnectorStatus Status { get; }
        bool IsConnected { get; }
        IMyShipConnector OtherConnector { get; }
        void Connect();
        void Disconnect();
        void ToggleConnect();
    }

    public interface IMyThrust : IMyFunctionalBlock
    {
        float ThrustOverride { get; set; }
        float ThrustOverridePercentage { get; set; }
        float MaxThrust { get; }
        float MaxEffectiveThrust { get; }
        float CurrentThrust { get; }
    }

    public interface IMyGyro : IMyFunctionalBlock
    {
        bool GyroOverride { get; set; }
        float Yaw { get; set; }
        float Pitch { get; set; }
        float Roll { get; set; }
        float GyroPower { get; set; }
    }

    public interface IMySensorBlock : IMyFunctionalBlock
    {
        bool IsActive { get; }
        bool DetectPlayers { get; set; }
        bool DetectSmallShips { get; set; }
        bool DetectLargeShips { get; set; }
        bool DetectStations { get; set; }
        bool DetectSubgrids { get; set; }
        bool DetectAsteroids { get; set; }
        float LeftExtend { get; set; }
        float RightExtend { get; set; }
        float TopExtend { get; set; }
        float BottomExtend { get; set; }
        float FrontExtend { get; set; }
        float BackExtend { get; set; }
    }

    public interface IMyDoor : IMyFunctionalBlock
    {
        DoorStatus Status { get; }
        float OpenRatio { get; }
        void OpenDoor();
        void CloseDoor();
        void ToggleDoor();
    }

    public interface IMyLightingBlock : IMyFunctionalBlock
    {
        Color Color { get; set; }
        float Radius { get; set; }
        float Intensity { get; set; }
        float BlinkIntervalSeconds { get; set; }
        float BlinkLength { get; set; }
        float BlinkOffset { get; set; }
    }

    public interface IMyMotorStator : IMyFunctionalBlock
    {
        float Angle { get; }
        float UpperLimitDeg { get; set; }
        float LowerLimitDeg { get; set; }
        float TargetVelocityRPM { get; set; }
        float Torque { get; set; }
        bool IsAttached { get; }
        IMyCubeGrid TopGrid { get; }
    }

    public interface IMyPistonBase : IMyFunctionalBlock
    {
        float CurrentPosition { get; }
        float MinLimit { get; set; }
        float MaxLimit { get; set; }
        float Velocity { get; set; }
        PistonStatus Status { get; }
        void Extend();
        void Retract();
    }

    public interface IMyShipController : IMyTerminalBlock
    {
        Vector3D GetNaturalGravity();
        Vector3D MoveIndicator { get; }
        Vector2 RotationIndicator { get; }
        float RollIndicator { get; }
        bool IsUnderControl { get; }
        bool CanControlShip { get; set; }
        bool ShowHorizonIndicator { get; set; }
        bool HandBrake { get; set; }
        bool DampenersOverride { get; set; }
    }

    public interface IMyTextPanel : IMyTextSurface, IMyFunctionalBlock { }

    public interface IMyPowerProducer : IMyFunctionalBlock
    {
        float CurrentOutput { get; }
        float MaxOutput { get; }
    }

    public interface IMyWindTurbine : IMyPowerProducer { }

    public interface IMySolarPanel : IMyPowerProducer { }
}

// ── Sandbox.ModAPI — mod/plugin-side block interfaces ─────────────────
namespace Sandbox.ModAPI
{
    using VRageMath;

    public interface IMyTerminalBlock : Sandbox.ModAPI.Ingame.IMyTerminalBlock
    {
    }

    public interface IMyFunctionalBlock : IMyTerminalBlock, Sandbox.ModAPI.Ingame.IMyFunctionalBlock
    {
    }

    public interface IMyTextPanel : Sandbox.ModAPI.Ingame.IMyTextPanel, IMyFunctionalBlock
    {
    }

    // ── MyAPIGateway — core mod API entry point ───────────────────────────

    public static class MyAPIGateway
    {
        public static IMySession Session = new StubSession();
        public static IMyMultiplayer Multiplayer = new StubMultiplayer();
        public static IMyEntities Entities = new StubEntities();
        public static IMyTerminalActionsHelper TerminalActionsHelper = new StubTerminalActionsHelper();
        public static IMyUtilities Utilities = new StubUtilities();
    }

    public interface IMySession
    {
        IMyWeatherEffects WeatherEffects { get; }
        System.TimeSpan ElapsedPlayTime { get; }
    }

    public interface IMyWeatherEffects
    {
        string GetWeather(Vector3D position);
    }

    public interface IMyMultiplayer
    {
        bool IsServer { get; }
    }

    public interface IMyEntities
    {
        void GetEntities(System.Collections.Generic.HashSet<VRage.ModAPI.IMyEntity> entities);
    }

    public interface IMyTerminalActionsHelper
    {
        Sandbox.ModAPI.Ingame.IMyGridTerminalSystem GetTerminalSystemForGrid(Sandbox.ModAPI.Ingame.IMyCubeGrid grid);
    }

    public interface IMyUtilities
    {
        void ShowMissionScreen(string screenTitle, string currentObjectivePrefix, string currentObjective, string description);
    }

    // ── Stubs ─────────────────────────────────────────────────────────────

    public class StubSession : IMySession
    {
        private readonly StubWeatherEffects _weather = new StubWeatherEffects();
        public IMyWeatherEffects WeatherEffects { get { return _weather; } }
        public System.TimeSpan ElapsedPlayTime { get { return System.TimeSpan.FromSeconds(120); } }
    }

    public class StubWeatherEffects : IMyWeatherEffects
    {
        public string GetWeather(Vector3D position) { return ""Clear""; }
    }

    public class StubMultiplayer : IMyMultiplayer
    {
        public bool IsServer { get { return true; } }
    }

    public class StubEntities : IMyEntities
    {
        public void GetEntities(System.Collections.Generic.HashSet<VRage.ModAPI.IMyEntity> entities) { entities.Clear(); }
    }

    public class StubTerminalActionsHelper : IMyTerminalActionsHelper
    {
        public Sandbox.ModAPI.Ingame.IMyGridTerminalSystem GetTerminalSystemForGrid(Sandbox.ModAPI.Ingame.IMyCubeGrid grid)
        {
            return new Sandbox.ModAPI.Ingame.StubGridTerminalSystem();
        }
    }

    public class StubUtilities : IMyUtilities
    {
        public void ShowMissionScreen(string screenTitle, string currentObjectivePrefix, string currentObjective, string description) { }
    }
}

// ── VRage.ModAPI — entity interfaces ──────────────────────────────────
namespace VRage.ModAPI
{
    public interface IMyEntity
    {
        long EntityId { get; }
        string DisplayName { get; }
    }
}

// ── VRage.Game.ModAPI — grid & slim block interfaces ──────────────────
namespace VRage.Game.ModAPI
{
    using System.Collections.Generic;

    public interface IMyCubeGrid : VRage.ModAPI.IMyEntity, Sandbox.ModAPI.Ingame.IMyCubeGrid
    {
        void GetBlocks(List<IMySlimBlock> blocks);
    }

    public interface IMySlimBlock
    {
        object FatBlock { get; }
    }

    public class StubSlimBlock : IMySlimBlock
    {
        public object FatBlock { get; set; }
    }

    public class StubModCubeGrid : IMyCubeGrid
    {
        public long EntityId { get; set; }
        public string DisplayName { get { return CustomName; } }
        public string CustomName { get; set; }
        public void GetBlocks(List<IMySlimBlock> blocks) { blocks.Clear(); }
        public StubModCubeGrid() { CustomName = ""Grid""; }
    }
}

// ── VRage.Game.Components — session component base ────────────────────
namespace VRage.Game.Components
{
    [System.Flags]
    public enum MyUpdateOrder { NoUpdate = 0, BeforeSimulation = 1, Simulation = 2, AfterSimulation = 4 }

    [System.AttributeUsage(System.AttributeTargets.Class)]
    public class MySessionComponentDescriptor : System.Attribute
    {
        public MyUpdateOrder UpdateOrder;
        public MySessionComponentDescriptor(MyUpdateOrder updateOrder) { UpdateOrder = updateOrder; }
    }

    public abstract class MySessionComponentBase
    {
        public virtual void LoadData() { }
        public virtual void UnloadData() { }
        public virtual void UpdateBeforeSimulation() { }
        public virtual void UpdateAfterSimulation() { }
        public virtual void BeforeStart() { }
        public virtual void Init(object sessionComponent) { }
    }
}

// ── VRage.Utils ───────────────────────────────────────────────────────
namespace VRage.Utils
{
    public class MyLog
    {
        public static MyLog Default = new MyLog();
        public void WriteLineAndConsole(string msg) { }
        public void WriteLine(string msg) { }
    }
}

// ── Sandbox.Game.GameSystems (stub namespace) ─────────────────────────
namespace Sandbox.Game.GameSystems { }
";
    }
}
