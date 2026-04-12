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
            public string SpriteName { get; set; }     // "SquareSimple", "Circle", or text content
            public string VariableName { get; set; }   // Variable name if indirect (e.g., "lbl")
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
                result[methodName] = addCalls;
                System.Diagnostics.Debug.WriteLine($"[SpriteAddMapper] {methodName}: {addCalls.Count} Add() calls (target: {addTargetVar})");
                for (int i = 0; i < addCalls.Count; i++)
                {
                    var call = addCalls[i];
                    string varInfo = call.VariableName != null ? $" (via {call.VariableName})" : " (direct)";
                    System.Diagnostics.Debug.WriteLine($"  [{i}] Creation line {call.LineNumber}, Add line {call.AddLineNumber}: {call.SpriteName ?? "(unknown)"}{varInfo}");
                }
            }
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
