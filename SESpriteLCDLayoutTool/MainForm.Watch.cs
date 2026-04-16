using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SESpriteLCDLayoutTool.Models;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool
{
    public partial class MainForm
    {
        // ── Watch expression management ───────────────────────────────────────────

        /// <summary>
        /// Adds a new watch expression. Attempts immediate compilation if an
        /// animation player is available for field type discovery.
        /// </summary>
        private void AddWatchExpression(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression)) return;

            if (_watchExpressions.Any(w => w.Expression == expression.Trim()))
            {
                SetStatus($"Watch already exists: {expression.Trim()}");
                return;
            }

            var watch = new WatchExpression { Expression = expression.Trim() };
            _watchExpressions.Add(watch);

            AnimationPlayer player = _animPlayer ?? _lastAnimPlayer;
            if (player != null)
                TryCompileWatch(watch, player);

            RefreshWatchList();
        }

        /// <summary>
        /// Removes the currently selected watch expression from the list.
        /// </summary>
        private void RemoveSelectedWatch()
        {
            if (_lstWatch == null || _lstWatch.SelectedIndices.Count == 0) return;

            int idx = _lstWatch.SelectedIndices[0];
            if (idx >= 0 && idx < _watchExpressions.Count)
            {
                _watchExpressions.RemoveAt(idx);
                RefreshWatchList();
            }
        }

        /// <summary>
        /// Compiles a single watch expression using field types from the player.
        /// </summary>
        private void TryCompileWatch(WatchExpression watch, AnimationPlayer player)
        {
            try
            {
                var fieldTypes = player.InspectFieldTypes();
                if (fieldTypes == null || fieldTypes.Count == 0) return;

                var orderedNames = fieldTypes.Keys.OrderBy(k => k).ToArray();
                var orderedTypes = orderedNames.Select(n => fieldTypes[n]).ToArray();

                WatchExpressionEvaluator.Compile(watch, orderedNames, orderedTypes);
            }
            catch (Exception ex)
            {
                watch.Error = ex.Message;
            }
        }

        /// <summary>
        /// Evaluates all watch expressions using current field values and updates the Watch ListView.
        /// Called each animation tick and after one-shot execution.
        /// </summary>
        private void EvaluateWatches(AnimationPlayer player)
        {
            if (player == null || _lstWatch == null || _watchExpressions.Count == 0)
                return;

            try
            {
                var fields = player.InspectFields();
                var fieldTypes = player.InspectFieldTypes();
                if (fields == null || fieldTypes == null) return;

                var orderedNames = fieldTypes.Keys.OrderBy(k => k).ToArray();
                var orderedTypes = orderedNames.Select(n => fieldTypes[n]).ToArray();
                var orderedValues = orderedNames.Select(n => fields.ContainsKey(n) ? fields[n] : null).ToArray();

                var previousValues = new Dictionary<string, string>();
                foreach (ListViewItem item in _lstWatch.Items)
                    previousValues[item.Text] = item.SubItems[1].Text;

                _lstWatch.BeginUpdate();

                foreach (var watch in _watchExpressions)
                {
                    if (WatchExpressionEvaluator.NeedsRecompile(watch, orderedNames))
                        WatchExpressionEvaluator.Compile(watch, orderedNames, orderedTypes);

                    if (watch.IsCompiled)
                        WatchExpressionEvaluator.Evaluate(watch, orderedValues);
                }

                RefreshWatchListInPlace(previousValues);
                _lstWatch.EndUpdate();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EvaluateWatches] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Fully rebuilds the Watch ListView from _watchExpressions. Used after add/remove.
        /// </summary>
        private void RefreshWatchList()
        {
            if (_lstWatch == null) return;

            _lstWatch.BeginUpdate();
            _lstWatch.Items.Clear();

            foreach (var watch in _watchExpressions)
            {
                string value = watch.Error != null ? $"⚠ {watch.Error}" : (watch.LastValue ?? "(not evaluated)");
                string type = watch.Error != null ? "error" : (watch.LastTypeName ?? "—");

                var item = new ListViewItem(new[] { watch.Expression, value, type });
                item.ForeColor = watch.Error != null
                    ? Color.FromArgb(255, 120, 100)
                    : watch.LastValue != null
                        ? GetColorForType(ConvertDisplayType(watch.LastTypeName))
                        : Color.FromArgb(120, 120, 120);
                _lstWatch.Items.Add(item);
            }

            _lstWatch.EndUpdate();
        }

        /// <summary>
        /// Updates watch ListView items in-place with change highlighting (no flicker).
        /// </summary>
        private void RefreshWatchListInPlace(Dictionary<string, string> previousValues)
        {
            if (_lstWatch.Items.Count != _watchExpressions.Count)
            {
                _lstWatch.Items.Clear();
                foreach (var watch in _watchExpressions)
                {
                    string value = watch.Error != null ? $"⚠ {watch.Error}" : (watch.LastValue ?? "(not evaluated)");
                    string type = watch.Error != null ? "error" : (watch.LastTypeName ?? "—");
                    var item = new ListViewItem(new[] { watch.Expression, value, type });
                    item.ForeColor = watch.Error != null
                        ? Color.FromArgb(255, 120, 100)
                        : Color.FromArgb(160, 220, 255);
                    _lstWatch.Items.Add(item);
                }
                return;
            }

            for (int i = 0; i < _watchExpressions.Count; i++)
            {
                var watch = _watchExpressions[i];
                var item = _lstWatch.Items[i];

                string value = watch.Error != null ? $"⚠ {watch.Error}" : (watch.LastValue ?? "(not evaluated)");
                string type = watch.Error != null ? "error" : (watch.LastTypeName ?? "—");

                if (item.SubItems[1].Text != value)
                    item.SubItems[1].Text = value;
                if (item.SubItems[2].Text != type)
                    item.SubItems[2].Text = type;

                if (watch.Error != null)
                {
                    item.ForeColor = Color.FromArgb(255, 120, 100);
                }
                else
                {
                    Color baseColor = watch.LastValue != null
                        ? GetColorForType(ConvertDisplayType(watch.LastTypeName))
                        : Color.FromArgb(160, 220, 255);

                    if (previousValues.TryGetValue(watch.Expression, out string oldVal) && oldVal != value)
                        item.ForeColor = BrightenColor(baseColor);
                    else
                        item.ForeColor = baseColor;
                }
            }
        }

        /// <summary>
        /// Converts a type name string back to a dummy value for color coding.
        /// </summary>
        private static object ConvertDisplayType(string typeName)
        {
            if (typeName == null) return null;
            switch (typeName)
            {
                case "Int32": case "Int64": case "Int16": case "Byte":
                case "UInt32": case "UInt64": case "UInt16": case "SByte":
                    return 0;
                case "Single": case "Double": case "Decimal":
                    return 0f;
                case "String":
                    return "";
                case "Boolean":
                    return false;
                default:
                    return new object();
            }
        }

        // ── Conditional breakpoint management ─────────────────────────────────────

        /// <summary>
        /// Sets a break condition expression. Compiles it immediately if a player is available.
        /// Animation will pause when this expression evaluates to true.
        /// </summary>
        private void SetBreakCondition(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                ClearBreakCondition();
                return;
            }

            _breakCondition = new WatchExpression { Expression = expression.Trim() };
            _breakHitThisTick = false;

            AnimationPlayer player = _animPlayer ?? _lastAnimPlayer;
            if (player != null)
                TryCompileWatch(_breakCondition, player);

            if (_breakCondition.Error != null)
            {
                _lblBreakStatus.Text = "⚠ " + _breakCondition.Error;
                _lblBreakStatus.ForeColor = Color.FromArgb(255, 120, 100);
                _lblBreakStatus.Width = 200;
            }
            else if (_breakCondition.IsCompiled)
            {
                _lblBreakStatus.Text = "🔴 Armed";
                _lblBreakStatus.ForeColor = Color.FromArgb(255, 100, 80);
                _lblBreakStatus.Width = 120;
            }
            else
            {
                _lblBreakStatus.Text = "⏳ Pending";
                _lblBreakStatus.ForeColor = Color.FromArgb(200, 200, 100);
                _lblBreakStatus.Width = 120;
            }

            SetStatus($"Break condition set: {expression.Trim()}");
        }

        /// <summary>
        /// Clears the break condition.
        /// </summary>
        private void ClearBreakCondition()
        {
            _breakCondition = null;
            _breakHitThisTick = false;
            if (_txtBreakCondition != null) _txtBreakCondition.Clear();
            if (_lblBreakStatus != null)
            {
                _lblBreakStatus.Text = "";
                _lblBreakStatus.ForeColor = Color.FromArgb(120, 120, 120);
            }
            SetStatus("Break condition cleared.");
        }

        /// <summary>
        /// Checks the break condition against current field values.
        /// Returns true if the condition evaluated to true and animation should pause.
        /// Uses edge detection: only triggers on false→true transitions.
        /// </summary>
        private bool CheckBreakCondition(AnimationPlayer player)
        {
            if (_breakCondition == null || player == null)
                return false;

            try
            {
                var fields = player.InspectFields();
                var fieldTypes = player.InspectFieldTypes();
                if (fields == null || fieldTypes == null) return false;

                var orderedNames = fieldTypes.Keys.OrderBy(k => k).ToArray();
                var orderedTypes = orderedNames.Select(n => fieldTypes[n]).ToArray();
                var orderedValues = orderedNames.Select(n => fields.ContainsKey(n) ? fields[n] : null).ToArray();

                if (WatchExpressionEvaluator.NeedsRecompile(_breakCondition, orderedNames))
                {
                    WatchExpressionEvaluator.Compile(_breakCondition, orderedNames, orderedTypes);
                    if (_breakCondition.Error != null)
                    {
                        _lblBreakStatus.Text = "⚠ " + _breakCondition.Error;
                        _lblBreakStatus.ForeColor = Color.FromArgb(255, 120, 100);
                        return false;
                    }
                }

                if (!_breakCondition.IsCompiled) return false;

                WatchExpressionEvaluator.Evaluate(_breakCondition, orderedValues);

                if (_breakCondition.Error != null)
                {
                    _lblBreakStatus.Text = "⚠ " + _breakCondition.Error;
                    _lblBreakStatus.ForeColor = Color.FromArgb(255, 120, 100);
                    return false;
                }

                bool isTruthy = IsTruthy(_breakCondition.LastValue);

                if (isTruthy && !_breakHitThisTick)
                {
                    _breakHitThisTick = true;
                    _lblBreakStatus.Text = "⏸ HIT!";
                    _lblBreakStatus.ForeColor = Color.FromArgb(255, 255, 80);
                    return true;
                }
                else if (!isTruthy)
                {
                    _breakHitThisTick = false;
                    _lblBreakStatus.Text = "🔴 Armed";
                    _lblBreakStatus.ForeColor = Color.FromArgb(255, 100, 80);
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CheckBreakCondition] Error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Determines if a watch expression result string is "truthy" (non-zero, non-null, non-false).
        /// </summary>
        private static bool IsTruthy(string formattedValue)
        {
            if (string.IsNullOrEmpty(formattedValue)) return false;
            if (formattedValue == "(null)") return false;
            if (formattedValue == "false") return false;
            if (formattedValue == "0") return false;
            if (formattedValue == "0f" || formattedValue == "0.0" || formattedValue == "0E0") return false;
            return true;
        }
    }
}
