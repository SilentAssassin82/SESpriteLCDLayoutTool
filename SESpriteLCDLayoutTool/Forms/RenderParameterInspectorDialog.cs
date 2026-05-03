using System;
using System.Collections.Generic;
using System.Drawing;
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

            Controls.Add(_pnlKnobs);
            Controls.Add(bottom);
            Controls.Add(top);
        }

        private void ScanAndPopulate()
        {
            _pnlKnobs.SuspendLayout();
            _pnlKnobs.Controls.Clear();
            _numerics.Clear();
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
                var hdr = new Label
                {
                    Text = "── " + g + " (" + byGroup[g].Count + ") ──",
                    AutoSize = true,
                    ForeColor = Color.FromArgb(120, 200, 255),
                    Margin = new Padding(0, 8, 0, 4),
                };
                _pnlKnobs.Controls.Add(hdr);

                foreach (var k in byGroup[g])
                {
                    AddKnobRow(k);
                }
            }

            _lblStatus.Text = string.Format("{0} knob(s) detected across {1} group(s).", _knobs.Count, groupOrder.Count);
            _pnlKnobs.ResumeLayout();
        }

        private void AddKnobRow(RenderParameterKnob k)
        {
            int rowWidth = _pnlKnobs.ClientSize.Width - 24;
            if (rowWidth < 540) rowWidth = 540;
            var row = new Panel { Width = rowWidth, Height = 30, Margin = new Padding(0, 2, 0, 2) };
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
            var slider = new TrackBar
            {
                Minimum = 0,
                Maximum = kSliderTicks,
                TickStyle = TickStyle.None,
                Location = new Point(326, 0),
                Width = rowWidth - 326 - 130,
                Height = 30,
                BackColor = row.BackColor,
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
            foreach (var kv in _numerics)
            {
                try { kv.Value.Value = (decimal)kv.Key.OriginalValue; } catch { }
            }
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
    }
}
