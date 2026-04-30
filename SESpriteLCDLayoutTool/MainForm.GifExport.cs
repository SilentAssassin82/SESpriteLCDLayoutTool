using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using SESpriteLCDLayoutTool.Models;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool
{
    public partial class MainForm
    {
        // ── Animated GIF export ───────────────────────────────────────────────────

        /// <summary>
        /// Shows the GIF export dialog, runs the animation in-place to capture frames,
        /// and writes an animated GIF89a file. Restores the pre-export layout when done.
        /// </summary>
        private void ShowExportGifDialog()
        {
            if (_layout == null)
            {
                MessageBox.Show(this, "Open or create a layout first.", "Export Animated GIF",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string code = _layout?.OriginalSourceCode;
            if (string.IsNullOrWhiteSpace(code))
                code = _codeBox?.Text;
            if (string.IsNullOrWhiteSpace(code))
            {
                MessageBox.Show(this, "Nothing to animate — there is no script in the code panel.",
                    "Export Animated GIF", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Detect whether an animation session is already prepared.
            // If so, we capture from the current tick instead of restarting.
            bool sessionLive = _animPlayer != null && _animPlayer.IsPlaying;
            int currentTick = sessionLive ? _animPlayer.CurrentTick : 0;

            // ── Build options dialog
            using (var dlg = new Form())
            {
                dlg.Text          = "Export Animated GIF";
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MinimizeBox   = false;
                dlg.MaximizeBox   = false;
                dlg.ClientSize    = new Size(380, 320);
                dlg.BackColor     = Color.FromArgb(30, 30, 30);
                dlg.ForeColor     = Color.FromArgb(220, 220, 220);
                dlg.Font          = new Font("Segoe UI", 9f);

                Label MakeLabel(string txt, int y) => new Label
                {
                    Text = txt, AutoSize = true, Left = 12, Top = y + 3, ForeColor = dlg.ForeColor,
                };

                NumericUpDown MakeNum(int min, int max, int val, int y) => new NumericUpDown
                {
                    Left = 200, Top = y, Width = 90, Minimum = min, Maximum = max, Value = val,
                    BackColor = Color.FromArgb(45, 45, 48), ForeColor = dlg.ForeColor,
                };

                // Source mode
                int yy = 12;
                var rdoFresh = new RadioButton
                {
                    Left = 12, Top = yy, AutoSize = true,
                    Text = "Fresh run (restart animation)",
                    Checked = !sessionLive, ForeColor = dlg.ForeColor,
                };
                dlg.Controls.Add(rdoFresh); yy += 22;

                var rdoContinue = new RadioButton
                {
                    Left = 12, Top = yy, AutoSize = true,
                    Text = sessionLive
                        ? $"Continue from current tick ({currentTick})"
                        : "Continue from current tick (no live session)",
                    Checked = sessionLive, Enabled = sessionLive, ForeColor = dlg.ForeColor,
                };
                dlg.Controls.Add(rdoContinue); yy += 28;

                dlg.Controls.Add(MakeLabel("Duration (seconds)", yy));
                var numSecs = MakeNum(1, 120, 5, yy); dlg.Controls.Add(numSecs); yy += 32;

                dlg.Controls.Add(MakeLabel("Frame rate (fps)", yy));
                var numFps = MakeNum(2, 30, 15, yy); dlg.Controls.Add(numFps); yy += 32;

                var lblWarmup = MakeLabel("Warm-up frames to skip", yy);
                dlg.Controls.Add(lblWarmup);
                var numWarmup = MakeNum(0, 5000, 0, yy); dlg.Controls.Add(numWarmup); yy += 32;


                int defaultW = Math.Min(_layout.SurfaceWidth,  512);
                int defaultH = Math.Min(_layout.SurfaceHeight, 512);
                dlg.Controls.Add(MakeLabel("Output width (px)", yy));
                var numW = MakeNum(64, 2048, defaultW, yy); dlg.Controls.Add(numW); yy += 32;

                dlg.Controls.Add(MakeLabel("Output height (px)", yy));
                var numH = MakeNum(64, 2048, defaultH, yy); dlg.Controls.Add(numH); yy += 32;

                var chkHideRef = new CheckBox
                {
                    Left = 12, Top = yy, AutoSize = true, Text = "Hide reference boxes (gold)", Checked = true,
                    ForeColor = dlg.ForeColor,
                };
                dlg.Controls.Add(chkHideRef); yy += 28;

                var chkLoop = new CheckBox
                {
                    Left = 200, Top = yy, AutoSize = true, Text = "Loop forever", Checked = true,
                    ForeColor = dlg.ForeColor,
                };
                dlg.Controls.Add(chkLoop);

                // Warm-up only matters for fresh runs — disable when continuing
                Action syncWarmup = () =>
                {
                    bool fresh = rdoFresh.Checked;
                    lblWarmup.Enabled = fresh;
                    numWarmup.Enabled = fresh;
                };
                rdoFresh.CheckedChanged    += (s, e) => syncWarmup();
                rdoContinue.CheckedChanged += (s, e) => syncWarmup();
                syncWarmup();

                var btnOk = new Button
                {
                    Text = "Export…", DialogResult = DialogResult.OK,
                    Left = 185, Top = 285, Width = 90,
                    BackColor = Color.FromArgb(60, 110, 180), ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
                };
                var btnCancel = new Button
                {
                    Text = "Cancel", DialogResult = DialogResult.Cancel,
                    Left = 280, Top = 285, Width = 90,
                    BackColor = Color.FromArgb(50, 50, 53), ForeColor = dlg.ForeColor, FlatStyle = FlatStyle.Flat,
                };
                dlg.Controls.Add(btnOk);
                dlg.Controls.Add(btnCancel);
                dlg.AcceptButton = btnOk;
                dlg.CancelButton = btnCancel;

                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;


                bool useExisting = rdoContinue.Checked && sessionLive;
                int seconds = (int)numSecs.Value;
                int fps     = (int)numFps.Value;
                int warmup  = useExisting ? 0 : (int)numWarmup.Value;
                int outW    = (int)numW.Value;
                int outH    = (int)numH.Value;
                int loopCount = chkLoop.Checked ? 0 : 1;
                int totalFrames = Math.Max(1, seconds * fps);
                int delayCs = Math.Max(2, (int)Math.Round(100.0 / fps));
                bool hideRef = chkHideRef.Checked;

                using (var sd = new SaveFileDialog
                {
                    Filter   = "Animated GIF (*.gif)|*.gif|All files (*.*)|*.*",
                    Title    = "Save Animated GIF",
                    FileName = (Path.GetFileNameWithoutExtension(_currentFile) ?? _layout?.Name ?? "Layout") + ".gif",
                })
                {
                    if (sd.ShowDialog(this) != DialogResult.OK)
                        return;

                    try
                    {
                        ExportAnimatedGif(code, sd.FileName, totalFrames, delayCs, outW, outH,
                                          loopCount, warmup, useExisting, hideRef);
                        SetStatus($"GIF exported: {Path.GetFileName(sd.FileName)} ({totalFrames} frames)");
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "Could not export GIF:\n" + ex.Message, "Export Animated GIF",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        /// <summary>
        /// Drives the animation player frame-by-frame and pipes each rendered frame
        /// into <see cref="GifExporter"/>.
        /// 
        /// When <paramref name="useExistingSession"/> is true, the currently-prepared
        /// animation player keeps its state (tick count, fields, runtime variables) so
        /// the GIF starts from wherever playback is right now — useful for animations
        /// that need a long warm-up. The pre-existing playback timer is paused during
        /// capture and resumed afterwards. Otherwise a fresh animation is prepared and
        /// stopped when capture completes.
        /// </summary>
        private void ExportAnimatedGif(string code, string outputPath,
                                       int totalFrames, int delayCs,
                                       int width, int height, int loopCount,
                                       int warmupFrames, bool useExistingSession, bool hideReferenceBoxes)
        {
            bool wasPaused = false;

            if (useExistingSession && _animPlayer != null && _animPlayer.IsPlaying)
            {
                // Pause without tearing down the session so state is preserved.
                wasPaused = _animPlayer.IsPaused;
                if (!wasPaused)
                    _animPlayer.Pause();
            }
            else
            {
                // Stop any current playback so capture has clean state.
                _animPlayer?.Stop();

                EnsureAnimPlayer();
                PushUndo();
                CaptureAnimPositionSnapshot();

                string error = _animPlayer.Prepare(code, null, _layout?.CapturedRows);
                if (error != null)
                {
                    ShowAnimationErrorWithDiagnostics(error);
                    _animPositionSnapshot = null;
                    UpdateAnimButtonStates();
                    throw new InvalidOperationException("Animation could not be prepared. See diagnostics.");
                }
            }

            // Use a progress dialog so the user knows something is happening.
            using (var progress = new Form())
            {
                progress.Text          = "Exporting GIF…";
                progress.StartPosition = FormStartPosition.CenterParent;
                progress.FormBorderStyle = FormBorderStyle.FixedDialog;
                progress.ControlBox    = false;
                progress.ClientSize    = new Size(380, 100);
                progress.BackColor     = Color.FromArgb(30, 30, 30);
                progress.ForeColor     = Color.FromArgb(220, 220, 220);
                progress.Font          = new Font("Segoe UI", 9f);

                int totalSteps = warmupFrames + totalFrames;
                var lbl = new Label
                {
                    Left = 12, Top = 12, Width = 356, Height = 20,
                    Text = warmupFrames > 0
                        ? $"Warming up (0 of {warmupFrames})…"
                        : $"Capturing frame 0 of {totalFrames}…",
                };
                var bar = new ProgressBar
                {
                    Left = 12, Top = 40, Width = 356, Height = 22,
                    Minimum = 0, Maximum = totalSteps, Value = 0, Style = ProgressBarStyle.Continuous,
                };
                progress.Controls.Add(lbl);
                progress.Controls.Add(bar);
                progress.Show(this);
                progress.Refresh();

                try
                {
                    // Warm-up: step the animation forward without recording, so
                    // long spin-up sequences (intro frames, splash text, etc.)
                    // are skipped before we start writing the GIF.
                    for (int w = 0; w < warmupFrames; w++)
                    {
                        _animPlayer.StepForward();
                        bar.Value = w + 1;
                        if ((w & 3) == 0)
                        {
                            lbl.Text = $"Warming up ({w + 1} of {warmupFrames})…";
                            progress.Refresh();
                            Application.DoEvents();
                        }
                    }

                    using (var fs = File.Create(outputPath))
                    using (var gif = new GifExporter(fs, width, height) { FrameDelayCs = delayCs, LoopCount = loopCount })
                    {
                        for (int i = 0; i < totalFrames; i++)
                        {
                            // StepForward fires FrameRendered synchronously, which causes
                            // OnAnimFrame to swap _layout.Sprites with the new frame data.
                            _animPlayer.StepForward();

                            using (var bmp = _canvas.RenderLayoutToBitmap(width, height, hideReferenceBoxes))
                                gif.AddFrame(bmp);

                            bar.Value = warmupFrames + i + 1;
                            lbl.Text  = $"Capturing frame {i + 1} of {totalFrames}…";
                            progress.Refresh();
                            Application.DoEvents();
                        }
                    }
                }
                finally
                {
                    progress.Close();
                }
            }

            if (useExistingSession)
            {
                // Restore the user's previous play/pause state so they can keep working.
                if (!wasPaused && _animPlayer != null)
                    _animPlayer.Play();
                UpdateAnimButtonStates();
            }
            else
            {
                // Fresh-run mode: stop so OnAnimStopped restores the original layout.
                _animPlayer.Stop();
            }
        }
    }
}
