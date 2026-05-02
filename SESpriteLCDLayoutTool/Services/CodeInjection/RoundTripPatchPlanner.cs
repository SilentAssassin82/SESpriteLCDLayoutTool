using System;
using System.Collections.Generic;
using SESpriteLCDLayoutTool.Models;

namespace SESpriteLCDLayoutTool.Services.CodeInjection
{
    /// <summary>
    /// Builds an <see cref="InjectionPlan"/> of <see cref="PropertyPatchOp"/>s for
    /// the round-trip "user edited a sprite on the canvas, push the literal change
    /// back into the original source code" workflow.
    ///
    /// This is the central-injector counterpart of the literal-replacement passes
    /// in <see cref="CodeGenerator.PatchOriginalSource"/>.  Slice 1 covers only
    /// the trivially-mappable string properties:
    ///   - <see cref="SpriteEntry.Text"/>
    ///   - <see cref="SpriteEntry.SpriteName"/> (texture sprites)
    ///   - <see cref="SpriteEntry.FontId"/>
    /// when the baseline value appears as an exact <c>"..."</c> regular string
    /// literal inside the sprite's own source chunk.  Anything else (verbatim,
    /// interpolated, helper call sites, colors, vectors, floats, baseline-only
    /// sprites) is reported in <see cref="PlannerReport.Uncovered"/> so the
    /// caller can fall back to the legacy patcher for those cases.
    ///
    /// The planner does NOT mutate the source — it only emits ops in absolute
    /// pre-edit coordinates so <see cref="RoslynCodeInjector"/> can apply them
    /// and report <see cref="SourceEdit"/>s to shift sprite offsets.
    /// </summary>
    public static class RoundTripPatchPlanner
    {
        /// <summary>One uncovered diff the planner could not express as a span op.</summary>
        public sealed class UncoveredChange
        {
            public SpriteEntry Sprite;
            public string Property;   // "Text" | "SpriteName" | "FontId" | "Color" | ...
            public string Reason;     // human-readable diagnostic
        }

        /// <summary>One diff that WAS expressed as a PropertyPatchOp.  Used by
        /// the round-trip wire-in to update ImportBaseline values after the
        /// injector applies its patches, so the legacy fallback sees no diff
        /// and doesn't double-patch.</summary>
        public sealed class CoveredChange
        {
            public SpriteEntry Sprite;
            public string Property;   // "Text" | "SpriteName" | "FontId" | "Color"
            public string NewValue;   // (string properties) value that was written into source
            // Color baselines are integer channels so we carry them separately
            // to avoid a string round-trip on the consumer side.
            public int NewColorR;
            public int NewColorG;
            public int NewColorB;
            public int NewColorA;
            // Float-valued properties (Position/Size/Rotation/Scale).
            public float NewFloatX;
            public float NewFloatY;
            public float NewFloatScalar;
        }

        public sealed class PlannerReport
        {
            public InjectionPlan Plan { get; } = new InjectionPlan();
            public List<UncoveredChange> Uncovered { get; } = new List<UncoveredChange>();
            public List<CoveredChange> Covered { get; } = new List<CoveredChange>();

            /// <summary>True when every detected diff was expressed as a PropertyPatchOp.</summary>
            public bool FullyCovered => Uncovered.Count == 0;
        }

        public static PlannerReport BuildPlan(LcdLayout layout)
        {
            var report = new PlannerReport();
            if (layout == null || string.IsNullOrEmpty(layout.OriginalSourceCode)) return report;

            string original = layout.OriginalSourceCode;

            foreach (var sp in layout.Sprites)
            {
                if (sp == null) continue;
                if (sp.IsReferenceLayout) continue;
                if (sp.ImportBaseline == null) continue;

                // Slice 1 only handles in-chunk literal patches.  Baseline-only
                // sprites (no SourceStart/SourceEnd) are routed to legacy via
                // the Uncovered list.
                if (sp.SourceStart < 0 || sp.SourceEnd < 0)
                {
                    AddUncoveredForChangedStrings(sp, report, "no source chunk (baseline-only sprite)");
                    continue;
                }
                if (sp.SourceStart >= original.Length || sp.SourceEnd > original.Length ||
                    sp.SourceEnd < sp.SourceStart)
                {
                    AddUncoveredForChangedStrings(sp, report, "source chunk out of range");
                    continue;
                }

                int chunkStart = sp.SourceStart;
                int chunkEnd = sp.SourceEnd;
                var bl = sp.ImportBaseline;

                // Text (text sprites only)
                if (sp.Type == SpriteEntryType.Text &&
                    !StringEquals(bl.Text, sp.Text) &&
                    bl.Text != null && sp.Text != null)
                {
                    TryAddStringPatch(report, sp, "Text",
                        original, chunkStart, chunkEnd, bl.Text, sp.Text);
                }

                // SpriteName (texture sprites only)
                if (sp.Type == SpriteEntryType.Texture &&
                    !StringEquals(bl.SpriteName, sp.SpriteName) &&
                    bl.SpriteName != null && sp.SpriteName != null)
                {
                    TryAddStringPatch(report, sp, "SpriteName",
                        original, chunkStart, chunkEnd, bl.SpriteName, sp.SpriteName);
                }

                // FontId (both types)
                if (!StringEquals(bl.FontId, sp.FontId) &&
                    bl.FontId != null && sp.FontId != null)
                {
                    TryAddStringPatch(report, sp, "FontId",
                        original, chunkStart, chunkEnd, bl.FontId, sp.FontId);
                }

                // Color: planner owns `new Color(R,G,B[,A])` and named colors
                // (Color.White etc) when the baseline form appears exactly once
                // inside the chunk.  Anything else (expressions, helper calls,
                // duplicates) routes to legacy.
                if (bl.ColorR != sp.ColorR || bl.ColorG != sp.ColorG ||
                    bl.ColorB != sp.ColorB || bl.ColorA != sp.ColorA)
                {
                    TryAddColorPatch(report, sp, original, chunkStart, chunkEnd);
                }
                if (Math.Abs(bl.X - sp.X) > 0.05f || Math.Abs(bl.Y - sp.Y) > 0.05f)
                {
                    TryAddVector2Patch(report, sp, "Position", original, chunkStart, chunkEnd, sp.X, sp.Y);
                }
                if (sp.Type == SpriteEntryType.Texture &&
                    (Math.Abs(bl.Width - sp.Width) > 0.05f || Math.Abs(bl.Height - sp.Height) > 0.05f))
                {
                    TryAddVector2Patch(report, sp, "Size", original, chunkStart, chunkEnd, sp.Width, sp.Height);
                }
                if (sp.Type == SpriteEntryType.Texture && Math.Abs(bl.Rotation - sp.Rotation) > 0.0005f)
                {
                    TryAddFloatPatch(report, sp, "Rotation", "RotationOrScale",
                        original, chunkStart, chunkEnd, sp.Rotation, 4);
                }
                else if (sp.Type == SpriteEntryType.Text && Math.Abs(bl.Scale - sp.Scale) > 0.005f)
                {
                    TryAddFloatPatch(report, sp, "Scale", "RotationOrScale",
                        original, chunkStart, chunkEnd, sp.Scale, 2);
                }
            }

            return report;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static void AddUncoveredForChangedStrings(SpriteEntry sp, PlannerReport report, string reason)
        {
            var bl = sp.ImportBaseline;
            if (bl == null) return;
            if (sp.Type == SpriteEntryType.Text && !StringEquals(bl.Text, sp.Text) &&
                bl.Text != null && sp.Text != null)
                report.Uncovered.Add(new UncoveredChange { Sprite = sp, Property = "Text", Reason = reason });
            if (sp.Type == SpriteEntryType.Texture && !StringEquals(bl.SpriteName, sp.SpriteName) &&
                bl.SpriteName != null && sp.SpriteName != null)
                report.Uncovered.Add(new UncoveredChange { Sprite = sp, Property = "SpriteName", Reason = reason });
            if (!StringEquals(bl.FontId, sp.FontId) && bl.FontId != null && sp.FontId != null)
                report.Uncovered.Add(new UncoveredChange { Sprite = sp, Property = "FontId", Reason = reason });
        }

        /// <summary>
        /// Looks for an exact <c>"oldValue"</c> regular-string literal inside
        /// the sprite's source chunk.  Emits a <see cref="PropertyPatchOp"/>
        /// covering the literal (including its surrounding quotes) when
        /// EXACTLY one such match exists; otherwise routes the change to the
        /// uncovered list so legacy can handle it.
        /// </summary>
        private static void TryAddStringPatch(
            PlannerReport report, SpriteEntry sp, string property,
            string original, int chunkStart, int chunkEnd,
            string oldValue, string newValue)
        {
            // We require a value that is safe to express as a plain "..." literal
            // both before and after the edit; anything containing characters that
            // would need escaping is routed to legacy until we add an escape pass.
            if (NeedsEscaping(oldValue) || NeedsEscaping(newValue))
            {
                report.Uncovered.Add(new UncoveredChange
                {
                    Sprite = sp, Property = property,
                    Reason = "value requires literal escaping (planner is escape-free in slice 1)"
                });
                return;
            }

            string needle = "\"" + oldValue + "\"";
            int chunkLen = chunkEnd - chunkStart;
            int searchFrom = chunkStart;
            int firstHit = -1;
            int hitCount = 0;
            while (true)
            {
                int idx = original.IndexOf(needle, searchFrom, StringComparison.Ordinal);
                if (idx < 0 || idx + needle.Length > chunkStart + chunkLen) break;
                hitCount++;
                if (firstHit < 0) firstHit = idx;
                searchFrom = idx + needle.Length;
                if (hitCount > 1) break;
            }

            if (hitCount != 1)
            {
                report.Uncovered.Add(new UncoveredChange
                {
                    Sprite = sp, Property = property,
                    Reason = hitCount == 0
                        ? "literal not found as exact \"...\" match in chunk (interpolated/expression?)"
                        : "literal appears multiple times in chunk; ambiguous"
                });
                return;
            }

            report.Plan.PropertyPatches.Add(new PropertyPatchOp
            {
                Action = InjectionAction.Update,
                Anchor = AnchorFor(sp, property),
                Start = firstHit,
                End = firstHit + needle.Length,
                ExpectedOldText = needle,
                NewText = "\"" + newValue + "\""
            });
            report.Covered.Add(new CoveredChange
            {
                Sprite = sp, Property = property, NewValue = newValue
            });
        }

        /// <summary>
        /// Looks for the baseline color in either <c>new Color(R, G, B[, A])</c>
        /// constructor form or a named-color form (<c>Color.White</c> etc.) inside
        /// the sprite's source chunk.  Emits a single <see cref="PropertyPatchOp"/>
        /// covering the matched expression when EXACTLY one such match exists;
        /// otherwise the change is routed to the uncovered list so legacy can
        /// handle it.
        /// </summary>
        private static void TryAddColorPatch(
            PlannerReport report, SpriteEntry sp,
            string original, int chunkStart, int chunkEnd)
        {
            var bl = sp.ImportBaseline;
            string newColorText = sp.ColorA != 255
                ? "new Color(" + sp.ColorR + ", " + sp.ColorG + ", " + sp.ColorB + ", " + sp.ColorA + ")"
                : "new Color(" + sp.ColorR + ", " + sp.ColorG + ", " + sp.ColorB + ")";

            // Bound the search strictly to the sprite's chunk so we never
            // accidentally rewrite a color expression belonging to another
            // sprite or unrelated code.
            string chunk = original.Substring(chunkStart, chunkEnd - chunkStart);

            // 1) Constructor form: `new Color ( R , G , B [, A] )` — same regex
            //    shape the legacy patcher uses, but we run it against the chunk
            //    only and require exactly one match.
            var ctorPattern = new System.Text.RegularExpressions.Regex(
                @"new\s+Color\s*\(\s*" + bl.ColorR + @"\s*,\s*" + bl.ColorG +
                @"\s*,\s*" + bl.ColorB + @"(?:\s*,\s*\d+)?\s*\)");
            var ctorMatches = ctorPattern.Matches(chunk);
            if (ctorMatches.Count == 1)
            {
                var m = ctorMatches[0];
                EmitColorPatch(report, sp, chunkStart + m.Index, m.Length, m.Value, newColorText);
                return;
            }
            if (ctorMatches.Count > 1)
            {
                report.Uncovered.Add(new UncoveredChange
                {
                    Sprite = sp, Property = "Color",
                    Reason = "ambiguous: baseline constructor color appears multiple times in chunk"
                });
                return;
            }

            // 2) Named-color form: only when the baseline maps to a known name AND
            //    the literal `Color.<Name>` appears exactly once in the chunk.
            string namedColor = MatchNamedColor(bl);
            if (namedColor != null)
            {
                int firstHit = chunk.IndexOf(namedColor, StringComparison.Ordinal);
                int secondHit = firstHit < 0 ? -1
                    : chunk.IndexOf(namedColor, firstHit + namedColor.Length, StringComparison.Ordinal);
                if (firstHit >= 0 && secondHit < 0)
                {
                    EmitColorPatch(report, sp, chunkStart + firstHit, namedColor.Length, namedColor, newColorText);
                    return;
                }
                if (firstHit >= 0)
                {
                    report.Uncovered.Add(new UncoveredChange
                    {
                        Sprite = sp, Property = "Color",
                        Reason = "ambiguous: baseline named color appears multiple times in chunk"
                    });
                    return;
                }
            }

            report.Uncovered.Add(new UncoveredChange
            {
                Sprite = sp, Property = "Color",
                Reason = "color is an expression / not expressible as constructor or named literal"
            });
        }

        private static void EmitColorPatch(
            PlannerReport report, SpriteEntry sp,
            int absoluteStart, int oldLength, string expectedOldText, string newColorText)
        {
            report.Plan.PropertyPatches.Add(new PropertyPatchOp
            {
                Action = InjectionAction.Update,
                Anchor = AnchorFor(sp, "Color"),
                Start = absoluteStart,
                End = absoluteStart + oldLength,
                ExpectedOldText = expectedOldText,
                NewText = newColorText
            });
            report.Covered.Add(new CoveredChange
            {
                Sprite = sp,
                Property = "Color",
                NewColorR = sp.ColorR,
                NewColorG = sp.ColorG,
                NewColorB = sp.ColorB,
                NewColorA = sp.ColorA
            });
        }

        /// <summary>
        /// Looks for a literal <c>{propName} = new Vector2(x, y)</c> assignment
        /// inside the sprite's source chunk (same regex shape as the legacy
        /// patcher). Emits a <see cref="PropertyPatchOp"/> when exactly one
        /// such assignment exists; otherwise routes to legacy via
        /// <see cref="UncoveredChange"/>. Output format mirrors the legacy
        /// patcher byte-for-byte (<c>{x:F1}f, {y:F1}f</c>).
        /// </summary>
        private static void TryAddVector2Patch(
            PlannerReport report, SpriteEntry sp, string property,
            string original, int chunkStart, int chunkEnd,
            float newX, float newY)
        {
            string chunk = original.Substring(chunkStart, chunkEnd - chunkStart);
            var pattern = new System.Text.RegularExpressions.Regex(
                @"(" + System.Text.RegularExpressions.Regex.Escape(property) +
                @"\s*=\s*new\s+Vector2\s*\(\s*)(-?[\d.]+f?\s*,\s*-?[\d.]+f?)(\s*\))");
            var matches = pattern.Matches(chunk);
            if (matches.Count != 1)
            {
                report.Uncovered.Add(new UncoveredChange
                {
                    Sprite = sp, Property = property,
                    Reason = matches.Count == 0
                        ? "not a literal Vector2 assignment in chunk (expression?)"
                        : "ambiguous: literal Vector2 assignment appears multiple times in chunk"
                });
                return;
            }

            var m = matches[0];
            var pairGroup = m.Groups[2];
            int absStart = chunkStart + pairGroup.Index;
            string newPair = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:F1}f, {1:F1}f", newX, newY);

            report.Plan.PropertyPatches.Add(new PropertyPatchOp
            {
                Action = InjectionAction.Update,
                Anchor = AnchorFor(sp, property),
                Start = absStart,
                End = absStart + pairGroup.Length,
                ExpectedOldText = pairGroup.Value,
                NewText = newPair
            });
            report.Covered.Add(new CoveredChange
            {
                Sprite = sp,
                Property = property,
                NewFloatX = newX,
                NewFloatY = newY
            });
        }

        /// <summary>
        /// Looks for a literal <c>{propName} = 0.5000f[,;]</c> assignment in the
        /// sprite's source chunk (same regex shape as the legacy patcher).
        /// Emits a <see cref="PropertyPatchOp"/> when exactly one such
        /// assignment exists; otherwise routes to legacy via
        /// <see cref="UncoveredChange"/>. Output format mirrors the legacy
        /// patcher byte-for-byte (F{decimals}f, invariant culture).
        /// </summary>
        private static void TryAddFloatPatch(
            PlannerReport report, SpriteEntry sp, string property, string sourcePropName,
            string original, int chunkStart, int chunkEnd,
            float newValue, int decimals)
        {
            string chunk = original.Substring(chunkStart, chunkEnd - chunkStart);
            var pattern = new System.Text.RegularExpressions.Regex(
                @"(" + System.Text.RegularExpressions.Regex.Escape(sourcePropName) +
                @"\s*=\s*)(-?[\d.]+f)(\s*[,;])");
            var matches = pattern.Matches(chunk);
            if (matches.Count != 1)
            {
                report.Uncovered.Add(new UncoveredChange
                {
                    Sprite = sp, Property = property,
                    Reason = matches.Count == 0
                        ? "not a literal float assignment in chunk (expression?)"
                        : "ambiguous: literal float assignment appears multiple times in chunk"
                });
                return;
            }

            var m = matches[0];
            var litGroup = m.Groups[2];
            int absStart = chunkStart + litGroup.Index;
            string newLit = newValue.ToString("F" + decimals,
                System.Globalization.CultureInfo.InvariantCulture) + "f";

            report.Plan.PropertyPatches.Add(new PropertyPatchOp
            {
                Action = InjectionAction.Update,
                Anchor = AnchorFor(sp, property),
                Start = absStart,
                End = absStart + litGroup.Length,
                ExpectedOldText = litGroup.Value,
                NewText = newLit
            });
            report.Covered.Add(new CoveredChange
            {
                Sprite = sp,
                Property = property,
                NewFloatScalar = newValue
            });
        }

        /// <summary>Returns the C# named color expression that matches the baseline values, or null.</summary>
        private static string MatchNamedColor(SpriteEntry bl)
        {
            if (bl.ColorA == 255)
            {
                if (bl.ColorR == 255 && bl.ColorG == 255 && bl.ColorB == 255) return "Color.White";
                if (bl.ColorR == 0   && bl.ColorG == 0   && bl.ColorB == 0)   return "Color.Black";
                if (bl.ColorR == 255 && bl.ColorG == 0   && bl.ColorB == 0)   return "Color.Red";
                if (bl.ColorR == 0   && bl.ColorG == 255 && bl.ColorB == 0)   return "Color.Green";
                if (bl.ColorR == 0   && bl.ColorG == 0   && bl.ColorB == 255) return "Color.Blue";
                if (bl.ColorR == 255 && bl.ColorG == 255 && bl.ColorB == 0)   return "Color.Yellow";
            }
            if (bl.ColorR == 0 && bl.ColorG == 0 && bl.ColorB == 0 && bl.ColorA == 0)
                return "Color.Transparent";
            return null;
        }

        private static string AnchorFor(SpriteEntry sp, string property)
        {
            // Anchor is purely diagnostic; identity uniqueness is guaranteed by
            // (Anchor, Start, End) so include the source offset to disambiguate
            // between two sprites that share the same name.
            string name = !string.IsNullOrEmpty(sp.SpriteName) ? sp.SpriteName
                       : !string.IsNullOrEmpty(sp.Text) ? sp.Text
                       : "sprite";
            return name + ":" + property;
        }

        private static bool StringEquals(string a, string b) =>
            string.Equals(a, b, StringComparison.Ordinal);

        private static bool NeedsEscaping(string s)
        {
            if (s == null) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' || c == '\\' || c == '\r' || c == '\n' || c == '\t') return true;
            }
            return false;
        }
    }
}
