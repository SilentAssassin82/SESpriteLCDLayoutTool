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
        }

        /// <summary>
        /// Merges runtime positions from <paramref name="snapshotSprites"/> into
        /// <paramref name="codeSprites"/>.  Matching is done by (Type + Data) in
        /// occurrence order, so the first TEXTURE "SquareSimple" in the code is
        /// matched to the first TEXTURE "SquareSimple" in the snapshot, etc.
        /// 
        /// Only position and size are transferred from the snapshot; colour,
        /// font, text content, rotation/scale, source tracking, and baselines
        /// are left untouched on the code sprites.
        /// </summary>
        public static MergeResult Merge(List<SpriteEntry> codeSprites,
                                        List<SpriteEntry> snapshotSprites)
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

            foreach (var code in codeSprites)
            {
                if (code.IsReferenceLayout) continue; // don't touch reference sprites

                string key = MakeKey(code);
                if (pool.TryGetValue(key, out var queue) && queue.Count > 0)
                {
                    var snap = queue.Dequeue();
                    ApplyPosition(code, snap);
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
                    ApplyPosition(codeSprites[i], snapshotSprites[i]);
                    matched++;
                }
                unmatched = Math.Max(0, codeSprites.Count - matched);
            }

            result.Matched = matched;
            result.Unmatched = unmatched;
            result.Summary = $"Matched {matched} sprite(s) to snapshot positions."
                + (unmatched > 0 ? $"  {unmatched} sprite(s) had no snapshot match." : "");
            return result;
        }

        /// <summary>
        /// Applies the position and size from a snapshot sprite to a code sprite,
        /// then refreshes the import baseline so the round-trip diff ignores
        /// the position change (it came from runtime, not user editing).
        /// </summary>
        private static void ApplyPosition(SpriteEntry code, SpriteEntry snapshot)
        {
            code.X = snapshot.X;
            code.Y = snapshot.Y;
            code.Width = snapshot.Width;
            code.Height = snapshot.Height;

            // Update the import baseline so the round-trip diff does NOT
            // treat the snapshot positions as user edits.  The baseline
            // records "what it looked like at import time" — so we update
            // the position/size fields to match the snapshot values.
            if (code.ImportBaseline != null)
            {
                code.ImportBaseline.X = snapshot.X;
                code.ImportBaseline.Y = snapshot.Y;
                code.ImportBaseline.Width = snapshot.Width;
                code.ImportBaseline.Height = snapshot.Height;
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
