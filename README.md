# SE Sprite LCD Layout Tool

A visual designer for [Space Engineers](https://store.steampowered.com/app/244850/Space_Engineers/) LCD sprite layouts. Design your LCD screens in a WYSIWYG editor, then export ready-to-paste C# code for Programmable Blocks, mods, or Torch/SE plugins.

![.NET Framework 4.8](https://img.shields.io/badge/.NET%20Framework-4.8-blue)
![C#](https://img.shields.io/badge/C%23-7.3-brightgreen)
![License](https://img.shields.io/badge/license-MIT-green)

---

## Features

### 🎨 Visual Canvas Designer
- **Drag & drop** sprite placement on a pixel-accurate LCD canvas
- **Zoom & pan** with mouse wheel and middle-click drag
- **Snap to grid** with configurable grid size
- **Resize handles** for quick sprite scaling
- **Rotation** support for texture sprites
- **Layer ordering** — move sprites up/down in the draw order
- **Undo/Redo** with full history
- **Dark theme** UI matching the SE aesthetic

### 🖼️ Real Texture Previews
- **Auto-detects** your Space Engineers installation via Steam registry
- **Loads actual SE textures** (DDS, PNG, JPG) from your local Content directory
- **Full DDS format support** — BC1 (DXT1), BC3 (DXT5), BC7 (BPTC with DX10 headers), and uncompressed 32-bit BGRA
- **Mod texture support** — loads textures from local mods (`%APPDATA%\SpaceEngineers\Mods\`) and Steam Workshop mods
- **SBC definition parsing** — discovers sprite→texture mappings from LCDTextureDefinition, TransparentMaterial, and block/item Icon elements
- **MyObjectBuilder icons** — automatically maps `MyObjectBuilder_TypeId/SubtypeId` sprites to their icon textures
- **Faction logo sprites** — path-style sprite names (e.g., `Textures\FactionLogo\Builders\BuilderIcon_1.dds`) load directly
- **ColorMatrix tinting** — previews sprites with your chosen color applied

### 📋 Sprite Catalog
- **Built-in sprites** — shapes (Circle, Triangle, SquareSimple, etc.), icons, HUD elements, backgrounds
- **Glyph catalog** — Unicode symbols available in SE fonts (White, Monospace, etc.) with tintability info
- **User sprite import** — paste the output of `GetSprites()` from a PB script to import all available sprite names
- **Auto-categorisation** — imported sprites grouped by prefix (Icons, HUD, MyObjectBuilder, Faction Logos, etc.)
- **Persistent catalog** — imported sprite names saved to `imported_sprites.txt` across sessions

### 💻 Code Generation & Import
- **Three output modes:**
  - **In-Game (PB)** — `Sandbox.ModAPI.Ingame` with `private` method
  - **Mod** — `Sandbox.ModAPI` with `public` method
  - **Plugin / Torch** — `Sandbox.ModAPI` with `IMyTextSurfaceProvider` usage hints
- **Code parser** supporting multiple C# patterns:
  - Object initializer: `new MySprite { Type = SpriteType.TEXTURE, ... }`
  - Constructor: `new MySprite(SpriteType.TEXTURE, "data", position, size, ...)`
  - Factory methods: `MySprite.CreateText(...)`, `MySprite.CreateSprite(...)`
  - Statement assignment: `sprite.Type = ...; sprite.Data = ...;`
  - Fully-qualified enum names (e.g., `VRage.Game.GUI.TextPanel.SpriteType.TEXTURE`)
- **Copy to clipboard** — one click to copy generated code

### 📸 LCD Snapshot Capture
- **IML plugin integration** — `!iml snapshot <name>` captures a live LCD panel's sprites to a `.cs` file
- **Captures live LCD state** — serializes runtime-resolved sprite positions to pasteable code
- **Reference layout import** — import with positions for visual reference only (commented out on export)
- **Visual indicators** — reference sprites shown with dotted amber border and `[REF]` tag

### 💾 File Operations
- **Save/Load layouts** — `.seld` XML format
- **Surface size presets** — Standard 1×1 LCD (512×512), Wide LCD (512×256), Corner LCD (1024×512), custom sizes

---

## Getting Started

### Requirements
- Windows with .NET Framework 4.8
- Space Engineers installed (for texture previews — the tool works without it, you just won't see texture previews)

### Running
1. Build the solution or download a release
2. Run `SESpriteLCDLayoutTool.exe`
3. The tool auto-detects your SE installation on first launch
4. Start designing!

### Importing Sprite Names from SE
To get the full list of available sprites from your game (including mods):

1. Create a Programmable Block in SE with this script:
   ```csharp
   public Program()
   {
       var surface = Me.GetSurface(0);
       var list = new List<string>();
       surface.GetSprites(list);
       Me.CustomData = string.Join("\n", list);
   }
   ```
2. Run the PB, then copy its Custom Data
3. In the tool: **Edit → Import Sprite Names** and paste

### Workflow
1. **Pick sprites** from the tree on the left (or type a custom name)
2. **Place and arrange** them on the canvas
3. **Select a code style** (In-Game / Mod / Plugin) from the dropdown
4. **Copy the generated code** and paste into your PB script, mod, or plugin

## Snapshot Helper (IML Plugin Integration)

The IML Torch plugin includes a built-in snapshot command that captures the resolved sprites from any IML-managed LCD panel and writes them to a `.cs` file you can import into the editor.

### Usage

1. Make sure the LCD panel is tagged with `[IML:LCD]` in its block name or CustomData
2. Run the chat command in-game or from the Torch console:

   ```
   !iml snapshot <LCD block name>
   ```
3. The snapshot is captured **immediately** and the full file path is shown in the chat response — no waiting required.
4. If the panel isn't tagged with `[IML:LCD]`, the command will tell you and show how to fix it.

### Output

The snapshot file (`iml-snapshot-<name>-<timestamp>.cs`) contains literal `new MySprite { ... }` initializers with all resolved positions, sizes, colors, fonts, and alignment values — ready to import into the layout editor.

```csharp
var sprites = new List<MySprite>
{
    new MySprite
    {
        Type = SpriteType.TEXT,
        Data = "INVENTORY",
        Position = new Vector2(10.0f, 10.0f),
        Color = new Color(220, 220, 225, 255),
        FontId = "White",
        RotationOrScale = 0.8500f,
    },
    // ... all resolved sprites from the panel
};
```

Paste the output file into the layout editor's **Paste Layout** dialog with **☑ Import as reference layout** checked to visualise and tweak the layout, then copy the modified code back into your plugin.

### How it works (plugin side)

The snapshot infrastructure lives in `LcdManager.cs`. When the command fires:

1. `RequestSnapshot(entityId, label)` sets a pending flag in a `ConcurrentDictionary`
2. `ForceUpdateLcdPanels()` triggers an immediate LCD scan — inventory is counted, sprite rows are built, and `EnqueueUpdate()` queues the render
3. Inside `ApplyPendingUpdates()`, the draw loop builds a `List<MySprite>`, flushes to `DrawFrame`, then checks the pending flag:
   - `SnapshotCollect()` copies the sprite list
   - `SnapshotLcd()` serialises to C# code and writes the `.cs` file
4. The command checks `HasPendingSnapshot()` — if it's still set, the panel wasn't IML-tagged; if cleared, the file was written and the path is returned via `LastSnapshotPath`

Key code (serialiser):

```csharp
private string SerializeSnapshot(long entityId)
{
    List<MySprite> sprites;
    if (!_capturedSprites.TryGetValue(entityId, out sprites) || sprites.Count == 0)
        return "// No sprites captured.";

    var sb = new StringBuilder();
    sb.AppendLine($"// Snapshot: {sprites.Count} sprite(s)");
    sb.AppendLine($"// Captured: {DateTime.UtcNow:u}");
    sb.AppendLine();
    sb.AppendLine("var sprites = new List<MySprite>");
    sb.AppendLine("{");
    for (int i = 0; i < sprites.Count; i++)
    {
        var s = sprites[i];
        sb.AppendLine("    new MySprite");
        sb.AppendLine("    {");
        sb.AppendLine($"        Type = SpriteType.{s.Type},");
        if (!string.IsNullOrEmpty(s.Data))
            sb.AppendLine($"        Data = \"{s.Data}\",");
        if (s.Position.HasValue)
            sb.AppendLine($"        Position = new Vector2({s.Position.Value.X:F1}f, {s.Position.Value.Y:F1}f),");
        if (s.Size.HasValue)
            sb.AppendLine($"        Size = new Vector2({s.Size.Value.X:F1}f, {s.Size.Value.Y:F1}f),");
        if (s.Color.HasValue)
        {
            var c = s.Color.Value;
            sb.AppendLine($"        Color = new Color({c.R}, {c.G}, {c.B}, {c.A}),");
        }
        if (!string.IsNullOrEmpty(s.FontId))
            sb.AppendLine($"        FontId = \"{s.FontId}\",");
        if (s.Alignment != TextAlignment.LEFT)
            sb.AppendLine($"        Alignment = TextAlignment.{s.Alignment},");
        if (Math.Abs(s.RotationOrScale - 1f) > 0.001f)
            sb.AppendLine($"        RotationOrScale = {s.RotationOrScale:F4}f,");
        sb.AppendLine("    },");
    }
    sb.AppendLine("};");
    return sb.ToString();
}
```

### Notes

- The snapshot file is written to wherever IML's working directory is on the server (same folder as `iml-config.xml` — check the chat response for the exact path)
- Panel names containing special characters (`:`, `=`, etc.) are sanitized to underscores in the filename
- If the client and server are on different machines, you'll need to grab the file from the server
- **Duplicate names:** The snapshot command matches by block name and returns the first match it finds. If you have multiple LCDs with the same name, temporarily rename the one you want to snapshot to something unique before running the command

---

## Project Structure

```
SESpriteLCDLayoutTool/
├── Controls/
│   └── LcdCanvas.cs           # Visual canvas with zoom/pan/snap/drag/resize
├── Data/
│   ├── AppSettings.cs          # Persisted settings (SE game path, auto-detection)
│   ├── GlyphCatalog.cs         # Unicode glyph reference for SE fonts
│   ├── SpriteCatalog.cs        # Built-in SE sprite names and presets
│   └── UserSpriteCatalog.cs    # User-imported sprite names (persisted)
├── Models/
│   ├── LcdLayout.cs            # Layout model (surface size + sprite list)
│   └── SpriteEntry.cs          # Individual sprite model (position, size, color, etc.)
├── Services/
│   ├── CodeGenerator.cs        # Generates C# MySprite code (PB/Mod/Plugin styles)
│   ├── CodeParser.cs           # Parses C# code back into sprite entries
│   ├── DdsLoader.cs            # DDS texture decoder (BC1/BC3/BC7/raw)
│   ├── SpriteTextureCache.cs   # SBC parser + texture loader/cache
│   └── UndoManager.cs          # Undo/redo history
├── MainForm.cs                 # Application UI
└── Program.cs                  # Entry point
```

---

## EULA Compliance

This tool is designed to be fully compliant with the [Space Engineers EULA](https://www.spaceengineersgame.com/eula/) and [Keen Software House modding guidelines](https://www.spaceengineersgame.com/modding.html).

### What this tool does:
- ✅ **Reads** SE definition files (`.sbc` XML) from your local installation at runtime to discover sprite→texture name mappings
- ✅ **Reads** texture files (`.dds`, `.png`) from your local installation at runtime for in-tool preview only
- ✅ **Contains** only sprite and font **name strings** (e.g., `"SquareSimple"`, `"IconEnergy"`) — these are API identifiers, not copyrighted content
- ✅ **Generates** standard C# code using SE's public scripting API (`MySprite`, `IMyTextSurface`, etc.)

### What this tool does NOT do:
- ❌ Does **not bundle, embed, or redistribute** any Space Engineers assets (textures, models, definitions, DLLs)
- ❌ Does **not modify** any game files
- ❌ Does **not include** any SE SDK binaries (the project references are for build-time resolution only and are not redistributed)
- ❌ Does **not extract or export** game assets — textures are loaded into memory for preview and never saved to disk
- ❌ Does **not reverse-engineer** game code — it only parses documented XML definition formats and uses the public scripting API

### Summary
This is a **companion tool** that reads from your own licensed Space Engineers installation to provide texture previews. It functions similarly to any mod development tool or IDE that references game files for autocompletion. No game content is redistributed.

---

## Building

1. Open `SESpriteLCDLayoutTool.sln` in Visual Studio 2019+
2. Build (Ctrl+Shift+B)
3. Run from `bin\Debug\SESpriteLCDLayoutTool.exe`

> **Note:** The project file references SE ModSDK DLLs for build-time type resolution, but the tool does not actually use any SE SDK types at runtime. If you don't have the ModSDK installed, you can safely remove these references — the tool will still build and run (it uses only .NET Framework BCL and WinForms).

---

## Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+Z` | Undo |
| `Ctrl+Y` | Redo |
| `Ctrl+S` | Save layout |
| `Ctrl+O` | Open layout |
| `Ctrl+V` | Paste layout code |
| `Delete` | Delete selected sprite |
| `Ctrl+D` | Duplicate selected sprite |
| `Ctrl+C` | Copy generated code |
| `+` / `-` | Move sprite up/down in layer order |
| Mouse wheel | Zoom canvas |
| Middle-click drag | Pan canvas |
| `G` | Toggle snap to grid |

---

## License

This project is licensed under the MIT License — see [LICENSE](LICENSE) for details.

Space Engineers is a trademark of Keen Software House. This project is not affiliated with or endorsed by Keen Software House.

---

## Contributing

Contributions welcome! Feel free to open issues or pull requests.

## Credits

Built by SilentAssassin82 with assistance from GitHub Copilot.
