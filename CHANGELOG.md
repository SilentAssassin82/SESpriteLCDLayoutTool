# Changelog

All notable changes to SE Sprite LCD Layout Tool will be documented in this file.

## v3.8.2 - 2026-07-30

### Fixed
- **Multi-sprite preview loop hitch** — the shared playhead timer now wraps at the LCM of all sprite animation periods instead of the simple maximum. This ensures every sprite hits its natural loop boundary simultaneously, preventing shorter-cycle sprites (e.g. 60-tick SemiCircles) from hitching back to frame 0 while longer-cycle sprites (e.g. 90-tick Triangle) are still mid-animation.
- **Mid-loop keyframe stretch causing playhead wrap** — dragging a keyframe bar to a tick beyond the current frozen loop window now immediately extends the window, so the playhead no longer wraps prematurely and resets all sprites in the middle of a loop.
- **Hover tooltips not appearing on initial load** — `SetCodeText` now triggers a background `ComputeSemanticMarkers` pass so the Roslyn semantic cache is populated immediately rather than waiting for the first `TextChanged` event.

## v3.8.0 - 2026-07-29

### Added
- **Reflection-based autocomplete expansion** — `RoslynMemberProvider` now resolves member lists for any Space Engineers or standard API type via `AppDomain` assembly reflection, with a permanent cache. Types not in the hardcoded `DotMembers` dictionary (e.g. `IMyTextSurface`, `IMyGridTerminalSystem`) now get accurate member suggestions automatically.

### Fixed
- **Autocomplete dot-trigger latency** — popup now fires the instant `.` is typed, using the `CharAdded` event path which bypasses the `_suppressCodeBoxEvents` guard that was delaying `TextChanged`-based triggering by one keystroke.
- **Multi-sprite timeline modulo bleed** — when a short animation (e.g. 60-tick SemiCircle) and a longer animation (e.g. 150-tick Triangle) were merged into a single script, stretching the longer animation's last keyframe incorrectly updated the shorter animation's `% 60` loop modulo to `% 150` via an unscoped regex. The tick-modulo update in `MergeKeyframedIntoCode` is now scoped to each animation's own tick counter variable (e.g. `_tick`, `_tick2`), so each animation loops independently at its own period. Canvas and preview are now in sync.

## v3.7.0 - 2026-07-28

### Added
- **Code folding** — fold/collapse any `{...}` block in the code editor via the gutter margin. Fold levels are computed manually from brace depth since the Roslyn/Null lexer doesn't provide them automatically.
- **Auto-expand on sprite navigation** — jumping to a sprite from the layer list now automatically expands any folded region containing the target line before scrolling to it.
- **Word occurrence highlight** — double-clicking any identifier highlights all other occurrences in the editor with a subtle blue box indicator (indicator 15). Clears automatically when the caret moves off the word.
- **Brace matching** — the `{` `}` `(` `)` `[` `]` pair under or immediately before the caret is highlighted in gold; unmatched braces highlight in red.
- **Auto-indent** — pressing Enter carries the current line's indentation to the new line. After `{` adds one extra indent level; if `}` immediately follows the caret the closing brace is placed on its own correctly-indented line.
- **Auto-close brackets** — typing `{` `(` `[` `"` inserts the matching closer and places the caret between them. Wraps any active selection. Typing a closer skips over an already-inserted one rather than doubling up.

### Fixed
- **Fade + ColorCycle animation conflict** — `FadeEffect` now emits a `Color.A` sub-key override instead of overwriting the full `Color` property, so Fade and ColorCycle can compose correctly. The animation injector folds the alpha channel into whatever color source is active (ColorCycle, keyframe, or static RGB).
- **`UnwrapBlinkGuards` newline collapse** — `Split("\n")` was discarding newlines, collapsing multi-sprite Add blocks onto one line and causing sprite deletion on regeneration. Fixed by re-emitting `\n` between split pieces.

## v3.5.0 - 2026-07-21

### Added
- **Real SE DLL loading for code analysis** — the syntax highlighter now loads actual Space Engineers game DLLs (VRage.Game.dll, VRage.Math.dll, Sandbox.Common.dll, etc.) from your SE Bin64 folder for Roslyn semantic analysis, providing accurate tooltips, member resolution, and diagnostics. Falls back to source stubs if SE is not installed.
- **Implicit standard .NET usings for analysis** — `using System;`, `using System.Collections.Generic;`, `using System.Linq;`, `using System.Text;`, and `using System.IO;` are now included in the implicit using block for Roslyn analysis, eliminating false-positive squiggles on `Math`, `List<T>`, `StringBuilder`, and other standard types.
- **PB bare-script class wrapping** — bare Programmable Block scripts (file-scope methods without a class declaration) are now wrapped in a `class Program : MyGridProgram { }` container for Roslyn analysis, so PB-style code gets full semantic diagnostics without spurious errors.

### Fixed
- **Autocomplete dot-trigger reliability** — the autocomplete popup now triggers immediately when typing `.` after a type or variable name; previously it required typing an additional character due to a race condition with the `_suppressCodeBoxEvents` guard.
- **Autocomplete mouse-click selection** — clicking an item in the autocomplete popup now correctly commits the selection; previously the popup would close without inserting because the editor's `LostFocus` handler was hiding it before the click registered.
- **Autocomplete focus management** — after committing an autocomplete selection via mouse click, focus is correctly returned to the code editor so you can continue typing immediately.

## v3.4.0 - 2026-05-15

### Changed
- **Complete code editor refactor** — replaced RichTextBox-based code editor with Scintilla (ScintillaNET), providing professional-grade syntax highlighting, line numbers, code folding, and improved performance for large scripts.
- **Enhanced syntax highlighting** — Scintilla editor now provides C# lexer-based syntax highlighting with proper tokenization for keywords, strings, comments, operators, and identifiers.
- **Improved code editing experience** — added line number margins, smooth scrolling, better selection handling, and standard code editor keyboard shortcuts.
- **Performance improvements** — Scintilla's native implementation provides significantly better performance when editing large code blocks compared to the previous RichTextBox implementation.

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
