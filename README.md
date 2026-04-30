# SE Sprite LCD Layout Tool

A powerful **WYSIWYG visual editor** for designing custom LCD sprite layouts in **[Space Engineers](https://store.steampowered.com/app/244850/Space_Engineers/)**.

Design your screens with drag & drop, preview real in-game textures, animate layouts with visual keyframe editing, debug performance, then export clean C# code for **Programmable Blocks**, **mods**, **Torch/SE plugins**, or **Pulsar client-side plugins**.

![.NET Framework 4.8](https://img.shields.io/badge/.NET%20Framework-4.8-blue)
![C#](https://img.shields.io/badge/C%23-7.3-brightgreen)
![License](https://img.shields.io/badge/license-MIT-green)

---

## 📑 Table of Contents

- [✨ Features](#-features)
- [🚀 Getting Started](#-getting-started)
- [📖 Workflow](#-workflow)
- [⌨️ Keyboard Shortcuts](#️-keyboard-shortcuts)
- [Screenshots](#screenshots)
- [Contributing](#contributing)
- [License](#license)

---

## ✨ Features

### 🎨 Visual Canvas Designer
- **Drag & drop** sprite placement with pixel-perfect accuracy
- **Multi-select** — Shift-click on canvas or layer list, box-select with rubber band, batch operations (delete, duplicate, hide/show, align, distribute)
- **Align & Distribute tools** — align edges, centers, or space sprites evenly with sub-pixel precision
- **Snap to grid** with configurable intervals + **snap to sprite edges** for magnetic alignment
- **Visual rulers** with coordinate readout and cursor crosshairs
- **Zoom & pan** (mouse wheel + middle-click drag)
- **Resize handles** and **rotation** support for texture sprites
- **Layer ordering**, visibility controls, and **sprite locking** (protect backgrounds while editing foreground)
- **Constrain to surface** toggle prevents sprites from leaving visible bounds
- **Undo/Redo** with full history
- **Dark theme** UI matching Space Engineers aesthetic

### 🖼️ Real Texture Previews
- **Auto-detects** Space Engineers via Steam registry
- **Loads actual SE textures** (DDS BC1/BC3/BC7, PNG, JPG) from Content + mods
- **Full ColorMatrix tinting** preview for MyObjectBuilder icons and faction logos
- **Automated tint detection** — analyzes atlas pixel data to determine if sprites are tintable vs. baked-color
- **Mod texture support** from local mods and Steam Workshop
- **SBC definition parsing** for sprite→texture mappings

### 📋 Sprite Catalog
- **15+ built-in categories** — shapes, icons, HUD elements, backgrounds, glyphs
- **SE Font Atlas Rendering** — controller icons and Unicode glyphs rendered using actual game DDS font atlases with correct tinting
- **User sprite import** — paste `GetSprites()` output from a PB
- **Glyph catalog** with 123+ Unicode characters across 10 categories
- **Catalog replace** — right-click any sprite to swap texture/glyph while preserving position, size, color

### 💻 Built-in Roslyn Compiler & Live Execution
- **Compile and run SE LCD scripts directly** — no need for Space Engineers, PB, or external build steps
- **Four script types auto-detected:**
  - **Programmable Block (PB)** — full `MyGridProgram` with `Main()`, functional stubs for `Me`, `Runtime`, `GridTerminalSystem`
  - **Mod / Session Component** — `MySessionComponentBase` with `UpdateAfterSimulation()`, full `MyAPIGateway` stubs
  - **Plugin / Torch** — methods accepting `IMyTextSurface`
  - **Pulsar** — `VRage.Plugins.IPlugin` client-side plugins
- **Comprehensive SE API stubs** — 60+ block interfaces, inventory, terminal properties, actions, math helpers, collections
- **Sprite capture pipeline** — `SpriteCollector` intercepts `DrawFrame().Add()` calls for accurate canvas rendering
- **Automatic entry-point detection** with custom call support
- **Constructor support** for PB field initialization
- **5-second timeout** prevents infinite loops

### 🎬 Animation System
- **Visual Keyframe Editor** — interactive timeline with draggable keyframe diamonds, per-keyframe easing (Linear, SineInOut, Bounce, Elastic, etc.)
- **8 animation effect types:** Rotate, Oscillate, Pulse, Fade, Blink, ColorCycle, Keyframed, GroupFollower
- **Stackable effects** — apply rotation + color cycle + pulse simultaneously to one sprite
- **Animation Groups** — link sprites into leader/follower groups with automatic delta offsets
- **Roslyn-based injection** — effects injected structurally into code AST with idempotent marker comments
- **Play / Pause / Stop / Step** controls with tick counter
- **Snapshot-anchored playback** — apply runtime snapshot positions, then animate with offsets preserved
- **Orchestrator detection** — auto-calls state-update methods (`Advance()`, `Tick()`, etc.) before rendering each frame
- **Timeline scrubber** — scrub through 500-tick history buffer to inspect past animation states

### 🎥 Animated GIF Export
- **File → Export Animated GIF…** — capture any animated layout to a looping `.gif` for demos and bug reports
- **Two capture modes:**
  - **Fresh run** — compile and run from tick 0
  - **Continue from current tick** — record live/paused session with state preserved (essential for long warm-ups)
- **Warm-up frames** — skip intro frames before recording starts
- **Configurable** duration, FPS, output size, loop count
- **Hide reference boxes** checkbox for clean output
- **Built-in GIF89a encoder** with NETSCAPE 2.0 loop extension

### 🔍 Runtime Debugging & Profiling
- **Variables panel** — live field inspector with reflection-based discovery, updated every tick
  - **Sparkline mini-charts** for numeric fields showing last 500 ticks
  - **Linked variable highlighting** — selecting a sprite highlights variables in its source context
  - **Double-click to edit** — modify values during animation
- **Watch expressions** — custom C# boolean/numeric expressions compiled via Roslyn, zero overhead after compilation
- **Conditional breakpoints** — pause animation when expression evaluates true (edge-triggered)
- **Console / Output tab** — captures `Echo()` output with tick tagging
- **Method performance heatmap** — code editor background colored by execution time (green < 0.5ms → red > 2ms)
  - Auto-injected `Stopwatch` instrumentation
  - Live updates during playback
- **Snapshot comparison** — bookmark two ticks (A/B) and diff all sprite property changes

### 🧭 Smart Code Navigation
- **Double-click sprite → jump to source** with 6-strategy fallback chain:
  - Direct character offset (file-synced sprites)
  - `[CallerLineNumber]` attribution (runtime-captured line numbers)
  - Roslyn `SpriteNavigationIndex` with occurrence matching
  - Loop-aware navigation for runtime sprite overflow (navigates to text content line, not `.Add()` line)
  - Content/prefix search with interpolated string support
- **Auto-expand folded regions** when navigating
- **Loop detection** via `SpriteAddMapper` — flags `.Add()` calls inside loops for smarter matching

### 🔄 Code Round-Trip & Expression Editing
- **Paste source code → edit visually → get original code back** with only changed properties patched
- **Offset-targeted literal patching** — surgically replaces Color, Vector2, float, string literals at known character positions while preserving expressions, ternaries, control flow
- **SOURCE VALUES panel** extracts all literals from source context:
  - **Color swatches** for `new Color(R,G,B)`, `Color.White`, etc.
  - **Vector2 fields** with property context (Position, Size)
  - **Float/string editors** for rotation, scale, texture names, font IDs
- **Auto re-execution** after expression edits — re-compiles and refreshes canvas in real time
- **RoslynCodeMerger** for AST-aware method body replacement
- **Per-sprite dynamic patching** for loop/switch-driven sprites

### 📐 Template Gallery
- **File → Template Gallery** — 15+ pre-built layouts: status bars, gauges, headers, grids, borders, progress indicators
- **One-click insert** at current canvas position

### 📝 Professional Code Editor (Scintilla)
- **Syntax highlighting** with C# lexer (keywords, strings, comments, operators)
- **Line numbers**, **code folding**, **brace matching**
- **Word occurrence highlight** (double-click identifier)
- **Auto-indent**, **auto-close brackets**
- **Wavy red underlines** on syntax errors with SE/Torch/Pulsar-aware false-positive filtering
- **Context-aware autocomplete:**
  - Dot-access member completion for 30+ SE types
  - Variable-type resolution (works with `var`, casts, `as` patterns)
  - Sprite name / font name completion inside string literals
  - Reflection-based member discovery for any SE API type
- **Real SE DLL analysis** — loads game DLLs as Roslyn metadata for accurate semantic diagnostics
- **Implicit standard usings** (`System`, `System.Linq`, etc.) for analysis

### 📸 LCD Snapshot Capture & Live Streaming
- **Four ready-to-paste helper snippets** (PB / Mod / Plugin / Pulsar) with dormant streaming
- **Capture live LCD panels** from running game to editable canvas layouts
- **Live LCD Streaming (Plugin only)** — real-time frame streaming over named pipe or file
  - **Self-disarming timer** (default 60s) — zero overhead when dormant
  - **Pause/Resume** — freeze frame, edit, resume stream
- **Snapshot Merge** — combine original source code + runtime snapshot for best of both:
  - Source preserves tracking, expressions, control flow
  - Snapshot provides true runtime positions/sizes
  - Matched by (Type + Data) in occurrence order
- **Snapshot tagging** — `_snapshotTag` field identifies which plugin produced the snapshot

### 🛠️ File Operations & Workflow
- **Save/Load layouts** (`.seld` XML) — includes original source code, so animation, round-trip, and detected methods restore fully
- **Bidirectional VS Code sync** — `File → Sync Script File (VS Code)…` watches `.cs` file for external edits and writes canvas changes back in real time
- **Export Script** (`Ctrl+Shift+S`) — save code panel to `.cs` file
- **Auto-detect script type** on import (PB / Mod / Plugin / Pulsar)

### 🐛 Debug Analysis Tools
- **Debug Stats Panel** — sprite count, texture/text breakdown, draw calls, game thread load estimate, load rating (🟢 Light → 🔴 Extreme)
- **Overdraw Heatmap** — 8×8 cell color-coding by overlap count (blue → red)
- **Bounding Box Overlay** — dashed rectangles + `#index` labels for every sprite
- **Texture Size Warnings** — ⚠ indicators for sprites where source texture ≥ 4× rendered size (VRAM waste)
- **VRAM Budget Dialog** — per-texture dimensions, memory usage, total footprint

---

## 🚀 Getting Started

### Requirements
- **Windows** + **.NET Framework 4.8**
- **Space Engineers** (recommended for full texture previews)
- **Visual Studio 2019+ or Build Tools** (optional — for built-in compiler only)

### Running the Tool
1. Download and extract a release zip
2. Run `setup.ps1` **once** to copy SE/Torch DLLs:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\setup.ps1
   ```
   (Auto-finds SE and Torch on your machine, copies needed DLLs. Run again after game updates.)
3. Launch `SESpriteLCDLayoutTool.exe`

### Optional: Import All Sprite Names
Run this in a PB once, then paste the Custom Data into **Edit → Import Sprite Names**:
```csharp
public Program()
{
    var surface = Me.GetSurface(0);
    var list = new List<string>();
    surface.GetSprites(list);
    Me.CustomData = string.Join("\n", list);
}
```

---

## 📖 Workflow

### Quick Start
1. Browse sprites in the left catalog or search by name
2. Drag onto canvas (or type custom texture name)
3. Arrange, resize, rotate, reorder layers
4. Select target output (PB / Mod / Plugin / Pulsar)
5. **Copy Code** and paste into your script!

### Advanced: Code Round-Trip
1. **Edit → Paste Layout Code** — paste your full source
2. Click **▶ Execute** — sprites appear with source tracking
3. Edit visually (drag, resize, recolor)
4. **Copy Code** — only changed properties are patched back into original source

### Animation Workflow
1. Add sprites to canvas
2. Right-click sprite → **Edit Animation…**
3. Add keyframes on timeline, set positions/colors/rotation
4. Choose easing and loop mode
5. **✏ Update Code** — merges animation arrays into code panel
6. **▶ Play** to preview live

### GIF Export Workflow
1. Design or import animated layout
2. **File → Export Animated GIF…**
3. Choose mode (Fresh run or Continue from current tick)
4. Set duration, FPS, output size
5. Save — ready for GitHub/Discord/bug reports

### Live Streaming (Plugins)
1. **Edit → Start Live Listening** in tool
2. In plugin, call `StartLcdStream(60)` (auto-disarms after 60s)
3. Frames appear in real time
4. **Pause** to freeze, edit, **Resume** to continue

### Rebuilding Layer Order After File Load
When loading a script file, the layer list may not reflect true code order. To fix:
1. Select main render method in dropdown
2. Click **Step Forward** once
3. Click **▶ Execute**
4. Layer list rebuilds in correct order; sprite-to-code navigation works

---

## ⌨️ Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+Z` | Undo canvas state |
| `Ctrl+Y` | Redo canvas state |
| `Ctrl+S` | Save layout |
| `Ctrl+O` | Open layout |
| `Ctrl+Shift+S` | Export script (.cs) |
| `Ctrl+V` | Paste layout code |
| `Ctrl+C` | Copy generated code |
| `Ctrl+D` | Duplicate selected sprite(s) |
| `Ctrl+A` | Select all sprites (canvas focused) |
| `Delete` | Delete selected sprite(s) |
| `+` / `-` | Move sprite up/down in layers |
| `Arrow keys` | Nudge sprite 1px |
| `Shift+Arrows` | Nudge sprite 10px |
| `G` | Toggle snap to grid |
| `Ctrl+Shift+G` | Toggle rulers + snap to edges |
| `Shift+Click` | Extend/toggle selection |
| `Middle-drag` | Pan canvas |
| `Mouse wheel` | Zoom |

**Code Editor**
| `Tab` | Smart indent selection |
| `Shift+Tab` | Outdent selection |
| `Enter` | Auto-indent new line |
| `Ctrl+Space` | Trigger autocomplete |

> **💡 Undo/Redo & Code Sync:** `Ctrl+Z`/`Ctrl+Y` restore canvas instantly. Click **Generate Code** afterward to sync the code panel (protects hand-edited expressions from silent overwrite).

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

---

## Contributing

Bug reports, feature requests, and pull requests are welcome!

## License

MIT License

---

**For complete version history**, see [CHANGELOG.md](CHANGELOG.md).

**Latest Release:** v3.10.1 (2026-04-29) — Plugin playback speed fixes, deterministic GIF capture timing, animated GIF export

---

Made for the Space Engineers community ❤️  
Happy building!

Contributing
- Bug reports and PRs welcome. Keep changes scoped, unit-testable, and target .NET Framework 4.8 for this repo.

License
- MIT. See LICENSE file.

Changelog
- The canonical changelog is in CHANGELOG.md. The repository contains the latest v3.10.1 entry (packaging, playback timing, GIF capture fixes, export UI). For a quick summary of recent work see CHANGELOG.md.

Contact
- Use GitHub issues on the project repo for feature requests, bugs, or packaging help.
