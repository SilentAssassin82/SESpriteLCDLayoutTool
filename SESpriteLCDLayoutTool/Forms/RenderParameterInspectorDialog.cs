using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool.Forms
{
    /// <summary>
    /// First-slice "Render Parameter Inspector" dialog. Lists numeric knobs
    /// extracted from class-level constants and a chosen render method body.
    /// The user adjusts values and clicks Apply; the patched source is returned
    /// to the main form via <see cref="PatchedCode"/> for re-execution / preview
    /// on the existing LCD canvas.
    /// </summary>
    public class RenderParameterInspectorDialog : Form
    {
        private readonly string _originalCode;
        private readonly List<RenderParameterKnob> _knobs;
        private readonly Dictionary<RenderParameterKnob, NumericUpDown> _numerics
            = new Dictionary<RenderParameterKnob, NumericUpDown>();
        // Color knobs are edited via a swatch + ColorDialog rather than per-channel
        // numeric inputs; we keep a Color reference so live preview/apply can read it.
        private readonly Dictionary<RenderParameterKnob, Func<Color>> _colorReaders
            = new Dictionary<RenderParameterKnob, Func<Color>>();

        private ComboBox _cmbMethod;
        private FlowLayoutPanel _pnlKnobs;
        private Label _lblStatus;
        private CheckBox _chkLivePreview;
        private Timer _liveTimer;

        /// <summary>Patched source to write back to the code box after Apply.</summary>
        public string PatchedCode { get; private set; }

        /// <summary>True once Apply has produced patched code.</summary>
        public bool DidApply { get; private set; }

        /// <summary>
        /// Optional live-preview hook. When the user enables "Live preview" and
        /// changes a knob, this callback is invoked (debounced ~250 ms) with the
        /// fully patched source. The host typically writes it to the code box and
        /// triggers an execute pass to refresh the canvas.
        /// </summary>
        public Action<string> LivePreviewCallback { get; set; }

        public RenderParameterInspectorDialog(string code, IList<string> renderMethodNames, string defaultMethod)
        {
            _originalCode = code ?? string.Empty;
            _knobs = new List<RenderParameterKnob>();

            Text = "Render Parameter Inspector";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(760, 640);
            MinimumSize = new Size(560, 480);
            BackColor = Color.FromArgb(30, 30, 30);
            ForeColor = Color.FromArgb(220, 220, 220);
            Font = new Font("Segoe UI", 9f);

            BuildUi(renderMethodNames, defaultMethod);
            ScanAndPopulate();

            _liveTimer = new Timer { Interval = 250 };
            _liveTimer.Tick += (s, e) =>
            {
                _liveTimer.Stop();
                FireLivePreview();
            };
            FormClosed += (s, e) => { _liveTimer?.Stop(); _liveTimer?.Dispose(); };
        }

        private void BuildUi(IList<string> methodNames, string defaultMethod)
        {
            var top = new Panel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(8, 6, 8, 4) };
            var lbl = new Label { Text = "Method:", AutoSize = true, Location = new Point(8, 10), ForeColor = ForeColor };
            _cmbMethod = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(64, 6),
                Width = 320,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.FromArgb(220, 220, 220),
                FlatStyle = FlatStyle.Flat,
            };
            _cmbMethod.Items.Add("(Constants only)");
            if (methodNames != null)
            {
                foreach (string n in methodNames)
                    if (!string.IsNullOrWhiteSpace(n)) _cmbMethod.Items.Add(n);
            }
            _cmbMethod.SelectedIndex = 0;
            if (!string.IsNullOrWhiteSpace(defaultMethod))
            {
                int idx = _cmbMethod.Items.IndexOf(defaultMethod);
                if (idx >= 0) _cmbMethod.SelectedIndex = idx;
            }
            _cmbMethod.SelectedIndexChanged += (s, e) => ScanAndPopulate();
            top.Controls.Add(lbl);
            top.Controls.Add(_cmbMethod);

            _chkLivePreview = new CheckBox
            {
                Text = "Live preview",
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(top.Width - 110, 9),
                ForeColor = Color.FromArgb(180, 220, 255),
            };
            top.Controls.Add(_chkLivePreview);

            _pnlKnobs = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(8),
                BackColor = Color.FromArgb(35, 35, 38),
            };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(8) };
            _lblStatus = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Color.FromArgb(180, 180, 180) };

            var btnReset = new Button
            {
                Text = "Reset",
                Width = 90, Height = 28,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Location = new Point(bottom.Width - 290, 30),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = ForeColor,
                FlatStyle = FlatStyle.Flat,
            };
            btnReset.Click += (s, e) => ResetKnobs();

            var btnApply = new Button
            {
                Text = "Apply",
                Width = 90, Height = 28,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Location = new Point(bottom.Width - 195, 30),
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
            };
            btnApply.Click += (s, e) => ApplyKnobs();

            var btnClose = new Button
            {
                Text = "Close",
                Width = 90, Height = 28,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Location = new Point(bottom.Width - 100, 30),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = ForeColor,
                FlatStyle = FlatStyle.Flat,
                DialogResult = DialogResult.OK,
            };

            bottom.Controls.Add(_lblStatus);
            bottom.Controls.Add(btnReset);
            bottom.Controls.Add(btnApply);
            bottom.Controls.Add(btnClose);

            var btnSave = new Button
            {
                Text = "Save Preset…",
                Width = 110, Height = 28,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Location = new Point(8, 30),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = ForeColor,
                FlatStyle = FlatStyle.Flat,
            };
            btnSave.Click += (s, e) => SavePreset();

            var btnLoad = new Button
            {
                Text = "Load Preset…",
                Width = 110, Height = 28,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Location = new Point(122, 30),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = ForeColor,
                FlatStyle = FlatStyle.Flat,
            };
            btnLoad.Click += (s, e) => LoadPreset();

            bottom.Controls.Add(btnSave);
            bottom.Controls.Add(btnLoad);

            Controls.Add(_pnlKnobs);
            Controls.Add(bottom);
            Controls.Add(top);
        }

        // Tracks collapsed-state per group label across re-populates so toggling
        // the method dropdown / Reset doesn't lose the user's collapse choices.
        private readonly HashSet<string> _collapsedGroups = new HashSet<string>(StringComparer.Ordinal);

        private void ScanAndPopulate()
        {
            _pnlKnobs.SuspendLayout();
            _pnlKnobs.Controls.Clear();
            _numerics.Clear();
            _colorReaders.Clear();
            _knobs.Clear();

            string method = _cmbMethod.SelectedIndex > 0 ? _cmbMethod.SelectedItem as string : null;
            var found = RenderParameterScanner.Scan(_originalCode, method);
            _knobs.AddRange(found);

            // Order: Constants first, then knobs grouped by GroupKey in order of first appearance.
            var groupOrder = new List<string>();
            var byGroup = new Dictionary<string, List<RenderParameterKnob>>();
            foreach (var k in _knobs)
            {
                string g = string.IsNullOrEmpty(k.GroupKey) ? (k.Category ?? "(other)") : k.GroupKey;
                if (!byGroup.ContainsKey(g))
                {
                    byGroup[g] = new List<RenderParameterKnob>();
                    groupOrder.Add(g);
                }
                byGroup[g].Add(k);
            }
            // Pin "Constants" to the top if present
            if (groupOrder.Remove("Constants")) groupOrder.Insert(0, "Constants");

            foreach (string g in groupOrder)
            {
                AddGroupHeader(g, byGroup[g]);
            }

            _lblStatus.Text = string.Format("{0} knob(s) detected across {1} group(s).", _knobs.Count, groupOrder.Count);
            _pnlKnobs.ResumeLayout();
        }

        private void AddGroupHeader(string groupKey, List<RenderParameterKnob> rows)
        {
            bool collapsed = _collapsedGroups.Contains(groupKey);
            var hdr = new Label
            {
                AutoSize = false,
                Width = _pnlKnobs.ClientSize.Width - 24,
                Height = 22,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(120, 200, 255),
                Margin = new Padding(0, 8, 0, 4),
                Cursor = Cursors.Hand,
                Tag = groupKey,
            };
            hdr.Text = (collapsed ? "▶ " : "▼ ") + groupKey + " (" + rows.Count + ")";
            _pnlKnobs.Controls.Add(hdr);

            // Add the rows up front and toggle visibility on collapse — keeps row
            // construction in one path and lets re-collapse be O(1).
            var addedRows = new List<Control>();
            foreach (var k in rows)
            {
                int before = _pnlKnobs.Controls.Count;
                AddKnobRow(k);
                for (int i = before; i < _pnlKnobs.Controls.Count; i++)
                    addedRows.Add(_pnlKnobs.Controls[i]);
            }
            foreach (var c in addedRows) c.Visible = !collapsed;

            hdr.Click += (s, e) =>
            {
                bool nowCollapsed = !_collapsedGroups.Contains(groupKey);
                if (nowCollapsed) _collapsedGroups.Add(groupKey);
                else _collapsedGroups.Remove(groupKey);
                hdr.Text = (nowCollapsed ? "▶ " : "▼ ") + groupKey + " (" + rows.Count + ")";
                foreach (var c in addedRows) c.Visible = !nowCollapsed;
            };
        }

        private void AddKnobRow(RenderParameterKnob k)
        {
            int rowWidth = _pnlKnobs.ClientSize.Width - 24;
            if (rowWidth < 540) rowWidth = 540;
            if (k.Color != null)
            {
                AddColorRow(k, rowWidth);
                return;
            }
            var row = new Panel
            {
                Width = rowWidth,
                Height = 30,
                Margin = new Padding(0, 2, 0, 2),
                BackColor = Color.FromArgb(35, 35, 38),
            };
            var nameLbl = new Label
            {
                Text = k.Name,
                AutoSize = false,
                Width = 220,
                Location = new Point(4, 6),
                ForeColor = ForeColor,
            };
            var num = new NumericUpDown
            {
                DecimalPlaces = k.IsFloat ? 4 : 0,
                Increment = k.IsFloat ? 0.5m : 1m,
                Minimum = -100000m,
                Maximum = 100000m,
                Location = new Point(228, 4),
                Width = 90,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.FromArgb(220, 220, 220),
            };
            try { num.Value = (decimal)k.CurrentValue; } catch { }

            // Auto-range slider. Floats <= 2 use 0..2 (typical for fractions/scales).
            // Integers and larger floats use a span around the original value.
            double sliderMin, sliderMax;
            ComputeSliderRange(k, out sliderMin, out sliderMax);
            const int kSliderTicks = 1000;
            var slider = new DarkSlider
            {
                Minimum = 0,
                Maximum = kSliderTicks,
                Location = new Point(326, 4),
                Width = rowWidth - 326 - 130,
                Height = 22,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            try { slider.Value = ValueToTick(k.CurrentValue, sliderMin, sliderMax, kSliderTicks); } catch { }

            // Sync slider <-> numeric without feedback loops.
            bool syncing = false;
            num.ValueChanged += (s, e) =>
            {
                if (syncing) return;
                syncing = true;
                try { slider.Value = ValueToTick((double)num.Value, sliderMin, sliderMax, kSliderTicks); } catch { }
                syncing = false;
                ScheduleLivePreview();
            };
            slider.ValueChanged += (s, e) =>
            {
                if (syncing) return;
                syncing = true;
                double v = TickToValue(slider.Value, sliderMin, sliderMax, kSliderTicks);
                try { num.Value = (decimal)Math.Max((double)num.Minimum, Math.Min((double)num.Maximum, v)); } catch { }
                syncing = false;
                ScheduleLivePreview();
            };

            var origLbl = new Label
            {
                Text = "(was " + k.OriginalLiteral + ")",
                AutoSize = false,
                Width = 120,
                TextAlign = ContentAlignment.MiddleLeft,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(rowWidth - 124, 8),
                ForeColor = Color.FromArgb(140, 140, 140),
            };
            row.Controls.Add(nameLbl);
            row.Controls.Add(num);
            row.Controls.Add(slider);
            row.Controls.Add(origLbl);
            _pnlKnobs.Controls.Add(row);
            _numerics[k] = num;
        }

        private void AddColorRow(RenderParameterKnob k, int rowWidth)
        {
            var row = new Panel
            {
                Width = rowWidth,
                Height = 30,
                Margin = new Padding(0, 2, 0, 2),
                BackColor = Color.FromArgb(35, 35, 38),
            };
            var nameLbl = new Label
            {
                Text = k.Name,
                AutoSize = false,
                Width = 220,
                Location = new Point(4, 6),
                ForeColor = ForeColor,
            };

            // Current edited color (mutable closure state).
            var current = Color.FromArgb(
                k.Color.A != null ? k.Color.A.CurrentValue : 255,
                k.Color.R.CurrentValue,
                k.Color.G.CurrentValue,
                k.Color.B.CurrentValue);

            var swatch = new Panel
            {
                Location = new Point(228, 4),
                Width = 60,
                Height = 22,
                BackColor = current,
                BorderStyle = BorderStyle.FixedSingle,
                Cursor = Cursors.Hand,
            };
            var rgbLbl = new Label
            {
                Text = FormatRgba(current, k.Color.A != null),
                AutoSize = false,
                Width = rowWidth - 296 - 130,
                Location = new Point(296, 8),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                ForeColor = Color.FromArgb(200, 200, 200),
            };
            var origLbl = new Label
            {
                Text = "(was " + FormatRgba(
                    Color.FromArgb(
                        k.Color.A != null ? k.Color.A.OriginalValue : 255,
                        k.Color.R.OriginalValue,
                        k.Color.G.OriginalValue,
                        k.Color.B.OriginalValue),
                    k.Color.A != null) + ")",
                AutoSize = false,
                Width = 120,
                TextAlign = ContentAlignment.MiddleLeft,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(rowWidth - 124, 8),
                ForeColor = Color.FromArgb(140, 140, 140),
            };

            EventHandler openPicker = (s, e) =>
            {
                using (var dlg = new ColorDialog
                {
                    Color = current,
                    FullOpen = true,
                    AnyColor = true,
                })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    current = dlg.Color;
                    swatch.BackColor = current;
                    rgbLbl.Text = FormatRgba(current, k.Color.A != null);
                    k.Color.R.CurrentValue = current.R;
                    k.Color.G.CurrentValue = current.G;
                    k.Color.B.CurrentValue = current.B;
                    if (k.Color.A != null) k.Color.A.CurrentValue = current.A;
                    ScheduleLivePreview();
                }
            };
            swatch.Click += openPicker;
            rgbLbl.Click += openPicker;

            row.Controls.Add(nameLbl);
            row.Controls.Add(swatch);
            row.Controls.Add(rgbLbl);
            row.Controls.Add(origLbl);
            _pnlKnobs.Controls.Add(row);
            _colorReaders[k] = () => current;
        }

        private static string FormatRgba(Color c, bool includeAlpha)
        {
            return includeAlpha
                ? string.Format("RGBA {0}, {1}, {2}, {3}", c.R, c.G, c.B, c.A)
                : string.Format("RGB {0}, {1}, {2}", c.R, c.G, c.B);
        }

        private static void ComputeSliderRange(RenderParameterKnob k, out double min, out double max)
        {
            double v = k.OriginalValue;
            double abs = Math.Abs(v);
            if (k.IsFloat && abs <= 2.0)
            {
                min = v < 0 ? -2.0 : 0.0;
                max = v < 0 ? 0.0 : 2.0;
                return;
            }
            // Symmetric span around the original value, at least ±10.
            double span = Math.Max(abs * 2.0, 10.0);
            min = v - span;
            max = v + span;
            if (!k.IsFloat)
            {
                min = Math.Floor(min);
                max = Math.Ceiling(max);
            }
            // Clamp non-negative for known-positive originals.
            if (v >= 0 && min < 0) min = 0;
        }

        private static int ValueToTick(double value, double min, double max, int ticks)
        {
            if (max <= min) return 0;
            double t = (value - min) / (max - min);
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return (int)Math.Round(t * ticks);
        }

        private static double TickToValue(int tick, double min, double max, int ticks)
        {
            if (ticks <= 0) return min;
            double t = (double)tick / ticks;
            return min + t * (max - min);
        }

        private void ResetKnobs()
        {
            // Re-scan from the original code to rebuild every row at original values
            // (handles numeric knobs and color swatches uniformly).
            ScanAndPopulate();
            _lblStatus.Text = "Reset to original values.";
        }

        private void ScheduleLivePreview()
        {
            if (_chkLivePreview == null || !_chkLivePreview.Checked) return;
            if (LivePreviewCallback == null) return;
            _liveTimer.Stop();
            _liveTimer.Start();
        }

        private void FireLivePreview()
        {
            if (LivePreviewCallback == null) return;
            foreach (var kv in _numerics)
                kv.Key.CurrentValue = (double)kv.Value.Value;
            string patched = RenderParameterScanner.ApplyEdits(_originalCode, _knobs);
            try { LivePreviewCallback(patched); _lblStatus.Text = "Live preview updated."; }
            catch (Exception ex) { _lblStatus.Text = "Live preview error: " + ex.Message; }
        }

        private void ApplyKnobs()
        {
            foreach (var kv in _numerics)
                kv.Key.CurrentValue = (double)kv.Value.Value;

            PatchedCode = RenderParameterScanner.ApplyEdits(_originalCode, _knobs);
            DidApply = true;
            _lblStatus.Text = "Patched code ready — close to apply to editor.";
            DialogResult = DialogResult.OK;
            Close();
        }

        // Preset format: a tiny line-based text file. One entry per line.
        // Numeric:  N|<key>=<invariant double>
        // Color:    C|<key>=<R>,<G>,<B>[,A]
        // Keys are stable per-knob using LiteralStart so presets target the same
        // source positions; values that no longer match are silently skipped.

        private static string MakeNumericKey(RenderParameterKnob k)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}@{1}", k.Name ?? "?", k.LiteralStart);
        }

        private static string MakeColorKey(RenderParameterKnob k)
        {
            int start = k.Color != null ? k.Color.R.LiteralStart : k.LiteralStart;
            return string.Format(CultureInfo.InvariantCulture, "{0}@{1}", k.Name ?? "color", start);
        }

        private void SavePreset()
        {
            // Pull latest numeric values from the UI before snapshotting.
            foreach (var kv in _numerics)
                kv.Key.CurrentValue = (double)kv.Value.Value;
            foreach (var kv in _colorReaders)
            {
                var c = kv.Value();
                kv.Key.Color.R.CurrentValue = c.R;
                kv.Key.Color.G.CurrentValue = c.G;
                kv.Key.Color.B.CurrentValue = c.B;
                if (kv.Key.Color.A != null) kv.Key.Color.A.CurrentValue = c.A;
            }

            using (var dlg = new SaveFileDialog
            {
                Title = "Save Inspector Preset",
                Filter = "Inspector preset (*.lcdpreset)|*.lcdpreset|All files (*.*)|*.*",
                DefaultExt = "lcdpreset",
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("# SESpriteLCDLayoutTool Render Parameter Preset v1");
                    foreach (var k in _knobs)
                    {
                        if (k.Color != null)
                        {
                            string key = MakeColorKey(k);
                            string val = k.Color.A != null
                                ? string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}",
                                    k.Color.R.CurrentValue, k.Color.G.CurrentValue, k.Color.B.CurrentValue, k.Color.A.CurrentValue)
                                : string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}",
                                    k.Color.R.CurrentValue, k.Color.G.CurrentValue, k.Color.B.CurrentValue);
                            sb.Append("C|").Append(key).Append('=').AppendLine(val);
                        }
                        else
                        {
                            string key = MakeNumericKey(k);
                            string val = k.CurrentValue.ToString("R", CultureInfo.InvariantCulture);
                            sb.Append("N|").Append(key).Append('=').AppendLine(val);
                        }
                    }
                    File.WriteAllText(dlg.FileName, sb.ToString());
                    _lblStatus.Text = "Preset saved: " + Path.GetFileName(dlg.FileName);
                }
                catch (Exception ex)
                {
                    _lblStatus.Text = "Save failed: " + ex.Message;
                }
            }
        }

        private void LoadPreset()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Load Inspector Preset",
                Filter = "Inspector preset (*.lcdpreset)|*.lcdpreset|All files (*.*)|*.*",
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var numericByKey = new Dictionary<string, double>(StringComparer.Ordinal);
                    var colorByKey = new Dictionary<string, int[]>(StringComparer.Ordinal);
                    foreach (string raw in File.ReadAllLines(dlg.FileName))
                    {
                        string line = raw == null ? "" : raw.Trim();
                        if (line.Length == 0 || line[0] == '#') continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 2) continue;
                        string prefix = line.Substring(0, 2);
                        string key = line.Substring(2, eq - 2);
                        string val = line.Substring(eq + 1);
                        if (prefix == "N|")
                        {
                            double d;
                            if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                                numericByKey[key] = d;
                        }
                        else if (prefix == "C|")
                        {
                            string[] parts = val.Split(',');
                            if (parts.Length < 3) continue;
                            int r, g, b, a = 255;
                            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) continue;
                            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out g)) continue;
                            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out b)) continue;
                            if (parts.Length >= 4) int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out a);
                            colorByKey[key] = new[] { r, g, b, a };
                        }
                    }

                    int applied = 0;
                    foreach (var k in _knobs)
                    {
                        if (k.Color != null)
                        {
                            int[] c;
                            if (colorByKey.TryGetValue(MakeColorKey(k), out c))
                            {
                                k.Color.R.CurrentValue = Clamp255(c[0]);
                                k.Color.G.CurrentValue = Clamp255(c[1]);
                                k.Color.B.CurrentValue = Clamp255(c[2]);
                                if (k.Color.A != null) k.Color.A.CurrentValue = Clamp255(c[3]);
                                applied++;
                            }
                        }
                        else
                        {
                            double d;
                            if (numericByKey.TryGetValue(MakeNumericKey(k), out d))
                            {
                                k.CurrentValue = d;
                                applied++;
                            }
                        }
                    }

                    // Rebuild the UI so numeric fields and color swatches reflect the loaded values.
                    ScanAndPopulateFromCurrent();
                    _lblStatus.Text = string.Format("Loaded preset: {0} knob(s) applied.", applied);
                    ScheduleLivePreview();
                }
                catch (Exception ex)
                {
                    _lblStatus.Text = "Load failed: " + ex.Message;
                }
            }
        }

        private static int Clamp255(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }

        // Re-renders rows using the existing _knobs (preserving CurrentValue) instead
        // of re-scanning the source, so loaded preset values survive the refresh.
        private void ScanAndPopulateFromCurrent()
        {
            _pnlKnobs.SuspendLayout();
            _pnlKnobs.Controls.Clear();
            _numerics.Clear();
            _colorReaders.Clear();

            var groupOrder = new List<string>();
            var byGroup = new Dictionary<string, List<RenderParameterKnob>>();
            foreach (var k in _knobs)
            {
                string g = string.IsNullOrEmpty(k.GroupKey) ? (k.Category ?? "(other)") : k.GroupKey;
                if (!byGroup.ContainsKey(g))
                {
                    byGroup[g] = new List<RenderParameterKnob>();
                    groupOrder.Add(g);
                }
                byGroup[g].Add(k);
            }
            if (groupOrder.Remove("Constants")) groupOrder.Insert(0, "Constants");
            foreach (string g in groupOrder) AddGroupHeader(g, byGroup[g]);

            _pnlKnobs.ResumeLayout();
        }
    }
}
