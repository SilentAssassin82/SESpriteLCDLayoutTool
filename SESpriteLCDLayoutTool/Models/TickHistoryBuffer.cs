using System;
using System.Collections.Generic;

namespace SESpriteLCDLayoutTool.Models
{
    /// <summary>
    /// Fixed-capacity ring buffer that stores per-tick snapshots of runner
    /// field values.  Used by the timeline scrubber to let users inspect
    /// historical state without re-executing.
    /// </summary>
    public sealed class TickHistoryBuffer
    {
        private readonly int _capacity;
        private readonly Dictionary<string, object>[] _snapshots;
        private readonly int[] _ticks;
        private int _head;   // next write position
        private int _count;  // items currently stored

        /// <summary>Smallest tick number still in the buffer.</summary>
        public int MinTick => _count == 0 ? 0 : _ticks[OldestIndex];

        /// <summary>Largest tick number in the buffer.</summary>
        public int MaxTick => _count == 0 ? 0 : _ticks[NewestIndex];

        /// <summary>Number of snapshots stored.</summary>
        public int Count => _count;

        public TickHistoryBuffer(int capacity = 500)
        {
            _capacity = Math.Max(16, capacity);
            _snapshots = new Dictionary<string, object>[_capacity];
            _ticks = new int[_capacity];
        }

        /// <summary>
        /// Records a snapshot of field values for the given tick.
        /// Values are shallow-cloned (primitives copy by value; reference
        /// types store the current reference — acceptable for display).
        /// </summary>
        public void Record(int tick, Dictionary<string, object> fields)
        {
            if (fields == null) return;

            // Clone the dictionary so later mutations don't affect history
            var clone = new Dictionary<string, object>(fields.Count);
            foreach (var kv in fields)
                clone[kv.Key] = CloneValue(kv.Value);

            _ticks[_head] = tick;
            _snapshots[_head] = clone;
            _head = (_head + 1) % _capacity;
            if (_count < _capacity) _count++;
        }

        /// <summary>
        /// Returns the snapshot closest to the requested tick, or null
        /// if the buffer is empty.
        /// </summary>
        public Dictionary<string, object> GetSnapshot(int tick)
        {
            if (_count == 0) return null;

            // Binary-ish scan — the buffer is in tick order (ring)
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

        /// <summary>
        /// Extracts a numeric time-series for a single field across all
        /// stored ticks.  Returns parallel arrays of ticks and values.
        /// Non-numeric values are skipped (NaN placeholder).
        /// </summary>
        public void GetNumericSeries(string fieldName, out int[] ticksOut, out float[] valuesOut)
        {
            ticksOut = new int[_count];
            valuesOut = new float[_count];

            for (int i = 0; i < _count; i++)
            {
                int idx = (OldestIndex + i) % _capacity;
                ticksOut[i] = _ticks[idx];

                var snap = _snapshots[idx];
                object val;
                if (snap != null && snap.TryGetValue(fieldName, out val))
                    valuesOut[i] = ToFloat(val);
                else
                    valuesOut[i] = float.NaN;
            }
        }

        /// <summary>Clears all recorded history.</summary>
        public void Clear()
        {
            _head = 0;
            _count = 0;
            Array.Clear(_snapshots, 0, _snapshots.Length);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private int OldestIndex => _count < _capacity ? 0 : _head;
        private int NewestIndex => (_head - 1 + _capacity) % _capacity;

        private static float ToFloat(object value)
        {
            if (value is float f) return f;
            if (value is double d) return (float)d;
            if (value is int i) return i;
            if (value is long l) return l;
            if (value is short s) return s;
            if (value is byte b) return b;
            if (value is decimal m) return (float)m;
            if (value is uint ui) return ui;
            if (value is bool bl) return bl ? 1f : 0f;
            return float.NaN;
        }

        private static object CloneValue(object value)
        {
            // Value types (int, float, bool, etc.) and strings are immutable — safe as-is
            if (value == null || value is ValueType || value is string)
                return value;
            // For reference types, store ToString() snapshot to avoid mutation issues
            return value.ToString();
        }
    }
}
