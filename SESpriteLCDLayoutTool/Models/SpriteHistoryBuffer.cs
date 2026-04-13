using System;
using System.Collections.Generic;
using System.Drawing;

namespace SESpriteLCDLayoutTool.Models
{
    /// <summary>
    /// Lightweight snapshot of a single sprite's visual state at a specific tick.
    /// Stored in <see cref="SpriteHistoryBuffer"/> for snapshot comparison.
    /// </summary>
    public struct SpriteSnapshotEntry
    {
        public SpriteEntryType Type;
        public string SpriteName;
        public string Text;
        public float X;
        public float Y;
        public float Width;
        public float Height;
        public int ColorR, ColorG, ColorB, ColorA;
        public float Rotation;
        public float Scale;
        public SpriteTextAlignment Alignment;
        public string FontId;

        public Color Color => Color.FromArgb(ColorA, ColorR, ColorG, ColorB);

        /// <summary>
        /// The display identity of this sprite — texture name for TEXTURE sprites,
        /// text content for TEXT sprites.
        /// </summary>
        public string DataKey => Type == SpriteEntryType.Text ? Text : SpriteName;

        public static SpriteSnapshotEntry FromSpriteEntry(SpriteEntry sp)
        {
            return new SpriteSnapshotEntry
            {
                Type = sp.Type,
                SpriteName = sp.SpriteName,
                Text = sp.Text,
                X = sp.X,
                Y = sp.Y,
                Width = sp.Width,
                Height = sp.Height,
                ColorR = sp.ColorR,
                ColorG = sp.ColorG,
                ColorB = sp.ColorB,
                ColorA = sp.ColorA,
                Rotation = sp.Rotation,
                Scale = sp.Scale,
                Alignment = sp.Alignment,
                FontId = sp.FontId,
            };
        }
    }

    /// <summary>
    /// Fixed-capacity ring buffer that stores per-tick sprite snapshots.
    /// Used by the Snapshot Comparison feature to let users bookmark two
    /// ticks and compare what sprites moved, changed color, or disappeared.
    /// </summary>
    public sealed class SpriteHistoryBuffer
    {
        private readonly int _capacity;
        private readonly SpriteSnapshotEntry[][] _snapshots;
        private readonly int[] _ticks;
        private int _head;
        private int _count;

        /// <summary>Smallest tick number still in the buffer.</summary>
        public int MinTick => _count == 0 ? 0 : _ticks[OldestIndex];

        /// <summary>Largest tick number in the buffer.</summary>
        public int MaxTick => _count == 0 ? 0 : _ticks[NewestIndex];

        /// <summary>Number of snapshots stored.</summary>
        public int Count => _count;

        // ── Bookmarks ──────────────────────────────────────────────────────

        /// <summary>Tick number for bookmark A (-1 = not set).</summary>
        public int BookmarkTickA { get; private set; } = -1;

        /// <summary>Tick number for bookmark B (-1 = not set).</summary>
        public int BookmarkTickB { get; private set; } = -1;

        public SpriteHistoryBuffer(int capacity = 500)
        {
            _capacity = Math.Max(16, capacity);
            _snapshots = new SpriteSnapshotEntry[_capacity][];
            _ticks = new int[_capacity];
        }

        /// <summary>
        /// Records a snapshot of sprite states for the given tick.
        /// </summary>
        public void Record(int tick, List<SpriteEntry> sprites)
        {
            if (sprites == null) return;

            var snap = new SpriteSnapshotEntry[sprites.Count];
            for (int i = 0; i < sprites.Count; i++)
                snap[i] = SpriteSnapshotEntry.FromSpriteEntry(sprites[i]);

            _ticks[_head] = tick;
            _snapshots[_head] = snap;
            _head = (_head + 1) % _capacity;
            if (_count < _capacity) _count++;
        }

        /// <summary>
        /// Returns the sprite snapshot closest to the requested tick,
        /// or null if the buffer is empty.
        /// </summary>
        public SpriteSnapshotEntry[] GetSnapshot(int tick)
        {
            if (_count == 0) return null;

            int bestIdx = -1;
            int bestDist = int.MaxValue;
            for (int i = 0; i < _count; i++)
            {
                int idx = (OldestIndex + i) % _capacity;
                int dist = Math.Abs(_ticks[idx] - tick);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = idx;
                }
                if (dist == 0) break;
            }

            return bestIdx >= 0 ? _snapshots[bestIdx] : null;
        }

        /// <summary>Sets bookmark A to the given tick.</summary>
        public void SetBookmarkA(int tick) => BookmarkTickA = tick;

        /// <summary>Sets bookmark B to the given tick.</summary>
        public void SetBookmarkB(int tick) => BookmarkTickB = tick;

        /// <summary>Clears both bookmarks.</summary>
        public void ClearBookmarks()
        {
            BookmarkTickA = -1;
            BookmarkTickB = -1;
        }

        /// <summary>Clears all recorded history and bookmarks.</summary>
        public void Clear()
        {
            _head = 0;
            _count = 0;
            Array.Clear(_snapshots, 0, _snapshots.Length);
            ClearBookmarks();
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private int OldestIndex => _count < _capacity ? 0 : _head;
        private int NewestIndex => (_head - 1 + _capacity) % _capacity;
    }
}
