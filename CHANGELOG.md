# Changelog

All notable changes to SE Sprite LCD Layout Tool will be documented in this file.

## v4.1.0 - 2026-05-01

### Added
- **Render Parameter Inspector** (`Edit → Render Parameter Inspector…`) — a WYSIWYG knob panel for tweaking the numeric "magic numbers" in any render method or class-level constants block without hand-editing source. Designed for iterating on layouts (padding, row heights, bar widths, gauge thresholds, etc.) with immediate visual feedback on the LCD canvas.
  - **Roslyn-first scanner** (`Services/RenderParameterScanner.cs`) — parses the user's source via `Microsoft.CodeAnalysis.CSharp` rather than regex so labels, group keys and edit positions stay accurate even with nested generics, lambdas, attributes and method-call argument lists. Falls back to the regex scanner on parse failure.
  - **Switch-case "virtual method" support** — for queue-processor renderers that fan out via `switch (row.RowKind) { case Header: … }` (the `Render<CaseName>` synthesized method pattern used throughout `LcdManager.cs`), the scanner walks the matching `case` section and exposes its inline literals just like a real method body, so each row kind can be tuned independently.
  - **Preprocessor-aware parsing** — `CSharpParseOptions` is configured with `TORCH`, `DEBUG`, `PULSAR` so `#if`-guarded code paths (Torch-only render branches, debug-only literals) are visible to the scanner instead of being silently dropped.
  - **Syntax-aware labels** — a literal inside `MySprite.CreateText(..., size: 1.4f, ...)` is labelled by its argument name/position rather than a misleading enclosing context like `row.TextColor`. Variable assignments, constructor argument positions, and named arguments all flow through to the row label.
  - **Grouping by enclosing scope** — knobs are clustered by target variable / enclosing call (e.g. `new Color(...)`, `MySprite.CreateText(...)`, `var bar = …`) and rendered under a header. The `Constants` group is pinned to the top of the panel.
  - **Collapsible group headers** — each header is a clickable label with a `▼/▶` chevron; toggling hides/shows the group's rows in place. Collapsed state persists across method-dropdown changes and Reset within a single dialog session.
  - **Per-knob slider + numeric** — every numeric knob exposes a synced `NumericUpDown` and a `DarkSlider` (custom owner-drawn dark-themed track + thumb in `Forms/DarkSlider.cs`) with auto-computed range based on the original value (symmetric ±span, clamped non-negative for known-positive originals, ±2.0 for small floats). Two-way bound without feedback loops.
  - **Color swatch grouping** — sequences of integer literals inside `new Color(R, G, B)` / `new Color(R, G, B, A)` constructors collapse into a single color row with a clickable swatch + RGB(A) text label that opens the standard `ColorDialog`. Component literals are skipped by the generic numeric pass so they don't show up twice.
  - **Live preview** — when the *Live preview* checkbox is enabled, knob changes are debounced (~250 ms) and the patched source is fed back into the existing execute pipeline so the LCD canvas refreshes in place, no Apply round-trip needed.
  - **Save / Load Preset** — knob configurations can be saved to a tiny line-based `.lcdpreset` text file (no JSON dependency on net48) keyed by `<name>@<literalStart>` for stable round-trip. Loading applies values to matching knobs and re-renders the rows preserving `CurrentValue`, then auto-fires live preview if enabled.
  - **Reset / Apply** — Reset re-scans from the original source (rebuilding both numeric and color rows uniformly); Apply writes the patched source back to the editor for the next Execute.

### Fixed
- **Right-click "Jump to Definition" failed for most detected methods** — `CodeExecutor.FindMethodDefinitionOffset` only matched return types `void` or `List<MySprite>`, so methods returning `Vector2`, `MySprite`, `IMyTextSurface`, generic types, custom types, or any method declared with modifiers like `virtual` / `override` / `async` silently produced "Could not find definition…". The regex now accepts any return type (including generics and arrays) plus the full set of common modifiers, and rejects matches that look like call sites (`return X(...)`, `.Foo(...)`, expressions starting with `=` or `.`). The switch-case fallback (`Render<CaseName>` → `case ...:`) is preserved.
- **Jump-to-definition selection landed on the wrong line on Windows-EOL files** — `MainForm.JumpToMethodDefinition` was searching `_codeBox.Text.Replace("\r", "")` and then calling `_codeBox.Select(start, length)`, but Scintilla's selection model uses offsets into the actual document. Stripping `\r` shifted every offset by the number of preceding line endings, so the highlighted region drifted further off as you went down the file. Now searches and selects against the live document directly.

## v4.0.0 - 2026-04-30

### Fixed
- **Timeline scrubber did not rewind the canvas** — dragging the tick slider updated the variables panel but the LCD canvas stayed frozen at the last played frame. `OnTimelineScrub` now applies the sprite snapshot from `SpriteHistory` for the selected tick, restoring each sprite's position, size, color, and rotation so the canvas visually matches the history state at every scrub position.
- **Animation injection left stale identifiers after effect removal** — removing an animation effect (e.g. Fade, Pulse) stripped the generated compute block and field declarations but left the animated expression (e.g. `(byte)(fadeAlpha)`, `100f * pulseScale`) baked into the sprite's static initializer. Those orphaned identifiers caused CS0841 compile errors on the next Execute. `RoslynAnimationInjector` now rewrites every animatable property (`Position`, `Size`, `Color`, `RotationOrScale`) back to clean static literals each pass, even for sprites with no current effects.
- **Duplicate-suffix collision when the same `SpriteEntry` appeared twice** — stale clones from certain refresh paths could resolve to the same Add block via `FindSpriteAddNodeById`, each consuming a suffix slot. The second copy would bake a higher-suffix identifier (e.g. `arot2`) into the property text while only the first's compute (`arot`) was emitted, producing CS0841. Injection now deduplicates entries by stable `Id` (or reference identity for empty-Id sprites) before assigning suffixes.
- **Marker-block stripping was not fully idempotent** — if a prior bug produced two consecutive ANIM-FIELDS or ANIM-EASE regions with identical markers, only the first pair was removed per pass and the second was left in place. `StripMarkerBlock` now loops until no further pairs remain.
- **`_tick` re-declared when already present in user code** — the injector emitted `int _tick = 0;` inside the ANIM-FIELDS region even when the host snippet already declared a `_tick` field with a modifier (`private`, `static`, etc.). `HasExistingField` now accepts any common numeric/bool type with any combination of access and storage modifiers.
- **`_tick++` inserted after trailing trivia** — `InsertTickIncrement` used `FullSpan.End` (which includes the trailing comment/newline trivia of the first statement) rather than `Span.End`, so `_tick++` could land on the wrong line in some code layouts. Switched to `Span.End`.
- **`Alignment` emitted for texture sprites** — `GenerateStaticAddBlock` always wrote `Alignment = TextAlignment.CENTER` regardless of sprite type. That property is only meaningful for text sprites; the line is now emitted only when `sp.Type == SpriteEntryType.Text`.

## v3.10.1 - 2026-04-29

### Fixed
- **Plugin-type animation playback was sluggish** — `PulsarPlugin`, `ModSurface`, and `TorchPlugin` scripts didn't expose a PB-style `UpdateFrequency`, so the animation timer fell back to its slowest interval (~10 fps). `AnimationPlayer.UpdateTimerInterval()` now applies a ~60 ms (≈16 fps) fallback for plugin script types, and per-frame snapshot/UI work was throttled to keep the watch window readable without choking playback.
- **GIF playback animated at wrong speed during capture** — capture was driven by wall-clock deltas while the GIF was encoded at the chosen FPS, so on-screen playback during export looked sped up and the resulting GIF could end up time-skewed. `AnimationPlayer.StepForward(double forcedElapsedSeconds)` now lets the GIF exporter feed a deterministic per-frame elapsed time, so the script's in-animation clock matches the encoded frame rate exactly. Heavy diagnostics (timeline scrubber, console output, heatmap, variable inspector) are also skipped while `_gifCaptureInProgress` is set.
- **GIF export progress UI starving the message pump** — `MainForm.GifExport` now updates the progress label/bar and pumps `Application.DoEvents()` at most every few frames during warm-up and capture, eliminating the redraw thrash that contributed to the perceived slowdown.

## v3.10.0 - 2026-04-28

### Added
- **Animated GIF export** (`File → Export Animated GIF…`) — capture a few seconds of any animated layout straight to a looping `.gif` for quick demos and bug reports.
  - **Two capture modes**: *Fresh run* (compiles and runs the script from tick 0) or *Continue from current tick* (records from the live/paused animation session, preserving state). The latter is essential for layouts with long warm-up/intro phases that exceed the recording window.
  - **Warm-up frames**: skip a configurable number of frames before recording starts (fresh-run mode) so intro animations can settle.
  - Configurable duration, FPS, output pixel size, and looping; written via a built-in GIF89a encoder (`Services/GifExporter.cs`) with NETSCAPE 2.0 loop extension.
  - **Hide reference boxes (gold)** checkbox in the export dialog produces a clean GIF even if the editor has the bounding boxes turned on.
- **View → Show Text Bounding Boxes** toggle — the gold dashed outline around every text sprite is now optional. Hide it for cleaner editing/screenshots without affecting the export pipeline. Defaults to on.

### Fixed
- **Properties panel edits silently dropped on helper-call sprites** — sprites authored via helper functions (e.g. `DrawGauge(s, "POWER", …)` → `MySprite.CreateText(label, …)`) couldn't be source-tracked because the parser doesn't follow the helper's parameter back to the call-site literal. Without an `ImportBaseline`, property edits never reached the code panel. `ExecuteCode` now seeds a baseline-only entry for every untracked sprite (Strategy 4) so `CodePatcher` pass 2 can resolve the literal via the sprite navigation index at edit time.
- **CodePatcher unpatched-change reporting** — when an edit hits a non-literal (interpolated string, ternary, expression-based property), `CodePatcher.PatchOriginalSource` now records the count and a human-readable reason via `LastUnpatchedChangeCount` / `LastUnpatchedReason` so the UI can surface a status warning instead of looking like a silent failure.
- **Heatmap colours all-green for single-method scripts** — switched from relative min/max normalisation to absolute millisecond thresholds. PB scripts (often a single instrumented `Main`) now show meaningful per-method colour, not a flat baseline.
- **Heatmap regex misidentifying `for (...) {` etc. as a method declaration** — the method-detection regex (in both `MainForm.cs` and `Services/CodeExecutor.cs`) now requires a real return-type token and rejects the C# control-flow keyword set (`if`, `for`, `foreach`, `while`, `switch`, `catch`, `using`, `lock`, `do`, `else`, `return`, `throw`, `new`, `fixed`, `unchecked`, `checked`). PB-style modifierless declarations (`void Main(...)`, `void Save()`) and `override` are now matched correctly.

### Repository hygiene
- `.gitignore` extended for Copilot/local-AI workspace caches (`**/.localpilot/`, `**/.copilot/`) and ad-hoc helper scripts (`_*.ps1`).

## v3.9.1 - 2026-08-01

### Fixed
- **Layer list shows actual text content for PB scripts** — text sprite labels in the layer list now display the sprite's text content (e.g. `TEXT "RADAR SWEEP"`) instead of the generic `Text` / `Text.2` placeholder. A shared `SpriteTypeHint()` helper was added and wired into all four `ImportLabel`-setting paths (Apply Code, file load, streaming, glyph replacement).
- **Properties panel text edit only applies once** — editing a text sprite's content in the properties panel now correctly updates the code on every keystroke. The root cause was `RefreshSourceTracking()` Strategy 1 building pool keys using `SpriteName ?? Text`, which always resolved to `"SquareSimple"` (the default) for text sprites. The key logic now branches explicitly on sprite type, using `"TEXT|" + Text` for text sprites and `"TEXTURE|" + SpriteName` for texture sprites, so `ImportBaseline` is re-established correctly after each patch.
- **Properties panel typing stutter** — the code writeback triggered by property edits is now debounced (350 ms) so the code panel only updates after a brief pause in typing rather than on every single keystroke.

## v3.9.0 - 2025-07-15

### Added
- **Export Script** (`File → Export Script…` / `Ctrl+Shift+S`) — saves the current code panel contents directly to a `.cs` file. Users writing PB scripts from scratch no longer need to copy-paste out of the editor; the file-sync path was already handled, now in-app authoring is too.

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

### Fixed
- Code editor diagnostics: improved false-positive undefined symbol suppression for SE ecosystem scripts.
- Multi-sprite timeline open flow and batch code update path hardened for timeline-generated edits.

---

## v3.2.0 - 2026-04-15

### Added
- **Wavy red underlines on syntax errors** — native RichEdit wavy red underlines with SE/Torch/Pulsar-aware false-positive filtering.
- **Bare PB script wrapping** — diagnostic-only wrapping in dummy class for Roslyn parse, corrected span offsets for accurate underlines.

### Fixed
- **From-scratch animation flow restored** — RoslynAnimationInjector now only accepts complete PB programs; bare snippets fall through to complete generators.
- **Simple-animation apply flow** — Apply button correctly uses `GenerateSimpleComplete` for method-body snippets.
- **Variables panel now shows mod fields by default** — `_`-prefixed fields visible for ModSurface/PulsarPlugin/TorchPlugin; only infrastructure fields hidden.
- **Sparklines work for mod scripts** — numeric mod fields populate Trend column with inline charts.
- **Mod state-update method detection** — `UpdateAfterSimulation`/`UpdateBeforeSimulation` regex now includes session component overrides.
- **Bare mod script stub fields** — bare mod scripts receive observable stub fields for Variables panel display.

---

## v3.1.0 - 2026-04-10

### Added
- **Stackable animation effects** — apply multiple simple animations to same sprite without overwriting.
- **Ordinal-aware sprite matching** — duplicate sprite names resolve by position among all layout sprites.
- **Global `_tick` counter** — all animation effects share single field, eliminating undefined variable errors.

### Fixed
- **Blink guard cleanup** — blink wrappers bracketed with marker comments, cleanly unwrapped before re-injection.
- **Span-based blink wrapping** — uses `addNode.Span` to avoid capturing leading trivia inside `if` block.
- **Missing Add block insertion** — `EnsureAllSpritesHaveAddBlocks` inserts only deficit blocks at correct positions.

---

## v3.0.0 - 2026-04-05

### Added
- **Roslyn-based animation injection** — structural AST approach replacing text-surgery code merging.
- **`IAnimationEffect` data model** — 8 concrete effect types with typed list in `SpriteEntry.AnimationEffects`.
- **`RoslynAnimationInjector` service** — parses AST, injects animation fields/compute/property-overrides with marker comments for idempotent updates.
- **Stacking multiple animations** — rotation keyframes + position keyframes + color cycle simultaneously on same sprite.
- **Group follower animation overhaul** — references leader's computed variables via `LeaderSuffix` with delta-based position/size.
- **Custom code editor undo/redo** — `CodeUndoManager` text-only undo stack immune to syntax highlighting formatting noise.
- **Enhanced syntax highlighting** — type names in member access expressions correctly highlighted in teal.

### Changed
- **Suffix resolution step** — maps `KeyframeEffect` instances to array suffixes for leader lookup.

---

## v2.9.9 - 2026-03-28

### Added
- **Sprite lock flag** — per-sprite lock state with 🔒 icon in layer list; locked sprites skip canvas hit-testing.
- **Lock/Unlock context menu** — right-click selected sprite(s) for Lock/Unlock Layer.
- Lock state preserved through undo/redo and `.seld` project files via `SpriteEntry.IsLocked`.

---

## v2.9.8 - 2026-03-25

### Added
- **Canvas rulers + snap-to-sprite edges** — 20px rulers with adaptive tick intervals, cursor crosshairs, magnetic edge snapping.
- **Show Rulers / Snap to Sprite Edges toggles** — View menu controls, default ON.
- **Keyboard shortcut** `Ctrl+Shift+G` toggles both.

---

## v2.9.7 - 2026-03-20

### Added
- **Multi-select + group move + align tools** — box-select (rubber band), Shift+click, Ctrl+A, group drag/nudge.
- **Align / Distribute submenu** — align edges/centers, space evenly (H/V) when 2+ sprites selected.

---

## v2.9.6 - 2026-03-15

### Fixed
- **Rotation precision** — rotation fields now support 4 decimal places (0.0001 increment, range −100 to 100); exact 2π (6.2832) enterable.

---

## v2.9.5 - 2026-03-10

### Changed
- **Simple animation generator upgraded to round-trip apply** — Rotate/Oscillate/Pulse/Fade/Blink/ColorCycle now work identically to keyframe generator.
- **3-tier merge strategy:** block replace, in-place variable merge, or complete program generation with footer marker.
- Dialog pre-populates with existing animation settings; Apply button closes dialog and updates Diff tab + script sync.

---

## v2.9.4 - 2026-03-05

### Fixed
- **Syntax highlighting after `#if` conditionals** — switched to `SourceCodeKind.Regular` with TORCH/STABLE/DEBUG/RELEASE symbols; inactive branches render in dark grey.

---

## v2.9.3 - 2026-03-01

### Changed
- **Services layer partial class refactor** — CodeExecutor split into SEStubs; CodeGenerator split into CodePatcher; AnimationSnippetGenerator split into KeyframedCodeGenerator.
- **MainForm split** — UIBuilder, Variables partials added; MainForm.cs reduced from 5,573 → 2,515 lines.

---

## v2.9.2 - 2026-02-25

### Changed
- **MainForm partial class refactor** — split into 7 focused partial files (Streaming, Animation, FileIO, ContextMenus, Watch, DebugTools, DarkTheme).
- UTF-8 BOM encoding fix for Unicode characters in button labels.

---

## v2.9.1 - 2026-02-20

### Added
- **Syntax highlighting** — C# keywords, types, strings, comments, numbers with custom SyntaxHighlighter service.

---

## v2.9.0 - 2026-02-15

### Added
- **Animation Groups** — link sprites into groups with leader keyframe data + automatic per-sprite offsets.
- **Create/Join/Leave Animation Group** context menu; layer list shows 🎬/🎭 indicators.
- **Group-aware code generation** — single shared keyframe arrays with delta offsets.
- **Group animation undo/redo** — deep-clones `AnimationGroupId` and `KeyframeAnimation` data.
- **Reusable compilation context** — `AnimationPlayer.AdoptContext()` for seamless field inspection.
- **Pre-computed call detection** — skips expensive Roslyn pass when calls already known.

### Changed
- **RoslynCodeMerger: always emit Alignment & RotationOrScale** — prevents data loss on round-trip.

---

## v2.8.3 - 2026-02-10

### Added
- **Copy/Paste Animation** — right-click sprite with keyframe animation → Copy Animation, paste to any other sprite.

---

## v2.8.2 - 2026-02-05

### Fixed
- **Variables panel flicker** — enabled double buffering on owner-drawn ListView.
- **Console tab flicker** — `WM_SETREDRAW` paint suppression for batch Echo updates.
- **Console line trimming** — caps at 2,000 lines, trims to 1,500 when exceeded.

---

## v2.8.1 - 2026-01-30

### Added
- **Visual Keyframe Animation Editor** — interactive timeline with draggable keyframe diamonds, per-keyframe property editing.
- **KeyframeTimeline control** — click-to-select, right-click add/delete, tick ruler.
- **7 easing types, 3 loop modes** — Linear/SineInOut/Bounce/Elastic, Loop/PingPong/Once.
- **Live preview** — renders sprite shapes with interpolated properties; texture-aware via `SpriteTextureCache`.
- **✏ Update Code button** — 3-tier merge strategy (block replace, smart array merge, append fallback).

### Changed
- **Code round-trip for keyframe animations** — `TryParseKeyframed()` parses arrays back into visual editor.
- **Script-type aware generation** — target-specific comments, field/render hints for PB/Mod/Plugin/Pulsar/LCD.

---

## v2.8.0 - 2026-01-25

### Fixed
- **Animation playback code destruction** — `IsPlaying` guard freezes code panel during playback.
- **Undo/redo code sync** — snapshots/restores `OriginalSourceCode` and `SourceLineNumber` alongside sprite state.

---

## v2.7.0 - 2026-01-20

### Added
- **Code Navigation Overhaul** — 6+ strategy chain with `SpriteNavigationIndex`, `CallerLineNumber` pipeline, loop-aware `SpriteAddMapper`.
- **Snapshot Comparison Bookmarks** — bookmark ticks A/B, diff sprite state changes with `SnapshotComparer`.
- **`DebugVariable` model** — structured debug variables from `// @DebugVar:` annotations.
- **Code injection instrumentation improvements** — switch-case `SetCurrentMethod`, local List PreRecord, StripTrailingAttributes fix.

### Changed
- **Navigation lands on sprite CREATION line** — not `.Add()` line; loop-aware helper navigates to text content line.

---

## v2.6.0 - 2026-01-15

### Added
- **Runtime Variable Inspector** — Variables tab with reflection-based field discovery, sparkline mini-charts, linked highlighting, double-click edit.
- **Watch Expressions** — custom C# expressions compiled via Roslyn, evaluated each tick with zero overhead.
- **Conditional Breakpoints** — boolean expression pauses animation on false→true transition.
- **Console / Output Tab** — captures `Echo()` output with tick tagging.
- **Method Performance Heatmap** — code editor background colored by execution time (green → red).
- **Timeline Scrubber** — TrackBar scrubs through 500-tick history; Variables panel shows historical values.
- **Sprite-to-Code Navigation** — double-click sprite in layer list to jump to source; `CodeNavigationService` multi-strategy.
- **Template Gallery** — 15+ pre-built LCD layout templates ready to insert.
- **Line Number Gutter** — custom control with synced line numbers and current-line highlighting.

### Changed
- **Roslyn Code Merger: Data property patching** — patches sprite name/text content changes.
- **Source tracking stabilization** — stable LINQ sort preserves execution order.
- **Layer list consistency** — shared `_layerListSprites` list eliminates index mismatches.

### Fixed
- **`.seld` serialization** — `[XmlIgnore]` on `SpriteMapping` prevents XML failures.
- **CodeExecutor** — `AddRange` instrumentation, `InjectMethodTimings` CurrentMethod save/restore, exposed `Compile()`.
- **Variables tab auto-switch** — no longer force-switches when selecting sprites.
- **Layer list focus theft** — `BeginInvoke` defers focus transfer after double-click navigation.

---

## v2.5.0 - 2026-01-10

### Added
- **Execute & Isolate: X-Coordinate Indexing** — O(1) lookup by X coordinate instead of O(n) linear search.
- **Roslyn Syntax Tree Merge** — `RoslynCodeMerger` service with AST-aware merging, preserves formatting/comments.
- **MethodBodyAnalyzer service** — extracts method bodies via Roslyn syntax trees.

### Changed
- **ElementSpriteMapping enhancements** — `.sprmap` v3 with Y offset persistence, position-based signatures.
- **SpriteMappingBuilder improvements** — multi-frame orchestrator, X-coordinate indexed matching.

### Fixed
- **Text sprite label fix** — always display `"TEXT 'content'"` instead of texture sprite names.

---

## v2.4.0 - 2026-01-05

### Added
- **Canvas multi-select** — Shift+click sprites on canvas for batch operations.
- **Hide Selected context menu** — right-click canvas shows count badge when multi-selected.
- **Smart brace-aware auto-indent** — Tab/Shift+Tab reformats based on brace nesting.
- **Auto-indent on Enter** — new lines match current indentation.

### Fixed
- **Switch-case method detection** — regex matches fully-qualified enum names.
- **Source file write prevention** — bidirectional sync only writes when content hash changed.
- **Code jump accuracy** — line-based position mapping for navigation.

---

## v2.3.0 - 2026-01-01

### Added
- **Layout file persistence for animation** — `.seld` files save/restore original source code.
- **VS Code bidirectional file sync** — watches `.cs` file for external edits, writes canvas changes back.
- **Auto-detect script type on import** — switches dropdown based on 6 regex patterns.

### Fixed
- **Indentation loss on round-trip** — leading whitespace preserved across 3 fix points.
- **FillPie crash** — guards against zero-size sprites.
- **Clipboard paste indentation** — normalizes `\n` to `\r\n`.
- **CS0841 in Mod/Pulsar animation** — declares `sprites` variable before call lines.
- **Sprite doubling in Mod/Pulsar** — conditional merge, FilterTopLevelCalls, full replacement in execute/isolate.
- **Isolate mode for Mod/Pulsar** — direct sprite replacement instead of snapshot merge.
- **Animation Play/Step ignoring selected method** — checks detected-calls list for focused animation.

---

## v2.2.0 - 2025-12-28

### Added
- **Script-type aware animation snippets** — auto-default `ListVarName`, target label in dialog title, context-aware comments.
- `TargetScriptType` enum with `TargetLabel()`, `FieldHint()`, `RenderHint()` helpers.

---

## v2.1.0 - 2025-12-25

### Added
- **Pulsar plugin support** — new Pulsar code style for `VRage.Plugins.IPlugin`.
- **Multi-select layer list** — Shift+click, Ctrl+click; batch operations with dynamic labels.
- **Snapshot tagging** — `_snapshotTag` field emits header, displayed in status bar on import.

### Fixed
- **Code jumping offset** — strips `\r` from Text before position calculation.

---

## v2.0.7 - 2025-12-20

### Added
- **Hide Layers Above** — right-click layer list; Show All Layers restores visibility.
- **Constrain to Surface toggle** — clamps drag/nudge to LCD bounds.
- **Deferred code refresh during drag** — shows `⟳ dragging…`, refreshes on mouse-up.
- **Coding-mode indicator** — label shows round-trip vs generated mode.

### Changed
- **Structural edits invalidate original source** — add/delete/duplicate clears round-trip tracking.
- **Post-parse sprite validation** — checks for NaN/Infinity/out-of-range after parsing.

### Fixed
- **Apply Code button visibility** — persists in round-trip mode.
- **Glyph replacement Scale** — preserves existing text scale value.
- **Layer list selection in isolation mode** — correctly tracks selected index.
- **Color swatch refresh** — updates after expression edits via auto re-execution.

---

## v2.0.6 - 2025-12-15

### Added
- **Animation snippet "Replace in Code"** — button auto-locates sprite's Add block, replaces with snippet.

### Fixed
- **Selection overwrite bug** — manual text selection preserved when clicking Insert.

---

## v2.0.5 - 2025-12-10

### Fixed
- **Round-trip patching for constructor + trailing-assignment sprites** — trailing properties included in tracked range.
- **`RefreshCode` unconditional sync** — always updates code panel regardless of dirty flag.
- **Apply Code button persists** — remains visible in round-trip mode.

---

## v2.0.4 - 2025-12-05

### Fixed
- **Canvas-edit-to-code sync** — editing sprites always updates generated code.
- **Detected methods desync** — stays in sync after canvas modification.
- **Insert at Cursor dirty flag** — no longer marks code panel as user-edited.

---

## v2.0.3 - 2025-12-01

### Added
- **List variable selector** — dropdown for `sprites` vs `frame` in animation snippet dialog.
- **Insert at Cursor** — non-modal dialog, button inserts snippet at cursor position.
- **Code editor context menu** — Select All/Cut/Copy/Paste, Set Indentation submenu.

### Fixed
- **Correct alignment** — texture sprites use actual `TextAlignment` value.

---

## v2.0.2 - 2025-11-28

### Added
- **Double-click to jump to method definition**.
- **Right-click context menu on detected methods** — Start Focused Animation, Jump to Definition.
- **Constructor-aware code execution** — uses animation pipeline for single-shot execution.
- **Focused animation mode** — non-focused sprites rendered at 20% opacity with 🔍 indicator.

---

## v2.0.1 - 2025-11-25

### Added
- **Context-aware code autocomplete** — dot-access completion, variable-type resolution, sprite/font name completion.
- 30+ SE types, all SE enums, keyboard navigation, double-click commit.

---

## v2.0.0 - 2025-11-20

### Added
- **Debug analysis tools** — Debug Stats Panel, Overdraw Heatmap, Bounding Box Overlay, Texture Size Warnings, VRAM Budget Dialog.
- **Animation Frame Timing** — Stopwatch-measured execution time in tick label.
- **`DebugAnalyzer` service** — Analyze(), AnalyzeTextureMemory(), AnalyzeSizeWarnings(), ComputeOverdrawMap().
- **Original texture dimension tracking** — GetOriginalSize() for VRAM estimation.
- **`AnimationPlayer.LastFrameMs`** — exposes recent frame execution time.

---

## v1.11.0 - 2025-11-15

### Added
- **Advancing `ElapsedPlayTime` for animations** — wall-clock delta advances Session.ElapsedPlayTime each frame.
- SetElapsedPlayTime() method generated; AnimInit() resets clock to 120s.

### Fixed
- **Compilation & runtime for mod session components** — using Sandbox.ModAPI auto-import, IMyCubeGrid ambiguity resolved, Vector2(float) constructor, DetectSurfaceCalls namespace-aware, LcdRunner base class preserved.

---

## v1.10.0 - 2025-11-10

### Added
- **Expanded SE mod/session component stubs** — MySessionComponentBase, MyAPIGateway, mod-side interfaces, power producers, entity/grid types, extended IMyTerminalBlock, upgraded StubTextSurface, additional colors, logging/misc.
- Detection regex updated for IMyTextPanel, auto-imported namespaces, base class inheritance preserved.

---

## v1.9.0 - 2025-11-05

### Added
- **Animation code snippets** — right-click sprite → Add Animation… generates ready-to-paste C# for 6 animation types.
- Parameter dialog with live code preview, Copy to Clipboard, supports TEXTURE/TEXT sprites.

---

## v1.8.0 - 2025-11-01

### Added
- **Animation orchestrator detection** — state-update method injection (Advance/Update/Tick) before rendering.

### Fixed
- **Double-advance bug** — timer/event wrappers excluded from state-update detection.
- **Simplified animation rendering** — removed key-based sprite merge, executor output shown directly.
- **Snapshot on empty layout** — creates blank layout, populates snapshot sprites as additions.

---

## v1.7.0 - 2025-10-28

### Added
- **Animation playback system** — Play/Pause/Stop/Step controls, multi-call animation, auto-detection, snapshot-anchored playback.
- **Async texture loading** — background thread fixes ContextSwitchDeadlock.
- **Texture decode error logging** — View → View Texture Load Errors….

### Changed
- **Position/Size precision** — F4 serialization (was F1).
- **SnapshotMerger** — occurrence-order matching instead of proximity.

### Fixed
- **UI fixes** — toolbar wrapping, capture/show-all buttons moved, TextBox.MaxLength set to 0.
- **Bug fixes** — hasDynamicPositions false-trigger, removed broken offset animation.

---

## v1.6.0 - 2025-10-25

### Added
- **Expanded SE API stubs** — block interfaces, IMyShipController, IMyBlockGroup, inventory system, IMyTerminalBlock extended, IMyTextSurface extended, Vector3D, enums, auto-imported namespaces.

---

## v1.5.0 - 2025-10-20

### Added
- **Full Programmable Block script execution** — mod/plugin script execution, script type auto-detection, sprite capture via SpriteCollector.
- **Functional concrete stubs** — StubTextSurface, StubProgrammableBlock, StubRuntime, StubGridTerminalSystem, StubTerminalBlock.
- **Extended math/color stubs** — MathHelper, Color constructors, operator, named colors, CreateSprite().
- **PB constructor extraction** — inlines constructor body before Main().
- **Script-type-aware UI** — result labels with tags, context-appropriate error hints.
- **`MyGridProgram` base class** — LcdRunner extends for PB scripts.

---

## v1.3.5 - 2025-10-15

### Added
- **Expression literal extraction & editing** — Vector2, float, string literals with offset-targeted patchers.
- **Unified offset management** — ShiftExpressionOffsets helper.
- **Expression model hierarchy** — ExpressionLiteral base class with typed subclasses.

---

## v1.3.4 - 2025-10-10

### Fixed
- **Live streaming visual** — canvas shows full runtime sprite list at all times.
- **User colour edits survive pause/resume** — baseline tracking prevents overwrite.

---

## v1.3.3 - 2025-10-05

### Changed
- **Live streaming round-trip** — merge in-place preserves SourceStart/SourceEnd/ImportBaseline.
- **`IsActivelyStreaming` pause-aware** — excludes paused state.
- **`RefreshCode` on pause toggle** — instant update when pausing/resuming.

---

## v1.3.2 - 2025-10-01

### Changed
- **Plugin snippet: FindPanel two-pass search** — CustomName first pass before CustomData fallback.
- **Snapshot file extension** — writes `.cs` instead of `.txt`.

---

## v1.3.0 - 2025-09-28

### Added
- **Live LCD Streaming** — named pipe real-time frame streaming, Pause/Resume editing.
- **Snapshot Helper Snippets** — four variants (PB/Mod/Plugin/Pulsar) generated by tool.
- **Snapshot Merge Workflow** — split paste dialog, sprites matched by (Type + Data), import baselines refreshed.
- **Automated Tint Detection** — analyzes atlas pixel data for grayscale vs coloured.
- **Expression-Aware Code Parsing** — ParseFloat extracts leading numeric literals.
- **Dynamic Position Detection** — (0,0) positions detected and auto-stacked.

---

## v1.2.0 - 2025-09-25

### Added
- **Code Round-Trip** — region-based and per-sprite dynamic round-trip, expression literal extraction, offset-targeted patching, unified offset management.
- **Layer List Context Menu** — right-click for Move Up/Down, Duplicate, Delete, Hide Layer/Layers Above, Show All.
- **Sprite Catalog Replace** — right-click catalog sprite to replace selected sprite's texture/glyph.

---

## v1.1.0 - 2025-09-20

### Added
- **SE Font Atlas Rendering** — PUA glyphs from DDS font atlas textures with tinting.
- **Expanded Glyph Catalog** — 10 Unicode categories, 123+ characters.
- **Font Selection Persistence** — selected font sticks when adding sprites.
- **Font Mixing Warning** — warns when switching fonts if canvas has other font family.
- **Stretch to Surface** — right-click context menu option.

### Changed
- **Color Swatch Labels** — PUA swatches labelled "(in-game only)" with explanation.

### Fixed
- **Glyph Add** — double-click correctly applies glyph character and font.

---

## v1.0.0 - 2025-09-15

### Added
- Initial release — WYSIWYG canvas, real SE texture loading, code generation (PB/Mod/Plugin/Pulsar), smart code parser, LCD snapshot capture, undo/redo, layer ordering, dark theme UI.
- Code heatmap updates now skip insignificant timing jitter and use a lower-frequency visual refresh cadence.
- Code diagnostics filtering now suppresses common false positives for implicit Space Engineers/Torch/Pulsar runtime symbols.

### Fixed
- Heatmap painting now uses redraw suppression for bulk RichTextBox background updates, eliminating visible top-to-bottom repaint streaming.
- Multi-sprite timeline open flow reliability improved for complex selections.
- Timeline-driven batch code update path hardened.
- Playback stop now resets profiler/inspector timing gates so first post-stop inspect/scrub updates are immediate.

## Earlier versions

- Version history through v3.2.0 is currently documented in README.md under the changelog section and can be migrated here over time.
