using System;
using System.Collections.Generic;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Custom text-only undo/redo stack for a code editor.
    /// Avoids the RichTextBox native undo which gets polluted by
    /// formatting (syntax highlighting) changes.
    /// </summary>
    internal sealed class CodeUndoManager
    {
        private readonly Stack<UndoState> _undoStack = new Stack<UndoState>();
        private readonly Stack<UndoState> _redoStack = new Stack<UndoState>();
        private bool _isUndoRedoing;

        /// <summary>True while an undo/redo operation is in progress — callers
        /// should skip pushing new states during this time.</summary>
        public bool IsUndoRedoing => _isUndoRedoing;

        public bool CanUndo => _undoStack.Count > 1; // need at least 2: current + previous
        public bool CanRedo => _redoStack.Count > 0;

        /// <summary>
        /// Push a snapshot of the current text and caret position.
        /// Call this from TextChanged (debounced or immediate) when not undoing.
        /// </summary>
        public void Push(string text, int caretPosition)
        {
            if (_isUndoRedoing) return;

            // Avoid duplicate consecutive identical states
            if (_undoStack.Count > 0)
            {
                var top = _undoStack.Peek();
                if (string.Equals(top.Text, text, StringComparison.Ordinal))
                    return;
            }

            _undoStack.Push(new UndoState(text, caretPosition));
            _redoStack.Clear();

            // Cap the stack to prevent unbounded memory use
            // (Stack doesn't support trimming, but 500 states is fine for this app)
        }

        /// <summary>
        /// Returns the previous state, or null if nothing to undo.
        /// The caller should set the text box contents to the returned text.
        /// </summary>
        public UndoState Undo()
        {
            if (!CanUndo) return null;
            _isUndoRedoing = true;
            try
            {
                var current = _undoStack.Pop();
                _redoStack.Push(current);
                return _undoStack.Peek(); // don't pop — it's the new "current"
            }
            finally { _isUndoRedoing = false; }
        }

        /// <summary>
        /// Returns the next redo state, or null if nothing to redo.
        /// </summary>
        public UndoState Redo()
        {
            if (!CanRedo) return null;
            _isUndoRedoing = true;
            try
            {
                var state = _redoStack.Pop();
                _undoStack.Push(state);
                return state;
            }
            finally { _isUndoRedoing = false; }
        }

        /// <summary>Clears both stacks (e.g. when loading new content).</summary>
        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }

        internal sealed class UndoState
        {
            public string Text { get; }
            public int CaretPosition { get; }
            public UndoState(string text, int caretPosition)
            {
                Text = text;
                CaretPosition = caretPosition;
            }
        }
    }
}
