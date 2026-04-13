using System;
using System.Collections.Generic;
using System.Text;
using SESpriteLCDLayoutTool.Models;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>Categorises how a sprite changed between two ticks.</summary>
    [Flags]
    public enum SpriteChangeKind
    {
        None       = 0,
        Moved      = 1 << 0,
        Recolored  = 1 << 1,
        Resized    = 1 << 2,
        Rotated    = 1 << 3,
        Rescaled   = 1 << 4,
        TextChanged = 1 << 5,
        Added      = 1 << 6,
        Removed    = 1 << 7,
    }

    /// <summary>
    /// Describes one sprite's change between two snapshots.
    /// </summary>
    public sealed class SpriteChange
    {
        public SpriteChangeKind Kind { get; set; }
        public int Index { get; set; }
        public string DisplayName { get; set; }

        /// <summary>Before state (null when <see cref="SpriteChangeKind.Added"/>).</summary>
        public SpriteSnapshotEntry? Before { get; set; }

        /// <summary>After state (null when <see cref="SpriteChangeKind.Removed"/>).</summary>
        public SpriteSnapshotEntry? After { get; set; }

        public string Summary => FormatSummary();

        private string FormatSummary()
        {
            var parts = new List<string>();

            if ((Kind & SpriteChangeKind.Added) != 0)
                return $"+ ADDED  [{After?.Type}] {DisplayName}";
            if ((Kind & SpriteChangeKind.Removed) != 0)
                return $"- REMOVED  [{Before?.Type}] {DisplayName}";

            if ((Kind & SpriteChangeKind.Moved) != 0 && Before.HasValue && After.HasValue)
                parts.Add($"pos ({Before.Value.X:F1},{Before.Value.Y:F1}) → ({After.Value.X:F1},{After.Value.Y:F1})");

            if ((Kind & SpriteChangeKind.Recolored) != 0 && Before.HasValue && After.HasValue)
                parts.Add($"color ({Before.Value.ColorR},{Before.Value.ColorG},{Before.Value.ColorB},{Before.Value.ColorA}) → ({After.Value.ColorR},{After.Value.ColorG},{After.Value.ColorB},{After.Value.ColorA})");

            if ((Kind & SpriteChangeKind.Resized) != 0 && Before.HasValue && After.HasValue)
                parts.Add($"size ({Before.Value.Width:F1}×{Before.Value.Height:F1}) → ({After.Value.Width:F1}×{After.Value.Height:F1})");

            if ((Kind & SpriteChangeKind.Rotated) != 0 && Before.HasValue && After.HasValue)
                parts.Add($"rot {Before.Value.Rotation:F2} → {After.Value.Rotation:F2}");

            if ((Kind & SpriteChangeKind.Rescaled) != 0 && Before.HasValue && After.HasValue)
                parts.Add($"scale {Before.Value.Scale:F2} → {After.Value.Scale:F2}");

            if ((Kind & SpriteChangeKind.TextChanged) != 0 && Before.HasValue && After.HasValue)
                parts.Add($"text \"{Truncate(Before.Value.Text, 20)}\" → \"{Truncate(After.Value.Text, 20)}\"");

            if (parts.Count == 0)
                return $"  (no visual change)  [{Before?.Type}] {DisplayName}";

            return $"~ {string.Join(", ", parts)}  [{Before?.Type ?? After?.Type}] {DisplayName}";
        }

        private static string Truncate(string s, int max)
        {
            if (s == null) return "(null)";
            return s.Length <= max ? s : s.Substring(0, max - 3) + "...";
        }
    }

    /// <summary>
    /// Compares two sprite snapshots (from different animation ticks) and
    /// produces a list of <see cref="SpriteChange"/> results describing what
    /// moved, recolored, disappeared, or appeared between them.
    /// </summary>
    public static class SnapshotComparer
    {
        private const float PositionEpsilon = 0.5f;
        private const float SizeEpsilon     = 0.5f;
        private const float RotationEpsilon = 0.005f;
        private const float ScaleEpsilon    = 0.005f;

        /// <summary>
        /// Compares two sprite snapshots and returns a list of changes.
        /// Matching is done by index (occurrence order) first, with a fallback
        /// to (Type + DataKey) matching for added/removed detection.
        /// </summary>
        public static List<SpriteChange> Compare(
            SpriteSnapshotEntry[] before,
            SpriteSnapshotEntry[] after)
        {
            if (before == null) before = Array.Empty<SpriteSnapshotEntry>();
            if (after == null) after = Array.Empty<SpriteSnapshotEntry>();

            var changes = new List<SpriteChange>();

            int commonCount = Math.Min(before.Length, after.Length);

            // Compare sprites that exist in both snapshots by index
            for (int i = 0; i < commonCount; i++)
            {
                var b = before[i];
                var a = after[i];
                var kind = DetectChanges(b, a);

                // Only report sprites that actually changed
                if (kind != SpriteChangeKind.None)
                {
                    changes.Add(new SpriteChange
                    {
                        Kind = kind,
                        Index = i,
                        DisplayName = GetDisplayName(b),
                        Before = b,
                        After = a,
                    });
                }
            }

            // Sprites that were removed (exist in before, not in after)
            for (int i = commonCount; i < before.Length; i++)
            {
                changes.Add(new SpriteChange
                {
                    Kind = SpriteChangeKind.Removed,
                    Index = i,
                    DisplayName = GetDisplayName(before[i]),
                    Before = before[i],
                    After = null,
                });
            }

            // Sprites that were added (exist in after, not in before)
            for (int i = commonCount; i < after.Length; i++)
            {
                changes.Add(new SpriteChange
                {
                    Kind = SpriteChangeKind.Added,
                    Index = i,
                    DisplayName = GetDisplayName(after[i]),
                    Before = null,
                    After = after[i],
                });
            }

            return changes;
        }

        /// <summary>
        /// Produces a formatted multi-line summary of all changes.
        /// </summary>
        public static string FormatReport(
            List<SpriteChange> changes,
            int tickA,
            int tickB,
            int totalBeforeCount,
            int totalAfterCount)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"── Snapshot Comparison: Tick {tickA} → Tick {tickB} ──");
            sb.AppendLine($"   Before: {totalBeforeCount} sprites  |  After: {totalAfterCount} sprites");
            sb.AppendLine();

            if (changes.Count == 0)
            {
                sb.AppendLine("   No visual changes detected.");
                return sb.ToString();
            }

            int moved = 0, recolored = 0, resized = 0, added = 0, removed = 0, other = 0;
            foreach (var c in changes)
            {
                if ((c.Kind & SpriteChangeKind.Added) != 0) added++;
                else if ((c.Kind & SpriteChangeKind.Removed) != 0) removed++;
                else
                {
                    if ((c.Kind & SpriteChangeKind.Moved) != 0) moved++;
                    if ((c.Kind & SpriteChangeKind.Recolored) != 0) recolored++;
                    if ((c.Kind & SpriteChangeKind.Resized) != 0) resized++;
                    other++;
                }
            }

            sb.Append("   Summary: ");
            var parts = new List<string>();
            if (moved > 0) parts.Add($"{moved} moved");
            if (recolored > 0) parts.Add($"{recolored} recolored");
            if (resized > 0) parts.Add($"{resized} resized");
            if (added > 0) parts.Add($"{added} added");
            if (removed > 0) parts.Add($"{removed} removed");
            if (other > 0 && moved == 0 && recolored == 0 && resized == 0)
                parts.Add($"{other} modified");
            sb.AppendLine(string.Join(", ", parts));
            sb.AppendLine();

            foreach (var c in changes)
            {
                sb.AppendLine($"   [{c.Index}] {c.Summary}");
            }

            return sb.ToString();
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static SpriteChangeKind DetectChanges(SpriteSnapshotEntry b, SpriteSnapshotEntry a)
        {
            var kind = SpriteChangeKind.None;

            if (Math.Abs(b.X - a.X) > PositionEpsilon || Math.Abs(b.Y - a.Y) > PositionEpsilon)
                kind |= SpriteChangeKind.Moved;

            if (b.ColorR != a.ColorR || b.ColorG != a.ColorG || b.ColorB != a.ColorB || b.ColorA != a.ColorA)
                kind |= SpriteChangeKind.Recolored;

            if (Math.Abs(b.Width - a.Width) > SizeEpsilon || Math.Abs(b.Height - a.Height) > SizeEpsilon)
                kind |= SpriteChangeKind.Resized;

            if (Math.Abs(b.Rotation - a.Rotation) > RotationEpsilon)
                kind |= SpriteChangeKind.Rotated;

            if (Math.Abs(b.Scale - a.Scale) > ScaleEpsilon)
                kind |= SpriteChangeKind.Rescaled;

            if (!string.Equals(b.Text, a.Text, StringComparison.Ordinal))
                kind |= SpriteChangeKind.TextChanged;

            return kind;
        }

        private static string GetDisplayName(SpriteSnapshotEntry sp)
        {
            if (sp.Type == SpriteEntryType.Text)
            {
                string t = sp.Text;
                if (t != null && t.Length > 20)
                    t = t.Substring(0, 17) + "...";
                return $"TEXT \"{t}\"";
            }
            return sp.SpriteName ?? "(unnamed)";
        }
    }
}
