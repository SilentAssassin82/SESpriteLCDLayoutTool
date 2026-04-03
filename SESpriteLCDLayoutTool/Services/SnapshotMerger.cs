using System;
using System.Collections.Generic;
using SESpriteLCDLayoutTool.Models;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Merges runtime snapshot sprite positions into code-imported sprites.
    /// The snapshot provides actual pixel positions resolved at runtime,
    /// which are applied to the code-imported sprites so the visual editor
    /// shows the true layout.  Source tracking and import baselines are
    /// preserved so the round-trip code generator can still patch only
    /// the properties the user actually changes (colours, textures, fonts).
    /// </summary>
    public static class SnapshotMerger
    {
        /// <summary>
        /// Result of a snapshot merge operation.
        /// </summary>
        public sealed class MergeResult
        {
            public int Matched;
            public int Unmatched;
            public string Summary;
            /// <summary>Snapshot sprites that had no matching code sprite (loop-generated extras).</summary>
            public List<SpriteEntry> UnmatchedSnapshots = new List<SpriteEntry>();
        }

        /// <summary>
        /// Merges runtime positions from <paramref name="snapshotSprites"/> into
        /// <paramref name="codeSprites"/>.  Matching is done by (Type + Data) in
        /// occurrence order, so the first TEXTURE "SquareSimple" in the code is
        /// matched to the first TEXTURE "SquareSimple" in the snapshot, etc.
        /// 
        /// Position and size are always transferred from the snapshot.  When
        /// <paramref name="applyColors"/> is true, colour components are also
        /// transferred and the import baseline is updated accordingly so the
        /// round-trip diff treats the live colour as the new baseline.
        /// </summary>
        public static MergeResult Merge(List<SpriteEntry> codeSprites,
                                        List<SpriteEntry> snapshotSprites,
                                        bool applyColors = false)
        {
            var result = new MergeResult();
            if (codeSprites == null || snapshotSprites == null ||
                codeSprites.Count == 0 || snapshotSprites.Count == 0)
            {
                result.Summary = "Nothing to merge — one or both sprite lists are empty.";
                return result;
            }

            // Build a consumption index for snapshot sprites keyed by (Type, Data).
            // Each key maps to a queue of snapshot sprites so duplicate Data values
            // are matched in order.
            var pool = new Dictionary<string, Queue<SpriteEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var snap in snapshotSprites)
            {
                string key = MakeKey(snap);
                if (!pool.TryGetValue(key, out var queue))
                {
                    queue = new Queue<SpriteEntry>();
                    pool[key] = queue;
                }
                queue.Enqueue(snap);
            }

            int matched = 0;
            int unmatched = 0;
            var matchedSnapshots = new HashSet<SpriteEntry>();

            foreach (var code in codeSprites)
            {
                if (code.IsReferenceLayout) continue; // don't touch reference sprites

                string key = MakeKey(code);
                if (pool.TryGetValue(key, out var queue) && queue.Count > 0)
                {
                    var snap = queue.Dequeue();
                    ApplyPosition(code, snap, applyColors);
                    matchedSnapshots.Add(snap);
                    matched++;
                }
                else
                {
                    unmatched++;
                }
            }

            // Fall back to positional (index) matching when keyed matching
            // yielded zero results — this handles generated/expression Data values.
            if (matched == 0 && unmatched > 0)
            {
                int count = Math.Min(codeSprites.Count, snapshotSprites.Count);
                for (int i = 0; i < count; i++)
                {
                    if (codeSprites[i].IsReferenceLayout) continue;
                    ApplyPosition(codeSprites[i], snapshotSprites[i], applyColors);
                    matchedSnapshots.Add(snapshotSprites[i]);
                    matched++;
                }
                unmatched = Math.Max(0, codeSprites.Count - matched);
            }

            foreach (var snap in snapshotSprites)
                if (!matchedSnapshots.Contains(snap))
                    result.UnmatchedSnapshots.Add(snap);

            result.Matched = matched;
            result.Unmatched = unmatched;
            result.Summary = $"Matched {matched} sprite(s) to snapshot positions."
                + (unmatched > 0 ? $"  {unmatched} sprite(s) had no snapshot match." : "");
            return result;
        }

        /// <summary>
        /// Applies the position and size from a snapshot sprite to a code sprite,
        /// then refreshes the import baseline so the round-trip diff ignores
        /// the position change (it came from runtime, not user editing).  When
        /// <paramref name="applyColors"/> is true, colour components are also
        /// applied — but only if the user has not manually changed the colour
        /// (current colour still matches the baseline).  The baseline colour is
        /// always advanced to the live frame value so the round-trip diff only
        /// fires for colours the user explicitly edits.
        /// </summary>
        private static void ApplyPosition(SpriteEntry code, SpriteEntry snapshot, bool applyColors = false)
        {
            code.X = snapshot.X;
            code.Y = snapshot.Y;
            code.Width = snapshot.Width;
            code.Height = snapshot.Height;

            if (applyColors)
            {
                // Preserve user-edited colours across pause/resume cycles.
                // If the current colour still matches the baseline the user hasn't
                // touched it, so it is safe to apply the live frame colour.
                bool colourUnchanged = code.ImportBaseline == null
                    || (code.ColorR == code.ImportBaseline.ColorR
                        && code.ColorG == code.ImportBaseline.ColorG
                        && code.ColorB == code.ImportBaseline.ColorB
                        && code.ColorA == code.ImportBaseline.ColorA);

                if (colourUnchanged)
                {
                    code.ColorR = snapshot.ColorR;
                    code.ColorG = snapshot.ColorG;
                    code.ColorB = snapshot.ColorB;
                    code.ColorA = snapshot.ColorA;
                }
            }

            // Update the import baseline so the round-trip diff does NOT
            // treat the snapshot positions (or live colours) as user edits.
            if (code.ImportBaseline != null)
            {
                code.ImportBaseline.X = snapshot.X;
                code.ImportBaseline.Y = snapshot.Y;
                code.ImportBaseline.Width = snapshot.Width;
                code.ImportBaseline.Height = snapshot.Height;

                if (applyColors)
                {
                    // Always advance the baseline colour to match the live frame
                    // so the diff only fires for colours the user changed.
                    code.ImportBaseline.ColorR = snapshot.ColorR;
                    code.ImportBaseline.ColorG = snapshot.ColorG;
                    code.ImportBaseline.ColorB = snapshot.ColorB;
                    code.ImportBaseline.ColorA = snapshot.ColorA;
                }
            }
        }

        private static string MakeKey(SpriteEntry sp)
        {
            string data = sp.Type == SpriteEntryType.Text
                ? (sp.Text ?? "")
                : (sp.SpriteName ?? "");
            return $"{sp.Type}|{data}";
        }
    }
}
