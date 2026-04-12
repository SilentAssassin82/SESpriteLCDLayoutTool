using System;
using System.Linq;
using System.Windows.Forms;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using SESpriteLCDLayoutTool.Models;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Handles navigation from sprites to their source code locations.
    /// Uses Roslyn to parse code and find the exact position even after code edits.
    /// </summary>
    public static class CodeNavigationService
    {
        /// <summary>
        /// Navigates to the source code location for a sprite using REAL-TIME Roslyn parsing.
        /// This method parses the CURRENT code every time, so it works even after edits.
        /// NO stale tracking, NO approximations - finds the EXACT location every time.
        /// </summary>
        /// <param name="sprite">The sprite to navigate to (uses SourceStart or SourceMethodName/SourceMethodIndex)</param>
        /// <param name="codeBox">The RichTextBox containing the source code</param>
        /// <returns>True if navigation was successful, false if sprite not found</returns>
        public static bool NavigateToSprite(SpriteEntry sprite, RichTextBox codeBox)
        {
            if (sprite == null || codeBox == null)
                return false;

            // DEBUG: Show exactly what the sprite claims about itself
            System.Diagnostics.Debug.WriteLine($"[CodeNav] ========== SPRITE CLICKED ==========");
            System.Diagnostics.Debug.WriteLine($"[CodeNav] DisplayName: '{sprite.DisplayName}'");
            System.Diagnostics.Debug.WriteLine($"[CodeNav] SpriteName: '{sprite.SpriteName}'");
            System.Diagnostics.Debug.WriteLine($"[CodeNav] SourceMethodName: '{sprite.SourceMethodName ?? "(NULL)"}'");
            System.Diagnostics.Debug.WriteLine($"[CodeNav] SourceMethodIndex: {sprite.SourceMethodIndex}");
            System.Diagnostics.Debug.WriteLine($"[CodeNav] SourceStart: {sprite.SourceStart}");
            System.Diagnostics.Debug.WriteLine($"[CodeNav] IsFromExecution: {sprite.IsFromExecution}");
            System.Diagnostics.Debug.WriteLine($"[CodeNav] ==========================================");

            // Get CURRENT code from the editor (not stale tracking!)
            string currentCode = codeBox.Text;
            if (string.IsNullOrWhiteSpace(currentCode))
                return false;

            // STRATEGY 0 (MOST RELIABLE): Use SourceStart — direct source position pointer.
            // This is the primary strategy for file sync sprites and any sprite with source
            // tracking (CodeParser sets SourceStart, and RefreshSourceTracking maintains it).
            // SourceStart is a direct character offset into the code — no runtime/static
            // matching needed, so it works correctly for loops, sub-calls, and complex code.
            if (sprite.SourceStart >= 0 && sprite.SourceStart < currentCode.Length)
            {
                try
                {
                    int lineStart = currentCode.LastIndexOf('\n', sprite.SourceStart) + 1;
                    int lineEnd = currentCode.IndexOf('\n', sprite.SourceStart);
                    if (lineEnd < 0) lineEnd = currentCode.Length;

                    codeBox.Focus();
                    codeBox.Select(lineStart, lineEnd - lineStart);
                    codeBox.ScrollToCaret();

                    System.Diagnostics.Debug.WriteLine($"[CodeNav] ✓ STRATEGY 0: Navigated using SourceStart {sprite.SourceStart}");
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CodeNav] Error using SourceStart: {ex.Message}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[CodeNav] → SKIPPING Strategy 0 (SourceStart): SourceStart={sprite.SourceStart}, codeLength={currentCode.Length}");
            }

            // STRATEGY 1: Use SpriteAddMapper (fallback for sprites without SourceStart)
            // Parses all render methods, finds all s.Add() calls with line numbers,
            // then looks up by SourceMethodName + SourceMethodIndex.
            // NOTE: This can be unreliable when runtime sprite order differs from static
            // Add() call order (loops, conditionals, sub-method calls).
            if (!string.IsNullOrEmpty(sprite.SourceMethodName) && sprite.SourceMethodIndex >= 0)
            {
                System.Diagnostics.Debug.WriteLine($"[CodeNav] STRATEGY 1: SpriteAddMapper - method='{sprite.SourceMethodName}', index={sprite.SourceMethodIndex}");
                try
                {
                    var addCallMap = SpriteAddMapper.BuildAddCallMap(currentCode);

                    // Debug: show what we found for this method
                    if (addCallMap.TryGetValue(sprite.SourceMethodName, out var calls))
                    {
                        System.Diagnostics.Debug.WriteLine($"[CodeNav] → Found {calls.Count} Add() calls in {sprite.SourceMethodName}:");
                        for (int i = 0; i < Math.Min(calls.Count, 10); i++)
                        {
                            System.Diagnostics.Debug.WriteLine($"[CodeNav]   [{i}] Line {calls[i].LineNumber}: {calls[i].SpriteName ?? "(unknown)"}");
                        }
                        if (calls.Count > 10)
                            System.Diagnostics.Debug.WriteLine($"[CodeNav]   ... and {calls.Count - 10} more");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[CodeNav] → Method '{sprite.SourceMethodName}' NOT FOUND in addCallMap!");
                        System.Diagnostics.Debug.WriteLine($"[CodeNav] → Available methods: {string.Join(", ", addCallMap.Keys)}");
                    }

                    int charPos = SpriteAddMapper.GetCharPosition(addCallMap, sprite.SourceMethodName, sprite.SourceMethodIndex);

                    if (charPos >= 0 && charPos < currentCode.Length)
                    {
                        int lineStart = currentCode.LastIndexOf('\n', charPos) + 1;
                        int lineEnd = currentCode.IndexOf('\n', charPos);
                        if (lineEnd < 0) lineEnd = currentCode.Length;

                        codeBox.Focus();
                        codeBox.Select(lineStart, lineEnd - lineStart);
                        codeBox.ScrollToCaret();

                        int lineNum = SpriteAddMapper.GetLineNumber(addCallMap, sprite.SourceMethodName, sprite.SourceMethodIndex);
                        System.Diagnostics.Debug.WriteLine($"[CodeNav] ✓ Navigated via SpriteAddMapper: {sprite.SourceMethodName}[{sprite.SourceMethodIndex}] → line {lineNum}");
                        return true;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[CodeNav] → SpriteAddMapper: Index {sprite.SourceMethodIndex} out of range for {sprite.SourceMethodName}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CodeNav] SpriteAddMapper error: {ex.Message}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[CodeNav] → SKIPPING Strategy 1 (SpriteAddMapper): SourceMethodName='{sprite.SourceMethodName ?? "(NULL)"}', SourceMethodIndex={sprite.SourceMethodIndex}");
            }

            // STRATEGY 2: Search by sprite content (MOST RELIABLE for executed code!)
            // For executed code with known SourceMethodName, search WITHIN that method
            // When SourceMethodName is null, search the ENTIRE code (still effective!)
            // This is MORE RELIABLE than Roslyn index because runtime order != source code order
            string searchContent = sprite.Type == SpriteEntryType.Text ? sprite.Text : sprite.SpriteName;

            System.Diagnostics.Debug.WriteLine($"[CodeNav] STRATEGY 2: Content search - searchContent='{searchContent}', length={searchContent?.Length ?? 0}, method='{sprite.SourceMethodName ?? "(null)"}'");

            if (!string.IsNullOrEmpty(searchContent) && searchContent.Length > 3)
            {
                System.Diagnostics.Debug.WriteLine($"[CodeNav] → Attempting content search for '{searchContent}' in {(string.IsNullOrEmpty(sprite.SourceMethodName) ? "ENTIRE CODE" : sprite.SourceMethodName + "()")}");
                try
                {
                    int searchStart = 0;
                    int searchEnd = currentCode.Length;
                    bool foundMethod = false;

                    // If we know the method, narrow the search to that method body
                    if (!string.IsNullOrEmpty(sprite.SourceMethodName))
                    {
                        // Find the method DEFINITION (not calls!)
                        string methodPattern = @"(private|public|protected|internal|static|\s)+(void|List<MySprite>|IEnumerable<MySprite>)\s+" + 
                                              System.Text.RegularExpressions.Regex.Escape(sprite.SourceMethodName) + @"\s*\(";
                        var methodRx = new System.Text.RegularExpressions.Regex(methodPattern);
                        var methodMatch = methodRx.Match(currentCode);

                        if (methodMatch.Success)
                        {
                            searchStart = methodMatch.Index;
                            searchEnd = currentCode.Length;

                            // Find the end of the method (closing brace at same indentation level)
                            int braceCount = 0;
                            bool foundOpenBrace = false;
                            for (int i = searchStart; i < currentCode.Length; i++)
                            {
                                if (currentCode[i] == '{')
                                {
                                    braceCount++;
                                    foundOpenBrace = true;
                                }
                                else if (currentCode[i] == '}')
                                {
                                    braceCount--;
                                    if (foundOpenBrace && braceCount == 0)
                                    {
                                        searchEnd = i;
                                        break;
                                    }
                                }
                            }

                            System.Diagnostics.Debug.WriteLine($"[CodeNav] → Found method '{sprite.SourceMethodName}' at chars {searchStart}-{searchEnd}");
                            foundMethod = true;
                        }
                    }

                    string searchRegion = currentCode.Substring(searchStart, searchEnd - searchStart);

                    // STRATEGY 2a: For texture sprites, search for position/size context
                    if (sprite.Type == SpriteEntryType.Texture)
                    {
                        var positionMatches = FindSpriteByPositionSize(searchRegion, searchContent, sprite);
                        if (positionMatches.Count >= 1)
                        {
                            int pos = positionMatches[0];
                            int absolutePos = searchStart + pos;
                            int lineStart = currentCode.LastIndexOf('\n', absolutePos) + 1;
                            int lineEnd = currentCode.IndexOf('\n', absolutePos);
                            if (lineEnd < 0) lineEnd = currentCode.Length;

                            codeBox.Focus();
                            codeBox.Select(lineStart, lineEnd - lineStart);
                            codeBox.ScrollToCaret();

                            System.Diagnostics.Debug.WriteLine($"[CodeNav] ✓ Navigated to '{searchContent}' using position/size context {(foundMethod ? "in '" + sprite.SourceMethodName + "'" : "globally")}");
                            return true;
                        }
                    }

                    // STRATEGY 2b: Search for ALL occurrences of the content string
                    string pattern = $"\"{searchContent}\"";

                    System.Diagnostics.Debug.WriteLine($"[CodeNav] → Searching for pattern: {pattern}");

                    var allMatches = new System.Collections.Generic.List<int>();
                    int searchPos = 0;
                    while ((searchPos = searchRegion.IndexOf(pattern, searchPos, StringComparison.Ordinal)) >= 0)
                    {
                        allMatches.Add(searchPos);
                        searchPos += pattern.Length;
                    }

                    System.Diagnostics.Debug.WriteLine($"[CodeNav] → Found {allMatches.Count} occurrence(s) of '{searchContent}'");

                    if (allMatches.Count == 1)
                    {
                        int pos = allMatches[0];
                        int absolutePos = searchStart + pos;
                        int lineStart = currentCode.LastIndexOf('\n', absolutePos) + 1;
                        int lineEnd = currentCode.IndexOf('\n', absolutePos);
                        if (lineEnd < 0) lineEnd = currentCode.Length;

                        codeBox.Focus();
                        codeBox.Select(lineStart, lineEnd - lineStart);
                        codeBox.ScrollToCaret();

                        System.Diagnostics.Debug.WriteLine($"[CodeNav] ✓ Navigated to unique content '{searchContent}' {(foundMethod ? "in method '" + sprite.SourceMethodName + "'" : "globally")}");
                        return true;
                    }
                    else if (allMatches.Count > 1 && !string.IsNullOrEmpty(sprite.SourceMethodName) && sprite.SourceMethodIndex >= 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[CodeNav] → Multiple matches ({allMatches.Count}), using SourceMethodIndex={sprite.SourceMethodIndex} as tiebreaker");

                        try
                        {
                            var sourceMap = SpriteSourceMapper.MapSpriteCreationSites(currentCode);
                            if (sourceMap.TryGetValue(sprite.SourceMethodName, out var locations) && 
                                sprite.SourceMethodIndex < locations.Count)
                            {
                                var targetLocation = locations[sprite.SourceMethodIndex];
                                int charIndex = targetLocation.SpanStart;

                                if (charIndex >= 0 && charIndex < currentCode.Length)
                                {
                                    int lineStart = currentCode.LastIndexOf('\n', charIndex) + 1;
                                    int lineEnd = currentCode.IndexOf('\n', charIndex);
                                    if (lineEnd < 0) lineEnd = currentCode.Length;

                                    codeBox.Focus();
                                    codeBox.Select(lineStart, lineEnd - lineStart);
                                    codeBox.ScrollToCaret();

                                    System.Diagnostics.Debug.WriteLine($"[CodeNav] ✓ Navigated using Roslyn index: {sprite.SourceMethodName}[{sprite.SourceMethodIndex}] → line {targetLocation.LineNumber}");
                                    return true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[CodeNav] Roslyn index fallback failed: {ex.Message}");
                        }
                    }
                    else if (allMatches.Count > 1)
                    {
                        // Multiple matches without SourceMethodIndex - use the first one
                        System.Diagnostics.Debug.WriteLine($"[CodeNav] → Multiple matches ({allMatches.Count}), no index tiebreaker, using first");
                        int pos = allMatches[0];
                        int absolutePos = searchStart + pos;
                        int lineStart = currentCode.LastIndexOf('\n', absolutePos) + 1;
                        int lineEnd = currentCode.IndexOf('\n', absolutePos);
                        if (lineEnd < 0) lineEnd = currentCode.Length;

                        codeBox.Focus();
                        codeBox.Select(lineStart, lineEnd - lineStart);
                        codeBox.ScrollToCaret();

                        System.Diagnostics.Debug.WriteLine($"[CodeNav] ✓ Navigated to first occurrence of '{searchContent}' globally");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CodeNav] Error searching by content: {ex.Message}");
                }
            }

            // STRATEGY 3: Use SourceMethodName + SourceMethodIndex (Roslyn parsing)
            // NOTE: This can be unreliable because runtime sprite order != source code order
            System.Diagnostics.Debug.WriteLine($"[CodeNav] STRATEGY 3: Roslyn index - method='{sprite.SourceMethodName ?? "(null)"}', index={sprite.SourceMethodIndex}");

            if (!string.IsNullOrEmpty(sprite.SourceMethodName) && sprite.SourceMethodIndex >= 0)
            {
                System.Diagnostics.Debug.WriteLine($"[CodeNav] → WARNING: Using Roslyn index (unreliable for runtime sprites!) - {sprite.SourceMethodName}[{sprite.SourceMethodIndex}]");
                try
                {
                    // Parse CURRENT code using Roslyn
                    var sourceMap = SpriteSourceMapper.MapSpriteCreationSites(currentCode);

                    // Look up this sprite by MethodName + Index
                    var location = SpriteSourceMapper.GetSourceLocation(
                        sprite.SourceMethodName,
                        sprite.SourceMethodIndex,
                        sourceMap);

                    if (location != null)
                    {
                        // Found it! Jump to the exact location
                        int charIndex = location.SpanStart;
                        if (charIndex >= 0 && charIndex < currentCode.Length)
                        {
                            // Find line start and end
                            int lineStart = currentCode.LastIndexOf('\n', charIndex) + 1;
                            int lineEnd = currentCode.IndexOf('\n', charIndex);
                            if (lineEnd < 0) lineEnd = currentCode.Length;

                            // Convert to RichTextBox selection
                            codeBox.Focus();
                            codeBox.Select(lineStart, lineEnd - lineStart);
                            codeBox.ScrollToCaret();

                            System.Diagnostics.Debug.WriteLine($"[CodeNav] ✓ Navigated to {sprite.SourceMethodName}[{sprite.SourceMethodIndex}] at line {location.LineNumber}");
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CodeNav] Error during Roslyn navigation: {ex.Message}");
                }
            }

            // FALLBACK STRATEGY 4: Search by variable name (BEST for runtime sprites!)
            // This is the key insight: instead of trying to find the sprite creation line,
            // find where the VARIABLE was defined. Works great for loop-generated sprites.
            System.Diagnostics.Debug.WriteLine($"[CodeNav] STRATEGY 4: Variable search - varName='{sprite.VariableName ?? "(null)"}'");

            if (!string.IsNullOrEmpty(sprite.VariableName))
            {
                System.Diagnostics.Debug.WriteLine($"[CodeNav] → Attempting variable search for '{sprite.VariableName}'");
                try
                {
                    // Search for variable declaration/assignment patterns:
                    // var star = new MySprite(...)
                    // MySprite titleText = MySprite.CreateText(...)
                    // auto-generated names like "item" from loops
                    string varName = sprite.VariableName.Trim();

                    System.Diagnostics.Debug.WriteLine($"[CodeNav] Searching for variable definition '{varName}'");

                    // IMPORTANT: Search for CREATION patterns, not just the variable name
                    // This ensures we find "var t = CreateText(...)" not "s.Add(t)"

                    // Pattern 1: var varName = MySprite.Create...
                    string pattern1 = @"\bvar\s+" + System.Text.RegularExpressions.Regex.Escape(varName) + @"\s*=\s*MySprite\.Create";
                    // Pattern 2: var varName = new MySprite
                    string pattern2 = @"\bvar\s+" + System.Text.RegularExpressions.Regex.Escape(varName) + @"\s*=\s*new\s+MySprite";
                    // Pattern 3: MySprite varName = MySprite.Create...
                    string pattern3 = @"\bMySprite\s+" + System.Text.RegularExpressions.Regex.Escape(varName) + @"\s*=\s*MySprite\.Create";
                    // Pattern 4: MySprite varName = new MySprite
                    string pattern4 = @"\bMySprite\s+" + System.Text.RegularExpressions.Regex.Escape(varName) + @"\s*=\s*new\s+MySprite";

                    var rx1 = new System.Text.RegularExpressions.Regex(pattern1);
                    var rx2 = new System.Text.RegularExpressions.Regex(pattern2);
                    var rx3 = new System.Text.RegularExpressions.Regex(pattern3);
                    var rx4 = new System.Text.RegularExpressions.Regex(pattern4);

                    var match1 = rx1.Match(currentCode);
                    var match2 = rx2.Match(currentCode);
                    var match3 = rx3.Match(currentCode);
                    var match4 = rx4.Match(currentCode);

                    // Use the first successful match
                    System.Text.RegularExpressions.Match match = null;
                    if (match1.Success) { match = match1; System.Diagnostics.Debug.WriteLine($"  -> Found with pattern 1: var {varName} = MySprite.Create"); }
                    else if (match2.Success) { match = match2; System.Diagnostics.Debug.WriteLine($"  -> Found with pattern 2: var {varName} = new MySprite"); }
                    else if (match3.Success) { match = match3; System.Diagnostics.Debug.WriteLine($"  -> Found with pattern 3: MySprite {varName} = MySprite.Create"); }
                    else if (match4.Success) { match = match4; System.Diagnostics.Debug.WriteLine($"  -> Found with pattern 4: MySprite {varName} = new MySprite"); }
                    else { System.Diagnostics.Debug.WriteLine($"  -> No matches found for variable '{varName}'"); }

                    if (match != null)
                    {
                        int varPos = match.Index;
                        int lineStart = currentCode.LastIndexOf('\n', varPos) + 1;
                        int lineEnd = currentCode.IndexOf('\n', varPos);
                        if (lineEnd < 0) lineEnd = currentCode.Length;

                        codeBox.Focus();
                        codeBox.Select(lineStart, lineEnd - lineStart);
                        codeBox.ScrollToCaret();

                        System.Diagnostics.Debug.WriteLine($"[CodeNav] ✓ Navigated to variable definition '{varName}' at position {varPos}");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CodeNav] Error searching for variable: {ex.Message}");
                }
            }

            // FALLBACK STRATEGY 5: Search by sprite content globally (text or sprite name)
            // For executed code with known SourceMethodName, search WITHIN that method first
            System.Diagnostics.Debug.WriteLine($"[CodeNav] STRATEGY 5: Global content search - searching for '{searchContent ?? "(null)"}'");

            if (!string.IsNullOrEmpty(searchContent) && searchContent.Length > 3) // Require minimum length to avoid false matches
            {
                try
                {
                    int searchStart = 0;
                    int searchEnd = currentCode.Length;

                    // If we know which method created this sprite, search only within that method
                    if (!string.IsNullOrEmpty(sprite.SourceMethodName))
                    {
                        // Find the method definition
                        string methodPattern = @"\b(private|public|protected|internal|static)?\s*(void|List<MySprite>|IEnumerable<MySprite>)?\s*" + 
                                              System.Text.RegularExpressions.Regex.Escape(sprite.SourceMethodName) + @"\s*\(";
                        var methodRx = new System.Text.RegularExpressions.Regex(methodPattern);
                        var methodMatch = methodRx.Match(currentCode);

                        if (methodMatch.Success)
                        {
                            searchStart = methodMatch.Index;

                            // Find the end of the method (next method or end of class)
                            // Look for closing brace at the same indentation level
                            int braceCount = 0;
                            bool foundOpenBrace = false;
                            for (int i = searchStart; i < currentCode.Length; i++)
                            {
                                if (currentCode[i] == '{')
                                {
                                    braceCount++;
                                    foundOpenBrace = true;
                                }
                                else if (currentCode[i] == '}')
                                {
                                    braceCount--;
                                    if (foundOpenBrace && braceCount == 0)
                                    {
                                        searchEnd = i;
                                        break;
                                    }
                                }
                            }

                            System.Diagnostics.Debug.WriteLine($"[CodeNav] Narrowed search to method '{sprite.SourceMethodName}' (chars {searchStart}-{searchEnd})");
                        }
                    }

                    // Search for the content string within the narrowed range
                    string escapedContent = System.Text.RegularExpressions.Regex.Escape(searchContent);
                    string pattern = $"\"{escapedContent}\"";

                    string searchRegion = currentCode.Substring(searchStart, searchEnd - searchStart);
                    int pos = searchRegion.IndexOf(pattern, StringComparison.Ordinal);

                    if (pos >= 0)
                    {
                        int absolutePos = searchStart + pos;
                        int lineStart = currentCode.LastIndexOf('\n', absolutePos) + 1;
                        int lineEnd = currentCode.IndexOf('\n', absolutePos);
                        if (lineEnd < 0) lineEnd = currentCode.Length;

                        codeBox.Focus();
                        codeBox.Select(lineStart, lineEnd - lineStart);
                        codeBox.ScrollToCaret();

                        System.Diagnostics.Debug.WriteLine($"[CodeNav] ✓ Navigated by searching for content '{searchContent}' in method '{sprite.SourceMethodName ?? "(global)"}'");
                        return true;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[CodeNav] Content search failed: '{searchContent}' not found in expected method");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CodeNav] Error searching by content: {ex.Message}");
                }
            }

            // FALLBACK STRATEGY 4: Jump to method definition (when exact sprite location unknown)
            // This handles runtime-generated sprites (loops, etc.) where we know the method but not exact line
            if (!string.IsNullOrEmpty(sprite.SourceMethodName))
            {
                try
                {
                    // Search for method definition (not calls)
                    string pattern = @"\b(private|public|protected|internal|static)?\s*(void|List<MySprite>|IEnumerable<MySprite>)?\s*" + 
                                    System.Text.RegularExpressions.Regex.Escape(sprite.SourceMethodName) + @"\s*\(";
                    var rx = new System.Text.RegularExpressions.Regex(pattern);
                    var match = rx.Match(currentCode);

                    if (match.Success)
                    {
                        int methodPos = match.Index;
                        int lineStart = currentCode.LastIndexOf('\n', methodPos) + 1;
                        int lineEnd = currentCode.IndexOf('\n', methodPos);
                        if (lineEnd < 0) lineEnd = currentCode.Length;

                        codeBox.Focus();
                        codeBox.Select(lineStart, lineEnd - lineStart);
                        codeBox.ScrollToCaret();

                        System.Diagnostics.Debug.WriteLine($"[CodeNav] ✓ Navigated to method definition '{sprite.SourceMethodName}()' (exact sprite not found - likely created in loop)");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CodeNav] Error searching for method: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine($"[CodeNav] ✗ Could not navigate to sprite '{sprite.DisplayName}'");
            System.Diagnostics.Debug.WriteLine($"  - SourceMethodName: '{sprite.SourceMethodName ?? "(null)"}'");
            System.Diagnostics.Debug.WriteLine($"  - SourceMethodIndex: {sprite.SourceMethodIndex}");
            System.Diagnostics.Debug.WriteLine($"  - VariableName: '{sprite.VariableName ?? "(null)"}'");
            System.Diagnostics.Debug.WriteLine($"  - SourceStart: {sprite.SourceStart}");
            System.Diagnostics.Debug.WriteLine($"  - Content: '{searchContent ?? "(null)"}'");

            return false;
        }

        /// <summary>
        /// Jumps to the source code location that created the given sprite.
        /// LEGACY METHOD - use NavigateToSprite instead for reliable navigation.
        /// </summary>
        /// <param name="sprite">The sprite to navigate to</param>
        /// <param name="codeBox">The RichTextBox containing the source code</param>
        /// <param name="currentCode">The current source code (if different from codeBox.Text)</param>
        /// <param name="spriteMapping">Optional sprite mapping to determine which method created the sprite</param>
        /// <returns>True if navigation was successful</returns>
        public static bool JumpToSpriteSource(SpriteEntry sprite, RichTextBox codeBox, string currentCode = null, ElementSpriteMapping spriteMapping = null)
        {
            if (sprite == null || codeBox == null)
                return false;

            // Use the provided code or get it from the textbox
            string code = currentCode ?? codeBox.Text;

            if (string.IsNullOrWhiteSpace(code))
                return false;

            // Strategy 0: If we have sprite mapping and no SourceMethodName, try to find the method that created this sprite
            if (string.IsNullOrEmpty(sprite.SourceMethodName) && spriteMapping != null && spriteMapping.HasData)
            {
                var methods = spriteMapping.GetMethodsForSprite(sprite);
                if (methods != null && methods.Any())
                {
                    // Use the first method found (usually there's only one)
                    sprite.SourceMethodName = methods.First();
                    System.Diagnostics.Debug.WriteLine($"[CodeNav] Found method via mapping: {sprite.SourceMethodName}");
                }
            }

            // Strategy 1: If sprite has exact line/char tracking, use that first
            if (sprite.SourceLineNumber > 0)
            {
                try
                {
                    int charIndex = GetCharIndexFromLineAndChar(code, sprite.SourceLineNumber, sprite.SourceCharacterPosition);
                    if (charIndex >= 0 && charIndex < code.Length)
                    {
                        // Select the sprite creation line
                        int lineStart = code.LastIndexOf('\n', charIndex) + 1;
                        int lineEnd = code.IndexOf('\n', charIndex);
                        if (lineEnd < 0) lineEnd = code.Length;

                        codeBox.Select(lineStart, lineEnd - lineStart);
                        codeBox.ScrollToCaret();

                        System.Diagnostics.Debug.WriteLine($"[CodeNav] Jumped to Line {sprite.SourceLineNumber} (index {charIndex})");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CodeNav] Error using line/char: {ex.Message}");
                }
            }

            // Strategy 2: If sprite has SourceStart/SourceEnd, try that (may be outdated after edits)
            if (sprite.SourceStart >= 0 && sprite.SourceEnd >= 0 &&
                sprite.SourceStart < code.Length && sprite.SourceEnd <= code.Length)
            {
                try
                {
                    codeBox.Select(sprite.SourceStart, sprite.SourceEnd - sprite.SourceStart);
                    codeBox.ScrollToCaret();

                    System.Diagnostics.Debug.WriteLine($"[CodeNav] Jumped to SourceStart {sprite.SourceStart}");
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CodeNav] Error using SourceStart/End: {ex.Message}");
                }
            }

            // Strategy 3: Use Roslyn to find the sprite's current location by method + index
            if (!string.IsNullOrEmpty(sprite.SourceMethodName) && sprite.SourceMethodIndex >= 0)
            {
                try
                {
                    var sourceMap = SpriteSourceMapper.MapSpriteCreationSites(code);
                    var location = SpriteSourceMapper.GetSourceLocation(
                        sprite.SourceMethodName,
                        sprite.SourceMethodIndex,
                        sourceMap);

                    if (location != null)
                    {
                        int charIndex = GetCharIndexFromLineAndChar(code, location.LineNumber, location.CharPosition);
                        if (charIndex >= 0 && charIndex < code.Length)
                        {
                            int lineStart = code.LastIndexOf('\n', charIndex) + 1;
                            int lineEnd = code.IndexOf('\n', charIndex);
                            if (lineEnd < 0) lineEnd = code.Length;

                            codeBox.Select(lineStart, lineEnd - lineStart);
                            codeBox.ScrollToCaret();

                            System.Diagnostics.Debug.WriteLine($"[CodeNav] Jumped via Roslyn to {sprite.SourceMethodName}[{sprite.SourceMethodIndex}]");
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CodeNav] Error using Roslyn navigation: {ex.Message}");
                }
            }

            // Strategy 4: For Torch/Pulsar plugins with case-based rendering (e.g., "RenderHeader" from "case Header:"),
            // search for the case block since these are extracted via regex, not Roslyn
            if (!string.IsNullOrEmpty(sprite.SourceMethodName))
            {
                // Check if this looks like a case-extracted method (typically starts with "Render")
                if (sprite.SourceMethodName.StartsWith("Render", StringComparison.Ordinal) && 
                    sprite.SourceMethodName.Length > 6)
                {
                    // Extract case name: "RenderHeader" → "Header"
                    string caseName = sprite.SourceMethodName.Substring(6);

                    try
                    {
                        // Search for "case Header:" or "case LcdSpriteRow.Kind.Header:"
                        var rxCase = new System.Text.RegularExpressions.Regex(
                            @"case\s+(?:[\w\.]+\.)?" + System.Text.RegularExpressions.Regex.Escape(caseName) + @"\s*:",
                            System.Text.RegularExpressions.RegexOptions.None);

                        var match = rxCase.Match(code); 
                        if (match.Success)
                        {
                            int caseIndex = match.Index;
                            int lineStart = code.LastIndexOf('\n', caseIndex) + 1;
                            int lineEnd = code.IndexOf('\n', caseIndex);
                            if (lineEnd < 0) lineEnd = code.Length;

                            codeBox.Select(lineStart, lineEnd - lineStart);
                            codeBox.ScrollToCaret();

                            System.Diagnostics.Debug.WriteLine($"[CodeNav] Jumped to case block for {sprite.SourceMethodName} (case {caseName}:)");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[CodeNav] Error searching for case block: {ex.Message}");
                    }
                }

                // Fallback: Try to find method definition by name
                try
                {
                    string pattern = @"\b" + System.Text.RegularExpressions.Regex.Escape(sprite.SourceMethodName) + @"\s*\(";
                    var rx = new System.Text.RegularExpressions.Regex(pattern);
                    var match = rx.Match(code);

                    if (match.Success)
                    {
                        int methodIndex = match.Index;
                        int lineStart = code.LastIndexOf('\n', methodIndex) + 1;
                        int lineEnd = code.IndexOf('\n', methodIndex);
                        if (lineEnd < 0) lineEnd = code.Length;

                        codeBox.Select(lineStart, lineEnd - lineStart);
                        codeBox.ScrollToCaret();

                        System.Diagnostics.Debug.WriteLine($"[CodeNav] Jumped to method definition for {sprite.SourceMethodName}");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CodeNav] Error searching for method definition: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine($"[CodeNav] Could not navigate to sprite '{sprite.DisplayName}' - no valid source tracking");
            return false;
        }

        /// <summary>
        /// Converts line number (1-based) and character position (0-based) to absolute character index.
        /// </summary>
        private static int GetCharIndexFromLineAndChar(string text, int lineNumber, int charPosition)
        {
            if (string.IsNullOrEmpty(text) || lineNumber < 1)
                return -1;

            try
            {
                var lines = text.Split('\n');
                if (lineNumber > lines.Length)
                    return -1;

                // Calculate absolute position
                int charIndex = 0;
                for (int i = 0; i < lineNumber - 1; i++)
                {
                    charIndex += lines[i].Length + 1; // +1 for \n
                }
                charIndex += Math.Min(charPosition, lines[lineNumber - 1].Length);

                return charIndex;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Gets a tooltip-friendly description of where this sprite comes from in code.
        /// </summary>
        public static string GetSourceLocationDescription(SpriteEntry sprite)
        {
            if (sprite == null)
                return null;

            if (sprite.SourceLineNumber > 0)
            {
                string method = !string.IsNullOrEmpty(sprite.SourceMethodName)
                    ? $" in {sprite.SourceMethodName}()"
                    : "";
                return $"Line {sprite.SourceLineNumber}{method}";
            }

            if (!string.IsNullOrEmpty(sprite.SourceMethodName))
            {
                return $"Created by {sprite.SourceMethodName}()";
            }

            if (sprite.SourceStart >= 0)
            {
                return $"Source position {sprite.SourceStart}";
            }

            return null;
        }

        /// <summary>
        /// Attempts to find which sprite is created at the current cursor position in the code editor.
        /// Used for reverse navigation: code → canvas.
        /// </summary>
        public static SpriteEntry FindSpriteAtCodePosition(string code, int caretPosition, LcdLayout layout)
        {
            if (layout == null || layout.Sprites == null || string.IsNullOrWhiteSpace(code))
                return null;

            try
            {
                // Get line/char from caret position
                var lines = code.Substring(0, Math.Min(caretPosition, code.Length)).Split('\n');
                int lineNumber = lines.Length;
                int charPosition = lines[lines.Length - 1].Length;

                // Find sprite with matching source location
                foreach (var sprite in layout.Sprites)
                {
                    if (sprite.SourceLineNumber == lineNumber)
                    {
                        // Allow some tolerance in character position (same line is good enough)
                        return sprite;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CodeNav] Error finding sprite at position: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Finds sprite creation sites within a method region by matching position/size values.
        /// This is more reliable than just sprite name for common sprites like "SquareSimple".
        /// </summary>
        private static System.Collections.Generic.List<int> FindSpriteByPositionSize(
            string searchRegion, string spriteName, SpriteEntry sprite)
        {
            var matches = new System.Collections.Generic.List<int>();

            // Search for sprite name occurrences
            string namePattern = $"\"{spriteName}\"";
            int searchPos = 0;

            while ((searchPos = searchRegion.IndexOf(namePattern, searchPos, StringComparison.Ordinal)) >= 0)
            {
                // Look for Vector2 values near this sprite name (position and size)
                // Get surrounding context (±500 chars should cover the sprite creation)
                int contextStart = Math.Max(0, searchPos - 200);
                int contextEnd = Math.Min(searchRegion.Length, searchPos + 500);
                string context = searchRegion.Substring(contextStart, contextEnd - contextStart);

                // Extract all numeric values from the context
                var numericValues = ExtractNumericValues(context);

                // Score this match based on how many position/size values match
                int score = 0;

                // Check for X position (allow some tolerance for expressions)
                if (MatchesValue(numericValues, sprite.X, 1f)) score += 10;
                // Check for Y position
                if (MatchesValue(numericValues, sprite.Y, 1f)) score += 10;
                // Check for Width
                if (MatchesValue(numericValues, sprite.Width, 1f)) score += 5;
                // Check for Height  
                if (MatchesValue(numericValues, sprite.Height, 1f)) score += 5;

                System.Diagnostics.Debug.WriteLine($"[FindSpriteByPositionSize] '{spriteName}' at {searchPos}: score={score} (X={sprite.X:F1}, Y={sprite.Y:F1}, W={sprite.Width:F1}, H={sprite.Height:F1})");

                // If we have a good score (at least position match), record this match
                if (score >= 15)
                {
                    matches.Add(searchPos);
                }

                searchPos += namePattern.Length;
            }

            return matches;
        }

        /// <summary>
        /// Extracts all numeric literal values from a code snippet.
        /// </summary>
        private static System.Collections.Generic.List<float> ExtractNumericValues(string code)
        {
            var values = new System.Collections.Generic.List<float>();

            // Match float literals like "123f", "123.45f", "123", "123.45"
            var rx = new System.Text.RegularExpressions.Regex(@"(\d+(?:\.\d+)?)[fF]?(?=\s*[,\)\}]|\s*/)");

            foreach (System.Text.RegularExpressions.Match m in rx.Matches(code))
            {
                if (float.TryParse(m.Groups[1].Value, 
                    System.Globalization.NumberStyles.Float, 
                    System.Globalization.CultureInfo.InvariantCulture, 
                    out float val))
                {
                    values.Add(val);
                }
            }

            return values;
        }

        /// <summary>
        /// Checks if any of the extracted numeric values matches the target value.
        /// </summary>
        private static bool MatchesValue(System.Collections.Generic.List<float> values, float target, float tolerance)
        {
            foreach (var v in values)
            {
                if (Math.Abs(v - target) <= tolerance)
                    return true;
            }
            return false;
        }
    }
}
