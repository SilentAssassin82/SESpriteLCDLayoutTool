using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SESpriteLCDLayoutTool.Models;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool
{
    /// <summary>
    /// Template gallery dialog that displays pre-built sprite templates.
    /// Users can preview templates, customize options, and insert generated code.
    /// </summary>
    public class TemplateGalleryDialog : Form
    {
        private ListBox _lstCategories;
        private FlowLayoutPanel _pnlTemplates;
        private RichTextBox _txtPreview;
        private Label _lblDescription;
        private ComboBox _cmbTargetScript;
        private NumericUpDown _numWidth;
        private NumericUpDown _numHeight;
        private TemplateDefinition _selectedTemplate;

        // SplitContainers (distances set in Load event)
        private SplitContainer _splitMain;
        private SplitContainer _splitRight;

        /// <summary>The generated code to insert (set when user clicks Insert).</summary>
        public string GeneratedCode { get; private set; }

        /// <summary>
        /// Compatibility tier of the template the user inserted, so callers
        /// can decide whether the smart-merge / Update Code path is allowed.
        /// </summary>
        public TemplateCompatibility GeneratedCompatibility { get; private set; }
            = TemplateCompatibility.Safe;

        /// <summary>Current surface dimensions from the layout.</summary>
        private float _surfaceWidth = 512f;
        private float _surfaceHeight = 512f;

        public TemplateGalleryDialog(float surfaceWidth = 512f, float surfaceHeight = 512f, int defaultTargetIndex = 0)
        {
            _surfaceWidth = surfaceWidth;
            _surfaceHeight = surfaceHeight;

            Text = "Template Gallery — Insert Pre-Built Sprite Patterns";
            Size = new Size(960, 680);
            MinimumSize = new Size(750, 520);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(30, 30, 30);
            ForeColor = Color.FromArgb(220, 220, 220);
            Font = new Font("Segoe UI", 9f);

            BuildUI(defaultTargetIndex);
            PopulateCategories();
            SelectCategory(null); // Show all

            // Set all SplitContainer sizing properties in Load event after form is laid out
            Load += (s, e) =>
            {
                _splitMain.Panel1MinSize = 120;
                _splitMain.Panel2MinSize = 400;
                _splitMain.SplitterDistance = Math.Max(120,
                    Math.Min(_splitMain.Width - 400 - _splitMain.SplitterWidth, 150));

                _splitRight.Panel1MinSize = 150;
                _splitRight.Panel2MinSize = 150;
                _splitRight.SplitterDistance = Math.Max(150,
                    Math.Min(_splitRight.Height - 150 - _splitRight.SplitterWidth, 280));
            };
        }

        private void BuildUI(int defaultTargetIndex)
        {
            // ═══════════════════════════════════════════════════════════════════
            // MAIN SPLIT: Left (categories) | Right (templates + preview)
            // ═══════════════════════════════════════════════════════════════════

            _splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                BackColor = Color.FromArgb(30, 30, 30),
            };

            // ── Left panel: Categories ──
            var pnlLeft = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(35, 35, 40),
                Padding = new Padding(4),
            };

            var lblCategories = new Label
            {
                Text = "CATEGORIES",
                Dock = DockStyle.Top,
                Height = 28,
                ForeColor = Color.FromArgb(140, 140, 140),
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                Padding = new Padding(4, 8, 0, 0),
            };

            _lstCategories = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(35, 35, 40),
                ForeColor = Color.FromArgb(220, 220, 220),
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 9f),
                IntegralHeight = false,
            };
            _lstCategories.SelectedIndexChanged += OnCategorySelected;

            pnlLeft.Controls.Add(_lstCategories);
            pnlLeft.Controls.Add(lblCategories);
            _splitMain.Panel1.Controls.Add(pnlLeft);

            // ── Right panel split: Top (templates) | Bottom (preview) ──
            _splitRight = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                BackColor = Color.FromArgb(30, 30, 30),
            };

            // ── Templates grid ──
            var pnlTemplatesOuter = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(25, 25, 28),
                Padding = new Padding(4),
            };

            var lblTemplates = new Label
            {
                Text = "TEMPLATES",
                Dock = DockStyle.Top,
                Height = 28,
                ForeColor = Color.FromArgb(140, 140, 140),
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                Padding = new Padding(4, 8, 0, 0),
            };

            _pnlTemplates = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(25, 25, 28),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(8),
            };

            pnlTemplatesOuter.Controls.Add(_pnlTemplates);
            pnlTemplatesOuter.Controls.Add(lblTemplates);
            _splitRight.Panel1.Controls.Add(pnlTemplatesOuter);

            // ── Preview panel ──
            var pnlPreviewOuter = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 20, 22),
                Padding = new Padding(4),
            };

            // Description label
            _lblDescription = new Label
            {
                Dock = DockStyle.Top,
                Height = 72,
                ForeColor = Color.FromArgb(180, 200, 255),
                Font = new Font("Segoe UI", 9f),
                Padding = new Padding(4, 4, 4, 4),
                Text = "Select a template to see its description and preview.",
            };

            // Code preview
            _txtPreview = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.FromArgb(14, 14, 16),
                ForeColor = Color.FromArgb(200, 220, 200),
                Font = new Font("Consolas", 9f),
                BorderStyle = BorderStyle.None,
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both,
            };

            pnlPreviewOuter.Controls.Add(_txtPreview);
            pnlPreviewOuter.Controls.Add(_lblDescription);
            _splitRight.Panel2.Controls.Add(pnlPreviewOuter);

            _splitMain.Panel2.Controls.Add(_splitRight);
            Controls.Add(_splitMain);

            // ═══════════════════════════════════════════════════════════════════
            // TOP OPTIONS BAR
            // ═══════════════════════════════════════════════════════════════════

            var pnlOptions = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 40,
                ColumnCount = 6,
                Padding = new Padding(8, 6, 8, 6),
                BackColor = Color.FromArgb(35, 35, 40),
            };
            pnlOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));  // "Target:"
            pnlOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140)); // combo
            pnlOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));  // "Surface:"
            pnlOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));  // width
            pnlOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20));  // "×"
            pnlOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));  // height

            pnlOptions.Controls.Add(new Label
            {
                Text = "Target script:",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(200, 200, 200),
            }, 0, 0);

            _cmbTargetScript = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(50, 50, 55),
                ForeColor = Color.FromArgb(220, 220, 220),
            };
            _cmbTargetScript.Items.AddRange(new[] { "PB (In-Game)", "Mod", "Plugin / Torch", "Pulsar", "LCD Helper" });
            _cmbTargetScript.SelectedIndex = Math.Min(defaultTargetIndex, 4);
            _cmbTargetScript.SelectedIndexChanged += (s, e) => RefreshPreview();
            pnlOptions.Controls.Add(_cmbTargetScript, 1, 0);

            pnlOptions.Controls.Add(new Label
            {
                Text = "Surface:",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.FromArgb(200, 200, 200),
            }, 2, 0);

            _numWidth = new NumericUpDown
            {
                Dock = DockStyle.Fill,
                Minimum = 64,
                Maximum = 2048,
                Value = (decimal)_surfaceWidth,
                BackColor = Color.FromArgb(50, 50, 55),
                ForeColor = Color.White,
            };
            _numWidth.ValueChanged += (s, e) => { _surfaceWidth = (float)_numWidth.Value; RefreshPreview(); };
            pnlOptions.Controls.Add(_numWidth, 3, 0);

            pnlOptions.Controls.Add(new Label
            {
                Text = "×",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(150, 150, 150),
            }, 4, 0);

            _numHeight = new NumericUpDown
            {
                Dock = DockStyle.Fill,
                Minimum = 64,
                Maximum = 2048,
                Value = (decimal)_surfaceHeight,
                BackColor = Color.FromArgb(50, 50, 55),
                ForeColor = Color.White,
            };
            _numHeight.ValueChanged += (s, e) => { _surfaceHeight = (float)_numHeight.Value; RefreshPreview(); };
            pnlOptions.Controls.Add(_numHeight, 5, 0);

            Controls.Add(pnlOptions);

            // ═══════════════════════════════════════════════════════════════════
            // BOTTOM TOOLBAR
            // ═══════════════════════════════════════════════════════════════════

            var pnlToolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                FlowDirection = FlowDirection.RightToLeft,
                BackColor = Color.FromArgb(30, 30, 30),
                Padding = new Padding(8, 6, 8, 6),
            };

            var btnClose = CreateButton("Close", Color.FromArgb(70, 70, 70), 80);
            btnClose.Click += (s, e) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };

            var btnCopy = CreateButton("📋 Copy Code", Color.FromArgb(0, 100, 180), 120);
            btnCopy.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(_txtPreview.Text))
                {
                    Clipboard.SetText(_txtPreview.Text);
                    _lblDescription.Text = "✓ Code copied to clipboard!";
                }
            };

            var btnInsert = CreateButton("📥 Insert Template", Color.FromArgb(0, 130, 80), 140);
            btnInsert.Click += (s, e) =>
            {
                if (_selectedTemplate != null)
                {
                    GeneratedCode = _txtPreview.Text;
                    GeneratedCompatibility = _selectedTemplate.Compatibility;
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };

            pnlToolbar.Controls.Add(btnClose);
            pnlToolbar.Controls.Add(btnCopy);
            pnlToolbar.Controls.Add(btnInsert);

            Controls.Add(pnlToolbar);
        }

        private Button CreateButton(string text, Color backColor, int width)
        {
            var btn = new Button
            {
                Text = text,
                Width = width,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = backColor,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f),
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private void PopulateCategories()
        {
            _lstCategories.Items.Clear();
            _lstCategories.Items.Add("All Templates");

            foreach (var cat in TemplateGallery.GetCategories())
                _lstCategories.Items.Add(cat);

            _lstCategories.SelectedIndex = 0;
        }

        private void OnCategorySelected(object sender, EventArgs e)
        {
            string selected = _lstCategories.SelectedItem?.ToString();
            if (selected == "All Templates")
                SelectCategory(null);
            else
                SelectCategory(selected);
        }

        private void SelectCategory(string category)
        {
            _pnlTemplates.Controls.Clear();

            var templates = TemplateGallery.GetByCategory(category);
            foreach (var template in templates)
            {
                var card = CreateTemplateCard(template);
                _pnlTemplates.Controls.Add(card);
            }

            // Clear selection
            _selectedTemplate = null;
            _lblDescription.Text = $"Select a template to preview. ({templates.Count} templates available)";
            _txtPreview.Clear();
        }

        private Panel CreateTemplateCard(TemplateDefinition template)
        {
            var card = new Panel
            {
                Width = 180,
                Height = 120,
                BackColor = Color.FromArgb(40, 40, 45),
                Margin = new Padding(6),
                Cursor = Cursors.Hand,
                Tag = template,
            };

            // Icon/preview area
            var pnlIcon = new Panel
            {
                Dock = DockStyle.Top,
                Height = 60,
                BackColor = Color.FromArgb(30, 30, 35),
            };

            // Simple colored shape as preview
            var lblIcon = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 24f),
                ForeColor = template.PreviewColor,
                Text = GetIconForTemplate(template),
            };
            pnlIcon.Controls.Add(lblIcon);

            // Animated badge
            if (template.IsAnimated)
            {
                var lblAnimated = new Label
                {
                    Text = "⚡",
                    AutoSize = true,
                    BackColor = Color.Transparent,
                    ForeColor = Color.FromArgb(255, 200, 80),
                    Font = new Font("Segoe UI", 10f),
                    Location = new Point(4, 4),
                };
                pnlIcon.Controls.Add(lblAnimated);
                lblAnimated.BringToFront();
            }

            // Title
            var lblTitle = new Label
            {
                Dock = DockStyle.Fill,
                Text = template.Name,
                ForeColor = Color.FromArgb(220, 220, 220),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(4),
            };

            // Category hint
            var lblCategory = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 18,
                Text = template.Category,
                ForeColor = Color.FromArgb(120, 120, 140),
                Font = new Font("Segoe UI", 7f),
                TextAlign = ContentAlignment.MiddleCenter,
            };

            card.Controls.Add(lblTitle);
            card.Controls.Add(lblCategory);
            card.Controls.Add(pnlIcon);

            // Click handlers
            Action selectCard = () => SelectTemplate(template, card);
            card.Click += (s, e) => selectCard();
            pnlIcon.Click += (s, e) => selectCard();
            lblIcon.Click += (s, e) => selectCard();
            lblTitle.Click += (s, e) => selectCard();
            lblCategory.Click += (s, e) => selectCard();

            // Hover effect
            Action<bool> setHover = (hover) =>
            {
                card.BackColor = hover ? Color.FromArgb(50, 55, 65) : Color.FromArgb(40, 40, 45);
            };
            card.MouseEnter += (s, e) => setHover(true);
            card.MouseLeave += (s, e) => setHover(_selectedTemplate == template);
            pnlIcon.MouseEnter += (s, e) => setHover(true);
            pnlIcon.MouseLeave += (s, e) => setHover(_selectedTemplate == template);

            return card;
        }

        private string GetIconForTemplate(TemplateDefinition template)
        {
            // Return an emoji based on category/type
            switch (template.Category)
            {
                case "Progress Bars": return "▰";
                case "Animations": return "◉";
                case "Status Indicators": return "●";
                case "Layout Helpers": return "▢";
                case "Gauges & Meters": return "◔";
                default: return "◆";
            }
        }

        private void SelectTemplate(TemplateDefinition template, Panel card)
        {
            // Deselect previous
            foreach (Control c in _pnlTemplates.Controls)
            {
                if (c is Panel p)
                    p.BackColor = Color.FromArgb(40, 40, 45);
            }

            // Select new
            _selectedTemplate = template;
            card.BackColor = Color.FromArgb(50, 70, 90);

            // Update description
            string animatedNote = template.IsAnimated ? " ⚡ Animated (requires tick updates)" : "";
            string compatLine = BuildCompatibilityLine(template);
            _lblDescription.Text =
                $"{template.Name}{animatedNote}\n{template.Description}" +
                (string.IsNullOrEmpty(compatLine) ? "" : "\n" + compatLine);

            RefreshPreview();
        }

        // ──────────────────────────────────────────────────────────────────
        //  Compatibility badge / note helpers
        // ──────────────────────────────────────────────────────────────────

        private static Label CreateCompatibilityBadge(TemplateCompatibility c)
        {
            string text;
            Color bg;
            switch (c)
            {
                case TemplateCompatibility.Safe:
                    text = "✓ Injector-Safe";
                    bg = Color.FromArgb(40, 110, 60);
                    break;
                case TemplateCompatibility.Standalone:
                    text = "⚡ Standalone";
                    bg = Color.FromArgb(140, 100, 30);
                    break;
                case TemplateCompatibility.Conflicting:
                    text = "⚠ Manual Insert";
                    bg = Color.FromArgb(150, 50, 50);
                    break;
                default:
                    return null;
            }

            return new Label
            {
                Text = text,
                AutoSize = true,
                BackColor = bg,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 7f, FontStyle.Bold),
                Padding = new Padding(4, 1, 4, 1),
            };
        }

        private static string BuildCompatibilityLine(TemplateDefinition template)
        {
            switch (template.Compatibility)
            {
                case TemplateCompatibility.Safe:
                    return null;
                case TemplateCompatibility.Standalone:
                    return "⚡ Standalone — " +
                        (string.IsNullOrEmpty(template.CompatibilityNote)
                            ? "Self-contained animation. Update Code / smart-merge is disabled for this template."
                            : template.CompatibilityNote);
                case TemplateCompatibility.Conflicting:
                    return "⚠ Manual insert only — " +
                        (string.IsNullOrEmpty(template.CompatibilityNote)
                            ? "May conflict with the animation injector if mixed with keyframe effects on the same sprite."
                            : template.CompatibilityNote);
                default:
                    return null;
            }
        }

        private void RefreshPreview()
        {
            if (_selectedTemplate == null)
            {
                _txtPreview.Clear();
                return;
            }

            var target = MapIndexToTarget(_cmbTargetScript.SelectedIndex);
            string code = _selectedTemplate.GenerateCode(_surfaceWidth, _surfaceHeight, target);
            _txtPreview.Text = code;
        }

        private TargetScriptType MapIndexToTarget(int index)
        {
            switch (index)
            {
                case 0: return TargetScriptType.ProgrammableBlock;
                case 1: return TargetScriptType.Mod;
                case 2: return TargetScriptType.Plugin;
                case 3: return TargetScriptType.Pulsar;
                case 4: return TargetScriptType.LcdHelper;
                default: return TargetScriptType.ProgrammableBlock;
            }
        }
    }
}
