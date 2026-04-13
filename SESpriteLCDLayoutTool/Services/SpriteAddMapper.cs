using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Maps sprite Add() calls within render methods to their source line numbers.
    /// Simple and robust: parse methods, find s.Add calls, record line numbers.
    /// </summary>
    public static class SpriteAddMapper
    {
        /// <summary>
        /// Info about a single s.Add() call within a method.
        /// </summary>
        public class AddCallInfo
        {
            public int LineNumber { get; set; }        // Line of sprite CREATION (not Add call)
            public int CharPosition { get; set; }      // Char position of sprite CREATION
            public int AddLineNumber { get; set; }     // Line of the .Add() call (for reference)
            public int AddCharPosition { get; set; }   // Char position of the .Add() call in full code
            public string SpriteName { get; set; }     // "SquareSimple", "Circle", or text content
            public string VariableName { get; set; }   // Variable name if indirect (e.g., "lbl")
            public bool IsInLoop { get; set; }         // True if .Add() is inside a for/foreach/while/lambda
        }

        /// <summary>
        /// Builds a map of all render methods and their s.Add() calls with line numbers.
        /// Handles both List&lt;MySprite&gt; patterns (s.Add) and MySpriteDrawFrame patterns (frame.Add).
        /// </summary>
        /// <param name="code">The source code to parse</param>
        /// <returns>Dictionary mapping method name to list of AddCallInfo (in source order)</returns>
        public static Dictionary<string, List<AddCallInfo>> BuildAddCallMap(string code)
        {
            var result = new Dictionary<string, List<AddCallInfo>>();

            if (string.IsNullOrWhiteSpace(code))
                return result;

            // Split into lines for line number tracking
            var lines = code.Split('\n');

            // === PASS 1: Find methods with List<MySprite> / IEnumerable<MySprite> parameters (any position) ===
            var methodPattern = new Regex(
                @"(?:private|public|internal|protected|static|\s)+(?:void|List<MySprite>|IEnumerable<MySprite>)\s+(\w+)\s*\(([^)]*(?:List<MySprite>|IEnumerable<MySprite>)[^)]*)\)",
                RegexOptions.Singleline);

            foreach (Match methodMatch in methodPattern.Matches(code))
            {
                string methodName = methodMatch.Groups[1].Value;
                string allParams = methodMatch.Groups[2].Value;

                // Find the sprite list parameter name (could be at any position)
                string spriteListParam = null;
                foreach (string param in allParams.Split(','))
                {
                    string p = param.Trim();
                    if (p.Contains("List<MySprite>") || p.Contains("IEnumerable<MySprite>"))
                    {
                        string[] parts = p.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            spriteListParam = parts[parts.Length - 1];
                            break;
                        }
                    }
                }
                if (spriteListParam == null) continue;

                ParseMethodAddCalls(code, result, methodName, spriteListParam, methodMatch.Index);
            }

            // === PASS 2: Find methods with MySpriteDrawFrame parameters (frame.Add pattern) ===
            var frameMethodPattern = new Regex(
                @"(?:private|public|internal|protected|static|\s)+void\s+(\w+)\s*\(([^)]*MySpriteDrawFrame[^)]*)\)",
                RegexOptions.Singleline);

            foreach (Match methodMatch in frameMethodPattern.Matches(code))
            {
                string methodName = methodMatch.Groups[1].Value;
                if (result.ContainsKey(methodName)) continue; // Already mapped in pass 1

                string allParams = methodMatch.Groups[2].Value;

                // Find the frame parameter name
                string frameParam = null;
                foreach (string param in allParams.Split(','))
                {
                    string p = param.Trim();
                    if (p.Contains("MySpriteDrawFrame"))
                    {
                        string[] parts = p.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            frameParam = parts[parts.Length - 1];
                            break;
                        }
                    }
                }
                if (frameParam == null) continue;

                ParseMethodAddCalls(code, result, methodName, frameParam, methodMatch.Index);
            }

            // === PASS 3: Find methods with IMyTextSurface/IMyTextPanel parameters (surface.DrawFrame() → frame.Add pattern) ===
            var surfaceMethodPattern = new Regex(
                @"(?:private|public|internal|protected|static|\s)+void\s+(\w+)\s*\(([^)]*(?:IMyTextSurface|IMyTextPanel)[^)]*)\)",
                RegexOptions.Singleline);

            foreach (Match methodMatch in surfaceMethodPattern.Matches(code))
            {
                string methodName = methodMatch.Groups[1].Value;
                if (result.ContainsKey(methodName)) continue; // Already mapped

                // Find method body
                int bodyStart = code.IndexOf('{', methodMatch.Index);
                if (bodyStart < 0) continue;
                int bodyEnd = FindMatchingBrace(code, bodyStart);
                if (bodyEnd <= bodyStart) continue;
                string methodBody = code.Substring(bodyStart, bodyEnd - bodyStart + 1);

                // Find frame variable: var frame = surface.DrawFrame() or using (var frame = ...)
                var frameVarMatch = Regex.Match(methodBody, @"(?:var|MySpriteDrawFrame)\s+(\w+)\s*=\s*\w+\.DrawFrame\s*\(");
                if (!frameVarMatch.Success)
                {
                    // Also try: using (var frame = surface.DrawFrame())
                    frameVarMatch = Regex.Match(methodBody, @"using\s*\(\s*(?:var|MySpriteDrawFrame)\s+(\w+)\s*=\s*\w+\.DrawFrame\s*\(");
                }
                if (!frameVarMatch.Success) continue;

                string frameVarName = frameVarMatch.Groups[1].Value;
                ParseMethodAddCalls(code, result, methodName, frameVarName, methodMatch.Index);
            }

            // === PASS 4: Map switch/case blocks as virtual "Render{CaseName}" methods ===
            // Complex plugins (e.g. IML) render via switch(row.RowKind) { case Header: ... }
            // The executor creates virtual method names like "RenderHeader" for each case.
            // We map each case block's .Add() calls under that virtual name so code jumps work.
            MapSwitchCaseBlocks(code, result);

            return result;
        }

        /// <summary>
        /// Parses a method body for .Add() calls on a given variable and adds them to the result map.
        /// Shared logic used by all three method-detection passes.
        /// </summary>
        private static void ParseMethodAddCalls(string code, Dictionary<string, List<AddCallInfo>> result,
            string methodName, string addTargetVar, int methodMatchIndex)
        {
            int bodyStart = code.IndexOf('{', methodMatchIndex);
            if (bodyStart < 0) return;
            int bodyEnd = FindMatchingBrace(code, bodyStart);
            if (bodyEnd <= bodyStart) return;

            string methodBody = code.Substring(bodyStart, bodyEnd - bodyStart + 1);

            // Detect loop/lambda ranges in the method body for IsInLoop flagging
            var loopRanges = FindLoopRanges(methodBody);

            var addPattern = new Regex(
                Regex.Escape(addTargetVar) + @"\.Add\s*\(",
                RegexOptions.Singleline);

            var addCalls = new List<AddCallInfo>();

            foreach (Match addMatch in addPattern.Matches(methodBody))
            {
                int addPosInBody = addMatch.Index;
                int addPosInCode = bodyStart + addPosInBody;
                int addLineNumber = GetLineNumber(code, addPosInCode);

                int parenStart = addPosInBody + addMatch.Length - 1;
                string addContents = ExtractParenContents(methodBody, parenStart);

                int creationLineNumber = addLineNumber;
                int creationCharPos = addPosInCode;
                string variableName = null;
                string spriteName = null;

                bool isDirect = addContents != null && 
                    (addContents.TrimStart().StartsWith("new ") || 
                     addContents.TrimStart().StartsWith("MySprite."));

                if (isDirect)
                {
                    spriteName = ExtractSpriteName(methodBody, addPosInBody);
                }
                else if (addContents != null)
                {
                    variableName = addContents.Trim().TrimEnd(')');

                    var creationPos = FindVariableCreation(methodBody, variableName, addPosInBody);
                    if (creationPos >= 0)
                    {
                        creationCharPos = bodyStart + creationPos;
                        creationLineNumber = GetLineNumber(code, creationCharPos);
                        spriteName = ExtractSpriteName(methodBody, creationPos);
                    }
                }

                // Check if this Add() is inside a loop or lambda
                bool isInLoop = false;
                foreach (var range in loopRanges)
                {
                    if (addPosInBody >= range[0] && addPosInBody <= range[1])
                    {
                        isInLoop = true;
                        break;
                    }
                }

                addCalls.Add(new AddCallInfo
                {
                    LineNumber = creationLineNumber,
                    CharPosition = creationCharPos,
                    AddLineNumber = addLineNumber,
                    AddCharPosition = addPosInCode,
                    SpriteName = spriteName,
                    VariableName = variableName,
                    IsInLoop = isInLoop
                });
            }

            if (addCalls.Count > 0)
            {
                result[methodName] = addCalls;
                System.Diagnostics.Debug.WriteLine($"[SpriteAddMapper] {methodName}: {addCalls.Count} Add() calls (target: {addTargetVar})");
                for (int i = 0; i < addCalls.Count; i++)
                {
                    var call = addCalls[i];
                    string varInfo = call.VariableName != null ? $" (via {call.VariableName})" : " (direct)";
                    string loopTag = call.IsInLoop ? " [LOOP]" : "";
                            System.Diagnostics.Debug.WriteLine($"  [{i}] Creation line {call.LineNumber}, Add line {call.AddLineNumber}: {call.SpriteName ?? "(unknown)"}{varInfo}{loopTag}");
                            }
                        }
                    }

                    /// <summary>
                    /// Maps switch/case blocks as virtual "Render{CaseName}" entries in the Add call map.
                    /// This lets <see cref="CodeNavigationService"/> navigate to the correct case block
                    /// when the user clicks a sprite produced by a switch/case render pattern.
                    /// </summary>
                    private static void MapSwitchCaseBlocks(string code, Dictionary<string, List<AddCallInfo>> result)
                    {
                        // Find switch statements
                        var rxSwitch = new Regex(
                            @"switch\s*\(\s*\w+(?:\.\w+)?\s*\)\s*\{",
                            RegexOptions.Singleline);

                        foreach (Match switchMatch in rxSwitch.Matches(code))
                        {
                            int switchBodyStart = switchMatch.Index + switchMatch.Length;
                            int switchBodyEnd = FindMatchingBrace(code, switchMatch.Index + switchMatch.Length - 1);
                            if (switchBodyEnd <= switchBodyStart) continue;

                            // Find the sprite list variable used inside this switch.
                            // Look backwards from the switch for a List<MySprite> declaration.
                            string searchBefore = code.Substring(0, switchMatch.Index);
                            string addTarget = null;

                            // Pattern: var/List<MySprite> name = new List<MySprite>();
                            var listDeclMatches = Regex.Matches(searchBefore,
                                @"(?:var|List<MySprite>)\s+(\w+)\s*=\s*new\s+List<MySprite>",
                                RegexOptions.Singleline);
                            if (listDeclMatches.Count > 0)
                                addTarget = listDeclMatches[listDeclMatches.Count - 1].Groups[1].Value;

                            if (addTarget == null) continue;

                            // Extract case blocks
                            string switchBody = code.Substring(switchBodyStart, switchBodyEnd - switchBodyStart);
                            var rxCase = new Regex(
                                @"case\s+(?:[\w\.]+\.)?(\w+)\s*:",
                                RegexOptions.Singleline);

                            foreach (Match caseMatch in rxCase.Matches(switchBody))
                            {
                                string caseName = caseMatch.Groups[1].Value;
                                string virtualName = "Render" + caseName;

                                // Skip if already mapped (real method takes priority)
                                if (result.ContainsKey(virtualName)) continue;

                                // Find the extent of this case block (up to next case or end of switch)
                                int caseStart = caseMatch.Index + caseMatch.Length;
                                int caseEnd = switchBody.Length;

                                // Look for the next case/default label or end of switch
                                var nextCase = rxCase.Match(switchBody, caseStart);
                                if (nextCase.Success)
                                    caseEnd = nextCase.Index;
                                else
                                {
                                    // Check for default: label
                                    var defaultMatch = Regex.Match(switchBody.Substring(caseStart), @"\bdefault\s*:");
                                    if (defaultMatch.Success)
                                        caseEnd = caseStart + defaultMatch.Index;
                                }

                                string caseBody = switchBody.Substring(caseStart, caseEnd - caseStart);
                                int caseAbsoluteStart = switchBodyStart + caseStart;

                                // Find .Add() calls within this case block
                                var addPattern = new Regex(
                                    Regex.Escape(addTarget) + @"\.Add\s*\(",
                                    RegexOptions.Singleline);

                                var addCalls = new List<AddCallInfo>();
                                foreach (Match addMatch in addPattern.Matches(caseBody))
                                {
                                    int addPosInCode = caseAbsoluteStart + addMatch.Index;
                                    int addLineNumber = GetLineNumber(code, addPosInCode);

                                    int parenStart = addMatch.Index + addMatch.Length - 1;
                                    string addContents = ExtractParenContents(caseBody, parenStart);

                                    int creationLineNumber = addLineNumber;
                                    int creationCharPos = addPosInCode;
                                    string variableName = null;
                                    string spriteName = null;

                                    bool isDirect = addContents != null &&
                                        (addContents.TrimStart().StartsWith("new ") ||
                                         addContents.TrimStart().StartsWith("MySprite."));

                                    if (isDirect)
                                    {
                                        spriteName = ExtractSpriteName(caseBody, addMatch.Index);
                                    }
                                    else if (addContents != null)
                                    {
                                        variableName = addContents.Trim().TrimEnd(')');
                                        var creationPos = FindVariableCreation(caseBody, variableName, addMatch.Index);
                                        if (creationPos >= 0)
                                        {
                                            creationCharPos = caseAbsoluteStart + creationPos;
                                            creationLineNumber = GetLineNumber(code, creationCharPos);
                                            spriteName = ExtractSpriteName(caseBody, creationPos);
                                        }
                                    }

                                    addCalls.Add(new AddCallInfo
                                    {
                                        LineNumber = creationLineNumber,
                                        CharPosition = creationCharPos,
                                        AddLineNumber = addLineNumber,
                                        SpriteName = spriteName,
                                        VariableName = variableName
                                    });
                                }

                                if (addCalls.Count > 0)
                                {
                                    result[virtualName] = addCalls;
                                    System.Diagnostics.Debug.WriteLine($"[SpriteAddMapper] {virtualName} (case {caseName}): {addCalls.Count} Add() calls");
                                }
                            }
                        }
                    }

                    /// <summary>
                    /// Detects for/foreach/while loop bodies and lambda/delegate blocks in a method body.
                    /// Returns a list of [start, end] char position pairs (relative to methodBody start).
                    /// </summary>
                    private static List<int[]> FindLoopRanges(string methodBody)
                    {
                        var ranges = new List<int[]>();

                        // Match for/foreach/while loops
                        var loopRx = new Regex(@"\b(for|foreach|while)\s*\(", RegexOptions.Singleline);
                        foreach (Match m in loopRx.Matches(methodBody))
                        {
                            int parenOpen = methodBody.IndexOf('(', m.Index);
                            if (parenOpen < 0) continue;

                            // Find matching closing paren
                            int depth = 0;
                            int parenClose = -1;
                            for (int i = parenOpen; i < methodBody.Length; i++)
                            {
                                if (methodBody[i] == '(') depth++;
                                else if (methodBody[i] == ')')
                                {
                                    depth--;
                                    if (depth == 0) { parenClose = i; break; }
                                }
                            }
                            if (parenClose < 0) continue;

                            // Find loop body opening brace (within a few chars of closing paren)
                            int braceOpen = -1;
                            for (int i = parenClose + 1; i < Math.Min(parenClose + 30, methodBody.Length); i++)
                            {
                                if (methodBody[i] == '{') { braceOpen = i; break; }
                            }
                            if (braceOpen < 0) continue;

                            int braceClose = FindMatchingBrace(methodBody, braceOpen);
                            if (braceClose <= braceOpen) continue;

                            ranges.Add(new[] { braceOpen, braceClose });
                        }

                        // Also detect lambda bodies: (...) => { ... } and delegate { ... }
                        var lambdaRx = new Regex(@"(?:=>\s*\{|delegate\s*(?:\([^)]*\)\s*)?\{)", RegexOptions.Singleline);
                        foreach (Match m in lambdaRx.Matches(methodBody))
                        {
                            int braceOpen = methodBody.IndexOf('{', m.Index);
                            if (braceOpen < 0) continue;
                            int braceClose = FindMatchingBrace(methodBody, braceOpen);
                            if (braceClose <= braceOpen) continue;
                            ranges.Add(new[] { braceOpen, braceClose });
                        }

                        return ranges;
                    }

                                /// <summary>
                    /// Gets the line number (1-based) for a character position in the code.
                    /// </summary>
                    private static int GetLineNumber(string code, int charPosition)
        {
            int line = 1;
            for (int i = 0; i < charPosition && i < code.Length; i++)
            {
                if (code[i] == '\n') line++;
            }
            return line;
        }

        /// <summary>
        /// Finds the matching closing brace for an opening brace.
        /// </summary>
        private static int FindMatchingBrace(string code, int openBracePos)
        {
            int depth = 0;
            for (int i = openBracePos; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Extracts the contents inside parentheses starting at the given position.
        /// </summary>
        private static string ExtractParenContents(string code, int openParenPos)
        {
            if (openParenPos < 0 || openParenPos >= code.Length || code[openParenPos] != '(')
                return null;

            int depth = 0;
            int start = openParenPos + 1;
            for (int i = openParenPos; i < code.Length; i++)
            {
                if (code[i] == '(') depth++;
                else if (code[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                        return code.Substring(start, i - start);
                }
            }
            return null;
        }

        /// <summary>
        /// Searches backwards in method body for where a variable was created with MySprite.
        /// Returns the position of the creation, or -1 if not found.
        /// </summary>
        private static int FindVariableCreation(string methodBody, string variableName, int beforePos)
        {
            if (string.IsNullOrEmpty(variableName))
                return -1;

            // Look for patterns like:
            // var lbl = MySprite.CreateText(...)
            // var lbl = MySprite.CreateSprite(...)  
            // var lbl = new MySprite(...)
            // MySprite lbl = MySprite.CreateText(...)
            // MySprite lbl = new MySprite(...)

            string searchRegion = methodBody.Substring(0, Math.Min(beforePos, methodBody.Length));

            // Pattern: (var|MySprite) variableName = (MySprite.Create|new MySprite)
            var patterns = new[]
            {
                // var lbl = MySprite.Create
                $@"(?:var|MySprite)\s+{Regex.Escape(variableName)}\s*=\s*MySprite\.Create",
                // var lbl = new MySprite
                $@"(?:var|MySprite)\s+{Regex.Escape(variableName)}\s*=\s*new\s+MySprite",
            };

            int bestPos = -1;

            foreach (var pattern in patterns)
            {
                var matches = Regex.Matches(searchRegion, pattern, RegexOptions.Singleline);
                foreach (Match m in matches)
                {
                    // Take the LAST match before the Add call (closest declaration)
                    if (m.Index > bestPos)
                        bestPos = m.Index;
                }
            }

            return bestPos;
        }

        /// <summary>
        /// Tries to extract the sprite name from an Add() call.
        /// </summary>
        private static string ExtractSpriteName(string methodBody, int addPos)
        {
            // Look for the sprite type/name in the Add call
            // Pattern: new MySprite(SpriteType.TEXTURE, "SpriteName", ...) or
            //          new MySprite(SpriteType.TEXT, "Text content", ...)
            int searchEnd = Math.Min(addPos + 500, methodBody.Length);
            string searchRegion = methodBody.Substring(addPos, searchEnd - addPos);
            
            // Find first quoted string after SpriteType
            var nameMatch = Regex.Match(searchRegion, @"SpriteType\.\w+\s*,\s*""([^""]+)""");
            if (nameMatch.Success)
                return nameMatch.Groups[1].Value;

            // Try CreateText pattern
            var textMatch = Regex.Match(searchRegion, @"CreateText\s*\(\s*""([^""]+)""");
            if (textMatch.Success)
                return textMatch.Groups[1].Value;

            // Try CreateSprite pattern
            var spriteMatch = Regex.Match(searchRegion, @"CreateSprite\s*\(\s*""([^""]+)""");
            if (spriteMatch.Success)
                return spriteMatch.Groups[1].Value;

            return null;
        }

        /// <summary>
        /// Gets the line number for a sprite based on its method name and index.
        /// </summary>
        /// <param name="map">The Add call map from BuildAddCallMap</param>
        /// <param name="methodName">The method that created the sprite</param>
        /// <param name="methodIndex">The index of this sprite within the method (0-based)</param>
        /// <returns>Line number, or -1 if not found</returns>
        public static int GetLineNumber(Dictionary<string, List<AddCallInfo>> map, string methodName, int methodIndex)
        {
            if (map == null || string.IsNullOrEmpty(methodName) || methodIndex < 0)
                return -1;

            if (map.TryGetValue(methodName, out var addCalls) && methodIndex < addCalls.Count)
            {
                return addCalls[methodIndex].LineNumber;
            }

            return -1;
        }

        /// <summary>
        /// Gets the character position for a sprite based on its method name and index.
        /// </summary>
        public static int GetCharPosition(Dictionary<string, List<AddCallInfo>> map, string methodName, int methodIndex)
        {
            if (map == null || string.IsNullOrEmpty(methodName) || methodIndex < 0)
                return -1;

            if (map.TryGetValue(methodName, out var addCalls) && methodIndex < addCalls.Count)
            {
                return addCalls[methodIndex].CharPosition;
            }

            return -1;
        }
    }
}
