using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SESpriteLCDLayoutTool.Models;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Analyzes method bodies to extract sprite creation patterns for isolation matching.
    /// Uses static code analysis instead of runtime execution to identify which sprites
    /// belong to which method.
    /// </summary>
    public static class MethodBodyAnalyzer
    {
        /// <summary>
        /// Extracts sprite patterns from a method body by parsing s.Add() calls.
        /// Returns characteristics that can be used to match sprites during isolation.
        /// </summary>
        public static MethodSpritePattern AnalyzeMethod(string methodName, string methodBody)
        {
            var pattern = new MethodSpritePattern
            {
                MethodName = methodName,
                TextPatterns = new List<string>(),
                TexturePatterns = new List<string>(),
                HasLoopGeneratedSprites = false
            };

            if (string.IsNullOrWhiteSpace(methodBody))
                return pattern;

            // Pattern 1: MySprite.CreateText("literal", ...)
            var rxCreateText = new Regex(@"MySprite\.CreateText\s*\(\s*""([^""]+)""", RegexOptions.IgnoreCase);
            foreach (Match m in rxCreateText.Matches(methodBody))
            {
                string text = m.Groups[1].Value;
                pattern.TextPatterns.Add(text);
            }

            // Pattern 2: new MySprite(..., "TextureName", ...)
            var rxNewSprite = new Regex(@"new\s+MySprite\s*\([^,]+,\s*""([^""]+)""", RegexOptions.IgnoreCase);
            foreach (Match m in rxNewSprite.Matches(methodBody))
            {
                string textureName = m.Groups[1].Value;
                pattern.TexturePatterns.Add(textureName);
            }

            // Pattern 3: Check for loop-generated sprites (for, while, foreach)
            if (Regex.IsMatch(methodBody, @"\b(for|while|foreach)\s*\(", RegexOptions.IgnoreCase))
            {
                pattern.HasLoopGeneratedSprites = true;
            }

            return pattern;
        }

        /// <summary>
        /// Extracts all method bodies from user code and builds sprite patterns.
        /// </summary>
        public static Dictionary<string, MethodSpritePattern> AnalyzeAllMethods(string userCode)
        {
            var results = new Dictionary<string, MethodSpritePattern>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(userCode))
                return results;

            // Pattern: void MethodName(params) { body }
            // Also matches: List<MySprite> MethodName(params) { body }
            var rxMethod = new Regex(
                @"(?:void|List<MySprite>)\s+(\w+)\s*\([^)]*\)\s*\{",
                RegexOptions.Singleline);

            foreach (Match methodMatch in rxMethod.Matches(userCode))
            {
                string methodName = methodMatch.Groups[1].Value;
                int bodyStart = methodMatch.Index + methodMatch.Length - 1; // Include opening brace

                // Find matching closing brace
                int bodyEnd = FindMatchingBrace(userCode, bodyStart);
                if (bodyEnd <= bodyStart)
                    continue;

                string methodBody = userCode.Substring(bodyStart, bodyEnd - bodyStart + 1);
                var pattern = AnalyzeMethod(methodName, methodBody);

                // Only store methods that actually create sprites
                if (pattern.TextPatterns.Count > 0 || pattern.TexturePatterns.Count > 0)
                {
                    results[methodName] = pattern;
                    System.Diagnostics.Debug.WriteLine(
                        $"[MethodBodyAnalyzer] {methodName}: {pattern.TextPatterns.Count} text patterns, " +
                        $"{pattern.TexturePatterns.Count} texture patterns, loops={pattern.HasLoopGeneratedSprites}");
                }
            }

            return results;
        }

        /// <summary>
        /// Matches a sprite against method patterns to determine which method(s) could have created it.
        /// </summary>
        public static List<string> FindMatchingMethods(
            SpriteEntry sprite,
            Dictionary<string, MethodSpritePattern> methodPatterns)
        {
            var matches = new List<string>();

            foreach (var kvp in methodPatterns)
            {
                string methodName = kvp.Key;
                var pattern = kvp.Value;

                bool isMatch = false;

                // Check text sprites
                if (sprite.Type == SpriteEntryType.Text && !string.IsNullOrEmpty(sprite.Text))
                {
                    // Exact match
                    if (pattern.TextPatterns.Any(p => p.Equals(sprite.Text, StringComparison.Ordinal)))
                    {
                        isMatch = true;
                    }
                    // Partial match for dynamic text
                    else if (pattern.HasLoopGeneratedSprites &&
                             pattern.TextPatterns.Any(p => sprite.Text.Contains(p) || p.Contains(sprite.Text)))
                    {
                        isMatch = true;
                    }
                }

                // Check texture sprites
                if (sprite.Type == SpriteEntryType.Texture && !string.IsNullOrEmpty(sprite.SpriteName))
                {
                    if (pattern.TexturePatterns.Contains(sprite.SpriteName, StringComparer.OrdinalIgnoreCase))
                    {
                        isMatch = true;
                    }
                }

                if (isMatch)
                    matches.Add(methodName);
            }

            return matches;
        }

        private static int FindMatchingBrace(string code, int openBracePos)
        {
            if (openBracePos < 0 || openBracePos >= code.Length || code[openBracePos] != '{')
                return -1;

            int depth = 1;
            for (int i = openBracePos + 1; i < code.Length; i++)
            {
                char c = code[i];
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }
    }

    /// <summary>
    /// Represents sprite creation patterns extracted from a method body.
    /// </summary>
    public class MethodSpritePattern
    {
        /// <summary>Method name</summary>
        public string MethodName { get; set; }

        /// <summary>Text patterns from CreateText calls (e.g., "SIGNAL", "POWER")</summary>
        public List<string> TextPatterns { get; set; }

        /// <summary>Texture names from new MySprite calls (e.g., "Circle", "SquareSimple")</summary>
        public List<string> TexturePatterns { get; set; }

        /// <summary>Whether this method generates sprites in loops (harder to match exactly)</summary>
        public bool HasLoopGeneratedSprites { get; set; }
    }
}
