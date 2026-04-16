# SE Sprite LCD Layout Tool

A powerful **WYSIWYG visual editor** for designing custom LCD sprite layouts in **[Space Engineers](https://store.steampowered.com/app/244850/Space_Engineers/)**.

Design your screens with drag & drop, preview real in-game textures, then export clean, ready-to-paste C# code for **Programmable Blocks**, **mods**, **Torch/SE plugins**, or **Pulsar client-side plugins**.

![.NET Framework 4.8](https://img.shields.io/badge/.NET%20Framework-4.8-blue)
![C#](https://img.shields.io/badge/C%23-7.3-brightgreen)
![License](https://img.shields.io/badge/license-MIT-green)

---

## 📑 Table of Contents

| Section | Description |
|---|---|
| [✨ Features](#-features) | Visual canvas, texture previews, sprite catalog, code generation, compiler, animation, debugging & profiling, snapshots, multi-select layers |
| [🚀 Getting Started](#-getting-started) | Requirements, running the tool, importing sprite names |
| [📖 Workflow](#-workflow) | Step-by-step usage guide |
| [📸 Snapshot Helpers & Live Streaming](#-snapshot-helpers--live-lcd-streaming) | Optional — mostly superseded by the built-in compiler; still useful without Roslyn or for live streaming. Includes snapshot tagging |
| [Screenshots](#screenshots) | Editor canvas, catalog, code generation, in-game results, demo videos |
| [⌨️ Keyboard Shortcuts](#️-keyboard-shortcuts) | All hotkeys and mouse controls |
| [Contributing](#contributing) | Bug reports, feature requests, PRs |
| [License](#license) | MIT |
| [📝 Changelog](#-changelog) | Version history (v1.0.0 → v2.9.9) |

---

## ✨ Features

### 🎨 Visual Canvas Designer
- **Drag & drop** sprite placement on a pixel-accurate LCD canvas
- **Zoom & pan** with mouse wheel and middle-click drag
- **Snap to grid** with configurable grid size
- **Constrain to surface** — optional toggle to keep sprites within the LCD surface bounds during drag and nudge
- **Resize handles** for quick sprite scaling
- **Rotation** support for texture sprites
- **Layer ordering** — move sprites up/down in the draw order
- **Multi-select** — Shift-click sprites directly on the **canvas** or in the **layer list** to build a selection; batch **delete**, **duplicate**, and **hide/show** operations apply to the entire selection
- **Layer visibility** — right-click **Hide Selected** to hide one or more sprites, or **Hide Layers Above** to reveal buried sprites; **Show All Layers** restores full visibility
- **Layer locking** — right-click **Lock Layer** to prevent accidental clicks/drags on sprites; locked sprites stay visible but skip hit-testing, perfect for protecting background panels
- **Undo/Redo** with full history
- **Dark theme** UI matching the Space Engineers aesthetic

### 🖼️ Real Texture Previews
- **Auto-detects** your Space Engineers installation via Steam registry
- **Loads actual SE textures** (DDS, PNG, JPG) from your local Content directory
- **Full DDS format support** — BC1 (DXT1), BC3 (DXT5), BC7 (BPTC with DX10 headers), and uncompressed 32-bit BGRA
- **Mod texture support** — loads textures from local mods (`%APPDATA%\SpaceEngineers\Mods\`) and Steam Workshop mods
- **SBC definition parsing** — discovers sprite→texture mappings from LCDTextureDefinition, TransparentMaterial, and block/item Icon elements
- **MyObjectBuilder icons**, faction logos, and **ColorMatrix tinting** preview

### 📋 Sprite Catalog
- **Built-in sprites** — shapes, icons, HUD elements, backgrounds
- **Glyph catalog** — Unicode symbols available in SE fonts (White, Monospace, etc.) with tint support
- **User sprite import** — paste the output of `GetSprites()` from a Programmable Block
- **Auto-categorisation** and persistent catalog (`imported_sprites.txt`)

### 💻 Code Generation & Import
- **Four output modes:**
  - **In-Game (PB)** — `Sandbox.ModAPI.Ingame` (private method)
  - **Mod** — `Sandbox.ModAPI` (public method)
  - **Plugin / Torch** — `Sandbox.ModAPI` with `IMyTextSurfaceProvider` hints
  - **Pulsar** — `VRage.Plugins.IPlugin` with `Init`/`Update`/`Dispose` lifecycle and `MyEntities`/`MyCubeGrid` surface access pattern
- Smart **code parser** supporting many C# styles (object initializers, constructors, factory methods, assignments, fully-qualified enums)
- **Code round-trip** — paste your existing source code, edit visually, and get your original code back with only the changed properties patched in (colors, textures, fonts). Works with both static layouts and dynamic code (loops, switch/case, expressions)
- **Expression literal editing** — the SOURCE VALUES panel extracts all literal values (Color, Vector2, float, string) from the source context surrounding each sprite and offers type-specific inline editing:
  - **Color** swatches for `new Color(R,G,B)`, `Color.White`, etc.
  - **Vector2** coordinate displays for `new Vector2(X, Y)` with property context (Position, Size)
  - **Float** numeric fields for `RotationOrScale = 0.8f` and similar assignments
  - **String** text fields for `Data = "SquareSimple"`, `FontId = "White"`, etc.
  - All edits are offset-targeted — the patcher replaces only the exact literal in your original source, preserving all surrounding expressions, ternaries, and control flow
  - Unified offset management keeps all tracked literal positions consistent across multi-edit sessions
- One-click **Copy to clipboard**

### 🔧 Built-in Code Compiler & Executor
- **Compile and run SE LCD scripts directly inside the tool** — no need for Space Engineers, a Programmable Block, or any external build step
- **Three script types auto-detected and supported:**
  - **LCD Helper** — standalone render methods with `List<MySprite>` first parameter (e.g. `RenderPanel(sprites, 512f, 10f, 1f)`)
  - **Programmable Block (PB)** — full PB scripts extending `MyGridProgram` with `Main()` entry point. Paste your entire PB script and the tool detects `Main(string, UpdateType)`, wires up functional stubs for `Me`, `Runtime`, `GridTerminalSystem`, runs the constructor body, calls `Main()`, and captures every sprite drawn via `frame.Add()` — no modifications to your code needed
  - **Mod / Plugin** — scripts with methods that accept `IMyTextSurface`. The tool creates a functional stub surface, passes it to your render method, and captures sprites drawn through `DrawFrame().Add()`
- The result label shows **`[PB]`**, **`[Mod]`**, **`[Pulsar]`**, or **`[LCD]`** tags so you always know which script type was detected
- Uses the **Roslyn C# compiler** (`csc.exe`) from your local Visual Studio installation (auto-detected via `vswhere.exe`)
- Compiles with `/langversion:7.3` and `/optimize+`, targeting the standard .NET Framework assemblies
- **Comprehensive Space Engineers type stubs** compiled alongside your code:
  - Core types: `MySprite`, `Vector2`, `Vector3D`, `Color`, `SpriteType`, `TextAlignment`, `MySpriteDrawFrame`, `ContentType`
  - PB infrastructure: `MyGridProgram`, `IMyProgrammableBlock`, `IMyRuntime`, `IMyGridTerminalSystem`, `UpdateType`, `UpdateFrequency`
  - Surface types: `IMyTextSurface` (with `ScriptBackgroundColor`/`ScriptForegroundColor`), `IMyTextSurfaceProvider`, `IMyTerminalBlock` (with `GetProperty()`/`GetAction()`/`GetInventory()`)
  - Block interfaces: `IMyFunctionalBlock`, `IMyBatteryBlock`, `IMyGasTank`, `IMyShipConnector`, `IMyThrust`, `IMyGyro`, `IMySensorBlock`, `IMyDoor`, `IMyLightingBlock`, `IMyMotorStator`, `IMyPistonBase`, `IMyShipController` (with `GetNaturalGravity()`, `MoveIndicator`, `RotationIndicator`)
  - Block groups: `IMyBlockGroup` (with `GetBlocksOfType<T>()`, `Name`), `IMyGridTerminalSystem.GetBlockGroupWithName()`
  - Inventory types: `IMyInventory` (with `CurrentVolume`, `MaxVolume`, `GetItems()`), `MyInventoryItem`, `MyFixedPoint`, `MyItemType`
  - Terminal: `ITerminalProperty`, `ITerminalAction` with stub implementations
  - Functional concrete stubs: `StubTextSurface` (working `DrawFrame()`, `WriteText()`, configurable size), `StubProgrammableBlock` (2 surfaces, `GetSurface()`), `StubRuntime` (with `UpdateFrequency`), `StubGridTerminalSystem` (`GetBlocksOfType<T>()`, `GetBlockWithId()`, `GetBlockGroupWithName()`), `StubBlockGroup`, `StubInventory`
  - Enums: `ChargeMode`, `MyShipConnectorStatus`, `DoorStatus`, `PistonStatus`
  - Math helpers: `MathHelper` (Pi, Clamp, Lerp, ToRadians, ToDegrees), extended `Color` constructors (float RGBA, alpha override), additional named colors (Yellow, Cyan, Magenta, Gray, Orange), `Color * float` operator, `MySprite.CreateSprite()`
  - Scripts using `using VRageMath`, `using VRage.Game.GUI.TextPanel`, `using Sandbox.ModAPI.Ingame`, `using VRage`, or `using VRage.Game.ModAPI.Ingame` compile without modification
- **Sprite capture via `SpriteCollector`** — `MySpriteDrawFrame.Add()` feeds into a global collector so sprites drawn through the SE surface API (not just `List<MySprite>`) are captured and rendered on the canvas
- **Automatic entry point detection** adapts to the script type:
  - LCD Helper: scans for methods with `List<MySprite>` first parameter
  - PB: detects `Main(string, UpdateType)`, `Main(string)`, or `Main()` and suggests `Main("", UpdateType.None)`
  - Mod: detects methods with `IMyTextSurface` parameter and suggests e.g. `DrawHUD(surface)`
  - All detected entry points are listed; select any one to execute, or type a custom call expression
- **PB constructor support** — the tool extracts the body of your `Program()` constructor and runs it before `Main()`, so `Runtime.UpdateFrequency` assignments and field initialisation work correctly
- **5-second timeout guard** — execution runs on a background thread with a hard timeout to catch infinite loops
- **Compilation errors** are reported with clear, script-type-aware messages (temp file paths stripped for readability)
- **Use from the Paste Layout Code dialog** — accessible via **Edit → Paste Layout Code…**:
  1. Paste your SE rendering code into the top editor (PB script, mod code, or standalone helpers)
  2. Click **▶ Execute Code** (or double-click a detected method in the list)
  3. The tool compiles, runs, and shows the resulting sprite count with script type (e.g. "✔ 42 sprites [PB]")
  4. Click **Import** to bring the executed sprites onto the canvas — or combine with a runtime snapshot for merged positions
- **Call isolation mode** — when you have a layout with multiple rendering methods, execute a single call to isolate only its sprites on the canvas (dimming the rest), then click "Show All" to restore the full frame
- **Auto re-execution after expression edits** — when you edit a color, vector, float, or string literal in the SOURCE VALUES panel, the tool automatically re-compiles and re-executes your patched source code to refresh the canvas in real time

### 🎬 Animation Playback
- **Animate any SE LCD script** — the tool compiles your code and runs it frame-by-frame on a timer, so you can preview animated displays (oscilloscopes, radar sweeps, gauge bars, starfields, progress bars, etc.) directly in the editor
- **Auto-detection of all rendering methods** — all `void` methods with `List<MySprite>` or `IMyTextSurface` parameters are discovered and called every frame, showing the full animated scene
- **Snapshot-anchored playback** — capture a live snapshot from the game, apply it, then press Play:
  - Positions from the snapshot become the authoritative canvas coordinates
  - On the first animation frame, per-sprite position offsets are computed between the snapshot and the animation output
  - Every subsequent frame applies those offsets so animated movement is preserved while the scene sits at the correct in-game positions
  - Sprites in the snapshot with no animation counterpart remain visible as static elements
- **Play / Pause / Stop / Step** controls with script-type indicator (`PB`, `Mod`, `LCD`) and tick counter
- **Works with all four script types** — Programmable Block, Mod, Pulsar, and LCD Helper scripts
- Layout is **fully restored** when animation stops — your editable sprites, source tracking, and positions return to their pre-animation state

### 🔍 Runtime Debugging & Profiling
- **Variables panel** — after executing code or during animation playback, the **Variables** tab shows all instance fields of the compiled script class with live values updated every tick
  - Fields are discovered via reflection on the compiled runner class — no annotations or configuration needed
  - Supports all SE types: `int`, `float`, `double`, `bool`, `string`, `Vector2`, `Color`, `List<T>`, arrays, and user-defined types
  - **Sparkline mini-charts** — numeric fields display a tiny inline trend graph in the Variables list, drawn from the tick history buffer (last 500 ticks)
  - **Linked variable highlighting** — selecting a sprite in the layer list highlights variables that appear in its source context, making it easy to see which fields drive a specific sprite
  - **Double-click to edit** — double-click any variable value to modify it directly; the change takes effect on the next animation tick
- **Watch expressions** — add custom C# expressions (e.g. `counter % 10`, `angle > 180`, `speed * deltaTime`) to the **Watch** tab
  - Expressions are compiled once via Roslyn into delegates and evaluated each tick with fresh field values — zero overhead after compilation
  - Shows result value and type; errors are displayed inline if compilation fails
  - Watch expressions can reference any instance field by name
- **Conditional breakpoints** — enter a C# boolean expression as a break condition (e.g. `tick > 100`, `health <= 0`)
  - Animation pauses automatically when the condition evaluates to `true`
  - Edge-triggered — only fires on the transition from false→true, so it pauses once per event rather than locking up
  - Status indicator shows whether the breakpoint is armed, triggered, or has a compilation error
- **Console / Output tab** — captures `Echo()` output from PB scripts and displays it in a scrollable console panel
  - Output is tagged with tick numbers during animation playback
  - Compilation errors are also routed to the console with red formatting
- **Method performance heatmap** — the code editor highlights method bodies with background colors based on per-method execution time
  - Colors use absolute thresholds: green (< 0.5 ms) → yellow (0.5–1 ms) → orange (1–2 ms) → red (> 2 ms)
  - Timings are injected automatically via `Stopwatch` instrumentation inserted into each method body at compile time
  - Heatmap updates live during animation playback so you can see hot methods in real time
  - Per-method timing data is also shown in the animation tick label (e.g. `PB  Tick: 42  (1.3 ms)`)
- **Timeline scrubber** — a track bar at the bottom of the animation panel lets you scrub through the tick history buffer
  - Scrubbing updates the Variables panel to show historical field values at any past tick
  - The tick history ring buffer stores the last 500 snapshots of all script fields
  - Scrubber range updates automatically as new ticks are recorded
- **Tick history buffer** (`TickHistoryBuffer`) — a fixed-capacity ring buffer storing per-tick snapshots of all runner fields
  - `GetSnapshot(tick)` retrieves the nearest recorded state
  - `GetNumericSeries(fieldName)` extracts time-series data for sparkline rendering
  - Capacity: 500 ticks (configurable)

### 🧭 Code Navigation
- **Sprite-to-code navigation** — double-click any sprite in the **layer list** to jump to its exact source code location in the editor
  - Uses Roslyn to parse the CURRENT code every time — works even after edits, no stale tracking
  - **Multi-strategy chain** with automatic fallback:
    - **Strategy 0 (SourceStart):** Direct character offset from source tracking — most reliable, works for file-synced and parsed sprites
    - **Strategy 0a (CallerLineNumber):** Uses `[CallerLineNumber]`-annotated line numbers recorded by `SpriteCollector.PreRecord()` during execution
    - **Strategy 0b (PreRecord queue):** Matches `PreRecord` line attributions for batch-flushed `List<MySprite>` patterns
    - **Strategy 1 (SpriteNavigationIndex + SpriteAddMapper):** Roslyn-built index of all sprite creation expressions (`new MySprite`, `CreateText`, `CreateSprite`, etc.) combined with `SpriteAddMapper` occurrence matching. For in-range sprites, navigates directly to the creation line. For out-of-range loop-generated sprites, searches for the sprite's text content in the method body and navigates to the **text line** (not the `Add()` line)
    - **Strategy 2 (Content search):** Searches the current code for the sprite's `Data` string, with prefix matching for interpolated strings (e.g. `$"WIND: {windData}"` matched by `"WIND:"`)
    - **Strategy 3–5 (Roslyn parse, Variable tracking, Global search):** Progressively broader fallbacks using full syntax tree analysis
  - **Loop-aware navigation** — sprites generated inside loops (common in Mod/Pulsar scripts) are correctly navigated even when runtime sprite count exceeds source `Add()` call count. The `SpriteAddMapper` detects `for`/`foreach`/`while` loop ranges and marks each `Add()` call with `IsInLoop`
- **`SpriteNavigationIndex`** — Roslyn-based index mapping sprite names/data to source locations with `EntryKind` classification (`DirectSprite`, `CallSiteArg`, `Generic`)
- **`SpriteAddMapper` service** — parses render methods to build a map of all `.Add()` calls with line numbers, variable names, sprite names, and loop context
  - Handles both `List<MySprite>` patterns (`s.Add`) and `MySpriteDrawFrame` patterns (`frame.Add`)
  - Tracks indirect additions via variable assignment (`var lbl = new MySprite {...}; s.Add(lbl);`)
  - **Loop detection** via `FindLoopRanges()` — identifies `for`/`foreach`/`while` loop bodies and flags `Add()` calls within them
- **`SpriteSourceMapper` service** — uses Roslyn syntax trees to map sprite creation calls to exact source locations
  - Groups results by method name with creation index for ordered matching
  - Produces `SpriteSourceLocation` objects with line number, character position, span, and code snippet
- **`CodeNavigationService`** — orchestrates the multi-strategy navigation with detailed Debug output showing which strategy was used
- **Source tracking enrichment** — `SpriteEntry` now carries `SourceLineNumber`, `SourceCharacterPosition`, and `SourceCodeSnippet` for precise navigation metadata

### 📐 Template Gallery
- **File → Template Gallery** — browse and insert pre-built sprite layout templates
  - 15+ templates covering common LCD patterns: status bars, gauges, headers, grids, borders, progress indicators
  - Each template includes a description, preview, and ready-to-insert sprite definitions
  - Templates are inserted at the current canvas position and added to the layout for immediate editing

### 📝 Code Editor Enhancements
- **Line number gutter** — the code editor now displays a line number margin on the left side
  - Syncs with the editor's scroll position and highlights the current line
  - Custom-drawn with dark theme styling (dark background, dim gray numbers)
  - Handles all scroll events (mouse wheel, keyboard, vertical scroll bar)

### 📸 LCD Snapshot Capture & Live Streaming
- **Four ready-to-paste helper snippets** generated by the tool — one each for **Programmable Block**, **Mod**, **Torch/Plugin**, and **Pulsar** targets
- Capture live LCD panels (resolved sprites with final positions, sizes, colors, etc.)
- Import as a fully **editable layout** or as a visual **reference overlay** (dotted amber border + [REF] tag)
- **Live LCD Streaming (Plugin only)** — stream frames in real time from a running game to the layout tool over a named pipe
  - **Self-disarming timer** (default 60 seconds) — the code lies completely dormant with zero overhead until triggered, then auto-stops
  - **Pause/Resume** — freeze a frame in the editor for visual editing, resume to continue the live stream
  - Start from the layout tool: **Edit → Start Live Listening**, then trigger `StartLcdStream()` in your plugin
- **Snapshot Merge** — paste your original source code *and* a runtime snapshot side-by-side to get the best of both:
  - **Original code** preserves source tracking, round-trip patching, and all expressions/control flow
  - **Snapshot** provides the true runtime-resolved positions and sizes
  - Sprites are matched by **(Type + Data)** in occurrence order, with positional fallback for expression-generated data
  - Import baselines are refreshed after merging, so the round-trip code generator treats snapshot positions as the new baseline (not as user edits)
- **Snapshot tagging** — the generated helper code includes a `_snapshotTag` field you can set to a label (e.g. `"MyPulsarHUD"`). When set, the serialized output includes a `// @SnapshotTag: MyPulsarHUD` header line. The layout tool parses this tag and displays it in the status bar on import, making it easy to identify which plugin or script produced a given snapshot — especially useful when multiple plugins render to the same LCD
  - **Dormant sprite awareness** — if a snapshot captures fewer sprites than expected (e.g. 4 sprites missing), this is typically because some sprites were inactive/dormant during the capture frame, not a merge bug. The tag helps you verify which plugin produced the snapshot so you can re-capture at a better moment
- Standalone **Apply Runtime Snapshot** dialog (**Edit → Apply Runtime Snapshot…**) — apply a snapshot to an already-imported layout at any time
- Extremely useful for debugging dynamic LCDs or starting from an existing complex display

### 📁 File Operations
- Save/Load layouts in `.seld` (XML) format — **including original script source code**, so animation, code round-trip, and detected methods are fully restored on load
- **Bidirectional VS Code sync** — **File → Sync Script File (VS Code)…** watches a `.cs` file for external edits and writes code changes from the canvas back to the file in real time
- Built-in surface presets: 1×1 LCD (512×512), Wide LCD (512×256), Corner LCD (1024×512), custom sizes

---

## 🚀 Getting Started

### Requirements
- Windows + **.NET Framework 4.8**
- Space Engineers (recommended for full texture previews)

#### Built-in Compiler Dependencies (optional — only needed for ▶ Execute Code)

The built-in code compiler lets you compile and run SE LCD scripts directly inside the tool. It requires the **Roslyn C# compiler** (`csc.exe`) which ships with Visual Studio. The tool auto-discovers it — no manual configuration needed — but one of the following must be installed:

| Option | What to install | Notes |
|---|---|---|
| **Visual Studio 2019, 2022, or later** | Any edition (Community is free) | The tool uses `vswhere.exe` to find your VS installation, then locates `csc.exe` at `<VS>\MSBuild\Current\Bin\Roslyn\csc.exe`. Any workload that includes the C# compiler will work — e.g. ".NET desktop development" or just the "C# and Visual Basic Roslyn compilers" individual component. |
| **Visual Studio Build Tools** | [Download](https://visualstudio.microsoft.com/downloads/#build-tools-for-visual-studio-2022) (free, ~1-2 GB) | A lightweight install without the full IDE. Select the **"MSBuild tools"** workload or the **"C# and Visual Basic Roslyn compilers"** component. |

> **If neither is installed:** the tool works normally for all other features (canvas editing, code generation, import, snapshots, live streaming). Only the **▶ Execute Code** button in the Paste Layout Code dialog will show an error asking you to install Visual Studio.

> **How it works under the hood:** the tool runs `vswhere.exe` (located at `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe`) to find the latest VS installation path, then looks for `csc.exe` inside `MSBuild\Current\Bin\Roslyn\`. The compiler is invoked as an external process — no Roslyn NuGet packages or SDKs are bundled with the tool.

### Running the Tool
1. Build the solution or download a release
2. Run `SESpriteLCDLayoutTool.exe`
3. The tool auto-detects your Space Engineers path on first launch

### Importing All Sprite Names (Recommended)
Run this script once in a Programmable Block:

```csharp
public Program()
{
    var surface = Me.GetSurface(0);
    var list = new List<string>();
    surface.GetSprites(list);
    Me.CustomData = string.Join("\n", list);
}
```

Copy the Custom Data, then in the tool: **Edit → Import Sprite Names** and paste.

---

## 📖 Workflow

1. Browse or search sprites in the catalog on the left
2. Drag them onto the canvas (or type a custom name)
3. Arrange, resize, rotate, and reorder layers
4. Select your target output style (In-Game / Mod / Plugin)
5. Click **Copy Code** and paste into your script!

> **💡 Tip — Rebuilding sprite layers after loading a file:**
> When you load or sync a script file, the layer list may not yet reflect the true code order. To fix this: select your **main render method** (e.g. `BuildSprites`) in the method dropdown, click **Step Forward** once to step into it, then click **▶ Execute**. This runs the method with full source tracking so the layer list rebuilds in correct code order — and double-click sprite-to-code navigation will work properly.

---

## 📸 Snapshot Helpers & Live LCD Streaming

> **Note:** This section is completely optional. Since v2.0.0 the tool has a **built-in Roslyn compiler** that can compile and execute your SE LCD code directly — paste your script, press **▶ Execute Code**, and the resulting sprites appear on the canvas with their true runtime positions. **If you have Visual Studio (or the Build Tools) installed, you likely don't need snapshots at all.** Snapshots remain useful if you don't have Roslyn available, or if you want to capture frames from a *live running game* (e.g. live LCD streaming over a named pipe).

> **Important:** The snapshot helper snippet must be added **inside the plugin or mod that actually renders the LCD** you want to capture. Sprites are drawn by that plugin's own code, so a separate script or programmable block cannot read them — you can only capture what the owning plugin writes to the surface.

The tool generates ready-to-paste helper code via the **Copy Snapshot Helper** button. Select your target (In-Game / Mod / Plugin / Pulsar) and copy. All four variants share the same core `SnapshotCollect()` + `SerializeSnapshot()` methods (including optional `_snapshotTag` identification) — the differences are in output transport, access modifiers, and surface access patterns.

---

### Programmable Block (PB)

Drop this into your PB script. After your drawing code, call `SnapshotCollect(mySprites)` then copy the result from CustomData.

```csharp
// ─── LCD Snapshot Helper (Programmable Block) ───────────────────────────
// After your drawing code:
//   SnapshotCollect(mySprites);
//   Me.CustomData = SerializeSnapshot();
// Then copy the PB's Custom Data and paste into the layout tool.

List<MySprite> _snapshotSprites = new List<MySprite>();

/// <summary>
/// Set this to identify which script produced the snapshot (e.g. "MyHUD").
/// The layout tool displays this tag on import.
/// </summary>
string _snapshotTag = "";

void SnapshotCollect(List<MySprite> sprites)
{
    _snapshotSprites.Clear();
    _snapshotSprites.AddRange(sprites);
}

string SerializeSnapshot()
{
    var sb = new StringBuilder();
    sb.AppendLine("// ── LCD Snapshot ──");
    sb.AppendLine($"// Captured: {DateTime.Now:yyyy-MM-dd HH:mm}  |  Sprites: {_snapshotSprites.Count}");
    if (!string.IsNullOrEmpty(_snapshotTag))
        sb.AppendLine($"// @SnapshotTag: {_snapshotTag}");
    sb.AppendLine();
    for (int i = 0; i < _snapshotSprites.Count; i++)
    {
        var s = _snapshotSprites[i];
        sb.AppendLine("frame.Add(new MySprite");
        sb.AppendLine("{");
        sb.AppendLine($"    Type           = SpriteType.{s.Type},");
        sb.AppendLine($"    Data           = \"{s.Data}\",");
        if (s.Position.HasValue)
            sb.AppendLine($"    Position       = new Vector2({s.Position.Value.X:F1}f, {s.Position.Value.Y:F1}f),");
        if (s.Size.HasValue)
            sb.AppendLine($"    Size           = new Vector2({s.Size.Value.X:F1}f, {s.Size.Value.Y:F1}f),");
        if (s.Color.HasValue)
            sb.AppendLine($"    Color          = new Color({s.Color.Value.R}, {s.Color.Value.G}, {s.Color.Value.B}, {s.Color.Value.A}),");
        if (s.FontId != null)
            sb.AppendLine($"    FontId         = \"{s.FontId}\",");
        sb.AppendLine($"    Alignment      = TextAlignment.{s.Alignment},");
        sb.AppendLine($"    RotationOrScale = {s.RotationOrScale:F4}f,");
        sb.AppendLine("});");
        sb.AppendLine();
    }
    return sb.ToString();
}
```

**Usage in your PB `Main()`:**
```csharp
SnapshotCollect(mySprites);
Me.CustomData = SerializeSnapshot();
```

---

### Mod

Add this to your mod's session component or drawing class. Output is shown via the in-game mission screen.

```csharp
// ─── LCD Snapshot Helper (Mod) ──────────────────────────────────────────
// Required usings:
//   using System.Text;
//   using VRage.Game.GUI.TextPanel;
//   using VRageMath;

public List<MySprite> _snapshotSprites = new List<MySprite>();
public string _snapshotTag = ""; // identifies the snapshot source

public void SnapshotCollect(List<MySprite> sprites) { /* ...same as PB... */ }
public string SerializeSnapshot() { /* ...same serializer, emits @SnapshotTag header when set... */ }
```

**Usage:**
```csharp
SnapshotCollect(mySprites);
var output = SerializeSnapshot();
MyAPIGateway.Utilities.ShowMissionScreen("Snapshot", "", "", output);
```
Copy from the mission screen and paste into the layout tool.

---

### Torch / Plugin (with Live Streaming)

The plugin variant includes everything above *plus*:
- **One-shot file snapshot** via `SnapshotLcd()` — writes to a file and logs via NLog
- **Live streaming** via named pipe — `StartLcdStream()` / `StopLcdStream()` / `StreamFrame()`

```csharp
// ─── LCD Snapshot Helper (Torch / Plugin) ───────────────────────────────
// Required usings:
//   using System;
//   using System.Collections.Generic;
//   using System.IO;
//   using System.IO.Pipes;
//   using System.Text;
//   using NLog;
//   using Sandbox.ModAPI;
//   using VRage.Game.GUI.TextPanel;
//   using VRageMath;

private static readonly Logger Log = LogManager.GetCurrentClassLogger();

public List<MySprite> _snapshotSprites = new List<MySprite>();
public string _snapshotTag = ""; // identifies the snapshot source
public void SnapshotCollect(List<MySprite> sprites) { /* ...same... */ }
public string SerializeSnapshot() { /* ...same, emits @SnapshotTag header when set... */ }

// ── One-shot file snapshot ───────────────────────────────────────────────

public void SnapshotLcd(IMyTextSurface surface, string label = "LcdSnapshot")
{
    if (surface == null) { Log.Warn("SnapshotLcd: surface is null"); return; }
    if (_snapshotSprites.Count == 0)
    {
        Log.Warn("SnapshotLcd: no sprites collected");
        return;
    }
    string output = SerializeSnapshot();
    string path = Path.Combine(
        StoragePath ?? Directory.GetCurrentDirectory(),
        $"{label}_{DateTime.Now:yyyyMMdd_HHmmss}.cs");
    File.WriteAllText(path, output);
    Log.Info($"LCD snapshot saved: {path}  ({_snapshotSprites.Count} sprites)");
}

// ── Live LCD Streaming (self-disarming) ─────────────────────────────────

private NamedPipeClientStream _lcdPipe;
private DateTime _lcdStreamExpiry;
private bool _lcdStreamActive;

public void StartLcdStream(int seconds = 60)
{
    StopLcdStream();
    try
    {
        _lcdPipe = new NamedPipeClientStream(".", "SELcdSnapshot", PipeDirection.Out);
        _lcdPipe.Connect(2000);
        _lcdStreamExpiry = DateTime.UtcNow.AddSeconds(seconds);
        _lcdStreamActive = true;
        Log.Info($"LCD stream started — auto-disarms in {seconds}s");
    }
    catch (Exception ex)
    {
        Log.Warn($"LCD stream failed to connect: {ex.Message}");
        StopLcdStream();
    }
}

public void StopLcdStream()
{
    _lcdStreamActive = false;
    try { _lcdPipe?.Dispose(); } catch { }
    _lcdPipe = null;
}

public void StreamFrame()
{
    if (!_lcdStreamActive) return; // dormant — zero overhead

    if (DateTime.UtcNow > _lcdStreamExpiry)
    {
        Log.Info("LCD stream expired — auto-disarmed");
        StopLcdStream();
        return;
    }

    if (_snapshotSprites.Count == 0) return;

    try
    {
        string frame = SerializeSnapshot();
        byte[] payload = Encoding.UTF8.GetBytes(frame);
        byte[] header = BitConverter.GetBytes(payload.Length);
        _lcdPipe.Write(header, 0, 4);
        _lcdPipe.Write(payload, 0, payload.Length);
        _lcdPipe.Flush();
    }
    catch (Exception ex)
    {
        Log.Warn($"LCD stream write failed: {ex.Message}");
        StopLcdStream();
    }
}
```

**Usage in your render loop:**
```csharp
SnapshotCollect(mySprites);   // always — cheap list copy
StreamFrame();                // no-op when dormant; auto-disarms after timeout
```

**Triggering from a chat command:**
```csharp
// e.g. /lcd watch
StartLcdStream(60);   // streams for 60 seconds then auto-disarms
```

> ⚠️ **Important — call order for `SnapshotCollect()`**
>
> Space Engineers does not expose a way to read back sprites that have already been committed to a draw frame.
> Your plugin code must call `SnapshotCollect(sprites)` with the sprite list **before** (or at the same time as) passing them to `frame.Add(...)` — not after.
>
> If `SnapshotCollect` is called after the frame is flushed, the snapshot and live feed will silently produce an empty or zero-sprite result with no error.
> This is an SE API limitation, not a bug in the tool.
>
> **Correct order:**
> ```csharp
> // 1. Build your sprite list
> var sprites = BuildSprites();
>
> // 2. Collect BEFORE flushing to frame
> SnapshotCollect(sprites);
>
> // 3. Then flush to frame
> using (var frame = surface.DrawFrame())
>     foreach (var s in sprites) frame.Add(s);
> ```

> **Key point:** When `_lcdStreamActive` is `false`, `StreamFrame()` returns on its very first line — **zero overhead**.

---

### Pulsar Plugin

Pulsar (client-side) plugins implement `VRage.Plugins.IPlugin` and don't have access to `GridTerminalSystem`. The generated snippet includes the same snapshot + live streaming code as Torch, but with surface access via `MyEntities` / `MyCubeGrid` iteration.

```csharp
// ─── LCD Snapshot Helper (Pulsar Plugin) ─────────────────────────────────────────
// Add this to your IPlugin class.
// Required usings:
//   using Sandbox.Game.Entities;       // MyCubeGrid, MyEntities
//   using Sandbox.ModAPI;               // IMyTextPanel
//   using VRage.Game.Entity;            // MyEntity
//   using VRage.Game.GUI.TextPanel;     // MySprite, SpriteType
//   using VRage.Plugins;                // IPlugin
//   using VRageMath;                    // Vector2, Color

// Surface access (Pulsar — no GridTerminalSystem):
foreach (MyEntity e in MyEntities.GetEntities())
{
    var grid = e as MyCubeGrid;
    if (grid == null) continue;
    foreach (var slim in grid.CubeBlocks)
    {
        var panel = slim.FatBlock as IMyTextPanel;
        if (panel != null && panel.CustomName == "YourLCD")
            surface = panel;
    }
}

public List<MySprite> _snapshotSprites = new List<MySprite>();

/// <summary>
/// Set this to a unique label (e.g. "MyPulsarHUD") so the layout tool
/// can distinguish snapshots from different plugins on the same LCD.
/// </summary>
public string _snapshotTag = "";

public void SnapshotCollect(List<MySprite> sprites) { /* ...same as Torch... */ }
public string SerializeSnapshot() { /* ...same serializer, emits @SnapshotTag header when set... */ }

public void SnapshotLcd(string label = "LcdSnapshot") { /* ...same file output via NLog... */ }
public void StartLcdStream(int seconds = 60) { /* ...same named pipe streaming... */ }
public void StopLcdStream() { /* ... */ }
public void StreamFrame() { /* ...same — dormant when not active... */ }
public void StartLcdFileStream(string path, int seconds = 60) { /* ...same file streaming... */ }
public void StopLcdFileStream() { /* ... */ }
public void StreamFrameToFile() { /* ...same... */ }
```

**Usage in your `Update()` loop:**
```csharp
SnapshotCollect(mySprites);   // always — cheap list copy
StreamFrame();                // no-op when dormant
```

The full generated snippet (via **Copy Snapshot Helper** with the Pulsar target selected) includes all method bodies, XML doc comments, and the file/pipe streaming code — identical to the Torch variant but with Pulsar-specific usings and surface access guidance.

---

### Live Streaming — Layout Tool Side

1. **Edit → Start Live Listening** — the tool opens a named pipe server and waits
2. In your plugin, trigger `StartLcdStream(60)` via a chat command
3. Frames appear on the canvas in real time as the plugin renders them
4. **Edit → Pause Live Stream** — freezes the current frame so you can drag sprites, edit colors, etc.
5. **Edit → Resume Live Stream** — continues receiving live frames
6. After 60 seconds (or whatever duration you set) the plugin auto-disarms — zero code changes needed
7. **Edit → Stop Live Listening** when you're done

**File-based live streaming (alternative — same machine only)**

1. Pass any file path to `StartLcdFileStream()` in your plugin code
2. In the layout tool, use **Edit → Watch Snapshot File…** and browse to that file
3. The canvas updates roughly every 150 ms (debounced) as the plugin overwrites the file each game tick
4. Use **Edit → Stop Watching File** when done

> File streaming has no connect-order dependency — the layout tool can start watching before or after the plugin begins writing.

### Merging a Snapshot with Your Source Code

The snapshot helpers give you **exact runtime positions**, but your original source code has **round-trip patching**, expressions, and control flow. The merge workflow combines both:

1. **Edit → Paste Layout Code** opens a split dialog:
   - **Top pane** — paste your original plugin/PB source code (the code that *creates* the sprites)
   - **Bottom pane** — paste the snapshot output (the runtime-resolved sprites)
2. Click **Import Sprites** — the tool parses both, matches sprites by `(Type, Data)` in order, and applies the snapshot's positions/sizes to the code-imported sprites
3. **Edit visually** — drag, resize, recolor sprites on the canvas with true positions
4. **Copy Code** — the round-trip generator patches only the properties you changed back into your original source, preserving all loops, `switch`/`case`, expressions, and comments

> **Tip:** If your code uses dynamic/expression-based `Data` values (e.g. string interpolation), the keyed match may not find pairs. In that case the merger falls back to **positional (index matching)** — first sprite in code ↔ first sprite in snapshot, and so on.

#### Applying a Snapshot Later

Already imported your code but forgot the snapshot? Use **Edit → Apply Runtime Snapshot…** to merge a snapshot into the current layout at any time. The merger refreshes import baselines so the positions are treated as the new starting point, not as user edits.

<details>
<summary>📄 Example snapshot output (60 sprites from an IML Ingots panel) — click to expand</summary>

```csharp
// ── LCD Snapshot ──
// Captured: 2026-04-01 21:26:41Z  |  Sprites: 60
// @SnapshotTag: IML-Ingots

var sprites = new List<MySprite>
{
    new MySprite
    {
        Type = SpriteType.TEXT,
        Data = "[IML: INGOTS]",
        Position = new Vector2(10.0f, 10.0f),
        Color = new Color(0, 200, 255, 255),
        FontId = "White",
        RotationOrScale = 0.8500f,
    },
    new MySprite
    {
        Type = SpriteType.TEXTURE,
        Data = "SquareSimple",
        Position = new Vector2(256.0f, 39.6f),
        Size = new Vector2(492.0f, 2.0f),
        Color = new Color(55, 55, 60, 255),
        Alignment = TextAlignment.CENTER,
        RotationOrScale = 0.0000f,
    },
    new MySprite
    {
        Type = SpriteType.TEXTURE,
        Data = "MyObjectBuilder_Ingot/Silicon",
        Position = new Vector2(22.4f, 59.6f),
        Size = new Vector2(18.7f, 18.7f),
        Color = new Color(255, 255, 255, 255),
        Alignment = TextAlignment.CENTER,
        RotationOrScale = 0.0000f,
    },
    new MySprite
    {
        Type = SpriteType.TEXT,
        Data = "Silicon",
        Position = new Vector2(35.7f, 48.2f),
        Color = new Color(255, 255, 255, 255),
        FontId = "White",
        RotationOrScale = 0.6800f,
    },
    new MySprite
    {
        Type = SpriteType.TEXTURE,
        Data = "SquareSimple",
        Position = new Vector2(379.0f, 59.6f),
        Size = new Vector2(246.0f, 29.9f),
        Color = new Color(30, 30, 35, 220),
        Alignment = TextAlignment.CENTER,
        RotationOrScale = 0.0000f,
    },
    // ... 55 more sprites (icons, bar backgrounds, text labels, warning triangles, etc.)
};
```

👉 [View the full snapshot file](docs/iml-snapshot-example.cs)

</details>

---

The snippet above drops into **any** Torch plugin, Pulsar plugin, mod, or PB unchanged — just wire `SnapshotCollect()`, `SnapshotLcd()`, `StartLcdStream()`, or `StartLcdFileStream()` to whatever trigger makes sense for your project (chat command, hotkey, timer, etc.).

<details>
<summary>📋 Worked integration example — IML (InventoryManagerLight) Torch plugin</summary>

The snippets are demonstrated here using **[IML (InventoryManagerLight)](https://github.com/SilentAssassin82/InventoryManagerLight)** — an open-source Torch plugin by the same author. IML is used purely as a concrete reference because its source is publicly available and fully verified. You do not need IML or any specific plugin to use this tool.

### How the snippet maps to IML's chat commands

IML wires each of the three output methods to an in-game chat command:

| IML command | Calls | Output |
|---|---|---|
| `!iml snapshot <tag>` | `SnapshotLcd(surface, label)` | `iml-snapshot-{name}-{timestamp}.cs` — new file each time |
| `!iml watch <tag> [seconds]` | `StartLcdFileStream(path, seconds)` | `iml-live-{name}.cs` — overwritten each game tick (~16 ms) |
| `!iml watchstop <tag>` | `StopLcdFileStream()` | Stops the live feed |

`<tag>` is a CustomData tag on the LCD panel (e.g. `IML:LCD`, `IML:LCD=MISC`). IML converts it to a filename-safe string by replacing ` : \ / * ? " < > |` with `_` — so `IML:LCD=MISC` → `iml-live-IML_LCD_MISC.cs`. Your plugin can use any naming convention.

### Connecting to the layout tool

**One-shot (`SnapshotLcd`):** Run the command → open the written `.cs` file → paste into **Edit → Paste Layout Code**.

**Live feed (`StartLcdFileStream`):** Run the watch command → in the layout tool use **Edit → Watch Snapshot File…** → browse to the `iml-live-*.cs` file. Canvas updates ~every 150 ms while the plugin writes each game tick.

**Named pipe (`StartLcdStream`):** Open **Edit → Start Live Listening** in the layout tool *first* (2-second connect timeout), then trigger `StartLcdStream()` in-game.

### FindPanel — two-pass tag search

The generated snippet's `FindPanel` helper avoids a common false-positive: a single `||` search returns the first block whose name *or* data contains the tag, which can be the wrong block if an unrelated block's name happens to match first. The two-pass approach scans `CustomName` in a complete first pass, then falls back to `CustomData` — so the tag always wins over coincidental name matches.

```csharp
// Your tag can be anything — "MyLCD", "CARGO_DISPLAY", etc.
IMyTextSurfaceProvider panel = FindPanel(allBlocks, "MyTag", out string foundName);
```

</details>

---

## Screenshots

### Editor Canvas
![Editor Canvas](docs/editor-canvas.png)

### Sprite Catalog & Texture Previews
![Sprite Catalog — Tree View](docs/sprite-catalog.png)
![Sprite Catalog — Texture Previews](docs/sprite-catalog1.png)

### Code Generation
![Code Output — In-Game (PB)](docs/code-generation.png)
![Code Output — Mod](docs/code-generation1.png)
![Code Output — Plugin / Torch](docs/code-generation2.png)
![Code Output — Pulsar](docs/code-generation3.png)

### Snapshot Import
![Snapshot Import](docs/snapshot-import.png)

### In-Game Result
![In-Game](docs/in-game-result.png)

### Demo Videos
[![▶ Editor walkthrough](docs/editor-canvas.png)](https://youtu.be/Hp9KDFYG17o)
[![▶ Snapshot import demo](docs/snapshot-import.png)](https://youtu.be/cE2PVtbPqnQ)

---

## ⌨️ Keyboard Shortcuts

<details>
<summary>Click to expand</summary>

| Shortcut | Action |
|---|---|
| `Ctrl+Z` | Undo (canvas state; press **Generate Code** to sync the code panel — see note below) |
| `Ctrl+Y` | Redo (same workflow as Undo) |
| `Ctrl+S` | Save layout |
| `Ctrl+O` | Open layout |
| `Ctrl+V` | Paste layout code |
| `Ctrl+C` | Copy generated code |
| `Ctrl+D` | Duplicate selected sprite(s) |
| `Delete` | Delete selected sprite(s) |
| `+` / `-` | Move sprite up/down in layer order |
| `Shift+Click` | Extend layer list selection |
| `Ctrl+Click` | Toggle individual layer list selection |
| `G` | Toggle snap to grid |
| Mouse wheel | Zoom canvas |
| Middle-click drag | Pan canvas |

</details>

> **💡 Undo / Redo & Code Sync:** `Ctrl+Z` and `Ctrl+Y` instantly restore the canvas to its previous state (sprite properties, positions, textures, etc.).
> Because the code panel uses expression-safe round-trip patching, the code text is not updated automatically after undo — click **Generate Code** to regenerate the code panel from the restored canvas.
> This two-step workflow protects hand-edited expressions and dynamic code from being silently overwritten.

---

## Contributing

Bug reports, feature requests, and pull requests are welcome!

## License

MIT License

---

## 📝 Changelog

### v2.9.9
- **Sprite lock flag** — new per-sprite lock state prevents accidental selection and modification:
   - **Locked sprites** display a 🔒 icon in the layer list and cannot be clicked/selected on the canvas
   - **Lock/Unlock context menu** — right-click selected sprite(s) → **Lock Layer** / **Unlock Layer** (or use canvas context menu for multi-select)
   - Locked sprites remain **visible** but are skipped during canvas hit-testing, allowing you to safely work with small sprites in front of large background panels
   - Lock state is preserved through **undo/redo** via `UndoManager.SpriteSnapshot`
   - Lock state persists in `.seld` project files via `SpriteEntry.IsLocked` property
   - Perfect for protecting background layouts or reference guides while editing foreground elements

### v2.9.8
- **Canvas rulers + snap-to-sprite edges** — visual alignment aid and precision snapping:
   - **Horizontal & vertical rulers** — 20px thick rulers on top and left edges showing LCD surface coordinates with adaptive tick intervals
   - **Cursor crosshairs** — hairlines follow mouse position in real-time, helping you read exact coordinates for precise placement
   - **Snap-to-sprite edges** — when dragging sprites near each other, automatic magnetic snapping aligns edges and prevents overlap
   - **Show Rulers** toggle (View menu, default ON) to hide rulers and reclaim canvas space
   - **Snap to Sprite Edges** toggle (View menu, default ON) for optional snap-to-grid-only workflow
   - **Keyboard shortcut** Ctrl+Shift+G to toggle both
   - Works seamlessly with multi-select group drag operations

### v2.9.7