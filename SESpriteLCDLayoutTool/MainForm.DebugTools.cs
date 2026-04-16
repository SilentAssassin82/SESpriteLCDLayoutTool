using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;
using SESpriteLCDLayoutTool.Controls;
using SESpriteLCDLayoutTool.Data;
using SESpriteLCDLayoutTool.Models;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool
{
    public partial class MainForm
    {
        // ── Debug tools ──────────────────────────────────────────────────────────

        private void ToggleDebugPanel(bool visible)
        {
            _debugPanelVisible = visible;
            _debugPanel.Visible = visible;
            if (visible) RefreshDebugStats();
        }

        private void RefreshDebugStats()
        {
            if (_layout == null) return;

            // Stats panel text
            if (_debugPanelVisible)
            {
                var stats = DebugAnalyzer.Analyze(_layout);
                var mem = DebugAnalyzer.AnalyzeTextureMemory(_layout, _textureCache);

                string line1 = DebugAnalyzer.BuildStatusSummary(stats);
                string line2 = mem.TotalBytes > 0
                    ? $"VRAM estimate: {DebugAnalyzer.FormatBytes(mem.TotalBytes)} ({mem.Entries.Count} unique textures)"
                    : "VRAM: — (no texture data)";
                _lblDebugStats.Text = line1 + "\n" + line2;
            }

            // Size warnings (always refresh if overlay is on)
            if (_canvas.ShowSizeWarnings)
            {
                _canvas.SizeWarnings = DebugAnalyzer.AnalyzeSizeWarnings(_layout, _textureCache);
                _canvas.Invalidate();
            }
        }

        private void ShowVramBudgetDialog()
        {
            if (_layout == null) { SetStatus("No layout loaded."); return; }

            var report = DebugAnalyzer.AnalyzeTextureMemory(_layout, _textureCache);
            if (report.Entries.Count == 0)
            {
                MessageBox.Show("No texture data available.\n\nLoad SE textures via View → Set SE Game Path to see VRAM estimates.",
                    "VRAM Budget", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Sort by VRAM descending
            report.Entries.Sort((a, b) => b.VramBytes.CompareTo(a.VramBytes));

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Estimated VRAM budget: {DebugAnalyzer.FormatBytes(report.TotalBytes)}");
            sb.AppendLine($"Unique textures: {report.Entries.Count}");
            sb.AppendLine();
            sb.AppendLine("Texture                                          Dimensions      VRAM");
            sb.AppendLine(new string('─', 78));

            foreach (var entry in report.Entries)
            {
                string name = entry.SpriteName.Length > 45
                    ? entry.SpriteName.Substring(0, 42) + "..."
                    : entry.SpriteName;
                sb.AppendLine($"{name,-48} {entry.OriginalWidth,4}×{entry.OriginalHeight,-4}   {DebugAnalyzer.FormatBytes(entry.VramBytes),10}");
            }

            sb.AppendLine(new string('─', 78));
            sb.AppendLine($"{"TOTAL",-48} {"",10}   {DebugAnalyzer.FormatBytes(report.TotalBytes),10}");

            using (var dlg = new Form
            {
                Text          = "VRAM Budget — Texture Memory Estimate",
                Size          = new Size(680, 460),
                StartPosition = FormStartPosition.CenterParent,
                BackColor     = Color.FromArgb(30, 30, 30),
                ForeColor     = Color.FromArgb(220, 220, 220),
                Font          = new Font("Segoe UI", 9f),
            })
            {
                var txt = new RichTextBox
                {
                    Dock      = DockStyle.Fill,
                    ReadOnly  = true,
                    BackColor = Color.FromArgb(14, 14, 14),
                    ForeColor = Color.FromArgb(200, 220, 200),
                    Font      = new Font("Consolas", 9f),
                    Text      = sb.ToString(),
                    WordWrap  = false,
                    ScrollBars = RichTextBoxScrollBars.Both,
                    BorderStyle = BorderStyle.None,
                };
                dlg.Controls.Add(txt);

                var btnClose = DarkButton("Close", Color.FromArgb(70, 70, 70));
                btnClose.Dock  = DockStyle.Bottom;
                btnClose.Height = 32;
                btnClose.Click += (s, e) => dlg.Close();
                dlg.Controls.Add(btnClose);

                // Size warnings summary
                var warnings = DebugAnalyzer.AnalyzeSizeWarnings(_layout, _textureCache);
                if (warnings.Count > 0)
                {
                    var warnSb = new System.Text.StringBuilder();
                    warnSb.AppendLine();
                    warnSb.AppendLine($"⚠ {warnings.Count} sprite(s) using oversized textures:");
                    warnSb.AppendLine();
                    foreach (var w in warnings)
                    {
                        string name = (w.Sprite.SpriteName ?? "").Length > 35
                            ? w.Sprite.SpriteName.Substring(0, 32) + "..."
                            : w.Sprite.SpriteName ?? "";
                        warnSb.AppendLine($"  {name,-38} tex {w.TextureWidth}×{w.TextureHeight} → rendered {w.RenderedWidth:F0}×{w.RenderedHeight:F0}  ({w.WasteRatio:F0}× waste)");
                    }
                    txt.AppendText(warnSb.ToString());
                }

                dlg.ShowDialog(this);
            }
        }

        // ── Pop-out code editor ───────────────────────────────────────────────────

        private void ToggleCodePopout()
        {
            if (_codePopoutForm != null && !_codePopoutForm.IsDisposed)
            {
                // Dock the code panel back into the main form
                DockCodePanel();
                return;
            }

            // Pop out: save splitter distance, reparent code panel into a new form
            _savedWorkSplitDistance = _workSplit.SplitterDistance;

            _codePopoutForm = new Form
            {
                Text          = "SE Sprite LCD — Code Editor",
                Size          = new Size(800, 600),
                MinimumSize   = new Size(480, 300),
                StartPosition = FormStartPosition.Manual,
                BackColor     = Color.FromArgb(30, 30, 30),
                ForeColor     = Color.FromArgb(220, 220, 220),
                Font          = Font,
                Icon          = Icon,
                ShowInTaskbar = true,
            };

            // Position the popout next to the main window
            var screen = Screen.FromControl(this);
            int popX = Right + 8;
            if (popX + _codePopoutForm.Width > screen.WorkingArea.Right)
                popX = screen.WorkingArea.Right - _codePopoutForm.Width;
            _codePopoutForm.Location = new Point(popX, Top);

            // Move the code panel from the main form into the popout
            _workSplit.Panel2.Controls.Remove(_codePanel);
            _codePopoutForm.Controls.Add(_codePanel);
            _workSplit.Panel2Collapsed = true;

            _btnPopOut.Text = "⬋ Dock";
            SetStatus("Code editor popped out — Ctrl+E to dock back");

            // Allow keyboard shortcuts while the popout has focus
            _codePopoutForm.KeyPreview = true;
            _codePopoutForm.KeyDown += (s, e) =>
            {
                // Tab / Shift+Tab for indent/outdent (ProcessCmdKey doesn't work on child form)
                if (e.KeyCode == Keys.Tab && _codeBox != null && _codeBox.Focused)
                {
                    if (e.Shift)
                        CodeBoxOutdentSelection();
                    else
                        CodeBoxIndentSelection();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                else if (e.KeyData == (Keys.Control | Keys.E))
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    DockCodePanel();
                }
            };

            _codePopoutForm.FormClosing += OnCodePopoutClosing;
            _codePopoutForm.Show();
        }

        private void OnCodePopoutClosing(object sender, FormClosingEventArgs e)
        {
            if (_codePopoutForm == null) return;
            _codePopoutForm.FormClosing -= OnCodePopoutClosing;

            _codePopoutForm.Controls.Remove(_codePanel);
            _workSplit.Panel2.Controls.Add(_codePanel);
            _workSplit.Panel2Collapsed = false;

            int maxDist = _workSplit.Height - _workSplit.Panel2MinSize - _workSplit.SplitterWidth;
            int minDist = _workSplit.Panel1MinSize;
            _workSplit.SplitterDistance = Math.Max(minDist, Math.Min(maxDist, _savedWorkSplitDistance));

            _btnPopOut.Text = "⬈ Pop Out";
            SetStatus("Code editor docked");
            _codePopoutForm = null;
        }

        private void DockCodePanel()
        {
            if (_codePopoutForm == null || _codePopoutForm.IsDisposed) return;
            _codePopoutForm.Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Cancel any pending write-back so closing the app never writes
            // generated/patched code back to the synced file.
            if (_writeBackTimer != null)
            {
                _writeBackTimer.Stop();
                _pendingWriteBack = null;
            }

            // Disconnect the file watcher without triggering a final write-back.
            if (_fileWatcher != null)
            {
                _fileWatcher.Stop();
                _fileWatcher.Dispose();
                _fileWatcher = null;
                _fileWatchBidirectional = false;
            }

            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _animPlayer?.Dispose();
                _pipeListener?.Dispose();
                _fileWatcher?.Dispose();
                _clipboardTimer?.Dispose();
                _writeBackTimer?.Dispose();
                _textureCache?.Dispose();
                _autoComplete?.Dispose();
                if (_codePopoutForm != null && !_codePopoutForm.IsDisposed)
                    _codePopoutForm.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
