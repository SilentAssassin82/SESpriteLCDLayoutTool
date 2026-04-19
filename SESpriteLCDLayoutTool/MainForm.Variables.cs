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
        // ── Variable Inspector Test ───────────────────────────────────────────────

        /// <summary>
        /// Tests the variable inspector MVP by compiling the current code as an animation
        /// and displaying all instance fields using reflection.
        ///
        /// USAGE:
        /// 1. Paste/write code with class-level fields (e.g., int counter; float angle;)
        /// 2. Click "🔍 Inspect Variables" in the code panel toolbar
        /// 3. See all instance fields and their initial values after constructor runs
        ///
        /// EXAMPLE CODE TO TEST:
        /// <code>
        /// int counter = 0;
        /// float angle = 0f;
        /// string[] items = new string[] { "foo", "bar", "baz" };
        ///
        /// public void DrawHUD(IMyTextSurface surface) {
        ///     counter++;
        ///     angle += 0.1f;
        ///     // ... sprite drawing code ...
        /// }
        /// </code>
        /// </summary>
        private void TestVariableInspector()
        {
            string code = _codeBox.Text;
            if (string.IsNullOrWhiteSpace(code))
            {
                MessageBox.Show("No code to inspect.", "Variable Inspector", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string call = _execCallBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(call) && _detectedMethods != null && _detectedMethods.Count > 0)
            {
                call = _detectedMethods[0].CallExpression;
            }

            // Use AnimationPlayer to compile and initialize the script
            using (var animPlayer = new AnimationPlayer(this))
            {
                string prepError = animPlayer.Prepare(code, call, _layout?.CapturedRows);
                if (prepError != null)
                {
                    MessageBox.Show(prepError, "Variable Inspector - Compile Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Inspect all instance fields
                var fields = animPlayer.InspectFields();
                if (fields == null || fields.Count == 0)
                {
                    MessageBox.Show("No instance fields found.", "Variable Inspector", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // Populate ListView
                _lstVariables.Items.Clear();
                foreach (var kv in fields.OrderBy(x => x.Key))
                {
                    string value = FormatFieldValue(kv.Value);
                    var item = new ListViewItem(new[] { kv.Key, value });
                    item.ForeColor = Color.FromArgb(160, 220, 255);
                    _lstVariables.Items.Add(item);
                }

                // Format the output for MessageBox
                var output = new System.Text.StringBuilder();
                output.AppendLine($"Script Type: {animPlayer.ScriptType}");
                output.AppendLine($"Instance Fields ({fields.Count}):\n");

                foreach (var kv in fields.OrderBy(x => x.Key))
                {
                    string value = FormatFieldValue(kv.Value);
                    output.AppendLine($"{kv.Key} = {value}");
                }

                // Show in a scrollable message box + switch to Variables tab
                MessageBox.Show(output.ToString(), "Variable Inspector", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        /// <summary>
        /// Formats a field value for display in the variable inspector.
        /// </summary>
        private string FormatFieldValue(object value)
        {
            if (value == null)
                return "(null)";

            if (value is string str)
                return $"\"{str}\"";

            if (value is System.Collections.IEnumerable enumerable && !(value is string))
            {
                var items = new List<string>();
                int count = 0;
                foreach (var item in enumerable)
                {
                    if (count >= 5)
                    {
                        items.Add("...");
                        break;
                    }
                    items.Add(item?.ToString() ?? "(null)");
                    count++;
                }
                return $"[{string.Join(", ", items)}] (count: {count}{(count >= 5 ? "+" : "")})";
            }

            return value.ToString();
        }

        /// <summary>
        /// Gets the display color for a variable based on its type.
        /// VIBRANT colors for visibility on dark backgrounds.
        /// </summary>
        private Color GetColorForType(object value)
        {
            if (value == null)
                return Color.FromArgb(160, 160, 160); // Brighter gray for null

            var type = value.GetType();

            // Numeric types - Bright blue shades
            if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
                type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte))
                return Color.FromArgb(80, 160, 255); // Vivid blue for integers

            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                return Color.FromArgb(0, 220, 255); // Bright cyan for floats

            // String - Bright green
            if (type == typeof(string))
                return Color.FromArgb(80, 255, 120); // Vivid green

            // Boolean - Bright orange
            if (type == typeof(bool))
                return Color.FromArgb(255, 160, 60); // Vivid orange

            // VRageMath types - Bright purple/magenta
            if (type.Name.Contains("Vector") || type.Name.Contains("Matrix"))
                return Color.FromArgb(220, 100, 255); // Vivid purple for vectors/matrices

            if (type.Name.Contains("Color"))
                return Color.FromArgb(255, 220, 60); // Bright yellow for colors

            // Collections - Aqua
            if (value is System.Collections.IEnumerable && !(value is string))
                return Color.FromArgb(100, 200, 255); // Aqua for arrays/lists

            // Default for objects
            return Color.FromArgb(140, 200, 255); // Default light blue
        }

        /// <summary>
        /// Brightens a color to indicate a changed value during animation.
        /// </summary>
        private Color BrightenColor(Color baseColor)
        {
            // Increase RGB values by 30% and add yellow tint for visibility
            int r = Math.Min(255, baseColor.R + (int)((255 - baseColor.R) * 0.4f) + 20);
            int g = Math.Min(255, baseColor.G + (int)((255 - baseColor.G) * 0.4f) + 20);
            int b = Math.Min(255, baseColor.B + (int)((255 - baseColor.B) * 0.2f)); // Less blue boost
            return Color.FromArgb(r, g, b);
        }

        /// <summary>
        /// Automatically inspects variables after Execute Code completes successfully.
        /// Populates the Variables tab and writes to Debug output.
        /// When <paramref name="ctx"/> is non-null the already-compiled context is
        /// adopted (zero-cost), avoiding a second Roslyn compilation.  Falls back
        /// to the legacy compile path when ctx is null.
        /// </summary>
        private void AutoInspectVariablesAfterExecution(CodeExecutor.AnimationContext ctx)
        {
            try
            {
                // Dispose previous AnimationPlayer only if it's NOT the same as _animPlayer
                if (_lastAnimPlayer != null && _lastAnimPlayer != _animPlayer)
                {
                    _lastAnimPlayer.Dispose();
                }

                if (ctx != null)
                {
                    _lastAnimPlayer = new AnimationPlayer(this);
                    _lastAnimPlayer.AdoptContext(ctx);
                }
                else
                {
                    _lastAnimPlayer = null;
                    return;
                }

                var fields = _lastAnimPlayer.InspectFields();
                if (fields == null || fields.Count == 0)
                    return;

                // Populate ListView
                _lstVariables.Items.Clear();
                foreach (var kv in fields.OrderBy(x => x.Key))
                {
                    if (!ShouldShowField(kv.Key))
                        continue; // Skip internal fields

                    string value = FormatFieldValue(kv.Value);
                    var item = new ListViewItem(new[] { kv.Key, value, "" });
                    item.ForeColor = GetColorForType(kv.Value); // Type-based color coding
                    _lstVariables.Items.Add(item);
                }

                // Update tab title (no frame number for initial Execute)
                if (_tabVariables != null)
                {
                    _tabVariables.Text = "Variables";
                }

                // Write to Debug output for reference
                System.Diagnostics.Debug.WriteLine("\n========== VARIABLE INSPECTION ==========");
                System.Diagnostics.Debug.WriteLine($"Script Type: {_lastAnimPlayer.ScriptType}");
                System.Diagnostics.Debug.WriteLine($"Instance Fields ({fields.Count}):");

                foreach (var kv in fields.OrderBy(x => x.Key))
                {
                    string value = FormatFieldValue(kv.Value);
                    System.Diagnostics.Debug.WriteLine($"  {kv.Key} = {value}");
                }

                System.Diagnostics.Debug.WriteLine("=========================================\n");

                // Evaluate watch expressions with initial field values
                EvaluateWatches(_lastAnimPlayer);
            }
            catch (Exception ex)
            {
                // Silent failure - don't interrupt successful execution
                System.Diagnostics.Debug.WriteLine($"[AutoInspect] Error: {ex.Message}");
                if (_lastAnimPlayer != null && _lastAnimPlayer != _animPlayer)
                {
                    _lastAnimPlayer.Dispose();
                }
                _lastAnimPlayer = null;
            }
        }

        /// <summary>
        /// Determines if a field should be shown based on the "Show internal fields" checkbox.
        /// </summary>
        // Infrastructure fields injected by the code builder — always hidden regardless of script type.
        private static readonly System.Collections.Generic.HashSet<string> _infraFields =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
            {
                "_echoLog", "_stubSurface", "_methodTimings",
                "Runtime", "Me", "GridTerminalSystem", "Storage",
            };

        private bool ShouldShowField(string fieldName)
        {
            if (_chkShowInternalFields?.Checked == true)
                return true;

            // Always hide compiler-generated fields and injected infrastructure.
            if (fieldName.StartsWith("<")) return false;
            if (_infraFields.Contains(fieldName)) return false;

            // For mod/plugin scripts, the SE _ prefix is user convention, not infrastructure.
            // Show those fields so the Variables panel isn't empty for mods.
            var scriptType = (_animPlayer ?? _lastAnimPlayer)?.ScriptType;
            bool isMod = scriptType == ScriptType.ModSurface
                      || scriptType == ScriptType.PulsarPlugin
                      || scriptType == ScriptType.TorchPlugin;
            if (isMod) return true;

            // PB / LCD helper: hide _ prefix (tool-injected stubs like _tick, _h2, etc.)
            if (fieldName.StartsWith("_")) return false;

            return true;
        }

        /// <summary>
        /// Updates the Variables tab during animation playback to show live field values.
        /// </summary>
        private void UpdateVariablesDuringAnimation(int tick)
        {
            // If we have a dedicated animation player (not _lastAnimPlayer from Execute),
            // use it for inspection. Otherwise fall back to _lastAnimPlayer.
            AnimationPlayer inspectPlayer = _animPlayer ?? _lastAnimPlayer;

            if (inspectPlayer == null)
                return;

            try
            {
                var fields = inspectPlayer.InspectFields();
                if (fields == null || fields.Count == 0)
                    return;

                // Track previous values for change highlighting
                var previousValues = new Dictionary<string, string>();
                foreach (ListViewItem item in _lstVariables.Items)
                {
                    previousValues[item.Text] = item.SubItems[1].Text;
                }

                // Update ListView WITHOUT clearing (prevents flashing)
                _lstVariables.BeginUpdate();

                var orderedFields = fields.OrderBy(x => x.Key).Where(kv => ShouldShowField(kv.Key)).ToList();

                // If field count changed, rebuild the list
                if (_lstVariables.Items.Count != orderedFields.Count)
                {
                    _lstVariables.Items.Clear();
                    foreach (var kv in orderedFields)
                    {
                        string value = FormatFieldValue(kv.Value);
                        var item = new ListViewItem(new[] { kv.Key, value, "" });
                        item.ForeColor = GetColorForType(kv.Value);
                        _lstVariables.Items.Add(item);
                    }
                }
                else
                {
                    // Update existing items in-place (no flashing)
                    for (int i = 0; i < orderedFields.Count; i++)
                    {
                        var kv = orderedFields[i];
                        string value = FormatFieldValue(kv.Value);
                        var item = _lstVariables.Items[i];

                        // Update value if changed
                        if (item.SubItems[1].Text != value)
                        {
                            item.SubItems[1].Text = value;
                        }

                        // Get type-based color, brighten if value changed from previous tick
                        Color baseColor = GetColorForType(kv.Value);
                        if (previousValues.TryGetValue(kv.Key, out string oldValue) && oldValue != value)
                        {
                            item.ForeColor = BrightenColor(baseColor); // Brighten to show change
                        }
                        else
                        {
                            item.ForeColor = baseColor; // Normal type-based color
                        }
                    }
                }

                _lstVariables.EndUpdate();

                // Update tab title with tick number
                if (_tabVariables != null)
                {
                    _tabVariables.Text = $"Variables (Tick {tick})";
                }

                // Evaluate watch expressions each tick
                EvaluateWatches(inspectPlayer);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateVariables] Error: {ex.Message}");
            }
        }

        // ── Timeline scrubber ───────────────────────────────────────────────────

        /// <summary>
        /// Called each animation frame to keep the scrubber range and position
        /// in sync with the tick history.
        /// </summary>
        private void UpdateTimelineScrubber(int currentTick)
        {
            AnimationPlayer player = _animPlayer ?? _lastAnimPlayer;
            if (player == null || _timelineScrubber == null) return;

            var history = player.TickHistory;
            if (history == null || history.Count == 0)
            {
                _timelineBar.Visible = false;
                return;
            }

            _timelineBar.Visible = true;
            _timelineScrubber.Minimum = history.MinTick;
            _timelineScrubber.Maximum = Math.Max(history.MinTick, history.MaxTick);

            // Track live unless user is scrubbing
            if (!_isScrubbing)
            {
                _timelineScrubber.Value = Math.Min(currentTick, _timelineScrubber.Maximum);
                _lblTimelineTick.Text = $"Tick {currentTick} / {history.MaxTick}";
            }
        }

        /// <summary>
        /// Handles the user dragging the timeline scrubber to a historical tick.
        /// Populates the Variables tab from the stored snapshot.
        /// </summary>
        private void OnTimelineScrub(object sender, EventArgs e)
        {
            if (_timelineScrubber == null) return;

            int scrubTick = _timelineScrubber.Value;
            _isScrubbing = true;
            _lblTimelineTick.Text = $"⏪ Tick {scrubTick}";

            AnimationPlayer player = _animPlayer ?? _lastAnimPlayer;
            if (player == null) return;

            var history = player.TickHistory;
            if (history == null || history.Count == 0) return;

            var snapshot = history.GetSnapshot(scrubTick);
            if (snapshot == null) return;

            // Populate _lstVariables from the historical snapshot
            _lstVariables.BeginUpdate();
            var orderedFields = new List<KeyValuePair<string, object>>();
            foreach (var kv in snapshot)
            {
                if (ShouldShowField(kv.Key))
                    orderedFields.Add(kv);
            }
            orderedFields.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));

            if (_lstVariables.Items.Count != orderedFields.Count)
            {
                _lstVariables.Items.Clear();
                foreach (var kv in orderedFields)
                {
                    string value = FormatFieldValue(kv.Value);
                    var item = new ListViewItem(new[] { kv.Key, value, "" });
                    item.ForeColor = GetColorForType(kv.Value);
                    _lstVariables.Items.Add(item);
                }
            }
            else
            {
                for (int i = 0; i < orderedFields.Count; i++)
                {
                    var kv = orderedFields[i];
                    string value = FormatFieldValue(kv.Value);
                    var item = _lstVariables.Items[i];
                    if (item.Text != kv.Key) item.Text = kv.Key;
                    if (item.SubItems[1].Text != value) item.SubItems[1].Text = value;
                    item.ForeColor = GetColorForType(kv.Value);
                }
            }
            _lstVariables.EndUpdate();

            if (_tabVariables != null)
                _tabVariables.Text = $"Variables (Tick {scrubTick})";
        }

        /// <summary>
        /// Resets the scrubbing flag so the scrubber tracks live ticks again.
        /// Called when animation resumes or a new frame arrives after the user
        /// stops dragging.
        /// </summary>
        private void ResetScrubbing()
        {
            _isScrubbing = false;
        }

        // ── Snapshot Comparison Bookmarks ────────────────────────────────────────

        private void OnBookmarkAClick(object sender, EventArgs e)
        {
            AnimationPlayer player = _animPlayer ?? _lastAnimPlayer;
            if (player == null) return;

            int tick = _isScrubbing ? _timelineScrubber.Value : player.CurrentTick;
            player.SetBookmarkA(tick);
            UpdateBookmarkLabel(player);
        }

        private void OnBookmarkBClick(object sender, EventArgs e)
        {
            AnimationPlayer player = _animPlayer ?? _lastAnimPlayer;
            if (player == null) return;

            int tick = _isScrubbing ? _timelineScrubber.Value : player.CurrentTick;
            player.SetBookmarkB(tick);
            UpdateBookmarkLabel(player);
        }

        private void OnCompareSnapshotsClick(object sender, EventArgs e)
        {
            AnimationPlayer player = _animPlayer ?? _lastAnimPlayer;
            if (player == null) return;

            var spriteHistory = player.SpriteHistory;
            if (spriteHistory == null) return;

            int tickA = spriteHistory.BookmarkTickA;
            int tickB = spriteHistory.BookmarkTickB;
            if (tickA < 0 || tickB < 0) return;

            var beforeSnap = spriteHistory.GetSnapshot(tickA);
            var afterSnap  = spriteHistory.GetSnapshot(tickB);

            var changes = Services.SnapshotComparer.Compare(beforeSnap, afterSnap);
            ShowSpriteComparisonDiff(changes, tickA, tickB,
                beforeSnap?.Length ?? 0, afterSnap?.Length ?? 0);

            SetStatus($"Compared tick {tickA} vs {tickB}: {changes.Count} change(s).");
        }

        private void UpdateBookmarkLabel(AnimationPlayer player)
        {
            if (_lblBookmarks == null) return;

            var history = player?.SpriteHistory;
            int a = history?.BookmarkTickA ?? -1;
            int b = history?.BookmarkTickB ?? -1;

            string aText = a >= 0 ? $"A=Tick {a}" : "A=—";
            string bText = b >= 0 ? $"B=Tick {b}" : "B=—";
            _lblBookmarks.Text = $"{aText}  |  {bText}";

            _btnCompareSnapshots.Enabled = a >= 0 && b >= 0;
        }

        /// <summary>
        /// Formats sprite comparison results into the Diff tab.
        /// Left panel shows "Before" sprite states, right panel shows "After".
        /// </summary>
        private void ShowSpriteComparisonDiff(
            List<Services.SpriteChange> changes,
            int tickA, int tickB,
            int totalBefore, int totalAfter)
        {
            if (_rtbDiffBefore == null || _rtbDiffAfter == null) return;

            _rtbDiffBefore.SuspendLayout();
            _rtbDiffAfter.SuspendLayout();
            _rtbDiffBefore.Clear();
            _rtbDiffAfter.Clear();

            var colorMoved     = Color.FromArgb(25, 35, 60);    // dark blue
            var colorRecolored = Color.FromArgb(40, 25, 50);    // dark purple
            var colorResized   = Color.FromArgb(50, 35, 15);    // dark orange
            var colorAdded     = Color.FromArgb(25, 50, 25);    // dark green
            var colorRemoved   = Color.FromArgb(60, 30, 30);    // dark red
            var colorDefault   = Color.FromArgb(18, 20, 30);

            var fgMoved     = Color.FromArgb(120, 170, 255);
            var fgRecolored = Color.FromArgb(200, 140, 255);
            var fgResized   = Color.FromArgb(255, 180, 100);
            var fgAdded     = Color.FromArgb(140, 255, 140);
            var fgRemoved   = Color.FromArgb(255, 140, 140);
            var fgDefault   = Color.FromArgb(100, 110, 140);
            var fgHeader    = Color.FromArgb(180, 200, 240);

            // Headers
            AppendDiffLine(_rtbDiffBefore, $"  ── Before: Tick {tickA} ({totalBefore} sprites) ──", colorDefault, fgHeader);
            AppendDiffLine(_rtbDiffAfter,  $"  ── After:  Tick {tickB} ({totalAfter} sprites) ──",  colorDefault, fgHeader);

            if (changes.Count == 0)
            {
                AppendDiffLine(_rtbDiffBefore, "  No visual changes detected.", colorDefault, fgDefault);
                AppendDiffLine(_rtbDiffAfter,  "  No visual changes detected.", colorDefault, fgDefault);
            }
            else
            {
                foreach (var c in changes)
                {
                    Color bgBefore, bgAfter, fgBefore, fgAfter;

                    if ((c.Kind & Services.SpriteChangeKind.Added) != 0)
                    {
                        bgBefore = colorDefault; bgAfter = colorAdded;
                        fgBefore = fgDefault;    fgAfter = fgAdded;
                        AppendDiffLine(_rtbDiffBefore, $"  [{c.Index}] (not present)", bgBefore, fgBefore);
                        AppendDiffLine(_rtbDiffAfter,  $"  [{c.Index}] + {FormatSpriteSnap(c.After.Value)}", bgAfter, fgAfter);
                    }
                    else if ((c.Kind & Services.SpriteChangeKind.Removed) != 0)
                    {
                        bgBefore = colorRemoved; bgAfter = colorDefault;
                        fgBefore = fgRemoved;    fgAfter = fgDefault;
                        AppendDiffLine(_rtbDiffBefore, $"  [{c.Index}] - {FormatSpriteSnap(c.Before.Value)}", bgBefore, fgBefore);
                        AppendDiffLine(_rtbDiffAfter,  $"  [{c.Index}] (removed)", bgAfter, fgAfter);
                    }
                    else
                    {
                        // Modified — pick color based on primary change type
                        if ((c.Kind & Services.SpriteChangeKind.Moved) != 0)
                        { bgBefore = colorMoved; bgAfter = colorMoved; fgBefore = fgMoved; fgAfter = fgMoved; }
                        else if ((c.Kind & Services.SpriteChangeKind.Recolored) != 0)
                        { bgBefore = colorRecolored; bgAfter = colorRecolored; fgBefore = fgRecolored; fgAfter = fgRecolored; }
                        else if ((c.Kind & Services.SpriteChangeKind.Resized) != 0)
                        { bgBefore = colorResized; bgAfter = colorResized; fgBefore = fgResized; fgAfter = fgResized; }
                        else
                        { bgBefore = colorDefault; bgAfter = colorDefault; fgBefore = fgDefault; fgAfter = fgDefault; }

                        AppendDiffLine(_rtbDiffBefore, $"  [{c.Index}] {FormatSpriteSnap(c.Before.Value)}", bgBefore, fgBefore);
                        AppendDiffLine(_rtbDiffAfter,  $"  [{c.Index}] {FormatSpriteSnap(c.After.Value)}  ← {c.Summary}", bgAfter, fgAfter);
                    }
                }
            }

            _rtbDiffBefore.SelectionStart = 0;
            _rtbDiffAfter.SelectionStart  = 0;
            _rtbDiffBefore.ResumeLayout();
            _rtbDiffAfter.ResumeLayout();

            _tabDiff.Text = changes.Count > 0 ? $"Diff ({changes.Count})" : "Diff";
        }

        private static string FormatSpriteSnap(Models.SpriteSnapshotEntry sp)
        {
            string name = sp.Type == Models.SpriteEntryType.Text
                ? $"TEXT \"{(sp.Text?.Length > 15 ? sp.Text.Substring(0, 12) + "..." : sp.Text)}\""
                : sp.SpriteName ?? "(unnamed)";
            return $"{name}  pos=({sp.X:F1},{sp.Y:F1})  size=({sp.Width:F1}×{sp.Height:F1})  color=({sp.ColorR},{sp.ColorG},{sp.ColorB},{sp.ColorA})";
        }

        // ── Variables ListView owner-draw (sparklines) ──────────────────────────

        private void LstVariables_DrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            e.DrawDefault = true;
        }

        private void LstVariables_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            // Columns 0 (Field) and 1 (Value): default text drawing
            if (e.ColumnIndex < 2)
            {
                // Use item forecolor for proper highlighting support
                var textColor = e.Item.ForeColor;
                using (var brush = new System.Drawing.SolidBrush(textColor))
                {
                    var sf = new System.Drawing.StringFormat
                    {
                        Alignment     = StringAlignment.Near,
                        LineAlignment = StringAlignment.Center,
                        Trimming      = StringTrimming.EllipsisCharacter,
                        FormatFlags   = StringFormatFlags.NoWrap,
                    };
                    var rect = e.Bounds;
                    rect.X += 2;
                    rect.Width -= 4;
                    e.Graphics.DrawString(e.SubItem.Text, _lstVariables.Font, brush, rect, sf);
                }
                return;
            }

            // Column 2 (Trend): draw sparkline for numeric fields
            AnimationPlayer player = _animPlayer ?? _lastAnimPlayer;
            var history = player?.TickHistory;
            if (history == null || history.Count < 2)
            {
                e.DrawDefault = false;
                return;
            }

            string fieldName = e.Item.Text;
            int[] ticks;
            float[] values;
            history.GetNumericSeries(fieldName, out ticks, out values);

            // Check if this field has any real numeric data
            bool hasData = false;
            for (int i = 0; i < values.Length; i++)
            {
                if (!float.IsNaN(values[i])) { hasData = true; break; }
            }

            if (!hasData) return;

            // Draw sparkline
            var bounds = e.Bounds;
            int pad = 2;
            int left = bounds.X + pad;
            int top = bounds.Y + pad;
            int width = bounds.Width - pad * 2;
            int height = bounds.Height - pad * 2;
            if (width < 8 || height < 4) return;

            // Find min/max for scaling
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < values.Length; i++)
            {
                if (float.IsNaN(values[i])) continue;
                if (values[i] < min) min = values[i];
                if (values[i] > max) max = values[i];
            }

            float range = max - min;
            if (range < 0.0001f) range = 1f; // flat line

            // Build points
            var points = new List<System.Drawing.Point>();
            for (int i = 0; i < values.Length; i++)
            {
                if (float.IsNaN(values[i])) continue;
                int px = left + (int)((float)i / Math.Max(1, values.Length - 1) * width);
                int py = top + height - (int)((values[i] - min) / range * height);
                points.Add(new System.Drawing.Point(px, py));
            }

            if (points.Count >= 2)
            {
                using (var pen = new System.Drawing.Pen(Color.FromArgb(100, 200, 255), 1f))
                {
                    e.Graphics.DrawLines(pen, points.ToArray());
                }

                // Draw current value dot (last point)
                var last = points[points.Count - 1];
                using (var dotBrush = new System.Drawing.SolidBrush(Color.FromArgb(255, 220, 80)))
                {
                    e.Graphics.FillEllipse(dotBrush, last.X - 2, last.Y - 2, 4, 4);
                }
            }
        }

        // ── Sprite-to-Variable linking ──────────────────────────────────────────

        /// <summary>
        /// Scans a source code region for identifiers that match known runner
        /// field names.  Returns the set of field names referenced in the region.
        /// </summary>
        private static HashSet<string> GetLinkedFieldNames(
            string sourceCode, int start, int end, ICollection<string> knownFields)
        {
            var linked = new HashSet<string>();
            if (string.IsNullOrEmpty(sourceCode) || start < 0 || end <= start)
                return linked;

            start = Math.Max(0, start);
            end = Math.Min(sourceCode.Length, end);
            string region = sourceCode.Substring(start, end - start);

            foreach (var field in knownFields)
            {
                if (string.IsNullOrEmpty(field)) continue;

                int idx = 0;
                while (idx < region.Length)
                {
                    int pos = region.IndexOf(field, idx, StringComparison.Ordinal);
                    if (pos < 0) break;

                    // Ensure it's a whole-word match (not part of a longer identifier)
                    bool leftOk = pos == 0 || !char.IsLetterOrDigit(region[pos - 1]) && region[pos - 1] != '_';
                    int afterEnd = pos + field.Length;
                    bool rightOk = afterEnd >= region.Length || !char.IsLetterOrDigit(region[afterEnd]) && region[afterEnd] != '_';

                    if (leftOk && rightOk)
                    {
                        linked.Add(field);
                        break; // found once is enough
                    }
                    idx = pos + 1;
                }
            }

            return linked;
        }

        /// <summary>
        /// Highlights rows in the Variables tab that correspond to fields
        /// referenced in the selected sprite's source code region.
        /// Resets all rows to normal when nothing is selected or no links found.
        /// </summary>
        private void HighlightLinkedVariables(SpriteEntry sprite)
        {
            if (_lstVariables == null || _lstVariables.Items.Count == 0) return;

            AnimationPlayer player = _animPlayer ?? _lastAnimPlayer;
            HashSet<string> linked = null;

            if (sprite != null && player != null)
            {
                string sourceCode = _layout?.OriginalSourceCode ?? _codeBox?.Text;
                var fields = player.InspectFields();

                if (fields != null && fields.Count > 0 && !string.IsNullOrEmpty(sourceCode))
                {
                    // Primary: use SourceStart/SourceEnd character offsets
                    if (sprite.SourceStart >= 0 && sprite.SourceEnd > sprite.SourceStart)
                    {
                        linked = GetLinkedFieldNames(sourceCode, sprite.SourceStart, sprite.SourceEnd, fields.Keys);
                    }

                    // Fallback: use SourceCodeSnippet if available
                    if ((linked == null || linked.Count == 0) && !string.IsNullOrEmpty(sprite.SourceCodeSnippet))
                    {
                        linked = GetLinkedFieldNames(sprite.SourceCodeSnippet, 0, sprite.SourceCodeSnippet.Length, fields.Keys);
                    }

                    // Fallback: if sprite has a SourceMethodName, scan that entire method body
                    if ((linked == null || linked.Count == 0) && !string.IsNullOrEmpty(sprite.SourceMethodName))
                    {
                        int mStart = sourceCode.IndexOf(sprite.SourceMethodName, StringComparison.Ordinal);
                        if (mStart >= 0)
                        {
                            // Find the method body (from method name to next closing brace block)
                            int braceStart = sourceCode.IndexOf('{', mStart);
                            if (braceStart >= 0)
                            {
                                int depth = 1;
                                int scan = braceStart + 1;
                                while (scan < sourceCode.Length && depth > 0)
                                {
                                    if (sourceCode[scan] == '{') depth++;
                                    else if (sourceCode[scan] == '}') depth--;
                                    scan++;
                                }
                                linked = GetLinkedFieldNames(sourceCode, braceStart, scan, fields.Keys);
                            }
                        }
                    }
                }
            }

            // Apply highlighting
            bool hasLinks = linked != null && linked.Count > 0;
            Color linkedColor = Color.FromArgb(255, 220, 80);     // Bright gold for linked fields
            Color dimColor = Color.FromArgb(80, 90, 100);          // Dimmed for unrelated fields

            _lstVariables.BeginUpdate();
            foreach (ListViewItem item in _lstVariables.Items)
            {
                if (hasLinks)
                {
                    item.ForeColor = linked.Contains(item.Text) ? linkedColor : dimColor;
                }
                else
                {
                    // No links — restore type-based color
                    var fields = (player ?? _lastAnimPlayer)?.InspectFields();
                    object value = null;
                    fields?.TryGetValue(item.Text, out value);
                    item.ForeColor = GetColorForType(value);
                }
            }
            _lstVariables.EndUpdate();
        }

        // ── Variable editing at runtime ─────────────────────────────────────────

        /// <summary>
        /// Edits the currently selected variable in the Variables list via a
        /// simple input dialog, then applies the new value to the live runner
        /// instance using reflection.
        /// </summary>
        private void EditSelectedVariable()
        {
            if (_lstVariables.SelectedItems.Count == 0) return;

            AnimationPlayer player = _animPlayer ?? _lastAnimPlayer;
            if (player == null)
            {
                SetStatus("No active session — run or animate code first.");
                return;
            }

            var selected = _lstVariables.SelectedItems[0];
            string fieldName = selected.Text;
            string currentValue = selected.SubItems[1].Text;

            // Look up the field type for display
            string typeName = "";
            var fieldTypes = player.InspectFieldTypes();
            if (fieldTypes != null && fieldTypes.TryGetValue(fieldName, out Type ft))
                typeName = ft.Name;

            string newValue = ShowEditVariableDialog(fieldName, currentValue, typeName);
            if (newValue == null) return; // cancelled

            string error = player.SetFieldValue(fieldName, newValue);
            if (error != null)
            {
                MessageBox.Show(error, "Edit Variable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Refresh display immediately
            UpdateVariablesDuringAnimation(_animPlayer?.CurrentTick ?? 0);
            SetStatus($"Set {fieldName} = {newValue}");
        }

        /// <summary>
        /// Shows a dark-themed input dialog for editing a variable value.
        /// Returns the new value string, or null if cancelled.
        /// </summary>
        private string ShowEditVariableDialog(string fieldName, string currentValue, string typeName)
        {
            using (var dlg = new Form())
            {
                dlg.Text = "Edit Variable";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.Size = new Size(380, 180);
                dlg.BackColor = Color.FromArgb(30, 30, 38);
                dlg.ForeColor = Color.FromArgb(220, 220, 220);

                var lblField = new Label
                {
                    Text = $"{fieldName}  ({typeName})",
                    Location = new Point(12, 12),
                    Size = new Size(350, 20),
                    ForeColor = Color.FromArgb(130, 190, 255),
                    Font = new Font("Consolas", 10f, FontStyle.Bold),
                };

                var txtValue = new TextBox
                {
                    Text = currentValue.TrimStart('"').TrimEnd('"'),
                    Location = new Point(12, 40),
                    Size = new Size(340, 24),
                    BackColor = Color.FromArgb(18, 18, 24),
                    ForeColor = Color.FromArgb(220, 255, 220),
                    Font = new Font("Consolas", 10f),
                    BorderStyle = BorderStyle.FixedSingle,
                };
                txtValue.SelectAll();

                var btnOk = new Button
                {
                    Text = "Apply",
                    DialogResult = DialogResult.OK,
                    Location = new Point(190, 100),
                    Size = new Size(75, 28),
                    BackColor = Color.FromArgb(40, 80, 50),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                };

                var btnCancel = new Button
                {
                    Text = "Cancel",
                    DialogResult = DialogResult.Cancel,
                    Location = new Point(275, 100),
                    Size = new Size(75, 28),
                    BackColor = Color.FromArgb(60, 40, 40),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                };

                dlg.AcceptButton = btnOk;
                dlg.CancelButton = btnCancel;
                dlg.Controls.AddRange(new Control[] { lblField, txtValue, btnOk, btnCancel });

                return dlg.ShowDialog(this) == DialogResult.OK ? txtValue.Text : null;
            }
        }


        /// <summary>
        /// Starts a focused animation that immediately pauses at tick 1 for step-through debugging.
        /// Used by the "Execute & Break" context menu on detected methods.
        /// </summary>
        private void StartFocusedAnimationAndBreak(string call)
        {
            if (_layout == null || string.IsNullOrWhiteSpace(call)) return;

            string code = _layout?.OriginalSourceCode ?? _codeBox.Text;
            if (string.IsNullOrWhiteSpace(code)) return;

            // Stop any existing animation
            if (_animPlayer != null && _animPlayer.IsPlaying)
            {
                _animPlayer.Stop();
            }

            // Use the same setup as StartFocusedAnimation for focus/isolation
            // but after Prepare, do StepForward (single frame) instead of Play
            DetectedMethodInfo methodInfo = _detectedMethods?.FirstOrDefault(m => m.CallExpression == call);
            bool isVirtual = methodInfo != null && methodInfo.Kind == MethodKind.SwitchCase;

            List<SnapshotRowData> execRows = _layout?.CapturedRows;
            string execCall = call;

            if (isVirtual)
            {
                var filteredRows = execRows?
                    .Where(r => string.Equals(r.Kind, methodInfo.CaseName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (filteredRows == null || filteredRows.Count == 0)
                {
                    filteredRows = new List<SnapshotRowData>
                    {
                        new SnapshotRowData
                        {
                            Kind = methodInfo.CaseName,
                            Text = methodInfo.CaseName,
                            StatText = "1,000",
                            TextColorR = 255, TextColorG = 255, TextColorB = 255, TextColorA = 255,
                        }
                    };
                }

                var result = CodeExecutor.ExecuteWithInit(code, null, filteredRows);
                if (result.Success && result.Sprites.Count > 0)
                {
                    _animFocusSprites = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var sp in result.Sprites)
                        _animFocusSprites.Add(BuildFocusSpriteKey(sp));
                    _animFocusCall = call;
                }

                if (_isolatedCallSprites != null && _isolatedCallSprites.Count > 0)
                { execCall = call; execRows = filteredRows; }
                else
                { execCall = null; execRows = _layout?.CapturedRows; }
            }
            else
            {
                var result = CodeExecutor.ExecuteWithInit(code, call, _layout?.CapturedRows);
                if (result.Success && result.Sprites.Count > 0)
                {
                    _animFocusSprites = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var sp in result.Sprites)
                        _animFocusSprites.Add(BuildFocusSpriteKey(sp));
                    _animFocusCall = call;
                }

                if (_isolatedCallSprites != null && _isolatedCallSprites.Count > 0)
                    execCall = call;
                else
                    execCall = null;
                execRows = _layout?.CapturedRows;
            }

            EnsureAnimPlayer();
            PushUndo();
            CaptureAnimPositionSnapshot();

            string error = _animPlayer.Prepare(code, execCall, execRows);
            if (error != null)
            {
                MessageBox.Show(error, "Animation Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _animPositionSnapshot = null;
                _animFocusCall = null;
                _animFocusSprites = null;
                UpdateAnimButtonStates();
                return;
            }

            // Execute single frame and stay paused — ready for step-through
            _animPlayer.StepForward();
            UpdateAnimButtonStates();
            SetStatus($"⏸ Paused at tick 1 — step with ⏭ | {call}");
        }

        private Panel BuildCodePanel()
        {
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 20, 20), Padding = new Padding(3) };

            var toolbar = new FlowLayoutPanel
            {
                Dock          = DockStyle.Top,
                Height        = 36,
                AutoSize      = true,
                BackColor     = Color.FromArgb(30, 30, 30),
                FlowDirection = FlowDirection.LeftToRight,
                Padding       = new Padding(4, 4, 4, 0),
            };

            _lblCodeTitle = new Label { Text = "Generated C# Code", Width = 150, Height = 26, ForeColor = Color.FromArgb(150, 200, 255), TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };

            _lblCodeMode = new Label
            {
                Text      = "▸ In-Game (PB)",
                Width     = 110,
                Height    = 26,
                ForeColor = Color.FromArgb(140, 210, 140),
                TextAlign = ContentAlignment.MiddleLeft,
                Font      = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            };

            var btnCopy = DarkButton("Copy to Clipboard", Color.FromArgb(0, 100, 180));
            btnCopy.Size   = new Size(140, 26);
            btnCopy.Click += (s, e) =>
            {
                if (string.IsNullOrEmpty(_codeBox.Text)) { SetStatus("Nothing to copy."); return; }
                Clipboard.SetText(_codeBox.Text);
                SetStatus("Code copied to clipboard!");
            };

            var btnRefresh = DarkButton("⟳ Generate Code", Color.FromArgb(0, 122, 60));
            btnRefresh.Size   = new Size(120, 26);
            btnRefresh.Click += (s, e) => RefreshCode(writeBack: true, force: true);  // User explicitly wants fresh code + write back to project

            var btnResetSource = DarkButton("Reset Source", Color.FromArgb(120, 60, 0));
            btnResetSource.Size = new Size(95, 26);
            btnResetSource.Click += (s, e) =>
            {
                if (_layout != null) _layout.OriginalSourceCode = null;
                RefreshCode(force: true);  // User explicitly wants to reset
                SetStatus("Round-trip source cleared — using generated template.");
            };

            _cmbCodeStyle = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width         = 110,
                Height        = 26,
                BackColor     = Color.FromArgb(45, 45, 48),
                ForeColor     = Color.FromArgb(220, 220, 220),
                FlatStyle     = FlatStyle.Flat,
            };
            _cmbCodeStyle.Items.AddRange(new object[] { "In-Game (PB)", "Mod", "Plugin / Torch", "Pulsar" });
            _cmbCodeStyle.SelectedIndex = 0;
            _cmbCodeStyle.SelectedIndexChanged += (s, e) =>
            {
                UpdateCodeModeLabel();
                RefreshCode();
            };

            _btnCaptureSnapshot = DarkButton("📷 Capture Snapshot", Color.FromArgb(140, 90, 0));
            _btnCaptureSnapshot.Size = new Size(140, 26);
            _btnCaptureSnapshot.Visible = false;
            _btnCaptureSnapshot.Click += (s, e) => ApplyLiveSnapshot();

            _btnApplyCode = DarkButton("📥 Apply Code", Color.FromArgb(160, 100, 0));
            _btnApplyCode.Size = new Size(110, 26);
            _btnApplyCode.Visible = false;
            _btnApplyCode.Click += (s, e) => ApplyCodeFromPanel();

            var btnPaste = DarkButton("📋 Paste Code", Color.FromArgb(100, 60, 140));
            btnPaste.Size = new Size(110, 26);
            btnPaste.Click += (s, e) => ShowPasteLayoutDialog();

            var btnInspect = DarkButton("🔍 Inspect Variables", Color.FromArgb(60, 120, 140));
            btnInspect.Size = new Size(130, 26);
            btnInspect.Click += (s, e) => TestVariableInspector();

            toolbar.Controls.Add(_lblCodeTitle);
            toolbar.Controls.Add(_lblCodeMode);
            toolbar.Controls.Add(btnPaste);
            toolbar.Controls.Add(btnCopy);
            toolbar.Controls.Add(btnRefresh);
            toolbar.Controls.Add(btnResetSource);
            toolbar.Controls.Add(_cmbCodeStyle);
            toolbar.Controls.Add(_btnApplyCode);
            toolbar.Controls.Add(_btnCaptureSnapshot);
            toolbar.Controls.Add(btnInspect);

            _btnPopOut = DarkButton("⬈ Pop Out", Color.FromArgb(80, 80, 100));
            _btnPopOut.Size = new Size(80, 26);
            _btnPopOut.Click += (s, e) => ToggleCodePopout();
            toolbar.Controls.Add(_btnPopOut);

            _codeBox = new RichTextBox
            {
                Dock          = DockStyle.Fill,
                BackColor     = Color.FromArgb(14, 14, 14),
                ForeColor     = Color.FromArgb(212, 212, 212),
                Font          = new Font("Consolas", 9f),
                ReadOnly      = false,
                BorderStyle   = BorderStyle.None,
                ScrollBars    = RichTextBoxScrollBars.Both,
                WordWrap      = false,
                HideSelection = false,
                AcceptsTab    = true,
            };
            _codeDiagTooltip = new ToolTip
            {
                InitialDelay = 250,
                ReshowDelay = 100,
                AutoPopDelay = 10000,
                ShowAlways = true,
            };
            // ── Syntax-highlight debounce timer ───────────────────────────────
            _syntaxTimer = new System.Windows.Forms.Timer { Interval = 900 };
            _syntaxTimer.Tick += (s, e) =>
            {
                _syntaxTimer.Stop();
                if (_highlightInProgress || _codeBox == null) return;

                // Full RichTextBox token colouring is expensive on very large scripts.
                // Keep auto-highlight for normal code size and skip oversized buffers.
                if (_codeBox.TextLength > LiveHighlightMaxChars) return;

                string current = _codeBox.Text;
                if (string.Equals(_lastHighlightedCode, current, StringComparison.Ordinal)) return;

                try
                {
                    _highlightInProgress = true;
                    SyntaxHighlighter.Highlight(_codeBox);
                    _lastHighlightedCode = _codeBox.Text;
                }
                finally
                {
                    _highlightInProgress = false;
                }
            };

            _codeBox.TextChanged += (s, e) =>
            {
                if (_suppressCodeBoxEvents) return;
                if (!_codeUndo.IsUndoRedoing)
                    _codeUndo.Push(_codeBox.Text, _codeBox.SelectionStart);
                SyntaxHighlighter.ClearDiagnosticCache(_codeBox);
                HideCodeDiagnosticTooltip();
                _codeBoxDirty = true;
                _lblCodeTitle.Text = "✏ Code (edited)";
                _lblCodeTitle.ForeColor = Color.FromArgb(255, 200, 80);
                if (_btnApplyCode != null) _btnApplyCode.Visible = true;
                _autoComplete?.OnTextChanged();
                // Restart the debounce so we only highlight after typing pauses
                _syntaxTimer.Stop();
                _syntaxTimer.Start();
            };
            _codeBox.LostFocus += (s, e) => _autoComplete?.Hide();
            _codeBox.MouseMove += (s, e) => UpdateCodeDiagnosticTooltip(e.Location);
            _codeBox.MouseLeave += (s, e) => HideCodeDiagnosticTooltip();

            // ── Code editor right-click context menu ──
            var ctxCode = new ContextMenuStrip();
            ctxCode.BackColor = Color.FromArgb(30, 30, 30);
            ctxCode.ForeColor = Color.White;
            ctxCode.Renderer  = new ToolStripProfessionalRenderer(new DarkColorTable());

            var mnuSelectAll = new ToolStripMenuItem("Select All", null, (s2, e2) => _codeBox.SelectAll());
            mnuSelectAll.ShortcutKeyDisplayString = "Ctrl+A";
            ctxCode.Items.Add(mnuSelectAll);
            ctxCode.Items.Add(new ToolStripSeparator());

            var mnuCut  = new ToolStripMenuItem("Cut",   null, (s2, e2) =>
            {
                string sel = _codeBox.SelectedText;
                if (!string.IsNullOrEmpty(sel))
                {
                    string norm = sel.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
                    Clipboard.SetText(norm);
                    _codeBox.SelectedText = "";
                }
            });
            mnuCut.ShortcutKeyDisplayString = "Ctrl+X";
            var mnuCopy = new ToolStripMenuItem("Copy",  null, (s2, e2) =>
            {
                string sel = _codeBox.SelectedText;
                if (string.IsNullOrEmpty(sel)) sel = _codeBox.Text;
                if (!string.IsNullOrEmpty(sel))
                {
                    string norm = sel.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
                    Clipboard.SetText(norm);
                }
            });
            mnuCopy.ShortcutKeyDisplayString = "Ctrl+C";
            var mnuPaste = new ToolStripMenuItem("Paste", null, (s2, e2) => _codeBox.Paste());
            mnuPaste.ShortcutKeyDisplayString = "Ctrl+V";
            ctxCode.Items.Add(mnuCut);
            ctxCode.Items.Add(mnuCopy);
            ctxCode.Items.Add(mnuPaste);

            ctxCode.Items.Add(new ToolStripSeparator());

            var mnuIndent = new ToolStripMenuItem("Set Indentation");
            mnuIndent.DropDownItems.Add(new ToolStripMenuItem("2 Spaces", null, (s2, e2) => ReindentCodeBox(2, false)));
            mnuIndent.DropDownItems.Add(new ToolStripMenuItem("4 Spaces", null, (s2, e2) => ReindentCodeBox(4, false)));
            mnuIndent.DropDownItems.Add(new ToolStripMenuItem("Tab",      null, (s2, e2) => ReindentCodeBox(0, true)));
            ctxCode.Items.Add(mnuIndent);

            ctxCode.Items.Add(new ToolStripSeparator());
            var mnuIndentSel  = new ToolStripMenuItem("Indent Selection",  null, (s2, e2) => CodeBoxIndentSelection());
            mnuIndentSel.ShortcutKeyDisplayString = "Tab";
            var mnuOutdentSel = new ToolStripMenuItem("Outdent Selection", null, (s2, e2) => CodeBoxOutdentSelection());
            mnuOutdentSel.ShortcutKeyDisplayString = "Shift+Tab";
            ctxCode.Items.Add(mnuIndentSel);
            ctxCode.Items.Add(mnuOutdentSel);

            ctxCode.Opening += (s2, e2) =>
            {
                bool hasSel = _codeBox.SelectionLength > 0;
                mnuCut.Enabled  = hasSel;
                mnuCopy.Enabled = hasSel;
                mnuPaste.Enabled = Clipboard.ContainsText();
            };
            _codeBox.ContextMenuStrip = ctxCode;

            var lineGutter = new Controls.LineNumberGutter(_codeBox);
            _findReplaceBar = new Controls.CodeFindReplaceBar(_codeBox);
            panel.Controls.Add(_codeBox);
            panel.Controls.Add(_findReplaceBar);
            panel.Controls.Add(lineGutter);
            panel.Controls.Add(toolbar);
            _autoComplete = new CodeAutoComplete(_codeBox);
            _codePanel = panel;

            // ── Execute-code bar (bottom of code panel) ───────────────────────────
            var lblExecPrefix = new Label
            {
                Text      = "▶ Call:",
                Dock      = DockStyle.Left,
                AutoSize  = false,
                Width     = 58,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding   = new Padding(6, 0, 0, 0),
                ForeColor = Color.FromArgb(130, 200, 255),
            };
            _execResultLabel = new Label
            {
                Text      = "–",
                Dock      = DockStyle.Right,
                AutoSize  = false,
                Width     = 90,
                TextAlign = ContentAlignment.MiddleRight,
                Padding   = new Padding(0, 0, 6, 0),
                ForeColor = Color.FromArgb(130, 130, 130),
            };
            _execCallBox = new TextBox
            {
                Dock        = DockStyle.Fill,
                Font        = new Font("Consolas", 9f),
                BackColor   = Color.FromArgb(18, 24, 38),
                ForeColor   = Color.FromArgb(160, 220, 255),
                BorderStyle = BorderStyle.FixedSingle,
            };
            var btnExecCode = DarkButton("▶ Execute Code", Color.FromArgb(20, 80, 160));
            btnExecCode.Dock  = DockStyle.Right;
            btnExecCode.Width = 110;
            btnExecCode.Click += OnExecCodeClick;

            var execBar = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = 30,
                BackColor = Color.FromArgb(22, 22, 32),
            };
            execBar.Controls.Add(_execCallBox);
            execBar.Controls.Add(_execResultLabel);
            execBar.Controls.Add(btnExecCode);
            execBar.Controls.Add(lblExecPrefix);
            panel.Controls.Add(execBar);

            // ── Animation playback bar (above exec bar) ──────────────────────────
            _animBar = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = 30,
                BackColor = Color.FromArgb(18, 28, 18),
            };

            _btnAnimPlay = DarkButton("▶ Play", Color.FromArgb(20, 100, 40));
            _btnAnimPlay.Dock  = DockStyle.Left;
            _btnAnimPlay.Width = 65;
            _btnAnimPlay.Click += OnAnimPlayClick;

            _btnAnimPause = DarkButton("⏸", Color.FromArgb(100, 100, 20));
            _btnAnimPause.Dock    = DockStyle.Left;
            _btnAnimPause.Width   = 36;
            _btnAnimPause.Enabled = false;
            _btnAnimPause.Click  += OnAnimPauseClick;

            _btnAnimStop = DarkButton("⏹", Color.FromArgb(120, 30, 30));
            _btnAnimStop.Dock    = DockStyle.Left;
            _btnAnimStop.Width   = 36;
            _btnAnimStop.Enabled = false;
            _btnAnimStop.Click  += OnAnimStopClick;

            _btnAnimStep = DarkButton("⏭", Color.FromArgb(60, 60, 120));
            _btnAnimStep.Dock  = DockStyle.Left;
            _btnAnimStep.Width = 36;
            _btnAnimStep.Click += OnAnimStepClick;

            _lblAnimTick = new Label
            {
                Text      = "Animation",
                Dock      = DockStyle.Fill,
                ForeColor = Color.FromArgb(130, 200, 130),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding   = new Padding(8, 0, 0, 0),
            };

            _animBar.Controls.Add(_lblAnimTick);
            _animBar.Controls.Add(_btnAnimStep);
            _animBar.Controls.Add(_btnAnimStop);
            _animBar.Controls.Add(_btnAnimPause);
            _animBar.Controls.Add(_btnAnimPlay);
            panel.Controls.Add(_animBar);

            // ── Timeline scrubber bar (below animation bar) ───────────────────────
            _timelineBar = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = 28,
                BackColor = Color.FromArgb(18, 22, 32),
                Visible   = false, // shown once history has data
            };

            _lblTimelineTick = new Label
            {
                Text      = "⏪ History",
                Dock      = DockStyle.Right,
                Width     = 120,
                ForeColor = Color.FromArgb(180, 180, 255),
                TextAlign = ContentAlignment.MiddleRight,
                Padding   = new Padding(0, 0, 6, 0),
                Font      = new Font("Consolas", 8f),
            };

            _timelineScrubber = new TrackBar
            {
                Dock      = DockStyle.Fill,
                Minimum   = 0,
                Maximum   = 1,
                Value     = 0,
                TickStyle = TickStyle.None,
                BackColor = Color.FromArgb(18, 22, 32),
                AutoSize  = false,
                Height    = 28,
            };
            _timelineScrubber.Scroll += OnTimelineScrub;

            _timelineBar.Controls.Add(_timelineScrubber);
            _timelineBar.Controls.Add(_lblTimelineTick);
            panel.Controls.Add(_timelineBar);

            // ── Snapshot comparison bar (below timeline) ──────────────────────────
            var snapBar = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = 26,
                BackColor = Color.FromArgb(22, 22, 38),
                Visible   = false,
            };
            // Link visibility to timeline bar
            _timelineBar.VisibleChanged += (s, ev) => snapBar.Visible = _timelineBar.Visible;

            _btnBookmarkA = DarkButton("📌A", Color.FromArgb(40, 70, 110));
            _btnBookmarkA.Dock  = DockStyle.Left;
            _btnBookmarkA.Width = 44;
            _btnBookmarkA.Click += OnBookmarkAClick;

            _btnBookmarkB = DarkButton("📌B", Color.FromArgb(40, 70, 110));
            _btnBookmarkB.Dock  = DockStyle.Left;
            _btnBookmarkB.Width = 44;
            _btnBookmarkB.Click += OnBookmarkBClick;

            _btnCompareSnapshots = DarkButton("🔍 Compare", Color.FromArgb(60, 50, 90));
            _btnCompareSnapshots.Dock  = DockStyle.Left;
            _btnCompareSnapshots.Width = 80;
            _btnCompareSnapshots.Enabled = false;
            _btnCompareSnapshots.Click += OnCompareSnapshotsClick;

            _lblBookmarks = new Label
            {
                Text      = "Bookmark two ticks to compare",
                Dock      = DockStyle.Fill,
                ForeColor = Color.FromArgb(120, 140, 180),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding   = new Padding(6, 0, 0, 0),
                Font      = new Font("Consolas", 7.5f),
            };

            snapBar.Controls.Add(_lblBookmarks);
            snapBar.Controls.Add(_btnCompareSnapshots);
            snapBar.Controls.Add(_btnBookmarkB);
            snapBar.Controls.Add(_btnBookmarkA);
            panel.Controls.Add(snapBar);

            // ── Splitter for resizing the tabs section ────────────────────────────────
            var splitter = new Splitter
            {
                Dock        = DockStyle.Bottom,
                Height      = 8,  // Bigger for easier grabbing
                BackColor   = Color.FromArgb(100, 110, 140),  // Bright blue-gray
                BorderStyle = BorderStyle.FixedSingle,  // Add border to make it obvious
                Cursor      = Cursors.HSplit,
                MinExtra    = 100,  // Min space for code box above
                MinSize     = 60,   // Min size for tabs below
            };
            panel.Controls.Add(splitter);  // Add after animBar so it's between anim and tabs

            // ── Bottom tabs (Detected Methods + Variables) ────────────────────────────
            var tabControl = new TabControl
            {
                Dock        = DockStyle.Bottom,
                Height      = 150,  // Increased from 90 to accommodate checkbox + variables
                BackColor   = Color.FromArgb(22, 26, 38),
                ForeColor   = Color.FromArgb(130, 180, 230),
            };

            // ── Tab 1: Detected Methods ───────────────────────────────────────────────
            var tabMethods = new TabPage("Detected Methods");
            tabMethods.BackColor = Color.FromArgb(22, 26, 38);

            _lstDetectedCalls = new ListBox
            {
                Dock        = DockStyle.Fill,
                BackColor   = Color.FromArgb(22, 26, 38),
                ForeColor   = Color.FromArgb(160, 220, 255),
                BorderStyle = BorderStyle.None,
                Font        = new Font("Consolas", 9f),
            };
            var callsTooltip = new ToolTip();
            callsTooltip.SetToolTip(_lstDetectedCalls,
                "Auto-detected rendering methods in your code\n" +
                "Single-click to populate call box\n" +
                "Double-click to execute and show sprites\n" +
                "Right-click for Execute & Isolate and Focused Animation");
            _lstDetectedCalls.SelectedIndexChanged += (s, e) =>
            {
                if (_lstDetectedCalls.SelectedItem != null)
                    _execCallBox.Text = _lstDetectedCalls.SelectedItem.ToString();
            };

            // Double-click → execute the selected call and isolate its sprites
            _lstDetectedCalls.MouseDoubleClick += (s, e) =>
            {
                if (_lstDetectedCalls.SelectedItem == null) return;
                string call = _lstDetectedCalls.SelectedItem.ToString();
                _execCallBox.Text = call;
                IsolateCallSprites(call);
            };

            // Right-click → context menu with focused-animation option
            var ctxCalls = new ContextMenuStrip();
            ctxCalls.BackColor = Color.FromArgb(30, 30, 30);
            ctxCalls.ForeColor = Color.White;
            ctxCalls.Renderer  = new ToolStripProfessionalRenderer(
                new DarkColorTable());

            // Right-click should select the item under the cursor first
            _lstDetectedCalls.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                int idx = _lstDetectedCalls.IndexFromPoint(e.Location);
                if (idx >= 0) _lstDetectedCalls.SelectedIndex = idx;
            };

            var mnuExecIsolate = new ToolStripMenuItem("▶ Execute & Isolate");
            mnuExecIsolate.Click += (s2, e2) =>
            {
                if (_lstDetectedCalls.SelectedItem == null) return;
                string call = _lstDetectedCalls.SelectedItem.ToString();
                _execCallBox.Text = call;
                IsolateCallSprites(call);
            };
            ctxCalls.Items.Add(mnuExecIsolate);

            // "Build Sprite Map" removed: instrumentation pipeline (SetCurrentMethod + RecordSpriteMethod)
            // now handles method attribution at execution time. No need to re-run methods in isolation.

            var mnuFocusAnim = new ToolStripMenuItem("▶ Start Focused Animation");
            mnuFocusAnim.Click += (s2, e2) =>
            {
                if (_lstDetectedCalls.SelectedItem == null) return;
                string call = _lstDetectedCalls.SelectedItem.ToString();
                _execCallBox.Text = call;
                StartFocusedAnimation(call);
            };
            ctxCalls.Items.Add(mnuFocusAnim);

            var mnuExecBreak = new ToolStripMenuItem("⏸ Execute & Break");
            mnuExecBreak.Click += (s2, e2) =>
            {
                if (_lstDetectedCalls.SelectedItem == null) return;
                string call = _lstDetectedCalls.SelectedItem.ToString();
                _execCallBox.Text = call;
                StartFocusedAnimationAndBreak(call);
            };
            ctxCalls.Items.Add(mnuExecBreak);

            var mnuJump = new ToolStripMenuItem("↗ Jump to Definition");
            mnuJump.Click += (s2, e2) =>
            {
                if (_lstDetectedCalls.SelectedItem == null) return;
                string call = _lstDetectedCalls.SelectedItem.ToString();
                _execCallBox.Text = call;
                JumpToMethodDefinition(call);
            };
            ctxCalls.Items.Add(mnuJump);

            _lstDetectedCalls.ContextMenuStrip = ctxCalls;

            tabMethods.Controls.Add(_lstDetectedCalls);
            tabControl.TabPages.Add(tabMethods);

            // ── Tab 2: Variables ───────────────────────────────────────────────────────
            _tabVariables = new TabPage("Variables");
            _tabVariables.BackColor = Color.FromArgb(22, 26, 38);

            _lstVariables = new ListView
            {
                Dock          = DockStyle.Fill,
                BackColor     = Color.FromArgb(22, 26, 38),
                ForeColor     = Color.FromArgb(160, 220, 255),
                BorderStyle   = BorderStyle.None,
                Font          = new Font("Consolas", 9f),
                View          = View.Details,
                FullRowSelect = true,
                GridLines     = true,
                OwnerDraw     = true,
            };
            // Enable double buffering to eliminate flicker during fast owner-draw updates (sparklines)
            typeof(ListView).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(_lstVariables, true, null);
            _lstVariables.Columns.Add("Field", 120);
            _lstVariables.Columns.Add("Value", 170);
            _lstVariables.Columns.Add("Trend", 110);
            _lstVariables.DrawColumnHeader += LstVariables_DrawColumnHeader;
            _lstVariables.DrawSubItem      += LstVariables_DrawSubItem;

            var varsTooltip = new ToolTip();
            varsTooltip.SetToolTip(_lstVariables,
                "Instance fields from last Execute Code\n" +
                "Updated automatically after execution\n" +
                "Right-click to copy field name or value");

            // ── Context menu for Variables ─────────────────────────────────────────────
            var ctxVariables = new ContextMenuStrip();
            ctxVariables.BackColor = Color.FromArgb(30, 30, 30);
            ctxVariables.ForeColor = Color.White;
            ctxVariables.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());

            var mnuCopyValue = new ToolStripMenuItem("Copy Value");
            mnuCopyValue.Click += (s2, e2) =>
            {
                if (_lstVariables.SelectedItems.Count == 0) return;
                var item = _lstVariables.SelectedItems[0];
                string value = item.SubItems[1].Text;  // Second column = value
                if (!string.IsNullOrEmpty(value))
                {
                    Clipboard.SetText(value);
                    SetStatus($"Copied: {value}");
                }
            };

            var mnuCopyFieldName = new ToolStripMenuItem("Copy Field Name");
            mnuCopyFieldName.Click += (s2, e2) =>
            {
                if (_lstVariables.SelectedItems.Count == 0) return;
                var item = _lstVariables.SelectedItems[0];
                string fieldName = item.Text;  // First column = field name
                if (!string.IsNullOrEmpty(fieldName))
                {
                    Clipboard.SetText(fieldName);
                    SetStatus($"Copied: {fieldName}");
                }
            };

            var mnuEditValue = new ToolStripMenuItem("✏️ Edit Value");
            mnuEditValue.Click += (s2, e2) =>
            {
                if (_lstVariables.SelectedItems.Count == 0) return;
                EditSelectedVariable();
            };

            ctxVariables.Items.Add(mnuEditValue);
            ctxVariables.Items.Add(new ToolStripSeparator());
            ctxVariables.Items.Add(mnuCopyValue);
            ctxVariables.Items.Add(mnuCopyFieldName);
            _lstVariables.ContextMenuStrip = ctxVariables;

            // Double-click a variable to edit its value at runtime
            _lstVariables.DoubleClick += (s2, e2) =>
            {
                if (_lstVariables.SelectedItems.Count == 0) return;
                EditSelectedVariable();
            };

            // ── Checkbox: Show internal fields ─────────────────────────────────────
            _chkShowInternalFields = new CheckBox
            {
                Text      = "Show internal fields",
                Dock      = DockStyle.Top,
                Height    = 22,
                BackColor = Color.FromArgb(22, 26, 38),
                ForeColor = Color.FromArgb(130, 180, 230),
                Checked   = false, // Default: hide internal fields
                Padding   = new Padding(6, 2, 0, 0),
            };
            var chkTooltip = new ToolTip();
            chkTooltip.SetToolTip(_chkShowInternalFields,
                "Show internal fields (_stubSurface, compiler-generated fields, etc.)\n" +
                "Uncheck to see only your variables (counter, angle, items, etc.)");
            _chkShowInternalFields.CheckedChanged += (s, e) =>
            {
                // Refresh variables list when checkbox changes
                if (_lastAnimPlayer != null || _animPlayer != null)
                {
                    UpdateVariablesDuringAnimation(_animPlayer?.CurrentTick ?? 0);
                }
            };

            _tabVariables.Controls.Add(_lstVariables);
            _tabVariables.Controls.Add(_chkShowInternalFields);
            tabControl.TabPages.Add(_tabVariables);

            // ── Tab 3: Watch Expressions ──────────────────────────────────────────────
            _tabWatch = new TabPage("Watch");
            _tabWatch.BackColor = Color.FromArgb(22, 26, 38);

            _lstWatch = new ListView
            {
                Dock          = DockStyle.Fill,
                BackColor     = Color.FromArgb(22, 26, 38),
                ForeColor     = Color.FromArgb(160, 220, 255),
                BorderStyle   = BorderStyle.None,
                Font          = new Font("Consolas", 9f),
                View          = View.Details,
                FullRowSelect = true,
                GridLines     = true,
            };
            _lstWatch.Columns.Add("Expression", 180);
            _lstWatch.Columns.Add("Value", 180);
            _lstWatch.Columns.Add("Type", 80);

            // ── Input bar: TextBox + Add + Remove buttons ─────────────────────────────
            var watchInputPanel = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 26,
                BackColor = Color.FromArgb(22, 26, 38),
                Padding   = new Padding(2, 2, 2, 2),
            };

            _txtWatchExpr = new TextBox
            {
                Dock      = DockStyle.Fill,
                BackColor = Color.FromArgb(35, 40, 55),
                ForeColor = Color.FromArgb(220, 220, 220),
                Font      = new Font("Consolas", 9f),
                BorderStyle = BorderStyle.FixedSingle,
            };
            var watchExprTooltip = new ToolTip();
            watchExprTooltip.SetToolTip(_txtWatchExpr,
                "Type a C# expression to watch (e.g., counter % 10, angle > 180)\n" +
                "Press Enter or click + to add\n" +
                "Uses your script's field names as variables");
            _txtWatchExpr.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    AddWatchExpression(_txtWatchExpr.Text);
                    _txtWatchExpr.Clear();
                }
            };

            var btnAddWatch = new Button
            {
                Text      = "+",
                Dock      = DockStyle.Right,
                Width     = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 120, 80),
                ForeColor = Color.White,
                Font      = new Font("Consolas", 10f, FontStyle.Bold),
            };
            btnAddWatch.FlatAppearance.BorderSize = 0;
            btnAddWatch.Click += (s, e) =>
            {
                AddWatchExpression(_txtWatchExpr.Text);
                _txtWatchExpr.Clear();
            };

            var btnRemoveWatch = new Button
            {
                Text      = "−",
                Dock      = DockStyle.Right,
                Width     = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(140, 50, 50),
                ForeColor = Color.White,
                Font      = new Font("Consolas", 10f, FontStyle.Bold),
            };
            btnRemoveWatch.FlatAppearance.BorderSize = 0;
            btnRemoveWatch.Click += (s, e) => RemoveSelectedWatch();

            watchInputPanel.Controls.Add(_txtWatchExpr);
            watchInputPanel.Controls.Add(btnRemoveWatch);
            watchInputPanel.Controls.Add(btnAddWatch);

            // ── Context menu for Watch ────────────────────────────────────────────────
            var ctxWatch = new ContextMenuStrip();
            ctxWatch.BackColor = Color.FromArgb(30, 30, 30);
            ctxWatch.ForeColor = Color.White;
            ctxWatch.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());

            var mnuCopyWatchValue = new ToolStripMenuItem("Copy Value");
            mnuCopyWatchValue.Click += (s2, e2) =>
            {
                if (_lstWatch.SelectedItems.Count == 0) return;
                string val = _lstWatch.SelectedItems[0].SubItems[1].Text;
                if (!string.IsNullOrEmpty(val))
                {
                    Clipboard.SetText(val);
                    SetStatus($"Copied: {val}");
                }
            };

            var mnuCopyWatchExpr = new ToolStripMenuItem("Copy Expression");
            mnuCopyWatchExpr.Click += (s2, e2) =>
            {
                if (_lstWatch.SelectedItems.Count == 0) return;
                string expr = _lstWatch.SelectedItems[0].Text;
                if (!string.IsNullOrEmpty(expr))
                {
                    Clipboard.SetText(expr);
                    SetStatus($"Copied: {expr}");
                }
            };

            var mnuRemoveWatch = new ToolStripMenuItem("Remove Watch");
            mnuRemoveWatch.Click += (s2, e2) => RemoveSelectedWatch();

            ctxWatch.Items.Add(mnuCopyWatchValue);
            ctxWatch.Items.Add(mnuCopyWatchExpr);
            ctxWatch.Items.Add(new ToolStripSeparator());
            ctxWatch.Items.Add(mnuRemoveWatch);
            _lstWatch.ContextMenuStrip = ctxWatch;

            // ── Break condition bar ───────────────────────────────────────────────────
            var breakPanel = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 26,
                BackColor = Color.FromArgb(28, 22, 22),
                Padding   = new Padding(2, 2, 2, 2),
            };

            var lblBreakPrefix = new Label
            {
                Text      = "Break:",
                Dock      = DockStyle.Left,
                Width     = 42,
                ForeColor = Color.FromArgb(255, 140, 100),
                TextAlign = ContentAlignment.MiddleLeft,
                Font      = new Font("Segoe UI", 8f, FontStyle.Bold),
            };

            _txtBreakCondition = new TextBox
            {
                Dock        = DockStyle.Fill,
                BackColor   = Color.FromArgb(40, 30, 30),
                ForeColor   = Color.FromArgb(255, 200, 180),
                Font        = new Font("Consolas", 9f),
                BorderStyle = BorderStyle.FixedSingle,
            };
            var breakTooltip = new ToolTip();
            breakTooltip.SetToolTip(_txtBreakCondition,
                "Pause animation when this C# expression becomes true\n" +
                "e.g., counter > 50, _radarAngle > 360, items.Count == 0\n" +
                "Press Enter or click Set to activate");
            _txtBreakCondition.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    SetBreakCondition(_txtBreakCondition.Text);
                }
            };

            var btnSetBreak = new Button
            {
                Text      = "Set",
                Dock      = DockStyle.Right,
                Width     = 36,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(140, 60, 30),
                ForeColor = Color.White,
                Font      = new Font("Segoe UI", 8f, FontStyle.Bold),
            };
            btnSetBreak.FlatAppearance.BorderSize = 0;
            btnSetBreak.Click += (s, e) => SetBreakCondition(_txtBreakCondition.Text);

            var btnClearBreak = new Button
            {
                Text      = "✕",
                Dock      = DockStyle.Right,
                Width     = 26,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(80, 30, 30),
                ForeColor = Color.FromArgb(200, 200, 200),
                Font      = new Font("Segoe UI", 9f),
            };
            btnClearBreak.FlatAppearance.BorderSize = 0;
            btnClearBreak.Click += (s, e) => ClearBreakCondition();

            _lblBreakStatus = new Label
            {
                Text      = "",
                Dock      = DockStyle.Right,
                Width     = 120,
                ForeColor = Color.FromArgb(120, 120, 120),
                TextAlign = ContentAlignment.MiddleRight,
                Font      = new Font("Consolas", 8f),
            };

            breakPanel.Controls.Add(_txtBreakCondition);
            breakPanel.Controls.Add(_lblBreakStatus);
            breakPanel.Controls.Add(btnClearBreak);
            breakPanel.Controls.Add(btnSetBreak);
            breakPanel.Controls.Add(lblBreakPrefix);

            _tabWatch.Controls.Add(_lstWatch);
            _tabWatch.Controls.Add(breakPanel);
            _tabWatch.Controls.Add(watchInputPanel);
            tabControl.TabPages.Add(_tabWatch);

            // ── Tab 4: Console (Echo output log) ──────────────────────────────────────
            _tabConsole = new TabPage("Console");
            _tabConsole.BackColor = Color.FromArgb(22, 26, 38);

            _rtbConsole = new RichTextBox
            {
                Dock       = DockStyle.Fill,
                BackColor  = Color.FromArgb(18, 20, 30),
                ForeColor  = Color.FromArgb(180, 220, 255),
                Font       = new Font("Consolas", 9f),
                ReadOnly   = true,
                BorderStyle = BorderStyle.None,
                WordWrap   = false,
            };

            var consoleBtnPanel = new Panel
            {
                Dock   = DockStyle.Top,
                Height = 26,
                BackColor = Color.FromArgb(30, 34, 48),
            };

            var btnClearConsole = new Button
            {
                Text      = "🗑 Clear",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(50, 55, 75),
                ForeColor = Color.FromArgb(180, 200, 230),
                Font      = new Font("Segoe UI", 8f),
                Height    = 22,
                Width     = 70,
                Location  = new Point(4, 2),
                Cursor    = Cursors.Hand,
            };
            btnClearConsole.FlatAppearance.BorderColor = Color.FromArgb(70, 80, 110);
            btnClearConsole.Click += (s, e) => { _rtbConsole.Clear(); };

            consoleBtnPanel.Controls.Add(btnClearConsole);
            _tabConsole.Controls.Add(_rtbConsole);
            _tabConsole.Controls.Add(consoleBtnPanel);
            tabControl.TabPages.Add(_tabConsole);

            // ── Tab 5: Diff (side-by-side patched code view) ──────────────────────────
            _tabDiff = new TabPage("Diff");
            _tabDiff.BackColor = Color.FromArgb(22, 26, 38);

            var diffSplit = new SplitContainer
            {
                Dock             = DockStyle.Fill,
                Orientation      = Orientation.Vertical,
                BackColor        = Color.FromArgb(30, 34, 48),
                SplitterDistance = 300,
                SplitterWidth    = 4,
            };
            diffSplit.Panel1MinSize = 140;
            diffSplit.Panel2MinSize = 140;
            diffSplit.Resize += (s, e) => EnsureDiffSplitCentered(diffSplit);

            // Left panel: Before
            var lblBefore = new Label
            {
                Text      = "Before",
                Dock      = DockStyle.Top,
                Height    = 18,
                ForeColor = Color.FromArgb(140, 160, 200),
                BackColor = Color.FromArgb(30, 34, 48),
                Font      = new Font("Segoe UI", 8f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
            };
            _rtbDiffBefore = new RichTextBox
            {
                Dock        = DockStyle.Fill,
                BackColor   = Color.FromArgb(18, 20, 30),
                ForeColor   = Color.FromArgb(170, 185, 210),
                Font        = new Font("Consolas", 9f),
                ReadOnly    = true,
                BorderStyle = BorderStyle.None,
                WordWrap    = false,
            };
            _rtbDiffBefore.VScroll += (s, e) => SyncDiffScroll(_rtbDiffBefore, _rtbDiffAfter);
            _rtbDiffBefore.HScroll += (s, e) => SyncDiffScroll(_rtbDiffBefore, _rtbDiffAfter);
            diffSplit.Panel1.Controls.Add(_rtbDiffBefore);
            diffSplit.Panel1.Controls.Add(lblBefore);

            // Right panel: After
            var lblAfter = new Label
            {
                Text      = "After",
                Dock      = DockStyle.Top,
                Height    = 18,
                ForeColor = Color.FromArgb(140, 160, 200),
                BackColor = Color.FromArgb(30, 34, 48),
                Font      = new Font("Segoe UI", 8f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
            };
            _rtbDiffAfter = new RichTextBox
            {
                Dock        = DockStyle.Fill,
                BackColor   = Color.FromArgb(18, 20, 30),
                ForeColor   = Color.FromArgb(170, 185, 210),
                Font        = new Font("Consolas", 9f),
                ReadOnly    = true,
                BorderStyle = BorderStyle.None,
                WordWrap    = false,
            };
            _rtbDiffAfter.VScroll += (s, e) => SyncDiffScroll(_rtbDiffAfter, _rtbDiffBefore);
            _rtbDiffAfter.HScroll += (s, e) => SyncDiffScroll(_rtbDiffAfter, _rtbDiffBefore);
            diffSplit.Panel2.Controls.Add(_rtbDiffAfter);
            diffSplit.Panel2.Controls.Add(lblAfter);

            var diffBtnPanel = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 26,
                BackColor = Color.FromArgb(30, 34, 48),
            };
            var btnClearDiff = new Button
            {
                Text      = "🗑 Clear",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(50, 55, 75),
                ForeColor = Color.FromArgb(180, 200, 230),
                Font      = new Font("Segoe UI", 8f),
                Height    = 22,
                Width     = 70,
                Location  = new Point(4, 2),
                Cursor    = Cursors.Hand,
            };
            btnClearDiff.FlatAppearance.BorderColor = Color.FromArgb(70, 80, 110);
            btnClearDiff.Click += (s, e) => { _rtbDiffBefore.Clear(); _rtbDiffAfter.Clear(); _tabDiff.Text = "Diff"; };
            diffBtnPanel.Controls.Add(btnClearDiff);

            _tabDiff.Controls.Add(diffSplit);
            _tabDiff.Controls.Add(diffBtnPanel);
            tabControl.TabPages.Add(_tabDiff);

            panel.Controls.Add(tabControl);

            return panel;
        }

    }
}
