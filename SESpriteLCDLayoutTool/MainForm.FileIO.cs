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
        // ── File I/O ──────────────────────────────────────────────────────────────
        private void OpenLayout()
        {
            using (var dlg = new OpenFileDialog { Filter = "LCD Layout (*.seld)|*.seld|All files (*.*)|*.*", Title = "Open Layout" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var xs = new XmlSerializer(typeof(LcdLayout));
                    using (var fs = File.OpenRead(dlg.FileName))
                        _layout = (LcdLayout)xs.Deserialize(fs);
                    _currentFile   = dlg.FileName;
                    _canvas.CanvasLayout = _layout;
                    RefreshLayerList();
                    ClearCodeDirty();

                    // Restore code style dropdown from saved source so animation
                    // and execution use the correct script type.
                    if (_layout.OriginalSourceCode != null)
                        AutoSwitchCodeStyle(_layout.OriginalSourceCode);

                    RefreshCode();
                    UpdateTitle();
                    SetStatus($"Opened: {Path.GetFileName(_currentFile)}");
                    RefreshDebugStats();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not open layout:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void SaveLayout(bool forceDialog)
        {
            if (forceDialog || _currentFile == null)
            {
                using (var dlg = new SaveFileDialog
                {
                    Filter   = "LCD Layout (*.seld)|*.seld|All files (*.*)|*.*",
                    Title    = "Save Layout",
                    FileName = _layout?.Name ?? "MyLayout",
                })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    _currentFile = dlg.FileName;
                }
            }

            try
            {
                var xs = new XmlSerializer(typeof(LcdLayout));
                using (var fs = File.Create(_currentFile))
                    xs.Serialize(fs, _layout);
                UpdateTitle();
                SetStatus($"Saved: {Path.GetFileName(_currentFile)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save layout:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ── Sprite import ─────────────────────────────────────────────────────────
        private void ShowImportSpriteDialog()
        {
            using (var dlg = new Form())
            {
                dlg.Text = "Import Sprite List from Space Engineers";
                dlg.Size = new Size(620, 520);
                dlg.MinimumSize = new Size(400, 340);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = Color.FromArgb(30, 30, 30);
                dlg.ForeColor = Color.FromArgb(220, 220, 220);
                dlg.Font = new Font("Segoe UI", 9f);

                const string PbScript =
                    "public void Main() {\r\n"
                  + "    var surface = Me.GetSurface(0);\r\n"
                  + "    var sprites = new List<string>();\r\n"
                  + "    surface.GetSprites(sprites);\r\n"
                  + "    sprites.Sort();\r\n"
                  + "    Me.CustomData = $\"// {sprites.Count} sprites found\\n\"\r\n"
                  + "                  + string.Join(\"\\n\", sprites);\r\n"
                  + "    Echo($\"Done \\u2014 {sprites.Count} sprites written to CustomData\");\r\n"
                  + "}";

                var lblInstructions = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 90,
                    Padding = new Padding(8, 8, 8, 4),
                    Text = "1. Copy the PB script below and paste it into a Programmable Block.\r\n"
                         + "2. Compile & Run — the script writes all sprite names to Custom Data.\r\n"
                         + "3. Copy the PB's Custom Data and paste it into the box below.\r\n\r\n"
                         + "(One sprite name per line — duplicates and built-in names are filtered automatically.)",
                    ForeColor = Color.FromArgb(180, 200, 255),
                    Font = new Font("Segoe UI", 9f),
                };

                var scriptToolbar = new FlowLayoutPanel
                {
                    Dock          = DockStyle.Top,
                    Height        = 34,
                    FlowDirection = FlowDirection.LeftToRight,
                    BackColor     = Color.FromArgb(30, 30, 30),
                    Padding       = new Padding(6, 4, 4, 0),
                };
                var btnCopyScript = DarkButton("\uD83D\uDCCB Copy PB Script to Clipboard", Color.FromArgb(0, 122, 60));
                btnCopyScript.Size = new Size(220, 26);
                btnCopyScript.Click += (s, e) =>
                {
                    Clipboard.SetText(PbScript);
                    SetStatus("PB script copied to clipboard — paste it into a Programmable Block in SE.");
                };
                var lblScriptHint = new Label
                {
                    Text      = "Copies the sprite-dump script for a Programmable Block",
                    Width     = 320,
                    Height    = 24,
                    ForeColor = Color.FromArgb(130, 130, 130),
                    Font      = new Font("Segoe UI", 8f, FontStyle.Italic),
                    TextAlign = ContentAlignment.MiddleLeft,
                };
                scriptToolbar.Controls.Add(btnCopyScript);
                scriptToolbar.Controls.Add(lblScriptHint);

                var txtPaste = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    BackColor = Color.FromArgb(20, 20, 20),
                    ForeColor = Color.FromArgb(200, 220, 200),
                    Font = new Font("Consolas", 9f),
                    AcceptsReturn = true,
                };

                var btnPanel = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    Height = 40,
                    FlowDirection = FlowDirection.RightToLeft,
                    BackColor = Color.FromArgb(30, 30, 30),
                    Padding = new Padding(4),
                };

                var btnImport = DarkButton("Import", Color.FromArgb(0, 100, 180));
                btnImport.Size = new Size(100, 28);
                var btnCancel = DarkButton("Cancel", Color.FromArgb(60, 60, 60));
                btnCancel.Size = new Size(80, 28);

                btnCancel.Click += (s, e) => dlg.Close();
                btnImport.Click += (s, e) =>
                {
                    int added = UserSpriteCatalog.Import(txtPaste.Text);
                    if (added > 0)
                    {
                        PopulateSpriteTree();
                        SetStatus($"Imported {added} new sprite names ({UserSpriteCatalog.Count} total)");
                    }
                    else
                    {
                        SetStatus("No new sprite names found in pasted text.");
                    }
                    dlg.Close();
                };

                var lblCount = new Label
                {
                    Text = UserSpriteCatalog.Count > 0
                        ? $"Currently {UserSpriteCatalog.Count} imported sprites on file."
                        : "No imported sprites yet.",
                    Dock = DockStyle.Bottom,
                    Height = 22,
                    ForeColor = Color.FromArgb(140, 140, 140),
                    Padding = new Padding(8, 2, 0, 0),
                };

                btnPanel.Controls.Add(btnImport);
                btnPanel.Controls.Add(btnCancel);

                dlg.Controls.Add(txtPaste);
                dlg.Controls.Add(scriptToolbar);
                dlg.Controls.Add(lblInstructions);
                dlg.Controls.Add(lblCount);
                dlg.Controls.Add(btnPanel);

                dlg.ShowDialog(this);
            }
        }

        private void ClearImportedSprites()
        {
            if (UserSpriteCatalog.Count == 0)
            {
                SetStatus("No imported sprites to clear.");
                return;
            }
            if (MessageBox.Show($"Remove all {UserSpriteCatalog.Count} imported sprite names?",
                    "Clear Imported Sprites", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            UserSpriteCatalog.Clear();
            PopulateSpriteTree();
            SetStatus("Imported sprite list cleared.");
        }

        // ── Paste layout code ─────────────────────────────────────────────────────
        private void ShowPasteLayoutDialog()
        {
            using (var dlg = new Form())
            {
                dlg.Text = "Paste Layout Code";
                dlg.Size = new Size(700, 660);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = Color.FromArgb(30, 30, 30);
                dlg.ForeColor = Color.FromArgb(220, 220, 220);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;

                var lblInstructions = new Label
                {
                    Text = "Paste SE LCD C# code containing MySprite definitions below.\n"
                         + "Supports PB scripts, mods, and plugins — initializer, constructor, and statement syntax.",
                    Dock = DockStyle.Top,
                    Height = 44,
                    Padding = new Padding(8, 8, 8, 0),
                };

                // ── Main code split: top = original code, bottom = snapshot ──
                var splitCode = new SplitContainer
                {
                    Dock = DockStyle.Fill,
                    Orientation = Orientation.Horizontal,
                    BackColor = Color.FromArgb(30, 30, 30),
                    Panel1MinSize = 120,
                    Panel2MinSize = 60,
                };

                var txtCode = new TextBox
                {
                    Multiline = true,
                    Dock = DockStyle.Fill,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    Font = new Font("Consolas", 9f),
                    BackColor = Color.FromArgb(20, 20, 20),
                    ForeColor = Color.FromArgb(212, 212, 212),
                    AcceptsTab = true,
                    MaxLength = 0,
                };
                bool txtCodeNormalizing = false;
                txtCode.TextChanged += (s, e) =>
                {
                    if (txtCodeNormalizing) return;
                    string t = txtCode.Text;
                    string n = t.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
                    if (n != t)
                    {
                        txtCodeNormalizing = true;
                        int pos = txtCode.SelectionStart;
                        txtCode.Text = n;
                        txtCode.SelectionStart = Math.Min(pos + (n.Length - t.Length), n.Length);
                        txtCodeNormalizing = false;
                    }
                };

                var lblSnapshot = new Label
                {
                    Text = "Runtime Snapshot (optional) — paste the output from the snapshot helper to apply real positions:",
                    Dock = DockStyle.Top,
                    Height = 28,
                    Padding = new Padding(8, 6, 8, 0),
                    ForeColor = Color.FromArgb(180, 200, 255),
                };

                var txtSnapshot = new TextBox
                {
                    Multiline = true,
                    Dock = DockStyle.Fill,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    Font = new Font("Consolas", 9f),
                    BackColor = Color.FromArgb(15, 18, 25),
                    ForeColor = Color.FromArgb(180, 210, 255),
                    AcceptsTab = true,
                    MaxLength = 0,
                };
                bool txtSnapNormalizing = false;
                txtSnapshot.TextChanged += (s, e) =>
                {
                    if (txtSnapNormalizing) return;
                    string t = txtSnapshot.Text;
                    string n = t.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
                    if (n != t)
                    {
                        txtSnapNormalizing = true;
                        int pos = txtSnapshot.SelectionStart;
                        txtSnapshot.Text = n;
                        txtSnapshot.SelectionStart = Math.Min(pos + (n.Length - t.Length), n.Length);
                        txtSnapNormalizing = false;
                    }
                };

                // ── Code-execution panel (sits above the snapshot box) ────────────────
                var lblCallPrefix = new Label
                {
                    Text = "▶ Execute call:",
                    Dock = DockStyle.Left,
                    AutoSize = false,
                    Width = 108,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(6, 0, 0, 0),
                    ForeColor = Color.FromArgb(130, 200, 255),
                };
                var lblExecResult = new Label
                {
                    Text = "–",
                    Dock = DockStyle.Right,
                    AutoSize = false,
                    Width = 130,
                    TextAlign = ContentAlignment.MiddleRight,
                    Padding = new Padding(0, 0, 6, 0),
                    ForeColor = Color.FromArgb(130, 130, 130),
                };
                var txtCallExpr = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Font = new Font("Consolas", 9f),
                    BackColor = Color.FromArgb(18, 24, 38),
                    ForeColor = Color.FromArgb(160, 220, 255),
                    BorderStyle = BorderStyle.FixedSingle,
                };
                var pnlExec = new Panel { Dock = DockStyle.Bottom, Height = 30 };
                pnlExec.Controls.Add(txtCallExpr);
                pnlExec.Controls.Add(lblExecResult);
                pnlExec.Controls.Add(lblCallPrefix);

                // ── Detected calls list ───────────────────────────────────────────
                var lblDetected = new Label
                {
                    Text      = "Detected methods (double-click to execute):",
                    Dock      = DockStyle.Bottom,
                    Height    = 18,
                    ForeColor = Color.FromArgb(130, 180, 230),
                    Font      = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                    Padding   = new Padding(4, 3, 0, 0),
                };
                var lstDetectedCalls = new ListBox
                {
                    Dock        = DockStyle.Bottom,
                    Height      = 60,
                    BackColor   = Color.FromArgb(22, 26, 38),
                    ForeColor   = Color.FromArgb(160, 220, 255),
                    BorderStyle = BorderStyle.None,
                    Font        = new Font("Consolas", 9f),
                };

                // Snapshot on top (paste first), code editor below with exec bar underneath
                splitCode.Panel1.Controls.Add(txtSnapshot);
                splitCode.Panel1.Controls.Add(lblSnapshot);
                splitCode.Panel2.Controls.Add(txtCode);
                splitCode.Panel2.Controls.Add(lstDetectedCalls);
                splitCode.Panel2.Controls.Add(lblDetected);
                splitCode.Panel2.Controls.Add(pnlExec);

                var chkReplace = new CheckBox
                {
                    Text = "Replace current layout (uncheck to append)",
                    Checked = true,
                    Dock = DockStyle.Bottom,
                    Height = 26,
                    Padding = new Padding(8, 4, 0, 0),
                    ForeColor = Color.FromArgb(200, 200, 200),
                };

                var chkReference = new CheckBox
                {
                    Text = "Import as reference layout (positions for visual reference only — commented out on export)",
                    Checked = false,
                    Dock = DockStyle.Bottom,
                    Height = 26,
                    Padding = new Padding(8, 2, 0, 0),
                    ForeColor = Color.FromArgb(180, 220, 180),
                };

                var btnPanel = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    FlowDirection = FlowDirection.RightToLeft,
                    Height = 36,
                    Padding = new Padding(4),
                };

                var btnImport = DarkButton("Import Sprites", Color.FromArgb(0, 122, 60));
                btnImport.Width = 130;
                var btnCancel = DarkButton("Cancel", Color.FromArgb(60, 60, 60));
                btnCancel.Width = 80;
                btnCancel.Click += (s, e) => dlg.Close();

                var btnSnapshot = DarkButton("📋 Copy Snapshot Helper", Color.FromArgb(100, 80, 0));
                btnSnapshot.Width = 170;
                btnSnapshot.Click += (s, e) =>
                {
                    Clipboard.SetText(CodeGenerator.GenerateSnapshotHelper(SelectedCodeStyle));
                    SetStatus("Snapshot helper script copied to clipboard!");
                };

                // ── Execution state (shared between Execute button and Import button) ──
                List<SpriteEntry> executedSprites = null;

                var btnExec = DarkButton("▶ Execute Code", Color.FromArgb(20, 80, 160));
                btnExec.Dock = DockStyle.Right;
                btnExec.Width = 120;
                pnlExec.Controls.Add(btnExec); // sits directly under the code editor
                btnExec.Click += (s, e) =>
                {
                    string call = txtCallExpr.Text.Trim();
                    if (string.IsNullOrWhiteSpace(call))
                    {
                        call = CodeExecutor.DetectCallExpression(txtCode.Text);
                        if (call == null)
                        {
                            // Fallback: if the code contains bare frame.Add(new MySprite patterns
                            // (e.g. snapshot output from a Torch plugin), parse sprites directly.
                            if (txtCode.Text.Contains("new MySprite"))
                            {
                                var parsed = Services.CodeParser.Parse(txtCode.Text);
                                if (parsed.Count > 0)
                                {
                                    executedSprites = parsed;
                                    lblExecResult.Text = "✔ " + parsed.Count + " sprites [Import]";
                                    lblExecResult.ForeColor = Color.FromArgb(80, 220, 100);
                                    return;
                                }
                            }

                            var st = CodeExecutor.DetectScriptType(txtCode.Text);
                            string hint = st == ScriptType.ProgrammableBlock
                                ? "Could not detect a Main() entry point in this PB script.\n\n"
                                  + "Enter the call expression manually, e.g.:\n"
                                  + "  Main(\"\", UpdateType.None)"
                                : st == ScriptType.PulsarPlugin
                                ? "Detected a Pulsar IPlugin class but could not find a render method\n"
                                  + "with an IMyTextSurface/IMyTextPanel parameter.\n\n"
                                  + "Enter the call expression manually, e.g.:\n"
                                  + "  DrawLayout(surface)"
                                : st == ScriptType.TorchPlugin
                                ? "Detected a Torch plugin class but could not find a render method.\n\n"
                                  + "Enter the call expression manually, e.g.:\n"
                                  + "  BuildSprites(new Vector2(512, 512))"
                                : st == ScriptType.ModSurface
                                ? "Could not detect a render method with an IMyTextSurface parameter.\n\n"
                                  + "Enter the call expression manually, e.g.:\n"
                                  + "  DrawHUD(surface)"
                                : "Could not detect a render method with a List<MySprite> parameter.\n\n"
                                  + "Enter the call expression manually in the 'Execute call' box, e.g.:\n"
                                  + "  RenderPanel(sprites, 512f, 10f, 1f)";
                            MessageBox.Show(hint,
                                "No Method Found", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            return;
                        }
                        txtCallExpr.Text = call;
                    }

                    lblExecResult.Text = "Running…";
                    lblExecResult.ForeColor = Color.FromArgb(200, 180, 60);
                    dlg.Refresh();

                    var execResult = CodeExecutor.Execute(txtCode.Text, call, capturedRows: _layout?.CapturedRows);
                    if (!execResult.Success)
                    {
                        executedSprites = null;
                        lblExecResult.Text = "✗ Error";
                        lblExecResult.ForeColor = Color.FromArgb(220, 80, 80);
                        MessageBox.Show(execResult.Error, "Execution Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    executedSprites = execResult.Sprites;
                    string typeTag = execResult.ScriptType == ScriptType.ProgrammableBlock ? " [PB]"
                                   : execResult.ScriptType == ScriptType.ModSurface        ? " [Mod]"
                                   : execResult.ScriptType == ScriptType.PulsarPlugin      ? " [Pulsar]"
                                   : execResult.ScriptType == ScriptType.TorchPlugin       ? " [Torch]"
                                   : " [LCD]";
                    lblExecResult.Text = "✔ " + executedSprites.Count + " sprites" + typeTag;
                    lblExecResult.ForeColor = Color.FromArgb(80, 220, 100);
                };

                // Auto-populate the detected calls list when the user leaves the code box
                txtCode.Leave += (s, e) =>
                {
                    var calls = CodeExecutor.DetectAllCallExpressions(txtCode.Text);
                    lstDetectedCalls.Items.Clear();
                    foreach (var c in calls)
                        lstDetectedCalls.Items.Add(c);
                    // Auto-fill the call box with the first detected call if empty
                    if (string.IsNullOrWhiteSpace(txtCallExpr.Text) && calls.Count > 0)
                        txtCallExpr.Text = calls[0];
                };

                // Single-click populates the call box
                lstDetectedCalls.SelectedIndexChanged += (s, e) =>
                {
                    if (lstDetectedCalls.SelectedItem != null)
                        txtCallExpr.Text = lstDetectedCalls.SelectedItem.ToString();
                };

                // Double-click executes the selected call
                lstDetectedCalls.MouseDoubleClick += (s, e) =>
                {
                    if (lstDetectedCalls.SelectedItem == null) return;
                    txtCallExpr.Text = lstDetectedCalls.SelectedItem.ToString();
                    btnExec.PerformClick();
                };

                btnImport.Click += (s, e) =>
                {
                    string sourceCode = txtCode.Text;
                    var sprites = CodeParser.Parse(sourceCode);
                    if (sprites.Count == 0)
                    {
                        MessageBox.Show(
                            "No MySprite definitions found in the pasted code.\n\n"
                            + "Supported patterns:\n"
                            + "  new MySprite { Type = SpriteType.TEXTURE, ... }\n"
                            + "  new MySprite(SpriteType.TEXTURE, \"data\", ...)\n"
                            + "  MySprite.CreateText(\"text\", ...)\n"
                            + "  sprite.Type = SpriteType.TEXTURE; sprite.Data = ...;",
                            "Nothing Found", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    bool isReference = chkReference.Checked;
                    if (isReference)
                    {
                        foreach (var sprite in sprites)
                            sprite.IsReferenceLayout = true;
                    }

                    // ── Per-sprite source tracking for dynamic round-trip ──
                    if (!isReference)
                    {
                        // Detect context labels and store baselines
                        var contextCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        bool hasDynamicPositions = false;
                        foreach (var sprite in sprites)
                        {
                            // Context label from surrounding case/method
                            string ctx = sprite.SourceStart >= 0
                                ? CodeGenerator.DetectSpriteContext(sourceCode, sprite.SourceStart)
                                : null;

                            string typeHint = sprite.Type == SpriteEntryType.Text
                                ? "Text"
                                : sprite.SpriteName ?? "Texture";

                            string label = ctx != null ? $"{ctx}: {typeHint}" : typeHint;

                            // Disambiguate duplicates within the same context
                            if (!contextCounts.TryGetValue(label, out int count))
                                count = 0;
                            contextCounts[label] = count + 1;
                            if (count > 0)
                                label += $".{count + 1}";

                            sprite.ImportLabel = label;
                            sprite.ImportBaseline = sprite.CloneValues();

                            // Check if positions look like expression-parsed defaults.
                            // Only flag as dynamic when the sprite's source text actually
                            // contains a Position property that couldn't be fully evaluated
                            // (parsed to 0,0).  When Position is absent the sprite
                            // intentionally uses the SE default center — don't move it.
                            if (sprite.SourceStart >= 0 && sprite.SourceEnd > sprite.SourceStart &&
                                sprite.SourceEnd <= sourceCode.Length)
                            {
                                string spriteText = sourceCode.Substring(sprite.SourceStart,
                                    sprite.SourceEnd - sprite.SourceStart);
                                bool hasPositionInSource = spriteText.IndexOf("Position", StringComparison.Ordinal) >= 0;
                                if (hasPositionInSource &&
                                    (sprite.X == 0f && sprite.Y == 0f))
                                    hasDynamicPositions = true;
                            }
                        }

                        // ── Snapshot merge: apply runtime positions ──
                        // Prefer executed sprites (from ▶ Execute Code) over the manual snapshot box.
                        // Execution gives real positions for ALL sprites including loop-generated ones.
                        if (executedSprites != null && executedSprites.Count > 0)
                        {
                            var mergeResult = SnapshotMerger.Merge(sprites, executedSprites);
                            SetStatus(mergeResult.Summary
                                + (mergeResult.UnmatchedSnapshots.Count > 0
                                    ? $"  {mergeResult.UnmatchedSnapshots.Count} loop/dynamic sprite(s) added as orphans."
                                    : ""));
                            hasDynamicPositions = false;

                            // Orphan sprites are loop- or expression-generated; they have accurate
                            // runtime positions but no source tracking (SourceStart stays -1).
                            foreach (var orphan in mergeResult.UnmatchedSnapshots)
                                sprites.Add(orphan);
                        }
                        else
                        {
                        string snapshotCode = txtSnapshot.Text;
                        if (!string.IsNullOrWhiteSpace(snapshotCode))
                        {
                            string snapTag = CodeParser.ParseSnapshotTag(snapshotCode);
                            var snapshotSprites = CodeParser.Parse(snapshotCode);
                            if (snapshotSprites.Count > 0)
                            {
                                var mergeResult = SnapshotMerger.Merge(sprites, snapshotSprites);
                                string tagInfo = snapTag != null ? $" [snapshot: {snapTag}]" : "";
                                SetStatus(mergeResult.Summary + tagInfo);
                                hasDynamicPositions = false; // snapshot resolved positions
                            }
                        }
                        }

                        // Auto-position sprites whose Position expression couldn't be evaluated
                        if (hasDynamicPositions)
                        {
                            float centerX = _layout.SurfaceWidth / 2f;
                            float yPos = 30f;
                            foreach (var sprite in sprites)
                            {
                                // Only reposition sprites whose source actually contains
                                // a Position property that parsed to zero (expression failure)
                                bool shouldMove = false;
                                bool shouldShrink = false;
                                if (sprite.SourceStart >= 0 && sprite.SourceEnd > sprite.SourceStart &&
                                    sprite.SourceEnd <= sourceCode.Length)
                                {
                                    string st = sourceCode.Substring(sprite.SourceStart,
                                        sprite.SourceEnd - sprite.SourceStart);
                                    shouldMove = st.IndexOf("Position", StringComparison.Ordinal) >= 0 &&
                                                 sprite.X == 0f && sprite.Y == 0f;
                                    shouldShrink = st.IndexOf("Size", StringComparison.Ordinal) >= 0 &&
                                                   sprite.Width == 100f && sprite.Height == 100f &&
                                                   sprite.Type == SpriteEntryType.Texture;
                                }

                                if (shouldMove)
                                {
                                    sprite.X = centerX;
                                    sprite.Y = yPos;
                                    yPos += 28f;
                                }
                                if (shouldShrink)
                                {
                                    sprite.Width = 24f;
                                    sprite.Height = 24f;
                                }
                            }
                        }
                    }

                    PushUndo();

                    // Sort sprites by SourceStart so layout order matches source order.
                    sprites.Sort((a, b) =>
                    {
                        if (a.SourceStart < 0 && b.SourceStart < 0) return 0;
                        if (a.SourceStart < 0) return 1;
                        if (b.SourceStart < 0) return -1;
                        return a.SourceStart.CompareTo(b.SourceStart);
                    });

                    if (chkReplace.Checked)
                        _layout.Sprites.Clear();

                    foreach (var sprite in sprites)
                        _layout.Sprites.Add(sprite);

                    // Store original source for round-trip code generation
                    if (!isReference)
                        _layout.OriginalSourceCode = sourceCode;

                    // Tag text sprites whose content originates from runtime game data
                    TagSnapshotSprites(sprites);

                    _canvas.SelectedSprite = sprites.Count > 0 ? sprites[0] : null;
                    _canvas.Invalidate();
                    RefreshLayerList();
                    ClearCodeDirty();

                    // Auto-switch code style based on detected script type
                    AutoSwitchCodeStyle(sourceCode);

                    RefreshCode();

                    string refNote = isReference ? " as reference" : "";
                    SetStatus($"Imported {sprites.Count} sprite(s){refNote} from pasted code.");
                    dlg.Close();
                };

                btnPanel.Controls.Add(btnImport);
                btnPanel.Controls.Add(btnCancel);
                btnPanel.Controls.Add(btnSnapshot);

                // Set splitter distance after adding to the form so layout is valid
                dlg.Load += (s, e) =>
                {
                    splitCode.SplitterDistance = Math.Max(80, splitCode.Height / 3);
                };

                dlg.Controls.Add(splitCode);
                dlg.Controls.Add(lblInstructions);
                dlg.Controls.Add(chkReference);
                dlg.Controls.Add(chkReplace);
                dlg.Controls.Add(btnPanel);

                dlg.ShowDialog(this);
            }
        }

        // ── Apply Runtime Snapshot ────────────────────────────────────────────────
        private void ShowApplySnapshotDialog()
        {
            // Create a blank layout if none exists — the snapshot will populate it
            if (_layout == null)
            {
                _layout = new LcdLayout();
                _canvas.CanvasLayout = _layout;
            }

            using (var dlg = new Form())
            {
                dlg.Text = "Apply Runtime Snapshot";
                dlg.Size = new Size(650, 420);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = Color.FromArgb(30, 30, 30);
                dlg.ForeColor = Color.FromArgb(220, 220, 220);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;

                var lblInfo = new Label
                {
                    Text = "Paste the runtime snapshot output (from the snapshot helper snippet) below.\n"
                         + "Positions and sizes will be merged into the current layout sprites.",
                    Dock = DockStyle.Top,
                    Height = 44,
                    Padding = new Padding(8, 8, 8, 0),
                    ForeColor = Color.FromArgb(180, 200, 255),
                };

                var txtSnap = new TextBox
                {
                    Multiline = true,
                    Dock = DockStyle.Fill,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    Font = new Font("Consolas", 9f),
                    BackColor = Color.FromArgb(15, 18, 25),
                    ForeColor = Color.FromArgb(180, 210, 255),
                    AcceptsTab = true,
                };

                var btnPanel = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    FlowDirection = FlowDirection.RightToLeft,
                    Height = 36,
                    Padding = new Padding(4),
                };

                var btnApply = DarkButton("Apply Snapshot", Color.FromArgb(0, 90, 140));
                btnApply.Width = 130;
                var btnCancel = DarkButton("Cancel", Color.FromArgb(60, 60, 60));
                btnCancel.Width = 80;
                btnCancel.Click += (s, e) => dlg.Close();

                btnApply.Click += (s, e) =>
                {
                    string snapCode = txtSnap.Text;
                    if (string.IsNullOrWhiteSpace(snapCode))
                    {
                        MessageBox.Show("Paste snapshot code first.", "Empty",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    string applyTag = CodeParser.ParseSnapshotTag(snapCode);
                    var snapshotSprites = CodeParser.Parse(snapCode);
                    var snapshotRows = CodeParser.ParseSnapshotRows(snapCode);
                    if (snapshotSprites.Count == 0)
                    {
                        MessageBox.Show(
                            "No MySprite definitions found in the snapshot text.",
                            "Nothing Found", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    // Collect editable sprites from layout
                    var editable = new List<SpriteEntry>();
                    foreach (var sp in _layout.Sprites)
                        if (!sp.IsReferenceLayout) editable.Add(sp);

                    PushUndo();
                    var result = SnapshotMerger.Merge(editable, snapshotSprites);

                    // Add any unmatched snapshot sprites directly to the layout
                    // (covers empty layouts, loop-generated extras, etc.)
                    foreach (var extra in result.UnmatchedSnapshots)
                        _layout.Sprites.Add(extra);

                    // Store captured row data for the execution engine
                    if (snapshotRows.Count > 0)
                        _layout.CapturedRows = snapshotRows;

                    _canvas.Invalidate();
                    RefreshLayerList();
                    RefreshCode();
                    string rowInfo = snapshotRows.Count > 0 ? $"  ({snapshotRows.Count} rows captured)" : "";
                    string applyTagInfo = applyTag != null ? $" [snapshot: {applyTag}]" : "";
                    SetStatus(result.Summary + rowInfo + applyTagInfo);
                    dlg.Close();
                };

                btnPanel.Controls.Add(btnApply);
                btnPanel.Controls.Add(btnCancel);

                dlg.Controls.Add(txtSnap);
                dlg.Controls.Add(lblInfo);
                dlg.Controls.Add(btnPanel);
                dlg.ShowDialog(this);
            }
        }

        // ── Sprite texture loading ────────────────────────────────────────────────
        private void LoadSpriteTexturesAsync(string contentPath)
        {
            if (string.IsNullOrWhiteSpace(contentPath) || !Directory.Exists(contentPath))
                return;

            _textureCache?.Dispose();
            _textureCache = null;
            _canvas.TextureCache = null;
            SetStatus("Loading sprite textures…");

            ThreadPool.QueueUserWorkItem(_ =>
            {
                var cache = new SpriteTextureCache();
                string result = cache.LoadFromContent(contentPath);

                BeginInvoke((Action)(() =>
                {
                    _textureCache = cache;
                    _canvas.TextureCache = cache;
                    AppSettings.GameContentPath = contentPath;
                    AppSettings.Save();
                    SetStatus($"Textures: {result}");
                }));
            });
        }

        private void BrowseGamePath()
        {
            using (var dlg = new FolderBrowserDialog
            {
                Description = "Select the Space Engineers Content directory\n(e.g. …\\SpaceEngineers\\Content)",
                ShowNewFolderButton = false,
            })
            {
                // Pre-select existing path if available
                if (!string.IsNullOrEmpty(AppSettings.GameContentPath) && Directory.Exists(AppSettings.GameContentPath))
                    dlg.SelectedPath = AppSettings.GameContentPath;

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string selected = dlg.SelectedPath;
                // If user picked the game root instead of Content, adjust
                if (!selected.EndsWith("Content", StringComparison.OrdinalIgnoreCase))
                {
                    string sub = Path.Combine(selected, "Content");
                    if (Directory.Exists(sub)) selected = sub;
                }

                string dataDir = Path.Combine(selected, "Data");
                if (!Directory.Exists(dataDir))
                {
                    MessageBox.Show("The selected folder doesn't appear to be an SE Content directory\n(no Data subfolder found).",
                        "Invalid Path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                LoadSpriteTexturesAsync(selected);
            }
        }

        private void AutoDetectGamePath()
        {
            string path = AppSettings.AutoDetectContentPath();
            if (path != null)
            {
                LoadSpriteTexturesAsync(path);
            }
            else
            {
                SetStatus("Could not auto-detect SE installation. Use View → Set SE Game Path to browse manually.");
                MessageBox.Show(
                    "Could not find a Space Engineers installation.\n\n" +
                    "Use View → Set SE Game Path… to manually browse to your\n" +
                    "SpaceEngineers\\Content directory.",
                    "SE Not Found", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void UnloadSpriteTextures()
        {
            _textureCache?.Dispose();
            _textureCache = null;
            _canvas.TextureCache = null;
            AppSettings.GameContentPath = null;
            AppSettings.Save();
            SetStatus("Sprite textures unloaded — using placeholder rendering.");
        }

        private void ShowTextureLoadErrors()
        {
            if (_textureCache == null || _textureCache.LoadErrors.Count == 0)
            {
                MessageBox.Show(
                    _textureCache == null
                        ? "No textures loaded — set the SE game path first."
                        : "All textures loaded successfully — no errors.",
                    "Texture Load Errors",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new Form())
            {
                dlg.Text = $"Texture Load Errors ({_textureCache.LoadErrors.Count})";
                dlg.Size = new Size(780, 520);
                dlg.MinimumSize = new Size(500, 300);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = Color.FromArgb(30, 30, 30);

                var info = new Label
                {
                    Text      = $"{_textureCache.LoadErrors.Count} texture(s) failed to load.  This is useful for debugging missing, corrupt, or unsupported textures in your mods.",
                    Dock      = DockStyle.Top,
                    AutoSize  = true,
                    Padding   = new Padding(8, 8, 8, 4),
                    ForeColor = Color.FromArgb(200, 200, 200),
                    Font      = new Font("Segoe UI", 9f),
                };

                var txt = new RichTextBox
                {
                    Dock      = DockStyle.Fill,
                    ReadOnly  = true,
                    BackColor = Color.FromArgb(14, 14, 14),
                    ForeColor = Color.FromArgb(212, 212, 212),
                    Font      = new Font("Consolas", 8.5f),
                    BorderStyle = BorderStyle.None,
                    WordWrap  = false,
                };

                var sb = new System.Text.StringBuilder();
                foreach (var err in _textureCache.LoadErrors)
                    sb.AppendLine(err);
                txt.Text = sb.ToString();

                var btnCopy = new Button
                {
                    Text      = "Copy to Clipboard",
                    Dock      = DockStyle.Bottom,
                    Height    = 32,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(0, 100, 180),
                    ForeColor = Color.White,
                };
                btnCopy.Click += (s, e) =>
                {
                    Clipboard.SetText(txt.Text);
                    btnCopy.Text = "✓ Copied!";
                };

                dlg.Controls.Add(txt);
                dlg.Controls.Add(info);
                dlg.Controls.Add(btnCopy);
                dlg.ShowDialog(this);
            }
        }

    }
}
