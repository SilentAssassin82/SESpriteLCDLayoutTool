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

        /// <summary>Patched source to write back to the code box after Apply.</summary>
        public string PatchedCode { get; private set; }

        /// <summary>True once Apply has produced patched code.</summary>
        public bool DidApply { get; private set; }

        public RenderParameterInspectorDialog(string code, IList<string> renderMethodNames, string defaultMethod)
        {
            _originalCode = code ?? string.Empty;
            _knobs = new List<RenderParameterKnob>();

            Text = "Render Parameter Inspector";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(520, 640);
            MinimumSize = new Size(420, 480);
            BackColor = Color.FromArgb(30, 30, 30);
            ForeColor = Color.FromArgb(220, 220, 220);
            Font = new Font("Segoe UI", 9f);

            BuildUi(renderMethodNames, defaultMethod);
            ScanAndPopulate();
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

            string lastCategory = null;
            foreach (var k in _knobs)
            {
                if (k.Category != lastCategory)
                {
                    var hdr = new Label
                    {
                        Text = "── " + (k.Category ?? "") + " ──",
                        AutoSize = true,
                        ForeColor = Color.FromArgb(120, 200, 255),
                        Margin = new Padding(0, 8, 0, 4),
                    };
                    _pnlKnobs.Controls.Add(hdr);
                    lastCategory = k.Category;
                }

                var row = new Panel { Width = _pnlKnobs.ClientSize.Width - 24, Height = 26, Margin = new Padding(0, 2, 0, 2) };
                var nameLbl = new Label
                {
                    Text = k.Name,
                    AutoSize = false,
                    Width = 240,
                    Location = new Point(4, 4),
                    ForeColor = ForeColor,
                };
                var num = new NumericUpDown
                {
                    DecimalPlaces = k.IsFloat ? 4 : 0,
                    Increment = k.IsFloat ? 0.5m : 1m,
                    Minimum = -10000m,
                    Maximum = 10000m,
                    Location = new Point(252, 2),
                    Width = 100,
                    BackColor = Color.FromArgb(45, 45, 48),
                    ForeColor = Color.FromArgb(220, 220, 220),
                };
                try { num.Value = (decimal)k.CurrentValue; } catch { }
                var origLbl = new Label
                {
                    Text = "(was " + k.OriginalLiteral + ")",
                    AutoSize = true,
                    Location = new Point(360, 6),
                    ForeColor = Color.FromArgb(140, 140, 140),
                };
                row.Controls.Add(nameLbl);
                row.Controls.Add(num);
                row.Controls.Add(origLbl);
                _pnlKnobs.Controls.Add(row);
                _numerics[k] = num;
            }

            _lblStatus.Text = string.Format("{0} knob(s) detected.", _knobs.Count);
            _pnlKnobs.ResumeLayout();
        }

        private void ResetKnobs()
        {
            foreach (var kv in _numerics)
            {
                try { kv.Value.Value = (decimal)kv.Key.OriginalValue; } catch { }
            }
            _lblStatus.Text = "Reset to original values.";
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
