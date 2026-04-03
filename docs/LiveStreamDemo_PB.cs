// ─── SE Sprite LCD Layout Tool — Live Stream Demo (PB Script) ───────────
// Paste this entire script into a Programmable Block.
// It draws a demo layout on a named LCD panel.
//
// Setup:
//   1. Place an LCD Panel and name it (default: "Demo LCD")
//   2. Set LCD_NAME below to match, or put the name in the PB's Custom Data
//   3. Run the PB (auto-runs every 100 ticks)
//   4. Open the PB's Custom Data — the snapshot code is appended after the LCD name
//   5. Copy everything below the "// ── LCD Snapshot ──" line and paste
//      into the layout tool's "Paste Layout" dialog
// ────────────────────────────────────────────────────────────────────────

// Required for PB scripts:
//   using VRage.Game.GUI.TextPanel;   (auto-available in PB)
//   using VRageMath;                  (auto-available in PB)

// ── Change this to your LCD panel name, or put the name in the PB's Custom Data ──
const string LCD_NAME = "Demo LCD";

List<MySprite> _sprites = new List<MySprite>();
IMyTextSurface _surface;

public Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.Update100;
}

public void Main(string argument, UpdateType updateSource)
{
    // Use Custom Data as the LCD name if it's set (first line only),
    // otherwise fall back to the constant.
    string lcdName = LCD_NAME;
    string cd = Me.CustomData;
    if (!string.IsNullOrEmpty(cd))
    {
        // First line = LCD name, rest might be old snapshot output
        string firstLine = cd.Split('\n')[0].Trim();
        if (firstLine.Length > 0 && !firstLine.StartsWith("//"))
            lcdName = firstLine;
    }

    // Find the LCD panel by name
    var block = GridTerminalSystem.GetBlockWithName(lcdName) as IMyTextSurfaceProvider;
    if (block == null)
    {
        Echo($"LCD not found: \"{lcdName}\"");
        Echo("Set the panel name in Custom Data or edit LCD_NAME in the script.");
        return;
    }

    _surface = block.GetSurface(0);
    _surface.ContentType = ContentType.SCRIPT;
    _surface.Script = "";

    _sprites.Clear();
    BuildDemoLayout(_surface, _sprites);

    using (var frame = _surface.DrawFrame())
    {
        foreach (var s in _sprites)
            frame.Add(s);
    }

    Echo($"Drawing on \"{lcdName}\" — {_sprites.Count} sprites");

    // ── Snapshot capture ──
    // Keep the LCD name on the first line, snapshot below
    SnapshotCollect(_sprites);
    Me.CustomData = lcdName + "\n" + SerializeSnapshot();
}

void BuildDemoLayout(IMyTextSurface surface, List<MySprite> sprites)
{
    float sw = surface.SurfaceSize.X;
    float sh = surface.SurfaceSize.Y;
    var ofs = (surface.TextureSize - surface.SurfaceSize) / 2f;

    // ── Background ──
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXTURE,
        Data = "SquareSimple",
        Position = ofs + new Vector2(sw / 2f, sh / 2f),
        Size = new Vector2(sw, sh),
        Color = new Color(10, 12, 18),
        Alignment = TextAlignment.CENTER,
    });

    // ── Top accent bar ──
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXTURE,
        Data = "SquareSimple",
        Position = ofs + new Vector2(sw / 2f, 3f),
        Size = new Vector2(sw, 6f),
        Color = new Color(0, 160, 255),
        Alignment = TextAlignment.CENTER,
    });

    // ── Title ──
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXT,
        Data = "LIVE STREAM DEMO",
        Position = ofs + new Vector2(sw / 2f, 16f),
        Color = new Color(0, 200, 255),
        FontId = "White",
        Alignment = TextAlignment.CENTER,
        RotationOrScale = 1.2f,
    });

    // ── Subtitle ──
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXT,
        Data = "SE Sprite LCD Layout Tool",
        Position = ofs + new Vector2(sw / 2f, 52f),
        Color = new Color(140, 160, 180),
        FontId = "White",
        Alignment = TextAlignment.CENTER,
        RotationOrScale = 0.7f,
    });

    // ── Divider line ──
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXTURE,
        Data = "SquareSimple",
        Position = ofs + new Vector2(sw / 2f, 82f),
        Size = new Vector2(460f, 2f),
        Color = new Color(50, 55, 65),
        Alignment = TextAlignment.CENTER,
    });

    // ── Info panel background ──
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXTURE,
        Data = "SquareSimple",
        Position = ofs + new Vector2(sw / 2f, 150f),
        Size = new Vector2(460f, 110f),
        Color = new Color(20, 24, 32),
        Alignment = TextAlignment.CENTER,
    });

    // ── Status icon (circle) ──
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXTURE,
        Data = "Circle",
        Position = ofs + new Vector2(60f, 115f),
        Size = new Vector2(24f, 24f),
        Color = new Color(0, 255, 80),
        Alignment = TextAlignment.CENTER,
    });

    // ── Status text ──
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXT,
        Data = "ONLINE",
        Position = ofs + new Vector2(78f, 104f),
        Color = new Color(0, 255, 80),
        FontId = "White",
        Alignment = TextAlignment.LEFT,
        RotationOrScale = 0.65f,
    });

    // ── Grid name label ──
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXT,
        Data = "Grid:",
        Position = ofs + new Vector2(50f, 130f),
        Color = new Color(100, 110, 130),
        FontId = "White",
        Alignment = TextAlignment.LEFT,
        RotationOrScale = 0.55f,
    });

    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXT,
        Data = Me.CubeGrid.CustomName,
        Position = ofs + new Vector2(110f, 130f),
        Color = new Color(200, 210, 225),
        FontId = "White",
        Alignment = TextAlignment.LEFT,
        RotationOrScale = 0.55f,
    });

    // ── Power icon ──
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXTURE,
        Data = "IconEnergy",
        Position = ofs + new Vector2(60f, 168f),
        Size = new Vector2(28f, 28f),
        Color = new Color(255, 220, 50),
        Alignment = TextAlignment.CENTER,
    });

    // ── Power label ──
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXT,
        Data = "Power Systems Nominal",
        Position = ofs + new Vector2(80f, 156f),
        Color = new Color(255, 220, 50),
        FontId = "White",
        Alignment = TextAlignment.LEFT,
        RotationOrScale = 0.55f,
    });

    // ── Hydrogen icon ──
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXTURE,
        Data = "IconHydrogen",
        Position = ofs + new Vector2(60f, 194f),
        Size = new Vector2(28f, 28f),
        Color = new Color(80, 180, 255),
        Alignment = TextAlignment.CENTER,
    });

    // ── Hydrogen label ──
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXT,
        Data = "H2 Tank: 87%",
        Position = ofs + new Vector2(80f, 182f),
        Color = new Color(80, 180, 255),
        FontId = "White",
        Alignment = TextAlignment.LEFT,
        RotationOrScale = 0.55f,
    });

    // ── Bottom section — decorative sprites ──

    // Crosshair
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXTURE,
        Data = "Cross",
        Position = ofs + new Vector2(80f, 290f),
        Size = new Vector2(48f, 48f),
        Color = new Color(255, 60, 60, 120),
        Alignment = TextAlignment.CENTER,
    });

    // Triangle (rotated)
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXTURE,
        Data = "Triangle",
        Position = ofs + new Vector2(180f, 290f),
        Size = new Vector2(48f, 48f),
        Color = new Color(0, 200, 120),
        Alignment = TextAlignment.CENTER,
        RotationOrScale = 0.5236f,  // 30 degrees
    });

    // Semi-circle
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXTURE,
        Data = "SemiCircle",
        Position = ofs + new Vector2(280f, 290f),
        Size = new Vector2(48f, 48f),
        Color = new Color(200, 100, 255),
        Alignment = TextAlignment.CENTER,
    });

    // Arrow
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXTURE,
        Data = "AH_BoreSight",
        Position = ofs + new Vector2(380f, 290f),
        Size = new Vector2(48f, 48f),
        Color = new Color(255, 180, 0),
        Alignment = TextAlignment.CENTER,
    });

    // Gear / settings
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXTURE,
        Data = "Screen_LoadingBar",
        Position = ofs + new Vector2(sw / 2f, 340f),
        Size = new Vector2(200f, 16f),
        Color = new Color(0, 140, 200),
        Alignment = TextAlignment.CENTER,
    });

    // ── Progress bar background ──
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXTURE,
        Data = "SquareSimple",
        Position = ofs + new Vector2(sw / 2f, 370f),
        Size = new Vector2(400f, 14f),
        Color = new Color(30, 35, 45),
        Alignment = TextAlignment.CENTER,
    });

    // ── Progress bar fill (75%) ──
    float fillW = 400f * 0.75f;
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXTURE,
        Data = "SquareSimple",
        Position = ofs + new Vector2(sw / 2f - (400f - fillW) / 2f, 370f),
        Size = new Vector2(fillW, 14f),
        Color = new Color(0, 180, 255),
        Alignment = TextAlignment.CENTER,
    });

    // ── Progress label ──
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXT,
        Data = "System Load: 75%",
        Position = ofs + new Vector2(sw / 2f, 382f),
        Color = new Color(120, 130, 150),
        FontId = "White",
        Alignment = TextAlignment.CENTER,
        RotationOrScale = 0.45f,
    });

    // ── Bottom divider ──
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXTURE,
        Data = "SquareSimple",
        Position = ofs + new Vector2(sw / 2f, 420f),
        Size = new Vector2(460f, 1f),
        Color = new Color(50, 55, 65),
        Alignment = TextAlignment.CENTER,
    });

    // ── Footer ──
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXT,
        Data = "Snapshot → Custom Data → Paste into Layout Tool",
        Position = ofs + new Vector2(sw / 2f, 430f),
        Color = new Color(80, 90, 110),
        FontId = "White",
        Alignment = TextAlignment.CENTER,
        RotationOrScale = 0.42f,
    });

    // ── Bottom accent bar ──
    sprites.Add(new MySprite
    {
        Type = SpriteType.TEXTURE,
        Data = "SquareSimple",
        Position = ofs + new Vector2(sw / 2f, sh - 3f),
        Size = new Vector2(sw, 6f),
        Color = new Color(0, 160, 255),
        Alignment = TextAlignment.CENTER,
    });
}

// ─── LCD Snapshot Helper (Programmable Block) ───────────────────────────
// Captures the sprite list and writes it to Custom Data.
// Copy Custom Data → paste into the layout tool's "Paste Layout" dialog.
// ────────────────────────────────────────────────────────────────────────

List<MySprite> _snapshotSprites = new List<MySprite>();

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
