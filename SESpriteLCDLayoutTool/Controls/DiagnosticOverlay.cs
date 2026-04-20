using System;

namespace SESpriteLCDLayoutTool.Controls
{
    /// <summary>
    /// Diagnostic overlay stub — Scintilla's built-in indicator system renders
    /// squiggly underlines natively, so this class is now a no-op placeholder
    /// to keep existing call sites (InvalidateEditor) compiling.
    /// </summary>
    internal sealed class DiagnosticOverlay : IDisposable
    {
        public DiagnosticOverlay(ScintillaCodeBox editor)
        {
        }

        /// <summary>
        /// Requests a diagnostic indicator refresh.  With Scintilla, indicators
        /// are painted automatically — this is kept for call-site compatibility.
        /// </summary>
        public void InvalidateEditor()
        {
            // No-op: Scintilla renders indicators natively.
        }

        public void Dispose()
        {
        }
    }
}
