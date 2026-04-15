using System;
using System.Collections.Generic;
using System.Linq;
using SESpriteLCDLayoutTool.Models;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Snapshot-based undo/redo manager for the LCD layout editor.
    /// Call <see cref="PushUndo"/> before every mutation to capture state.
    /// </summary>
    public class UndoManager
    {
        private readonly Stack<LayoutSnapshot> _undoStack = new Stack<LayoutSnapshot>();
        private readonly Stack<LayoutSnapshot> _redoStack = new Stack<LayoutSnapshot>();

        private const int MaxHistory = 80;

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        /// <summary>
        /// Captures a snapshot of the current sprite list. Call this BEFORE mutating.
        /// </summary>
        public void PushUndo(LcdLayout layout)
        {
            if (layout == null) return;
            _undoStack.Push(Snapshot(layout));
            _redoStack.Clear();

            // Trim oldest entries if we exceed the limit
            if (_undoStack.Count > MaxHistory)
            {
                var temp = _undoStack.ToArray();
                _undoStack.Clear();
                for (int i = Math.Min(temp.Length - 1, MaxHistory - 1); i >= 0; i--)
                    _undoStack.Push(temp[i]);
            }
        }

        /// <summary>
        /// Restores the previous state. Returns the Id of the sprite that was selected
        /// (if we can infer it), or null.
        /// </summary>
        public bool Undo(LcdLayout layout)
        {
            if (!CanUndo || layout == null) return false;
            _redoStack.Push(Snapshot(layout));
            Restore(layout, _undoStack.Pop());
            return true;
        }

        public bool Redo(LcdLayout layout)
        {
            if (!CanRedo || layout == null) return false;
            _undoStack.Push(Snapshot(layout));
            Restore(layout, _redoStack.Pop());
            return true;
        }

        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }

        // ── Snapshot helpers ──────────────────────────────────────────────────────
        private static LayoutSnapshot Snapshot(LcdLayout layout)
        {
            var sprites = layout.Sprites.Select(s => new SpriteSnapshot
            {
                Id         = s.Id,
                Type       = s.Type,
                SpriteName = s.SpriteName,
                X          = s.X,
                Y          = s.Y,
                Width      = s.Width,
                Height     = s.Height,
                ColorR     = s.ColorR,
                ColorG     = s.ColorG,
                ColorB     = s.ColorB,
                ColorA     = s.ColorA,
                Rotation   = s.Rotation,
                Text       = s.Text,
                FontId     = s.FontId,
                Alignment  = s.Alignment,
                Scale      = s.Scale,
                IsReferenceLayout = s.IsReferenceLayout,
                ImportLabel       = s.ImportLabel,
                SourceStart       = s.SourceStart,
                SourceEnd         = s.SourceEnd,
                ImportBaseline    = s.ImportBaseline,
                SourceLineNumber  = s.SourceLineNumber,
                AnimationGroupId  = s.AnimationGroupId,
                KeyframeAnimation = CloneAnimation(s.KeyframeAnimation),
            }).ToList();

            return new LayoutSnapshot
            {
                Sprites = sprites,
                OriginalSourceCode = layout.OriginalSourceCode,
            };
        }

        private static void Restore(LcdLayout layout, LayoutSnapshot snapshot)
        {
            layout.Sprites.Clear();
            foreach (var snap in snapshot.Sprites)
            {
                layout.Sprites.Add(new SpriteEntry
                {
                    Id         = snap.Id,
                    Type       = snap.Type,
                    SpriteName = snap.SpriteName,
                    X          = snap.X,
                    Y          = snap.Y,
                    Width      = snap.Width,
                    Height     = snap.Height,
                    ColorR     = snap.ColorR,
                    ColorG     = snap.ColorG,
                    ColorB     = snap.ColorB,
                    ColorA     = snap.ColorA,
                    Rotation   = snap.Rotation,
                    Text       = snap.Text,
                    FontId     = snap.FontId,
                    Alignment  = snap.Alignment,
                    Scale      = snap.Scale,
                    IsReferenceLayout = snap.IsReferenceLayout,
                    ImportLabel       = snap.ImportLabel,
                    SourceStart       = snap.SourceStart,
                    SourceEnd         = snap.SourceEnd,
                    ImportBaseline    = snap.ImportBaseline,
                    SourceLineNumber  = snap.SourceLineNumber,
                    AnimationGroupId  = snap.AnimationGroupId,
                    KeyframeAnimation = CloneAnimation(snap.KeyframeAnimation),
                });
            }
            layout.OriginalSourceCode = snapshot.OriginalSourceCode;
        }

        /// <summary>Deep-clones KeyframeAnimationParams for snapshot isolation.</summary>
        private static KeyframeAnimationParams CloneAnimation(KeyframeAnimationParams src)
        {
            if (src == null) return null;
            return new KeyframeAnimationParams
            {
                ListVarName  = src.ListVarName,
                Loop         = src.Loop,
                TargetScript = src.TargetScript,
                Keyframes    = src.Keyframes?.Select(k => new Keyframe
                {
                    Tick         = k.Tick,
                    X            = k.X,
                    Y            = k.Y,
                    Width        = k.Width,
                    Height       = k.Height,
                    ColorR       = k.ColorR,
                    ColorG       = k.ColorG,
                    ColorB       = k.ColorB,
                    ColorA       = k.ColorA,
                    Rotation     = k.Rotation,
                    Scale        = k.Scale,
                    EasingToNext = k.EasingToNext,
                }).ToList() ?? new List<Keyframe>(),
            };
        }

        /// <summary>Full layout snapshot including sprites and code state.</summary>
        private struct LayoutSnapshot
        {
            public List<SpriteSnapshot> Sprites;
            public string OriginalSourceCode;
        }

        /// <summary>Internal value-copy of a SpriteEntry for the undo stack.</summary>
        private struct SpriteSnapshot
        {
            public string Id;
            public SpriteEntryType Type;
            public string SpriteName;
            public float X, Y, Width, Height;
            public int ColorR, ColorG, ColorB, ColorA;
            public float Rotation;
            public string Text;
            public string FontId;
            public SpriteTextAlignment Alignment;
            public float Scale;
            public bool IsReferenceLayout;
            public string ImportLabel;
            public int SourceStart;
            public int SourceEnd;
            public SpriteEntry ImportBaseline;
            public int SourceLineNumber;
            public string AnimationGroupId;
            public KeyframeAnimationParams KeyframeAnimation;
        }
    }
}
