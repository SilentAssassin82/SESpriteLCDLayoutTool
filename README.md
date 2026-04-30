# SE Sprite LCD Layout Tool

WYSIWYG editor for designing Space Engineers LCD sprite layouts. Build screens visually, preview real in-game textures, animate, debug, and export ready-to-paste C# code for Programmable Blocks, mods, and plugins.

![.NET Framework 4.8](https://img.shields.io/badge/.NET%20Framework-4.8-blue) ![License](https://img.shields.io/badge/license-MIT-green)

This README is refreshed to reflect the current state of the project (v3.10.1). Screenshots in the repository may be outdated — keep them, swap later as you mentioned.

Contents
- Features (short)
- Quick start (build, run, package)
- Snapshot helpers (summary)
- GIF export (notes and caveats)
- Troubleshooting (packaging/version)
- Contributing & License
- Changelog pointer

If you want a longer, historic feature reference the in-app Help or the full CHANGELOG.md.

Quick summary of what the tool does
- Visual canvas with layer ordering, multi-select, snapping, rotation, and previews of real SE textures
- Built-in Roslyn-based compiler and executor: run PB/mod/helper code locally to preview or import runtime sprites
- Animation preview, tick history, variables/watch, console, and method heatmap
- Live snapshot import and optional named-pipe streaming from plugins
- Export helpers: copy-to-clipboard code snippets and an Export Animated GIF feature for quick demos

Getting started (short)
1. Requirements: Windows + .NET Framework 4.8. Visual Studio or Build Tools are only needed if you want in-tool compilation (Execute Code).
2. Run setup once to copy SE/Torch DLLs: `powershell -ExecutionPolicy Bypass -File .\setup.ps1`
3. Run the exe: `SESpriteLCDLayoutTool.exe`

Building from source
- Open the solution in Visual Studio (target framework 4.8) and build in Release.
- The package script (`package-release.cmd`) reads version from `Properties\AssemblyInfo.cs`, builds the Release output, and creates `release\SESpriteLCDLayoutTool-v<version>.zip`. If you bump versions, update AssemblyInfo accordingly.

Importing sprite names (recommended)
1. In a PB, run `surface.GetSprites(list)` and write the list to `Me.CustomData`.
2. In the tool: Edit → Import Sprite Names and paste the list.

Snapshot helpers (brief)
- The tool can generate small helper snippets for PB, mods, and plugins that serialize the current frame as code you can paste back into the editor. Useful when you want exact runtime-resolved positions or want to import frames from a running game.
- Live streaming is supported for plugins (named pipe). See Edit → Start Live Listening in the app for details.

Animated GIF export (notes)
- Export Animated GIF captures frames from the current animation state and writes a looping GIF. Options: duration, FPS, warm-up frames, output size, and hide-reference-boxes.
- Caveat: GDI+'s GIF encoder is limited in palette/dither behavior. The current exporter uses the built-in encoder and provides acceptable results for quick demos. Higher-quality GIFs may require a specialized encoder library or an external tool for superior quantisation/dithering.

Troubleshooting
- Packaging produced the wrong version zip? The package script reads `Properties\AssemblyInfo.cs` to determine the version. Update `AssemblyVersion`/`AssemblyFileVersion` before packaging.
- Build issues: ensure Visual Studio (or Build Tools) is installed if you need the compiler; otherwise the app runs for editing and import features that do not require Roslyn.

Contributing
- Bug reports and PRs welcome. Keep changes scoped, unit-testable, and target .NET Framework 4.8 for this repo.

License
- MIT. See LICENSE file.

Changelog
- The canonical changelog is in CHANGELOG.md. The repository contains the latest v3.10.1 entry (packaging, playback timing, GIF capture fixes, export UI). For a quick summary of recent work see CHANGELOG.md.

Contact
- Use GitHub issues on the project repo for feature requests, bugs, or packaging help.
