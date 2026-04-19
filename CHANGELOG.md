# Changelog

All notable changes to SE Sprite LCD Layout Tool will be documented in this file.

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
