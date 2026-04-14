# Copilot Instructions

## Project Guidelines

### CRITICAL: Code Editor Dirty Flag Protection
- **NEVER** call `RefreshCode()` in a way that overwrites user-edited code without the `force: true` parameter.
- The `_codeBoxDirty` flag tracks when the user has manually edited the code panel (templates, animations, expressions).
- `RefreshCode(bool writeBack = false, bool force = false)` checks `_codeBoxDirty` internally and returns early unless `force: true`.
- **Only use `force: true`** when the user EXPLICITLY requests code regeneration (e.g., clicking "Generate Code" or "Reset Source" buttons).
- Canvas interactions (selection, dragging, nudging, layer changes) must NEVER force-overwrite user code.
- Templates with expressions (like `thermX`, `fillY`, `temperature * tubeHeight`) cannot be round-tripped - if you regenerate code from sprites, the expressions are lost forever.
- If you add a new call to `RefreshCode()`, just call `RefreshCode()` without `force` - the internal check handles protection automatically.

### CRITICAL: Pulsar/Mod Script Code Preservation
- **NEVER** clear `_layout.OriginalSourceCode` or call `RefreshCode()` after executing Pulsar/Mod scripts.
- Pulsar plugins and Mod scripts use `DrawFrame()` at runtime - their sprites have NO static source tracking (`SourceStart = -1`).
- If you clear `OriginalSourceCode` and regenerate code from these untracked sprites, you get BROKEN code that won't compile.
- The original imported code must stay in the code panel so users can edit and re-execute.
- Check `result.ScriptType == ScriptType.PulsarPlugin || result.ScriptType == ScriptType.ModSurface` before any code regeneration in execute paths.
- The `_layout.IsPulsarOrModLayout` flag is set when executing Pulsar/Mod scripts. `RefreshCode()` checks this flag and skips regeneration automatically.
- Clear `IsPulsarOrModLayout = false` when importing fresh code (file sync, paste) that has proper source tracking.

### CRITICAL: Switch-Case Method Extraction
- The switch-case method extraction feature in CodeExecutor.DetectSwitchCaseRenderMethods() must be preserved during any future code changes. This feature auto-generates virtual render methods (RenderHeader, RenderBar, etc.) from switch statement case blocks in queue processor patterns (e.g., IML's switch(row.RowKind)). It was broken during previous UI restoration work and must not be broken again. Always verify that DetectSwitchCaseRenderMethods is still being called in the ModSurface/PulsarPlugin detection pipeline and that the regex pattern @"case\s+(?:[\w\.]+\.)?(\w+)\s*:" correctly matches fully-qualified enum names like "case LcdSpriteRow.Kind.Header:".

### CRITICAL: Sprite Code Navigation
- Code navigation from clicking a sprite in the layer list MUST land on the sprite CREATION line (e.g. `var t = MySprite.CreateText(...)` or `new MySprite(...)`) — NOT on the `.Add()` line. The `.Add()` line has no editable sprite data. This applies to ALL plugin types.

## SESpriteLCDLayoutTool Guidelines
- The SESpriteLCDLayoutTool is designed as a full WYSIWYG visual editor for Space Engineers LCD sprites, akin to "3ds Max for LCDs."
- All elements (color, size, sprite selection, text, animations) should work cohesively together.
- Test plugins for Torch and Pulsar should support straightforward frame animations that are fully editable.
- More complex data-driven plugins (e.g., inventory counts, game engine values) may still contain hardcoded values (positions, padding, colors) that should be editable.
- The user aims to expand and refine editing capabilities for data-driven plugins over time.