# SE Sprite LCD Layout Tool

A powerful **WYSIWYG visual editor** for designing custom LCD sprite layouts in **[Space Engineers](https://store.steampowered.com/app/244850/Space_Engineers/)**.

Design your screens with drag & drop, preview real in-game textures, then export clean, ready-to-paste C# code for **Programmable Blocks**, **mods**, or **Torch/SE plugins**.

![.NET Framework 4.8](https://img.shields.io/badge/.NET%20Framework-4.8-blue)
![C#](https://img.shields.io/badge/C%23-7.3-brightgreen)
![License](https://img.shields.io/badge/license-MIT-green)

---

## ✨ Features

### 🎨 Visual Canvas Designer
- **Drag & drop** sprite placement on a pixel-accurate LCD canvas
- **Zoom & pan** with mouse wheel and middle-click drag
- **Snap to grid** with configurable grid size
- **Resize handles** for quick sprite scaling
- **Rotation** support for texture sprites
- **Layer ordering** — move sprites up/down in the draw order
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
- **Three output modes:**
  - **In-Game (PB)** — `Sandbox.ModAPI.Ingame` (private method)
  - **Mod** — `Sandbox.ModAPI` (public method)
  - **Plugin / Torch** — `Sandbox.ModAPI` with `IMyTextSurfaceProvider` hints
- Smart **code parser** supporting many C# styles (object initializers, constructors, factory methods, assignments, fully-qualified enums)
- One-click **Copy to clipboard**

### 📸 LCD Snapshot Capture (Plugin Helper)
- Includes a **helper code snippet** you can add to **any** Torch or SE plugin
- Capture live LCD panels (resolved sprites with final positions, sizes, colors, etc.)
- Export as a `.cs` file that can be imported directly into the layout tool
- Import as a fully **editable layout** or as a visual **reference overlay** (dotted amber border + [REF] tag)
- Extremely useful for debugging dynamic LCDs or starting from an existing complex display

### 📁 File Operations
- Save/Load layouts in `.seld` (XML) format
- Built-in surface presets: 1×1 LCD (512×512), Wide LCD (512×256), Corner LCD (1024×512), custom sizes

---

## 🚀 Getting Started

### Requirements
- Windows + **.NET Framework 4.8**
- Space Engineers (recommended for full texture previews)

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

---

## 📸 Adding Snapshot Support to Your Plugin

> **Note:** This section is completely optional. The layout tool works standalone — you do **not** need any plugin, mod, or the Inventory Manager to use it. This is just an example showing how you *could* wire up a live snapshot export in your own plugin, so you have real sprite data to import and compare against.

> **Important:** The snapshot helper snippet must be added **inside the plugin or mod that actually renders the LCD** you want to capture. Sprites are drawn by that plugin's own code, so a separate script or programmable block cannot read them — you can only capture what the owning plugin writes to the surface. Add the snippet to whichever plugin's LCD you wish to edit.

The tool can import live snapshots from any plugin that implements a simple helper.

### Example Helper Snippet

```csharp
// Call this from your chat command, timer, or wherever you want to capture
private void CaptureLcdSnapshot(IMyTextSurface surface, string panelName)
{
    var sprites = new List<MySprite>();
    surface.GetSprites(sprites);   // or use your own resolved list

    var sb = new StringBuilder();
    sb.AppendLine($"// LCD Snapshot: {panelName}");
    sb.AppendLine($"// Captured: {DateTime.UtcNow}");
    sb.AppendLine();
    sb.AppendLine("var sprites = new List<MySprite>");
    sb.AppendLine("{");

    foreach (var s in sprites)
    {
        sb.AppendLine("    new MySprite");
        sb.AppendLine("    {");
        sb.AppendLine($"        Type = SpriteType.{s.Type},");
        if (!string.IsNullOrEmpty(s.Data)) sb.AppendLine($"        Data = \"{EscapeString(s.Data)}\",");
        sb.AppendLine($"        Position = new Vector2({s.Position.X}f, {s.Position.Y}f),");
        sb.AppendLine($"        Size = new Vector2({s.Size.X}f, {s.Size.Y}f),");
        sb.AppendLine($"        Color = new Color({s.Color.R}, {s.Color.G}, {s.Color.B}, {s.Color.A}),");
        if (!string.IsNullOrEmpty(s.FontId)) sb.AppendLine($"        FontId = \"{s.FontId}\",");
        if (Math.Abs(s.RotationOrScale) > 0.001f) sb.AppendLine($"        RotationOrScale = {s.RotationOrScale}f,");
        sb.AppendLine("    },");
    }
    sb.AppendLine("};");

    string path = Path.Combine("Snapshots", $"lcd-snapshot-{panelName}-{DateTime.Now:yyyyMMdd-HHmmss}.cs");
    Directory.CreateDirectory(Path.GetDirectoryName(path));
    File.WriteAllText(path, sb.ToString());
}
```

Once implemented, the generated `.cs` file can be imported directly into the layout tool.

<details>
<summary>📄 Example snapshot output (60 sprites from an IML Ingots panel) — click to expand</summary>

```csharp
// Snapshot: 60 sprite(s)
// Captured: 2026-04-01 21:26:41Z

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
| `Ctrl+Z` | Undo |
| `Ctrl+Y` | Redo |
| `Ctrl+S` | Save layout |
| `Ctrl+O` | Open layout |
| `Ctrl+V` | Paste layout code |
| `Ctrl+C` | Copy generated code |
| `Ctrl+D` | Duplicate selected sprite |
| `Delete` | Delete selected sprite |
| `+` / `-` | Move sprite up/down in layer order |
| `G` | Toggle snap to grid |
| Mouse wheel | Zoom canvas |
| Middle-click drag | Pan canvas |

</details>

---

## Contributing

Bug reports, feature requests, and pull requests are welcome!

## License

MIT License

---

Made for the Space Engineers community ❤️
Happy building!