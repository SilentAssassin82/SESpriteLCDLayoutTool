# Changelog

All notable changes to SE Sprite LCD Layout Tool will be documented in this file.

## v3.5.0 - 2026-07-21

### Added
- **Real SE DLL loading for code analysis** — the syntax highlighter now loads actual Space Engineers game DLLs (VRage.Game.dll, VRage.Math.dll, Sandbox.Common.dll, etc.) from your SE Bin64 folder for Roslyn semantic analysis, providing accurate tooltips, member resolution, and diagnostics. Falls back to source stubs if SE is not installed.
- **Implicit standard .NET usings for analysis** — `using System;`, `using System.Collections.Generic;`, `using System.Linq;`, `using System.Text;`, and `using System.IO;` are now included in the implicit using block for Roslyn analysis, eliminating false-positive squiggles on `Math`, `List<T>`, `StringBuilder`, and other standard types.
- **PB bare-script class wrapping** — bare Programmable Block scripts (file-scope methods without a class declaration) are now wrapped in a `class Program : MyGridProgram { }` container for Roslyn analysis, so PB-style code gets full semantic diagnostics without spurious errors.

### Fixed
- **Autocomplete dot-trigger reliability** — the autocomplete popup now triggers immediately when typing `.` after a type or variable name; previously it required typing an additional character due to a race condition with the `_suppressCodeBoxEvents` guard.
- **Autocomplete mouse-click selection** — clicking an item in the autocomplete popup now correctly commits the selection; previously the popup would close without inserting because the editor's `LostFocus` handler was hiding it before the click registered.
- **Autocomplete focus management** — after committing an autocomplete selection via mouse click, focus is correctly returned to the code editor so you can continue typing immediately.

## v3.3.0 - 2026-04-19

### Added
- Layer list hover tooltips with delayed display and richer sprite metadata context.
- Embedded multi-sprite timeline preview controls for context/path/ghost/focus/dim workflows.

### Changed
- Animation playback update cadence now throttles expensive inspector and watch refresh paths for smoother complex scenes.
- Code heatmap updates now skip insignificant timing jitter and use a lower-frequency visual refresh cadence.
- Code diagnostics filtering now suppresses common false positives for implicit Space Engineers/Torch/Pulsar runtime symbols.

### Fixed
- Heatmap painting now uses redraw suppression for bulk RichTextBox background updates, eliminating visible top-to-bottom repaint streaming.
- Multi-sprite timeline open flow reliability improved for complex selections.
- Timeline-driven batch code update path hardened.
- Playback stop now resets profiler/inspector timing gates so first post-stop inspect/scrub updates are immediate.

## Earlier versions

- Version history through v3.2.0 is currently documented in README.md under the changelog section and can be migrated here over time.
