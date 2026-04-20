using System;
using System.Linq;
using System.Windows.Forms;
using SESpriteLCDLayoutTool.Controls;
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
        // ══════════════════════════════════════════════════════════════════════════
        // ONE-TIME RESOLUTION: Bind every sprite's SourceStart at execution time.
        // Runs the Roslyn index + SpriteAddMapper ONCE, stores SourceStart on each
        // sprite so navigation becomes a trivial Strategy 0 lookup.
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Resolves <see cref="SpriteEntry.SourceStart"/> for every sprite in the list
        /// using the current source code. Builds the Roslyn navigation index and the
        /// SpriteAddMapper map ONCE, then sets SourceStart on each sprite that doesn't
        /// already have one.  Call this immediately after execution produces sprites.
        /// </summary>
        /// <param name="sourceCode">The source code that produced the sprites.</param>
        /// <param name="sprites">The sprites to resolve (modified in-place).</param>
        public static void ResolveSourceLocations(string sourceCode, System.Collections.Generic.List<Models.SpriteEntry> sprites)
        {
            if (string.IsNullOrWhiteSpace(sourceCode) || sprites == null || sprites.Count == 0)
                return;

            // Build both indexes once
            var navIndex = SpriteNavigationIndex.Build(sourceCode);
            var addCallMap = SpriteAddMapper.BuildAddCallMap(sourceCode);

            int resolved = 0, alreadySet = 0, unresolved = 0;

            System.Diagnostics.Debug.WriteLine($"[Resolve] ════════ ResolveSourceLocations: {sprites.Count} sprites ════════");

            for (int i = 0; i < sprites.Count; i++)
            {
                var sp = sprites[i];

                // Skip sprites that already have a valid SourceStart
                if (sp.SourceStart >= 0)
                {
                    alreadySet++;
                    System.Diagnostics.Debug.WriteLine($"[Resolve] [{i}] '{sp.DisplayName}' — ALREADY SET SourceStart={sp.SourceStart}");
                    continue;
                }

                int charOffset = -1;
                string strategy = "none";

                // Strategy A: Roslyn index lookup (unique/unambiguous text or sprite name)
                string lookupText = sp.Type == Models.SpriteEntryType.Text ? sp.Text : sp.SpriteName;
                if (!string.IsNullOrEmpty(lookupText))
                {
                    var hit = navIndex.Lookup(lookupText, sp.SourceMethodName, sp.SourceMethodIndex);
                    if (hit != null)
                    {
                        charOffset = hit.CharOffset;
                        strategy = $"A(Index) \"{lookupText}\" → line {hit.Line} [{hit.Kind}]";
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[Resolve] [{i}] Index lookup NULL for \"{lookupText}\" — falling to Strategy B");
                    }
                }

                // Strategy B: SpriteAddMapper (method + index — handles ambiguous textures)
                if (charOffset < 0 && !string.IsNullOrEmpty(sp.SourceMethodName) && sp.SourceMethodIndex >= 0)
                {
                    int mapperPos = SpriteAddMapper.GetCharPosition(addCallMap, sp.SourceMethodName, sp.SourceMethodIndex);
                    if (mapperPos >= 0)
                    {
                        charOffset = mapperPos;
                        int mapperLine = SpriteAddMapper.GetLineNumber(addCallMap, sp.SourceMethodName, sp.SourceMethodIndex);
                        strategy = $"B(Mapper) {sp.SourceMethodName}[{sp.SourceMethodIndex}] → line {mapperLine}";
                    }
                }

                if (charOffset >= 0 && charOffset < sourceCode.Length)
                {
                    sp.SourceStart = charOffset;
                    resolved++;

                    // VALIDATION: show what line we resolved to
                    int lineStart = sourceCode.LastIndexOf('\n', charOffset) + 1;
                    int lineEnd = sourceCode.IndexOf('\n', charOffset);
                    if (lineEnd < 0) lineEnd = sourceCode.Length;
                    string resolvedLine = sourceCode.Substring(lineStart, Math.Min(80, lineEnd - lineStart)).Trim();
                    System.Diagnostics.Debug.WriteLine($"[Resolve] [{i}] '{sp.DisplayName}' → {strategy} | Line: \"{resolvedLine}\"");
                }
                else
                {
                    unresolved++;
                    System.Diagnostics.Debug.WriteLine($"[Resolve] [{i}] '{sp.DisplayName}' — UNRESOLVED (text='{lookupText}', method='{sp.SourceMethodName}', idx={sp.SourceMethodIndex})");
                }
            }

            System.Diagnostics.Debug.WriteLine(
                $"[Resolve] Summary: {resolved} resolved, {alreadySet} already set, {unresolved} unresolved out of {sprites.Count} sprites");
        }

        /// <summary>
        /// Navigates to the source code location for a sprite using REAL-TIME Roslyn parsing.
        /// This method parses the CURRENT code every time, so it works even after edits.
        /// NO stale tracking, NO approximations - finds the EXACT location every time.
        /// </summary>
        /// <param name="sprite">The sprite to navigate to (uses SourceStart or SourceMethodName/SourceMethodIndex)</param>
        /// <param name="codeBox">The RichTextBox containing the source code</param>
        /// <returns>True if navigation was successful, false if sprite not found</returns>
        public static bool NavigateToSprite(SpriteEntry sprite, ScintillaCodeBox codeBox)
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
            System.Diagnostics.Debug.WriteLine($"[CodeNav] SourceLineNumber: {sprite.SourceLineNumber}");
            System.Diagnostics.Debug.WriteLine($"[CodeNav] SourceInvocationIndex: {sprite.SourceInvocationIndex}");
            System.Diagnostics.Debug.WriteLine($"[CodeNav] IsFromExecution: {sprite.IsFromExecution}");
            System.Diagnostics.Debug.WriteLine($"[CodeNav] ==========================================");

            // Get CURRENT code from the editor (not stale tracking!)
            string currentCode = codeBox.Text;
            if (string.IsNullOrWhiteSpace(currentCode))
                return false;

            // Fallback position from SpriteAddMapper if the target line doesn't contain
            // the sprite's text as a literal (e.g. text passed through a parameter).
            int mapperFallbackStart = -1;
            int mapperFallbackEnd   = -1;

            // ══════════════════════════════════════════════════════════════════════
            // STRATEGY 0 (MOST RELIABLE): DIRECT LINE NUMBER from instrumentation.
            // SourceLineNumber is set at instrumentation time for EVERY .Add() call
            // across ALL script types (PBs, Mods, Pulsar, Torch, LCD helpers).
            // It's a 1-based line number in the ORIGINAL user code — no post-hoc
            // matching, no Roslyn, no content comparison. Just: go to line N.
            // This is the primary strategy because it can't break when code structure
            // changes — it was computed from the code itself at compile time.
            // ══════════════════════════════════════════════════════════════════════
            if (sprite.SourceLineNumber > 0)
            {
                try
                {
                    int targetLine = sprite.SourceLineNumber; // 1-based
                    int lineStart = 0;
                    int currentLine = 1;
                    for (int i = 0; i < currentCode.Length; i++)
                    {
                        if (currentLine == targetLine)
                        {
                            lineStart = i;
                            break;
                        }
                        if (currentCode[i] == '\n')
                        {
                            currentLine++;
                            if (currentLine == targetLine)
                            {
                                lineStart = i + 1;
                                break;
                            }
                        }
                    }

                    if (currentLine == targetLine)
                    {
                        int lineEnd = currentCode.IndexOf('\n', lineStart);
                        if (lineEnd < 0) lineEnd = currentCode.Length;

                        // The SourceLineNumber points to the .Add() call, but in a WYSIWYG
                        // editor we need to land on the sprite CREATION line where the actual
                        // editable data lives (e.g. MySprite.CreateText / new MySprite).
                        // If the .Add() line itself has inline sprite data, use it as-is.
                        // Otherwise, extract the variable name and scan backwards to find
                        // where that variable was created/assigned.
                        string targetLineText = currentCode.Substring(lineStart, lineEnd - lineStart);

                        // VALIDATION: SourceLineNumber was recorded at the .Add() call during
                        // instrumentation. If the user has since edited the code (adding/removing
                        // lines), this line may no longer be an .Add() call. If the target line
                        // doesn't contain sprite-related content, the line number is stale —
                        // fall through to content-based strategies that parse the CURRENT code.
                        bool looksLikeSpriteCode =
                            targetLineText.IndexOf(".Add(", StringComparison.Ordinal) >= 0 ||
                            targetLineText.IndexOf("new MySprite", StringComparison.Ordinal) >= 0 ||
                            targetLineText.IndexOf("MySprite.Create", StringComparison.Ordinal) >= 0;

                        if (!looksLikeSpriteCode)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[CodeNav] → Strategy 0: Line {targetLine} doesn't contain sprite code (stale after edit?), falling through. Line: \"{targetLineText.Trim()}\"");
                        }
                        else
                        {
                            bool hasInlineData = targetLineText.IndexOf("new MySprite", StringComparison.Ordinal) >= 0 ||
                                                 targetLineText.IndexOf("CreateText", StringComparison.Ordinal) >= 0;

                            if (!hasInlineData)
                            {
                                // Extract variable name from .Add(varName)
                                var addVarRx = new System.Text.RegularExpressions.Regex(@"\.Add\s*\(\s*(\w+)\s*\)");
                                var addVarMatch = addVarRx.Match(targetLineText);

                                if (addVarMatch.Success)
                                {
                                    string varName = addVarMatch.Groups[1].Value;
                                    string varEscaped = System.Text.RegularExpressions.Regex.Escape(varName);

                                    // Scan backwards to find where this variable was created
                                    int scanPos = lineStart;
                                    for (int back = 0; back < 30 && scanPos > 0; back++)
                                    {
                                        if (scanPos < 2) break;
                                        int prevStart = currentCode.LastIndexOf('\n', scanPos - 2);
                                        prevStart = (prevStart < 0) ? 0 : prevStart + 1;
                                        string prevLine = currentCode.Substring(prevStart, scanPos - prevStart);

                                        // Match: "var varName =" or "MySprite varName =" or "varName = ..."
                                        bool isCreation =
                                            System.Text.RegularExpressions.Regex.IsMatch(prevLine,
                                                @"\bvar\s+" + varEscaped + @"\b") ||
                                            System.Text.RegularExpressions.Regex.IsMatch(prevLine,
                                                @"\b" + varEscaped + @"\s*=[^=]");

                                        if (isCreation)
                                        {
                                            lineStart = prevStart;
                                            lineEnd = currentCode.IndexOf('\n', lineStart);
                                            if (lineEnd < 0) lineEnd = currentCode.Length;
                                            System.Diagnostics.Debug.WriteLine(
                                                $"[CodeNav] → Adjusted from .Add({varName}) to creation line ({back + 1} lines back)");
                                            break;
                                        }
                                        scanPos = prevStart;
                                    }
                                }
                            }

                            // ── TEXT LITERAL REDIRECT ──────────────────────────────
                            // For loop/parameterized patterns (e.g. DrawGauge called
                            // in a loop, or a for-loop creating status items), the
                            // creation line is a generic template like:
                            //     var nt = CreateText(label, ...)
                            // where 'label' is a variable, not the actual text.
                            // In this case, find the nearest quoted literal matching
                            // the sprite's text and navigate THERE instead — that's
                            // where the user can see/edit the specific value.
                            string spriteTextForRedirect = sprite.Type == SpriteEntryType.Text ? sprite.Text : null;
                            if (!string.IsNullOrEmpty(spriteTextForRedirect) && spriteTextForRedirect.Length > 1)
                            {
                                string currentCreationLine = currentCode.Substring(lineStart, lineEnd - lineStart);
                                string quotedLiteral = "\"" + spriteTextForRedirect + "\"";

                                if (currentCreationLine.IndexOf(quotedLiteral, StringComparison.Ordinal) < 0)
                                {
                                    // Creation line doesn't contain the text as a literal.
                                    // Search for the nearest quoted occurrence in the code.
                                    int bestPos = -1;
                                    int bestDist = int.MaxValue;
                                    int searchIdx = 0;
                                    while ((searchIdx = currentCode.IndexOf(quotedLiteral, searchIdx, StringComparison.Ordinal)) >= 0)
                                    {
                                        int dist = Math.Abs(searchIdx - lineStart);
                                        if (dist < bestDist)
                                        {
                                            bestDist = dist;
                                            bestPos = searchIdx;
                                        }
                                        searchIdx += quotedLiteral.Length;
                                    }

                                    if (bestPos >= 0)
                                    {
                                        lineStart = currentCode.LastIndexOf('\n', bestPos) + 1;
                                        lineEnd = currentCode.IndexOf('\n', bestPos);
                                        if (lineEnd < 0) lineEnd = currentCode.Length;
                                        System.Diagnostics.Debug.WriteLine(
                                            $"[CodeNav] → Redirected to text literal \"{spriteTextForRedirect}\" (nearest match, distance={bestDist} chars)");
                                    }
                                }
                            }

                            codeBox.Focus();
                            codeBox.Select(lineStart, lineEnd - lineStart);
                            codeBox.ScrollToCaret();

                            System.Diagnostics.Debug.WriteLine($"[CodeNav] ✓ STRATEGY 0 (LineNumber): Navigated using SourceLineNumber {sprite.SourceLineNumber}");
                            return true;
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[CodeNav] → SourceLineNumber {sprite.SourceLineNumber} exceeds code line count ({currentLine})");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CodeNav] Error using SourceLineNumber: {ex.Message}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[CodeNav] → SKIPPING Strategy 0 (SourceLineNumber): SourceLineNumber={sprite.SourceLineNumber}");
            }

            // STRATEGY 0a: Use SourceStart — direct character offset from source tracking.
            // This is the primary fallback for file-synced sprites and any sprite with source
            // tracking from CodeParser (SourceStart is set, RefreshSourceTracking maintains it).
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

                    System.Diagnostics.Debug.WriteLine($"[CodeNav] ✓ STRATEGY 0a (SourceStart): Navigated using SourceStart {sprite.SourceStart}");
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CodeNav] Error using SourceStart: {ex.Message}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[CodeNav] → SKIPPING Strategy 0a (SourceStart): SourceStart={sprite.SourceStart}, codeLength={currentCode.Length}");
            }

            // ──────────────────────────────────────────────────────────────────
            // STRATEGY 0a: CALL-SITE NAVIGATION via TEXT LITERAL MATCHING
            // When a method (e.g. DrawGauge) is called multiple times, all
            // sprites from it share the same SourceLineNumber (the .Add() line
            // inside the method body).  We find the CALL SITE that contains the
            // sprite's text as a quoted argument — e.g. DrawGauge(s, "POWER", ...)
            // This is more reliable than invocation counting because source order
            // may differ from execution order.
            // Priority: 1) unique text match in call args → navigate
            //           2) multiple text matches → use invocation index
            //           3) no text match → use invocation index
            //           4) nothing works → fall through to 0b
            // ──────────────────────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(sprite.SourceMethodName))
            {
                try
                {
                    // Find ALL call sites for this method in the source code.
                    string methodEscaped = System.Text.RegularExpressions.Regex.Escape(sprite.SourceMethodName);
                    var callSiteRx = new System.Text.RegularExpressions.Regex(
                        @"(?<!\w)" + methodEscaped + @"\s*\(");

                    // Pattern for method DEFINITION to exclude it
                    var defRx = new System.Text.RegularExpressions.Regex(
                        @"(void|List<MySprite>|IEnumerable<MySprite>|string\[\]\[\]|string|int|float|double|bool|var)\s+" +
                        methodEscaped + @"\s*\(");

                    var callSites = new System.Collections.Generic.List<int>();
                    var match = callSiteRx.Match(currentCode);
                    while (match.Success)
                    {
                        int lineStart0a = currentCode.LastIndexOf('\n', Math.Max(0, match.Index - 1)) + 1;
                        string linePrefix = currentCode.Substring(lineStart0a, match.Index - lineStart0a);
                        string fullLine = linePrefix + match.Value;

                        // Skip matches inside comments or string literals
                        string trimmedPrefix = linePrefix.TrimStart();
                        bool inComment = trimmedPrefix.StartsWith("//") ||
                                         trimmedPrefix.StartsWith("*") ||
                                         trimmedPrefix.StartsWith("/*");
                        bool inString = linePrefix.IndexOf('"') >= 0 &&
                                        linePrefix.IndexOf('"') < linePrefix.Length &&
                                        (linePrefix.Length - linePrefix.Replace("\"", "").Length) % 2 == 1;

                        if (!inComment && !inString && !defRx.IsMatch(fullLine))
                        {
                            callSites.Add(match.Index);
                        }
                        match = match.NextMatch();
                    }

                    System.Diagnostics.Debug.WriteLine(
                        $"[CodeNav] Strategy 0a: Found {callSites.Count} call site(s) for '{sprite.SourceMethodName}', invocation={sprite.SourceInvocationIndex}");

                    if (callSites.Count > 1)
                    {
                        // Determine the sprite's text for argument matching
                        string spriteText0a = sprite.Type == SpriteEntryType.Text ? sprite.Text : null;
                        string quotedText = !string.IsNullOrEmpty(spriteText0a) && spriteText0a.Length > 1
                            ? "\"" + spriteText0a + "\""
                            : null;

                        // --- Phase 1: Search each call site's argument expression for the sprite text literal ---
                        int textMatchIdx = -1;
                        int textMatchCount = 0;
                        if (quotedText != null)
                        {
                            for (int c = 0; c < callSites.Count; c++)
                            {
                                // Extract the call expression: from call site to next ';' (capped at 500 chars)
                                int exprStart = callSites[c];
                                int semiPos = currentCode.IndexOf(';', exprStart);
                                if (semiPos < 0 || semiPos - exprStart > 500)
                                    semiPos = Math.Min(exprStart + 500, currentCode.Length);
                                string callExpr = currentCode.Substring(exprStart, semiPos - exprStart);

                                if (callExpr.IndexOf(quotedText, StringComparison.Ordinal) >= 0)
                                {
                                    textMatchIdx = c;
                                    textMatchCount++;
                                    System.Diagnostics.Debug.WriteLine(
                                        $"[CodeNav] Strategy 0a: Call site #{c} contains {quotedText}");
                                }
                            }
                        }

                        int navigateToCallSite = -1;
                        string navReason = null;

                        if (textMatchCount == 1)
                        {
                            // BEST CASE: Unique text match — most reliable navigation
                            navigateToCallSite = textMatchIdx;
                            navReason = $"unique text match {quotedText} at call site #{textMatchIdx}";
                        }
                        else if (textMatchCount > 1 && sprite.SourceInvocationIndex >= 0)
                        {
                            // Multiple text matches — use invocation index among the text-matched sites
                            // Collect the indices of text-matched call sites
                            var textMatched = new System.Collections.Generic.List<int>();
                            for (int c = 0; c < callSites.Count; c++)
                            {
                                int exprStart = callSites[c];
                                int semiPos = currentCode.IndexOf(';', exprStart);
                                if (semiPos < 0 || semiPos - exprStart > 500)
                                    semiPos = Math.Min(exprStart + 500, currentCode.Length);
                                string callExpr = currentCode.Substring(exprStart, semiPos - exprStart);
                                if (callExpr.IndexOf(quotedText, StringComparison.Ordinal) >= 0)
                                    textMatched.Add(c);
                            }
                            // Use invocation index within the text-matched subset
                            if (sprite.SourceInvocationIndex < textMatched.Count)
                            {
                                navigateToCallSite = textMatched[sprite.SourceInvocationIndex];
                                navReason = $"text match {quotedText} + invocation #{sprite.SourceInvocationIndex} among {textMatched.Count} text matches";
                            }
                            else if (sprite.SourceInvocationIndex < callSites.Count)
                            {
                                // Fallback: invocation index against all call sites
                                navigateToCallSite = sprite.SourceInvocationIndex;
                                navReason = $"invocation #{sprite.SourceInvocationIndex} (text matched {textMatched.Count} but idx out of range)";
                            }
                        }
                        else if (textMatchCount == 0 && sprite.SourceInvocationIndex >= 0 &&
                                 callSites.Count > 1 && sprite.SourceInvocationIndex < callSites.Count)
                        {
                            // No text match (texture sprite or computed text) — use invocation index
                            navigateToCallSite = sprite.SourceInvocationIndex;
                            navReason = $"invocation #{sprite.SourceInvocationIndex} (no text match, {callSites.Count} call sites)";
                        }

                        if (navigateToCallSite >= 0 && navigateToCallSite < callSites.Count)
                        {
                            int callPos = callSites[navigateToCallSite];
                            int lineStart = currentCode.LastIndexOf('\n', callPos) + 1;
                            int lineEnd = currentCode.IndexOf('\n', callPos);
                            if (lineEnd < 0) lineEnd = currentCode.Length;

                            codeBox.Focus();
                            codeBox.Select(lineStart, lineEnd - lineStart);
                            codeBox.ScrollToCaret();

                            System.Diagnostics.Debug.WriteLine(
                                $"[CodeNav] ✓ STRATEGY 0a: Navigated to call site #{navigateToCallSite} of '{sprite.SourceMethodName}' — {navReason}");
                            return true;
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[CodeNav] → Strategy 0a: Could not resolve call site (textMatches={textMatchCount}, invocation={sprite.SourceInvocationIndex}, sites={callSites.Count}) — falling through");
                        }
                    }
                    else
                    {
                        // 0 or 1 call site — nothing to disambiguate, fall through
                        System.Diagnostics.Debug.WriteLine(
                            $"[CodeNav] → Strategy 0b (call-site): {callSites.Count} call site(s) for '{sprite.SourceMethodName}' — falling through to content strategies");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CodeNav] Strategy 0a error: {ex.Message}");
                }
            }

            // ──────────────────────────────────────────────────────────────────
            // STRATEGY 1: ROSLYN NAVIGATION INDEX (fallback for executed code)
            // Builds a complete map of every string literal in the source,
            // detects helper-method parameter flow (DrawGauge → CreateText),
            // and resolves the best source line via dictionary lookup.
            // This replaces the old regex-based strategies for text sprites.
            // ──────────────────────────────────────────────────────────────────
            string lookupText = sprite.Type == SpriteEntryType.Text ? sprite.Text : sprite.SpriteName;
            if (!string.IsNullOrEmpty(lookupText))
            {
                try
                {
                    var navIndex = SpriteNavigationIndex.Build(currentCode);

                    // Dump the full index to Debug output for transparency
                    System.Diagnostics.Debug.WriteLine(navIndex.Dump());

                    var hit = navIndex.Lookup(lookupText, sprite.SourceMethodName, sprite.SourceMethodIndex);
                    if (hit != null)
                    {
                        int charIdx = hit.CharOffset;
                        if (charIdx >= 0 && charIdx < currentCode.Length)
                        {
                            int lineStart = currentCode.LastIndexOf('\n', charIdx) + 1;
                            int lineEnd = currentCode.IndexOf('\n', charIdx);
                            if (lineEnd < 0) lineEnd = currentCode.Length;

                            // For [Generic] entries, validate the target line is a sprite creation site.
                            // Generic entries like array initializers (string[] dirs = { "NORTH", ... })
                            // are NOT sprite creation lines — navigating there is misleading.
                            string indexTargetLine = currentCode.Substring(lineStart, lineEnd - lineStart);
                            bool isSpriteCreationLine = hit.Kind != SpriteNavigationIndex.EntryKind.Generic ||
                                indexTargetLine.IndexOf(".Add(", StringComparison.Ordinal) >= 0 ||
                                indexTargetLine.IndexOf("new MySprite", StringComparison.Ordinal) >= 0 ||
                                indexTargetLine.IndexOf("CreateText", StringComparison.Ordinal) >= 0 ||
                                indexTargetLine.IndexOf("CreateSprite", StringComparison.Ordinal) >= 0 ||
                                indexTargetLine.IndexOf("SpriteType.", StringComparison.Ordinal) >= 0;

                            if (isSpriteCreationLine)
                            {
                                codeBox.Focus();
                                codeBox.Select(lineStart, lineEnd - lineStart);
                                codeBox.ScrollToCaret();

                                System.Diagnostics.Debug.WriteLine(
                                    $"[CodeNav] ✓ STRATEGY 1 (Index): \"{lookupText}\" → line {hit.Line} [{hit.Kind}] in {hit.Method ?? "top-level"}");
                                return true;
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"[CodeNav] → Strategy 1 (Index): \"{lookupText}\" → line {hit.Line} [{hit.Kind}] — NOT a sprite creation line, falling through");
                            }
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[CodeNav] → Index lookup returned null for \"{lookupText}\"");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CodeNav] Index error: {ex.Message}");
                }
            }

            // ──────────────────────────────────────────────────────────────────
            // STRATEGY 2: SpriteAddMapper (fallback for sprites without SourceStart)
            // Parses all render methods, finds all s.Add() calls with line numbers,
            // then looks up by SourceMethodName + SourceMethodIndex.
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

                        // Check: does the target line contain the sprite's text as a quoted literal?
                        // If not, the text is passed through a variable (e.g. DrawGauge(s, "POWER", ...) 
                        // calls CreateText(label, ...) — the Add call is inside the helper method where
                        // "label" is a variable, not the "POWER" literal).  Save as fallback and let
                        // Strategy 2 find the actual literal first.
                        string targetLine = currentCode.Substring(lineStart, lineEnd - lineStart);
                        string spriteText = sprite.Type == SpriteEntryType.Text ? sprite.Text : null;
                        bool lineHasLiteral = string.IsNullOrEmpty(spriteText) ||
                                              targetLine.IndexOf("\"" + spriteText + "\"", StringComparison.Ordinal) >= 0;

                        if (lineHasLiteral)
                        {
                            codeBox.Focus();
                            codeBox.Select(lineStart, lineEnd - lineStart);
                            codeBox.ScrollToCaret();

                            int lineNum = SpriteAddMapper.GetLineNumber(addCallMap, sprite.SourceMethodName, sprite.SourceMethodIndex);
                            System.Diagnostics.Debug.WriteLine($"[CodeNav] ✓ Navigated via SpriteAddMapper: {sprite.SourceMethodName}[{sprite.SourceMethodIndex}] → line {lineNum}");
                            return true;
                        }
                        else
                        {
                            // Save as fallback — later strategies may find the literal at the call site
                            mapperFallbackStart = lineStart;
                            mapperFallbackEnd   = lineEnd;
                            System.Diagnostics.Debug.WriteLine($"[CodeNav] → SpriteAddMapper target line lacks literal \"{spriteText}\" — deferring (saved fallback)");
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[CodeNav] → SpriteAddMapper: Index {sprite.SourceMethodIndex} out of range for {sprite.SourceMethodName}");

                        // ── LOOP-AWARE FALLBACK ──────────────────────────────────────
                        // When runtime index exceeds source Add() count, the sprite was
                        // produced by a loop/lambda. Find the sprite's text (or a prefix)
                        // in the method body and navigate to the nearest in-loop source
                        // Add() call.
                        if (calls != null && calls.Count > 0)
                        {
                            string loopSpriteText = sprite.Type == SpriteEntryType.Text ? sprite.Text : sprite.SpriteName;
                            if (!string.IsNullOrEmpty(loopSpriteText))
                            {
                                int loopNavResult = NavigateToNearestLoopAdd(
                                    currentCode, sprite.SourceMethodName, loopSpriteText, calls, codeBox);
                                if (loopNavResult > 0)
                                    return true;
                            }
                        }
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

            if (!string.IsNullOrEmpty(searchContent))
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
                        else if (sprite.SourceMethodName.StartsWith("Render"))
                        {
                            // Virtual switch/case method name (e.g. "RenderHeader" from "case Header:")
                            // No real method definition exists — search for the case block instead
                            string caseName = sprite.SourceMethodName.Substring("Render".Length);
                            string casePattern = @"case\s+(?:[\w\.]+\.)?(" + System.Text.RegularExpressions.Regex.Escape(caseName) + @")\s*:";
                            var caseRx = new System.Text.RegularExpressions.Regex(casePattern);
                            var caseMatch = caseRx.Match(currentCode);

                            if (caseMatch.Success)
                            {
                                searchStart = caseMatch.Index;
                                searchEnd = currentCode.Length;

                                // Find end of this case block: next case/default label or closing brace
                                var nextCaseRx = new System.Text.RegularExpressions.Regex(@"\bcase\s+[\w\.]+\s*:|default\s*:|^\s*\}", System.Text.RegularExpressions.RegexOptions.Multiline);
                                var nextCaseMatch = nextCaseRx.Match(currentCode, caseMatch.Index + caseMatch.Length);
                                if (nextCaseMatch.Success)
                                    searchEnd = nextCaseMatch.Index;

                                System.Diagnostics.Debug.WriteLine($"[CodeNav] → Found case block '{caseName}' at chars {searchStart}-{searchEnd}");
                                foundMethod = true;
                            }
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

                    // If no exact match found, try prefix matching for computed/interpolated strings
                    // e.g., "WIND: N/A" → search for $"WIND: or "WIND: as a partial match
                    if (allMatches.Count == 0 && searchContent.Length > 3 && foundMethod)
                    {
                        foreach (char sep in new[] { ':', ' ', '-', '(' })
                        {
                            int sepIdx = searchContent.IndexOf(sep);
                            if (sepIdx >= 2)
                            {
                                string prefix = searchContent.Substring(0, sepIdx + 1);
                                string prefixPattern = "$\"" + prefix;
                                int prefixPos = searchRegion.IndexOf(prefixPattern, StringComparison.Ordinal);
                                if (prefixPos < 0)
                                {
                                    prefixPattern = "\"" + prefix;
                                    prefixPos = searchRegion.IndexOf(prefixPattern, StringComparison.Ordinal);
                                }

                                if (prefixPos >= 0)
                                {
                                    int absolutePos = searchStart + prefixPos;
                                    int lineStart = currentCode.LastIndexOf('\n', absolutePos) + 1;
                                    int lineEnd = currentCode.IndexOf('\n', absolutePos);
                                    if (lineEnd < 0) lineEnd = currentCode.Length;

                                    codeBox.Focus();
                                    codeBox.Select(lineStart, lineEnd - lineStart);
                                    codeBox.ScrollToCaret();

                                    System.Diagnostics.Debug.WriteLine(
                                        $"[CodeNav] ✓ STRATEGY 2 (prefix match): found \"{prefix}\" for computed '{searchContent}' in {sprite.SourceMethodName ?? "global"}");
                                    return true;
                                }
                            }
                        }
                    }

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
                            System.Collections.Generic.List<SpriteSourceLocation> locations = null;

                            // Try real method mapping first
                            if (sourceMap.TryGetValue(sprite.SourceMethodName, out locations) && 
                                sprite.SourceMethodIndex < locations.Count)
                            {
                                // found via real method
                            }
                            // Fallback: try case block mapping for virtual switch/case names
                            else if (sprite.SourceMethodName.StartsWith("Render"))
                            {
                                string caseName = sprite.SourceMethodName.Substring("Render".Length);
                                locations = SpriteSourceMapper.MapCaseBlockSpriteCreations(currentCode, caseName);
                            }

                            if (locations != null && sprite.SourceMethodIndex < locations.Count)
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

            // If content search didn't find anything but SpriteAddMapper had a valid position
            // (inside a helper method where the text is passed as a parameter), use it.
            if (mapperFallbackStart >= 0)
            {
                codeBox.Focus();
                codeBox.Select(mapperFallbackStart, mapperFallbackEnd - mapperFallbackStart);
                codeBox.ScrollToCaret();

                System.Diagnostics.Debug.WriteLine($"[CodeNav] ✓ Navigated via SpriteAddMapper fallback (helper method sprite creation site)");
                return true;
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

        // ══════════════════════════════════════════════════════════════════════════
        // LOOP-AWARE NAVIGATION HELPER
        // When a runtime sprite index exceeds the source Add() call count (the sprite
        // was produced by a loop/lambda), find the sprite's text (or a prefix of it)
        // in the method body and navigate to the nearest in-loop source Add() call.
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Finds the sprite's text in the method body and navigates to the nearest
        /// in-loop source Add() call.  Returns 1 if navigation succeeded, 0 if not.
        /// </summary>
        private static int NavigateToNearestLoopAdd(
            string currentCode, string methodName, string spriteText,
            System.Collections.Generic.List<SpriteAddMapper.AddCallInfo> calls,
            ScintillaCodeBox codeBox)
        {
            try
            {
                // Find the method body bounds
                string methodEsc = System.Text.RegularExpressions.Regex.Escape(methodName);
                var defRx = new System.Text.RegularExpressions.Regex(
                    @"(void|List<MySprite>|IEnumerable<MySprite>)\s+" + methodEsc + @"\s*\(");
                var defMatch = defRx.Match(currentCode);
                if (!defMatch.Success) return 0;

                int mStart = defMatch.Index;
                int mEnd = currentCode.Length;
                int braceCount = 0;
                bool foundBrace = false;
                for (int k = mStart; k < currentCode.Length; k++)
                {
                    if (currentCode[k] == '{') { braceCount++; foundBrace = true; }
                    else if (currentCode[k] == '}')
                    {
                        braceCount--;
                        if (foundBrace && braceCount == 0) { mEnd = k; break; }
                    }
                }

                string mBody = currentCode.Substring(mStart, mEnd - mStart);

                // Search for the sprite text as a quoted literal in the method body
                int textPosInMethod = mBody.IndexOf("\"" + spriteText + "\"", StringComparison.Ordinal);

                // If not found, try prefix matching for computed/interpolated strings
                // e.g., "WIND: N/A" → look for "$\"WIND:" or "\"WIND:"
                if (textPosInMethod < 0 && spriteText.Length > 3)
                {
                    foreach (char sep in new[] { ':', ' ', '-', '(' })
                    {
                        int sepIdx = spriteText.IndexOf(sep);
                        if (sepIdx >= 2)
                        {
                            string prefix = spriteText.Substring(0, sepIdx + 1);
                            textPosInMethod = mBody.IndexOf("$\"" + prefix, StringComparison.Ordinal);
                            if (textPosInMethod < 0)
                                textPosInMethod = mBody.IndexOf("\"" + prefix, StringComparison.Ordinal);
                            if (textPosInMethod >= 0)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"[CodeNav] → Loop fallback: found prefix \"{prefix}\" at method offset {textPosInMethod}");
                                break;
                            }
                        }
                    }
                }

                if (textPosInMethod < 0) return 0;

                // Compute the line number where the text was found
                int textAbsPos = mStart + textPosInMethod;
                int textLine = 1;
                for (int k = 0; k < textAbsPos && k < currentCode.Length; k++)
                    if (currentCode[k] == '\n') textLine++;

                // Find the nearest source Add() call AT or AFTER the text line.
                // Text definitions (string literals, array inits) precede the Add()
                // calls that use them, so forward proximity is the best heuristic.
                int bestDist = int.MaxValue;
                int bestIdx = -1;
                for (int ci = 0; ci < calls.Count; ci++)
                {
                    int callLine = calls[ci].AddLineNumber;
                    if (callLine < textLine) continue; // prefer forward
                    int dist = callLine - textLine;
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestIdx = ci;
                    }
                }

                // Fallback: nearest Add() in any direction
                if (bestIdx < 0)
                {
                    for (int ci = 0; ci < calls.Count; ci++)
                    {
                        int dist = Math.Abs(calls[ci].AddLineNumber - textLine);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestIdx = ci;
                        }
                    }
                }

                if (bestIdx >= 0 && bestDist <= 80)
                {
                    // Navigate to the TEXT line (where the string was found),
                    // not the Add() line — the text line is more useful for the user.
                    int navCharPos = textAbsPos;
                    if (navCharPos >= 0 && navCharPos < currentCode.Length)
                    {
                        int lineStart = currentCode.LastIndexOf('\n', navCharPos) + 1;
                        int lineEnd = currentCode.IndexOf('\n', navCharPos);
                        if (lineEnd < 0) lineEnd = currentCode.Length;

                        codeBox.Focus();
                        codeBox.Select(lineStart, lineEnd - lineStart);
                        codeBox.ScrollToCaret();

                        System.Diagnostics.Debug.WriteLine(
                            $"[CodeNav] ✓ Loop-aware fallback: text found at line {textLine}, validated by Add()[{bestIdx}] at line {calls[bestIdx].AddLineNumber} (distance={bestDist} lines)");
                        return 1;
                    }
                }

                return 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CodeNav] Loop-aware fallback error: {ex.Message}");
                return 0;
            }
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
        public static bool JumpToSpriteSource(SpriteEntry sprite, ScintillaCodeBox codeBox, string currentCode = null, ElementSpriteMapping spriteMapping = null)
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
