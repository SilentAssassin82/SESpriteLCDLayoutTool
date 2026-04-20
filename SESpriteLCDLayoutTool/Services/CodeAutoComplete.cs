using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using SESpriteLCDLayoutTool.Controls;
using SESpriteLCDLayoutTool.Data;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Context-aware autocomplete popup for the code editor panel.
    /// Offers SE LCD API values: sprite names, font names, enum members,
    /// named colors, and dot-access members for all SE API types.
    /// </summary>
    public sealed class CodeAutoComplete : IDisposable
    {
        // ── Autocomplete context types ──────────────────────────────────────────
        private enum AcContext { None, SpriteData, FontId, DotAccess }

        // ── Fields ──────────────────────────────────────────────────────────────
        private readonly ScintillaCodeBox _editor;
        private readonly ListBox _popup;
        private List<string> _allItems = new List<string>();
        private string _filterPrefix = "";
        private int _replaceStart;    // char index in editor where the typed prefix begins
        private AcContext _activeCtx = AcContext.None;
        private bool _inserting;      // guard against re-entrancy during commit

        // ── Dot-access member dictionaries ──────────────────────────────────────
        // Maps a type/enum name to the list of members to suggest after the dot.
        private static readonly Dictionary<string, string[]> DotMembers =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // ── Enums ───────────────────────────────────────────────────────
            ["SpriteType"]             = new[] { "TEXTURE", "TEXT", "CLIP_RECT" },
            ["TextAlignment"]          = new[] { "LEFT", "CENTER", "RIGHT" },
            ["ContentType"]            = new[] { "NONE", "TEXT_AND_IMAGE", "SCRIPT" },
            ["UpdateType"]             = new[] { "None", "Once", "Update1", "Update10", "Update100", "Terminal", "Trigger" },
            ["UpdateFrequency"]        = new[] { "None", "Update1", "Update10", "Update100", "Once" },
            ["ChargeMode"]             = new[] { "Auto", "Recharge", "Discharge" },
            ["MyShipConnectorStatus"]  = new[] { "Unconnected", "Connectable", "Connected" },
            ["DoorStatus"]             = new[] { "Open", "Closed", "Opening", "Closing" },
            ["PistonStatus"]           = new[] { "Extended", "Retracted", "Extending", "Retracting", "Stopped" },
            ["MyUpdateOrder"]          = new[] { "NoUpdate", "BeforeSimulation", "Simulation", "AfterSimulation" },

            // ── Named colors ────────────────────────────────────────────────
            ["Color"] = new[]
            {
                "White", "Black", "Red", "Green", "Blue", "Yellow",
                "Cyan", "Magenta", "Gray", "Orange", "Lime",
                "DarkGray", "LightGray", "Transparent"
            },

            // ── Structs / static classes ────────────────────────────────────
            ["Vector2"] = new[] { "X", "Y", "Zero", "One", "Length()", "LengthSquared()" },
            ["Vector3D"] = new[]
            {
                "X", "Y", "Z", "Zero", "One",
                "Length()", "LengthSquared()", "Normalize()"
            },
            ["MathHelper"] = new[]
            {
                "Pi", "TwoPi", "PiOver2", "PiOver4",
                "Clamp()", "Lerp()", "ToRadians()", "ToDegrees()"
            },
            ["MySprite"] = new[]
            {
                "Type", "Data", "Position", "Size", "Color",
                "FontId", "RotationOrScale", "Alignment",
                "CreateText()", "CreateSprite()"
            },
            ["MyFixedPoint"] = new[] { "RawValue", "ToIntSafe()", "MaxValue", "MinValue" },
            ["MyItemType"] = new[]
            {
                "TypeId", "SubtypeId",
                "Parse()", "MakeOre()", "MakeIngot()", "MakeComponent()", "MakeAmmo()", "MakeTool()"
            },

            // ── MyGridProgram (base class for PB scripts) ───────────────────
            ["MyGridProgram"] = new[] { "Runtime", "Me", "GridTerminalSystem", "Storage", "Echo()", "Save()" },

            // ── MyAPIGateway ────────────────────────────────────────────────
            ["MyAPIGateway"] = new[]
            {
                "Session", "Multiplayer", "Entities",
                "TerminalActionsHelper", "Utilities"
            },
            ["MyLog"] = new[] { "Default", "WriteLineAndConsole()", "WriteLine()" },

            // ── Interfaces — Terminal / Block base ──────────────────────────
            ["IMyTerminalBlock"] = new[]
            {
                "CustomName", "CustomData", "DetailedInfo",
                "IsWorking", "IsFunctional", "EntityId", "CubeGrid",
                "GetProperty()", "GetAction()",
                "HasInventory", "InventoryCount", "GetInventory()",
                "GetPosition()"
            },
            ["IMyFunctionalBlock"] = new[]
            {
                "Enabled",
                // inherited
                "CustomName", "CustomData", "DetailedInfo",
                "IsWorking", "IsFunctional", "EntityId", "CubeGrid",
                "GetProperty()", "GetAction()",
                "HasInventory", "InventoryCount", "GetInventory()",
                "GetPosition()"
            },

            // ── Surface / Text ──────────────────────────────────────────────
            ["IMyTextSurface"] = new[]
            {
                "ContentType", "FontColor", "BackgroundColor",
                "ScriptBackgroundColor", "ScriptForegroundColor",
                "FontSize", "Font", "TextPadding", "Script",
                "SurfaceSize", "TextureSize",
                "WriteText()", "ReadText()", "DrawFrame()", "GetSprites()"
            },
            ["IMyTextPanel"] = new[]
            {
                "ContentType", "FontColor", "BackgroundColor",
                "ScriptBackgroundColor", "ScriptForegroundColor",
                "FontSize", "Font", "TextPadding", "Script",
                "SurfaceSize", "TextureSize",
                "WriteText()", "ReadText()", "DrawFrame()", "GetSprites()",
                "Enabled", "CustomName", "CustomData"
            },
            ["IMyTextSurfaceProvider"] = new[] { "GetSurface()", "SurfaceCount" },

            // ── PB block ────────────────────────────────────────────────────
            ["IMyProgrammableBlock"] = new[]
            {
                "GetSurface()", "SurfaceCount",
                "CustomName", "CustomData", "DetailedInfo",
                "IsWorking", "IsFunctional", "EntityId"
            },

            // ── Grid Terminal System ────────────────────────────────────────
            ["IMyGridTerminalSystem"] = new[]
            {
                "GetBlocksOfType()", "GetBlockWithId()", "GetBlockWithName()",
                "SearchBlocksOfName()",
                "GetBlockGroupWithName()", "GetBlockGroups()"
            },
            ["GridTerminalSystem"] = new[]
            {
                "GetBlocksOfType()", "GetBlockWithId()", "GetBlockWithName()",
                "SearchBlocksOfName()",
                "GetBlockGroupWithName()", "GetBlockGroups()"
            },

            // ── Block Group ─────────────────────────────────────────────────
            ["IMyBlockGroup"] = new[] { "Name", "GetBlocks()", "GetBlocksOfType()" },

            // ── Runtime ─────────────────────────────────────────────────────
            ["IMyRuntime"] = new[]
            {
                "UpdateFrequency", "TimeSinceLastRun",
                "LastRunTimeMs", "MaxInstructionCount"
            },
            ["Runtime"] = new[]
            {
                "UpdateFrequency", "TimeSinceLastRun",
                "LastRunTimeMs", "MaxInstructionCount"
            },

            // ── PB shorthand — Me ───────────────────────────────────────────
            ["Me"] = new[]
            {
                "GetSurface()", "SurfaceCount",
                "CustomName", "CustomData", "DetailedInfo",
                "IsWorking", "IsFunctional", "EntityId",
                "GetProperty()", "GetAction()",
                "HasInventory", "InventoryCount", "GetInventory()",
                "GetPosition()", "CubeGrid"
            },

            // ── Inventory ───────────────────────────────────────────────────
            ["IMyInventory"] = new[]
            {
                "CurrentVolume", "MaxVolume", "CurrentMass", "ItemCount",
                "GetItems()", "GetItemAt()",
                "CanItemsBeAdded()", "ContainItems()", "GetItemAmount()"
            },

            // ── Block interfaces ────────────────────────────────────────────
            ["IMyBatteryBlock"] = new[]
            {
                "CurrentStoredPower", "MaxStoredPower",
                "CurrentInput", "CurrentOutput",
                "ChargeMode", "IsCharging", "HasCapacityRemaining",
                "Enabled", "CustomName", "CustomData"
            },
            ["IMyGasTank"] = new[]
            {
                "FilledRatio", "Capacity",
                "AutoRefillBottles", "Stockpile",
                "Enabled", "CustomName", "CustomData"
            },
            ["IMyShipConnector"] = new[]
            {
                "Status", "IsConnected", "OtherConnector",
                "Connect()", "Disconnect()", "ToggleConnect()",
                "Enabled", "CustomName", "CustomData"
            },
            ["IMyThrust"] = new[]
            {
                "ThrustOverride", "ThrustOverridePercentage",
                "MaxThrust", "MaxEffectiveThrust", "CurrentThrust",
                "Enabled", "CustomName", "CustomData"
            },
            ["IMyGyro"] = new[]
            {
                "GyroOverride", "Yaw", "Pitch", "Roll", "GyroPower",
                "Enabled", "CustomName", "CustomData"
            },
            ["IMySensorBlock"] = new[]
            {
                "IsActive",
                "DetectPlayers", "DetectSmallShips", "DetectLargeShips",
                "DetectStations", "DetectSubgrids", "DetectAsteroids",
                "LeftExtend", "RightExtend", "TopExtend",
                "BottomExtend", "FrontExtend", "BackExtend",
                "Enabled", "CustomName", "CustomData"
            },
            ["IMyDoor"] = new[]
            {
                "Status", "OpenRatio",
                "OpenDoor()", "CloseDoor()", "ToggleDoor()",
                "Enabled", "CustomName", "CustomData"
            },
            ["IMyLightingBlock"] = new[]
            {
                "Color", "Radius", "Intensity",
                "BlinkIntervalSeconds", "BlinkLength", "BlinkOffset",
                "Enabled", "CustomName", "CustomData"
            },
            ["IMyMotorStator"] = new[]
            {
                "Angle", "UpperLimitDeg", "LowerLimitDeg",
                "TargetVelocityRPM", "Torque",
                "IsAttached", "TopGrid",
                "Enabled", "CustomName", "CustomData"
            },
            ["IMyPistonBase"] = new[]
            {
                "CurrentPosition", "MinLimit", "MaxLimit",
                "Velocity", "Status",
                "Extend()", "Retract()",
                "Enabled", "CustomName", "CustomData"
            },
            ["IMyShipController"] = new[]
            {
                "GetNaturalGravity()",
                "MoveIndicator", "RotationIndicator", "RollIndicator",
                "IsUnderControl", "CanControlShip",
                "ShowHorizonIndicator", "HandBrake", "DampenersOverride",
                "CustomName", "CustomData"
            },
            ["IMyPowerProducer"] = new[]
            {
                "CurrentOutput", "MaxOutput",
                "Enabled", "CustomName", "CustomData"
            },
            ["IMyWindTurbine"] = new[]
            {
                "CurrentOutput", "MaxOutput",
                "Enabled", "CustomName", "CustomData"
            },
            ["IMySolarPanel"] = new[]
            {
                "CurrentOutput", "MaxOutput",
                "Enabled", "CustomName", "CustomData"
            },

            // ── Grid ────────────────────────────────────────────────────────
            ["IMyCubeGrid"] = new[] { "CustomName", "EntityId", "DisplayName", "GetBlocks()" },

            // ── Draw frame ──────────────────────────────────────────────────
            ["MySpriteDrawFrame"] = new[] { "Add()", "AddRange()", "Dispose()" },

            // ── Session / Multiplayer / Entities ────────────────────────────
            ["Session"]    = new[] { "ElapsedPlayTime", "WeatherEffects" },
            ["IMySession"] = new[] { "ElapsedPlayTime", "WeatherEffects" },
            ["Multiplayer"]    = new[] { "IsServer" },
            ["IMyMultiplayer"] = new[] { "IsServer" },
            ["Entities"]    = new[] { "GetEntities()" },
            ["IMyEntities"] = new[] { "GetEntities()" },
            ["Utilities"]    = new[] { "ShowMissionScreen()" },
            ["IMyUtilities"] = new[] { "ShowMissionScreen()" },

            // ── StringBuilder (very common in PB scripts) ───────────────────
            ["StringBuilder"] = new[]
            {
                "Append()", "AppendLine()", "Clear()", "Length",
                "Insert()", "Remove()", "Replace()", "ToString()"
            },
            // Common collection shorthand
            ["Math"] = new[]
            {
                "Abs()", "Max()", "Min()", "Sqrt()", "Sin()", "Cos()",
                "Tan()", "Atan2()", "Ceiling()", "Floor()", "Round()",
                "PI", "E", "Pow()", "Log()", "Sign()"
            },
            ["TimeSpan"] = new[]
            {
                "TotalSeconds", "TotalMilliseconds", "TotalMinutes",
                "TotalHours", "TotalDays",
                "Seconds", "Minutes", "Hours", "Days", "Milliseconds",
                "FromSeconds()", "FromMilliseconds()", "FromMinutes()",
                "Zero"
            },
        };

        /// <summary>
        /// Maps method names to their return types so that
        /// <c>var x = obj.GetSurface(0); x.</c> can resolve to the correct members.
        /// </summary>
        private static readonly Dictionary<string, string> MethodReturnTypes =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GetSurface"]          = "IMyTextSurface",
            ["DrawFrame"]           = "MySpriteDrawFrame",
            ["GetInventory"]        = "IMyInventory",
            ["GetNaturalGravity"]   = "Vector3D",
            ["GetBlockGroupWithName"] = "IMyBlockGroup",
            ["GetPosition"]         = "Vector3D",
        };

        /// <summary>
        /// Regex matching common C# local/field declarations:
        ///   TypeName varName
        ///   Namespace.TypeName varName =
        ///   Sandbox.ModAPI.Ingame.IMyTextPanel varName;
        /// Also matches generic types like List&lt;IMyBatteryBlock&gt;.
        /// </summary>
        private static readonly Regex RxDeclaration = new Regex(
            @"(?:^|[\s;{(,])((?:[A-Za-z]\w*\.)*[A-Z]\w*(?:<[\w.]+>)?)\s+(\w+)\s*[=;,)\[]",
            RegexOptions.Compiled);

        /// <summary>
        /// Regex matching <c>var varName = expr.MethodName(...)</c>
        /// to resolve var-declared variables via method return types.
        /// </summary>
        private static readonly Regex RxVarMethodCall = new Regex(
            @"\bvar\s+(\w+)\s*=\s*\w+\.(\w+)\s*\(",
            RegexOptions.Compiled);

        /// <summary>
        /// Regex matching <c>var varName = (TypeName)expr</c> or
        /// <c>var varName = (Namespace.TypeName)expr</c> — cast on RHS of var.
        /// </summary>
        private static readonly Regex RxVarCast = new Regex(
            @"\bvar\s+(\w+)\s*=\s*\(((?:[A-Za-z]\w*\.)*[A-Z]\w*)\)",
            RegexOptions.Compiled);

        /// <summary>
        /// Regex matching <c>var varName = expr as TypeName</c> or
        /// <c>var varName = expr as Namespace.TypeName</c>.
        /// </summary>
        private static readonly Regex RxVarAs = new Regex(
            @"\bvar\s+(\w+)\s*=\s*.+\bas\s+((?:[A-Za-z]\w*\.)*[A-Z]\w*)",
            RegexOptions.Compiled);

        /// <summary>
        /// Extracts the simple type name from a potentially fully-qualified name.
        /// e.g. "Sandbox.ModAPI.Ingame.IMyTextSurface" → "IMyTextSurface"
        /// </summary>
        private static string SimpleName(string typeName)
        {
            int dot = typeName.LastIndexOf('.');
            return dot >= 0 ? typeName.Substring(dot + 1) : typeName;
        }

        /// <summary>
        /// Looks up a type name (possibly fully-qualified) in DotMembers.
        /// Tries the full name first, then falls back to the simple name.
        /// </summary>
        private static bool TryResolveDotMembers(string typeName, out string resolvedKey)
        {
            if (DotMembers.ContainsKey(typeName)) { resolvedKey = typeName; return true; }
            string simple = SimpleName(typeName);
            if (simple != typeName && DotMembers.ContainsKey(simple)) { resolvedKey = simple; return true; }
            resolvedKey = null;
            return false;
        }

        /// <summary>
        /// Attempts to resolve a variable name to a known type name
        /// by scanning the editor text for declarations.
        /// Returns null if the variable cannot be resolved.
        /// </summary>
        private static string ResolveVariableType(string variableName, string editorText)
        {
            // 1. Explicit type declaration: TypeName varName = ...
            //    Also handles Sandbox.ModAPI.Ingame.IMyTextPanel lcd
            foreach (Match m in RxDeclaration.Matches(editorText))
            {
                if (m.Groups[2].Value == variableName)
                {
                    string typeName = m.Groups[1].Value;
                    string resolved;
                    if (typeName != "var" && TryResolveDotMembers(typeName, out resolved))
                        return resolved;
                }
            }

            // 2. var varName = something.MethodName(...) — resolve via return type
            foreach (Match m in RxVarMethodCall.Matches(editorText))
            {
                if (m.Groups[1].Value == variableName)
                {
                    string methodName = m.Groups[2].Value;
                    string retType;
                    if (MethodReturnTypes.TryGetValue(methodName, out retType) && DotMembers.ContainsKey(retType))
                        return retType;
                }
            }

            // 3. var varName = (TypeName)expr — cast (may be fully-qualified)
            foreach (Match m in RxVarCast.Matches(editorText))
            {
                if (m.Groups[1].Value == variableName)
                {
                    string typeName = m.Groups[2].Value;
                    string resolved;
                    if (TryResolveDotMembers(typeName, out resolved))
                        return resolved;
                }
            }

            // 4. var varName = expr as TypeName (may be fully-qualified)
            foreach (Match m in RxVarAs.Matches(editorText))
            {
                if (m.Groups[1].Value == variableName)
                {
                    string typeName = m.Groups[2].Value;
                    string resolved;
                    if (TryResolveDotMembers(typeName, out resolved))
                        return resolved;
                }
            }

            return null;
        }

        // ── Constructor ─────────────────────────────────────────────────────────
        public CodeAutoComplete(ScintillaCodeBox editor)
        {
            _editor = editor ?? throw new ArgumentNullException(nameof(editor));

            _popup = new ListBox
            {
                Visible       = false,
                Font          = new Font("Consolas", 9f),
                BackColor     = Color.FromArgb(30, 30, 40),
                ForeColor     = Color.FromArgb(210, 220, 240),
                BorderStyle   = BorderStyle.FixedSingle,
                IntegralHeight = false,
                Width         = 320,
                Height        = 160,
            };
            _popup.Click      += (s, e) => CommitSelection();
            _popup.DoubleClick += (s, e) => CommitSelection();
            _popup.KeyDown    += OnPopupKeyDown;

            // Add popup as sibling of editor so it floats on top
            if (_editor.Parent != null)
                _editor.Parent.Controls.Add(_popup);

            _popup.BringToFront();
        }

        /// <summary>Is the autocomplete popup currently visible?</summary>
        public bool IsActive => _popup.Visible;

        /// <summary>True when the popup ListBox has focus (e.g. during a mouse click).</summary>
        public bool IsPopupFocused => _popup.Focused;

        // ── Public API called from MainForm ─────────────────────────────────────

        /// <summary>
        /// Call on every text change in the editor.  Detects context and shows/hides the popup.
        /// </summary>
        public void OnTextChanged()
        {
            if (_inserting) return;
            DetectAndShow();
        }

        /// <summary>
        /// Call from ProcessCmdKey or KeyDown.  Returns true if the key was consumed.
        /// </summary>
        public bool HandleKey(Keys keyData)
        {
            if (!_popup.Visible) return false;

            switch (keyData)
            {
                case Keys.Escape:
                    Hide();
                    return true;

                case Keys.Tab:
                case Keys.Enter:
                    CommitSelection();
                    return true;

                case Keys.Up:
                    if (_popup.SelectedIndex > 0) _popup.SelectedIndex--;
                    return true;

                case Keys.Down:
                    if (_popup.SelectedIndex < _popup.Items.Count - 1) _popup.SelectedIndex++;
                    return true;

                default:
                    return false;
            }
        }

        // ── Context detection ───────────────────────────────────────────────────

        private void DetectAndShow()
        {
            int caret = _editor.SelectionStart;
            if (caret <= 0) { Hide(); return; }

            string text = _editor.Text;
            if (caret > text.Length) { Hide(); return; }

            // Extract left-of-caret context (up to 200 chars back for safety)
            int lookBack = Math.Min(caret, 200);
            string left = text.Substring(caret - lookBack, lookBack);

            AcContext ctx = AcContext.None;
            List<string> items = null;
            string prefix = "";
            int prefixStartInEditor = caret;

            // ── 1. Inside a string literal: Data = "...|  or  FontId = "...|
            int lastQuote = left.LastIndexOf('"');
            if (lastQuote >= 0)
            {
                string afterQuote = left.Substring(lastQuote + 1);
                if (!afterQuote.Contains("\""))
                {
                    string beforeQuote = left.Substring(0, lastQuote);
                    string propCtx = beforeQuote.TrimEnd();

                    if (propCtx.EndsWith("="))
                        propCtx = propCtx.Substring(0, propCtx.Length - 1).TrimEnd();

                    if (propCtx.EndsWith("Data", StringComparison.OrdinalIgnoreCase))
                    {
                        ctx = AcContext.SpriteData;
                        prefix = afterQuote;
                        prefixStartInEditor = caret - afterQuote.Length;
                    }
                    else if (propCtx.EndsWith("FontId", StringComparison.OrdinalIgnoreCase))
                    {
                        ctx = AcContext.FontId;
                        prefix = afterQuote;
                        prefixStartInEditor = caret - afterQuote.Length;
                    }
                }
            }

            // ── 2. Dot-access patterns: TypeName.member
            if (ctx == AcContext.None)
            {
                int dotPos = -1;
                for (int i = left.Length - 1; i >= 0; i--)
                {
                    char c = left[i];
                    if (c == '.')
                    {
                        dotPos = i;
                        break;
                    }
                    if (char.IsLetterOrDigit(c) || c == '_')
                        continue;
                    break;
                }

                if (dotPos >= 0)
                {
                    string afterDot = left.Substring(dotPos + 1);
                    int wordStart = dotPos - 1;
                    while (wordStart >= 0 && (char.IsLetterOrDigit(left[wordStart]) || left[wordStart] == '_'))
                        wordStart--;
                    wordStart++;
                    string beforeDot = left.Substring(wordStart, dotPos - wordStart);

                    string[] members;
                    if (beforeDot.Length > 0 && DotMembers.TryGetValue(beforeDot, out members))
                    {
                        ctx = AcContext.DotAccess;
                        items = members.ToList();
                        prefix = afterDot;
                        prefixStartInEditor = caret - afterDot.Length;
                    }
                    else if (beforeDot.Length > 0)
                    {
                        // Fallback: resolve variable name to its declared type
                        string resolved = ResolveVariableType(beforeDot, text);
                        if (resolved != null && DotMembers.TryGetValue(resolved, out members))
                        {
                            ctx = AcContext.DotAccess;
                            items = members.ToList();
                            prefix = afterDot;
                            prefixStartInEditor = caret - afterDot.Length;
                        }
                    }
                }
            }

            if (ctx == AcContext.None) { Hide(); return; }

            // Build suggestion list for the detected context
            _allItems = items ?? GetSuggestions(ctx);
            _filterPrefix = prefix;
            _replaceStart = prefixStartInEditor;
            _activeCtx = ctx;

            ApplyFilter();
        }

        private List<string> GetSuggestions(AcContext ctx)
        {
            switch (ctx)
            {
                case AcContext.SpriteData:
                    return GetAllSpriteNames();
                case AcContext.FontId:
                    return SpriteCatalog.Fonts.ToList();
                default:
                    return new List<string>();
            }
        }

        private List<string> GetAllSpriteNames()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cat in SpriteCatalog.Categories)
                foreach (var s in cat.Sprites)
                    set.Add(s);
            foreach (var s in UserSpriteCatalog.Sprites)
                set.Add(s);
            var list = set.ToList();
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        // ── Filter & display ────────────────────────────────────────────────────

        private void ApplyFilter()
        {
            var filtered = string.IsNullOrEmpty(_filterPrefix)
                ? _allItems
                : _allItems.Where(s => s.IndexOf(_filterPrefix, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            if (filtered.Count == 0)
            {
                Hide();
                return;
            }

            _popup.BeginUpdate();
            _popup.Items.Clear();
            foreach (var item in filtered)
                _popup.Items.Add(item);
            _popup.EndUpdate();

            if (_popup.Items.Count > 0)
                _popup.SelectedIndex = 0;

            PositionPopup();
            if (!_popup.Visible)
                _popup.Visible = true;
        }

        private void PositionPopup()
        {
            Point caretPt = _editor.GetPositionFromCharIndex(_editor.SelectionStart);
            Point screenPt = _editor.PointToScreen(caretPt);
            Point parentPt = _popup.Parent.PointToClient(screenPt);

            int lineHeight = _editor.Lines[0].Height + 2;
            int x = parentPt.X;
            int y = parentPt.Y + lineHeight;

            if (x + _popup.Width > _popup.Parent.ClientSize.Width)
                x = _popup.Parent.ClientSize.Width - _popup.Width;
            if (x < 0) x = 0;

            if (y + _popup.Height > _popup.Parent.ClientSize.Height)
                y = parentPt.Y - _popup.Height;

            _popup.Location = new Point(x, y);
        }

        // ── Commit / Hide ───────────────────────────────────────────────────────

        private void CommitSelection()
        {
            if (_popup.SelectedItem == null) { Hide(); return; }

            _inserting = true;
            try
            {
                string selected = _popup.SelectedItem.ToString();
                int caret = _editor.SelectionStart;
                int prefixLen = caret - _replaceStart;
                if (prefixLen < 0) prefixLen = 0;

                _editor.Select(_replaceStart, prefixLen);
                _editor.SelectedText = selected;
            }
            finally
            {
                _inserting = false;
            }
            Hide();
            _editor.Focus();
        }

        public void Hide()
        {
            _popup.Visible = false;
            _activeCtx = AcContext.None;
        }

        // ── Popup keyboard handling ─────────────────────────────────────────────

        private void OnPopupKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Tab) { CommitSelection(); e.Handled = true; }
        }

        // ── IDisposable ─────────────────────────────────────────────────────────

        public void Dispose()
        {
            _popup?.Dispose();
        }
    }
}
