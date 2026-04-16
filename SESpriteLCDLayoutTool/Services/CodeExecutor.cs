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
        /// <summary>Pulsar plugin — implements <c>IPlugin</c> with Init/Update/Dispose lifecycle.</summary>
        PulsarPlugin,
        /// <summary>Torch plugin — extends <c>TorchPluginBase</c>.</summary>
        TorchPlugin,
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
    public static partial class CodeExecutor
    {
        // ── Result type ───────────────────────────────────────────────────────

        public sealed class ExecutionResult
        {
            public List<SpriteEntry> Sprites { get; set; }
            public string Error { get; set; }
            public bool Success { get { return Error == null; } }
            public ScriptType ScriptType { get; set; }
            public List<string> OutputLines { get; set; }
            /// <summary>Per-method timing data from the last execution. Key = method name, Value = elapsed ms.</summary>
            public Dictionary<string, double> MethodTimings { get; set; }
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
        private static readonly Regex _rxPulsarPlugin = new Regex(
            @"class\s+\w+\s*:\s*IPlugin\b", RegexOptions.Compiled);
        private static readonly Regex _rxTorchPlugin = new Regex(
            @"class\s+\w+\s*:\s*TorchPluginBase\b", RegexOptions.Compiled);
        private static readonly Regex _rxSpriteListMethod = new Regex(
            @"(?:private|public|internal|protected)?\s*(?:static\s+)?void\s+(\w+)\s*\(\s*List\s*<\s*MySprite\s*>\s+\w+([^)]*)\)",
            RegexOptions.Compiled);

        // Additional using-directive / namespace patterns for broader detection
        private static readonly Regex _rxUsingIngame = new Regex(
            @"using\s+Sandbox\.ModAPI\.Ingame\s*;", RegexOptions.Compiled);
        private static readonly Regex _rxUsingVRagePlugins = new Regex(
            @"using\s+VRage\.Plugins\s*;", RegexOptions.Compiled);
        private static readonly Regex _rxUsingModAPI = new Regex(
            @"using\s+Sandbox\.ModAPI\s*;", RegexOptions.Compiled);
        private static readonly Regex _rxUsingTorch = new Regex(
            @"using\s+Torch\b", RegexOptions.Compiled);
        private static readonly Regex _rxUsingVRageGui = new Regex(
            @"using\s+VRage\.Game\.GUI\.TextPanel\s*;", RegexOptions.Compiled);
        private static readonly Regex _rxPulsarNamespace = new Regex(
            @"namespace\s+[\w.]*Pulsar[\w.]*\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        /// a mod/plugin with IMyTextSurface methods, a Pulsar plugin, a Torch plugin, or a plain LCD helper.
        /// Uses class inheritance, method signatures, using directives, and namespace hints.
        /// </summary>
        public static ScriptType DetectScriptType(string userCode)
        {
            if (string.IsNullOrWhiteSpace(userCode)) return ScriptType.LcdHelper;

            // Torch plugin detection: class extending TorchPluginBase
            // Check BEFORE stripping preprocessor directives
            if (_rxTorchPlugin.IsMatch(userCode))
                return ScriptType.TorchPlugin;

            // Torch detection: check for #if TORCH, RenderContext, or LcdSpriteRow
            // BEFORE stripping preprocessor directives to catch the marker itself
            if (userCode.Contains("#if TORCH") ||
                userCode.Contains("#endif // TORCH") ||
                userCode.Contains("class RenderContext") ||
                userCode.Contains("struct LcdSpriteRow") ||
                (userCode.Contains("RenderContext ctx") && userCode.Contains("LcdSpriteRow")))
                return ScriptType.TorchPlugin;

            // Resolve #if/#else/#endif so detection sees all conditionally-compiled code
            userCode = StripPreprocessorDirectives(userCode);

            // PB detection: class extending MyGridProgram, or Main() with SE signature
            if (_rxPbClass.IsMatch(userCode) || _rxMainMethod.IsMatch(userCode))
                return ScriptType.ProgrammableBlock;

            // Pulsar plugin detection: class implementing IPlugin, or using VRage.Plugins,
            // or namespace containing "Pulsar"
            if (_rxPulsarPlugin.IsMatch(userCode) ||
                _rxUsingVRagePlugins.IsMatch(userCode) ||
                _rxPulsarNamespace.IsMatch(userCode))
                return ScriptType.PulsarPlugin;

            // Torch helper detection: has List<MySprite> returning method + VRage types
            // WITHOUT Sandbox.ModAPI.Ingame (which would make it PB code)
            // This catches standalone Torch LCD helper classes like animation demos
            bool hasSpriteReturnMethod = _rxSpriteReturnMethod.IsMatch(userCode);
            bool hasVRageTypes = _rxUsingVRageGui.IsMatch(userCode) || userCode.Contains("using VRageMath;");
            bool hasIngame = _rxUsingIngame.IsMatch(userCode);

            if (hasSpriteReturnMethod && hasVRageTypes && !hasIngame)
                return ScriptType.TorchPlugin;

            // Plain LCD helper: sprite return method without VRage context
            if (hasSpriteReturnMethod)
                return ScriptType.LcdHelper;

            // Mod/surface detection: methods accepting IMyTextSurface
            if (_rxSurfaceMethod.IsMatch(userCode))
                return ScriptType.ModSurface;

            // Using-directive fallbacks (when class/method patterns don't match):
            // "using Sandbox.ModAPI.Ingame;" without MyGridProgram/Main → still PB code
            if (hasIngame)
                return ScriptType.ProgrammableBlock;

            // "using Torch..." → Torch plugin code
            if (_rxUsingTorch.IsMatch(userCode))
                return ScriptType.TorchPlugin;

            // "using Sandbox.ModAPI;" (without .Ingame) → Mod surface code
            if (_rxUsingModAPI.IsMatch(userCode))
                return ScriptType.ModSurface;

            // "using VRage.Game.GUI.TextPanel;" or "using VRageMath;" without Sandbox imports
            // → Torch helper code (server-side sprite rendering)
            if (hasVRageTypes && !_rxUsingModAPI.IsMatch(userCode))
                return ScriptType.TorchPlugin;

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
        public static List<string> DetectAllCallExpressions(string userCode,
            List<SnapshotRowData> capturedRows = null)
        {
            var results = new List<string>();
            if (string.IsNullOrWhiteSpace(userCode)) return results;

            // Resolve #if/#else/#endif so detection sees all conditionally-compiled code
            userCode = StripPreprocessorDirectives(userCode);
            System.Diagnostics.Debug.WriteLine($"[DetectAllCallExpressions] Code after preprocessor strip: {userCode.Length} chars");
            System.Diagnostics.Debug.WriteLine($"[DetectAllCallExpressions] First 500 chars:\n{userCode.Substring(0, Math.Min(500, userCode.Length))}");

            var scriptType = DetectScriptType(userCode);
            System.Diagnostics.Debug.WriteLine($"[DetectAllCallExpressions] Script type: {scriptType}");

            switch (scriptType)
            {
                case ScriptType.ProgrammableBlock:
                    DetectPbCalls(userCode, results);
                    break;

                case ScriptType.PulsarPlugin:
                case ScriptType.ModSurface:
                case ScriptType.TorchPlugin:
                    DetectSurfaceCalls(userCode, results, capturedRows);
                    // Check for orchestrator methods that return List<MySprite>
                    results.AddRange(DetectSpriteReturnCalls(userCode));
                    // Also check for any List<MySprite> helpers in the same file
                    DetectLcdHelperCalls(userCode, results);
                    // Extract virtual render methods from switch case blocks
                    DetectSwitchCaseRenderMethods(userCode, results, capturedRows);
                    // Fallback: detect methods that render sprites inline
                    // (surface obtained internally, not via parameter)
                    DetectInlineRenderMethods(userCode, results, capturedRows);
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

        /// <summary>
        /// Returns detected call expressions with metadata indicating whether each
        /// represents a real method or a virtual method extracted from a switch case.
        /// </summary>
        public static List<DetectedMethodInfo> GetDetectedMethodsWithMetadata(string userCode,
            List<SnapshotRowData> capturedRows = null)
        {
            return GetDetectedMethodsWithMetadata(userCode, null, capturedRows);
        }

        /// <summary>
        /// Returns detected call expressions with metadata.
        /// When <paramref name="preComputedCalls"/> is non-null, skips the
        /// expensive <see cref="DetectAllCallExpressions"/> pass.
        /// </summary>
        public static List<DetectedMethodInfo> GetDetectedMethodsWithMetadata(string userCode,
            List<string> preComputedCalls, List<SnapshotRowData> capturedRows = null)
        {
            var result = new List<DetectedMethodInfo>();
            var calls = preComputedCalls ?? DetectAllCallExpressions(userCode, capturedRows);

            // Strip preprocessor directives for offset calculation
            string cleanCode = StripPreprocessorDirectives(userCode);

            // Build a set of all case names from switch statements to identify virtual methods
            var virtualMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rxSwitch = new Regex(@"switch\s*\(\s*(\w+(?:\.\w+)?)\s*\)\s*\{", RegexOptions.Singleline);
            foreach (Match switchMatch in rxSwitch.Matches(cleanCode))
            {
                int bodyStart = switchMatch.Index + switchMatch.Length;
                int bodyEnd = ScanToMatchingBrace(cleanCode, bodyStart);
                if (bodyEnd <= bodyStart) continue;

                string switchBody = cleanCode.Substring(bodyStart, bodyEnd - 1 - bodyStart);
                var rxCase = new Regex(@"case\s+(?:[\w\.]+\.)?(\w+)\s*:", RegexOptions.Singleline);

                foreach (Match caseMatch in rxCase.Matches(switchBody))
                {
                    string caseName = caseMatch.Groups[1].Value;
                    if (!string.IsNullOrWhiteSpace(caseName))
                    {
                        string methodName = "Render" + caseName;
                        virtualMethods.Add(methodName);
                    }
                }
            }

            // Create DetectedMethodInfo for each call
            foreach (string call in calls)
            {
                // Extract method name from call expression
                int parenIdx = call.IndexOf('(');
                string methodName = parenIdx > 0 ? call.Substring(0, parenIdx).Trim() : call.Trim();

                // Determine if this is a virtual method
                bool isVirtual = virtualMethods.Contains(methodName);

                // Find the source position
                int sourcePos = FindMethodDefinitionOffset(cleanCode, call);

                var info = new DetectedMethodInfo
                {
                    CallExpression = call,
                    SourcePosition = sourcePos,
                    Kind = isVirtual ? MethodKind.SwitchCase : MethodKind.RealMethod,
                    CaseName = isVirtual ? methodName.Substring(6) : null // Remove "Render" prefix
                };

                result.Add(info);
            }

            return result;
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

        /// <summary>
        /// Filters a list of detected call expressions to only top-level methods.
        /// When a script has a method hierarchy (e.g. DrawHud calls DrawRadar),
        /// calling all of them in RunFrame doubles sprites from sub-methods.
        /// This removes methods whose name appears as a call inside the body of
        /// another detected method.
        /// </summary>
        private static List<string> FilterTopLevelCalls(string userCode, List<string> calls)
        {
            if (calls.Count <= 1) return calls;

            // Extract method names from call expressions
            // "DrawHud(surface, viewport)" → "DrawHud"
            // "sprites = BuildSprites(...)" → "BuildSprites"
            var names = new List<string>();
            foreach (string c in calls)
            {
                string expr = c.Trim().TrimEnd(';');
                int eq = expr.IndexOf('=');
                if (eq >= 0) expr = expr.Substring(eq + 1).Trim();
                int paren = expr.IndexOf('(');
                names.Add(paren > 0 ? expr.Substring(0, paren).Trim() : expr);
            }

            // For each detected method, find its body in the source and check
            // whether it calls any other detected method.
            var calledByOther = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string parentName in names)
            {
                // Find the method signature in the source
                var sigRx = new Regex(
                    @"(?:private|public|internal|protected)?\s*(?:static\s+)?(?:\w+(?:<[^>]+>)?)\s+"
                    + Regex.Escape(parentName) + @"\s*\(");
                Match sigMatch = sigRx.Match(userCode);
                if (!sigMatch.Success) continue;

                // Find opening brace of the method body
                int bodyStart = userCode.IndexOf('{', sigMatch.Index + sigMatch.Length);
                if (bodyStart < 0) continue;

                // Match the closing brace
                int depth = 0;
                int bodyEnd = -1;
                for (int i = bodyStart; i < userCode.Length; i++)
                {
                    if (userCode[i] == '{') depth++;
                    else if (userCode[i] == '}') { depth--; if (depth == 0) { bodyEnd = i; break; } }
                }
                if (bodyEnd < 0) continue;

                string body = userCode.Substring(bodyStart, bodyEnd - bodyStart + 1);

                // Check if any other detected method is called within this body
                foreach (string childName in names)
                {
                    if (string.Equals(childName, parentName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Look for "ChildName(" as a call (not a declaration)
                    if (Regex.IsMatch(body, @"(?<!\w)" + Regex.Escape(childName) + @"\s*\("))
                        calledByOther.Add(childName);
                }
            }

            if (calledByOther.Count == 0) return calls;

            var filtered = new List<string>();
            for (int i = 0; i < calls.Count; i++)
            {
                if (!calledByOther.Contains(names[i]))
                    filtered.Add(calls[i]);
            }

            // If everything was filtered (circular calls), return original
            return filtered.Count > 0 ? filtered : calls;
        }

        private static void DetectSurfaceCalls(string userCode, List<string> results,
            List<SnapshotRowData> capturedRows = null)
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

                    // Match IMyTextSurface / IMyTextPanel with or without namespace qualification
                    string bareType = typeName.Contains(".") ? typeName.Substring(typeName.LastIndexOf('.') + 1) : typeName;
                    if (bareType == "IMyTextSurface" || bareType == "IMyTextPanel")
                        args.Add("surface");
                    else if (typeName.Equals("LcdSpriteRow", StringComparison.OrdinalIgnoreCase))
                        args.Add(GuessLcdSpriteRowArg(methodName, capturedRows));
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
        /// Fallback detection for Mod/Plugin scripts where the surface is obtained
        /// internally (e.g. via <c>MyAPIGateway.Entities</c> or field access) rather
        /// than passed as a parameter.  Finds void methods whose body contains
        /// <c>DrawFrame</c>, <c>new MySprite</c>, or <c>MySprite.Create</c>,
        /// indicating inline sprite rendering.
        /// </summary>
        private static void DetectInlineRenderMethods(string userCode, List<string> results,
            List<SnapshotRowData> capturedRows = null)
        {
            var rxAnyVoidMethod = new Regex(
                @"(?:private|public|internal|protected)\s*(?:static\s+)?void\s+(\w+)\s*\(([^)]*)\)\s*\{",
                RegexOptions.Singleline);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string r in results)
            {
                int p = r.IndexOf('(');
                if (p > 0) seen.Add(r.Substring(0, p).Trim());
            }

            System.Diagnostics.Debug.WriteLine($"[DetectInlineRenderMethods] Already detected: {string.Join(", ", seen)}");
            System.Diagnostics.Debug.WriteLine($"[DetectInlineRenderMethods] Found {rxAnyVoidMethod.Matches(userCode).Count} void methods");

            foreach (Match m in rxAnyVoidMethod.Matches(userCode))
            {
                string methodName = m.Groups[1].Value;
                System.Diagnostics.Debug.WriteLine($"[DetectInlineRenderMethods] Checking method: {methodName}");

                // Skip constructors, lifecycle, and already-detected methods
                if (methodName == "Dispose" || methodName == "Init" || methodName == "Save"
                    || methodName == "Main")
                {
                    System.Diagnostics.Debug.WriteLine($"  -> Skipped (lifecycle method)");
                    continue;
                }
                if (seen.Contains(methodName))
                {
                    System.Diagnostics.Debug.WriteLine($"  -> Skipped (already detected)");
                    continue;
                }

                // Scan the method body for sprite rendering patterns
                int bodyStart = m.Index + m.Length;
                int bodyEnd = ScanToMatchingBrace(userCode, bodyStart);
                if (bodyEnd <= bodyStart)
                {
                    System.Diagnostics.Debug.WriteLine($"  -> Skipped (no valid body)");
                    continue;
                }

                string body = userCode.Substring(bodyStart, bodyEnd - 1 - bodyStart);
                if (!body.Contains("DrawFrame") && !body.Contains("new MySprite")
                    && !body.Contains("MySprite.Create"))
                {
                    System.Diagnostics.Debug.WriteLine($"  -> Skipped (no sprite rendering code found)");
                    continue;
                }

                // Skip methods that take StringBuilder (code generators, not renderers)
                string paramDecl = m.Groups[2].Value.Trim();
                if (Regex.IsMatch(paramDecl, @"\bStringBuilder\b"))
                {
                    System.Diagnostics.Debug.WriteLine($"  -> Skipped (StringBuilder parameter)");
                    continue;
                }

                // Build a call expression with guessed default args
                var args = new List<string>();
                if (!string.IsNullOrWhiteSpace(paramDecl))
                {
                    foreach (string param in paramDecl.Split(','))
                    {
                        string p = param.Trim();
                        if (string.IsNullOrWhiteSpace(p)) continue;
                        string[] parts = p.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2) continue;
                        string typeName = parts[0];
                        string bareType = typeName.Contains(".") ? typeName.Substring(typeName.LastIndexOf('.') + 1) : typeName;
                        string paramName = parts[parts.Length - 1].ToLowerInvariant().TrimStart('_');
                        if (bareType == "IMyTextSurface" || bareType == "IMyTextPanel")
                            args.Add("surface");
                        else if (bareType.Equals("LcdSpriteRow", StringComparison.OrdinalIgnoreCase))
                            args.Add(GuessLcdSpriteRowArg(methodName, capturedRows));
                        else
                            args.Add(GuessDefaultArg(typeName.ToLowerInvariant(), paramName));
                    }
                }
                string call = methodName + "(" + string.Join(", ", args) + ")";
                System.Diagnostics.Debug.WriteLine($"  -> DETECTED: {call}");
                results.Add(call);
                seen.Add(methodName);
            }
        }

        /// <summary>
        /// Extracts virtual render methods from switch statement case blocks, allowing
        /// each LCD element to be executed and animated individually even when the
        /// original code has all rendering inline within a switch.
        /// For example, <c>case LcdSpriteRow.Kind.Header:</c> becomes <c>RenderHeader(ctx, row)</c>.
        /// </summary>
        private static void DetectSwitchCaseRenderMethods(string userCode, List<string> results,
            List<SnapshotRowData> capturedRows = null)
        {
            // Pattern: switch (identifier) { case EnumOrConstant: ... }
            var rxSwitch = new Regex(
                @"switch\s*\(\s*(\w+(?:\.\w+)?)\s*\)\s*\{",
                RegexOptions.Singleline);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in results)
            {
                int p = r.IndexOf('(');
                if (p > 0) seen.Add(r.Substring(0, p).Trim());
            }

            System.Diagnostics.Debug.WriteLine($"[DetectSwitchCaseRenderMethods] Searching for switch statements...");

            foreach (Match switchMatch in rxSwitch.Matches(userCode))
            {
                string switchExpr = switchMatch.Groups[1].Value;
                System.Diagnostics.Debug.WriteLine($"[DetectSwitchCaseRenderMethods] Found switch ({switchExpr})");

                // Find the switch body
                int bodyStart = switchMatch.Index + switchMatch.Length;
                int bodyEnd = ScanToMatchingBrace(userCode, bodyStart);
                if (bodyEnd <= bodyStart)
                {
                    System.Diagnostics.Debug.WriteLine($"  -> Skipped (no valid body)");
                    continue;
                }

                string switchBody = userCode.Substring(bodyStart, bodyEnd - 1 - bodyStart);

                // Extract all case labels
                // Pattern: case EnumType.Value: or case ConstantName: or case Namespace.Type.Value:
                var rxCase = new Regex(
                    @"case\s+(?:[\w\.]+\.)?(\w+)\s*:",
                    RegexOptions.Singleline);

                foreach (Match caseMatch in rxCase.Matches(switchBody))
                {
                    string caseName = caseMatch.Groups[1].Value; // e.g., "Header", "Separator"

                    if (string.IsNullOrWhiteSpace(caseName))
                        continue;

                    // Generate method name: Render + CaseName
                    string methodName = "Render" + caseName;

                    // Skip if already detected
                    if (seen.Contains(methodName))
                    {
                        System.Diagnostics.Debug.WriteLine($"  -> case {caseName}: skipped (method already detected)");
                        continue;
                    }

                    // Build call expression with guessed default args
                    // Common pattern: RenderHeader(ctx, row) or RenderBar(default, default)
                    string call = methodName + "(default, default)";

                    System.Diagnostics.Debug.WriteLine($"  -> case {caseName}: DETECTED as {call}");
                    results.Add(call);
                    seen.Add(methodName);
                }
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
        /// When the call expression includes <c>ApplyPendingUpdates</c> and the user
        /// code defines an <c>EnqueueUpdate</c> method accepting <c>LcdSpriteRow[]</c>,
        /// returns C# statements that pre-populate the queue with representative sample
        /// data so the render method has something to draw.  When <paramref name="capturedRows"/>
        /// contains runtime data, those real values are used instead of hardcoded placeholders.
        /// Returns <c>null</c> if the pattern is not detected.
        /// </summary>
        private static string BuildSampleEnqueueCode(string userCode, string callExpression,
            List<SnapshotRowData> capturedRows = null)
        {
            if (string.IsNullOrWhiteSpace(callExpression) || string.IsNullOrWhiteSpace(userCode))
                return null;
            // Check the user's source code (not the call expression) for the queue-processor
            // pattern.  Auto-detected calls use the outer render method name (e.g.
            // "RenderLcd(_stubSurface)"), not "ApplyPendingUpdates", so gating on
            // callExpression would always fail for auto-detected pipelines.
            if (!userCode.Contains("ApplyPendingUpdates"))
                return null;
            // Check for an EnqueueUpdate method that takes LcdSpriteRow[]
            if (!Regex.IsMatch(userCode, @"\bEnqueueUpdate\s*\(.*LcdSpriteRow\s*\[\]"))
                return null;

            var sb = new StringBuilder();

            // Use captured rows from a runtime snapshot when available
            if (capturedRows != null && capturedRows.Count > 0)
            {
                sb.AppendLine("            // Runtime-captured rows from snapshot");
                sb.AppendLine("            EnqueueUpdate(2, new LcdSpriteRow[] {");
                for (int i = 0; i < capturedRows.Count; i++)
                {
                    string comma = (i < capturedRows.Count - 1) ? "," : ",";
                    sb.AppendLine("                " + capturedRows[i].ToCSharpInitializer() + comma);
                }
                sb.AppendLine("            });");
                return sb.ToString();
            }

            sb.AppendLine("            // Sample data so the queue-processor has representative rows to render");
            sb.AppendLine("            EnqueueUpdate(2, new LcdSpriteRow[] {");
            sb.AppendLine("                new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Header, Text = \"Inventory\", TextColor = Color.White },");
            sb.AppendLine("                new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Separator },");
            sb.AppendLine("                new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Item, Text = \"Iron Ingot\", StatText = \"1,500\", TextColor = Color.White },");
            sb.AppendLine("                new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.ItemBar, Text = \"Steel Plate\", StatText = \"800 / 1000\", BarFill = 0.8f, BarFillColor = new Color(0, 200, 80), TextColor = Color.White },");
            sb.AppendLine("                new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.ItemBar, Text = \"Interior Plate\", StatText = \"200 / 500\", BarFill = 0.4f, BarFillColor = new Color(200, 160, 0), TextColor = Color.White },");
            sb.AppendLine("                new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.ItemBar, Text = \"Computer\", StatText = \"50 / 300\", BarFill = 0.17f, BarFillColor = new Color(220, 30, 0), ShowAlert = true, TextColor = new Color(255, 160, 0) },");
            sb.AppendLine("                new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Footer, Text = \"Updated: just now\", TextColor = new Color(110, 110, 115) },");
            sb.AppendLine("            });");
            return sb.ToString();
        }

        /// <summary>
        /// Given a call expression like <c>"RenderStarfield(sprites, 512f, 512f, 1f)"</c>,
        /// finds the character offset of the method definition in <paramref name="userCode"/>.
        /// Returns -1 if not found.
        /// For virtual methods extracted from switch cases (e.g., RenderHeader), searches
        /// for the corresponding case block if no real method is found.
        /// </summary>
        public static int FindMethodDefinitionOffset(string userCode, string callExpression)
        {
            if (string.IsNullOrWhiteSpace(userCode) || string.IsNullOrWhiteSpace(callExpression))
                return -1;

            string methodName = ExtractMethodName(callExpression);
            if (string.IsNullOrEmpty(methodName)) return -1;

            // Strategy 1: Search for a real method definition
            // "void MethodName(" or "internal static void MethodName(" or "List<MySprite> MethodName(" etc.
            // Allow optional access modifiers (public, private, internal, protected, static, etc.)
            var rxDef = new Regex(
                @"(?:public\s+|private\s+|internal\s+|protected\s+|static\s+)*(?:void|List\s*<\s*MySprite\s*>)\s+" + Regex.Escape(methodName) + @"\s*\(",
                RegexOptions.Compiled);

            Match match = rxDef.Match(userCode);
            if (match.Success) return match.Index;

            // Strategy 2: Virtual method from switch case
            // If the method name starts with "Render" and wasn't found as a real method,
            // search for the corresponding case block.
            if (methodName.StartsWith("Render", StringComparison.OrdinalIgnoreCase))
            {
                string caseName = methodName.Substring(6); // Remove "Render" prefix
                // Pattern: case EnumType.CaseName: or case CaseName:
                var rxCase = new Regex(
                    @"case\s+(?:[\w\.]+\.)?" + Regex.Escape(caseName) + @"\s*:",
                    RegexOptions.Compiled | RegexOptions.Singleline);
                Match caseMatch = rxCase.Match(userCode);
                if (caseMatch.Success)
                {
                    System.Diagnostics.Debug.WriteLine($"[FindMethodDefinitionOffset] Found switch case '{caseName}' for virtual method '{methodName}' at position {caseMatch.Index}");
                    return caseMatch.Index;
                }
            }

            return -1;
        }

        /// <summary>
        /// Extracts the method name from a call expression.
        /// </summary>
        public static string ExtractMethodName(string callExpression)
        {
            if (string.IsNullOrWhiteSpace(callExpression)) return null;

            // Extract method name from call expression:
            //   "RenderStarfield(sprites, 512f, 512f, 1f)"  → "RenderStarfield"
            //   "sprites = BuildSprites(...)"                → "BuildSprites"
            //   "Main(\"\", UpdateType.None)"                → "Main"

            // IMPORTANT: Extract the part BEFORE the first '(' to avoid matching
            // '=' or '.' inside the argument list (e.g., "new Foo { X = Y.Z }")
            int paren = callExpression.IndexOf('(');
            if (paren < 0) return null;  // Not a method call

            string beforeArgs = callExpression.Substring(0, paren).Trim();

            // Check for assignment: "sprites = BuildSprites" → "BuildSprites"
            int eq = beforeArgs.IndexOf('=');
            if (eq >= 0) beforeArgs = beforeArgs.Substring(eq + 1).Trim();

            // Strip object qualifier: "surface.DrawHUD" → "DrawHUD"
            int lastDot = beforeArgs.LastIndexOf('.');
            if (lastDot >= 0) beforeArgs = beforeArgs.Substring(lastDot + 1);

            return beforeArgs;
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
        public static ExecutionResult Execute(string userCode, string callExpression, int tick = 0,
            List<SnapshotRowData> capturedRows = null)
        {
            if (string.IsNullOrWhiteSpace(userCode))
                return Fail("No code provided.");
            if (string.IsNullOrWhiteSpace(callExpression))
                return Fail("No call expression provided.");

            // Resolve #if/#else/#endif so all conditionally-compiled code is available
            userCode = StripPreprocessorDirectives(userCode);

            var scriptType = DetectScriptType(userCode);

            try
            {
                bool hadClassWrapper;
                string source = BuildSource(userCode, callExpression, tick, scriptType, out hadClassWrapper, capturedRows);

                // Diagnostic: save generated source for inspection
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "SELcd_LastGeneratedSource.cs"), source); }
                catch { /* ignore */ }

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

        /// <summary>
        /// Compiles using the animation pipeline (which runs the constructor body)
        /// and executes a single frame.  Use this instead of <see cref="Execute"/>
        /// when the script has class-level fields initialised in the constructor
        /// (e.g. arrays, RNG seeds, phase offsets).
        /// </summary>
        public static ExecutionResult ExecuteWithInit(string userCode, string callExpression,
            List<SnapshotRowData> capturedRows = null)
        {
            AnimationContext discarded;
            var result = ExecuteWithInit(userCode, callExpression, capturedRows, out discarded);
            discarded?.Dispose();
            return result;
        }

        /// <summary>
        /// Same as the parameterless overload, but keeps the compiled
        /// <see cref="AnimationContext"/> alive in <paramref name="keepAliveCtx"/>
        /// so callers can reuse it for field inspection without a second compilation.
        /// The caller takes ownership and must dispose the context when finished.
        /// </summary>
        public static ExecutionResult ExecuteWithInit(string userCode, string callExpression,
            List<SnapshotRowData> capturedRows, out AnimationContext keepAliveCtx)
        {
            keepAliveCtx = null;
            if (string.IsNullOrWhiteSpace(userCode))
                return Fail("No code provided.");
            // callExpression may be null — CompileForAnimation auto-detects
            // all rendering methods for non-PB scripts when null.

            // Resolve #if/#else/#endif so all conditionally-compiled code is available
            userCode = StripPreprocessorDirectives(userCode);

            AnimationContext ctx = null;
            try
            {
                ctx = CompileForAnimation(userCode, callExpression, capturedRows);
                InitAnimation(ctx);
                var result = RunAnimationFrame(ctx, 32, 0, 0.016);
                result.ScriptType = ctx.ScriptType;
                keepAliveCtx = ctx;   // transfer ownership to caller
                ctx = null;           // prevent finally from disposing
                return result;
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
            finally
            {
                ctx?.Dispose();
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
            internal MethodInfo SetElapsedPlayTimeMethod;
            internal MethodInfo GetEchoLogMethod;
            internal MethodInfo GetMethodTimingsMethod;
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
        public static AnimationContext CompileForAnimation(string userCode, string callExpression = null,
            List<SnapshotRowData> capturedRows = null)
        {
            // Resolve #if/#else/#endif so all conditionally-compiled code is available
            userCode = StripPreprocessorDirectives(userCode);

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
                else if (scriptType == ScriptType.PulsarPlugin || scriptType == ScriptType.ModSurface || scriptType == ScriptType.TorchPlugin)
                {
                    // Prefer orchestrators (methods returning List<MySprite>) which
                    // call sub-methods internally — avoids double-calling sub-methods.
                    animCalls = DetectSpriteReturnCalls(userCode);
                    if (animCalls.Count == 0)
                    {
                        // No orchestrators — use surface methods, filtered to
                        // top-level only (skip methods called by other detected methods)
                        animCalls = new List<string>();
                        DetectSurfaceCalls(userCode, animCalls, capturedRows);
                        animCalls = FilterTopLevelCalls(userCode, animCalls);
                    }
                    if (animCalls.Count == 0)
                    {
                        animCalls = new List<string>();
                        DetectLcdHelperCalls(userCode, animCalls);
                    }
                    if (animCalls.Count == 0)
                    {
                        DetectInlineRenderMethods(userCode, animCalls, capturedRows);
                    }
                }
                else
                {
                    animCalls = DetectAllCallExpressions(userCode, capturedRows);
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
                case ScriptType.PulsarPlugin:
                case ScriptType.ModSurface:
                case ScriptType.TorchPlugin:
                    source = BuildModSurfaceAnimationSource(userCode, callExpression, stateUpdateCalls, capturedRows);
                    break;
                default: // LcdHelper
                    source = BuildLcdHelperAnimationSource(userCode, callExpression, stateUpdateCalls);
                    break;
            }

            // Diagnostic: save generated source for inspection
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "SELcd_LastGeneratedSource.cs"), source); }
            catch { /* ignore */ }

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
                SetElapsedPlayTimeMethod = runnerType.GetMethod("SetElapsedPlayTime"),
                GetEchoLogMethod = runnerType.GetMethod("GetEchoLog"),
                GetMethodTimingsMethod = runnerType.GetMethod("GetMethodTimings"),
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
            if (ctx.SetElapsedPlayTimeMethod != null)
                ctx.SetElapsedPlayTimeMethod.Invoke(ctx.Runner, new object[] { elapsedSeconds });

            // Run synchronously - AnimationPlayer already runs this on a ThreadPool thread.
            // Avoid creating a new Thread per frame to prevent memory pressure and GC thrashing.
            string[][] rawData = null;
            Exception runEx = null;

            try
            {
                rawData = (string[][])ctx.FrameMethod.Invoke(
                    ctx.Runner, new object[] { updateType, tick });
            }
            catch (Exception ex)
            {
                runEx = ex.InnerException ?? ex;
            }

            if (runEx != null)
                return Fail("Runtime error: " + runEx.Message);
            if (rawData == null)
                return Fail("No sprite data returned.");

            // Read echo log from runner
            List<string> outputLines = null;
            try
            {
                if (ctx.GetEchoLogMethod != null)
                {
                    var log = (string[])ctx.GetEchoLogMethod.Invoke(ctx.Runner, null);
                    if (log != null && log.Length > 0)
                        outputLines = new List<string>(log);
                }
            }
            catch { }

            // Read per-method timing data from runner
            Dictionary<string, double> methodTimings = null;
            try
            {
                if (ctx.GetMethodTimingsMethod != null)
                {
                    var raw = (string[])ctx.GetMethodTimingsMethod.Invoke(ctx.Runner, null);
                    if (raw != null && raw.Length > 0)
                    {
                        methodTimings = new Dictionary<string, double>(raw.Length);
                        foreach (string entry in raw)
                        {
                            int sep = entry.IndexOf('|');
                            if (sep > 0 && double.TryParse(entry.Substring(sep + 1),
                                NumberStyles.Float, CultureInfo.InvariantCulture, out double ms))
                                methodTimings[entry.Substring(0, sep)] = ms;
                        }
                    }
                }
            }
            catch { }

            return new ExecutionResult
            {
                Sprites = ConvertFromMatrix(rawData),
                ScriptType = ctx.ScriptType,
                OutputLines = outputLines,
                MethodTimings = methodTimings,
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
                                          int tick, ScriptType scriptType, out bool hadClassWrapper,
                                          List<SnapshotRowData> capturedRows = null)
        {
            switch (scriptType)
            {
                case ScriptType.ProgrammableBlock:
                    return BuildPbSource(userCode, callExpression, out hadClassWrapper);
                case ScriptType.PulsarPlugin:
                case ScriptType.ModSurface:
                case ScriptType.TorchPlugin:
                    return BuildModSurfaceSource(userCode, callExpression, out hadClassWrapper, capturedRows);
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
                "VRage.Utils", "Sandbox.Game.GameSystems", "InventoryManagerLight",
                "System", "System.Collections.Generic",
                "System.Globalization", "System.Linq", "System.Text", "System.Threading",
                // Block namespaces that introduce type-name collisions with SE types
                // (e.g. System.Net.Mime.ContentType vs VRage.Game.GUI.TextPanel.ContentType)
                "System.Net.Mime"
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
            sb.AppendLine("    using Sandbox.ModAPI;");
            sb.AppendLine("    using Sandbox.Game.GameSystems;");
            sb.AppendLine("    using InventoryManagerLight;");
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
            // Get method info from SpriteCollector parallel lists (if available)
            sb.AppendLine("                string methodName = (SpriteCollector.CapturedMethods != null && i < SpriteCollector.CapturedMethods.Count) ? SpriteCollector.CapturedMethods[i] ?? \"\" : \"\";");
            sb.AppendLine("                int methodIdx = (SpriteCollector.CapturedMethodIndices != null && i < SpriteCollector.CapturedMethodIndices.Count) ? SpriteCollector.CapturedMethodIndices[i] : -1;");
            sb.AppendLine("                int srcLine = (SpriteCollector.CapturedLineNumbers != null && i < SpriteCollector.CapturedLineNumbers.Count) ? SpriteCollector.CapturedLineNumbers[i] : -1;");
            sb.AppendLine("                int invocIdx = (SpriteCollector.CapturedInvocationIndices != null && i < SpriteCollector.CapturedInvocationIndices.Count) ? SpriteCollector.CapturedInvocationIndices[i] : -1;");
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
            sb.AppendLine("                    ((int)sp.Alignment).ToString(),");
            sb.AppendLine("                    methodName,");
            sb.AppendLine("                    methodIdx.ToString(),");
            sb.AppendLine("                    srcLine.ToString(),");
            sb.AppendLine("                    invocIdx.ToString()");
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

            // Instrument render methods for accurate sprite tracking
            stripped = InstrumentRenderMethods(stripped, userCode);
            stripped = InjectMethodTimings(stripped);

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
            sb.AppendLine("        private readonly System.Collections.Generic.List<string> _echoLog = new System.Collections.Generic.List<string>();");
            sb.AppendLine("        public void Echo(string text) { _echoLog.Add(text); }");
            sb.AppendLine("        public string[] GetEchoLog() { return _echoLog.ToArray(); }");
            sb.AppendLine("        private static readonly System.Collections.Generic.Dictionary<string, double> _methodTimings = new System.Collections.Generic.Dictionary<string, double>();");
            sb.AppendLine("        public string[] GetMethodTimings() { var r = new System.Collections.Generic.List<string>(); foreach (var kv in _methodTimings) r.Add(kv.Key + \"|\" + kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); return r.ToArray(); }");
            sb.AppendLine("        public void SaveCustomData(string d) { }");
            sb.AppendLine();
            sb.AppendLine("        public string[][] RunAllData() {");
            sb.AppendLine("            _echoLog.Clear();");
            sb.AppendLine("            _methodTimings.Clear();");
            sb.AppendLine("            SpriteCollector.Reset();");
            sb.AppendLine("            var sprites = new SELcdExec.TrackedSpriteList();");
            // Extract method name for tracking
            string methodName = ExtractMethodNameFromCall(callLine);
            if (!string.IsNullOrEmpty(methodName))
            {
                sb.AppendLine($"            SpriteCollector.SetCurrentMethod(\"{methodName}\");");
                sb.AppendLine("            sprites.BeginTrack();");
                sb.AppendLine($"            var _swT0 = System.Diagnostics.Stopwatch.StartNew();");
            }
            EmitCallLine(sb, callLine, "            ");
            if (!string.IsNullOrEmpty(methodName))
            {
                sb.AppendLine($"            _swT0.Stop(); _methodTimings[\"{methodName}\"] = _swT0.Elapsed.TotalMilliseconds;");
                sb.AppendLine("            sprites.EndTrack();");
            }
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

            // Instrument render methods for accurate sprite tracking
            stripped = InstrumentRenderMethods(stripped, userCode, isProgrammableBlock: true);
            stripped = InjectMethodTimings(stripped);

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
            sb.AppendLine("        private readonly System.Collections.Generic.List<string> _echoLog = new System.Collections.Generic.List<string>();");
            sb.AppendLine("        public new void Echo(string text) { _echoLog.Add(text); }");
            sb.AppendLine("        public string[] GetEchoLog() { return _echoLog.ToArray(); }");
            sb.AppendLine("        private static readonly System.Collections.Generic.Dictionary<string, double> _methodTimings = new System.Collections.Generic.Dictionary<string, double>();");
            sb.AppendLine("        public string[] GetMethodTimings() { var r = new System.Collections.Generic.List<string>(); foreach (var kv in _methodTimings) r.Add(kv.Key + \"|\" + kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); return r.ToArray(); }");
            sb.AppendLine();

            // Entry point
            sb.AppendLine("        public string[][] RunAllData() {");
            sb.AppendLine("            _echoLog.Clear();");
            sb.AppendLine("            _methodTimings.Clear();");
            sb.AppendLine("            _InitStubs();");
            sb.AppendLine("            SpriteCollector.Reset();");
            if (!string.IsNullOrWhiteSpace(ctorBody))
            {
                sb.AppendLine("            // Constructor body (Program()):");
                sb.AppendLine("            {");
                sb.AppendLine("                " + ctorBody);
                sb.AppendLine("            }");
            }
            sb.AppendLine("            var _swMain = System.Diagnostics.Stopwatch.StartNew();");
            EmitCallLine(sb, callLine, "            ");
            sb.AppendLine("            _swMain.Stop(); _methodTimings[\"Main\"] = _swMain.Elapsed.TotalMilliseconds;");
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

            // Instrument render methods for accurate sprite tracking
            stripped = InstrumentRenderMethods(stripped, userCode, isProgrammableBlock: true);
            stripped = InjectMethodTimings(stripped);

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
            sb.AppendLine("        private readonly System.Collections.Generic.List<string> _echoLog = new System.Collections.Generic.List<string>();");
            sb.AppendLine("        public new void Echo(string text) { _echoLog.Add(text); }");
            sb.AppendLine("        public string[] GetEchoLog() { return _echoLog.ToArray(); }");
            sb.AppendLine("        private static readonly System.Collections.Generic.Dictionary<string, double> _methodTimings = new System.Collections.Generic.Dictionary<string, double>();");
            sb.AppendLine("        public string[] GetMethodTimings() { var r = new System.Collections.Generic.List<string>(); foreach (var kv in _methodTimings) r.Add(kv.Key + \"|\" + kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); return r.ToArray(); }");
            sb.AppendLine();

            // AnimInit — called once to set up stubs + run constructor
            sb.AppendLine("        public void AnimInit() {");
            sb.AppendLine("            _InitStubs();");
            sb.AppendLine("            SpriteCollector.Reset();");
            sb.AppendLine("            Sandbox.ModAPI.StubSession._elapsedTotalSeconds = 120.0;");
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
            sb.AppendLine("            _echoLog.Clear();");
            sb.AppendLine("            _methodTimings.Clear();");
            sb.AppendLine("            SpriteCollector.Reset();");
            sb.AppendLine("            SpriteCollector.SetCurrentMethod(\"Main\");");
            sb.AppendLine("            var _swMain = System.Diagnostics.Stopwatch.StartNew();");
            sb.AppendLine("            " + mainCall);
            sb.AppendLine("            _swMain.Stop(); _methodTimings[\"Main\"] = _swMain.Elapsed.TotalMilliseconds;");
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
            sb.AppendLine();

            // SetElapsedPlayTime — advances MyAPIGateway.Session.ElapsedPlayTime each frame
            sb.AppendLine("        public void SetElapsedPlayTime(double elapsed) {");
            sb.AppendLine("            Sandbox.ModAPI.StubSession._elapsedTotalSeconds += elapsed;");
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
            string className, baseClassName;
            stripped = StripClassWrapper(stripped, out hadClassWrapper, out className, out baseClassName);

            string ctorBody = "";
            if (hadClassWrapper && !string.IsNullOrEmpty(className))
            {
                ctorBody = ExtractConstructorBody(stripped, className);
                stripped = StripConstructors(stripped, className);
            }
            stripped = StripReadonly(stripped);

            // Instrument render methods for accurate sprite tracking
            stripped = InstrumentRenderMethods(stripped, userCode);
            stripped = InjectMethodTimings(stripped);

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
            string baseDecl = (baseClassName == "MySessionComponentBase") ? " : MySessionComponentBase" : "";
            sb.AppendLine($"    public class LcdRunner{baseDecl} {{");

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
            sb.AppendLine("        private readonly System.Collections.Generic.List<string> _echoLog = new System.Collections.Generic.List<string>();");
            sb.AppendLine("        public void Echo(string text) { _echoLog.Add(text); }");
            sb.AppendLine("        public string[] GetEchoLog() { return _echoLog.ToArray(); }");
            sb.AppendLine("        private static readonly System.Collections.Generic.Dictionary<string, double> _methodTimings = new System.Collections.Generic.Dictionary<string, double>();");
            sb.AppendLine("        public string[] GetMethodTimings() { var r = new System.Collections.Generic.List<string>(); foreach (var kv in _methodTimings) r.Add(kv.Key + \"|\" + kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); return r.ToArray(); }");
            sb.AppendLine("        public void SaveCustomData(string d) { }");
            sb.AppendLine();

            // AnimInit
            sb.AppendLine("        public void AnimInit() {");
            sb.AppendLine("            Sandbox.ModAPI.StubSession._elapsedTotalSeconds = 120.0;");
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
            sb.AppendLine("            _echoLog.Clear();");
            sb.AppendLine("            _methodTimings.Clear();");
            sb.AppendLine("            SpriteCollector.Reset();");
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
            sb.AppendLine("            var sprites = new SELcdExec.TrackedSpriteList();");
            // Emit each call with method tracking and timing
            int swId = 0;
            foreach (string cl in callLines)
            {
                string cleanCall = cl.TrimEnd(';', ' ');
                string methodName = ExtractMethodNameFromCall(cleanCall);
                if (!string.IsNullOrEmpty(methodName))
                {
                    sb.AppendLine($"            SpriteCollector.SetCurrentMethod(\"{methodName}\");");
                    sb.AppendLine("            sprites.BeginTrack();");
                    sb.AppendLine($"            var _swT{swId} = System.Diagnostics.Stopwatch.StartNew();");
                }
                EmitCallLine(sb, cl, "            ");
                if (!string.IsNullOrEmpty(methodName))
                {
                    sb.AppendLine($"            _swT{swId}.Stop(); _methodTimings[\"{methodName}\"] = _swT{swId}.Elapsed.TotalMilliseconds;");
                    sb.AppendLine("            sprites.EndTrack();");
                    sb.AppendLine("            SpriteCollector.SetCurrentMethod(null);");
                    swId++;
                }
            }
            AppendSpriteSerialisation(sb, "sprites");
            sb.AppendLine("        }");
            sb.AppendLine();

            // SetElapsedPlayTime — advances MyAPIGateway.Session.ElapsedPlayTime each frame
            sb.AppendLine("        public void SetElapsedPlayTime(double elapsed) {");
            sb.AppendLine("            Sandbox.ModAPI.StubSession._elapsedTotalSeconds += elapsed;");
            sb.AppendLine("        }");

            sb.AppendLine("    }"); // class LcdRunner
            sb.AppendLine("}");     // namespace
            return sb.ToString();
        }

        // ── Mod / surface animation source (compile once, run many) ─────────

        private static string BuildModSurfaceAnimationSource(string userCode, string callExpression,
            List<string> stateUpdateCalls = null, List<SnapshotRowData> capturedRows = null)
        {
            string[] userUsings;
            string stripped = ExtractUsings(userCode, out userUsings);
            bool hadClassWrapper;
            string className, baseClassName;
            stripped = StripClassWrapper(stripped, out hadClassWrapper, out className, out baseClassName);

            string ctorBody = "";
            if (hadClassWrapper && !string.IsNullOrEmpty(className))
            {
                ctorBody = ExtractConstructorBody(stripped, className);
                stripped = StripConstructors(stripped, className);
            }
            stripped = StripReadonly(stripped);

            // Instrument render methods for accurate sprite tracking
            stripped = InstrumentRenderMethods(stripped, userCode);
            stripped = InjectMethodTimings(stripped);

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
            string baseDecl = (baseClassName == "MySessionComponentBase") ? " : MySessionComponentBase" : "";
            sb.AppendLine($"    public class LcdRunner{baseDecl} {{");
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
            sb.AppendLine("        private readonly System.Collections.Generic.List<string> _echoLog = new System.Collections.Generic.List<string>();");
            sb.AppendLine("        public void Echo(string text) { _echoLog.Add(text); }");
            sb.AppendLine("        public string[] GetEchoLog() { return _echoLog.ToArray(); }");
            sb.AppendLine("        private static readonly System.Collections.Generic.Dictionary<string, double> _methodTimings = new System.Collections.Generic.Dictionary<string, double>();");
            sb.AppendLine("        public string[] GetMethodTimings() { var r = new System.Collections.Generic.List<string>(); foreach (var kv in _methodTimings) r.Add(kv.Key + \"|\" + kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); return r.ToArray(); }");
            sb.AppendLine("        public void SaveCustomData(string d) { }");
            sb.AppendLine();

            // AnimInit
            sb.AppendLine("        public void AnimInit() {");
            sb.AppendLine("            Sandbox.ModAPI.StubSession._elapsedTotalSeconds = 120.0;");
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
            sb.AppendLine("            _echoLog.Clear();");
            sb.AppendLine("            _methodTimings.Clear();");
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
            // Inject sample queue data when the method is a queue-processor (e.g. ApplyPendingUpdates)
            string sampleCode = BuildSampleEnqueueCode(userCode, callExpression, capturedRows);
            if (sampleCode != null) sb.Append(sampleCode);
            // Declare sprites before calls — call expressions may reference it
            // as a parameter (LCD helpers) or assignment target (return methods)
            sb.AppendLine("            var sprites = new SELcdExec.TrackedSpriteList();");
            EmitCallsWithContextCollection(sb, userCode, callLines, "            ");
            // Only merge SpriteCollector when no call directly manages the sprites
            // list (return-method assignments and LCD-helper parameters already
            // collect sprites; merging would double-count since those methods may
            // also add to SpriteCollector internally via surface.DrawFrame())
            if (!callLines.Exists(cl => cl.Contains("sprites")))
                sb.AppendLine("            { var _c = SpriteCollector.Captured; if (_c.Count > 0) sprites.AddRange(_c); }");
            AppendSpriteSerialisation(sb, "sprites");
            sb.AppendLine("        }");
            sb.AppendLine();

            // SetElapsedPlayTime — advances MyAPIGateway.Session.ElapsedPlayTime each frame
            sb.AppendLine("        public void SetElapsedPlayTime(double elapsed) {");
            sb.AppendLine("            Sandbox.ModAPI.StubSession._elapsedTotalSeconds += elapsed;");
            sb.AppendLine("        }");

            sb.AppendLine("    }"); // class LcdRunner
            sb.AppendLine("}");     // namespace
            return sb.ToString();
        }

        // ── Mod / surface script source ──────────────────────────────────────

        private static string BuildModSurfaceSource(string userCode, string callExpression,
                                                    out bool hadClassWrapper,
                                                    List<SnapshotRowData> capturedRows = null)
        {
            string[] userUsings;
            string stripped = ExtractUsings(userCode, out userUsings);
            string className, baseClassName;
            stripped = StripClassWrapper(stripped, out hadClassWrapper, out className, out baseClassName);
            if (hadClassWrapper && !string.IsNullOrEmpty(className))
                stripped = StripConstructors(stripped, className);

            // Instrument render methods for accurate sprite tracking
            stripped = InstrumentRenderMethods(stripped, userCode);
            stripped = InjectMethodTimings(stripped);

            // Rewrite the call expression: replace `surface` token with the local stub variable
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
            string baseDecl = (baseClassName == "MySessionComponentBase") ? " : MySessionComponentBase" : "";
            sb.AppendLine($"    public class LcdRunner{baseDecl} {{");
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
            sb.AppendLine("        private readonly System.Collections.Generic.List<string> _echoLog = new System.Collections.Generic.List<string>();");
            sb.AppendLine("        public void Echo(string text) { _echoLog.Add(text); }");
            sb.AppendLine("        public string[] GetEchoLog() { return _echoLog.ToArray(); }");
            sb.AppendLine("        private static readonly System.Collections.Generic.Dictionary<string, double> _methodTimings = new System.Collections.Generic.Dictionary<string, double>();");
            sb.AppendLine("        public string[] GetMethodTimings() { var r = new System.Collections.Generic.List<string>(); foreach (var kv in _methodTimings) r.Add(kv.Key + \"|\" + kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); return r.ToArray(); }");
            sb.AppendLine("        public void SaveCustomData(string d) { }");
            sb.AppendLine();
            sb.AppendLine("        public string[][] RunAllData() {");
            sb.AppendLine("            _echoLog.Clear();");
            sb.AppendLine("            _methodTimings.Clear();");
            sb.AppendLine("            SpriteCollector.Reset();");
            sb.AppendLine("            var _stubSurface = new StubTextSurface(512f, 512f);");
            // Inject sample queue data when the method is a queue-processor (e.g. ApplyPendingUpdates)
            string sampleCode = BuildSampleEnqueueCode(userCode, callExpression, capturedRows);
            if (sampleCode != null) sb.Append(sampleCode);
            sb.AppendLine("            var sprites = new SELcdExec.TrackedSpriteList();");
            EmitCallsWithContextCollection(sb, userCode, callLines, "            ");
            if (!callLines.Exists(cl => cl.Contains("sprites")))
                sb.AppendLine("            { var _c = SpriteCollector.Captured; if (_c.Count > 0) sprites.AddRange(_c); }");
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

        internal static Assembly Compile(string source)
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
                    // Save a copy for diagnostics before deleting
                    string diagPath = Path.Combine(Path.GetTempPath(), "SELcd_LastCompileError.cs");
                    try { File.Copy(tempSrc, diagPath, true); } catch { }

                    // Strip temp file path from error messages for readability
                    output = output.Replace(tempSrc, "<user code>");
                    throw new InvalidOperationException(
                        "Compilation errors:\n" + output.TrimEnd()
                        + "\n\n[Diagnostic: generated source saved to " + diagPath + "]");
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

            // Read echo log from runner
            List<string> outputLines = null;
            try
            {
                MethodInfo getEcho = runnerType.GetMethod("GetEchoLog");
                if (getEcho != null)
                {
                    var log = (string[])getEcho.Invoke(runner, null);
                    if (log != null && log.Length > 0)
                        outputLines = new List<string>(log);
                }
            }
            catch { }

            // Read per-method timing data from runner
            Dictionary<string, double> methodTimings = null;
            try
            {
                MethodInfo getTimings = runnerType.GetMethod("GetMethodTimings");
                if (getTimings != null)
                {
                    var raw = (string[])getTimings.Invoke(runner, null);
                    if (raw != null && raw.Length > 0)
                    {
                        methodTimings = new Dictionary<string, double>(raw.Length);
                        foreach (string entry in raw)
                        {
                            int sep = entry.IndexOf('|');
                            if (sep > 0 && double.TryParse(entry.Substring(sep + 1),
                                NumberStyles.Float, CultureInfo.InvariantCulture, out double ms))
                                methodTimings[entry.Substring(0, sep)] = ms;
                        }
                    }
                }
            }
            catch { }

            return new ExecutionResult { Sprites = ConvertFromMatrix(rawData), OutputLines = outputLines, MethodTimings = methodTimings };
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
                        IsFromExecution = true, // Mark as execution-generated to prevent code insertion
                    };

                    if (isText)
                    {
                        entry.Text = row[1];
                        entry.SpriteName = null; // Clear default SpriteName so DisplayName shows text
                    }
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

                    // Read source method info (fields 13 and 14) if present
                    if (row.Length > 13 && !string.IsNullOrEmpty(row[13]))
                        entry.SourceMethodName = row[13];
                    if (row.Length > 14 && !string.IsNullOrEmpty(row[14]))
                    {
                        int methodIdx;
                        if (int.TryParse(row[14], out methodIdx))
                            entry.SourceMethodIndex = methodIdx;
                    }
                    // Read source line number (field 15) if present
                    if (row.Length > 15 && !string.IsNullOrEmpty(row[15]))
                    {
                        int srcLine;
                        if (int.TryParse(row[15], out srcLine))
                            entry.SourceLineNumber = srcLine;
                    }
                    // Read invocation index (field 16) if present
                    if (row.Length > 16 && !string.IsNullOrEmpty(row[16]))
                    {
                        int invocIdx;
                        if (int.TryParse(row[16], out invocIdx))
                            entry.SourceInvocationIndex = invocIdx;
                    }

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

        /// <summary>
        /// Generates a method-specific <c>LcdSpriteRow</c> initializer based on the
        /// render method name.  When <paramref name="capturedRows"/> contains data from
        /// a runtime snapshot, the first row whose kind matches the method name is used
        /// instead of a placeholder.
        /// </summary>
        private static string GuessLcdSpriteRowArg(string methodName,
            List<SnapshotRowData> capturedRows = null)
        {
            string lower = methodName.ToLowerInvariant();

            // Try to find a matching captured row by kind
            if (capturedRows != null && capturedRows.Count > 0)
            {
                SnapshotRowData match = null;
                foreach (var r in capturedRows)
                {
                    string rk = (r.Kind ?? "").ToLowerInvariant();
                    if (lower.Contains(rk) || rk.Contains(lower.Replace("render", "")))
                    {
                        match = r;
                        break;
                    }
                }
                // Fallback: use the first captured row that isn't a separator
                if (match == null)
                {
                    foreach (var r in capturedRows)
                    {
                        if (!string.Equals(r.Kind, "Separator", StringComparison.OrdinalIgnoreCase))
                        {
                            match = r;
                            break;
                        }
                    }
                }
                if (match != null)
                    return match.ToCSharpInitializer();
            }

            if (lower.Contains("header"))
                return "new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Header, "
                     + "Text = \"Inventory\", TextColor = Color.White }";

            if (lower.Contains("separator"))
                return "new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Separator }";

            if (lower.Contains("itembar"))
                return "new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.ItemBar, "
                     + "Text = \"Steel Plate\", StatText = \"800 / 1000\", "
                     + "BarFill = 0.8f, BarFillColor = new Color(0, 200, 80), TextColor = Color.White }";

            if (lower.Contains("item"))
                return "new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Item, "
                     + "Text = \"Iron Ingot\", StatText = \"1,500\", "
                     + "IconSprite = \"MyObjectBuilder_Ingot/Iron\", TextColor = Color.White }";

            if (lower.Contains("bar"))
                return "new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Bar, "
                     + "BarFill = 0.6f, BarFillColor = new Color(0, 200, 80) }";

            if (lower.Contains("footer"))
                return "new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Footer, "
                     + "Text = \"Updated: just now\", TextColor = new Color(110, 110, 115) }";

            if (lower.Contains("stat"))
                return "new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Stat, "
                     + "Text = \"Status: Online\", TextColor = Color.White }";

            // Generic fallback with visible placeholder
            return "new LcdSpriteRow { RowKind = LcdSpriteRow.Kind.Stat, "
                 + "Text = \"[placeholder]\", TextColor = Color.White }";
        }

        /// <summary>
        /// Emits call lines into RunFrame/RunAllData.  For methods that collect sprites
        /// into a context parameter (e.g. <c>ctx.Sprites.Add</c>), automatically wraps
        /// the call with context variable creation and sprite collection so the sprites
        /// end up in the <c>sprites</c> list.
        /// Also sets SpriteCollector.CurrentMethod before each call for source tracking.
        /// </summary>
        private static void EmitCallsWithContextCollection(StringBuilder sb, string userCode,
            List<string> callLines, string indent)
        {
            int ctxId = 0;
            int swId = 0;
            foreach (string cl in callLines)
            {
                string cleanCall = cl.TrimEnd(';', ' ');
                int parenPos = cleanCall.IndexOf('(');
                if (parenPos <= 0)
                {
                    sb.AppendLine(indent + cl);
                    continue;
                }

                // Extract method name (handle "sprites = Method(...)" assignment)
                string beforeParen = cleanCall.Substring(0, parenPos).Trim();
                string methodName = beforeParen;
                int eqPos = methodName.LastIndexOf('=');
                if (eqPos >= 0) methodName = methodName.Substring(eqPos + 1).Trim();

                // Set current method for sprite source tracking and begin tracking
                sb.AppendLine($"{indent}SpriteCollector.SetCurrentMethod(\"{methodName}\");");
                sb.AppendLine($"{indent}sprites.BeginTrack();");

                // Find the method declaration in user code
                var rxMethod = new Regex(
                    @"(?:private|public|internal|protected)\s*(?:static\s+)?void\s+" +
                    Regex.Escape(methodName) + @"\s*\(([^)]*)\)\s*\{",
                    RegexOptions.Singleline);
                var match = rxMethod.Match(userCode);
                if (!match.Success)
                {
                    sb.AppendLine($"{indent}var _swT{swId} = System.Diagnostics.Stopwatch.StartNew();");
                    EmitCallLine(sb, cl, indent);
                    sb.AppendLine($"{indent}_swT{swId}.Stop(); _methodTimings[\"{methodName}\"] = _swT{swId}.Elapsed.TotalMilliseconds;");
                    swId++;
                    sb.AppendLine($"{indent}sprites.EndTrack();");
                    sb.AppendLine($"{indent}SpriteCollector.SetCurrentMethod(null);");
                    continue;
                }

                // Scan body for param.Sprites.Add pattern
                int bodyStart = match.Index + match.Length;
                int bodyEnd = ScanToMatchingBrace(userCode, bodyStart);
                if (bodyEnd <= bodyStart)
                {
                    sb.AppendLine($"{indent}var _swT{swId} = System.Diagnostics.Stopwatch.StartNew();");
                    EmitCallLine(sb, cl, indent);
                    sb.AppendLine($"{indent}_swT{swId}.Stop(); _methodTimings[\"{methodName}\"] = _swT{swId}.Elapsed.TotalMilliseconds;");
                    swId++;
                    sb.AppendLine($"{indent}sprites.EndTrack();");
                    sb.AppendLine($"{indent}SpriteCollector.SetCurrentMethod(null);");
                    continue;
                }
                string body = userCode.Substring(bodyStart, bodyEnd - 1 - bodyStart);

                var rxSpriteAdd = new Regex(@"(\w+)\.Sprites\.Add");
                var spriteMatch = rxSpriteAdd.Match(body);
                if (!spriteMatch.Success)
                {
                    sb.AppendLine($"{indent}var _swT{swId} = System.Diagnostics.Stopwatch.StartNew();");
                    EmitCallLine(sb, cl, indent);
                    sb.AppendLine($"{indent}_swT{swId}.Stop(); _methodTimings[\"{methodName}\"] = _swT{swId}.Elapsed.TotalMilliseconds;");
                    swId++;
                    sb.AppendLine($"{indent}sprites.EndTrack();");
                    sb.AppendLine($"{indent}SpriteCollector.SetCurrentMethod(null);");
                    continue;
                }
                string ctxParamName = spriteMatch.Groups[1].Value;

                // Find the type and position of that parameter
                string paramDecl = match.Groups[1].Value;
                string ctxTypeName = null;
                int paramIndex = -1;
                int idx = 0;
                foreach (string param in paramDecl.Split(','))
                {
                    string p = param.Trim();
                    string[] parts = p.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && parts[parts.Length - 1] == ctxParamName)
                    {
                        ctxTypeName = parts[0];
                        paramIndex = idx;
                        break;
                    }
                    idx++;
                }
                if (ctxTypeName == null || paramIndex < 0)
                {
                    sb.AppendLine($"{indent}var _swT{swId} = System.Diagnostics.Stopwatch.StartNew();");
                    EmitCallLine(sb, cl, indent);
                    sb.AppendLine($"{indent}_swT{swId}.Stop(); _methodTimings[\"{methodName}\"] = _swT{swId}.Elapsed.TotalMilliseconds;");
                    swId++;
                    sb.AppendLine($"{indent}sprites.EndTrack();");
                    sb.AppendLine($"{indent}SpriteCollector.SetCurrentMethod(null);");
                    continue;
                }

                string varName = "_ctxIso" + ctxId++;

                // Use an initializer method (e.g. EnsureCtx) if one exists for this type
                var rxInitMethod = new Regex(
                    @"(?:private|public|internal|protected)\s*(?:static\s+)?" +
                    Regex.Escape(ctxTypeName) + @"\s+(\w+)\s*\(\s*" +
                    Regex.Escape(ctxTypeName) + @"\s+\w+\s*\)");
                var initMatch = rxInitMethod.Match(userCode);
                if (initMatch.Success)
                    sb.AppendLine($"{indent}var {varName} = {initMatch.Groups[1].Value}(null);");
                else
                    sb.AppendLine($"{indent}var {varName} = new {ctxTypeName}();");

                // Replace the context parameter in the call with our variable
                string argsSection = cleanCall.Substring(parenPos + 1);
                int closeIdx = argsSection.LastIndexOf(')');
                if (closeIdx >= 0) argsSection = argsSection.Substring(0, closeIdx);
                var args = SplitCallArgs(argsSection);
                if (paramIndex < args.Count)
                    args[paramIndex] = varName;

                string prefix = eqPos >= 0 ? beforeParen.Substring(0, eqPos + 1).TrimEnd() + " " : "";
                sb.AppendLine($"{indent}var _swT{swId} = System.Diagnostics.Stopwatch.StartNew();");
                sb.AppendLine($"{indent}{prefix}{methodName}({string.Join(", ", args)});");
                sb.AppendLine($"{indent}_swT{swId}.Stop(); _methodTimings[\"{methodName}\"] = _swT{swId}.Elapsed.TotalMilliseconds;");
                swId++;

                // Collect sprites from context
                sb.AppendLine($"{indent}if ({varName}.Sprites != null && {varName}.Sprites.Count > 0) sprites.AddRange({varName}.Sprites);");
                sb.AppendLine($"{indent}sprites.EndTrack();");
                sb.AppendLine($"{indent}SpriteCollector.SetCurrentMethod(null);");
            }
        }

        /// <summary>
        /// Splits a call-argument string by commas, respecting nested parens/braces/brackets.
        /// </summary>
        private static List<string> SplitCallArgs(string argsStr)
        {
            var args = new List<string>();
            int depth = 0;
            int start = 0;
            for (int i = 0; i < argsStr.Length; i++)
            {
                char c = argsStr[i];
                if (c == '(' || c == '{' || c == '[') depth++;
                else if (c == ')' || c == '}' || c == ']') depth--;
                else if (c == ',' && depth == 0)
                {
                    args.Add(argsStr.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            string last = argsStr.Substring(start).Trim();
            if (last.Length > 0) args.Add(last);
            return args;
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

        /// <summary>
        /// Instruments render method bodies with SetCurrentMethod calls for accurate sprite tracking.
        /// This enables nested method calls to correctly track which method produced each sprite.
        /// Injects "SpriteCollector.SetCurrentMethod(\"MethodName\");" as the first line of each
        /// method that takes or returns List&lt;MySprite&gt; or similar sprite collection.
        /// ALSO instruments each .Add() call to record the method immediately.
        /// </summary>
        private static string InstrumentRenderMethods(string code, string originalCode = null, bool isProgrammableBlock = false)
        {
            if (string.IsNullOrEmpty(code)) return code;

            // STEP 1: Inject SetCurrentMethod at method entry
            // Pattern matches method declarations that:
            // 1. Take sprite list parameters: void RenderSomething(List<MySprite> sprites, ...)
            // 2. Return sprite lists: List<MySprite> BuildSprites(...)
            // 3. Take IEnumerable<MySprite>: void DrawGauge(IEnumerable<MySprite> sprites, ...)
            // 4. Take MySpriteDrawFrame: void RenderPanel(MySpriteDrawFrame frame, ...)
            // 5. Take IMyTextSurface: void DrawFrame(IMyTextSurface surface, ...)
            var rxMethod = new Regex(
                @"(?:private|public|internal|protected|static|\s)+(?:(?<returns>List<MySprite>|IEnumerable<MySprite>)|void)\s+(?<name>\w+)\s*\((?<params>[^)]*)\)\s*\{",
                RegexOptions.Singleline);

            // Collect ALL sprite list parameter names across all render methods
            // so InstrumentAddCalls knows which .Add() calls are sprite adds.
            // Includes MySpriteDrawFrame parameters — these have .Add() that captures
            // sprites via the stub, so we need to instrument them for line number tracking.
            var spriteListNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in rxMethod.Matches(code))
            {
                string parameters = m.Groups["params"].Value;
                foreach (string param in parameters.Split(','))
                {
                    string p = param.Trim();
                    if (p.Contains("List<MySprite>") || p.Contains("IEnumerable<MySprite>") || p.Contains("MySpriteDrawFrame"))
                    {
                        string[] parts = p.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                            spriteListNames.Add(parts[parts.Length - 1]);
                    }
                }
            }
            System.Diagnostics.Debug.WriteLine($"[InstrumentRenderMethods] Sprite list param names: {string.Join(", ", spriteListNames)}");

            var result = new StringBuilder(code.Length + 2000);
            int pos = 0;
            int instrumentedCount = 0;

            foreach (Match match in rxMethod.Matches(code))
            {
                string methodName = match.Groups["name"].Value;
                string returns = match.Groups["returns"].Value;
                string parameters = match.Groups["params"].Value;

                // Only instrument if method returns sprites OR takes sprites/frame/surface as parameter
                bool hasSprites = !string.IsNullOrEmpty(returns) || 
                                  parameters.Contains("List<MySprite>") || 
                                  parameters.Contains("IEnumerable<MySprite>") ||
                                  parameters.Contains("MySpriteDrawFrame") ||
                                  parameters.Contains("IMyTextSurface") ||
                                  parameters.Contains("IMyTextPanel");

                if (!hasSprites)
                {
                    continue; // Skip non-sprite methods
                }

                System.Diagnostics.Debug.WriteLine($"[InstrumentRenderMethods] Instrumenting: {methodName}");
                instrumentedCount++;

                int insertPos = match.Index + match.Length; // Right after the opening brace

                // Append everything up to and including the opening brace
                result.Append(code, pos, insertPos - pos);

                // Inject the SetCurrentMethod call
                result.AppendLine();
                result.Append("            SpriteCollector.SetCurrentMethod(\"");
                result.Append(methodName);
                result.AppendLine("\");");

                pos = insertPos;
            }

            System.Diagnostics.Debug.WriteLine($"[InstrumentRenderMethods] Total instrumented: {instrumentedCount} methods");

            // Append remainder of code
            result.Append(code, pos, code.Length - pos);

            // STEP 1b: Inject SetCurrentMethod at the start of each switch-case block
            // that corresponds to a virtual render method (e.g. case Header: → SetCurrentMethod("RenderHeader")).
            // This is required for ModSurface/switch-case plugins where sprites are added
            // via MySpriteDrawFrame.Add() inside case blocks — those blocks are not real
            // methods, so STEP 1 above doesn't instrument them.
            // NOTE: Skip for PB scripts — PBs have a single Main() method and injecting
            // SetCurrentMethod("Render0") mid-method creates inconsistent method attribution
            // that breaks sprite navigation index lookups.
            string codeAfterStep1 = result.ToString();
            if (!isProgrammableBlock)
            {
                var rxSwitch = new Regex(
                    @"switch\s*\(\s*(\w+(?:\.\w+)?)\s*\)\s*\{",
                    RegexOptions.Singleline);
                var rxCase = new Regex(
                    @"case\s+(?:[\w\.]+\.)?(\w+)\s*:",
                    RegexOptions.Singleline);

                var caseInjections = new List<KeyValuePair<int, string>>(); // insertion offset → code to inject

                foreach (Match switchMatch in rxSwitch.Matches(codeAfterStep1))
                {
                    int bodyStart = switchMatch.Index + switchMatch.Length;
                    int bodyEnd = ScanToMatchingBrace(codeAfterStep1, bodyStart);
                    if (bodyEnd <= bodyStart) continue;

                    string switchBody = codeAfterStep1.Substring(bodyStart, bodyEnd - 1 - bodyStart);

                    foreach (Match caseMatch in rxCase.Matches(switchBody))
                    {
                        string caseName = caseMatch.Groups[1].Value;
                        if (string.IsNullOrWhiteSpace(caseName)) continue;

                        // Skip common non-render case values (default, numeric, etc.)
                        if (caseName == "default") continue;

                        string virtualMethodName = "Render" + caseName;

                        // Absolute position in codeAfterStep1 right after the "case Xxx:" colon
                        int absolutePos = bodyStart + caseMatch.Index + caseMatch.Length;
                        string injection = " SpriteCollector.SetCurrentMethod(\"" + virtualMethodName + "\");";
                        caseInjections.Add(new KeyValuePair<int, string>(absolutePos, injection));
                    }
                }

                if (caseInjections.Count > 0)
                {
                    // Insert from last to first to preserve positions
                    var sb2 = new StringBuilder(codeAfterStep1);
                    for (int i = caseInjections.Count - 1; i >= 0; i--)
                    {
                        sb2.Insert(caseInjections[i].Key, caseInjections[i].Value);
                    }
                    codeAfterStep1 = sb2.ToString();
                    System.Diagnostics.Debug.WriteLine(
                        $"[InstrumentRenderMethods] Injected SetCurrentMethod into {caseInjections.Count} switch-case block(s)");
                }
            }

            // STEP 1c: Detect local List<MySprite> variables (e.g. var spriteList = new List<MySprite>())
            // so InstrumentAddCalls can instrument their .Add() calls with PreRecord.
            // This is critical for the batch-flush pattern where sprites are collected
            // in a local list then flushed to MySpriteDrawFrame.Add() in a separate loop.
            var localSpriteListNames = new HashSet<string>(StringComparer.Ordinal);
            var rxLocalList = new Regex(@"(?:var|List<MySprite>)\s+(\w+)\s*=\s*new\s+List<MySprite>", RegexOptions.Singleline);
            string codeToScanLocals = originalCode ?? codeAfterStep1;
            foreach (Match localMatch in rxLocalList.Matches(codeToScanLocals))
            {
                string localName = localMatch.Groups[1].Value;
                // Don't add if already a method parameter (parameter takes precedence)
                if (!spriteListNames.Contains(localName))
                    localSpriteListNames.Add(localName);
            }
            if (localSpriteListNames.Count > 0)
                System.Diagnostics.Debug.WriteLine($"[InstrumentRenderMethods] Local sprite list names: {string.Join(", ", localSpriteListNames)}");

            // STEP 1d: Detect local MySpriteDrawFrame variables (e.g. var frame = surface.DrawFrame())
            // These use .Add() which goes through the MySpriteDrawFrame stub. We add them
            // to spriteListNames (NOT localSpriteListNames) so InstrumentAddCalls injects
            // RecordSpriteMethod(lineNum) — not PreRecord — because the stub's Add() already
            // calls RecordSpriteMethod(-1) and the instrumented call must merge the real line.
            // This covers PBs, Mods, Pulsar, Torch — any script that uses frame.Add().
            var rxLocalFrame = new Regex(@"(?:var|MySpriteDrawFrame)\s+(\w+)\s*=\s*\w+\.DrawFrame\s*\(", RegexOptions.Singleline);
            foreach (Match frameMatch in rxLocalFrame.Matches(codeToScanLocals))
            {
                string frameName = frameMatch.Groups[1].Value;
                if (!spriteListNames.Contains(frameName) && !localSpriteListNames.Contains(frameName))
                {
                    spriteListNames.Add(frameName);
                    System.Diagnostics.Debug.WriteLine($"[InstrumentRenderMethods] Detected MySpriteDrawFrame local: '{frameName}'");
                }
            }

            // STEP 2: Instrument each .Add() call to record the method
            // This is critical because List<T>.Add is NOT virtual, so our TrackedSpriteList.Add
            // doesn't get called when the variable is declared as List<MySprite>.
            // For method parameter lists: s.Add(x); → s.Add(x); SpriteCollector.RecordSpriteMethod(lineNum);
            // For local lists (batch-flush): s.Add(x); → s.Add(x); SpriteCollector.PreRecord(lineNum);
            string instrumentedCode = codeAfterStep1;
            instrumentedCode = InstrumentAddCalls(instrumentedCode, spriteListNames, localSpriteListNames, originalCode);

            return instrumentedCode;
        }

        /// <summary>
        /// Injects per-method Stopwatch timing into every user method so the code
        /// heatmap can show execution time for ALL methods, not just top-level calls.
        /// Wraps each method body in try/finally { _methodTimings["Name"] = elapsed }.
        /// </summary>
        private static string InjectMethodTimings(string code)
        {
            if (string.IsNullOrEmpty(code)) return code;

            // Match method declarations (access modifier + optional static + return type + name + params + {)
            // Use [ \t] instead of \s in return-type class to prevent cross-newline greedy matching
            var rxMethod = new Regex(
                @"(?:private|public|internal|protected)[ \t]+(?:static[ \t]+)?(?:override[ \t]+)?(?:void|[\w<>\[\], \t]+)[ \t]+(\w+)\s*\([^)]*\)\s*\{");

            // Collect all matches first, then process from end to start so char positions stay valid
            var matches = new List<Match>();
            foreach (Match m in rxMethod.Matches(code))
                matches.Add(m);

            if (matches.Count == 0) return code;

            // Process from last match to first to preserve positions
            var sb = new StringBuilder(code);
            for (int i = matches.Count - 1; i >= 0; i--)
            {
                var m = matches[i];
                string methodName = m.Groups[1].Value;

                // Skip generated helper methods (these start with _ or are infrastructure)
                if (methodName.StartsWith("_") || methodName == "GetEchoLog" ||
                    methodName == "GetMethodTimings" || methodName == "RunAllData" ||
                    methodName == "RunFrame")
                    continue;

                int braceOpen = m.Index + m.Length - 1; // position of the opening {
                // Find matching closing brace
                int depth = 1;
                int braceClose = -1;
                for (int c = braceOpen + 1; c < sb.Length && depth > 0; c++)
                {
                    if (sb[c] == '{') depth++;
                    else if (sb[c] == '}') { depth--; if (depth == 0) braceClose = c; }
                }
                if (braceClose < 0) continue;

                // Sanitize method name for variable use (replace any non-alphanumeric)
                string safeVar = Regex.Replace(methodName, @"\W", "_");

                // Insert closing: } finally { record timing + restore CurrentMethod } right BEFORE the closing brace
                string finallyBlock = $"\n            }} finally {{ _sw_{safeVar}.Stop(); _methodTimings[\"{methodName}\"] = _sw_{safeVar}.Elapsed.TotalMilliseconds; SpriteCollector.SetCurrentMethod(_prev_{safeVar}); }}";
                sb.Insert(braceClose, finallyBlock);

                // Insert opening: save CurrentMethod + Stopwatch start + try { right AFTER the opening brace
                string tryBlock = $"\n            var _prev_{safeVar} = SpriteCollector.CurrentMethod; var _sw_{safeVar} = System.Diagnostics.Stopwatch.StartNew(); try {{";
                sb.Insert(braceOpen + 1, tryBlock);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Instruments .Add() calls on known sprite list variables to track which method added each sprite.
        /// For method parameter lists:  s.Add(x); → s.Add(x); SpriteCollector.RecordSpriteMethod(line);
        /// For local list variables:    s.Add(x); → s.Add(x); SpriteCollector.PreRecord(line);
        /// Local lists use PreRecord because their sprites are later batch-flushed to
        /// MySpriteDrawFrame.Add(), which calls RecordSpriteMethod(-1) and consumes the pre-records.
        /// </summary>
        private static string InstrumentAddCalls(string code, HashSet<string> spriteListNames, HashSet<string> localSpriteListNames, string originalCode)
        {
            // Combine both sets for regex matching
            var allNames = new HashSet<string>(StringComparer.Ordinal);
            if (spriteListNames != null)
                foreach (var n in spriteListNames) allNames.Add(n);
            if (localSpriteListNames != null)
                foreach (var n in localSpriteListNames) allNames.Add(n);

            if (allNames.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[InstrumentAddCalls] No sprite list names known, skipping");
                return code;
            }

            // Build a regex that matches ANY .Add( call on any known sprite list variable
            // Captures the variable name so we can distinguish param vs local
            var escapedNames = new List<string>();
            foreach (var n in allNames)
                escapedNames.Add(Regex.Escape(n));
            string namePattern = string.Join("|", escapedNames);
            var rxAdd = new Regex(
                @"\b(" + namePattern + @")\.Add\s*\(",
                RegexOptions.Singleline);

            // Pre-scan ORIGINAL code for .Add() positions to compute 1-based line numbers.
            // The Nth .Add() in the transformed code corresponds to the Nth .Add() in the
            // original code (transformations only INSERT code, never remove/reorder .Add() calls).
            var originalLineNumbers = new List<int>();
            if (!string.IsNullOrEmpty(originalCode))
            {
                foreach (Match origMatch in rxAdd.Matches(originalCode))
                {
                    int lineNum = 1;
                    for (int i = 0; i < origMatch.Index && i < originalCode.Length; i++)
                    {
                        if (originalCode[i] == '\n') lineNum++;
                    }
                    originalLineNumbers.Add(lineNum);
                }
                System.Diagnostics.Debug.WriteLine($"[InstrumentAddCalls] Pre-scanned {originalLineNumbers.Count} .Add() calls in original code");
            }

            var result = new StringBuilder(code.Length + 2000);
            int pos = 0;
            int instrumentedCount = 0;

            foreach (Match match in rxAdd.Matches(code))
            {
                // Find the opening paren of Add(
                int addOpenParen = code.IndexOf('(', match.Index);
                if (addOpenParen < 0) continue;

                int closeParen = FindMatchingParen(code, addOpenParen);
                if (closeParen < 0) continue;

                // Find the semicolon after the closing paren
                int semiColon = code.IndexOf(';', closeParen);
                if (semiColon < 0 || semiColon > closeParen + 10) continue; // Must be close

                // Append everything up to and including the semicolon
                result.Append(code, pos, semiColon + 1 - pos);

                // Look up the original line number for this Nth .Add() call
                int origLine = (instrumentedCount < originalLineNumbers.Count)
                    ? originalLineNumbers[instrumentedCount]
                    : -1;

                // Determine if this is a local list variable (batch-flush → PreRecord)
                // or a method parameter (direct tracking → RecordSpriteMethod)
                string varName = match.Groups[1].Value;
                bool isLocal = localSpriteListNames != null && localSpriteListNames.Contains(varName);

                if (isLocal)
                {
                    result.Append(" SpriteCollector.PreRecord(");
                    result.Append(origLine);
                    result.Append(");");
                }
                else
                {
                    result.Append(" SpriteCollector.RecordSpriteMethod(");
                    result.Append(origLine);
                    result.Append(");");
                }

                instrumentedCount++;
                pos = semiColon + 1;
            }

            System.Diagnostics.Debug.WriteLine($"[InstrumentAddCalls] Instrumented {instrumentedCount} Add() calls (params: {string.Join(", ", spriteListNames)}, locals: {string.Join(", ", localSpriteListNames ?? new HashSet<string>())})");

            // Append remainder of code
            result.Append(code, pos, code.Length - pos);
            return result.ToString();
        }

        /// <summary>
        /// Finds the matching closing parenthesis for an opening paren.
        /// </summary>
        private static int FindMatchingParen(string code, int openParenPos)
        {
            int depth = 0;
            for (int i = openParenPos; i < code.Length; i++)
            {
                if (code[i] == '(') depth++;
                else if (code[i] == ')')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
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
        /// Resolves C# preprocessor directives (<c>#if</c>/<c>#elif</c>/<c>#else</c>/<c>#endif</c>)
        /// by keeping <c>#if</c>/<c>#elif</c> branches and dropping <c>#else</c> branches.
        /// This effectively "defines all symbols", making the maximum amount of user code
        /// visible for detection and compilation.  Lines that are purely preprocessor
        /// directives are removed from the output.
        /// Returns the original string unchanged when no directives are present.
        /// </summary>
        private static string StripPreprocessorDirectives(string code)
        {
            if (code == null) return code;
            if (code.IndexOf('#') < 0) return code; // fast-path: no directives

            var sb = new StringBuilder(code.Length);
            var lines = code.Split('\n');
            // Stack tracks whether the current nesting level is "active" (content kept).
            // true  = keep lines  (inside #if / #elif that we accept)
            // false = drop lines  (inside #else)
            var activeStack = new Stack<bool>();

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();

                if (trimmed.StartsWith("#if ") || trimmed.StartsWith("#if\t"))
                {
                    // Enter a new #if block — keep the #if branch
                    activeStack.Push(true);
                    if (i < lines.Length - 1) sb.Append('\n'); // blank line preserves line numbering
                    continue;
                }

                if (trimmed.StartsWith("#elif ") || trimmed.StartsWith("#elif\t"))
                {
                    // Treat #elif same as another accepted branch — keep content
                    if (activeStack.Count > 0) activeStack.Pop();
                    activeStack.Push(true);
                    if (i < lines.Length - 1) sb.Append('\n');
                    continue;
                }

                if (trimmed.StartsWith("#else"))
                {
                    // Switch to the else branch — drop content
                    if (activeStack.Count > 0) activeStack.Pop();
                    activeStack.Push(false);
                    if (i < lines.Length - 1) sb.Append('\n');
                    continue;
                }

                if (trimmed.StartsWith("#endif"))
                {
                    // Exit the current #if block
                    if (activeStack.Count > 0) activeStack.Pop();
                    if (i < lines.Length - 1) sb.Append('\n');
                    continue;
                }

                // Keep the line only if all enclosing #if blocks are active
                bool active = true;
                foreach (bool a in activeStack)
                {
                    if (!a) { active = false; break; }
                }

                if (active)
                {
                    sb.Append(lines[i]);
                    if (i < lines.Length - 1) sb.Append('\n');
                }
                else
                {
                    // Emit blank line for inactive content to preserve line numbering
                    if (i < lines.Length - 1) sb.Append('\n');
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// If <paramref name="code"/> contains a class definition, extracts its body
        /// (the content between the outermost braces) so the methods can be injected
        /// directly into <c>LcdRunner</c>.  Sets <paramref name="hadWrapper"/> to true
        /// and <paramref name="className"/> to the class name when a wrapper was stripped.
        /// </summary>
        private static string StripClassWrapper(string code, out bool hadWrapper, out string className)
        {
            string baseClassName;
            return StripClassWrapper(code, out hadWrapper, out className, out baseClassName);
        }

        private static string StripClassWrapper(string code, out bool hadWrapper, out string className, out string baseClassName)
        {
            // Strip outermost namespace wrapper so top-level types are exposed
            string inner = StripOuterNamespace(code);

            string classPattern =
                @"(?:public|private|internal|protected)?\s*" +
                @"(?:static|sealed|abstract|partial)?\s*" +
                @"class\s+(\w+)[\w\s:,<>]*\{";
            var matches = Regex.Matches(inner, classPattern, RegexOptions.Singleline);

            if (matches.Count == 0)
            {
                hadWrapper = false;
                className = null;
                baseClassName = null;
                return inner;
            }

            // Find the class with the largest body
            Match bestMatch = null;
            int bestBodyStart = 0;
            int bestBodyEnd = 0;

            foreach (Match m in matches)
            {
                int bodyStart = m.Index + m.Length;
                int bodyEnd = ScanToMatchingBrace(inner, bodyStart);
                if (bestMatch == null || (bodyEnd - bodyStart) > (bestBodyEnd - bestBodyStart))
                {
                    bestMatch = m;
                    bestBodyStart = bodyStart;
                    bestBodyEnd = bodyEnd;
                }
            }

            className = bestMatch.Groups[1].Value;

            // Extract base class name from the inheritance list (e.g. ": MySessionComponentBase")
            string header = bestMatch.Value;
            var baseMatch = Regex.Match(header, @":\s*(\w+)");
            baseClassName = baseMatch.Success ? baseMatch.Groups[1].Value : null;

            hadWrapper = true;

            // Extract the main class body
            string body = inner.Substring(bestBodyStart, bestBodyEnd - 1 - bestBodyStart);

            // Preserve sibling type definitions (structs, enums, helper classes)
            // that appear outside the main class — they become nested types in LcdRunner
            string before = inner.Substring(0, bestMatch.Index).Trim();
            string after = inner.Substring(bestBodyEnd).Trim();

            // Strip class-level attributes (e.g. [MySessionComponentDescriptor(...)])
            // that trail at the end of 'before' — they target the original class and
            // would cause CS0592 when injected into the generated LcdRunner class body.
            before = StripTrailingAttributes(before);

            var result = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(before))
            {
                result.AppendLine(before);
                result.AppendLine();
            }
            result.Append(body);
            if (!string.IsNullOrWhiteSpace(after))
            {
                result.AppendLine();
                result.Append(after);
            }
            return result.ToString();
        }

        /// <summary>
        /// Strips the outermost <c>namespace Xxx { }</c> wrapper, returning the
        /// content between the braces.  Returns the original code when no namespace
        /// wrapper is found.
        /// </summary>
        private static string StripOuterNamespace(string code)
        {
            var nsMatch = Regex.Match(code, @"^\s*namespace\s+[\w.]+\s*\{", RegexOptions.Multiline);
            if (!nsMatch.Success) return code;

            int bodyStart = nsMatch.Index + nsMatch.Length;
            int bodyEnd = ScanToMatchingBrace(code, bodyStart);
            if (bodyEnd <= bodyStart) return code;

            return code.Substring(bodyStart, bodyEnd - 1 - bodyStart);
        }

        /// <summary>
        /// Removes trailing <c>[...]</c> attribute blocks from the end of a string.
        /// Used to strip class-level attributes that precede the main class declaration
        /// (e.g. <c>[MySessionComponentDescriptor(…)]</c>) so they don't leak into
        /// the generated <c>LcdRunner</c> body where they would be invalid.
        /// </summary>
        private static string StripTrailingAttributes(string text)
        {
            string trimmed = text.TrimEnd();
            while (trimmed.EndsWith("]"))
            {
                int close = trimmed.Length - 1;
                int depth = 0;
                int i = close;
                for (; i >= 0; i--)
                {
                    if (trimmed[i] == ']') depth++;
                    else if (trimmed[i] == '[')
                    {
                        depth--;
                        if (depth == 0) break;
                    }
                }
                if (i < 0 || depth != 0) break;
                trimmed = trimmed.Substring(0, i).TrimEnd();
            }
            return trimmed;
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

        /// <summary>
        /// Extracts the method name from a call expression like "RenderPanel(sprites, 512f, 10f, 1f)"
        /// or "sprites = BuildSprites(...)".
        /// </summary>
        private static string ExtractMethodNameFromCall(string callExpression)
        {
            if (string.IsNullOrWhiteSpace(callExpression)) return null;

            int parenPos = callExpression.IndexOf('(');
            if (parenPos <= 0) return null;

            string beforeParen = callExpression.Substring(0, parenPos).Trim();
            // Handle assignment expressions like "sprites = RenderMethod(...)"
            int eqPos = beforeParen.LastIndexOf('=');
            if (eqPos >= 0)
                beforeParen = beforeParen.Substring(eqPos + 1).Trim();

            return beforeParen;
        }

        /// <summary>
        /// Emits a call line, converting sprite assignment expressions to AddRange calls.
        /// This is needed because TrackedSpriteList can't be assigned from List&lt;MySprite&gt;.
        /// </summary>
        private static void EmitCallLine(StringBuilder sb, string cl, string indent)
        {
            string cleanCall = cl.TrimEnd(';', ' ');
            // Handle assignment expressions like "sprites = BuildSprites(...)"
            // Convert to AddRange since TrackedSpriteList can't be assigned from List<MySprite>
            if (cleanCall.TrimStart().StartsWith("sprites") && cleanCall.Contains("="))
            {
                int eqPos = cleanCall.IndexOf('=');
                string lhs = cleanCall.Substring(0, eqPos).Trim();
                if (lhs == "sprites")
                {
                    string rhs = cleanCall.Substring(eqPos + 1).Trim();
                    sb.AppendLine($"{indent}sprites.AddRange({rhs});");
                    return;
                }
            }
            sb.AppendLine(indent + cl);
        }
    }
}
