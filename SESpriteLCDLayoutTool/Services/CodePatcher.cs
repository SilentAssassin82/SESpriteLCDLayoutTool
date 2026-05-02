using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using SESpriteLCDLayoutTool.Models;
using SESpriteLCDLayoutTool.Services.CodeInjection;

namespace SESpriteLCDLayoutTool.Services
{
    public static partial class CodeGenerator
    {
        // ── Per-sprite dynamic round-trip ─────────────────────────────────────

        /// <summary>
        /// Number of sprite property changes from the most recent <see cref="PatchOriginalSource"/>
        /// call that could not be applied because the source uses an expression
        /// (interpolated string, ternary, variable, etc.) instead of a literal value.
        /// MainForm reads this to show a status warning so the user understands why
        /// the code panel didn't reflect their edit.
        /// </summary>
        public static int LastUnpatchedChangeCount { get; private set; }

        /// <summary>
        /// Description of the first unpatched change in the most recent
        /// <see cref="PatchOriginalSource"/> call (e.g. "Text on TEXT sprite #3").
        /// </summary>
        public static string LastUnpatchedReason { get; private set; }

        /// <summary>Diagnostic counter: number of <see cref="SourceEdit"/>s the
        /// round-trip planner pre-pass applied during the most recent
        /// <see cref="PatchOriginalSource"/> call (0 when the flag is off, the
        /// planner produced no ops, or the injector refused).</summary>
        public static int LastInjectorEditCount { get; private set; }

        /// <summary>
        /// Per-sprite property patching for dynamic code (loops, switch/case, expressions).
        /// Compares each sprite's current values against its ImportBaseline and surgically
        /// replaces only the literal values (colors, textures, fonts) that changed,
        /// preserving all dynamic expressions and surrounding code.
        /// Returns null if per-sprite patching is not applicable.
        /// </summary>
        public static string PatchOriginalSource(LcdLayout layout)
        {
            LastUnpatchedChangeCount = 0;
            LastUnpatchedReason = null;
            LastInjectorEditCount = 0;

            string original = layout.OriginalSourceCode;
            if (string.IsNullOrWhiteSpace(original)) return null;

            // ── Central-injector pre-pass: the planner emits PropertyPatchOps
            //    for every (sprite, property) pair it can express as an exact
            //    span rewrite (text/sprite name/font/color/position/size/
            //    rotation/scale).  The injector applies them and reports
            //    SourceEdits we use to shift SourceStart/SourceEnd in lockstep
            //    with the legacy offset contract.  Covered baselines are
            //    advanced so the legacy string-literal fallback below sees no
            //    diff for those properties and never double-patches.
            original = ApplyRoundTripPlannerPrePass(layout, original);

            // Collect sprites that have source tracking AND a baseline (chunk patching).
            var patchable = new System.Collections.Generic.List<SpriteEntry>();
            // Collect sprites that only have a baseline (for nav-index pass 2 — covers
            // helper-call sprites like DrawGauge(s, "POWER", ...) where the runtime
            // sprite couldn't be matched to a parsed code chunk).
            var baselineOnly = new System.Collections.Generic.List<SpriteEntry>();
            foreach (var sp in layout.Sprites)
            {
                if (sp.IsReferenceLayout) continue;
                if (sp.ImportBaseline == null) continue;
                if (sp.SourceStart < 0 || sp.SourceEnd < 0)
                {
                    baselineOnly.Add(sp);
                    continue;
                }
                patchable.Add(sp);
            }

            if (patchable.Count == 0 && baselineOnly.Count == 0) return null;

            // Sort by SourceStart descending so we patch from end-to-start (preserves offsets)
            patchable.Sort((a, b) => b.SourceStart.CompareTo(a.SourceStart));

            var result = new StringBuilder(original);

            foreach (var sp in patchable)
            {
                var bl = sp.ImportBaseline;
                if (sp.SourceStart >= result.Length || sp.SourceEnd > result.Length) continue;

                string chunk = result.ToString(sp.SourceStart, sp.SourceEnd - sp.SourceStart);
                string patched = chunk;

                // Patch texture/sprite name (string literal)
                if (sp.Type == SpriteEntryType.Texture &&
                    bl.SpriteName != sp.SpriteName &&
                    bl.SpriteName != null && sp.SpriteName != null)
                {
                    string before = patched;
                    patched = ReplaceStringLiteral(patched, bl.SpriteName, sp.SpriteName);
                    if (patched == before) NoteUnpatched(sp, "sprite name (expression in source)");
                }

                // Patch text content (string literal)
                if (sp.Type == SpriteEntryType.Text &&
                    bl.Text != sp.Text &&
                    bl.Text != null && sp.Text != null)
                {
                    string before = patched;
                    patched = ReplaceStringLiteral(patched, bl.Text, sp.Text);
                    if (patched == before) NoteUnpatched(sp, "text (interpolated/expression $\"...\" in source)");
                }

                // Patch font name (string literal)
                if (bl.FontId != sp.FontId &&
                    bl.FontId != null && sp.FontId != null)
                {
                    string before = patched;
                    patched = ReplaceStringLiteral(patched, bl.FontId, sp.FontId);
                    if (patched == before) NoteUnpatched(sp, "font (expression in source)");
                }

                // Color / Position / Size / Rotation / Scale literals are owned
                // by the central-injector pre-pass above (RoundTripPatchPlanner
                // emits PropertyPatchOps for every literal form the legacy
                // helpers used to handle).  Anything the planner can't express
                // (expression-only forms, interpolated literals, baseline-not-
                // in-chunk) is reported via NoteUnpatched in the pre-pass.

                // Only substitute if something actually changed
                if (patched != chunk)
                {
                    int oldLen = sp.SourceEnd - sp.SourceStart;
                    int newLen = patched.Length;
                    result.Remove(sp.SourceStart, oldLen);
                    result.Insert(sp.SourceStart, patched);

                    // Update the sprite's own end so the next edit reads the
                    // full patched chunk (not a truncated one), and refresh
                    // its baseline so the next diff is computed against the
                    // values we just persisted to source.  Sprites are patched
                    // in descending SourceStart order, so earlier sprites are
                    // unaffected by this delta.
                    sp.SourceEnd = sp.SourceStart + newLen;
                    sp.ImportBaseline = sp.CloneValues();
                }
            }

            // ── Pass 2: out-of-chunk literal fallback (Torch / helper code) ──
            // For string properties (text / sprite name / font) where the
            // in-chunk patch failed because the literal lives OUTSIDE the
            // sprite's source chunk (e.g. helper call-site argument like
            // DrawGauge(s, "POWER", ...) where the chunk is the helper's
            // CreateText(label, ...) line), use the Roslyn-backed
            // SpriteNavigationIndex to map the value to the correct call-site
            // literal — disambiguated by method name / index when the same
            // string appears multiple times.  Falls back to global unique-
            // literal replacement only if the index can't resolve.
            //
            // Pass 2 sprites are processed in descending SourceStart order
            // so earlier offsets stay valid as later edits shift the buffer.
            // We also build a fresh nav index after each successful edit
            // (rare and cheap) so subsequent lookups see the new positions.
            SpriteNavigationIndex navIndex = SpriteNavigationIndex.Build(result.ToString());

            // Pass 2 runs over BOTH chunk-tracked sprites (where in-chunk patch may
            // have failed for an out-of-chunk literal) AND baseline-only sprites
            // (helper-call sprites like DrawGauge(s, "POWER", ...) that have no
            // source chunk at all).  The nav index is the only source of truth for
            // these — ShiftSpritePositions only shifts entries that have offsets.
            var allBaselined = new System.Collections.Generic.List<SpriteEntry>(patchable);
            allBaselined.AddRange(baselineOnly);

            foreach (var sp in allBaselined)
            {
                var bl = sp.ImportBaseline;
                if (bl == null) continue;

                bool changed = false;

                if (sp.Type == SpriteEntryType.Text &&
                    bl.Text != sp.Text && bl.Text != null && sp.Text != null)
                {
                    if (TryTargetedLiteralReplace(result, sp, bl.Text, sp.Text, navIndex,
                        out int editPos, out int delta))
                    {
                        ShiftSpritePositions(patchable, editPos, delta, sp);
                        sp.ImportBaseline = sp.CloneValues();
                        changed = true;
                    }
                }

                if (sp.Type == SpriteEntryType.Texture &&
                    bl.SpriteName != sp.SpriteName &&
                    bl.SpriteName != null && sp.SpriteName != null)
                {
                    if (TryTargetedLiteralReplace(result, sp, bl.SpriteName, sp.SpriteName, navIndex,
                        out int editPos, out int delta))
                    {
                        ShiftSpritePositions(patchable, editPos, delta, sp);
                        sp.ImportBaseline = sp.CloneValues();
                        changed = true;
                    }
                }

                if (bl.FontId != sp.FontId && bl.FontId != null && sp.FontId != null)
                {
                    if (TryTargetedLiteralReplace(result, sp, bl.FontId, sp.FontId, navIndex,
                        out int editPos, out int delta))
                    {
                        ShiftSpritePositions(patchable, editPos, delta, sp);
                        sp.ImportBaseline = sp.CloneValues();
                        changed = true;
                    }
                }

                // Rebuild the nav index if the buffer shifted (offsets are now stale).
                if (changed)
                    navIndex = SpriteNavigationIndex.Build(result.ToString());
            }

            string final = result.ToString();
            // Return the (possibly unchanged) original — null only when patching is not applicable
            return final;
        }

        /// <summary>
        /// Round-trip planner pre-pass.
        ///
        /// Builds a <see cref="RoundTripPatchPlanner.PlannerReport"/> for the
        /// layout, runs the resulting <see cref="InjectionPlan"/> through
        /// <see cref="RoslynCodeInjector"/>, and uses the reported
        /// <see cref="SourceEdit"/>s to shift each sprite's
        /// <c>SourceStart</c>/<c>SourceEnd</c> in lockstep with the rewritten
        /// buffer — preserving the line-jump contract that the canvas relies
        /// on.  For every covered (sprite, property) pair the sprite's
        /// <see cref="SpriteEntry.ImportBaseline"/> field is advanced to the
        /// new value, so the legacy string-literal fallback that runs after
        /// this returns "no diff" for those properties and never double-patches
        /// them.
        ///
        /// Anything the planner couldn't cover (interpolated literals,
        /// ambiguous duplicates, escape-required values, baseline-only
        /// sprites) is left in the baseline untouched, so the legacy
        /// <c>ReplaceStringLiteral</c> / nav-index passes inside
        /// <see cref="PatchOriginalSource"/> still own those cases.
        /// </summary>
        private static string ApplyRoundTripPlannerPrePass(LcdLayout layout, string original)
        {
            var report = RoundTripPatchPlanner.BuildPlan(layout);
            if (report.Plan.PropertyPatches.Count == 0) return original;

            var injector = new RoslynCodeInjector();
            var injResult = injector.Apply(original, report.Plan);
            if (!injResult.Success || injResult.Edits.Count == 0)
            {
                // Injector refused to apply (parse error, all spans stale,
                // overlaps etc.).  Leave the buffer untouched and let the
                // legacy literal pass try again — it owns the diff diagnostics
                // through NoteUnpatched.
                return original;
            }

            LastInjectorEditCount = injResult.Edits.Count;

            // Apply each edit's pre-edit (Start, End, Delta) to every sprite's
            // SourceStart/SourceEnd using exactly the same rule
            // ShiftSpritePositions uses, so the canvas sees identical offsets
            // whether the legacy or new path applied the patch.
            foreach (var edit in injResult.Edits)
            {
                int editStart = edit.Start;
                int delta = edit.Delta;
                if (delta == 0) continue;
                foreach (var sp in layout.Sprites)
                {
                    if (sp.SourceStart < 0 || sp.SourceEnd < 0) continue;
                    if (sp.SourceStart >= editStart)
                    {
                        sp.SourceStart += delta;
                        sp.SourceEnd += delta;
                    }
                    else if (sp.SourceEnd > editStart)
                    {
                        // Edit landed inside this sprite's chunk — extend its end.
                        sp.SourceEnd += delta;
                    }
                }
            }

            // Advance baselines on covered (sprite, property) pairs so the
            // legacy literal pass sees no diff and skips them entirely.
            foreach (var c in report.Covered)
            {
                if (c.Sprite == null || c.Sprite.ImportBaseline == null) continue;
                switch (c.Property)
                {
                    case "Text":       c.Sprite.ImportBaseline.Text = c.NewValue; break;
                    case "SpriteName": c.Sprite.ImportBaseline.SpriteName = c.NewValue; break;
                    case "FontId":     c.Sprite.ImportBaseline.FontId = c.NewValue; break;
                    case "Color":
                        c.Sprite.ImportBaseline.ColorR = c.NewColorR;
                        c.Sprite.ImportBaseline.ColorG = c.NewColorG;
                        c.Sprite.ImportBaseline.ColorB = c.NewColorB;
                        c.Sprite.ImportBaseline.ColorA = c.NewColorA;
                        break;
                    case "Position":
                        c.Sprite.ImportBaseline.X = c.NewFloatX;
                        c.Sprite.ImportBaseline.Y = c.NewFloatY;
                        break;
                    case "Size":
                        c.Sprite.ImportBaseline.Width = c.NewFloatX;
                        c.Sprite.ImportBaseline.Height = c.NewFloatY;
                        break;
                    case "Rotation":
                        c.Sprite.ImportBaseline.Rotation = c.NewFloatScalar;
                        break;
                    case "Scale":
                        c.Sprite.ImportBaseline.Scale = c.NewFloatScalar;
                        break;
                }
            }

            return injResult.RewrittenSource;
        }

        /// <summary>
        /// Locates the best-matching string literal for a sprite property edit
        /// using the Roslyn-backed <see cref="SpriteNavigationIndex"/>, which
        /// understands helper-method call sites (e.g. <c>DrawGauge(s, "POWER", ...)</c>)
        /// and uses the sprite's runtime <c>SourceMethodName</c> /
        /// <c>SourceMethodIndex</c> for disambiguation.  Falls back to the
        /// global unique-literal scan when the index cannot resolve.
        /// On success, replaces the literal's content in <paramref name="buffer"/>
        /// and reports the absolute edit position and length delta.
        /// </summary>
        private static bool TryTargetedLiteralReplace(StringBuilder buffer,
            SpriteEntry sprite, string oldValue, string newValue,
            SpriteNavigationIndex navIndex,
            out int editPos, out int delta)
        {
            editPos = -1;
            delta = 0;
            if (string.IsNullOrEmpty(oldValue) || newValue == null) return false;

            // 1) Ask the navigation index for the best location.  It already
            //    prefers CallSiteArg / DirectSprite over generic literals and
            //    disambiguates using SourceMethodName / SourceMethodIndex.
            int charOffset = -1;
            if (navIndex != null)
            {
                var entry = navIndex.Lookup(oldValue, sprite.SourceMethodName, sprite.SourceMethodIndex);
                if (entry != null)
                    charOffset = entry.CharOffset;
            }

            string text = buffer.ToString();
            var literals = FindStringLiterals(text);
            if (literals.Count == 0) return false;

            // Find candidate non-interpolated literals matching the old value.
            var candidates = new List<int>();
            for (int i = 0; i < literals.Count; i++)
            {
                var lit = literals[i];
                string content = text.Substring(lit.ContentStart, lit.ContentEnd - lit.ContentStart);
                string decoded = DecodeForLiteral(content, lit.Kind);
                if (decoded.IndexOf('\uFFFF') >= 0) continue;
                if (!string.Equals(decoded, oldValue, StringComparison.Ordinal)) continue;
                candidates.Add(i);
            }
            if (candidates.Count == 0) return false;

            int matchIndex = -1;

            // 2) If the nav index gave us an offset, find the literal whose
            //    quote-start matches it (literals[i].ContentStart - 1 for "..." form,
            //    or use proximity since interpolated/verbatim shift the offset).
            if (charOffset >= 0)
            {
                int bestDist = int.MaxValue;
                foreach (int ci in candidates)
                {
                    var lit = literals[ci];
                    int litStart = lit.ContentStart - 1; // skip opening quote
                    int dist = Math.Abs(litStart - charOffset);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        matchIndex = ci;
                    }
                }
                // Tight tolerance — the nav index produces exact spans.
                if (bestDist > 8) matchIndex = -1;
            }

            // 3) Fallback: if exactly one candidate exists in the whole file, use it.
            if (matchIndex < 0 && candidates.Count == 1)
                matchIndex = candidates[0];

            // 4) Fallback: pick the candidate nearest to the sprite's source anchor.
            //    Real-world Torch helpers usually have the call-site close to the
            //    helper invocation chain that produced the sprite.
            if (matchIndex < 0 && sprite.SourceStart >= 0 && candidates.Count > 1)
            {
                int anchor = sprite.SourceStart;
                int bestDist = int.MaxValue;
                foreach (int ci in candidates)
                {
                    int dist = Math.Abs(literals[ci].ContentStart - anchor);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        matchIndex = ci;
                    }
                }
            }

            if (matchIndex < 0) return false;

            var hit = literals[matchIndex];
            string ins = EscForLiteral(newValue, hit.Kind);
            int contentLen = hit.ContentEnd - hit.ContentStart;
            buffer.Remove(hit.ContentStart, contentLen);
            buffer.Insert(hit.ContentStart, ins);

            editPos = hit.ContentStart;
            delta = ins.Length - contentLen;
            return true;
        }

        /// <summary>
        /// Searches the whole source for non-interpolated string literals whose
        /// decoded content equals <paramref name="oldValue"/>.  If exactly one
        /// such literal exists, replaces its content with the appropriately
        /// escaped form of <paramref name="newValue"/> and returns the absolute
        /// edit position and length delta.  Returns false when zero or
        /// multiple matches are found (ambiguous — leave for the user).
        /// </summary>
        private static bool TryGlobalUniqueLiteralReplace(StringBuilder buffer,
            string oldValue, string newValue, out int editPos, out int delta)
        {
            editPos = -1;
            delta = 0;
            if (string.IsNullOrEmpty(oldValue) || newValue == null) return false;

            string text = buffer.ToString();
            var literals = FindStringLiterals(text);
            if (literals.Count == 0) return false;

            int matchIndex = -1;
            for (int i = 0; i < literals.Count; i++)
            {
                var lit = literals[i];
                string content = text.Substring(lit.ContentStart, lit.ContentEnd - lit.ContentStart);
                string decoded = DecodeForLiteral(content, lit.Kind);
                // Only consider non-interpolated literals (no holes).
                if (decoded.IndexOf('\uFFFF') >= 0) continue;
                if (!string.Equals(decoded, oldValue, StringComparison.Ordinal)) continue;

                if (matchIndex >= 0) return false; // ambiguous — bail
                matchIndex = i;
            }

            if (matchIndex < 0) return false;

            var hit = literals[matchIndex];
            string ins = EscForLiteral(newValue, hit.Kind);
            int contentLen = hit.ContentEnd - hit.ContentStart;
            buffer.Remove(hit.ContentStart, contentLen);
            buffer.Insert(hit.ContentStart, ins);

            editPos = hit.ContentStart;
            delta = ins.Length - contentLen;
            return true;
        }

        /// <summary>
        /// Adjusts <c>SourceStart</c>/<c>SourceEnd</c> of every patchable sprite
        /// (except the originating one) whose range starts after a global edit
        /// position so subsequent in-chunk reads stay aligned to the buffer.
        /// </summary>
        private static void ShiftSpritePositions(List<SpriteEntry> sprites,
            int editPos, int delta, SpriteEntry origin)
        {
            if (delta == 0) return;
            foreach (var sp in sprites)
            {
                // Shift any sprite whose chunk starts AT or AFTER the edit.
                if (sp.SourceStart >= editPos)
                {
                    sp.SourceStart += delta;
                    sp.SourceEnd += delta;
                }
                else if (sp.SourceEnd > editPos)
                {
                    // Edit landed inside this sprite's chunk — extend its end.
                    // (Rare: only when the unique literal happens to be inside
                    // another sprite's range, which means in-chunk patching
                    // would have caught it — but stay safe.)
                    sp.SourceEnd += delta;
                }
            }
        }

        private static void NoteUnpatched(SpriteEntry sp, string reason)
        {
            LastUnpatchedChangeCount++;
            if (LastUnpatchedReason == null)
            {
                string label = sp.UserLabel
                    ?? (sp.Type == SpriteEntryType.Text ? "TEXT sprite" : (sp.SpriteName ?? "TEXTURE sprite"));
                LastUnpatchedReason = $"{label}: {reason}";
            }
        }

        /// <summary>
        /// Inserts code blocks for newly added sprites (those with SourceStart &lt; 0) into the
        /// patched source code. After insertion, updates each sprite's SourceStart/SourceEnd
        /// and creates an ImportBaseline so they become tracked for future round-trip patching.
        /// Returns the modified code with new sprites inserted, or the original if no insertion needed.
        /// </summary>
        public static string InsertNewSpritesIntoSource(LcdLayout layout, string patchedCode)
        {
            if (layout == null || string.IsNullOrWhiteSpace(patchedCode)) return patchedCode;

            // Find untracked sprites (newly added, not yet in the code)
            var newSprites = new List<SpriteEntry>();
            foreach (var sp in layout.Sprites)
            {
                if (sp.IsReferenceLayout) continue;
                if (sp.SourceStart >= 0) continue; // already tracked
                newSprites.Add(sp);
            }

            if (newSprites.Count == 0) return patchedCode;

            // Find the insertion point: look for the last "frame.Add(" or ".Add(new MySprite"
            // and insert after its closing ");", or before "}" if we can find the frame block end.
            int insertPos = FindSpriteInsertionPoint(patchedCode);
            if (insertPos < 0) return patchedCode; // couldn't find a safe insertion point

            // Detect indentation from surrounding code
            string indent = DetectIndentation(patchedCode, insertPos);

            var sb = new StringBuilder(patchedCode);
            int offset = 0; // track how much we've shifted positions

            foreach (var sp in newSprites)
            {
                string spriteCode = GenerateSingleSpriteCode(sp, indent);
                int actualInsertPos = insertPos + offset;

                sb.Insert(actualInsertPos, spriteCode);

                // Update the sprite's source tracking so it becomes a tracked sprite
                sp.SourceStart = actualInsertPos;
                sp.SourceEnd = actualInsertPos + spriteCode.Length;
                sp.ImportBaseline = sp.CloneValues();
                sp.ImportLabel = sp.Type == SpriteEntryType.Text ? "Text" : (sp.SpriteName ?? "Texture");

                offset += spriteCode.Length;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Finds the best position to insert new sprite code blocks.
        /// Looks for patterns like "frame.Add(new MySprite" or ".Add(new MySprite"
        /// and returns the position after the last such block's closing ");".
        /// </summary>
        private static int FindSpriteInsertionPoint(string code)
        {
            // Strategy: Find all ".Add(new MySprite" occurrences and pick the last one's end
            int lastAddEnd = -1;
            int searchPos = 0;

            while (searchPos < code.Length)
            {
                // Look for frame.Add or .Add patterns followed by MySprite
                int addIdx = code.IndexOf(".Add(new MySprite", searchPos, StringComparison.Ordinal);
                if (addIdx < 0)
                    addIdx = code.IndexOf(".Add(MySprite.Create", searchPos, StringComparison.Ordinal);
                if (addIdx < 0) break;

                // Find the closing ");" for this sprite block
                int closePos = FindSpriteBlockEnd(code, addIdx);
                if (closePos > lastAddEnd)
                    lastAddEnd = closePos;

                searchPos = addIdx + 10;
            }

            // If we found sprite adds, insert after the last one
            if (lastAddEnd > 0)
                return lastAddEnd;

            // Fallback: look for "using (var frame = " and insert before its closing "}"
            int frameStart = code.IndexOf("using (var frame =", StringComparison.Ordinal);
            if (frameStart < 0)
                frameStart = code.IndexOf("using(var frame =", StringComparison.Ordinal);
            if (frameStart >= 0)
            {
                // Find the opening brace of the using block
                int braceOpen = code.IndexOf('{', frameStart);
                if (braceOpen >= 0)
                {
                    // Find matching closing brace
                    int depth = 1;
                    int pos = braceOpen + 1;
                    while (pos < code.Length && depth > 0)
                    {
                        if (code[pos] == '{') depth++;
                        else if (code[pos] == '}') depth--;
                        if (depth > 0) pos++;
                    }
                    // Insert before the closing brace, with proper newlines
                    if (depth == 0 && pos > braceOpen)
                        return pos;
                }
            }

            return -1;
        }

        /// <summary>
        /// Finds the end position of a sprite block starting from a ".Add(" call.
        /// Returns the position after the closing ");" or "});" for object initializers.
        /// </summary>
        private static int FindSpriteBlockEnd(string code, int addStart)
        {
            // Look for object initializer pattern: .Add(new MySprite { ... });
            int braceOpen = code.IndexOf('{', addStart);
            int parenClose = code.IndexOf(");", addStart, StringComparison.Ordinal);

            // If there's a brace before the first ");", it's an object initializer
            if (braceOpen >= 0 && (parenClose < 0 || braceOpen < parenClose))
            {
                // Find matching closing brace
                int depth = 1;
                int pos = braceOpen + 1;
                while (pos < code.Length && depth > 0)
                {
                    if (code[pos] == '{') depth++;
                    else if (code[pos] == '}') depth--;
                    pos++;
                }
                // Now find ");" after the closing brace
                int endMarker = code.IndexOf(");", pos, StringComparison.Ordinal);
                if (endMarker >= 0)
                {
                    // Include trailing newline if present
                    int end = endMarker + 2;
                    if (end < code.Length && code[end] == '\r') end++;
                    if (end < code.Length && code[end] == '\n') end++;
                    return end;
                }
            }
            else if (parenClose >= 0)
            {
                // Simple constructor call: .Add(MySprite.CreateText(...));
                int end = parenClose + 2;
                if (end < code.Length && code[end] == '\r') end++;
                if (end < code.Length && code[end] == '\n') end++;
                return end;
            }

            return -1;
        }

        /// <summary>
        /// Detects the indentation level at the given position by looking at surrounding code.
        /// </summary>
        private static string DetectIndentation(string code, int pos)
        {
            // Walk backward to find the start of the previous line
            int lineStart = pos - 1;
            while (lineStart > 0 && code[lineStart] != '\n') lineStart--;
            if (lineStart > 0) lineStart++; // skip the \n

            // Count leading whitespace
            var indent = new StringBuilder();
            int i = lineStart;
            while (i < code.Length && (code[i] == ' ' || code[i] == '\t'))
            {
                indent.Append(code[i]);
                i++;
            }

            // Default to 8 spaces if we couldn't detect
            return indent.Length > 0 ? indent.ToString() : "        ";
        }

        /// <summary>
        /// Generates the code block for a single sprite, ready to be inserted into existing source.
        /// Uses the standard frame.Add(new MySprite { ... }); pattern.
        /// </summary>
        public static string GenerateSingleSpriteCode(SpriteEntry sp, string indent = "        ")
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.Append(indent);
            sb.AppendLine($"// [+] {sp.DisplayName}");
            sb.Append(indent);
            sb.AppendLine("frame.Add(new MySprite");
            sb.Append(indent);
            sb.AppendLine("{");

            string innerIndent = indent + "    ";

            if (sp.Type == SpriteEntryType.Text)
            {
                sb.Append(innerIndent);
                sb.AppendLine("Type           = SpriteType.TEXT,");
                sb.Append(innerIndent);
                sb.AppendLine($"Data           = {Q(sp.Text)},");
                sb.Append(innerIndent);
                sb.AppendLine($"Position       = new Vector2({sp.X:F4}f, {sp.Y:F4}f),");
                sb.Append(innerIndent);
                sb.AppendLine($"Color          = new Color({sp.ColorR}, {sp.ColorG}, {sp.ColorB}, {sp.ColorA}),");
                sb.Append(innerIndent);
                sb.AppendLine($"FontId         = {Q(sp.FontId)},");
                sb.Append(innerIndent);
                sb.AppendLine($"Alignment      = TextAlignment.{sp.Alignment.ToString().ToUpperInvariant()},");
                sb.Append(innerIndent);
                sb.AppendLine($"RotationOrScale = {sp.Scale:F2}f,");
            }
            else
            {
                sb.Append(innerIndent);
                sb.AppendLine("Type           = SpriteType.TEXTURE,");
                sb.Append(innerIndent);
                sb.AppendLine($"Data           = {Q(sp.SpriteName)},");
                sb.Append(innerIndent);
                sb.AppendLine($"Position       = new Vector2({sp.X:F4}f, {sp.Y:F4}f),");
                sb.Append(innerIndent);
                sb.AppendLine($"Size           = new Vector2({sp.Width:F4}f, {sp.Height:F4}f),");
                sb.Append(innerIndent);
                sb.AppendLine($"Color          = new Color({sp.ColorR}, {sp.ColorG}, {sp.ColorB}, {sp.ColorA}),");
                sb.Append(innerIndent);
                sb.AppendLine("Alignment      = TextAlignment.CENTER,");
                sb.Append(innerIndent);
                sb.AppendLine($"RotationOrScale = {sp.Rotation:F4}f,");
            }

            sb.Append(indent);
            sb.AppendLine("});");

            return sb.ToString();
        }

        /// <summary>
        /// Replaces a quoted string literal in the source text.
        /// Falls back to a smart edit when the exact baseline literal isn't present
        /// (e.g. interpolated strings, verbatim strings, concatenations) by detecting
        /// append/prepend/middle-replace edits and applying them inside the source
        /// literal that holds the changed segment.
        /// </summary>
        private static string ReplaceStringLiteral(string text, string oldValue, string newValue)
        {
            // Fast path: exact literal in source.
            string escapedOld = $"\"{Esc(oldValue)}\"";
            string escapedNew = $"\"{Esc(newValue)}\"";
            int idx = text.IndexOf(escapedOld, StringComparison.Ordinal);
            if (idx >= 0)
                return text.Substring(0, idx) + escapedNew + text.Substring(idx + escapedOld.Length);

            if (oldValue == null || newValue == null) return text;

            // Smart path: find a literal whose decoded content (with interpolation
            // holes as wildcards) matches the baseline value, then apply the
            // append/prepend/middle edit inside the static segment that contains it.
            var literals = FindStringLiterals(text);
            if (literals.Count == 0) return text;

            // What changed?
            int commonPrefix = 0;
            int maxPrefix = Math.Min(oldValue.Length, newValue.Length);
            while (commonPrefix < maxPrefix && oldValue[commonPrefix] == newValue[commonPrefix])
                commonPrefix++;

            int commonSuffix = 0;
            int maxSuffix = Math.Min(oldValue.Length - commonPrefix, newValue.Length - commonPrefix);
            while (commonSuffix < maxSuffix &&
                   oldValue[oldValue.Length - 1 - commonSuffix] == newValue[newValue.Length - 1 - commonSuffix])
                commonSuffix++;

            string oldMiddle = oldValue.Substring(commonPrefix, oldValue.Length - commonPrefix - commonSuffix);
            string newMiddle = newValue.Substring(commonPrefix, newValue.Length - commonPrefix - commonSuffix);
            int changeStart = commonPrefix;                       // index in baseline where change starts
            int changeEnd   = oldValue.Length - commonSuffix;     // exclusive end in baseline

            foreach (var lit in literals)
            {
                string content = text.Substring(lit.ContentStart, lit.ContentEnd - lit.ContentStart);
                string decoded = DecodeForLiteral(content, lit.Kind);

                // Anchor the literal's static segments against the baseline.  Each
                // static run between interpolation holes must appear in baseline,
                // in order, with the first run at index 0 and the last run flush
                // with baseline.Length.
                int[] segStarts; // start index in baseline of each segment
                int[] segLengths;
                if (!TryAnchorLiteral(decoded, oldValue, out segStarts, out segLengths))
                    continue;

                // Walk segments to find the one that holds (changeStart..changeEnd).
                // segCount may be 1 (no holes) or more (interpolated).
                int segCount = segStarts.Length;
                for (int s = 0; s < segCount; s++)
                {
                    int segBaseStart = segStarts[s];
                    int segBaseEnd   = segStarts[s] + segLengths[s];

                    // The change region [changeStart, changeEnd] must be fully
                    // contained in this segment's baseline range.  Pure insertions
                    // at segment boundaries (changeStart == changeEnd == segBaseStart
                    // or == segBaseEnd) also fall through here naturally.
                    if (changeStart < segBaseStart || changeEnd > segBaseEnd) continue;

                    // Locate this segment inside the raw literal content.
                    int segRawStart, segRawEnd;
                    if (!TryFindSegmentRawRange(content, lit.Kind, s, out segRawStart, out segRawEnd))
                        continue;

                    int relStart = changeStart - segBaseStart;
                    int relEnd   = changeEnd   - segBaseStart;
                    int rawAtStart = MapSegmentDecodedToRaw(content, lit.Kind, segRawStart, segRawEnd, relStart);
                    int rawAtEnd   = MapSegmentDecodedToRaw(content, lit.Kind, segRawStart, segRawEnd, relEnd);
                    if (rawAtStart < 0 || rawAtEnd < 0) continue;

                    int absStart = lit.ContentStart + rawAtStart;
                    int absEnd   = lit.ContentStart + rawAtEnd;
                    string ins = EscForLiteral(newMiddle, lit.Kind);
                    return text.Substring(0, absStart) + ins + text.Substring(absEnd);
                }
            }

            return text;
        }

        /// <summary>
        /// Tries to anchor a literal's decoded content (with '\uFFFF' placeholders for
        /// interpolation holes) against a baseline runtime string.  Returns the
        /// baseline start index and length of each static segment when the segments
        /// appear in order with the first anchored at 0 and the last flush with
        /// baseline.Length.
        /// </summary>
        private static bool TryAnchorLiteral(string decoded, string baseline,
                                             out int[] segStarts, out int[] segLengths)
        {
            segStarts = null;
            segLengths = null;
            var segments = decoded.Split('\uFFFF');
            var starts = new int[segments.Length];
            var lengths = new int[segments.Length];
            int pos = 0;
            for (int i = 0; i < segments.Length; i++)
            {
                string seg = segments[i];
                lengths[i] = seg.Length;
                if (seg.Length == 0)
                {
                    starts[i] = pos;
                    continue;
                }
                int hit = baseline.IndexOf(seg, pos, StringComparison.Ordinal);
                if (hit < 0) return false;
                // First segment must anchor at start; otherwise the literal isn't
                // the producer of this baseline.
                if (i == 0 && hit != 0) return false;
                starts[i] = hit;
                pos = hit + seg.Length;
            }
            // Last non-empty segment must end at baseline.Length so the literal
            // fully covers the baseline.
            for (int i = segments.Length - 1; i >= 0; i--)
            {
                if (segments[i].Length > 0)
                {
                    if (starts[i] + lengths[i] != baseline.Length) return false;
                    break;
                }
            }
            // Patch up empty trailing segments to point at baseline.Length
            for (int i = segments.Length - 1; i >= 0 && segments[i].Length == 0; i--)
                starts[i] = baseline.Length;

            segStarts = starts;
            segLengths = lengths;
            return true;
        }

        /// <summary>
        /// Returns the raw-content range [start, end) of the Nth static segment
        /// inside a literal (segments are separated by interpolation holes).
        /// </summary>
        private static bool TryFindSegmentRawRange(string raw, LiteralKind kind, int segmentIndex,
                                                   out int rawStart, out int rawEnd)
        {
            rawStart = -1;
            rawEnd = -1;
            bool verbatim = kind == LiteralKind.Verbatim || kind == LiteralKind.InterpolatedVerbatim;
            bool interp = kind == LiteralKind.Interpolated || kind == LiteralKind.InterpolatedVerbatim;
            int currentSeg = 0;
            int segStart = 0;
            int i = 0;
            while (i < raw.Length)
            {
                char c = raw[i];
                if (interp && c == '{' && i + 1 < raw.Length && raw[i + 1] == '{') { i += 2; continue; }
                if (interp && c == '}' && i + 1 < raw.Length && raw[i + 1] == '}') { i += 2; continue; }
                if (interp && c == '{')
                {
                    if (currentSeg == segmentIndex) { rawStart = segStart; rawEnd = i; return true; }
                    int depth = 1; i++;
                    while (i < raw.Length && depth > 0)
                    {
                        if (raw[i] == '{') depth++;
                        else if (raw[i] == '}') depth--;
                        i++;
                    }
                    currentSeg++;
                    segStart = i;
                    continue;
                }
                if (verbatim && c == '"' && i + 1 < raw.Length && raw[i + 1] == '"') { i += 2; continue; }
                if (!verbatim && c == '\\' && i + 1 < raw.Length) { i += 2; continue; }
                i++;
            }
            if (currentSeg == segmentIndex)
            {
                rawStart = segStart;
                rawEnd = raw.Length;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Maps a decoded-offset within a single static segment back to its raw
        /// content index, accounting for verbatim/regular escapes.  No interpolation
        /// holes can appear inside the segment by definition.
        /// </summary>
        private static int MapSegmentDecodedToRaw(string raw, LiteralKind kind,
                                                  int segRawStart, int segRawEnd,
                                                  int decodedOffset)
        {
            bool verbatim = kind == LiteralKind.Verbatim || kind == LiteralKind.InterpolatedVerbatim;
            int rawIdx = segRawStart;
            int decIdx = 0;
            while (rawIdx < segRawEnd)
            {
                if (decIdx == decodedOffset) return rawIdx;
                char c = raw[rawIdx];
                if (verbatim)
                {
                    if (c == '"' && rawIdx + 1 < segRawEnd && raw[rawIdx + 1] == '"') { rawIdx += 2; decIdx++; continue; }
                    rawIdx++; decIdx++;
                }
                else
                {
                    if (c == '\\' && rawIdx + 1 < segRawEnd) { rawIdx += 2; decIdx++; continue; }
                    rawIdx++; decIdx++;
                }
            }
            return decIdx == decodedOffset ? rawIdx : -1;
        }

        private enum LiteralKind { Regular, Verbatim, Interpolated, InterpolatedVerbatim }

        private struct StringLiteralRange
        {
            public int Start;          // position of the opening punctuation (e.g. $, @, ")
            public int ContentStart;   // first character of content (after opening quote)
            public int ContentEnd;     // exclusive index of closing quote
            public int End;            // ContentEnd + 1
            public LiteralKind Kind;
        }

        /// <summary>
        /// Locates string literals in a code chunk: "x", @"x", $"x", $@"x", @$"x".
        /// Skips comments. Returns the literals in source order.
        /// </summary>
        private static List<StringLiteralRange> FindStringLiterals(string text)
        {
            var list = new List<StringLiteralRange>();
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];

                // Skip line comment
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }
                // Skip block comment
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++;
                    i = Math.Min(text.Length, i + 2);
                    continue;
                }
                // Skip char literal
                if (c == '\'')
                {
                    i++;
                    while (i < text.Length && text[i] != '\'')
                    {
                        if (text[i] == '\\' && i + 1 < text.Length) i++;
                        i++;
                    }
                    if (i < text.Length) i++;
                    continue;
                }

                LiteralKind kind;
                int prefixLen;
                if (c == '"') { kind = LiteralKind.Regular; prefixLen = 1; }
                else if (c == '@' && i + 1 < text.Length && text[i + 1] == '"') { kind = LiteralKind.Verbatim; prefixLen = 2; }
                else if (c == '$' && i + 1 < text.Length && text[i + 1] == '"') { kind = LiteralKind.Interpolated; prefixLen = 2; }
                else if (c == '$' && i + 2 < text.Length && text[i + 1] == '@' && text[i + 2] == '"') { kind = LiteralKind.InterpolatedVerbatim; prefixLen = 3; }
                else if (c == '@' && i + 2 < text.Length && text[i + 1] == '$' && text[i + 2] == '"') { kind = LiteralKind.InterpolatedVerbatim; prefixLen = 3; }
                else { i++; continue; }

                int start = i;
                int contentStart = i + prefixLen;
                int p = contentStart;
                bool verbatim = kind == LiteralKind.Verbatim || kind == LiteralKind.InterpolatedVerbatim;
                bool interp = kind == LiteralKind.Interpolated || kind == LiteralKind.InterpolatedVerbatim;
                int braceDepth = 0;

                while (p < text.Length)
                {
                    char pc = text[p];
                    if (interp && braceDepth == 0 && pc == '{' && p + 1 < text.Length && text[p + 1] == '{') { p += 2; continue; }
                    if (interp && braceDepth == 0 && pc == '}' && p + 1 < text.Length && text[p + 1] == '}') { p += 2; continue; }
                    if (interp && pc == '{') { braceDepth++; p++; continue; }
                    if (interp && pc == '}' && braceDepth > 0) { braceDepth--; p++; continue; }

                    if (braceDepth > 0) { p++; continue; }

                    if (verbatim)
                    {
                        if (pc == '"')
                        {
                            if (p + 1 < text.Length && text[p + 1] == '"') { p += 2; continue; }
                            break;
                        }
                        p++;
                    }
                    else
                    {
                        if (pc == '\\' && p + 1 < text.Length) { p += 2; continue; }
                        if (pc == '"') break;
                        if (pc == '\n') break; // unterminated
                        p++;
                    }
                }

                if (p < text.Length && text[p] == '"')
                {
                    list.Add(new StringLiteralRange
                    {
                        Start = start,
                        ContentStart = contentStart,
                        ContentEnd = p,
                        End = p + 1,
                        Kind = kind
                    });
                    i = p + 1;
                }
                else
                {
                    i++;
                }
            }
            return list;
        }

        /// <summary>
        /// Decodes a literal's raw content into its runtime string value, treating
        /// interpolation holes as opaque placeholders that don't appear in the
        /// runtime text seen by the editor.  Holes are replaced with "\uFFFF" so
        /// they don't accidentally match any user text.
        /// </summary>
        private static string DecodeForLiteral(string raw, LiteralKind kind)
        {
            var sb = new StringBuilder(raw.Length);
            bool verbatim = kind == LiteralKind.Verbatim || kind == LiteralKind.InterpolatedVerbatim;
            bool interp = kind == LiteralKind.Interpolated || kind == LiteralKind.InterpolatedVerbatim;
            int i = 0;
            while (i < raw.Length)
            {
                char c = raw[i];
                if (interp && c == '{' && i + 1 < raw.Length && raw[i + 1] == '{') { sb.Append('{'); i += 2; continue; }
                if (interp && c == '}' && i + 1 < raw.Length && raw[i + 1] == '}') { sb.Append('}'); i += 2; continue; }
                if (interp && c == '{')
                {
                    // Skip the hole
                    int depth = 1;
                    i++;
                    while (i < raw.Length && depth > 0)
                    {
                        if (raw[i] == '{') depth++;
                        else if (raw[i] == '}') depth--;
                        i++;
                    }
                    sb.Append('\uFFFF'); // unmatchable placeholder
                    continue;
                }
                if (verbatim)
                {
                    if (c == '"' && i + 1 < raw.Length && raw[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                    sb.Append(c);
                    i++;
                }
                else
                {
                    if (c == '\\' && i + 1 < raw.Length)
                    {
                        char n = raw[i + 1];
                        switch (n)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case '0': sb.Append('\0'); break;
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            default: sb.Append(n); break;
                        }
                        i += 2;
                        continue;
                    }
                    sb.Append(c);
                    i++;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Maps a decoded-content index back to the corresponding raw-content index
        /// inside the literal.  Returns -1 if the decoded index falls inside an
        /// interpolation hole (cannot be mapped unambiguously).
        /// </summary>
        private static int MapDecodedToRaw(string raw, LiteralKind kind, int decodedIndex)
        {
            bool verbatim = kind == LiteralKind.Verbatim || kind == LiteralKind.InterpolatedVerbatim;
            bool interp = kind == LiteralKind.Interpolated || kind == LiteralKind.InterpolatedVerbatim;
            int rawIdx = 0;
            int decIdx = 0;
            while (rawIdx < raw.Length)
            {
                if (decIdx == decodedIndex) return rawIdx;
                char c = raw[rawIdx];
                if (interp && c == '{' && rawIdx + 1 < raw.Length && raw[rawIdx + 1] == '{') { rawIdx += 2; decIdx++; continue; }
                if (interp && c == '}' && rawIdx + 1 < raw.Length && raw[rawIdx + 1] == '}') { rawIdx += 2; decIdx++; continue; }
                if (interp && c == '{')
                {
                    int depth = 1; rawIdx++;
                    while (rawIdx < raw.Length && depth > 0)
                    {
                        if (raw[rawIdx] == '{') depth++;
                        else if (raw[rawIdx] == '}') depth--;
                        rawIdx++;
                    }
                    decIdx++; // hole counts as one decoded char (the placeholder)
                    continue;
                }
                if (verbatim)
                {
                    if (c == '"' && rawIdx + 1 < raw.Length && raw[rawIdx + 1] == '"') { rawIdx += 2; decIdx++; continue; }
                    rawIdx++; decIdx++;
                }
                else
                {
                    if (c == '\\' && rawIdx + 1 < raw.Length) { rawIdx += 2; decIdx++; continue; }
                    rawIdx++; decIdx++;
                }
            }
            return decIdx == decodedIndex ? rawIdx : -1;
        }

        /// <summary>Escapes a runtime string slice for insertion into a literal of the given kind.</summary>
        private static string EscForLiteral(string s, LiteralKind kind)
        {
            if (s == null) return "";
            bool verbatim = kind == LiteralKind.Verbatim || kind == LiteralKind.InterpolatedVerbatim;
            bool interp = kind == LiteralKind.Interpolated || kind == LiteralKind.InterpolatedVerbatim;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (interp && c == '{') { sb.Append("{{"); continue; }
                if (interp && c == '}') { sb.Append("}}"); continue; }
                if (verbatim)
                {
                    if (c == '"') sb.Append("\"\"");
                    else sb.Append(c);
                }
                else
                {
                    switch (c)
                    {
                        case '\\': sb.Append("\\\\"); break;
                        case '"':  sb.Append("\\\""); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        case '\0': sb.Append("\\0"); break;
                        default: sb.Append(c); break;
                    }
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Detects the surrounding code context for a sprite at a given position.
        /// Looks backwards for case labels, method names, or region comments.
        /// Returns a label like "Header", "Item", "Footer" or null.
        /// </summary>
        public static string DetectSpriteContext(string code, int position)
        {
            if (code == null || position <= 0) return null;

            string before = code.Substring(0, Math.Min(position, code.Length));

            // Strategy 1: nearest case statement (switch-based layouts)
            var caseMatch = Regex.Match(before,
                @"case\s+(?:\w+\.)*(\w+)\s*:", RegexOptions.RightToLeft);
            if (caseMatch.Success)
                return caseMatch.Groups[1].Value;

            // Strategies 2 & 3: search the 400 chars immediately before the sprite
            int searchStart = Math.Max(0, before.Length - 400);
            string recent = before.Substring(searchStart);

            // Strategy 2: nearest variable declaration (var/string/float/int/…)
            var varMatch = Regex.Match(recent,
                @"\b(?:var|string|float|int|bool|double|Vector2|Color)\s+(\w+)\s*=",
                RegexOptions.RightToLeft);
            if (varMatch.Success)
                return varMatch.Groups[1].Value;

            // Strategy 3: nearest single-line // comment
            var commentMatch = Regex.Match(recent,
                @"//\s*([A-Za-z][^\r\n]{1,38})", RegexOptions.RightToLeft);
            if (commentMatch.Success)
            {
                string label = commentMatch.Groups[1].Value.Trim();
                if (label.Length >= 2) return label;
            }

            return null;
        }

        // ── Region-based round-trip (static layouts) ──────────────────────────

        /// <summary>
        /// When OriginalSourceCode is set, splices updated sprite definitions back
        /// into the original pasted code so the user can paste straight back into
        /// their project. Returns null if round-trip is not possible.
        /// </summary>
        public static string GenerateRoundTrip(LcdLayout layout)
        {
            string original = layout.OriginalSourceCode;
            if (string.IsNullOrWhiteSpace(original) || layout.Sprites.Count == 0)
                return null;

            // Find all MySprite definitions in the original code.
            // Match: new MySprite { ... }, new MySprite(...), MySprite.CreateText(...), MySprite.CreateSprite(...)
            var spritePattern = new Regex(
                @"(new\s+MySprite\s*[\({])|(MySprite\s*\.\s*Create(?:Text|Sprite)\s*\()",
                RegexOptions.Compiled);

            var matches = spritePattern.Matches(original);
            if (matches.Count == 0) return null;

            // Safety guard: if the number of non-reference sprites in the layout
            // doesn't match the number of MySprite definitions in the original source,
            // round-trip would produce bloated or incorrect output — fall back to Generate().
            int nonRefCount = 0;
            foreach (var sp in layout.Sprites)
                if (!sp.IsReferenceLayout) nonRefCount++;
            if (nonRefCount != matches.Count) return null;

            // Region start: beginning of the LINE containing the first MySprite match.
            // This ensures we capture "frame.Add(" or similar prefixes on the same line.
            // Also absorb any preceding comment/blank lines (like "// [1] SquareSimple").
            int firstMatchPos = matches[0].Index;
            int regionStart = FindLineStart(original, firstMatchPos);
            regionStart = AbsorbPrecedingComments(original, regionStart);

            // Region end: after the last MySprite block's closing punctuation
            int regionEnd = -1;
            for (int i = matches.Count - 1; i >= 0; i--)
            {
                string matchText = matches[i].Value;
                char lastChar = matchText[matchText.Length - 1];

                if (lastChar == '{')
                {
                    int braceEnd = FindMatchingBrace(original, matches[i].Index + matchText.Length - 1);
                    if (braceEnd >= 0)
                        regionEnd = SkipTrailingPunctuation(original, braceEnd + 1);
                }
                else // '(' — constructor or factory method
                {
                    int parenEnd = FindMatchingParen(original, matches[i].Index + matchText.Length - 1);
                    if (parenEnd >= 0)
                        regionEnd = SkipTrailingPunctuation(original, parenEnd + 1);
                }

                if (regionEnd > 0) break;
            }

            if (regionEnd <= regionStart) return null;

            // Detect indentation from the line containing the first MySprite match
            string indent = DetectIndent(original, firstMatchPos);

            // Detect the inner indent from the first property line inside the
            // initializer block instead of hardcoding 4 spaces.  This preserves
            // the user's indentation style (tabs, 2 spaces, 8 spaces, etc.).
            string innerIndent = DetectInnerIndent(original, matches[0].Index, indent);

            // Detect the wrapper prefix on the same line before "new MySprite"
            // (e.g. "frame.Add(" or "sprites.Add(") so the round-trip preserves
            // the user's original calling convention.
            string wrapperPrefix = DetectWrapperPrefix(original, firstMatchPos);
            string wrapperSuffix = (wrapperPrefix.Length > 0 && wrapperPrefix.TrimEnd().EndsWith("(")) ? ");" : ";";

            // Generate just the sprite definitions with the detected indentation
            var sb = new StringBuilder();
            int actualCount = 0;
            for (int i = 0; i < layout.Sprites.Count; i++)
            {
                var sp = layout.Sprites[i];
                if (sp.IsReferenceLayout || sp.IsHidden) continue;

                if (actualCount > 0) sb.AppendLine();

                sb.AppendLine($"{indent}// [{actualCount + 1}] {sp.DisplayName}");
                sb.AppendLine($"{indent}{wrapperPrefix}new MySprite");
                sb.AppendLine($"{indent}{{");

                if (sp.Type == SpriteEntryType.Text)
                {
                    sb.AppendLine($"{innerIndent}Type           = SpriteType.TEXT,");
                    sb.AppendLine($"{innerIndent}Data           = {Q(sp.Text)},");
                    sb.AppendLine($"{innerIndent}Position       = new Vector2({sp.X:F1}f, {sp.Y:F1}f),");
                    sb.AppendLine($"{innerIndent}Color          = new Color({sp.ColorR}, {sp.ColorG}, {sp.ColorB}, {sp.ColorA}),");
                    sb.AppendLine($"{innerIndent}FontId         = {Q(sp.FontId)},");
                    sb.AppendLine($"{innerIndent}Alignment      = TextAlignment.{sp.Alignment.ToString().ToUpperInvariant()},");
                    sb.AppendLine($"{innerIndent}RotationOrScale = {sp.Scale:F2}f,");
                }
                else
                {
                    sb.AppendLine($"{innerIndent}Type           = SpriteType.TEXTURE,");
                    sb.AppendLine($"{innerIndent}Data           = {Q(sp.SpriteName)},");
                    sb.AppendLine($"{innerIndent}Position       = new Vector2({sp.X:F1}f, {sp.Y:F1}f),");
                    sb.AppendLine($"{innerIndent}Size           = new Vector2({sp.Width:F1}f, {sp.Height:F1}f),");
                    sb.AppendLine($"{innerIndent}Color          = new Color({sp.ColorR}, {sp.ColorG}, {sp.ColorB}, {sp.ColorA}),");
                    sb.AppendLine($"{innerIndent}Alignment      = TextAlignment.CENTER,");
                    sb.AppendLine($"{innerIndent}RotationOrScale = {sp.Rotation:F4}f,");
                }

                sb.Append($"{indent}}}{wrapperSuffix}");
                actualCount++;
            }

            if (actualCount == 0) return null;

            // Ensure the splice ends with a newline
            sb.AppendLine();

            // Splice: original[0..regionStart) + generated + original[regionEnd..)
            string before = original.Substring(0, regionStart);
            string after  = original.Substring(regionEnd);

            return before + sb.ToString() + after;
        }

        /// <summary>Finds the position of the start of the line containing <paramref name="position"/>.</summary>
        private static int FindLineStart(string code, int position)
        {
            int i = position;
            while (i > 0 && code[i - 1] != '\n')
                i--;
            return i;
        }

        /// <summary>
        /// From a line start position, walks backwards to absorb any preceding
        /// comment lines or blank lines that are part of the sprite block.
        /// </summary>
        private static int AbsorbPrecedingComments(string code, int lineStart)
        {
            int pos = lineStart;
            while (pos > 0)
            {
                // Find the start of the preceding line
                int prevEnd = pos - 1; // char before current '\n'
                if (prevEnd >= 0 && code[prevEnd] == '\n') prevEnd--; // skip \n
                if (prevEnd >= 0 && code[prevEnd] == '\r') prevEnd--; // skip \r

                int prevStart = prevEnd;
                while (prevStart > 0 && code[prevStart - 1] != '\n')
                    prevStart--;
                if (prevStart < 0) prevStart = 0;

                // Get the trimmed content of the preceding line
                string prevLine = code.Substring(prevStart, prevEnd - prevStart + 1).Trim();

                // Absorb comment lines and blank lines
                if (prevLine.Length == 0 || prevLine.StartsWith("//"))
                    pos = prevStart;
                else
                    break;
            }
            return pos;
        }

        private static string DetectIndent(string code, int position)
        {
            // Walk backwards from position to find the start of the line
            int lineStart = position;
            while (lineStart > 0 && code[lineStart - 1] != '\n')
                lineStart--;

            // Extract leading whitespace
            var indent = new StringBuilder();
            for (int i = lineStart; i < position && i < code.Length; i++)
            {
                if (code[i] == ' ' || code[i] == '\t')
                    indent.Append(code[i]);
                else
                    break;
            }
            return indent.ToString();
        }

        /// <summary>
        /// Detects the inner indentation used for properties inside the first
        /// MySprite initializer block.  Falls back to <paramref name="outerIndent"/>
        /// plus four spaces when detection fails.
        /// </summary>
        private static string DetectInnerIndent(string code, int matchPos, string outerIndent)
        {
            string fallback = outerIndent + "    ";

            // Find the opening '{' of the initializer after matchPos
            int bracePos = code.IndexOf('{', matchPos);
            if (bracePos < 0) return fallback;

            // Find the start of the next line after '{'
            int pos = bracePos + 1;
            while (pos < code.Length && code[pos] != '\n')
                pos++;
            if (pos >= code.Length) return fallback;
            pos++; // skip past '\n'

            // Extract leading whitespace of the first property line
            var sb = new StringBuilder();
            while (pos < code.Length && (code[pos] == ' ' || code[pos] == '\t'))
            {
                sb.Append(code[pos]);
                pos++;
            }

            // Sanity check: the inner indent should be longer than the outer indent
            // and the line should contain something (not be blank).
            string result = sb.ToString();
            if (result.Length > outerIndent.Length && pos < code.Length && code[pos] != '\r' && code[pos] != '\n')
                return result;
            return fallback;
        }

        /// <summary>
        /// Detects the wrapper prefix on the same line before "new MySprite",
        /// e.g. "frame.Add(" or "sprites.Add(".  Returns empty string when
        /// the "new MySprite" is at the beginning of the non-whitespace content.
        /// </summary>
        private static string DetectWrapperPrefix(string code, int matchPos)
        {
            // Walk backwards from matchPos to the start of the line
            int lineStart = matchPos;
            while (lineStart > 0 && code[lineStart - 1] != '\n')
                lineStart--;

            // Skip leading whitespace to get to the content start
            int contentStart = lineStart;
            while (contentStart < matchPos && (code[contentStart] == ' ' || code[contentStart] == '\t'))
                contentStart++;

            // If the content starts right at matchPos, there's no wrapper
            if (contentStart >= matchPos) return "";

            // Extract the text between contentStart and matchPos (e.g. "frame.Add(")
            return code.Substring(contentStart, matchPos - contentStart);
        }

        private static int SkipTrailingPunctuation(string code, int pos)
        {
            // Skip whitespace, ");", ",", or ";" after a closing brace/paren
            while (pos < code.Length)
            {
                char c = code[pos];
                if (c == ')' || c == ';' || c == ',' || c == ' ' || c == '\t')
                    pos++;
                else if (c == '\r' || c == '\n')
                {
                    pos++;
                    break;
                }
                else
                    break;
            }
            // Consume trailing newline
            if (pos < code.Length && code[pos] == '\n') pos++;
            return pos;
        }

        // ── Expression color extraction ──────────────────────────────────────

        /// <summary>
        /// Scans the source code around a sprite's definition for all Color literals.
        /// Looks backwards up to 600 chars from SourceStart (to catch variable declarations
        /// like <c>var tColor = flash ? new Color(0,200,255) : new Color(0,150,210);</c>)
        /// and within the sprite definition itself.
        /// Returns an empty list if no literals are found.
        /// </summary>
        public static List<ExpressionColor> ExtractColorLiterals(string sourceCode, int sourceStart, int sourceEnd)
        {
            var results = new List<ExpressionColor>();
            if (string.IsNullOrEmpty(sourceCode) || sourceStart < 0 || sourceEnd < 0) return results;

            // Scan window: up to 600 chars before the sprite definition to capture
            // variable declarations with Color expressions, plus the definition itself.
            int scanStart = Math.Max(0, sourceStart - 600);
            int scanEnd = Math.Min(sourceCode.Length, sourceEnd);
            if (scanEnd <= scanStart) return results;

            string window = sourceCode.Substring(scanStart, scanEnd - scanStart);

            // Match: new Color(R, G, B) and new Color(R, G, B, A) where R/G/B/A are integer literals
            var colorCtorPattern = new Regex(
                @"new\s+Color\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)(?:\s*,\s*(\d+))?\s*\)");

            foreach (Match m in colorCtorPattern.Matches(window))
            {
                int r = int.Parse(m.Groups[1].Value);
                int g = int.Parse(m.Groups[2].Value);
                int b = int.Parse(m.Groups[3].Value);
                int a = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 255;

                results.Add(new ExpressionColor
                {
                    R = r, G = g, B = b, A = a,
                    SourceOffset = scanStart + m.Index,
                    SourceLength = m.Length,
                    LiteralText = m.Value,
                });
            }

            // Match named colors: Color.White, Color.Black, Color.Red, etc.
            var namedColorPattern = new Regex(@"Color\.(White|Black|Red|Green|Blue|Yellow|Transparent|Cyan|Magenta|Orange|Gray|LightGray|DarkGray)(?!\w)");
            foreach (Match m in namedColorPattern.Matches(window))
            {
                // Skip if this overlaps with a "new Color(" match (unlikely but safe)
                int absOffset = scanStart + m.Index;
                bool overlaps = false;
                foreach (var ec in results)
                    if (absOffset >= ec.SourceOffset && absOffset < ec.SourceOffset + ec.SourceLength)
                    { overlaps = true; break; }
                if (overlaps) continue;

                var rgba = ResolveNamedColor(m.Groups[1].Value);
                if (rgba == null) continue;

                results.Add(new ExpressionColor
                {
                    R = rgba[0], G = rgba[1], B = rgba[2], A = rgba[3],
                    SourceOffset = absOffset,
                    SourceLength = m.Length,
                    LiteralText = m.Value,
                });
            }

            // Sort by source offset for consistent display order
            results.Sort((a, b) => a.SourceOffset.CompareTo(b.SourceOffset));
            return results;
        }

        /// <summary>Resolves a named color string to RGBA values.</summary>
        private static int[] ResolveNamedColor(string name)
        {
            switch (name)
            {
                case "White":       return new[] { 255, 255, 255, 255 };
                case "Black":       return new[] { 0, 0, 0, 255 };
                case "Red":         return new[] { 255, 0, 0, 255 };
                case "Green":       return new[] { 0, 128, 0, 255 };
                case "Blue":        return new[] { 0, 0, 255, 255 };
                case "Yellow":      return new[] { 255, 255, 0, 255 };
                case "Transparent": return new[] { 0, 0, 0, 0 };
                case "Cyan":        return new[] { 0, 255, 255, 255 };
                case "Magenta":     return new[] { 255, 0, 255, 255 };
                case "Orange":      return new[] { 255, 165, 0, 255 };
                case "Gray":        return new[] { 128, 128, 128, 255 };
                case "LightGray":   return new[] { 211, 211, 211, 255 };
                case "DarkGray":    return new[] { 169, 169, 169, 255 };
                default: return null;
            }
        }

        /// <summary>
        /// Patches a specific Color literal in OriginalSourceCode at a known offset.
        /// Returns the updated source code, or null if the offset is out of range.
        /// </summary>
        public static string PatchColorAtOffset(string sourceCode, ExpressionColor expr, int newR, int newG, int newB, int newA)
        {
            if (sourceCode == null || expr == null) return null;
            if (expr.SourceOffset < 0 || expr.SourceOffset + expr.SourceLength > sourceCode.Length) return null;

            // Verify the literal text still matches at the expected offset
            string actual = sourceCode.Substring(expr.SourceOffset, expr.SourceLength);
            if (actual != expr.LiteralText) return null;

            string replacement = newA != 255
                ? $"new Color({newR}, {newG}, {newB}, {newA})"
                : $"new Color({newR}, {newG}, {newB})";

            return sourceCode.Substring(0, expr.SourceOffset)
                 + replacement
                 + sourceCode.Substring(expr.SourceOffset + expr.SourceLength);
        }

        // ── Expression Vector2 extraction ────────────────────────────────────

        /// <summary>
        /// Scans the source code around a sprite's definition for all <c>new Vector2(X, Y)</c> literals.
        /// Looks backwards up to 600 chars from SourceStart and within the definition itself.
        /// </summary>
        public static List<ExpressionVector2> ExtractVector2Literals(string sourceCode, int sourceStart, int sourceEnd)
        {
            var results = new List<ExpressionVector2>();
            if (string.IsNullOrEmpty(sourceCode) || sourceStart < 0 || sourceEnd < 0) return results;

            int scanStart = Math.Max(0, sourceStart - 600);
            int scanEnd = Math.Min(sourceCode.Length, sourceEnd);
            if (scanEnd <= scanStart) return results;

            string window = sourceCode.Substring(scanStart, scanEnd - scanStart);

            // Match: new Vector2(X, Y) where X and Y are integer or float literals (with optional f suffix)
            var pattern = new Regex(
                @"new\s+Vector2\s*\(\s*(-?[\d.]+)f?\s*,\s*(-?[\d.]+)f?\s*\)");

            // Also try to detect property context from "PropertyName = new Vector2(...)"
            var ctxPattern = new Regex(
                @"(\w+)\s*=\s*new\s+Vector2\s*\(");

            foreach (Match m in pattern.Matches(window))
            {
                if (!float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float x)) continue;
                if (!float.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float y)) continue;

                // Look for property context
                string propCtx = null;
                var ctxMatch = ctxPattern.Match(window, Math.Max(0, m.Index - 80), Math.Min(80, m.Index));
                foreach (Match cm in ctxPattern.Matches(window))
                {
                    // The "new Vector2(" in the context match should align with this match
                    if (cm.Index + cm.Length >= m.Index && cm.Index <= m.Index)
                    {
                        propCtx = cm.Groups[1].Value;
                        break;
                    }
                }

                results.Add(new ExpressionVector2
                {
                    X = x,
                    Y = y,
                    PropertyContext = propCtx,
                    SourceOffset = scanStart + m.Index,
                    SourceLength = m.Length,
                    LiteralText = m.Value,
                });
            }

            results.Sort((a, b) => a.SourceOffset.CompareTo(b.SourceOffset));
            return results;
        }

        // ── Expression float extraction ──────────────────────────────────────

        /// <summary>
        /// Scans the source code around a sprite's definition for float literal assignments
        /// (e.g. <c>RotationOrScale = 0.8f</c>).  Only matches floats that appear as
        /// property/variable assignments to reduce false positives.
        /// </summary>
        public static List<ExpressionFloat> ExtractFloatLiterals(string sourceCode, int sourceStart, int sourceEnd)
        {
            var results = new List<ExpressionFloat>();
            if (string.IsNullOrEmpty(sourceCode) || sourceStart < 0 || sourceEnd < 0) return results;

            int scanStart = Math.Max(0, sourceStart - 600);
            int scanEnd = Math.Min(sourceCode.Length, sourceEnd);
            if (scanEnd <= scanStart) return results;

            string window = sourceCode.Substring(scanStart, scanEnd - scanStart);

            // Match: PropertyName = <float>f  (requires trailing 'f' and assignment context)
            var pattern = new Regex(
                @"(\w+)\s*=\s*(-?[\d.]+)f\s*[,;\r\n]");

            foreach (Match m in pattern.Matches(window))
            {
                string propName = m.Groups[1].Value;

                // Skip assignments that are part of Vector2/Color constructors by checking
                // if this is inside parens — we only want standalone assignments
                // Simple heuristic: skip if propName is a type keyword
                if (propName == "new" || propName == "var" || propName == "int" ||
                    propName == "float" || propName == "double") continue;

                if (!float.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float val)) continue;

                // The literal is just the number+f portion
                string literalText = m.Groups[2].Value + "f";
                int literalOffset = scanStart + m.Index + m.Value.IndexOf(m.Groups[2].Value);

                results.Add(new ExpressionFloat
                {
                    Value = val,
                    PropertyContext = propName,
                    SourceOffset = literalOffset,
                    SourceLength = literalText.Length,
                    LiteralText = literalText,
                });
            }

            results.Sort((a, b) => a.SourceOffset.CompareTo(b.SourceOffset));
            return results;
        }

        // ── Expression string extraction ─────────────────────────────────────

        /// <summary>
        /// Scans the source code around a sprite's definition for quoted string literals
        /// in property assignments (e.g. <c>Data = "SquareSimple"</c>, <c>FontId = "White"</c>).
        /// </summary>
        public static List<ExpressionString> ExtractStringLiterals(string sourceCode, int sourceStart, int sourceEnd)
        {
            var results = new List<ExpressionString>();
            if (string.IsNullOrEmpty(sourceCode) || sourceStart < 0 || sourceEnd < 0) return results;

            int scanStart = Math.Max(0, sourceStart - 600);
            int scanEnd = Math.Min(sourceCode.Length, sourceEnd);
            if (scanEnd <= scanStart) return results;

            string window = sourceCode.Substring(scanStart, scanEnd - scanStart);

            // Match: PropertyName = "value"  (standard quoted string literal in assignment)
            var pattern = new Regex(
                @"(\w+)\s*=\s*""((?:[^""\\]|\\.)*)""");

            foreach (Match m in pattern.Matches(window))
            {
                string propName = m.Groups[1].Value;
                string rawValue = m.Groups[2].Value;

                // Unescape the string value (basic C# unescaping)
                string value = rawValue
                    .Replace("\\\\", "\x01")    // placeholder for backslash
                    .Replace("\\\"", "\"")
                    .Replace("\\n", "\n")
                    .Replace("\\r", "\r")
                    .Replace("\\t", "\t")
                    .Replace("\x01", "\\");

                // Also handle \uXXXX escapes
                value = Regex.Replace(value, @"\\u([0-9A-Fa-f]{4})",
                    em => ((char)Convert.ToInt32(em.Groups[1].Value, 16)).ToString());

                // The literal includes the quotes
                string literalText = "\"" + rawValue + "\"";
                int literalOffset = scanStart + m.Index + m.Value.IndexOf('"');

                results.Add(new ExpressionString
                {
                    Value = value,
                    PropertyContext = propName,
                    SourceOffset = literalOffset,
                    SourceLength = literalText.Length,
                    LiteralText = literalText,
                });
            }

            results.Sort((a, b) => a.SourceOffset.CompareTo(b.SourceOffset));
            return results;
        }

        // ── Offset-targeted patchers ─────────────────────────────────────────

        /// <summary>
        /// Patches a specific Vector2 literal in OriginalSourceCode at a known offset.
        /// Returns the updated source code, or null if the offset is out of range or text doesn't match.
        /// </summary>
        public static string PatchVector2AtOffset(string sourceCode, ExpressionVector2 expr, float newX, float newY)
        {
            if (sourceCode == null || expr == null) return null;
            if (expr.SourceOffset < 0 || expr.SourceOffset + expr.SourceLength > sourceCode.Length) return null;

            string actual = sourceCode.Substring(expr.SourceOffset, expr.SourceLength);
            if (actual != expr.LiteralText) return null;

            string replacement = $"new Vector2({newX:F1}f, {newY:F1}f)";

            return sourceCode.Substring(0, expr.SourceOffset)
                 + replacement
                 + sourceCode.Substring(expr.SourceOffset + expr.SourceLength);
        }

        /// <summary>
        /// Patches a specific float literal in OriginalSourceCode at a known offset.
        /// Returns the updated source code, or null if the offset is out of range or text doesn't match.
        /// </summary>
        public static string PatchFloatAtOffset(string sourceCode, ExpressionFloat expr, float newValue, int decimals = 4)
        {
            if (sourceCode == null || expr == null) return null;
            if (expr.SourceOffset < 0 || expr.SourceOffset + expr.SourceLength > sourceCode.Length) return null;

            string actual = sourceCode.Substring(expr.SourceOffset, expr.SourceLength);
            if (actual != expr.LiteralText) return null;

            string replacement = newValue.ToString($"F{decimals}") + "f";

            return sourceCode.Substring(0, expr.SourceOffset)
                 + replacement
                 + sourceCode.Substring(expr.SourceOffset + expr.SourceLength);
        }

        /// <summary>
        /// Patches a specific string literal in OriginalSourceCode at a known offset.
        /// Returns the updated source code, or null if the offset is out of range or text doesn't match.
        /// </summary>
        public static string PatchStringAtOffset(string sourceCode, ExpressionString expr, string newValue)
        {
            if (sourceCode == null || expr == null || newValue == null) return null;
            if (expr.SourceOffset < 0 || expr.SourceOffset + expr.SourceLength > sourceCode.Length) return null;

            string actual = sourceCode.Substring(expr.SourceOffset, expr.SourceLength);
            if (actual != expr.LiteralText) return null;

            string replacement = $"\"{Esc(newValue)}\"";

            return sourceCode.Substring(0, expr.SourceOffset)
                 + replacement
                 + sourceCode.Substring(expr.SourceOffset + expr.SourceLength);
        }

        // ── Unified offset management ────────────────────────────────────────

        /// <summary>
        /// Shifts source offsets for all expression literals across all sprites after a patch
        /// at <paramref name="patchedOffset"/> that changed source length by <paramref name="delta"/>.
        /// Also adjusts SourceStart/SourceEnd for sprites after the patch point.
        /// </summary>
        /// <param name="sprites">All sprites in the layout.</param>
        /// <param name="patchedOffset">Character offset where the patch was applied.</param>
        /// <param name="delta">Change in source length (newLength - oldLength). Can be negative.</param>
        /// <param name="excludeLiteral">The literal that was patched (skip it during shifting).</param>
        public static void ShiftExpressionOffsets(
            IList<SpriteEntry> sprites, int patchedOffset, int delta, ExpressionLiteral excludeLiteral = null)
        {
            if (delta == 0 || sprites == null) return;

            foreach (var sprite in sprites)
            {
                // Shift color expression offsets
                if (sprite.ExpressionColors != null)
                {
                    foreach (var ec in sprite.ExpressionColors)
                    {
                        if (ec.SourceOffset > patchedOffset)
                            ec.SourceOffset += delta;
                    }
                }

                // Shift Vector2 expression offsets
                if (sprite.ExpressionVectors != null)
                {
                    foreach (var ev in sprite.ExpressionVectors)
                    {
                        if (ReferenceEquals(ev, excludeLiteral)) continue;
                        if (ev.SourceOffset > patchedOffset)
                            ev.SourceOffset += delta;
                    }
                }

                // Shift float expression offsets
                if (sprite.ExpressionFloats != null)
                {
                    foreach (var ef in sprite.ExpressionFloats)
                    {
                        if (ReferenceEquals(ef, excludeLiteral)) continue;
                        if (ef.SourceOffset > patchedOffset)
                            ef.SourceOffset += delta;
                    }
                }

                // Shift string expression offsets
                if (sprite.ExpressionStrings != null)
                {
                    foreach (var es in sprite.ExpressionStrings)
                    {
                        if (ReferenceEquals(es, excludeLiteral)) continue;
                        if (es.SourceOffset > patchedOffset)
                            es.SourceOffset += delta;
                    }
                }

                // Shift SourceStart/SourceEnd for sprites after the patch point
                if (sprite.SourceStart > patchedOffset)
                {
                    sprite.SourceStart += delta;
                    sprite.SourceEnd += delta;
                }
            }
        }

        private static int FindMatchingBrace(string code, int openPos)
        {
            int depth = 1;
            for (int i = openPos + 1; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static int FindMatchingParen(string code, int openPos)
        {
            int depth = 1;
            for (int i = openPos + 1; i < code.Length; i++)
            {
                if (code[i] == '(') depth++;
                else if (code[i] == ')') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }
    }
}
